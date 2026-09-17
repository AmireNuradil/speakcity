'use strict';
// Dependency-free contract tests. Responses are test doubles, never shipped as application dialogue.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { randomUUID } = require('node:crypto');
const root = path.resolve(__dirname, '..');
const source = file => fs.readFileSync(path.join(root, file), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const json = (value, status = 200) => new Response(JSON.stringify(value), {status,headers:{'Content-Type':'application/json'}});
function storage(initial = {}) {
  const values = new Map(Object.entries(initial).map(([key,value]) => [key, typeof value === 'string' ? value : JSON.stringify(value)]));
  const writes=[];
  return {values,writes,getItem:key=>values.get(key) ?? null,setItem:(key,value)=>{ values.set(key,String(value)); writes.push([key,String(value)]); },removeItem:key=>values.delete(key)};
}
function element() {
  const classes=new Set(), listeners={};
  return {innerHTML:'',textContent:'',dataset:{},listeners,disabled:false,scrollHeight:200,scrollTop:0,
    classList:{add:key=>classes.add(key),remove:key=>classes.delete(key),contains:key=>classes.has(key),toggle:(key,on)=>on?classes.add(key):classes.delete(key)},
    addEventListener:(type,handler)=>{ (listeners[type]??=[]).push(handler); },setAttribute(){},focus(){this.focused=true;},
    closest(){return this;},click(){this.clicked=true;},remove(){this.removed=true;}};
}
function harness({local={},session={},transport}={}) {
  const elements=new Map(), timers=new Map(), windowEvents={}, documentEvents={}, calls=[], urls=[], revoked=[], anchors=[];
  let timerId=0;
  const node=selector=>{ if(!elements.has(selector)) elements.set(selector,element()); return elements.get(selector); };
  const document={documentElement:{lang:'en'},hidden:false,title:'',activeElement:null,
    querySelector:node,querySelectorAll:()=>[],addEventListener:(type,handler)=>{ (documentEvents[type]??=[]).push(handler); },
    createElement:tag=>{const e=element();e.tag=tag;return e;},body:{appendChild:e=>anchors.push(e)}};
  const window={addEventListener:(type,handler)=>{(windowEvents[type]??=[]).push(handler);},scrollTo(){}};
  const localStorage=storage(local),sessionStorage=storage(session);
  class Audio {
    constructor(url){this.src=url;this.paused=false;this.onended=null;this.onerror=null;}
    async play(){this.played=true;}
    pause(){this.paused=true;}
  }
  const context={window,document,localStorage,sessionStorage,console,Blob,Response,Error,TypeError,AbortController,Audio,
    crypto:{randomUUID},queueMicrotask,
    URL:{createObjectURL:blob=>{urls.push(blob);return `blob:https://speakcity.local/test-${urls.length}`;},revokeObjectURL:url=>revoked.push(url)},
    setTimeout:(fn,delay)=>{const id=++timerId;timers.set(id,{fn,delay});return id;},clearTimeout:id=>timers.delete(id),
    setInterval:()=>++timerId,clearInterval(){},
    fetch:async(url,options={})=>{calls.push({url,...options});if(!transport)throw new Error(`Unexpected request: ${url}`);return transport(url,options);}};
  vm.createContext(context);
  vm.runInContext(source('i18n.js'),context,{filename:'i18n.js'});
  vm.runInContext(source('scenario-catalog.js'),context,{filename:'scenario-catalog.js'});
  const exports='S,sceneKeys,preferencesFrom,normalizeWords,normalizeFeedback,catalogFrom,sceneTitle,escape,errorText,errorBanner,city,practice,vocabulary,filteredWords,modal,settings,directionSchematic,render,api,bootstrap,configureAI,start,send,finish,saveWord,exportWords,setPref,rememberSession,rememberDraft,restoreSession,playSpeech,stopAudio,stopCapture,requestMic,record,scheduleListen,goto,releaseResources';
  const instrumented=source('app.js').replace('render(); bootstrap();\n})();',`window.__test={${exports}}; render();\n})();`);
  assert.notEqual(instrumented,source('app.js'),'Test-only instrumentation anchor exists');
  vm.runInContext(instrumented,context,{filename:'app.js'});
  const api=window.__test;
  return {api,context,window,document,node,calls,timers,localStorage,sessionStorage,urls,revoked,anchors,windowEvents,documentEvents,
    ready(configured=true){api.S.booting=false;api.S.connected=true;api.S.status={conversation_installed:configured,tts_installed:true,stt_installed:true,engine:'Test provider'};api.S.prefs.autoVoice=false;},
    async click(action){const el=element();el.dataset.action=action;for(const listener of node('#app').listeners.click)await listener({target:el,preventDefault(){}});},
    async windowEvent(type){for(const listener of windowEvents[type]||[])await listener();},
    async documentEvent(type,event={}){for(const listener of documentEvents[type]||[])await listener(event);},
    async runTimer(delay){const match=[...timers].find(([,timer])=>timer.delay===delay);assert.ok(match,`Expected timer ${delay}`);timers.delete(match[0]);await match[1].fn();}
  };
}
const activeSession=(scenario='airport',count=0)=>({id:'session-1',scenario,count,ended:false,messages:[{role:'assistant',content:'A test-only opening.'}]});
const resultFeedback=(scenario='airport')=>({scenario,turn_count:1,corrections:[{original:'I has a bag.',corrected:'I have a bag.',explanation:{en:'Use have with I.',kk:'I сөзімен have қолданылады.',ru:'С I используйте have.'}}],vocabulary:[{word:'bag',meaning:{en:'A thing for carrying.',kk:'Сөмке.',ru:'Сумка.'},example:'I have a bag.',encountered:true}],messages:[{role:'user',content:'I has a bag.'}]});

test('local-only entrypoint, original assets, and no shipped web installation code',()=>{
  const html=source('index.html');
  for(const match of html.matchAll(/(?:src|href)="([^"]+)"/g)){
    assert.ok(match[1].startsWith('/'));
    assert.ok(fs.existsSync(path.join(root,match[1])));
  }
  assert.match(html,/media-src 'self' blob:/);
  assert.match(html,/script-src 'self'/);
  for(const file of ['app.js','i18n.js','index.html','recorder.js']){
    const body=source(file);
    assert.doesNotMatch(body,/\/static\/|Railway|serviceWorker|hosted-text\.js|pwa\.js|app\.webmanifest|register\(.*sw\.js|https?:\/\//i);
    assert.doesNotMatch(body,/require\(|process\.env|window\.open\(/);
  }
  for(const filename of ['pwa.js','hosted-text.js','sw.js','app.webmanifest']) assert.ok(!fs.existsSync(path.join(root,filename)));
  const h=harness();
  assert.equal(h.window.SC_SCENARIOS.length,8);
  for(const item of h.window.SC_SCENARIOS){assert.ok(!('opening' in item));assert.ok(!('context' in item));assert.ok(fs.existsSync(path.join(root,'assets',item.image)));}
});

test('all language packs have matching keys and desktop privacy/setup strings',()=>{
  const h=harness(), packs=h.window.SC_LANG, keys=Object.keys(packs.en).sort();
  for(const locale of ['en','kk','ru']){
    assert.deepEqual(Object.keys(packs[locale]).sort(),keys);
    assert.ok(packs[locale].configureAI.length>4);
    assert.ok(packs[locale].privacyText.includes('Whisper'));
    assert.ok(packs[locale].privacyText.includes('Kokoro'));
    assert.ok(!('soon' in packs[locale]));
  }
  assert.doesNotMatch(JSON.stringify(packs),/browser|localhost|Chrome|server memory|No learner API key|required\. No account|airport or|Two places|браузер|браузере|Браузер/);
});

test('eight active pins and eight cards remain accessible without configuration in all languages',async()=>{
  const h=harness();h.ready(false);
  for(const locale of ['en','kk','ru']){
    h.api.S.prefs.language=locale;
    const html=h.api.city();
    assert.equal([...html.matchAll(/class="pin pin-[^"]+ active/g)].length,8);
    assert.equal([...html.matchAll(/class="scene-card /g)].length,8);
    for(const id of h.api.sceneKeys){
      const buttons=[...html.matchAll(new RegExp(`<button[^>]+data-start="${id}"[^>]*>`,'g'))];
      assert.equal(buttons.length,2);buttons.forEach(button=>assert.ok(!button[0].includes('disabled')));
      await h.api.start(id);assert.equal(h.api.S.modal.kind,'setup');assert.equal(h.api.S.modal.scenario,id);
      assert.match(h.api.modal(),/data-action="configure"/);assert.equal(h.calls.length,0);
    }
    assert.match(html,/data-action="configure"/);
  }
  h.api.S.modal=null;await h.api.goto('vocabulary');assert.equal(h.api.S.view,'vocabulary');
  await h.api.goto('settings');assert.equal(h.api.S.view,'settings');assert.match(h.api.settings(),/data-action="configure"/);
});

test('bootstrap consumes full scenario metadata and ignores secret-looking extra fields',async()=>{
  const h=harness({transport:async()=>json({conversation_installed:false,tts_installed:true,stt_installed:true,desktop:true,max_turns:8,engine:'Provider name',api_key:'MUST_NOT_STORE',scenarios:[{id:'hotel',title:{en:'Native hotel'},role:{en:'Native role'},mission:{en:'Native mission'},hint:'Native hint',image:'lucy.webp',opening:'NOT A CLIENT REPLY',context:'private prompt'}]})});
  await h.api.bootstrap();
  assert.equal(h.api.S.scenes.hotel.title.en,'Native hotel');assert.equal(h.api.S.scenes.hotel.image,'lucy.webp');
  assert.equal(h.api.S.scenes.hotel.role.en,'Native role');assert.equal(h.api.S.scenes.hotel.hint,'Native hint');
  assert.equal(h.api.S.scenes.school.id,'school');assert.equal(h.api.S.session,null);
  assert.ok(!JSON.stringify(h.api.S).includes('MUST_NOT_STORE'));
  assert.ok(!JSON.stringify(h.api.S.scenes).includes('private prompt'));
  assert.equal(h.calls[0].url,'/api/bootstrap');assert.equal(h.calls[0].cache,'no-store');
});

test('configure uses bodyless native POST, refreshes bootstrap, and does not auto-start a conversation',async()=>{
  let done=false;
  const h=harness({transport:async url=>{
    if(url==='/api/configure'){done=true;return json({configured:true});}
    if(url==='/api/bootstrap')return json({conversation_installed:done,tts_installed:true,stt_installed:true,engine:'Configured model'});
    throw new Error('Unexpected dialogue start');
  }});
  h.ready(false);h.api.S.modal={kind:'setup',scenario:'hotel'};
  await h.api.configureAI();
  assert.equal(h.calls.length,2);assert.equal(h.calls[0].method,'POST');assert.equal(h.calls[0].body,undefined);
  assert.equal(h.api.S.status.conversation_installed,true);assert.equal(h.api.S.configuring,false);
  assert.match(h.api.modal(),/data-start="hotel"/);assert.equal(h.api.S.session,null);
  assert.equal(h.localStorage.writes.length,0);
});

test('cancelled native configuration leaves onboarding and city usable',async()=>{
  const h=harness({transport:async url=>json(url==='/api/configure'?{configured:false}:{conversation_installed:false,tts_installed:true,stt_installed:true})});
  h.ready(false);await h.api.configureAI();
  assert.equal(h.api.S.configuring,false);assert.equal(h.api.S.status.conversation_installed,false);
  assert.match(h.api.city(),/setup-title/);assert.match(h.node('#toast').textContent,/not configured yet/);
});

test('native configuration errors are escaped and visible inside the setup modal',async()=>{
  const h=harness({transport:async()=>json({detail:'Provider said <img src=x onerror=alert(1)>'},400)});
  h.ready(false);h.api.S.modal={kind:'setup',scenario:'hotel'};await h.api.configureAI();
  assert.match(h.api.modal(),/Provider said &lt;img/);assert.ok(!h.api.modal().includes('<img src=x'));
});

test('known error codes translate; unknown English and prototype names remain safe text',()=>{
  const h=harness();
  for(const language of ['en','kk','ru']){
    h.api.S.prefs.language=language;
    assert.notEqual(h.api.errorText('not_configured'),'not_configured');
    assert.notEqual(h.api.errorText('invalid_api_key'),'invalid_api_key');
    assert.notEqual(h.api.errorText('tts_missing'),'tts_missing');
  }
  h.api.S.error='<svg onload="bad()">Provider error & detail</svg>';
  const html=h.api.errorBanner();assert.match(html,/&lt;svg/);assert.ok(!html.includes('<svg onload='));
  assert.equal(h.api.errorText('constructor'),'constructor');
});

test('all eight scenarios start only through POST and use their own role, mission, hint, and available portrait',async()=>{
  const h=harness({transport:async(url,options)=>{
    assert.equal(url,'/api/sessions');const payload=JSON.parse(options.body);
    return json({session_id:`session-${payload.scenario}`,reply:`Native reply for ${payload.scenario}.`});
  }});h.ready();
  for(const id of h.api.sceneKeys){
    h.api.S.session=null;await h.api.start(id);assert.equal(h.api.S.session.scenario,id);
    const request=JSON.parse(h.calls.at(-1).body);assert.deepEqual(request,{scenario:id,language:'en',level:'A2'});
    assert.equal(h.api.S.session.messages[0].content,`Native reply for ${id}.`);
    h.api.S.hint=true;
    for(const language of ['en','kk','ru']){
      h.api.S.prefs.language=language;const html=h.api.practice(),data=h.api.S.scenes[id];
      assert.ok(html.includes(h.api.escape(data.role[language])));assert.ok(html.includes(h.api.escape(data.mission[language])));
      assert.ok(html.includes(`/assets/${data.image}`));assert.ok(html.includes(h.api.escape(data.hint.split('|')[0].trim())));
      assert.ok(!html.includes(`/assets/${id}.webp`) || ['airport','cafe'].includes(id));
    }
    h.api.S.prefs.language='en';
  }
  assert.equal(h.calls.length,8);
});

test('changing scenes explicitly deletes the old session; a 204 response is supported',async()=>{
  const h=harness({transport:async(url,options)=>options.method==='DELETE'?new Response(null,{status:204}):json({session_id:'new-hotel',reply:'Fresh native response'})});
  h.ready();h.api.S.session=activeSession();await h.api.start('hotel');
  assert.equal(h.api.S.modal.kind,'confirm');assert.equal(h.calls.length,0);
  await h.click('modal-confirm');
  assert.equal(h.calls[0].url,'/api/sessions/session-1');assert.equal(h.calls[0].method,'DELETE');
  assert.equal(h.calls[1].url,'/api/sessions');assert.equal(h.api.S.session.scenario,'hotel');
});

test('Directions grid has exact nine positions, localized labels, and only cardinal CSS links',()=>{
  const h=harness();
  const expected=['Airport','Hotel','School','Hospital','Café','Shop','City Map','Park','Job Interview'];
  for(const language of ['en','kk','ru']){
    h.api.S.prefs.language=language;
    const html=h.api.directionSchematic();
    assert.deepEqual([...html.matchAll(/<strong lang="en">([^<]+)<\/strong>/g)].map(m=>m[1]),expected);
    assert.equal([...html.matchAll(/role="row"/g)].length,3);assert.equal([...html.matchAll(/role="cell"/g)].length,9);
    assert.match(html,/default-start/);
    h.api.S.session=activeSession('directions');assert.match(h.api.practice(),/direction-schematic/);
  }
  const css=source('app.css');assert.match(css,/direction-place:not\(:last-child\)::after/);
  assert.match(css,/direction-row:not\(:last-child\) .direction-place::before/);
  assert.doesNotMatch(css,/rotate\(/);
});

test('turn failures preserve text and request_id, retries do not add fixtures or extra fields',async()=>{
  let count=0;
  const h=harness({transport:async()=>++count===1?json({detail:'provider_error'},503):json({reply:'A live native follow-up.',turn_count:1,at_limit:false})});
  h.ready();h.api.S.session=activeSession('school');h.api.S.view='practice';h.api.S.draft='I like drawing.';
  await h.api.send();assert.equal(h.api.S.draft,'I like drawing.');assert.equal(h.api.S.session.messages.length,1);
  const id=h.api.S.pending.request_id;assert.ok(id);assert.ok(h.sessionStorage.getItem('sc_draft_v1').includes(id));
  assert.match(h.api.errorBanner(),/data-action="retry"/);assert.match(h.api.errorBanner(),/data-action="configure"/);
  await h.api.send();
  assert.equal(JSON.parse(h.calls[1].body).request_id,id);assert.deepEqual(Object.keys(JSON.parse(h.calls[1].body)).sort(),['request_id','text']);
  assert.equal(h.api.S.draft,'');assert.equal(h.api.S.pending,null);assert.equal(h.api.S.session.count,1);
  assert.equal(h.api.S.session.messages[2].content,'A live native follow-up.');
});

test('eight-turn cap blocks both send and microphone; finish remains real native feedback',async()=>{
  const h=harness({transport:async(url,options)=>{
    assert.equal(url,'/api/sessions/session-1/finish');assert.deepEqual(JSON.parse(options.body),{language:'kk'});
    return json({...resultFeedback('interview'),turn_count:8});
  }});h.ready();h.api.S.session=activeSession('interview',8);h.api.S.view='practice';h.api.S.draft='No more turns';h.api.S.prefs.language='kk';
  await h.api.send();await h.api.requestMic();assert.equal(h.calls.length,0);
  await h.api.finish();assert.equal(h.api.S.feedback.scenario,'interview');assert.equal(h.api.S.feedback.vocabulary[0].scenario,'interview');
  assert.equal(h.api.S.view,'feedback');assert.equal(h.api.S.session.ended,true);assert.equal(h.api.S.feedback.turn_count,8);
});

test('vocabulary schema guard, save, all-eight filter, search, review, and JSON export',async()=>{
  const h=harness({local:{sc_vocabulary_v1:[null,7,{word:'broken',meaning:true,example:'x'},...['airport','hotel','school','hospital','cafe','shop','directions','interview'].map(scenario=>({scenario,word:scenario,meaning:{en:`meaning ${scenario}`,ru:`слово ${scenario}`},example:`Example ${scenario}`}))]}});
  h.ready(false);assert.equal(h.api.S.vocab.length,8);
  const html=h.api.vocabulary();
  for(const id of h.api.sceneKeys){assert.ok(html.includes(`<option value="${id}"`));h.api.S.filter=id;assert.equal(h.api.filteredWords().length,1);}
  h.api.S.filter='all';h.api.S.search='example hotel';assert.equal(h.api.filteredWords()[0].scenario,'hotel');
  h.api.S.feedback=h.api.normalizeFeedback(resultFeedback('shop'),'shop');h.api.saveWord(0);h.api.saveWord(0);assert.equal(h.api.S.vocab.length,9);
  assert.equal(JSON.parse(h.localStorage.getItem('sc_vocabulary_v1')).length,9);
  await h.click('flashcards');assert.equal(h.api.S.modal.kind,'flash');await h.click('reveal');assert.equal(h.api.S.revealed,true);assert.ok(h.api.modal().includes('meaning airport'));
  h.api.exportWords();assert.equal(h.anchors.length,1);assert.equal(h.anchors[0].download,'speakcity-vocabulary.json');assert.ok(h.anchors[0].clicked);
  const exported=JSON.parse(await h.urls[0].text());assert.equal(exported.version,1);assert.equal(exported.words.length,9);
  await h.runTimer(30000);assert.equal(h.revoked.length,1);
});

test('malformed preferences, multilingual values, and injected assets never become executable markup',()=>{
  const h=harness({local:{sc_preferences_v1:{language:'<script>',voice:'foreign',speed:'NaN',autoVoice:'false',mode:'bad',level:'C9',api_key:'unrelated'},sc_vocabulary_v1:'{bad JSON'}});
  assert.deepEqual(plain(h.api.S.prefs),{language:'en',voice:'american',speed:.95,autoVoice:true,mode:'manual',level:'A2'});
  assert.equal(h.api.S.vocab.length,0);assert.ok(!('api_key' in h.api.S.prefs));
  const raw=[{id:'hotel',image:'https://bad.example/x.webp',role:{en:'<img onerror="x">'},title:{en:'" data-start="evil'},mission:{en:'<script>x</script>'}}];
  h.api.S.scenes=h.api.catalogFrom(raw,h.api.S.scenes);h.api.S.session=activeSession('hotel');
  const html=h.api.practice();assert.match(html,/src="\/assets\/lucy.webp"/);assert.match(html,/&lt;script&gt;/);assert.ok(!html.includes('<script>'));
  const words=h.api.normalizeWords([{scenario:'hotel',word:'one',meaning:1,example:'x'},{scenario:'hotel',word:'two',meaning:{ru:'текст'},example:'x'}]);
  assert.equal(words.length,1);assert.equal(words[0].meaning.en,'текст');
});

test('refresh restores the session and draft without reopening the mic or replaying speech',async()=>{
  const h=harness({session:{sc_active:'session-1',sc_draft_v1:{version:1,session_id:'session-1',draft:'Draft survives.',pending:null}},transport:async url=>{
    if(url==='/api/bootstrap')return json({conversation_installed:true,tts_installed:true,stt_installed:true});
    return json({scenario:'hotel',turn_count:2,messages:[{role:'assistant',content:'Native restored conversation'}],ended:false,busy:false,feedback:null});
  }});
  await h.api.bootstrap();assert.equal(h.api.S.view,'practice');assert.equal(h.api.S.draft,'Draft survives.');assert.equal(h.api.S.session.scenario,'hotel');
  assert.equal(h.api.S.autoActive,false);assert.equal(h.api.S.recorder,null);assert.equal(h.api.S.audio,null);assert.equal(h.calls.length,2);
});

test('refresh reconciles an already-completed pending turn and never appends it twice',async()=>{
  const pending={text:'I like art.',request_id:'request-1',base_count:0};
  const h=harness({session:{sc_active:'session-1',sc_draft_v1:{version:1,session_id:'session-1',draft:'I like art.',pending}},transport:async()=>json({scenario:'school',turn_count:1,messages:[{role:'assistant',content:'Initial'},{role:'user',content:'I like art.'},{role:'assistant',content:'Follow-up'}],ended:false,busy:false})});
  h.ready();await h.api.restoreSession('session-1');assert.equal(h.api.S.draft,'');assert.equal(h.api.S.pending,null);assert.equal(h.api.S.session.messages.length,3);
});

test('restore errors retain the active id; expired conversations clear it but not vocabulary',async()=>{
  let expired=false;
  const h=harness({session:{sc_active:'session-1'},local:{sc_vocabulary_v1:[{scenario:'hotel',word:'room',meaning:{en:'A room'},example:'A room.'}]},transport:async()=>expired?json({detail:'session_expired'},404):Promise.reject(new TypeError('Disconnected'))});
  h.ready();await h.api.restoreSession('session-1');assert.equal(h.sessionStorage.getItem('sc_active'),'session-1');assert.equal(h.api.S.restoreId,'session-1');assert.equal(h.api.S.error,'restoreFailed');
  expired=true;await h.api.restoreSession('session-1');assert.equal(h.sessionStorage.getItem('sc_active'),null);assert.equal(h.api.S.view,'city');assert.equal(h.api.S.vocab.length,1);
});

test('in-flight refresh polling keeps server history and exposes retry if the next poll fails',async()=>{
  let n=0;
  const h=harness({transport:async()=>++n===1?json({scenario:'shop',turn_count:0,messages:[],ended:false,busy:true}):json({detail:'Native temporary error'},503)});
  h.ready();await h.api.restoreSession('session-1');assert.equal(h.api.S.phase,'thinking');await h.runTimer(2000);assert.equal(h.api.S.phase,'idle');
  assert.equal(h.api.S.restoreId,'session-1');assert.match(h.api.errorBanner(),/data-action="retry"/);
});

test('TTS uses same-origin WAV blobs, American default, speed, replay and mute cleanup',async()=>{
  const h=harness({transport:async(url,options)=>{assert.equal(url,'/api/tts');return new Response(new Uint8Array([82,73,70,70]),{headers:{'Content-Type':'audio/wav'}});}});
  h.ready();h.api.S.prefs.autoVoice=true;
  await h.api.playSpeech('Native text');assert.deepEqual(JSON.parse(h.calls[0].body),{text:'Native text',voice:'american',speed:.95});
  assert.equal(h.api.S.phase,'speaking');const audio=h.api.S.audio;assert.ok(audio.played);
  await h.api.setPref('autoVoice',false);assert.ok(audio.paused);assert.equal(h.api.S.audio,null);assert.equal(h.revoked.length,1);
  await h.api.setPref('voice','british');await h.api.setPref('speed',.8);await h.api.playSpeech('Replay');
  assert.deepEqual(JSON.parse(h.calls[1].body),{text:'Replay',voice:'british',speed:.8});
  assert.equal(h.localStorage.values.has('sc_preferences_v1'),true);
});

test('missing speech engines leave typed conversation available and do not call TTS/STT',async()=>{
  const h=harness();h.ready();h.api.S.status.tts_installed=false;h.api.S.status.stt_installed=false;h.api.S.session=activeSession();h.api.S.view='practice';
  await h.api.playSpeech('Hello');await h.api.requestMic();assert.equal(h.calls.length,0);assert.equal(h.api.S.modal,null);
  h.api.S.draft='I can type';const html=h.api.practice();const textarea=html.match(/<textarea[^>]*>/)[0];assert.ok(!textarea.includes('disabled'));
});

test('manual mic produces editable transcript; hands-free sends only after explicit consent',async()=>{
  const h=harness({transport:async(url,options)=>url==='/api/stt'?json({text:'I like books.'}):json({reply:'Native response',turn_count:1,at_limit:false})});
  class Recorder { constructor(options){this.options=options;} async start(){this.started=true;} async cancel(){this.cancelled=true;} async stop(reason){await this.options.onDone(new Blob(['wav'],{type:'audio/wav'}),reason);} }
  h.window.SpeakcityRecorder=Recorder;h.ready();h.api.S.session=activeSession('school');h.api.S.view='practice';
  await h.api.requestMic();assert.equal(h.api.S.modal.kind,'consent');assert.equal(h.calls.length,0);
  await h.click('consent');assert.ok(h.api.S.recorder.started);
  await h.api.requestMic();assert.equal(h.api.S.draft,'I like books.');assert.equal(h.calls.length,1);assert.equal(h.calls[0].headers['Content-Type'],'audio/wav');assert.ok(h.calls[0].body instanceof Blob);
  h.api.S.draft='';h.api.S.mode='handsfree';await h.api.requestMic();assert.equal(h.api.S.autoActive,true);
  await h.api.S.recorder.stop('utterance');assert.equal(h.api.S.session.count,1);assert.equal(h.calls.filter(call=>call.url.endsWith('/turn')).length,1);
});

test('pause during transcription keeps text but prevents automatic sending',async()=>{
  let resolve;
  const h=harness({transport:async()=>new Promise(done=>{resolve=done;})});
  class Recorder { constructor(options){this.options=options;} async start(){} async cancel(){this.cancelled=true;} }
  h.window.SpeakcityRecorder=Recorder;h.ready();h.api.S.session=activeSession();h.api.S.view='practice';h.api.S.mode='handsfree';h.api.S.consent=true;h.api.S.autoActive=true;
  await h.api.record();const done=h.api.S.recorder.options.onDone(new Blob(['wav']),'utterance');
  await h.click('pause');resolve(json({text:'Keep this draft'}));await done;
  assert.equal(h.api.S.draft,'Keep this draft');assert.equal(h.api.S.autoActive,false);assert.equal(h.calls.length,1);
});

test('pause, scene navigation, hidden window, pagehide and Escape release capture',async()=>{
  for(const action of ['pause','navigation','hidden','pagehide','escape']){
    const h=harness();h.ready();h.api.S.session=activeSession();h.api.S.view='practice';h.api.S.phase='listening';h.api.S.autoActive=true;
    const recorder={cancelled:false,async cancel(){this.cancelled=true;}};h.api.S.recorder=recorder;
    if(action==='pause')await h.click('pause');
    if(action==='navigation')await h.api.goto('city');
    if(action==='hidden'){h.document.hidden=true;await h.documentEvent('visibilitychange');}
    if(action==='pagehide')await h.windowEvent('pagehide');
    if(action==='escape')await h.documentEvent('keydown',{key:'Escape'});
    assert.ok(recorder.cancelled,action);assert.equal(h.api.S.recorder,null,action);assert.equal(h.api.S.autoActive,false,action);
  }
});

test('a late TTS response after navigation cannot resume sound',async()=>{
  let resolve;
  const h=harness({transport:async()=>new Promise(done=>{resolve=done;})});h.ready();
  const playback=h.api.playSpeech('Hello');await h.api.goto('vocabulary');resolve(new Response(new Uint8Array([1,2,3])));await playback;
  assert.equal(h.api.S.audio,null);assert.equal(h.urls.length,0);assert.equal(h.api.S.view,'vocabulary');
});

test('API boundary refuses non-native URLs and session IDs from storage cannot inject paths',async()=>{
  const h=harness({session:{sc_active:'../../outside?x="'}});
  await assert.rejects(h.api.api('https://outside.example/'),/invalid_body/);
  await h.api.restoreSession('../../outside?x="');assert.equal(h.calls.length,0);assert.equal(h.api.S.restoreId,null);
});
