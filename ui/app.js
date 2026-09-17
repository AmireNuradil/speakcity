(() => {
'use strict';
const $ = selector => document.querySelector(selector);
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const paths = {
 city:'M3 21V7l6-4 6 4 6-4v14l-6 4-6-4-6 4Zm6-18v14m6-10v14',
 book:'M4 3h13a3 3 0 0 1 3 3v15H7a3 3 0 0 1-3-3V3Zm0 14a3 3 0 0 1 3-3h13M8 7h8',
 settings:'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM12 2v3m0 14v3M2 12h3m14 0h3M5 5l2 2m10 10 2 2M19 5l-2 2M7 17l-2 2',
 mic:'M9 5a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0V5Zm-3 6v1a6 6 0 0 0 12 0v-1M12 18v4m-4 0h8',
 plane:'m22 2-7 20-4-9-9-4L22 2ZM11 13l6-6',
 cup:'M4 6h12v8a5 5 0 0 1-5 5H9a5 5 0 0 1-5-5V6Zm12 1h3a3 3 0 0 1 0 6h-3M3 22h16M8 2v1m5-1v1',
 arrow:'M5 12h14m-6-6 6 6-6 6', back:'M19 12H5m6-6-6 6 6 6',
 sound:'M11 4 6 8H2v8h4l5 4V4Zm5 4a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14',
 shield:'M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6l-9-4ZM8 12l3 3 5-6',
 check:'m5 12 4 4L20 5', plus:'M12 4v16M4 12h16', close:'m6 6 12 12M6 18 18 6',
 search:'M10 3a7 7 0 1 0 0 14 7 7 0 0 0 0-14Zm5 12 6 6',
 pause:'M8 4v16M16 4v16', play:'m7 3 14 9-14 9V3Z',
 clock:'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 4v6l4 2',
 trash:'M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7m4-7v7',
 wave:'M3 10v4m4-8v12m5-16v20m5-17v14m4-10v4',
 download:'M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5',
 bulb:'M8 15a7 7 0 1 1 8 0v4H8v-4Zm2 7h4',
};
const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${paths[name] || paths.city}"/></svg>`;

const sceneKeys = ['airport','hotel','school','hospital','cafe','shop','directions','interview'];
const locales = ['en','kk','ru'];
const imageNames = new Set(['airport.webp','cafe.webp','lucy.webp']);
const own = (object, key) => object != null && Object.hasOwn(object, key);
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const textValue = (value, max = 8000) => typeof value === 'string' ? value.slice(0, max) : '';
const safeId = value => typeof value === 'string' && /^[a-zA-Z0-9_-]{1,160}$/.test(value) ? value : null;
const turnCount = value => Number.isInteger(value) && value >= 0 && value <= 8;
function readStore(key, fallback, storage = localStorage) {
  try { return JSON.parse(storage.getItem(key)) ?? fallback; } catch { return fallback; }
}
function multilingual(value, fallback = {}) {
  const result = {};
  const incoming = typeof value === 'string' ? {en:value} : isObject(value) ? value : {};
  const previous = isObject(fallback) ? fallback : {};
  const anyText = textValue(incoming.en) || textValue(previous.en) || locales.map(locale=>textValue(incoming[locale])).find(Boolean) || '';
  for (const locale of locales) result[locale] = textValue(incoming[locale]) || textValue(previous[locale]) || anyText;
  return result;
}
function catalogFrom(raw, base = {}) {
  const incoming = Array.isArray(raw) ? raw : isObject(raw) ? Object.values(raw) : [];
  const result = {};
  for (const id of sceneKeys) {
    const source = incoming.find(item => isObject(item) && item.id === id) || {};
    const old = base[id] || {};
    result[id] = {
      id, title:multilingual(source.title,old.title), role:multilingual(source.role,old.role),
      mission:multilingual(source.mission,old.mission), hint:textValue(source.hint) || textValue(old.hint),
      image:imageNames.has(source.image) ? source.image : imageNames.has(old.image) ? old.image : 'lucy.webp'
    };
  }
  return result;
}
const bundledCatalog = catalogFrom(window.SC_SCENARIOS);
const defaults = {language:'en', voice:'american', speed:.95, autoVoice:true, mode:'manual', level:'A2'};
function preferencesFrom(raw) {
  const saved = isObject(raw) ? raw : {};
  return {
    language:locales.includes(saved.language) ? saved.language : defaults.language,
    voice:['american','british'].includes(saved.voice) ? saved.voice : defaults.voice,
    speed:(typeof saved.speed === 'number' && Number.isFinite(saved.speed) && saved.speed >= .75 && saved.speed <= 1.2) ? saved.speed : defaults.speed,
    autoVoice:typeof saved.autoVoice === 'boolean' ? saved.autoVoice : defaults.autoVoice,
    mode:['manual','handsfree'].includes(saved.mode) ? saved.mode : defaults.mode,
    level:['A1','A2'].includes(saved.level) ? saved.level : defaults.level
  };
}
function normalizeWord(raw, scenario) {
  if (!isObject(raw)) return null;
  const id = sceneKeys.includes(raw.scenario) ? raw.scenario : scenario;
  if (!sceneKeys.includes(id) || typeof raw.word !== 'string' || !raw.word.trim() || typeof raw.example !== 'string') return null;
  const meaning = multilingual(raw.meaning);
  if (!locales.some(locale => meaning[locale])) return null;
  return {scenario:id, word:raw.word.trim().slice(0,160), meaning, example:raw.example.slice(0,4000),
    encountered:raw.encountered === true, ...(typeof raw.savedAt === 'string' ? {savedAt:raw.savedAt.slice(0,40)} : {})};
}
function wordKey(word) { return `${word.scenario}:${word.word.toLowerCase()}`; }
function normalizeWords(raw, scenario) {
  const seen = new Set();
  return (Array.isArray(raw) ? raw : []).map(word => normalizeWord(word,scenario)).filter(word => {
    if (!word || seen.has(wordKey(word))) return false;
    seen.add(wordKey(word)); return true;
  }).slice(0,1000);
}
function normalizeMessages(raw) {
  return (Array.isArray(raw) ? raw : []).filter(message => isObject(message) && ['user','assistant'].includes(message.role) && typeof message.content === 'string')
    .slice(-40).map(message => ({role:message.role, content:message.content.slice(0,16000)}));
}
function normalizeFeedback(raw, scenario) {
  if (!isObject(raw) || !Array.isArray(raw.corrections) || !Array.isArray(raw.vocabulary)) throw new Error('invalid_response');
  const id = sceneKeys.includes(raw.scenario) ? raw.scenario : scenario;
  if (!sceneKeys.includes(id) || !turnCount(raw.turn_count)) throw new Error('invalid_response');
  return {scenario:id, turn_count:raw.turn_count, messages:normalizeMessages(raw.messages),
    vocabulary:normalizeWords(raw.vocabulary,id),
    corrections:raw.corrections.filter(c => isObject(c) && typeof c.original === 'string' && typeof c.corrected === 'string')
      .slice(0,24).map(c => ({original:textValue(c.original), corrected:textValue(c.corrected), explanation:multilingual(c.explanation)}))};
}
const prefs = preferencesFrom(readStore('sc_preferences_v1',{}));
const S = {
  view:'city', prefs, vocab:normalizeWords(readStore('sc_vocabulary_v1',[])), scenes:bundledCatalog, mode:prefs.mode,
  status:null, connected:false, booting:true, bootPromise:null, configuring:false, starting:false,
  phase:'idle', phaseAt:0, session:null, feedback:null, draft:'', pending:null, error:'', hint:false,
  autoActive:false, consent:false, recorder:null, listenTimer:null, audio:null, audioUrl:null,
  audioAbort:null, speakSeq:0, modal:null, search:'', filter:'all', flash:0, revealed:false,
  restoreId:null, restoreTimer:null, restoreToken:0, restoring:false
};
const errorAliases = Object.freeze({
  not_configured:'notConfigured', ai_not_configured:'notConfigured', api_not_configured:'notConfigured',
  configuration_required:'notConfigured', api_key_missing:'notConfigured', missing_api_key:'notConfigured',
  invalid_api_key:'invalidApiKey', authentication_failed:'invalidApiKey', unauthorized:'invalidApiKey',
  provider_auth_error:'invalidApiKey', provider_error:'providerError', api_error:'providerError',
  rate_limited:'rateLimited', rate_limit:'rateLimited', rate_limit_exceeded:'rateLimited', quota_exceeded:'rateLimited',
  tts_unavailable:'ttsUnavailable', tts_not_installed:'ttsUnavailable', tts_missing:'ttsUnavailable',
  stt_unavailable:'sttUnavailable', stt_not_installed:'sttUnavailable', stt_missing:'sttUnavailable'
});
const t = key => {
  const chosen = String(key ?? '');
  const dictionary = window.SC_LANG[S.prefs.language] || window.SC_LANG.en;
  return own(dictionary,chosen) ? dictionary[chosen] : own(window.SC_LANG.en,chosen) ? window.SC_LANG.en[chosen] : chosen;
};
const escT = key => escape(t(key));
const local = value => typeof value === 'string' ? value : textValue(value?.[S.prefs.language]) || textValue(value?.en);
const errorKey = code => own(errorAliases,code) ? errorAliases[code] : code;
const errorText = code => t(errorKey(code));
const scene = id => S.scenes[id] || bundledCatalog[id];
const sceneTitle = id => local(scene(id)?.title) || t(id);
const sceneIcon = key => ({airport:'plane',cafe:'cup',school:'book',hospital:'plus',interview:'book',hotel:'city',shop:'book',directions:'city'}[key] || 'city');
const maxTurns = () => 8;
const busy = () => S.starting || S.configuring || S.restoring || ['thinking','reviewing','transcribing'].includes(S.phase);
const audioBusy = () => ['speaking','preparing'].includes(S.phase);
const transient = new Set(['listening','thinking','reviewing','transcribing','preparing','speaking']);
const configured = () => S.status?.conversation_installed === true;
const sessionPath = id => `/api/sessions/${encodeURIComponent(id)}`;
function toast(message) {
  const node = $('#toast'); if (!node) return;
  node.textContent = message; node.classList.add('show'); clearTimeout(toast.timer);
  toast.timer = setTimeout(() => node.classList.remove('show'),4200);
}
function store(key,value,storage = localStorage) {
  try { storage.setItem(key,JSON.stringify(value)); return true; }
  catch { toast(t('storageError')); return false; }
}
function savePrefs() { store('sc_preferences_v1',S.prefs); }
function rememberSession() {
  try {
    if (S.session) sessionStorage.setItem('sc_active',S.session.id);
    else sessionStorage.removeItem('sc_active');
  } catch { toast(t('storageError')); }
}
function rememberDraft() {
  try {
    if (!S.session || S.session.ended) sessionStorage.removeItem('sc_draft_v1');
    else store('sc_draft_v1',{version:1,session_id:S.session.id,draft:S.draft,pending:S.pending},sessionStorage);
  } catch { toast(t('storageError')); }
}
function restoreDraft(session) {
  const saved = readStore('sc_draft_v1',{},sessionStorage);
  if (!isObject(saved) || saved.version !== 1 || saved.session_id !== session.id || session.ended) return;
  S.draft = textValue(saved.draft,400);
  const pending = saved.pending;
  if (isObject(pending) && typeof pending.text === 'string' && pending.text.trim() === S.draft.trim() && safeId(pending.request_id)) {
    S.pending = {text:pending.text.slice(0,400),request_id:pending.request_id,base_count:turnCount(pending.base_count) ? pending.base_count : session.count};
    const lastUser = [...session.messages].reverse().find(message => message.role === 'user');
    if (session.count > S.pending.base_count && lastUser?.content === S.pending.text) { S.draft=''; S.pending=null; rememberDraft(); }
  }
}
function phase(value) { S.phase=value; S.phaseAt=Date.now(); render(); }
function setError(error) {
  S.error = textValue(error instanceof Error ? error.message : String(error),4000) || 'server_error';
  S.autoActive=false; clearTimeout(S.listenTimer); S.phase='idle';
  if (errorKey(S.error) === 'notConfigured' && S.status) S.status.conversation_installed=false;
  render();
}
const requests = new Set();
async function api(url,{method='GET',body,raw=false,timeoutMs=180000}={}) {
  // All requests stay inside the native virtual origin. No provider URLs or credentials live here.
  if (!/^\/api\//.test(url)) throw new Error('invalid_body');
  const controller = new AbortController(); requests.add(controller);
  const timer = timeoutMs ? setTimeout(() => controller.abort(),timeoutMs) : null;
  try {
    const response = await fetch(url,{method,credentials:'same-origin',cache:'no-store',signal:controller.signal,
      headers:{'X-Speakcity':'1',...(body !== undefined ? {'Content-Type':raw ? 'audio/wav' : 'application/json'} : {})},
      body:body === undefined ? undefined : raw ? body : JSON.stringify(body)});
    if (!response.ok) {
      const data = await response.json().catch(() => ({}));
      const error = new Error(typeof data.detail === 'string' ? data.detail : 'server_error');
      error.httpStatus=response.status; throw error;
    }
    if (response.status===204) return null;
    const result = await response.json().catch(() => { throw new Error('invalid_response'); });
    return result;
  } catch (error) {
    if (error instanceof TypeError || error.name === 'AbortError') throw new Error('network');
    throw error;
  } finally { clearTimeout(timer); requests.delete(controller); }
}
function errorBanner() {
  if (!S.error) return '';
  const needsSettings = ['notConfigured','invalidApiKey','providerError','rateLimited'].includes(errorKey(S.error));
  return `<div class="error-banner" role="alert"><span>${escape(errorText(S.error))}</span><div class="error-actions"><button class="button small secondary" data-action="retry" ${busy()?'disabled':''}>${escT('retry')}</button>${needsSettings?`<button class="button small secondary" data-action="configure" ${busy()?'disabled':''}>${escT('configureAI')}</button>`:''}</div></div>`;
}
function languageOptions() {
  return [['en','English'],['kk','Қазақша'],['ru','Русский']].map(([key,label]) => `<option value="${key}" ${S.prefs.language===key?'selected':''}>${label}</option>`).join('');
}
function header() {
  return `<header class="shell-header"><a class="brand" href="#city" aria-label="SPEAKCITY AI"><span class="brand-mark">${icon('wave')}</span>SPEAKCITY <em>AI</em></a><nav class="nav" aria-label="${escT('navigation')}">${[['city','city'],['vocabulary','book'],['settings','settings']].map(([view,ic])=>`<button data-nav="${view}" class="${S.view===view?'active':''}" ${S.view===view?'aria-current="page"':''}>${icon(ic)}${escT(view)}</button>`).join('')}</nav><div class="header-right"><span class="free-tag">${escT('free')}</span><label class="sr-only" for="header-language">${escT('language')}</label><select id="header-language" data-pref="language">${languageOptions()}</select></div></header>`;
}
function chip() {
  const ready = configured() && S.status?.tts_installed && S.status?.stt_installed;
  const key = S.booting ? 'checking' : !S.connected ? 'offline' : !configured() ? 'notConfigured' : ready ? 'ready' : 'speechMissing';
  return `<div class="status-chip ${ready?'':'warn'}" role="status"><i></i>${escT(key)}</div>`;
}
function footer() { return `<footer class="footer"><span>SPEAKCITY AI · ${escT('free')}</span><span>${escT('localTag')}</span></footer>`; }
function configureButton() {
  return `<button class="button" data-action="configure" ${busy()?'disabled':''}>${icon('settings')}${escT(S.configuring?'configuring':'configureAI')}</button>`;
}
function onboarding() {
  if (S.booting || configured()) return '';
  return `<section class="onboarding" aria-labelledby="setup-title"><div class="onboarding-copy"><p class="eyebrow">${escT('allEight')}</p><h2 id="setup-title">${escT('configureTitle')}</h2><p>${escT('configureIntro')}</p><p class="steps">${escT('configureSteps')}</p><p>${escT('configureNativeNote')}</p></div>${configureButton()}</section>`;
}
function city() {
  const active = S.session && !S.session.ended;
  return `<div class="page-heading"><div><p class="eyebrow">SPEAKCITY / ${escT('city')}</p><h1>${escT('title')}</h1><p>${escT('intro')}</p></div>${chip()}</div>${errorBanner()}${onboarding()}
    ${active?`<div class="finish-banner resume-banner"><span>${escape(sceneTitle(S.session.scenario))} · ${S.session.count} / ${maxTurns()}</span><button class="button small secondary" data-action="resume">${icon('play')}${escT('resume')}</button></div>`:''}
    <div class="city-grid"><div><div class="map-panel"><img src="/assets/city.webp" alt="${escT('cityAlt')}"><span class="map-caption">SPEAKCITY</span>${sceneKeys.map(id=>`<button class="pin pin-${id} active ${id}" data-start="${id}" aria-label="${escape(sceneTitle(id)+' — '+t('open'))}">${icon(sceneIcon(id))}${escape(sceneTitle(id))}</button>`).join('')}</div><p class="map-note">${icon('city')}${escT('cityHint')}</p><div class="scene-cards">${sceneKeys.map(id=>`<button class="scene-card ${id}" data-start="${id}"><span class="scene-icon">${icon(sceneIcon(id))}</span><div><h3>${escape(sceneTitle(id))}</h3><p>${escape(local(scene(id).mission))}</p></div><span class="arrow">${icon('arrow')}</span></button>`).join('')}</div></div><aside class="lucy-card"><img src="/assets/lucy.webp" alt="${escT('lucyAlt')}" decoding="async"><div class="copy"><p class="eyebrow">${escT('meet')}</p><h2>Lucy</h2><p>${escT('lucyIntro')}</p><span class="status-chip">${icon('sound')}Kokoro · ${escT(S.prefs.voice)}</span><div class="lucy-small-note">${escT('readyNote')}</div></div></aside></div><section class="how" aria-label="${escT('first')}">${[1,2,3].map(n=>`<article class="how-item"><span class="number">0${n}</span><div><h3>${escT('step'+n)}</h3><p>${escT('stepText'+n)}</p></div></article>`).join('')}</section>`;
}
function directionSchematic(location='practice') {
  const prefix=location==='setup'?'setup':'practice';
  // This grid, NOT the decorative city image, is the geometry used by the AI scenario.
  const rows = [
    [['airport','Airport'],['hotel','Hotel'],['school','School']],
    [['hospital','Hospital'],['cafe','Café'],['shop','Shop']],
    [['mapPavilion','City Map'],['park','Park'],['interview','Job Interview']]
  ];
  return `<section class="direction-schematic" aria-labelledby="${prefix}-schematic-heading"><header><h3 id="${prefix}-schematic-heading">${escT('schematicTitle')}</h3><span>${escT('schematicTag')}</span></header><div class="direction-grid" role="table" aria-label="${escT('schematicTitle')}" aria-describedby="${prefix}-schematic-note">${rows.map(row=>`<div class="direction-row" role="row">${row.map(([key,label])=>`<div class="direction-place ${key==='mapPavilion'?'default-start':''}" role="cell" data-place="${key}"><strong lang="en">${label}</strong>${S.prefs.language!=='en'?`<small>${escape(sceneKeys.includes(key)?sceneTitle(key):t(key))}</small>`:''}</div>`).join('')}</div>`).join('')}</div><p class="direction-caption" id="${prefix}-schematic-note"><strong>${escT('schematicStart')}</strong>${escT('schematicNote')}</p></section>`;
}
function statusLine() {
  return `<span class="live-status ${transient.has(S.phase)?'busy':''}" aria-live="polite"><span class="dot"></span><span>${escape(t(S.phase))}<span id="elapsed"></span></span></span>`;
}
function messages() {
  return (S.session?.messages || []).map((message,index)=>`<div class="message ${message.role==='user'?'user':''}">${message.role==='assistant'?'<img class="chat-avatar" src="/assets/lucy.webp" alt="">':''}<div class="bubble-wrap"><div class="speaker-label">${message.role==='user'?escT('you'):'Lucy'}</div><div class="bubble" lang="en">${escape(message.content)}</div>${message.role==='assistant'?`<button class="bubble-replay" data-replay="${index}" ${busy()||S.recorder?'disabled':''}>${icon('sound')}${escT('replay')}</button>`:''}</div></div>`).join('');
}
function practice() {
  if (!S.session) return city();
  const id=S.session.scenario, data=scene(id), blocked=busy()||audioBusy(), cap=S.session.count>=maxTurns()||S.session.ended;
  return `<div class="breadcrumbs"><div class="crumb-left"><button class="button ghost" data-nav="city">${icon('back')}${escT('back')}</button><span>SPEAKCITY / ${escape(sceneTitle(id))}</span></div><button class="button secondary" data-action="finish" ${busy()?'disabled':''}>${icon('check')}${escT('end')}</button></div><div class="practice-grid"><aside class="scene-side"><div class="scene-portrait ${data.image==='lucy.webp'?'generic':''}"><img src="/assets/${data.image}" alt="${escape('Lucy · '+local(data.role))}"><div class="portrait-caption"><h2>Lucy</h2><p>${escape(local(data.role))}</p></div></div><div class="mission"><h3>${escT('mission')}</h3><p>${escape(local(data.mission))}</p></div><div class="privacy-note">${icon('shield')}<span>${escT('aiNote')}</span></div></aside><div class="conversation-column">${id==='directions'?directionSchematic():''}<section class="conversation-panel"><div class="conversation-heading"><h2>${escape(sceneTitle(id))}</h2><span class="turn-count">${escT('turn')} <strong>${S.session.count} / ${maxTurns()}</strong></span><button class="button ghost small voice-toggle" data-action="mute" aria-pressed="${!S.prefs.autoVoice}" aria-label="${escT(S.prefs.autoVoice?'muteVoice':'unmuteVoice')}">${icon('sound')}${escT(S.prefs.autoVoice?'muteVoice':'unmuteVoice')}</button></div><div class="chat" id="chat" role="log" aria-label="${escT('conversation')}">${messages()}${S.phase==='thinking'?'<div class="loading-dots" aria-hidden="true"><i></i><i></i><i></i></div>':''}</div>${S.session.count>=maxTurns()-2?`<div class="finish-banner">${escT(cap?'limit':'doneSoon')}</div>`:''}<div class="input-area"><div class="mode-row"><div class="segmented" aria-label="${escT('defaultMode')}">${['manual','handsfree'].map(mode=>`<button data-mode="${mode}" class="${S.mode===mode?'on':''}" aria-pressed="${S.mode===mode}" ${blocked?'disabled':''}>${icon(mode==='manual'?'mic':'wave')}${escT(mode)}</button>`).join('')}</div>${statusLine()}</div>${errorBanner()}<div class="mic-row"><button class="mic-button ${S.recorder?'recording':''}" id="mic-button" data-action="mic" aria-label="${escT(S.recorder?'stopRec':'mic')}" ${blocked||cap?'disabled':''}>${icon(S.recorder?'pause':'mic')}</button><div><p class="mic-label">${escT(S.recorder?'stopRec':S.mode==='handsfree'?(S.autoActive?'pause':'call'):'mic')}</p><p class="mic-caption">${escT(S.mode==='handsfree'?'handsNote':'transcriptNote')}</p></div>${S.autoActive?`<button class="button small secondary" data-action="pause">${icon('pause')}${escT('pause')}</button>`:''}${audioBusy()?`<button class="button small secondary" data-action="stop-audio">${icon('pause')}${escT('stopAudio')}</button>`:''}</div><form id="answer-form" class="composer"><label class="sr-only" for="answer">${escT('draft')}</label><textarea id="answer" maxlength="400" placeholder="${escT('type')}" ${busy()||S.recorder||cap?'disabled':''}>${escape(S.draft)}</textarea><button class="button" type="submit" ${blocked||S.recorder||cap||!S.draft.trim()?'disabled':''}>${icon('arrow')}${escT('send')}</button></form><div class="input-note"><button class="button ghost small" data-action="hint">${icon('bulb')}${escT('help')}</button><span id="char-count">${S.draft.length} / 400</span></div>${S.hint?`<div class="hint-box" lang="en"><ul>${data.hint.split('|').filter(Boolean).map(hint=>`<li>${escape(hint.trim())}</li>`).join('')}</ul></div>`:''}<div class="conversation-tools"><span class="fine-print">${escT('shortcut')}</span><span class="fine-print">${escT('localSpeech')} · Whisper / Kokoro</span></div></div></section><p class="grammar-note">${icon('shield')}${escT('remember')}</p></div></div>`;
}
function wordCard(word,{savedView=false,index=0}={}) {
  const saved=S.vocab.some(item=>wordKey(item)===wordKey(word));
  return `<article class="word-card"><span class="word-meta">${escape(sceneTitle(word.scenario))} · ${escT(word.encountered?'encountered':'suggested')}</span><h3 lang="en">${escape(word.word)}</h3><p>${escape(local(word.meaning))}</p><p class="example" lang="en">${escape(word.example)}</p><div class="word-actions"><button class="button small ${saved?'secondary':''}" ${savedView?`data-remove-word="${escape(wordKey(word))}"`:`data-save-word="${index}"`} ${saved&&!savedView?'disabled':''}>${icon(savedView?'trash':saved?'check':'plus')}${escT(savedView?'remove':saved?'saved':'save')}</button><button class="button ghost small" data-say-word="${index}" data-word-source="${savedView?'vocab':'feedback'}" aria-label="${escT('replay')}: ${escape(word.word)}">${icon('sound')}</button></div></article>`;
}
function feedback() {
  const data=S.feedback;
  if (!data) return `<div class="page-heading"><div><p class="eyebrow">${escT('completed')}</p><h1>${escT('feedbackTitle')}</h1><p>${escT('noFeedback')}</p></div></div>${errorBanner()}<button class="button" data-action="finish-now" ${busy()?'disabled':''}>${icon('clock')}${escT(busy()?'reviewing':'retry')}</button>`;
  return `<div class="page-heading"><div><p class="eyebrow">${escT('completed')} / ${escape(sceneTitle(data.scenario))}</p><h1>${escT('feedbackTitle')}</h1><p>${escT('feedbackIntro')}</p></div><button class="button" data-start="${data.scenario}" ${busy()?'disabled':''}>${icon('arrow')}${escT('again')}</button></div>${errorBanner()}<div class="summary-strip"><div class="metric"><strong>${data.turn_count}</strong><span>${escT('answers')}</span></div><div class="metric"><strong>${data.vocabulary.length}</strong><span>${escT('phrases')}</span></div><p>${escT('noScore')}</p><button class="button secondary small" data-action="history">${icon('book')}${escT('history')}</button></div><div class="feedback-layout"><section><h2 class="section-heading">${escT('grammar')}</h2>${data.corrections.length?data.corrections.map(c=>`<article class="correction"><p class="correction-label">${escT('before')}</p><p class="original" lang="en">${escape(c.original)}</p><p class="correction-label">${escT('after')}</p><p class="corrected" lang="en">${escape(c.corrected)}</p><p class="explanation"><strong>${escT('why')}</strong> ${escape(local(c.explanation))}</p></article>`).join(''):`<div class="empty-state">${icon('check')}<h2>${escT(data.turn_count?'noErrors':'noTurns')}</h2><p>${escT('noErrorsNote')}</p></div>`}<p class="fine-print">${escT('aiFeedback')}</p><button class="button secondary" data-nav="city">${icon('city')}${escT('back')}</button></section><section><h2 class="section-heading">${escT('words')}</h2>${data.vocabulary.map((word,index)=>wordCard(word,{index})).join('')}</section></div>`;
}
function filteredWords() {
  const query=S.search.toLocaleLowerCase();
  return S.vocab.filter(word=>(S.filter==='all'||word.scenario===S.filter)&&`${word.word} ${local(word.meaning)} ${word.example}`.toLocaleLowerCase().includes(query));
}
function vocabCards() {
  const words=filteredWords();
  return words.length?words.map(word=>wordCard(word,{savedView:true,index:S.vocab.indexOf(word)})).join(''):`<p class="fine-print">${escT('noMatches')}</p>`;
}
function vocabulary() {
  return `<div class="page-heading"><div><p class="eyebrow">${escT('vocabulary')}</p><h1>${escT('vocabTitle')}</h1><p>${escT('vocabIntro')}</p></div>${S.vocab.length?`<button class="button" data-action="flashcards">${icon('book')}${escT('review')}</button>`:''}</div>${S.vocab.length?`<div class="toolbar"><label class="search-input">${icon('search')}<input id="vocab-search" type="search" placeholder="${escT('search')}" value="${escape(S.search)}" aria-label="${escT('search')}"></label><select id="vocab-filter" aria-label="${escT('all')}">${['all',...sceneKeys].map(id=>`<option value="${id}" ${S.filter===id?'selected':''}>${escape(id==='all'?t('all'):sceneTitle(id))}</option>`).join('')}</select><button class="button secondary" data-action="export">${icon('download')}${escT('export')}</button></div><div class="vocab-grid" id="vocab-grid">${vocabCards()}</div><p class="fine-print">${escT('localWords')}</p>`:`<div class="empty-state">${icon('book')}<h2>${escT('empty')}</h2><p>${escT('emptyText')}</p><button class="button" data-nav="city">${icon('arrow')}${escT('city')}</button></div>`}`;
}
function settings() {
  return `<div class="page-heading"><div><p class="eyebrow">${escT('settings')}</p><h1>${escT('settingsTitle')}</h1><p>${escT('settingsIntro')}</p></div></div><div class="settings-grid"><section class="settings-panel"><section class="setting provider-panel"><h2>${escT('providerSettings')}</h2>${chip()}<p>${escT('apiNote')}</p><p>${escT('configureNativeNote')}</p>${configureButton()}</section><div class="setting"><label for="setting-language">${escT('language')}</label><select id="setting-language" data-pref="language">${languageOptions()}</select><p>${escT('languageNote')}</p></div><div class="setting"><label for="voice">${escT('voice')}</label><select id="voice" data-pref="voice">${['american','british'].map(voice=>`<option value="${voice}" ${S.prefs.voice===voice?'selected':''}>${escT(voice)}</option>`).join('')}</select></div><div class="setting"><label for="speed">${escT('speed')} · <span id="speed-value">${S.prefs.speed.toFixed(2)}×</span></label><input id="speed" type="range" min="0.75" max="1.2" step="0.05" value="${S.prefs.speed}" data-pref="speed"><div class="range-labels"><span>${escT('slow')}</span><span>${escT('normal')}</span><span>${escT('quick')}</span></div><button class="button secondary small" data-action="test-voice" ${audioBusy()||busy()?'disabled':''}>${icon('sound')}${escT('testVoice')}</button>${audioBusy()?`<button class="button small ghost" data-action="stop-audio">${escT('stopAudio')}</button>`:''}</div><div class="setting"><label><input type="checkbox" data-pref="autoVoice" ${S.prefs.autoVoice?'checked':''}>${escT('autoVoice')}</label></div><div class="setting"><label for="default-mode">${escT('defaultMode')}</label><select id="default-mode" data-pref="mode">${['manual','handsfree'].map(mode=>`<option value="${mode}" ${S.prefs.mode===mode?'selected':''}>${escT(mode)}</option>`).join('')}</select></div><div class="setting"><label for="level">${escT('level')}</label><select id="level" data-pref="level"><option value="A1" ${S.prefs.level==='A1'?'selected':''}>${escT('beginner')}</option><option value="A2" ${S.prefs.level==='A2'?'selected':''}>${escT('developing')}</option></select><p>${escT('levelNote')}</p></div><p class="fine-print">${escT('savePrefs')}</p></section><aside class="settings-aside">${icon('shield')}<h2>${escT('privacy')}</h2><p>${escT('privacyText')}</p><div class="divider"></div><h2>${escT('model')}</h2><p>${escape(S.status?.engine||t('noEngineName'))} · Kokoro · Whisper</p><p>${escT('modelNote')}</p><div class="divider"></div><p>${escT('localWords')}</p><button class="button danger small" data-action="clear-words" ${S.vocab.length?'':'disabled'}>${icon('trash')}${escT('clearWords')}</button></aside></div>${errorBanner()}`;
}
function modal() {
  const current=S.modal; if (!current) return '';
  let body='', label=current.title || t('history');
  if (current.kind==='confirm') body=`<h2>${escape(current.title)}</h2><p>${escape(current.text||'')}</p><div class="modal-actions"><button class="button secondary" data-action="modal-close">${escT('cancel')}</button><button class="button" data-action="modal-confirm">${escape(current.ok||t('confirm'))}</button></div>`;
  else if (current.kind==='setup') {
    const data=scene(current.scenario); label=sceneTitle(current.scenario);
    body=`<div class="scene-preview"><img src="/assets/${data.image}" alt=""><div><h2>${escape(label)}</h2><p>${escape(local(data.role))}</p></div></div><p class="mission-preview">${escape(local(data.mission))}</p>${current.scenario==='directions'?directionSchematic('setup'):''}<p>${escT(configured()?'setupContinue':'configureIntro')}</p>${!configured()?`<p>${escT('configureNativeNote')}</p>`:''}${errorBanner()}<div class="modal-actions"><button class="button secondary" data-action="modal-close" ${S.configuring?'disabled':''}>${escT('close')}</button>${configured()?`<button class="button" data-start="${current.scenario}">${escT('open')}${icon('arrow')}</button>`:configureButton()}</div>`;
  } else if (current.kind==='consent') {
    label=t('consentTitle'); body=`${icon('mic')}<h2>${escT('consentTitle')}</h2><p>${escT('consentBody')}</p><p>${escT('consentFooter')}</p><div class="modal-actions"><button class="button secondary" data-action="modal-close">${escT('cancel')}</button><button class="button" data-action="consent">${escT('consentAction')}</button></div>`;
  } else if (current.kind==='history') body=`<h2>${escT('history')}</h2>${(S.feedback?.messages||[]).map(message=>`<div class="history-item"><strong>${message.role==='user'?escT('you'):'Lucy'}</strong><span lang="en">${escape(message.content)}</span></div>`).join('')}<div class="modal-actions"><button class="button secondary" data-action="modal-close">${escT('close')}</button></div>`;
  else if (current.kind==='flash' && S.vocab.length) {
    const word=S.vocab[S.flash%S.vocab.length]; label=t('review');
    body=`<div class="flashcard"><div class="counter">${S.flash%S.vocab.length+1} / ${S.vocab.length}</div><h2 class="big-word" lang="en">${escape(word.word)}</h2><div class="meaning">${S.revealed?`<p>${escape(local(word.meaning))}</p><p lang="en">${escape(word.example)}</p>`:`<button class="button secondary" data-action="reveal">${escT('reveal')}</button>`}</div><div class="modal-actions"><button class="button secondary" data-action="modal-close">${escT('close')}</button><button class="button" data-action="next-card">${escT('next')}${icon('arrow')}</button></div></div>`;
  }
  return `<div class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="${escape(label)}">${body}</section></div>`;
}
function render() {
  document.documentElement.lang=S.prefs.language;
  const title=S.view==='practice'?sceneTitle(S.session?.scenario||'city'):t(S.view);
  document.title=`SPEAKCITY AI · ${title}`;
  const contents={city,practice,feedback,vocabulary,settings};
  $('#app').innerHTML=`${header()}<main class="page" ${S.modal?'inert':''}>${(contents[S.view]||city)()}${footer()}</main>${modal()}`;
  if (S.modal) $('.shell-header')?.setAttribute('inert','');
  if (S.view==='practice') { const chat=$('#chat'); if (chat) chat.scrollTop=chat.scrollHeight; }
  if (S.modal) queueMicrotask(()=>$('.modal button:not(:disabled)')?.focus());
}
function confirmDialog(title,action,text='',ok='') { S.modal={kind:'confirm',title,text,action,ok}; render(); }
async function goto(view) {
  if (!['city','practice','feedback','vocabulary','settings'].includes(view)) return;
  if (busy()) { toast(t('turn_in_progress')); return; }
  await stopCapture(); stopAudio(); rememberDraft(); S.view=view; S.error=''; S.modal=null; render(); window.scrollTo(0,0);
}
async function configureAI() {
  if (busy()) return;
  S.configuring=true; await stopCapture(); stopAudio(); S.error=''; render();
  try {
    // No body: the native application owns the settings window and all credentials.
    const result=await api('/api/configure',{method:'POST',timeoutMs:0});
    if (typeof result?.configured !== 'boolean') throw new Error('invalid_response');
    S.status={...S.status,conversation_installed:result.configured};
    await bootstrap({restore:false});
    toast(t(configured()?'setupDone':'setupCancelled'));
  } catch (error) { S.error=error.message||'server_error'; }
  finally { S.configuring=false; render(); }
}
async function start(scenario,force=false) {
  if (!sceneKeys.includes(scenario)) { setError('invalid_scenario'); return; }
  if (busy()) return;
  if (S.booting) { toast(t('checkingRequest')); return; }
  if (S.session && !S.session.ended && S.session.scenario===scenario) { await goto('practice'); return; }
  if (!configured()) { await stopCapture(); stopAudio(); S.modal={kind:'setup',scenario}; render(); return; }
  const oldId=S.session&&!S.session.ended?S.session.id:S.restoreId;
  if (oldId && !force) { await stopCapture(); stopAudio(); confirmDialog(t('newConversation'),()=>start(scenario,true)); return; }
  S.starting=true; S.modal=null; await stopCapture(); stopAudio(); S.error=''; phase('thinking');
  clearTimeout(S.restoreTimer); S.restoreToken++;
  try {
    if (oldId) {
      try { await api(sessionPath(oldId),{method:'DELETE'}); }
      catch (error) { if (error.httpStatus!==404 && error.message!=='session_expired') throw error; }
      S.session=null; S.feedback=null; S.restoreId=null; rememberSession(); rememberDraft();
    }
    const data=await api('/api/sessions',{method:'POST',body:{scenario,language:S.prefs.language,level:S.prefs.level}});
    if (!safeId(data?.session_id) || typeof data.reply!=='string') throw new Error('invalid_response');
    S.session={id:data.session_id,scenario,count:0,ended:false,messages:[{role:'assistant',content:data.reply.slice(0,16000)}]};
    S.feedback=null; S.draft=''; S.pending=null; S.hint=false; S.mode=S.prefs.mode; S.view='practice'; S.phase='idle'; S.starting=false;
    rememberSession(); rememberDraft(); render(); window.scrollTo(0,0);
    if (S.prefs.autoVoice && !document.hidden) await playSpeech(data.reply);
  } catch (error) { S.starting=false; setError(error); }
  finally { S.starting=false; }
}
async function send() {
  if (!S.session || S.session.ended || S.session.count>=maxTurns() || busy() || audioBusy() || S.recorder) return;
  const text=S.draft.trim().slice(0,400); if (!text) return;
  const sessionId=S.session.id;
  if (!S.pending || S.pending.text!==text) S.pending={text,request_id:crypto.randomUUID(),base_count:S.session.count};
  rememberDraft(); S.error=''; phase('thinking');
  try {
    const result=await api(`${sessionPath(sessionId)}/turn`,{method:'POST',body:{text:S.pending.text,request_id:S.pending.request_id}});
    if (typeof result?.reply!=='string' || !turnCount(result.turn_count) || result.turn_count<1 || result.turn_count<S.session.count || result.turn_count>S.session.count+1) throw new Error('invalid_response');
    if (S.session?.id!==sessionId) return;
    // An idempotent retry can return an already-restored turn. Never append it twice.
    if (result.turn_count>S.session.count) S.session.messages.push({role:'user',content:text},{role:'assistant',content:result.reply.slice(0,16000)});
    S.session.count=result.turn_count; S.draft=''; S.pending=null; S.phase='idle'; rememberDraft();
    if (result.at_limit || result.turn_count>=maxTurns()) S.autoActive=false;
    render();
    if (S.prefs.autoVoice && S.view==='practice' && !document.hidden) await playSpeech(result.reply);
    else scheduleListen();
  } catch (error) { setError(error); }
}
function stopAudio() {
  S.speakSeq++; S.audioAbort?.abort(); S.audioAbort=null;
  if (S.audio) { S.audio.onended=null; S.audio.onerror=null; S.audio.pause(); S.audio.src=''; S.audio=null; }
  if (S.audioUrl) { URL.revokeObjectURL(S.audioUrl); S.audioUrl=null; }
  if (audioBusy()) S.phase='idle';
}
async function playSpeech(text) {
  if (S.recorder || busy() || document.hidden || typeof text!=='string' || !text.trim()) return;
  if (S.status?.tts_installed===false) { S.autoActive=false; toast(t('ttsUnavailable')); return; }
  stopAudio(); const seq=S.speakSeq; S.error=''; phase('preparing');
  const controller=new AbortController(); S.audioAbort=controller;
  const timer=setTimeout(()=>controller.abort(),90000);
  try {
    const response=await fetch('/api/tts',{method:'POST',credentials:'same-origin',signal:controller.signal,
      headers:{'Content-Type':'application/json','X-Speakcity':'1'},body:JSON.stringify({text,voice:S.prefs.voice,speed:S.prefs.speed})});
    if (!response.ok) { const data=await response.json().catch(()=>({})); throw new Error(typeof data.detail==='string'?data.detail:'playback'); }
    const blob=await response.blob(); if (seq!==S.speakSeq || document.hidden) return;
    if (!blob.size) throw new Error('playback');
    S.audioUrl=URL.createObjectURL(blob); S.audio=new Audio(S.audioUrl); S.phase='speaking'; render();
    S.audio.onended=()=>{ if (seq!==S.speakSeq) return; stopAudio(); S.phase='idle'; render(); scheduleListen(); };
    S.audio.onerror=()=>{ if (seq!==S.speakSeq) return; stopAudio(); S.autoActive=false; S.phase='idle'; render(); toast(t('playback')); };
    await S.audio.play();
  } catch (error) {
    if (seq!==S.speakSeq) return;
    stopAudio(); S.autoActive=false; S.phase='idle'; render();
    toast(error.name==='NotAllowedError' ? t('playback') : errorText(error.name==='AbortError'?'network':error.message||'playback'));
  } finally { clearTimeout(timer); }
}
async function stopCapture() {
  S.autoActive=false; clearTimeout(S.listenTimer); S.listenTimer=null;
  const recorder=S.recorder; S.recorder=null;
  if (recorder) await recorder.cancel();
  if (S.phase==='listening') S.phase='idle';
}
function scheduleListen() {
  clearTimeout(S.listenTimer);
  if (!S.autoActive || S.mode!=='handsfree' || S.view!=='practice' || !S.session || S.session.ended || S.session.count>=maxTurns() || document.hidden) return;
  S.listenTimer=setTimeout(()=>{ if (S.autoActive && S.view==='practice' && !document.hidden && !busy() && !audioBusy() && !S.recorder) record(); },450);
}
async function requestMic() {
  if (S.recorder) { S.autoActive=false; await S.recorder.stop('manual'); return; }
  if (S.autoActive) { await stopCapture(); render(); return; }
  if (busy() || audioBusy() || !S.session || S.session.ended || S.session.count>=maxTurns()) return;
  if (S.status?.stt_installed===false) { toast(t('sttUnavailable')); return; }
  if (!S.consent) { S.modal={kind:'consent'}; render(); return; }
  if (S.mode==='handsfree') S.autoActive=true;
  await record();
}
async function record() {
  if (!S.consent || !S.session || S.session.ended || S.session.count>=maxTurns() || S.view!=='practice' || busy() || audioBusy() || S.recorder || document.hidden) return;
  if (S.status?.stt_installed===false) { S.autoActive=false; toast(t('sttUnavailable')); return; }
  S.error=''; const sessionId=S.session.id;
  const recorder=new window.SpeakcityRecorder({handsfree:S.mode==='handsfree',
    onDone:async(blob,reason)=>{
      if (S.recorder!==recorder || S.session?.id!==sessionId) return;
      S.recorder=null;
      if (reason==='limit') { S.autoActive=false; toast(t('recordingCap')); }
      phase('transcribing');
      try {
        const result=await api('/api/stt',{method:'POST',body:blob,raw:true});
        if (typeof result?.text!=='string') throw new Error('invalid_response');
        if (S.session?.id!==sessionId) return;
        S.draft=result.text.trim().slice(0,400); S.pending=null; S.phase='idle'; rememberDraft(); render();
        if (!S.draft) { S.autoActive=false; toast(t('noSpeech')); return; }
        if (S.autoActive && S.mode==='handsfree' && !document.hidden && S.view==='practice') await send();
        else $('#answer')?.focus();
      } catch (error) { setError(error); }
    },
    onError:code=>{ if (S.recorder!==recorder) return; S.recorder=null; S.autoActive=false; S.phase='idle'; render(); toast(errorText(code)); },
    onLevel:rms=>{ const button=$('#mic-button'); if (button) button.classList.toggle('voice-detected',rms>.012); }
  });
  S.recorder=recorder; phase('listening');
  try { await recorder.start(); }
  catch (error) {
    await recorder.cancel();
    if (S.recorder!==recorder) return;
    S.recorder=null; S.autoActive=false; S.phase='idle'; render();
    toast(t(error.name==='NotAllowedError'?'micDenied':error.name==='NotFoundError'?'micMissing':'micUnsupported'));
  }
}
async function finish() {
  if (!S.session || busy()) return;
  await stopCapture(); stopAudio(); S.session.ended=true; S.error=''; S.view='feedback'; rememberDraft(); phase('reviewing');
  try {
    const result=await api(`${sessionPath(S.session.id)}/finish`,{method:'POST',body:{language:S.prefs.language}});
    S.feedback=normalizeFeedback(result,S.session.scenario);
    if (!S.feedback.messages.length) S.feedback.messages=[...S.session.messages];
    S.phase='idle'; rememberSession(); render(); window.scrollTo(0,0);
  } catch (error) { setError(error); }
}
function saveWord(index) {
  const word=S.feedback?.vocabulary[index]; if (!word) return;
  if (!S.vocab.some(item=>wordKey(item)===wordKey(word))) {
    if (S.vocab.length>=1000) { toast(t('vocabFull')); return; }
    S.vocab.push({...word,savedAt:new Date().toISOString()});
  }
  const saved=store('sc_vocabulary_v1',S.vocab); render(); if (saved) toast(t('stored'));
}
function exportWords() {
  const blob=new Blob([JSON.stringify({version:1,words:S.vocab},null,2)],{type:'application/json'});
  const url=URL.createObjectURL(blob), anchor=document.createElement('a');
  anchor.href=url; anchor.download='speakcity-vocabulary.json'; anchor.hidden=true;
  document.body.appendChild(anchor); anchor.click(); anchor.remove();
  setTimeout(()=>URL.revokeObjectURL(url),30000);
}
async function setPref(key,value) {
  if (!own(defaults,key)) return;
  if (['mode','language'].includes(key)) await stopCapture();
  S.prefs=preferencesFrom({...S.prefs,[key]:value});
  if (key==='mode') S.mode=S.prefs.mode;
  savePrefs();
  if (['voice','speed','autoVoice'].includes(key)) stopAudio();
  render(); if (key==='autoVoice' && !S.prefs.autoVoice) scheduleListen();
}
$('#app').addEventListener('submit',event=>{ if (event.target.id==='answer-form') { event.preventDefault(); send(); } });
$('#app').addEventListener('input',event=>{
  const el=event.target;
  if (el.id==='answer') {
    S.draft=el.value.slice(0,400); rememberDraft();
    if ($('#char-count')) $('#char-count').textContent=`${S.draft.length} / 400`;
    const button=$('#answer-form button'); if (button) button.disabled=!S.draft.trim()||busy()||audioBusy()||!!S.recorder||S.session?.ended||S.session?.count>=maxTurns();
  }
  if (el.id==='vocab-search') { S.search=el.value; $('#vocab-grid').innerHTML=vocabCards(); }
  if (el.dataset.pref==='speed') {
    S.prefs=preferencesFrom({...S.prefs,speed:Number(el.value)}); savePrefs(); stopAudio();
    if ($('#speed-value')) $('#speed-value').textContent=`${S.prefs.speed.toFixed(2)}×`;
  }
});
$('#app').addEventListener('change',async event=>{
  const el=event.target;
  if (el.dataset.pref) await setPref(el.dataset.pref,el.type==='checkbox'?el.checked:el.type==='range'?Number(el.value):el.value);
  if (el.id==='vocab-filter') { S.filter=['all',...sceneKeys].includes(el.value)?el.value:'all'; $('#vocab-grid').innerHTML=vocabCards(); }
});
$('#app').addEventListener('click',async event=>{
  const el=event.target.closest('button,a'); if (!el || el.disabled) return;
  if (el.dataset.nav) { await goto(el.dataset.nav); return; }
  if (el.classList.contains('brand')) { event.preventDefault(); await goto('city'); return; }
  if (el.dataset.start) { S.modal=null; await start(el.dataset.start); return; }
  if (el.dataset.mode) {
    if (!['manual','handsfree'].includes(el.dataset.mode) || busy() || audioBusy()) return;
    await stopCapture(); S.mode=el.dataset.mode; render(); return;
  }
  if (el.dataset.replay!==undefined) {
    const message=S.session?.messages[Number(el.dataset.replay)];
    if (message?.role==='assistant') { S.autoActive=false; await playSpeech(message.content); } return;
  }
  if (el.dataset.saveWord!==undefined) { saveWord(Number(el.dataset.saveWord)); return; }
  if (el.dataset.removeWord) {
    S.vocab=S.vocab.filter(word=>wordKey(word)!==el.dataset.removeWord);
    const saved=store('sc_vocabulary_v1',S.vocab); render(); if (saved) toast(t('removed')); return;
  }
  if (el.dataset.sayWord!==undefined) {
    const list=el.dataset.wordSource==='vocab'?S.vocab:S.feedback?.vocabulary, word=list?.[Number(el.dataset.sayWord)];
    if (word) await playSpeech(word.word+'. '+word.example); return;
  }
  switch (el.dataset.action) {
    case 'configure':await configureAI(); break;
    case 'mic':await requestMic(); break;
    case 'pause':await stopCapture(); stopAudio(); render(); break;
    case 'mute':await setPref('autoVoice',!S.prefs.autoVoice); break;
    case 'stop-audio':S.autoActive=false; clearTimeout(S.listenTimer); stopAudio(); render(); break;
    case 'hint':S.hint=!S.hint; render(); break;
    case 'resume':await goto('practice'); break;
    case 'finish':await stopCapture(); stopAudio(); confirmDialog(t('endConfirm'),finish); break;
    case 'finish-now':await finish(); break;
    case 'retry':
      S.error='';
      if (S.restoreId) await restoreSession(S.restoreId);
      else if (S.view==='feedback') await finish();
      else if (S.view==='practice' && S.draft.trim()) await send();
      else await bootstrap();
      break;
    case 'modal-close':if (!S.configuring) { S.modal=null; render(); } break;
    case 'modal-confirm':{
      const action=S.modal?.action; S.modal=null; render(); if (action) await action(); break;
    }
    case 'consent':
      S.consent=true; S.modal=null; render(); if (S.mode==='handsfree') S.autoActive=true; await record(); break;
    case 'history':S.modal={kind:'history'}; render(); break;
    case 'flashcards':if (S.vocab.length) { S.flash=0; S.revealed=false; S.modal={kind:'flash'}; render(); } break;
    case 'reveal':S.revealed=true; render(); break;
    case 'next-card':S.flash++; S.revealed=false; render(); break;
    case 'test-voice':await playSpeech("Hi, I'm Lucy. Let's practise English together. Take your time."); break;
    case 'export':exportWords(); break;
    case 'clear-words':confirmDialog(t('clearConfirm'),()=>{ S.vocab=[]; store('sc_vocabulary_v1',[]); render(); }); break;
  }
});
document.addEventListener('keydown',event=>{
  if (event.key==='Escape') {
    if (S.modal) { if (!S.configuring) { S.modal=null; render(); } }
    else { stopCapture().then(render); stopAudio(); render(); }
  }
  if (S.modal && event.key==='Tab') {
    const nodes=[...document.querySelectorAll('.modal button,.modal input,.modal select')].filter(node=>!node.disabled);
    if (nodes.length) {
      const first=nodes[0],last=nodes[nodes.length-1];
      if (event.shiftKey && document.activeElement===first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement===last) { event.preventDefault(); first.focus(); }
    }
  }
  if (event.key==='Enter' && event.ctrlKey && event.target.id==='answer') { event.preventDefault(); send(); }
});
function releaseResources() {
  // cancel() stops tracks synchronously, before its first await (including pending permission races).
  stopCapture().catch(()=>{}); stopAudio(); clearTimeout(S.restoreTimer); S.restoreToken++;
}
window.addEventListener('pagehide',releaseResources);
window.addEventListener('beforeunload',()=>{ rememberDraft(); releaseResources(); for (const request of requests) request.abort(); });
document.addEventListener('visibilitychange',()=>{
  if (document.hidden) { stopCapture().then(()=>{}); stopAudio(); }
  else render();
});
setInterval(()=>{
  const el=$('#elapsed');
  if (el && transient.has(S.phase)) { const seconds=Math.floor((Date.now()-S.phaseAt)/1000); el.textContent=seconds>2?` · ${seconds} ${t('elapsed')}`:''; }
},1000);
async function restoreSession(id,attempt=0,token) {
  if (!safeId(id)) { S.restoreId=null; rememberSession(); return; }
  if (token===undefined) { token=++S.restoreToken; clearTimeout(S.restoreTimer); }
  S.restoreId=id; S.restoring=true; render();
  try {
    const result=await api(sessionPath(id)); if (token!==S.restoreToken) return;
    if (!sceneKeys.includes(result?.scenario) || !turnCount(result.turn_count) || !Array.isArray(result.messages) || typeof result.ended!=='boolean') throw new Error('invalid_response');
    const wasEmpty=!S.session;
    S.session={id,scenario:result.scenario,count:result.turn_count,messages:normalizeMessages(result.messages),ended:result.ended};
    S.feedback=result.feedback?normalizeFeedback(result.feedback,result.scenario):null;
    if (S.feedback && !S.feedback.messages.length) S.feedback.messages=[...S.session.messages];
    if (wasEmpty) restoreDraft(S.session);
    else if (S.pending && S.session.count>S.pending.base_count) restoreDraft(S.session);
    S.view=result.ended?'feedback':'practice'; S.phase=result.busy?(result.ended?'reviewing':'thinking'):'idle';
    S.phaseAt=Date.now(); S.error=''; S.restoring=false;
    if (result.busy && attempt<90) S.restoreTimer=setTimeout(()=>restoreSession(id,attempt+1,token),2000);
    else if (result.busy) { S.phase='idle'; S.error='turn_in_progress'; }
    else { S.restoreId=null; if (wasEmpty) toast(t('welcomeBack')); }
    render();
  } catch (error) {
    if (token!==S.restoreToken) return;
    S.restoring=false; S.phase='idle';
    if (error.httpStatus===404 || error.message==='session_expired') {
      S.session=null; S.feedback=null; S.restoreId=null; rememberSession(); rememberDraft(); S.view='city'; S.error='session_expired';
    } else { S.error=error.message==='network'?'restoreFailed':error.message; }
    render();
  }
}
async function bootstrap({restore=true}={}) {
  if (S.bootPromise) return S.bootPromise;
  S.bootPromise=(async()=>{
    try {
      const data=await api('/api/bootstrap');
      if (!isObject(data) || typeof data.conversation_installed!=='boolean' || typeof data.tts_installed!=='boolean' || typeof data.stt_installed!=='boolean') throw new Error('invalid_response');
      S.scenes=catalogFrom(data.scenarios,bundledCatalog);
      // conversation_installed means provider configuration, NOT a local conversation model.
      S.status={conversation_installed:data.conversation_installed,tts_installed:data.tts_installed,stt_installed:data.stt_installed,
        engine:textValue(data.engine,200),tts:'Kokoro',stt:'Whisper',max_turns:8,desktop:true};
      S.connected=true; S.booting=false;
      if (['network','invalid_response','notConfigured','restoreFailed'].includes(errorKey(S.error))) S.error='';
      let previous=null; try { previous=safeId(sessionStorage.getItem('sc_active')); } catch {}
      if (restore && previous && !S.session) await restoreSession(previous);
      else render();
      return true;
    } catch (error) { S.connected=false; S.booting=false; S.error=error.message||'network'; render(); return false; }
  })();
  try { return await S.bootPromise; } finally { S.bootPromise=null; }
}
render(); bootstrap();
})();
