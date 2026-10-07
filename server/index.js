// ControlPhone — máy chủ cục bộ: HTTP (giao diện + API) + WebSocket (video + điều khiển)
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');
const { WebSocketServer } = require('ws');
const adb = require('./adb');
const store = require('./store');
const P = require('./protocol');
const { DeviceManager } = require('./devices');
const { Actions, UPLOAD_DIR, safeName, stamp } = require('./actions');

const PORT = parseInt(process.env.PORT || '8686', 10);
const PUBLIC = path.join(__dirname, '..', 'public');

const manager = new DeviceManager();
const clients = new Set();

function broadcast(msg) {
  const s = JSON.stringify(msg);
  for (const c of clients) if (c.ws.readyState === 1) c.ws.send(s);
}

const actions = new Actions(manager, broadcast);

function devicesMessage() {
  return {
    t: 'devices',
    list: manager.list().map((d) => manager.serialize(d)),
    groups: store.state.groups,
    solo: store.state.solo || null,
  };
}

manager.on('changed', () => broadcast(devicesMessage()));
manager.on('log', (level, msg) => { console.log(`[${level}] ${msg}`); broadcast({ t: 'log', level, msg }); });
manager.on('clipboard', (d, text) => broadcast({ t: 'clipboard', vid: d.vid, text }));
manager.on('size', (d, kind, w, h) => broadcast({ t: 'size', vid: d.vid, kind, w, h }));

// ---------- phát video ----------
const KIND_CODE = { thumb: 0, hq: 1 };
const BACKPRESSURE = 6 * 1024 * 1024;

function frameHeader(kind, vid, isConfig, isKey) {
  const h = Buffer.alloc(4);
  h.writeUInt8(KIND_CODE[kind], 0);
  h.writeUInt16BE(vid, 1);
  h.writeUInt8((isConfig ? 1 : 0) | (isKey ? 2 : 0), 3);
  return h;
}

manager.on('packet', (d, kind, isConfig, isKey, data) => {
  let header = null;
  for (const c of clients) {
    if (c.ws.readyState !== 1) continue;
    const wants = kind === 'thumb' ? c.subs.has(d.vid) : c.hqVid === d.vid;
    if (!wants) continue;
    const k = kind + d.vid;
    if (!isConfig) {
      if (c.needKey.has(k)) {
        if (!isKey) continue;
        c.needKey.delete(k);
      } else if (!isKey && c.ws.bufferedAmount > BACKPRESSURE) {
        // trình duyệt xử lý không kịp: bỏ khung tới keyframe kế tiếp
        c.needKey.add(k);
        const s = kind === 'thumb' ? d.thumb : d.hq;
        if (s) s.requestKeyFrame();
        continue;
      }
    }
    if (!header) header = frameHeader(kind, d.vid, isConfig, isKey);
    c.ws.send(Buffer.concat([header, data]), { binary: true });
  }
});

function primeStream(c, d, kind) {
  const s = kind === 'thumb' ? d.thumb : d.hq;
  c.needKey.add(kind + d.vid);
  if (!s) return;
  if (s.width) c.ws.send(JSON.stringify({ t: 'size', vid: d.vid, kind, w: s.width, h: s.height }));
  if (s.config) c.ws.send(Buffer.concat([frameHeader(kind, d.vid, true, false), s.config]), { binary: true });
  s.requestKeyFrame();
}

// thống kê băng thông video (tổng tất cả máy) mỗi 2 giây
let videoBytes = 0;
manager.on('packet', (d, kind, isConfig, isKey, data) => { videoBytes += data.length + 12; });
setInterval(() => {
  let sessions = 0;
  for (const d of manager.list()) for (const s of [d.thumb, d.hq]) if (s && s.running) sessions++;
  broadcast({ t: 'stats', kbps: Math.round((videoBytes * 8) / 2000), sessions });
  videoBytes = 0;
}, 2000);

manager.on('session-closed', (d, kind) => {
  for (const c of clients) c.needKey.add(kind + d.vid);
});

// Khi phiên mới bắt đầu thì keyframe đầu tiên sẽ tự tới — bỏ cờ chờ khi nhận config mới
manager.on('packet', (d, kind, isConfig) => {
  if (!isConfig) return;
  for (const c of clients) c.needKey.add(kind + d.vid);
});

// ---------- điều khiển ----------
function targets(vids) {
  const out = [];
  if (!Array.isArray(vids)) return out;
  for (const v of vids) {
    const d = manager.byVid(v);
    if (!d) continue;
    const s = manager.controlSession(d);
    if (s) out.push({ d, s });
  }
  return out;
}

const NAMED = {
  home: (s) => s.keyPress(P.KEY.HOME),
  back: (s) => s.keyPress(P.KEY.BACK),
  recents: (s) => s.keyPress(P.KEY.APP_SWITCH),
  menu: (s) => s.keyPress(P.KEY.MENU),
  power: (s) => s.keyPress(P.KEY.POWER),
  volup: (s) => s.keyPress(P.KEY.VOLUME_UP),
  voldown: (s) => s.keyPress(P.KEY.VOLUME_DOWN),
  mute: (s) => s.keyPress(P.KEY.VOLUME_MUTE),
  wake: (s) => s.keyPress(P.KEY.WAKEUP),
  sleep: (s) => s.keyPress(P.KEY.SLEEP),
  enter: (s) => s.keyPress(P.KEY.ENTER),
  del: (s) => s.keyPress(P.KEY.DEL),
  notif: (s) => s.send(P.empty(P.TYPE.EXPAND_NOTIFICATION_PANEL)),
  quick: (s) => s.send(P.empty(P.TYPE.EXPAND_SETTINGS_PANEL)),
  collapse: (s) => s.send(P.empty(P.TYPE.COLLAPSE_PANELS)),
  rotate: (s) => s.send(P.empty(P.TYPE.ROTATE_DEVICE)),
  screenoff: (s) => s.send(P.setDisplayPower(false)),
  screenon: (s) => s.send(P.setDisplayPower(true)),
  copy: (s) => s.send(P.getClipboard(1)),
  getclip: (s) => s.send(P.getClipboard(0)),
  unlock: (s) => {
    s.keyPress(P.KEY.WAKEUP);
    // một lần vuốt lên để mở màn hình khoá (không mật khẩu)
    setTimeout(() => {
      s.touch(0, 0.5, 0.85);
      let i = 1;
      const t = setInterval(() => {
        const y = 0.85 - (0.55 * i) / 8;
        if (i >= 8) { s.touch(1, 0.5, y); clearInterval(t); } else s.touch(2, 0.5, y);
        i++;
      }, 16);
    }, 350);
  },
};

function handleControl(c, m) {
  switch (m.t) {
    case 'sub': {
      const next = new Set((m.v || []).map(Number));
      for (const vid of next) {
        if (!c.subs.has(vid)) {
          const d = manager.byVid(vid);
          c.subs.add(vid);
          if (d) primeStream(c, d, 'thumb');
        }
      }
      for (const vid of [...c.subs]) if (!next.has(vid)) c.subs.delete(vid);
      break;
    }
    case 'hq': {
      const vid = m.v == null ? null : Number(m.v);
      if (vid === c.hqVid) break;
      if (c.hqVid != null) { const old = manager.byVid(c.hqVid); if (old) manager.releaseHq(old); }
      c.hqVid = vid;
      if (vid != null) {
        const d = manager.byVid(vid);
        if (d) { manager.acquireHq(d); primeStream(c, d, 'hq'); }
      }
      break;
    }
    case 'kf': {
      const d = manager.byVid(Number(m.v));
      if (d) primeStream(c, d, m.kind === 'hq' ? 'hq' : 'thumb');
      break;
    }
    case 'touch':
      for (const { s } of targets(m.v)) s.touch(m.a, m.x, m.y, m.p == null ? P.POINTER_ID_GENERIC_FINGER : m.p);
      break;
    case 'scroll':
      for (const { s } of targets(m.v)) s.scroll(m.x, m.y, m.h || 0, m.vs || 0);
      break;
    case 'key':
      for (const { s } of targets(m.v)) {
        if (m.a === 'down') s.send(P.keycode(0, m.k, 0, m.m || 0));
        else if (m.a === 'up') s.send(P.keycode(1, m.k, 0, m.m || 0));
        else s.keyPress(m.k, m.m || 0);
      }
      break;
    case 'text': {
      const str = String(m.s || '');
      if (!str) break;
      const ascii = /^[\x20-\x7e]*$/.test(str);
      for (const { s } of targets(m.v)) {
        if (ascii && m.mode !== 'paste' && str.length <= 300) s.send(P.text(str));
        else s.send(P.setClipboard(str, true));
        if (m.enter) setTimeout(() => s.keyPress(P.KEY.ENTER), 150);
      }
      break;
    }
    case 'clipset':
      for (const { s } of targets(m.v)) s.send(P.setClipboard(String(m.s || ''), false));
      break;
    case 'act': {
      const fn = NAMED[m.n];
      if (fn) for (const { s } of targets(m.v)) fn(s);
      break;
    }
    case 'info': {
      for (const v of m.v || []) { const d = manager.byVid(v); if (d) manager.refreshInfo(d); }
      break;
    }
    default:
      break;
  }
}

// ---------- HTTP ----------
const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.json': 'application/json',
};

function sendJson(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(body);
}

function readBody(req, limit = 2 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let n = 0;
    req.on('data', (d) => { n += d.length; if (n > limit) { reject(new Error('quá lớn')); req.destroy(); } else chunks.push(d); });
    req.on('end', () => {
      const s = Buffer.concat(chunks).toString('utf8');
      try { resolve(s ? JSON.parse(s) : {}); } catch (e) { reject(e); }
    });
    req.on('error', reject);
  });
}

function deviceFrom(id) {
  const d = manager.get(id);
  if (!d || !d.activeSerial || d.pending) throw new Error('Thiết bị không trực tuyến');
  return d;
}

const API = {
  'GET /api/state': () => ({ ...devicesMessage(), settings: store.settings, adb: adb.ADB_PATH, subnets: actions.localSubnets(), wifiHistory: store.state.wifiHistory }),

  'POST /api/settings': (b) => {
    const s = store.settings;
    const before = JSON.stringify([s.thumbMaxSize, s.thumbBitRate, s.thumbFps, s.powerOnConnect, s.screenOffOnConnect]);
    const beforeHq = JSON.stringify([s.hqMaxSize, s.hqBitRate, s.hqFps]);
    const prefBefore = s.preferTransport;
    for (const k of Object.keys(store.DEFAULT_SETTINGS)) {
      if (b[k] === undefined) continue;
      s[k] = typeof store.DEFAULT_SETTINGS[k] === 'number' ? Number(b[k]) : b[k];
    }
    store.save();
    manager.startLimiter.n = s.maxParallelStart;
    if (JSON.stringify([s.thumbMaxSize, s.thumbBitRate, s.thumbFps, s.powerOnConnect, s.screenOffOnConnect]) !== before) manager.restartAll();
    else if (JSON.stringify([s.hqMaxSize, s.hqBitRate, s.hqFps]) !== beforeHq) {
      for (const d of manager.list()) if (d.hq) { const h = d.hq; d.hq = null; h.stop(); manager._ensureHq(d); }
    }
    if (s.preferTransport !== prefBefore) manager.reapplyTransportPreference();
    return { ok: true, settings: s };
  },

  'POST /api/batch': (b) => {
    const task = actions.runBatch(b.op, b.ids || [], b.params || {}, b.title);
    return { ok: true, taskId: task.id, total: task.total };
  },

  'POST /api/label': (b) => {
    const meta = store.deviceMeta(b.id);
    meta.label = String(b.label || '').slice(0, 40);
    store.save();
    manager.changed();
    return { ok: true };
  },

  'POST /api/num': (b) => {
    const meta = store.deviceMeta(b.id);
    const n = parseInt(b.num, 10);
    if (!(n > 0)) throw new Error('Số không hợp lệ');
    meta.num = n;
    store.state.nextNum = Math.max(store.state.nextNum, n + 1);
    store.save();
    manager.changed();
    return { ok: true };
  },

  // Đánh số lại theo thứ tự id gửi lên
  'POST /api/renumber': (b) => {
    let n = 1;
    for (const id of b.ids || []) store.deviceMeta(id).num = n++;
    store.state.nextNum = Math.max(n, ...Object.values(store.state.devices).map((x) => x.num + 1));
    store.save();
    manager.changed();
    return { ok: true };
  },

  'POST /api/group': (b) => {
    const name = String(b.name || '').trim().slice(0, 30);
    const groups = store.state.groups;
    if (b.action === 'create') {
      if (!name) throw new Error('Thiếu tên nhóm');
      if (!groups.includes(name)) groups.push(name);
    } else if (b.action === 'delete') {
      store.state.groups = groups.filter((g) => g !== name);
      for (const m of Object.values(store.state.devices)) m.groups = (m.groups || []).filter((g) => g !== name);
    } else if (b.action === 'add' || b.action === 'remove') {
      if (b.action === 'add' && !groups.includes(name)) groups.push(name);
      for (const id of b.ids || []) {
        const m = store.deviceMeta(id);
        m.groups = (m.groups || []).filter((g) => g !== name);
        if (b.action === 'add') m.groups.push(name);
      }
    }
    store.save();
    manager.changed();
    return { ok: true, groups: store.state.groups };
  },

  'POST /api/connect': (b) => actions.connect(b.addr),
  'POST /api/pair': (b) => actions.pair(b.addr, b.code),
  'POST /api/scan': (b) => actions.scan(b.subnet, b.port || 5555),
  'POST /api/pause': (b) => ({ ok: true, changed: manager.setPaused(b.ids || [], !!b.paused) }),
  'POST /api/solo': (b) => ({ ok: true, solo: manager.setSolo(b.ids || null) }),
  'POST /api/remove-offline': () => { manager.removeOffline(); return { ok: true }; },
  'POST /api/restart': (b) => {
    for (const id of b.ids || []) { const d = manager.get(id); if (d && d.activeSerial && !d.pending) manager.restartSessions(d); }
    return { ok: true };
  },
  'POST /api/adb-restart': async () => {
    await adb.run(['kill-server'], 15000);
    await adb.startServer();
    return { ok: true };
  },
  'POST /api/scrcpy': (b) => {
    const d = deviceFrom(b.id);
    return actions.launchScrcpy(d.activeSerial, ['--max-size', '1280', '--no-audio'], manager.label(d));
  },
  'POST /api/otg': (b) => {
    const serial = b.serial || (b.id ? deviceFrom(b.id).activeSerial : '');
    return actions.launchScrcpy(serial || null, ['--otg']);
  },
  'POST /api/open-folder': (b) => {
    const dir = b.which === 'uploads' ? UPLOAD_DIR : store.settings.screenshotDir;
    fs.mkdirSync(dir, { recursive: true });
    spawn('explorer', [dir], { detached: true, stdio: 'ignore' }).unref();
    return { ok: true };
  },
  'POST /api/files': async (b) => actions.listFiles(deviceFrom(b.id), b.path),
  'POST /api/delete': async (b) => ({ ok: true, msg: await actions.deletePath(deviceFrom(b.id), b.path) }),
  'POST /api/apps': async (b) => ({ ok: true, apps: await actions.listApps(deviceFrom(b.id)) }),
};

async function handleUpload(req, res, url) {
  const name = safeName(url.searchParams.get('name') || 'file');
  const token = crypto.randomBytes(6).toString('hex') + '-' + name;
  fs.mkdirSync(UPLOAD_DIR, { recursive: true });
  const file = path.join(UPLOAD_DIR, token);
  const ws = fs.createWriteStream(file);
  let size = 0;
  req.on('data', (d) => { size += d.length; });
  req.pipe(ws);
  ws.on('finish', () => sendJson(res, 200, { ok: true, token, name: url.searchParams.get('name') || name, size }));
  ws.on('error', (e) => sendJson(res, 500, { ok: false, error: e.message }));
}

async function handlePull(req, res, url) {
  const d = deviceFrom(url.searchParams.get('id'));
  const p = url.searchParams.get('path');
  const name = path.posix.basename(p) || 'file';
  const sock = await adb.execStream(d.activeSerial, `cat ${adb.shellQuote(p)}`);
  res.writeHead(200, {
    'Content-Type': 'application/octet-stream',
    'Content-Disposition': `attachment; filename*=UTF-8''${encodeURIComponent(name)}`,
  });
  sock.pipe(res);
  req.on('close', () => sock.destroy());
}

async function handleShot(req, res, url) {
  const d = deviceFrom(url.searchParams.get('id'));
  const png = await adb.execOut(d.activeSerial, 'screencap -p', 30000);
  const meta = store.deviceMeta(d.id);
  const name = `${String(meta.num).padStart(3, '0')}_${safeName(meta.label || d.info.model)}_${stamp()}.png`;
  res.writeHead(200, {
    'Content-Type': 'image/png',
    'Content-Disposition': `${url.searchParams.get('dl') ? 'attachment' : 'inline'}; filename="${name}"`,
    'Cache-Control': 'no-store',
  });
  res.end(png);
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  // chỉ cho phép truy cập từ máy này
  const ra = req.socket.remoteAddress || '';
  if (!/^(::1|127\.0\.0\.1|::ffff:127\.0\.0\.1)$/.test(ra)) { res.writeHead(403); res.end('forbidden'); return; }
  try {
    if (url.pathname.startsWith('/api/')) {
      if (req.method === 'POST' && url.pathname === '/api/upload') return await handleUpload(req, res, url);
      if (req.method === 'GET' && url.pathname === '/api/pull') return await handlePull(req, res, url);
      if (req.method === 'GET' && url.pathname === '/api/shot') return await handleShot(req, res, url);
      const fn = API[`${req.method} ${url.pathname}`];
      if (!fn) return sendJson(res, 404, { ok: false, error: 'not found' });
      const body = req.method === 'POST' ? await readBody(req) : {};
      const out = await fn(body, url);
      return sendJson(res, 200, out);
    }
    let rel = decodeURIComponent(url.pathname);
    if (rel === '/') rel = '/index.html';
    const file = path.normalize(path.join(PUBLIC, rel));
    if (!file.startsWith(PUBLIC) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      res.writeHead(404); res.end('not found'); return;
    }
    res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' });
    fs.createReadStream(file).pipe(res);
  } catch (e) {
    if (!res.headersSent) sendJson(res, 500, { ok: false, error: e.message });
    else res.end();
  }
});

const wss = new WebSocketServer({ server, path: '/ws', perMessageDeflate: false });
wss.on('error', () => {}); // lỗi cổng đã được xử lý ở server.on('error')
wss.on('connection', (ws, req) => {
  const ra = req.socket.remoteAddress || '';
  if (!/^(::1|127\.0\.0\.1|::ffff:127\.0\.0\.1)$/.test(ra)) { ws.close(); return; }
  const c = { ws, subs: new Set(), hqVid: null, needKey: new Set() };
  clients.add(c);
  ws.send(JSON.stringify(devicesMessage()));
  ws.on('message', (data, isBinary) => {
    if (isBinary) return;
    try { handleControl(c, JSON.parse(data.toString())); } catch (e) { /* bỏ qua tin hỏng */ }
  });
  ws.on('close', () => {
    clients.delete(c);
    if (c.hqVid != null) { const d = manager.byVid(c.hqVid); if (d) manager.releaseHq(d); }
  });
});

function openAppWindow() {
  if (process.env.CP_NO_BROWSER) return; // chạy không mở trình duyệt (kiểm thử / chạy nền)
  const url = `http://127.0.0.1:${PORT}`;
  const pf = process.env['ProgramFiles'] || 'C:\\Program Files';
  const pf86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
  const local = process.env.LOCALAPPDATA || '';
  const candidates = [
    path.join(pf86, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(pf, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(pf, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    path.join(pf86, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    path.join(local, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  ];
  const exe = candidates.find((p) => p && fs.existsSync(p));
  if (!exe) {
    spawn('cmd', ['/c', 'start', '', url], { detached: true, stdio: 'ignore' }).unref();
    return;
  }
  const profile = path.join(store.DATA_DIR, 'browser-profile');
  spawn(exe, [
    `--app=${url}`, `--user-data-dir=${profile}`, '--start-maximized', '--no-first-run', '--no-default-browser-check',
    '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows',
  ], { detached: true, stdio: 'ignore' }).unref();
}

server.on('error', (e) => {
  if (e.code !== 'EADDRINUSE') throw e;
  // Cổng đã bị chiếm: nếu đó là ControlPhone đang chạy sẵn thì chỉ mở lại cửa sổ giao diện
  http.get({ host: '127.0.0.1', port: PORT, path: '/api/state', timeout: 3000 }, (res) => {
    res.resume();
    if (res.statusCode === 200) {
      console.log('ControlPhone đã đang chạy sẵn — mở cửa sổ giao diện.');
      openAppWindow();
      setTimeout(() => process.exit(0), 1500);
    } else {
      notOurs();
    }
  }).on('error', notOurs).on('timeout', notOurs);
  function notOurs() {
    console.log(`Cổng ${PORT} đang bị chương trình khác dùng. Hãy tắt chương trình đó, hoặc chạy với cổng khác: set PORT=8687 rồi chạy lại.`);
    setTimeout(() => process.exit(2), 500);
  }
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`ControlPhone đang chạy: http://127.0.0.1:${PORT}`);
  console.log(`ADB: ${adb.ADB_PATH}`);
  manager.start();
  if (process.argv.includes('--open')) openAppWindow();
});

process.on('uncaughtException', (e) => console.error('[lỗi]', e));
process.on('unhandledRejection', (e) => console.error('[lỗi]', e));
