// Một phiên scrcpy-server trên thiết bị: 1 socket video (H.264) + 1 socket điều khiển.
'use strict';
const EventEmitter = require('events');
const fs = require('fs');
const path = require('path');
const adb = require('./adb');
const P = require('./protocol');

const SERVER_VERSION = '5.0';
const SERVER_LOCAL = path.join(adb.VENDOR_DIR, 'scrcpy-server');
const SERVER_REMOTE = '/data/local/tmp/controlphone-server.jar';
const SERVER_SIZE = fs.existsSync(SERVER_LOCAL) ? fs.statSync(SERVER_LOCAL).size : 0;

const pushPromises = new Map(); // serial -> Promise

function ensureServerPushed(serial) {
  if (pushPromises.has(serial)) return pushPromises.get(serial);
  const p = (async () => {
    let size = -1;
    try {
      // dọn bản sao jar sót lại từ lần chạy trước, rồi kiểm tra bản gốc
      const out = await adb.shell(serial, `rm -f /data/local/tmp/controlphone-*.jar; stat -c %s ${SERVER_REMOTE} 2>/dev/null`, 8000);
      size = parseInt(out.trim(), 10);
    } catch (_) { /* bỏ qua */ }
    if (size === SERVER_SIZE) return;
    const r = await adb.run([...adb.sel(serial), 'push', SERVER_LOCAL, SERVER_REMOTE], 120000);
    if (r.code !== 0) throw new Error('Không push được scrcpy-server: ' + (r.stderr || r.stdout).trim());
  })();
  pushPromises.set(serial, p);
  p.catch(() => pushPromises.delete(serial));
  return p;
}

function forgetPushed(serial) {
  pushPromises.delete(serial);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class ScrcpySession extends EventEmitter {
  /**
   * @param {object} o {serial, kind:'thumb'|'hq', maxSize, bitRate, maxFps, powerOn}
   */
  constructor(o) {
    super();
    this.serial = o.serial;
    this.kind = o.kind || 'thumb';
    this.opts = o;
    this.width = 0;
    this.height = 0;
    this.deviceName = '';
    this.config = null; // gói SPS/PPS gần nhất
    this.running = false;
    this.closed = false;
    this.logTail = [];
    this.lastResetAt = 0;
    this.bytes = 0;
  }

  async start() {
    const { serial } = this;
    await ensureServerPushed(serial);
    if (this.closed) throw new Error('đã đóng');
    this.scid = (Math.floor(Math.random() * 0x7fffffff)).toString(16).padStart(8, '0');
    const o = this.opts;
    // video=false: phiên "chỉ điều khiển" cho máy đang tạm dừng xem (không mã hoá/gửi hình)
    this.videoOn = o.video !== false;
    const videoArgs = this.videoOn
      ? ['video_codec=h264', `max_size=${o.maxSize | 0}`, `video_bit_rate=${o.bitRate | 0}`, `max_fps=${o.maxFps | 0}`]
      : ['video=false', 'send_device_meta=false'];
    const args = [
      SERVER_VERSION,
      `scid=${this.scid}`,
      'log_level=info',
      'audio=false',
      ...videoArgs,
      'tunnel_forward=true',
      'control=true',
      'clipboard_autosync=false',
      `power_on=${o.powerOn ? 'true' : 'false'}`,
      `stay_awake=${o.stayAwake ? 'true' : 'false'}`,
      'cleanup=true',
      'downsize_on_error=true',
      // khung I thưa (20 giây) để máy đứng yên gần như không tốn băng thông;
      // khi cần hình ngay (mở lại ô, lỗi giải mã) client yêu cầu RESET_VIDEO
      'video_codec_options=i-frame-interval:int=20',
    ];
    // scrcpy-server tự xoá file jar khi chạy (cleanup) → mỗi phiên dùng một bản sao riêng
    const sessionJar = `/data/local/tmp/controlphone-${this.scid}.jar`;
    const cmd = `cp ${SERVER_REMOTE} ${sessionJar} && CLASSPATH=${sessionJar} app_process / com.genymobile.scrcpy.Server ${args.join(' ')}`;
    this.shellSock = await adb.openService(serial, 'shell:' + cmd, 15000);
    this.shellSock.on('data', (d) => {
      const lines = d.toString('utf8').split(/\r?\n/).filter(Boolean);
      for (const l of lines) {
        this.logTail.push(l);
        if (this.logTail.length > 30) this.logTail.shift();
      }
    });
    this.shellSock.on('close', () => this._close('server đã thoát'));
    this.shellSock.resume();

    const socketName = 'localabstract:scrcpy_' + this.scid;
    // Kết nối video: thử lại tới khi server sẵn sàng (byte dummy đầu tiên)
    let video = null;
    let first = null;
    for (let i = 0; i < 100 && !this.closed; i++) {
      try {
        const s = await adb.openService(serial, socketName, 4000);
        first = await new Promise((resolve) => {
          const onData = (d) => { cleanup(); resolve(d); };
          const onClose = () => { cleanup(); resolve(null); };
          const cleanup = () => { s.removeListener('data', onData); s.removeListener('close', onClose); s.pause(); };
          s.on('data', onData);
          s.on('close', onClose);
          s.resume();
        });
        if (first) { video = s; break; }
        s.destroy();
      } catch (_) { /* chưa sẵn sàng */ }
      await sleep(i < 20 ? 100 : 250);
    }
    if (!video) {
      const tail = this.logTail.slice(-3).join(' | ');
      this._close('không kết nối được video');
      throw new Error('Không kết nối được scrcpy' + (tail ? ': ' + tail : ''));
    }
    if (!this.videoOn) {
      // socket đầu tiên chính là socket điều khiển; không có hình nên toạ độ chạm là toạ độ thật của màn hình
      this.control = video;
      this.control.setNoDelay(true);
      this.control.on('error', () => {});
      this.control.on('close', () => this._close('control đóng'));
      this._readControl(first.subarray(1));
      await this.refreshScreenSize();
      this.running = true;
      this.emit('started');
      return;
    }
    this.video = video;
    this.control = await adb.openService(serial, socketName, 8000);
    this.control.setNoDelay(true);
    this.control.on('error', () => {});
    this.control.on('close', () => this._close('control đóng'));
    this._readControl();

    this.running = true;
    this._readVideo(first.subarray(1));
    this.emit('started');
  }

  /** Kích thước màn hình hiện tại (đã tính xoay) — dùng cho phiên chỉ điều khiển. */
  async refreshScreenSize() {
    try {
      const out = await adb.shell(this.serial, "dumpsys window displays | grep -m1 -oE 'cur=[0-9]+x[0-9]+'; wm size", 8000);
      const m = out.match(/cur=(\d+)x(\d+)/) || out.match(/Override size:\s*(\d+)x(\d+)/) || out.match(/Physical size:\s*(\d+)x(\d+)/);
      if (m) { this.width = +m[1]; this.height = +m[2]; }
    } catch (_) { /* giữ kích thước cũ */ }
    if (!this.width) { this.width = 1080; this.height = 2220; }
  }

  _readVideo(initial) {
    let buf = initial;
    let stage = 0; // 0: tên thiết bị, 1: codec id, 2: packets
    const onData = (d) => {
      this.bytes += d.length;
      buf = buf.length ? Buffer.concat([buf, d]) : d;
      for (;;) {
        if (stage === 0) {
          if (buf.length < 64) return;
          this.deviceName = buf.toString('utf8', 0, 64).replace(/\0[\s\S]*$/, '');
          buf = buf.subarray(64);
          stage = 1;
        }
        if (stage === 1) {
          if (buf.length < 4) return;
          const codec = buf.readUInt32BE(0);
          buf = buf.subarray(4);
          if (codec === 0 || codec === 1) {
            this._close('thiết bị từ chối luồng video');
            return;
          }
          stage = 2;
        }
        if (buf.length < 12) return;
        const hi = buf.readUInt32BE(0);
        if (hi & 0x80000000) {
          // session meta: kích thước khung hình mới (xoay màn hình, reset...)
          this.width = buf.readUInt32BE(4);
          this.height = buf.readUInt32BE(8);
          buf = buf.subarray(12);
          this.emit('size', this.width, this.height);
          continue;
        }
        const size = buf.readUInt32BE(8);
        if (buf.length < 12 + size) return;
        const isConfig = (hi & 0x40000000) !== 0;
        const isKey = (hi & 0x20000000) !== 0;
        const data = Buffer.from(buf.subarray(12, 12 + size));
        buf = buf.subarray(12 + size);
        if (isConfig) this.config = data;
        this.emit('packet', isConfig, isKey, data);
      }
    };
    this.video.on('data', onData);
    this.video.on('close', () => this._close('video đóng'));
    this.video.on('error', () => {});
    this.video.resume();
  }

  _readControl(initial) {
    let buf = initial && initial.length ? Buffer.from(initial) : Buffer.alloc(0);
    this.control.on('data', (d) => {
      buf = Buffer.concat([buf, d]);
      for (;;) {
        if (buf.length < 1) return;
        const type = buf[0];
        if (type === 0) { // clipboard
          if (buf.length < 5) return;
          const len = buf.readUInt32BE(1);
          if (buf.length < 5 + len) return;
          this.emit('clipboard', buf.toString('utf8', 5, 5 + len));
          buf = buf.subarray(5 + len);
        } else if (type === 1) { // ack clipboard
          if (buf.length < 9) return;
          buf = buf.subarray(9);
        } else if (type === 2) { // uhid output
          if (buf.length < 5) return;
          const len = buf.readUInt16BE(3);
          if (buf.length < 5 + len) return;
          buf = buf.subarray(5 + len);
        } else {
          buf = Buffer.alloc(0);
          return;
        }
      }
    });
    this.control.resume();
  }

  send(b) {
    if (!this.running || !this.control || this.control.destroyed) return false;
    this.control.write(b);
    return true;
  }

  // --- tiện ích điều khiển (toạ độ chuẩn hoá 0..1) ---
  touch(action, nx, ny, pointerId = P.POINTER_ID_GENERIC_FINGER) {
    if (!this.width || !this.height) return false;
    const x = Math.round(Math.max(0, Math.min(1, nx)) * (this.width - 1));
    const y = Math.round(Math.max(0, Math.min(1, ny)) * (this.height - 1));
    return this.send(P.touch(action, pointerId, x, y, this.width, this.height, action === 1 ? 0 : 1));
  }

  scroll(nx, ny, h, v) {
    if (!this.width || !this.height) return false;
    const x = Math.round(nx * (this.width - 1));
    const y = Math.round(ny * (this.height - 1));
    return this.send(P.scroll(x, y, this.width, this.height, h, v));
  }

  keyPress(code, meta = 0) {
    this.send(P.keycode(0, code, 0, meta));
    return this.send(P.keycode(1, code, 0, meta));
  }

  requestKeyFrame() {
    if (!this.videoOn) return;
    const now = Date.now();
    if (now - this.lastResetAt < 800) return;
    this.lastResetAt = now;
    this.send(P.empty(P.TYPE.RESET_VIDEO));
  }

  _close(reason) {
    if (this.closed) return;
    this.closed = true;
    this.running = false;
    for (const s of [this.video, this.control, this.shellSock]) {
      if (s) { try { s.destroy(); } catch (_) { /* bỏ qua */ } }
    }
    this.emit('close', reason);
  }

  stop() {
    this._close('dừng');
  }
}

module.exports = { ScrcpySession, ensureServerPushed, forgetPushed, SERVER_REMOTE };
