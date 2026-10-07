// Giải mã H.264 bằng WebCodecs (dùng GPU/CPU của trình duyệt), vẽ lên canvas.

function findSps(data) {
  for (let i = 0; i + 4 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && (data[i + 2] === 1 || (data[i + 2] === 0 && data[i + 3] === 1))) {
      const start = data[i + 2] === 1 ? i + 3 : i + 4;
      if ((data[start] & 0x1f) === 7) return start;
    }
  }
  return -1;
}

function codecFromConfig(data) {
  const s = findSps(data);
  if (s < 0 || s + 3 >= data.length) return 'avc1.42001f';
  const hex = (n) => n.toString(16).padStart(2, '0');
  return 'avc1.' + hex(data[s + 1]) + hex(data[s + 2]) + hex(data[s + 3]);
}

export class StreamDecoder {
  /**
   * @param {HTMLCanvasElement} canvas
   * @param {{onNeedKey?:Function, onFirstFrame?:Function, onResize?:Function, hw?:string}} opt
   */
  constructor(canvas, opt = {}) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    this.opt = opt;
    this.config = null;
    this.codec = null;
    this.waitKey = true;
    this.ts = 0;
    this.hasFrame = false;
    this.frames = 0;
    this.closed = false;
    this._create();
  }

  _create() {
    this.decoder = new VideoDecoder({
      output: (f) => this._draw(f),
      error: (e) => this._fail(e),
    });
    this.configured = false;
  }

  _configure() {
    if (this.decoder.state === 'closed') this._create();
    try {
      this.decoder.configure({
        codec: this.codec,
        optimizeForLatency: true,
        hardwareAcceleration: this.opt.hw || 'no-preference',
      });
      this.configured = true;
    } catch (e) {
      console.warn('configure lỗi', this.codec, e);
      this.configured = false;
    }
  }

  feed(flags, data) {
    if (this.closed) return;
    const isConfig = (flags & 1) !== 0;
    const isKey = (flags & 2) !== 0;
    if (isConfig) {
      this.config = data.slice();
      const codec = codecFromConfig(this.config);
      if (!this.configured || codec !== this.codec || this.decoder.state !== 'configured') {
        this.codec = codec;
        this._configure();
      }
      this.waitKey = true;
      return;
    }
    if (!this.configured || this.decoder.state !== 'configured') return;
    if (this.waitKey && !isKey) return;
    if (!isKey && this.decoder.decodeQueueSize > 8) {
      // giải mã không kịp → bỏ tới keyframe kế tiếp
      this.waitKey = true;
      this.opt.onNeedKey && this.opt.onNeedKey();
      return;
    }
    let payload = data;
    if (isKey) {
      this.waitKey = false;
      if (this.config) {
        payload = new Uint8Array(this.config.length + data.length);
        payload.set(this.config, 0);
        payload.set(data, this.config.length);
      }
    }
    try {
      this.ts += 33333;
      this.decoder.decode(new EncodedVideoChunk({ type: isKey ? 'key' : 'delta', timestamp: this.ts, data: payload }));
    } catch (e) {
      this._fail(e);
    }
  }

  _draw(frame) {
    if (this.closed) { frame.close(); return; }
    const w = frame.displayWidth;
    const h = frame.displayHeight;
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
      this.opt.onResize && this.opt.onResize(w, h);
    }
    this.ctx.drawImage(frame, 0, 0, w, h);
    frame.close();
    this.frames++;
    if (!this.hasFrame) {
      this.hasFrame = true;
      this.opt.onFirstFrame && this.opt.onFirstFrame();
    }
  }

  _fail(e) {
    if (this.closed) return;
    console.warn('decoder lỗi', e && e.message);
    try { this.decoder.close(); } catch (_) { /* bỏ qua */ }
    this._create();
    this.waitKey = true;
    if (this.codec) this._configure();
    this.opt.onNeedKey && this.opt.onNeedKey();
  }

  /** Gọi khi bắt đầu nhận lại luồng (sau khi ngừng đăng ký). */
  resume() {
    this.waitKey = true;
  }

  close() {
    this.closed = true;
    try { this.decoder.close(); } catch (_) { /* bỏ qua */ }
  }
}

export function webCodecsSupported() {
  return typeof window.VideoDecoder === 'function' && typeof window.EncodedVideoChunk === 'function';
}
