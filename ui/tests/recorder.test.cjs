'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.resolve(__dirname,'..');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
function deferred(){let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};}
function setup({handsfree=false,permission,worklet,resume,closeReject=false,sampleRate=48000,disconnectThrows=false}={}){
  const timers=new Map(),modules=[],contexts=[],nodes=[],done=[],errors=[],levels=[];
  let time=0,timerId=0;
  const track={stopped:false,onended:null,stop(){this.stopped=true;}};
  const stream={getTracks:()=>[track]};
  const connectable=()=>({connect(target){return target;},disconnect(){this.disconnected=true;if(disconnectThrows)throw new Error('disconnect failed');}});
  class AudioContext{
    constructor(){this.sampleRate=sampleRate;this.state='running';this.destination={};this.audioWorklet={addModule:async url=>{modules.push(url);if(worklet)await worklet.promise;}};contexts.push(this);}
    async resume(){if(resume)await resume.promise;}
    createMediaStreamSource(){return connectable();}
    createGain(){return {...connectable(),gain:{value:1}};}
    async close(){this.closeCalled=true;this.state='closed';if(closeReject)throw new Error('close failed');}
  }
  class AudioWorkletNode{
    constructor(context,name){this.context=context;this.name=name;this.port={onmessage:null,closed:false,close(){this.closed=true;}};Object.assign(this,connectable());nodes.push(this);}
  }
  const window={AudioContext,AudioWorkletNode};
  const context={window,navigator:{mediaDevices:{getUserMedia:async options=>{assert.equal(options.video,false);assert.equal(options.audio.channelCount,1);return permission?permission.promise:stream;}}},AudioContext,AudioWorkletNode,
    performance:{now:()=>time},setInterval:(fn,delay)=>{const id=++timerId;timers.set(id,{fn,delay});return id;},clearInterval:id=>timers.delete(id),
    Float32Array,Int16Array,ArrayBuffer,DataView,Blob,Error};
  vm.createContext(context);vm.runInContext(fs.readFileSync(path.join(root,'recorder.js'),'utf8'),context,{filename:'recorder.js'});
  const recorder=new window.SpeakcityRecorder({handsfree,onDone:(blob,reason)=>done.push({blob,reason}),onError:error=>errors.push(error),onLevel:level=>levels.push(level)});
  return {recorder,track,stream,contexts,nodes,done,errors,levels,modules,timers,context,
    setTime:value=>{time=value;},emit(values){const node=nodes.at(-1);assert.ok(node.port.onmessage);node.port.onmessage({data:values});},
    async runTimers(){for(const timer of [...timers.values()])timer.fn();await tick();}};
}

test('recorder loads the root worklet and outputs mono 16 kHz signed 16-bit PCM WAV',async()=>{
  const h=setup();await h.recorder.start();assert.deepEqual(h.modules,['/recorder-worklet.js']);assert.equal(h.nodes[0].name,'speakcity-capture');
  h.emit(new Float32Array(48000).fill(.5));await h.recorder.stop('manual');
  assert.equal(h.done.length,1);assert.ok(h.track.stopped);assert.equal(h.contexts[0].state,'closed');assert.equal(h.timers.size,0);assert.equal(h.nodes[0].port.onmessage,null);
  const {blob,reason}=h.done[0];assert.equal(reason,'manual');assert.equal(blob.type,'audio/wav');
  const bytes=await blob.arrayBuffer(),view=new DataView(bytes),read=(offset,length)=>String.fromCharCode(...new Uint8Array(bytes,offset,length));
  assert.equal(read(0,4),'RIFF');assert.equal(read(8,4),'WAVE');assert.equal(read(12,4),'fmt ');assert.equal(read(36,4),'data');
  assert.equal(view.getUint16(20,true),1);assert.equal(view.getUint16(22,true),1);assert.equal(view.getUint32(24,true),16000);assert.equal(view.getUint16(34,true),16);
  assert.equal(view.getUint32(40,true),32000);assert.equal(bytes.byteLength,32044);assert.equal(view.getInt16(44,true),16383);
  await h.recorder.stop();assert.equal(h.done.length,1);
});

test('cancel releases tracks synchronously and emits no audio',async()=>{
  const h=setup();await h.recorder.start();h.emit(new Float32Array(16000).fill(.2));
  const stop=h.recorder.cancel();assert.ok(h.track.stopped);await stop;
  assert.equal(h.done.length,0);assert.equal(h.errors.length,0);assert.equal(h.recorder.chunks.length,0);
});

test('cancel during permission prompt stops the late-granted stream without opening audio context',async()=>{
  const permission=deferred(),h=setup({permission});const start=h.recorder.start();
  await h.recorder.cancel();permission.resolve(h.stream);await start;
  assert.ok(h.track.stopped);assert.equal(h.contexts.length,0);assert.equal(h.nodes.length,0);assert.equal(h.done.length,0);
});

test('cancel while the worklet loads cannot reconnect capture',async()=>{
  const worklet=deferred(),h=setup({worklet});const start=h.recorder.start();await tick();
  assert.equal(h.contexts.length,1);await h.recorder.cancel();assert.ok(h.track.stopped);
  worklet.resolve();await start;assert.equal(h.nodes.length,0);assert.equal(h.contexts[0].state,'closed');
});

test('cancel during AudioContext.resume cannot reconnect capture',async()=>{
  const resume=deferred(),h=setup({resume});const start=h.recorder.start();await tick();
  await h.recorder.cancel();resume.resolve();await start;
  assert.ok(h.track.stopped);assert.equal(h.nodes.length,0);assert.equal(h.timers.size,0);
});

test('worklet load failure releases stream and closes audio context',async()=>{
  const worklet=deferred(),h=setup({worklet});const start=h.recorder.start();await tick();
  worklet.reject(new Error('module unavailable'));await assert.rejects(start,/module unavailable/);
  assert.ok(h.track.stopped);assert.equal(h.contexts[0].state,'closed');assert.equal(h.done.length,0);
});

test('cleanup still releases capture when disconnect or AudioContext.close fails',async()=>{
  const h=setup({closeReject:true,disconnectThrows:true});await h.recorder.start();await h.recorder.cancel();
  assert.ok(h.track.stopped);assert.ok(h.contexts[0].closeCalled);assert.equal(h.timers.size,0);
});

test('hands-free segmentation waits for speech and 1.8 seconds of silence',async()=>{
  const h=setup({handsfree:true});await h.recorder.start();
  for(let i=0;i<3;i++){h.setTime(100+i*100);h.emit(new Float32Array(4096).fill(.2));}
  h.setTime(1500);h.emit(new Float32Array(4096));assert.equal(h.done.length,0);
  h.setTime(2200);h.emit(new Float32Array(4096));await tick();
  assert.equal(h.done.length,1);assert.equal(h.done[0].reason,'utterance');assert.ok(h.track.stopped);
});

test('hands-free stops and releases a silent microphone after 12 seconds',async()=>{
  const h=setup({handsfree:true});await h.recorder.start();h.setTime(12001);await h.runTimers();
  assert.deepEqual(h.errors,['noSpeech']);assert.ok(h.track.stopped);assert.equal(h.done.length,0);
});

test('recording samples are hard-bounded even if a timer fires late',async()=>{
  const h=setup();await h.recorder.start();h.emit(new Float32Array(48000*35).fill(.1));await tick();
  assert.equal(h.done.length,1);assert.equal(h.done[0].reason,'limit');assert.ok(h.track.stopped);
  const bytes=await h.done[0].blob.arrayBuffer();assert.equal(bytes.byteLength,44+29*16000*2);
});

test('the 29-second timer releases capture and does not depend on a future audio frame',async()=>{
  const h=setup();await h.recorder.start();h.emit(new Float32Array(48000).fill(.1));h.setTime(29001);await h.runTimers();
  assert.equal(h.done.length,1);assert.equal(h.done[0].reason,'limit');assert.ok(h.track.stopped);
});

test('short recording and removed device fail safely without sending audio',async()=>{
  const short=setup();await short.recorder.start();short.emit(new Float32Array(100));await short.recorder.stop();assert.deepEqual(short.errors,['noSpeech']);
  const removed=setup();await removed.recorder.start();removed.track.onended();await tick();assert.deepEqual(removed.errors,['micMissing']);assert.ok(removed.track.stopped);assert.equal(removed.done.length,0);
});

test('worklet registers exactly speakcity-capture and emits only full mono chunks',()=>{
  let Processor,name;const posted=[];
  class AudioWorkletProcessor{constructor(){this.port={postMessage:chunk=>posted.push(chunk)};}}
  const context={AudioWorkletProcessor,Float32Array,registerProcessor:(key,value)=>{name=key;Processor=value;}};
  vm.createContext(context);vm.runInContext(fs.readFileSync(path.join(root,'recorder-worklet.js'),'utf8'),context);
  assert.equal(name,'speakcity-capture');const processor=new Processor();
  for(let i=0;i<31;i++)assert.equal(processor.process([[new Float32Array(128).fill(.25)]]),true);
  assert.equal(posted.length,0);processor.process([[new Float32Array(128).fill(.25)]]);
  assert.equal(posted.length,1);assert.equal(posted[0].length,4096);assert.equal(posted[0][0],.25);
  processor.process([]);assert.equal(posted.length,1);
});
