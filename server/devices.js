// Quản lý danh sách thiết bị: gộp USB/WiFi theo serial phần cứng, giữ phiên scrcpy, đọc thông tin pin/IP.
'use strict';
const EventEmitter = require('events');
const adb = require('./adb');
const store = require('./store');
const { ScrcpySession, forgetPushed } = require('./scrcpy');
const P = require('./protocol');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class Limiter {
  constructor(n) { this.n = n; this.active = 0; this.q = []; }
  run(fn) {
    return new Promise((resolve, reject) => {
      this.q.push({ fn, resolve, reject });
      this._next();
    });
  }
  _next() {
    while (this.active < this.n && this.q.length) {
      const { fn, resolve, reject } = this.q.shift();
      this.active++;
      Promise.resolve().then(fn).then(resolve, reject).finally(() => { this.active--; this._next(); });
    }
  }
}

let vidCounter = 1;

class Device {
  constructor(id) {
    this.id = id;
    this.vid = vidCounter++;
    this.transports = new Map(); // serial -> 'usb' | 'wifi'
    this.activeSerial = null;
    this.info = {};
    this.status = 'offline';
    this.error = '';
    this.thumb = null;
    this.hq = null;
    this.hqRefs = 0;
    this.restartDelay = 1000;
    this.restartTimer = null;
    this.pending = false; // placeholder cho máy unauthorized/offline
  }
}

class DeviceManager extends EventEmitter {
  constructor() {
    super();
    this.devices = new Map(); // id -> Device
    this.serialToId = new Map(); // serial -> hwId
    this.serialStates = new Map(); // serial -> state
    this.probing = new Set();
    this.startLimiter = new Limiter(store.settings.maxParallelStart || 6);
    this.infoLimiter = new Limiter(8);
    this.changedTimer = null;
  }

  start() {
    adb.startServer().then(() => {
      this.stopTrack = adb.trackDevices((list) => this._onTrack(list), (e) => this.emit('log', 'warn', 'adb: ' + e.message));
    });
    this.infoTimer = setInterval(() => this.refreshAllInfo(), 20000);
    this.wifiTimer = setInterval(() => this._autoReconnectWifi(), 20000);
  }

  changed() {
    if (this.changedTimer) return;
    this.changedTimer = setTimeout(() => { this.changedTimer = null; this.emit('changed'); }, 200);
  }

  byVid(vid) {
    for (const d of this.devices.values()) if (d.vid === vid) return d;
    return null;
  }

  get(id) { return this.devices.get(id); }

  list() {
    return [...this.devices.values()];
  }

  serialize(d) {
    const meta = d.pending ? { label: '', num: 0, groups: [] } : store.deviceMeta(d.id);
    return {
      id: d.id,
      vid: d.vid,
      num: meta.num,
      label: meta.label || '',
      groups: meta.groups || [],
      model: d.info.model || '',
      manufacturer: d.info.manufacturer || '',
      android: d.info.android || '',
      sdk: d.info.sdk || 0,
      root: !!d.info.root,
      battery: d.info.battery ?? null,
      charging: !!d.info.charging,
      temp: d.info.temp ?? null,
      ip: d.info.ip || '',
      screenOn: d.info.screenOn ?? null,
      status: d.status,
      error: d.error,
      serial: d.activeSerial || [...d.transports.keys()][0] || '',
      transport: d.activeSerial ? d.transports.get(d.activeSerial) : null,
      transports: [...d.transports.entries()].map(([serial, type]) => ({ serial, type })),
      w: d.thumb ? d.thumb.width : 0,
      h: d.thumb ? d.thumb.height : 0,
      pending: d.pending,
      paused: this.isPaused(d),
      manualPaused: !!meta.paused,
      soloHidden: this.isSoloHidden(d),
    };
  }

  // ---------- theo dõi adb ----------
  _onTrack(list) {
    // khoá theo địa chỉ kết nối (serial, hoặc serial@t<id> khi nhiều máy trùng serial)
    const seen = new Map(list.map((x) => [x.addr || x.serial, x.state]));
    this.dupSerialCount = list.length - new Set(list.map((x) => x.serial)).size;
    // serial biến mất hoặc không còn ở trạng thái device
    for (const [serial, id] of [...this.serialToId.entries()]) {
      if (seen.get(serial) !== 'device') {
        this.serialToId.delete(serial);
        forgetPushed(serial);
        const d = this.devices.get(id);
        if (d) this._detach(d, serial);
      }
    }
    // placeholder cho các máy chưa authorize / offline
    for (const [id, d] of [...this.devices.entries()]) {
      if (d.pending && seen.get(d.activeSerial) !== d.status) {
        this.devices.delete(id);
        this.changed();
      }
    }
    for (const { addr, serial: rawSerial, state } of list) {
      const serial = addr || rawSerial;
      if (state === 'device') {
        if (!this.serialToId.has(serial) && !this.probing.has(serial)) this._probe(serial);
      } else {
        const pid = 'pending:' + serial;
        if (!this.devices.has(pid)) {
          const d = new Device(pid);
          d.pending = true;
          d.activeSerial = serial;
          d.transports.set(serial, adb.isWifiSerial(serial) ? 'wifi' : 'usb');
          d.info.model = serial;
          this.devices.set(pid, d);
        }
        const d = this.devices.get(pid);
        d.status = state; // unauthorized | offline | authorizing | connecting
        d.error = state === 'unauthorized' ? 'Hãy bấm "Cho phép gỡ lỗi USB" trên điện thoại' : 'Thiết bị đang ' + state;
        this.changed();
      }
    }
    this.serialStates = seen;
  }

  async _probe(serial) {
    this.probing.add(serial);
    try {
      let out = '';
      for (let i = 0; i < 3; i++) {
        try {
          out = await adb.shell(serial, [
            'getprop ro.serialno', 'getprop ro.product.model', 'getprop ro.product.manufacturer',
            'getprop ro.build.version.release', 'getprop ro.build.version.sdk',
            'echo "SU=$(which su 2>/dev/null)"', 'echo "UID=$(id -u)"', 'echo "AID=$(settings get secure android_id 2>/dev/null)"',
          ].join('; '), 10000);
          break;
        } catch (e) {
          await sleep(1000);
        }
      }
      if (!out) throw new Error('không đọc được thông tin');
      if (this.serialStates.get(serial) !== 'device') return;
      const lines = out.split('\n').map((s) => s.trim());
      const aidM = out.match(/AID=([0-9a-f]{6,})/i);
      const androidId = aidM ? aidM[1].toLowerCase() : '';
      const type = adb.isWifiSerial(serial) ? 'wifi' : 'usb';
      const info = {
        model: lines[1], manufacturer: lines[2], android: lines[3], sdk: parseInt(lines[4], 10) || 0,
        root: /SU=\S+/.test(out) || /UID=0/.test(out), androidId,
      };
      const hwId = this._resolveId(lines[0], androidId, type, serial);
      this.devices.delete('pending:' + serial);
      let d = this.devices.get(hwId);
      if (!d) {
        d = new Device(hwId);
        this.devices.set(hwId, d);
      }
      Object.assign(d.info, info);
      d.transports.set(serial, type);
      this.serialToId.set(serial, hwId);
      const meta = store.deviceMeta(hwId);
      if (androidId && meta.androidId !== androidId) { meta.androidId = androidId; store.save(); }
      if (type === 'wifi') this._rememberWifi(hwId, serial);
      this._chooseTransport(d);
      this.refreshInfo(d);
      this.changed();
    } catch (e) {
      this.emit('log', 'warn', `Không nhận diện được ${serial}: ${e.message}`);
    } finally {
      this.probing.delete(serial);
    }
  }

  /**
   * Định danh ổn định cho một máy. Thường là ro.serialno (để gộp USB + WiFi của cùng một máy).
   * Nhiều máy chạy ROM nhân bản có CÙNG serial → phân biệt thêm bằng Android ID ("serial#androidId").
   */
  _resolveId(serialno, aid, type, addr) {
    const dup = store.state.dupSerials || (store.state.dupSerials = []);
    serialno = (serialno || '').trim();
    if (!serialno || serialno === 'unknown') return aid ? 'aid:' + aid : addr;
    const keyed = (a) => `${serialno}#${(a || addr).slice(0, 12)}`;
    if (dup.includes(serialno)) return keyed(aid);
    const live = this.devices.get(serialno);
    const meta = store.state.devices[serialno];
    const otherAid = (live && live.info.androidId) || (meta && meta.androidId) || '';
    if (aid && otherAid && otherAid !== aid) {
      // 2 máy khác nhau trùng serial → từ nay mỗi máy dùng id riêng (giữ tên/số của máy cũ)
      dup.push(serialno);
      store.save();
      this._rekey(serialno, keyed(otherAid));
      this.emit('log', 'warn', `Phát hiện nhiều máy trùng serial ${serialno} — đã tách riêng từng máy`);
      return keyed(aid);
    }
    // ROM nhân bản hoàn toàn (trùng cả Android ID) mà đang có 2 kết nối USB cùng lúc → vẫn tách ra
    if (live && type === 'usb' && [...live.transports.values()].includes('usb')) return `${serialno}~${addr}`;
    return serialno;
  }

  _rekey(oldId, newId) {
    const all = store.state.devices;
    if (all[oldId]) { if (!all[newId]) all[newId] = all[oldId]; delete all[oldId]; }
    if (store.state.solo) store.state.solo = store.state.solo.map((x) => (x === oldId ? newId : x));
    const d = this.devices.get(oldId);
    if (d) {
      this.devices.delete(oldId);
      d.id = newId;
      this.devices.set(newId, d);
      for (const s of d.transports.keys()) this.serialToId.set(s, newId);
    }
    store.save();
    this.changed();
  }

  _rememberWifi(hwId, serial) {
    const meta = store.deviceMeta(hwId);
    meta.wifi = serial;
    const hist = store.state.wifiHistory;
    if (!hist.includes(serial)) hist.push(serial);
    store.save();
  }

  _detach(d, serial) {
    d.transports.delete(serial);
    if (d.activeSerial === serial) {
      d.activeSerial = null;
      this._stopSessions(d);
    }
    this._chooseTransport(d);
    this.changed();
  }

  _chooseTransport(d) {
    const prefer = store.settings.preferTransport === 'wifi' ? 'wifi' : 'usb';
    let best = null;
    for (const [serial, type] of d.transports) {
      if (!best || (type === prefer && d.transports.get(best) !== prefer)) best = serial;
    }
    if (best === d.activeSerial) {
      if (!best) { d.status = 'offline'; d.error = 'Mất kết nối'; }
      return;
    }
    if (d.activeSerial) this._stopSessions(d);
    d.activeSerial = best;
    if (!best) {
      d.status = 'offline';
      d.error = 'Mất kết nối';
      return;
    }
    d.restartDelay = 1000;
    this._ensureThumb(d);
    if (d.hqRefs > 0) this._ensureHq(d);
  }

  reapplyTransportPreference() {
    for (const d of this.devices.values()) if (!d.pending) this._chooseTransport(d);
  }

  // ---------- phiên scrcpy ----------
  _sessionOpts(d, kind) {
    const s = store.settings;
    return kind === 'hq'
      ? { serial: d.activeSerial, kind, maxSize: s.hqMaxSize, bitRate: s.hqBitRate, maxFps: s.hqFps, powerOn: s.powerOnConnect }
      // tắt màn hình thật khi kết nối: giữ máy thức (stay_awake) để vẫn xem/điều khiển được
      : { serial: d.activeSerial, kind, maxSize: s.thumbMaxSize, bitRate: s.thumbBitRate, maxFps: s.thumbFps, powerOn: s.powerOnConnect, stayAwake: !!s.screenOffOnConnect,
        // máy đang "tạm dừng xem": chỉ giữ phiên điều khiển, không truyền hình
        video: !this.isPaused(d) };
  }

  /** Tạm dừng xem = tạm dừng thủ công, hoặc bị ẩn do chế độ "Chỉ hiển thị các máy này". */
  isPaused(d) {
    if (d.pending) return false;
    return !!store.deviceMeta(d.id).paused || this.isSoloHidden(d);
  }

  isSoloHidden(d) {
    const solo = store.state.solo;
    return !!(solo && solo.length && !d.pending && !solo.includes(d.id));
  }

  /** Chế độ chỉ hiển thị: ids = danh sách máy được hiển thị; null = trở lại bình thường. */
  setSolo(ids) {
    const before = new Map(this.list().map((d) => [d.id, this.isPaused(d)]));
    store.state.solo = ids && ids.length ? [...new Set(ids)] : null;
    store.save();
    for (const d of this.list()) {
      if (!d.pending && d.activeSerial && before.get(d.id) !== this.isPaused(d)) this.restartSessions(d);
    }
    this.changed();
    return store.state.solo;
  }

  /** Tạm dừng / tiếp tục truyền hình của các máy (vẫn điều khiển & đồng bộ được khi tạm dừng). */
  setPaused(ids, paused) {
    let n = 0;
    for (const id of ids) {
      const d = this.devices.get(id);
      if (!d || d.pending) continue;
      const meta = store.deviceMeta(id);
      if (!!meta.paused === !!paused) continue;
      meta.paused = !!paused;
      n++;
      if (d.activeSerial) this.restartSessions(d);
    }
    store.save();
    this.changed();
    return n;
  }

  _ensureThumb(d) {
    if (d.thumb || !d.activeSerial || d.pending) return;
    if (d.restartTimer) { clearTimeout(d.restartTimer); d.restartTimer = null; }
    const sess = new ScrcpySession(this._sessionOpts(d, 'thumb'));
    d.thumb = sess;
    d.status = 'connecting';
    d.error = '';
    this.changed();
    this._wire(d, sess, 'thumb');
    this.startLimiter.run(() => (sess.closed ? null : sess.start())).then(() => {
      if (d.thumb !== sess) return;
      d.status = 'online';
      d.error = '';
      d.restartDelay = 1000;
      this.changed();
      // mặc định tắt màn hình điện thoại (vẫn truyền hình về máy tính); khi thoát app màn hình tự bật lại
      if (store.settings.screenOffOnConnect) setTimeout(() => sess.send(P.setDisplayPower(false)), 600);
    }).catch((e) => {
      if (d.thumb === sess) { d.error = e.message; this.changed(); }
      forgetPushed(sess.serial); // lần thử lại sẽ kiểm tra/push lại server
      sess.stop();
    });
  }

  _ensureHq(d) {
    if (d.hq || !d.activeSerial || d.pending) return;
    const sess = new ScrcpySession(this._sessionOpts(d, 'hq'));
    d.hq = sess;
    this._wire(d, sess, 'hq');
    sess.start().then(() => { d.hqFails = 0; }).catch((e) => {
      d.hqFails = (d.hqFails || 0) + 1;
      this.emit('log', 'warn', `Không mở được luồng nét (${this.label(d)}): ${e.message}`);
      forgetPushed(sess.serial);
      sess.stop();
    });
  }

  _wire(d, sess, kind) {
    sess.on('packet', (isConfig, isKey, data) => this.emit('packet', d, kind, isConfig, isKey, data));
    sess.on('size', (w, h) => { this.emit('size', d, kind, w, h); if (kind === 'thumb') this.changed(); });
    sess.on('clipboard', (text) => this.emit('clipboard', d, text));
    sess.on('close', (reason) => {
      if (kind === 'thumb' && d.thumb === sess) {
        d.thumb = null;
        if (d.activeSerial && this.serialToId.has(d.activeSerial)) {
          d.status = 'connecting';
          if (!d.error) d.error = 'Đang kết nối lại (' + reason + ')';
          const delay = d.restartDelay;
          d.restartDelay = Math.min(d.restartDelay * 2, 15000);
          d.restartTimer = setTimeout(() => { d.restartTimer = null; this._ensureThumb(d); }, delay);
        }
        this.changed();
      } else if (kind === 'hq' && d.hq === sess) {
        d.hq = null;
        if (d.hqRefs > 0 && (d.hqFails || 0) < 3 && d.activeSerial && this.serialToId.has(d.activeSerial)) {
          setTimeout(() => { if (d.hqRefs > 0) this._ensureHq(d); }, 1500);
        }
      }
      this.emit('session-closed', d, kind);
    });
  }

  _stopSessions(d) {
    if (d.restartTimer) { clearTimeout(d.restartTimer); d.restartTimer = null; }
    const t = d.thumb; const h = d.hq;
    d.thumb = null; d.hq = null;
    if (t) t.stop();
    if (h) h.stop();
  }

  restartSessions(d) {
    this._stopSessions(d);
    d.restartDelay = 1000;
    this._ensureThumb(d);
    if (d.hqRefs > 0) this._ensureHq(d);
  }

  restartAll() {
    for (const d of this.devices.values()) if (!d.pending && d.activeSerial) this.restartSessions(d);
  }

  acquireHq(d) {
    d.hqRefs++;
    d.hqFails = 0;
    this._ensureHq(d);
  }

  releaseHq(d) {
    d.hqRefs = Math.max(0, d.hqRefs - 1);
    if (d.hqRefs === 0 && d.hq) {
      const h = d.hq;
      d.hq = null;
      h.stop();
    }
  }

  /** Phiên dùng để gửi điều khiển: ưu tiên luồng thumb (luôn chạy). */
  controlSession(d) {
    if (d.thumb && d.thumb.running) return d.thumb;
    if (d.hq && d.hq.running) return d.hq;
    return null;
  }

  removeOffline() {
    for (const [id, d] of [...this.devices.entries()]) {
      if (!d.activeSerial && !d.pending) this.devices.delete(id);
    }
    this.changed();
  }

  label(d) {
    const meta = store.deviceMeta(d.id);
    return `#${meta.num} ${meta.label || d.info.model || d.id}`;
  }

  // ---------- thông tin pin / IP / màn hình ----------
  refreshAllInfo() {
    for (const d of this.devices.values()) if (d.activeSerial && !d.pending) this.refreshInfo(d);
  }

  refreshInfo(d) {
    if (d.infoBusy) return;
    d.infoBusy = true;
    this.infoLimiter.run(async () => {
      const serial = d.activeSerial;
      if (!serial) return;
      const out = await adb.shell(serial, [
        "dumpsys battery | grep -E '^  (level|AC powered|USB powered|Wireless powered|temperature):'",
        'ip -f inet addr show wlan0 2>/dev/null | grep -m1 inet',
        "dumpsys power | grep -m1 -E 'mWakefulness='",
      ].join('; '), 10000);
      const m = (re) => { const r = out.match(re); return r ? r[1] : null; };
      const level = m(/level:\s*(\d+)/);
      const temp = m(/temperature:\s*(-?\d+)/);
      d.info.battery = level != null ? parseInt(level, 10) : null;
      d.info.temp = temp != null && parseInt(temp, 10) > 0 ? parseInt(temp, 10) / 10 : null;
      d.info.charging = /(AC|USB|Wireless) powered:\s*true/.test(out);
      d.info.ip = m(/inet\s+(\d+\.\d+\.\d+\.\d+)/) || '';
      const wake = m(/mWakefulness=(\w+)/);
      d.info.screenOn = wake ? wake === 'Awake' : null;
      this.changed();
    }).catch(() => {}).finally(() => { d.infoBusy = false; });
  }

  // ---------- WiFi ----------
  async _autoReconnectWifi() {
    if (!store.settings.autoReconnectWifi) return;
    const connected = new Set(this.serialStates.keys());
    const targets = [];
    for (const [id, meta] of Object.entries(store.state.devices)) {
      if (!meta.wifi || connected.has(meta.wifi)) continue;
      const d = this.devices.get(id);
      // chỉ tự nối lại khi máy đang không có kết nối nào (tránh làm phiền khi đã có USB)
      if (d && d.activeSerial) continue;
      targets.push(meta.wifi);
    }
    const lim = new Limiter(8);
    await Promise.all(targets.map((t) => lim.run(() => adb.run(['connect', t], 6000))));
  }
}

module.exports = { DeviceManager, Limiter };
