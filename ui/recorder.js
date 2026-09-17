/* Actual microphone capture, local turn segmentation, bounded mono PCM WAV output. */
window.SpeakcityRecorder = class {
  constructor({ handsfree, onDone, onError, onLevel }) {
    Object.assign(this, { handsfree, onDone, onError, onLevel });
    this.chunks = []; this.total = 0; this.closed = false; this.heardSpeech = false;
    this.loudFrames = 0; this.lastSpeech = 0; this.started = 0; this.cancelled = false;
  }
  async start() {
    if (this.closed) return;
    if (!navigator.mediaDevices?.getUserMedia || !window.AudioContext || !window.AudioWorkletNode) throw new Error('micUnsupported');
    this.stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false });
    if (this.closed) { this.stream.getTracks().forEach(t => t.stop()); return; }
    try {
      this.context = new AudioContext();
      await this.context.audioWorklet.addModule('/recorder-worklet.js');
      if (this.closed) { await this.release(); return; }
      await this.context.resume();
      if (this.closed) { await this.release(); return; }
      this.stream.getTracks().forEach(track => { track.onended = () => this.stop('device'); });
      this.source = this.context.createMediaStreamSource(this.stream);
      this.node = new AudioWorkletNode(this.context, 'speakcity-capture');
      this.gain = this.context.createGain(); this.gain.gain.value = 0;
      this.source.connect(this.node).connect(this.gain).connect(this.context.destination);
      this.started = performance.now();
      this.node.port.onmessage = ({ data }) => {
        if (this.closed) return;
        const remaining = Math.floor(this.context.sampleRate * 29) - this.total;
        if (remaining <= 0) { this.stop('limit'); return; }
        const chunk = new Float32Array(data).slice(0, remaining);
        if (!chunk.length) return;
        this.chunks.push(chunk); this.total += chunk.length;
        let sum = 0; for (const v of chunk) sum += v * v;
        const rms = Math.sqrt(sum / chunk.length), now = performance.now();
        this.onLevel?.(rms);
        if (rms > .012) { this.loudFrames++; this.lastSpeech = now; }
        if (this.loudFrames >= 3) this.heardSpeech = true;
        if (this.total >= Math.floor(this.context.sampleRate * 29)) this.stop('limit');
        else if (this.handsfree && this.heardSpeech && now - this.lastSpeech > 1800) this.stop('utterance');
      };
      this.timer = setInterval(() => {
        const elapsed = (performance.now() - this.started) / 1000;
        if (elapsed >= 29) this.stop('limit');
        else if (this.handsfree && elapsed >= 12 && !this.heardSpeech) this.stop('silence');
      }, 200);
    } catch (e) { await this.cancel(); throw e; }
  }
  async release() {
    // Release tracks before awaiting anything; pagehide cannot wait for AudioContext.close().
    clearInterval(this.timer);
    this.stream?.getTracks().forEach(track => { track.onended = null; try { track.stop(); } catch {} });
    if (this.node?.port) { this.node.port.onmessage = null; try { this.node.port.close?.(); } catch {} }
    for (const node of [this.node, this.source, this.gain]) { try { node?.disconnect(); } catch {} }
    if (this.context && this.context.state !== 'closed') { try { await this.context.close(); } catch {} }
  }
  async cancel() { this.cancelled = true; await this.stop('cancel'); }
  async stop(reason = 'manual') {
    if (this.closed) return;
    this.closed = true;
    const rate = this.context?.sampleRate || 48000;
    await this.release();
    if (reason === 'cancel' || this.cancelled) { this.chunks = []; return; }
    if (reason === 'device') { this.chunks = []; this.onError?.('micMissing'); return; }
    if (reason === 'silence' || !this.total || this.total / rate < .25) { this.chunks = []; this.onError?.('noSpeech'); return; }
    const input = new Float32Array(this.total); let at = 0;
    for (const chunk of this.chunks) { input.set(chunk, at); at += chunk.length; }
    this.chunks = [];
    const count = Math.floor(input.length * 16000 / rate), output = new Int16Array(count);
    for (let i = 0; i < count; i++) {
      const begin = Math.floor(i * rate / 16000), end = Math.min(input.length, Math.max(begin + 1, Math.floor((i + 1) * rate / 16000)));
      let sum = 0; for (let j = begin; j < end; j++) sum += input[j];
      output[i] = Math.max(-1, Math.min(1, sum / (end - begin))) * 32767;
    }
    const buffer = new ArrayBuffer(44 + output.length * 2), view = new DataView(buffer);
    const str = (offset, text) => [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
    str(0, 'RIFF'); view.setUint32(4, buffer.byteLength - 8, true); str(8, 'WAVE'); str(12, 'fmt ');
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, 16000, true); view.setUint32(28, 32000, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
    str(36, 'data'); view.setUint32(40, output.length * 2, true);
    output.forEach((v, i) => view.setInt16(44 + i * 2, v, true));
    this.onDone(new Blob([buffer], { type: 'audio/wav' }), reason);
  }
};
