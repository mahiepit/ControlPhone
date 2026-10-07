// ControlPhone — giao diện quản lý & điều khiển hàng loạt
import { icon } from './icons.js';
import { StreamDecoder, webCodecsSupported } from './decoder.js';
import { esc, h, hydrateIcons, toast, modal, modalOpen, confirmBox, promptBox, ctxMenu, closeCtx, api, fmtSize, uploadFile } from './ui.js';
import { androidKeycode, metaOf } from './keymap.js';
import { initI18n, t as tr, LANGS, currentLang, setLang } from './i18n.js';
import { DONATE } from './donate.js';

await initI18n(); // nạp ngôn ngữ trước khi dựng giao diện

const $ = (s, r = document) => r.querySelector(s);
const TEST_MODE = new URLSearchParams(location.search).has('test'); // bỏ qua tạm dừng khi cửa sổ ẩn
const $$ = (s, r = document) => [...r.querySelectorAll(s)];

function lsGet(k, def) { try { const v = localStorage.getItem('cp.' + k); return v == null ? def : JSON.parse(v); } catch (_) { return def; } }
function lsSet(k, v) { try { localStorage.setItem('cp.' + k, JSON.stringify(v)); } catch (_) { /* bỏ qua */ } }

const S = {
  devices: new Map(), // id -> thiết bị
  byVid: new Map(),
  groups: [],
  settings: {},
  selected: new Set(lsGet('selected', [])),
  filter: lsGet('filter', 'all'),
  search: '',
  syncOff: lsGet('syncOff', false), // tắt tạm đồng bộ tự động
  live: true,
  sort: lsGet('sort', 'num'),
  tileW: lsGet('tileW', 180),
  viewerId: null,
  visible: new Set(),
  lastClickedId: null,
  tiles: new Map(), // id -> tile
  hoverVid: null,
  kbdVid: null,
  ws: null,
  subsSent: '',
  tasks: new Map(),
  // đo thực tế: giải mã CPU chạy đủ 100 luồng x 15fps, GPU bị nghẽn khi nhiều luồng nhỏ
  decoderHw: lsGet('decoderHw2', 'prefer-software'),
  order: [],
};

// ====================================================================
// WebSocket
// ====================================================================
function connectWs() {
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.binaryType = 'arraybuffer';
  S.ws = ws;
  ws.onopen = () => { S.subsSent = ''; updateSubs(true); if (S.viewerId) sendWs({ t: 'hq', v: S.devices.get(S.viewerId)?.vid }); };
  ws.onclose = () => {
    S.ws = null;
    for (const t of S.tiles.values()) t.decoder && t.decoder.resume();
    setTimeout(connectWs, 1000);
  };
  ws.onmessage = (e) => {
    if (typeof e.data !== 'string') return onBinary(e.data);
    const m = JSON.parse(e.data);
    if (m.t === 'devices') onDevices(m);
    else if (m.t === 'size') onSize(m);
    else if (m.t === 'task') onTask(m.task);
    else if (m.t === 'log') toast(m.msg, m.level === 'warn' ? 'err' : '');
    else if (m.t === 'clipboard') onClipboard(m);
    else if (m.t === 'stats') $('#statBw').textContent = m.kbps >= 1000 ? (m.kbps / 1000).toFixed(1) + ' Mbps' : m.kbps + ' kbps';
  };
}

function sendWs(m) {
  if (S.ws && S.ws.readyState === 1) S.ws.send(JSON.stringify(m));
}

function onBinary(buf) {
  const dv = new DataView(buf);
  const kind = dv.getUint8(0);
  const vid = dv.getUint16(1);
  const flags = dv.getUint8(3);
  const data = new Uint8Array(buf, 4);
  if (kind === 0) {
    const d = S.byVid.get(vid);
    const t = d && S.tiles.get(d.id);
    if (t) ensureDecoder(t).feed(flags, data);
  } else if (kind === 1 && viewer.vid === vid && viewer.decoder) {
    viewer.decoder.feed(flags, data);
  }
}

function onSize(m) {
  const d = S.byVid.get(m.vid);
  if (!d) return;
  if (m.kind === 'thumb') { d.w = m.w; d.h = m.h; const t = S.tiles.get(d.id); if (t) setAspect(t, d); }
  if (m.kind === 'hq' && viewer.vid === m.vid) { viewer.w = m.w; viewer.h = m.h; layoutViewer(); }
}

let clipWaiter = null;
function onClipboard(m) {
  if (clipWaiter) { clipWaiter(m.text); clipWaiter = null; return; }
  navigator.clipboard.writeText(m.text).then(() => toast('Đã sao chép clipboard từ điện thoại', 'ok')).catch(() => {});
}

// ====================================================================
// Danh sách thiết bị
// ====================================================================
function onDevices(m) {
  const seen = new Set();
  for (const d of m.list) {
    seen.add(d.id);
    const old = S.devices.get(d.id);
    if (old && old.vid !== d.vid) S.byVid.delete(old.vid);
    S.devices.set(d.id, d);
    S.byVid.set(d.vid, d);
  }
  for (const [id, d] of [...S.devices]) {
    if (!seen.has(id)) {
      S.devices.delete(id);
      S.byVid.delete(d.vid);
      const t = S.tiles.get(id);
      if (t) { observer.unobserve(t.el); if (t.decoder) t.decoder.close(); t.el.remove(); S.tiles.delete(id); }
      S.visible.delete(d.vid);
    }
  }
  S.groups = m.groups || [];
  const soloChanged = JSON.stringify(S.solo || null) !== JSON.stringify(m.solo || null);
  S.solo = m.solo || null;
  for (const id of [...S.selected]) {
    if (!S.devices.has(id) && S.devices.size) S.selected.delete(id);
    else if (S.devices.get(id)?.soloHidden) S.selected.delete(id); // máy bị ẩn không còn nhận thao tác đồng bộ
  }
  renderAll();
  updateSoloUi();
  if (soloChanged) onSoloChanged();
  updateSubs();
  if (S.viewerId) {
    if (!S.devices.has(S.viewerId)) closeViewer(); else updateViewerHead();
  }
}

const isOnline = (d) => d && d.status === 'online';

function displayName(d) {
  return d.label || d.model || d.serial;
}

function sortedDevices() {
  const arr = [...S.devices.values()];
  const by = S.sort;
  const statusRank = (d) => (d.status === 'online' ? 0 : d.status === 'connecting' ? 1 : 2);
  arr.sort((a, b) => {
    if (a.pending !== b.pending) return a.pending ? 1 : -1;
    if (by === 'name') return displayName(a).localeCompare(displayName(b), 'vi', { numeric: true }) || a.num - b.num;
    if (by === 'status') return statusRank(a) - statusRank(b) || a.num - b.num;
    if (by === 'battery') return (a.battery ?? 999) - (b.battery ?? 999) || a.num - b.num;
    return a.num - b.num;
  });
  return arr;
}

function matchFilter(d) {
  if (d.soloHidden) return false; // chế độ "Chỉ hiển thị các máy này"
  const f = S.filter;
  if (f === 'sel' && !S.selected.has(d.id)) return false;
  if (f === 'usb' && d.transport !== 'usb') return false;
  if (f === 'wifi' && d.transport !== 'wifi') return false;
  if (f === 'bad' && d.status === 'online') return false;
  if (f === 'paused' && !d.paused) return false;
  if (f.startsWith('group:') && !d.groups.includes(f.slice(6))) return false;
  if (S.search) {
    const q = S.search.toLowerCase();
    const hay = [String(d.num), d.label, d.model, d.serial, d.ip, d.id, ...d.transports.map((x) => x.serial)].join(' ').toLowerCase();
    if (!hay.includes(q)) return false;
  }
  return true;
}

function renderAll() {
  renderTiles();
  renderSidebar();
  renderStats();
}

function renderStats() {
  const all = [...S.devices.values()].filter((d) => !d.pending);
  $('#statOnline').textContent = all.filter(isOnline).length;
  $('#statTotal').textContent = all.length;
  $('#statSel').textContent = S.selected.size;
  $('#abCount').textContent = S.selected.size;
  updateSyncUi();
  lsSet('selected', [...S.selected]);
  updateViewerHead();
}

// ---------------- ô màn hình ----------------
function createTile(d) {
  const el = h(`<div class="tile" data-id="${esc(d.id)}">
    <div class="tile-head"><span class="num"></span><span class="name" data-no-i18n></span><span class="badges"></span></div>
    <div class="screen"><canvas width="9" height="18"></canvas><div class="ov"></div></div>
  </div>`);
  const t = { id: d.id, el, canvas: $('canvas', el), ov: $('.ov', el), head: $('.tile-head', el), decoder: null, sig: '' };
  S.tiles.set(d.id, t);
  observer.observe(el);
  return t;
}

function ensureDecoder(t) {
  if (!t.decoder) {
    t.decoder = new StreamDecoder(t.canvas, {
      hw: S.decoderHw,
      onNeedKey: () => { const d = S.devices.get(t.id); if (d) sendWs({ t: 'kf', v: d.vid, kind: 'thumb' }); },
      onFirstFrame: () => updateTileOverlay(t, S.devices.get(t.id)),
    });
  }
  return t.decoder;
}

function setAspect(t, d) {
  if (d.w && d.h) t.el.querySelector('.screen').style.aspectRatio = `${d.w} / ${d.h}`;
}

function batteryHtml(d) {
  if (d.battery == null) return '';
  const cls = d.charging ? 'chg' : d.battery <= 20 ? 'low' : '';
  return `<span class="batt ${cls}" title="${tr('Pin')}${d.charging ? ' ' + tr('(đang sạc)') : ''}${d.temp ? ' · ' + d.temp + '°C' : ''}">${d.battery}%</span>`;
}

function updateTile(t, d) {
  const sig = [d.num, d.label, d.model, d.transport, d.root, d.battery, d.charging, d.status, d.error, d.w, d.h, d.transports.length, d.paused, d.zoomed].join('|');
  if (sig === t.sig) return;
  t.sig = sig;
  $('.num', t.el).textContent = d.pending ? '?' : String(d.num).padStart(2, '0');
  $('.name', t.el).textContent = displayName(d);
  t.head.title = `${displayName(d)}\n${d.model} · Android ${d.android}\n${d.transports.map((x) => x.type.toUpperCase() + ': ' + x.serial).join('\n')}${d.ip ? '\nIP: ' + d.ip : ''}\n\n${tr('Click: chọn máy này · Ctrl+click: thêm/bớt vào nhóm · Nhấp đúp: mở lớn · Chuột phải: menu')}`;
  const badges = [];
  // icon kết nối: đậm = đang dùng, mờ = kết nối dự phòng
  for (const type of ['usb', 'wifi']) {
    if (!d.transports.some((x) => x.type === type)) continue;
    const on = d.transport === type;
    badges.push(`<span class="tico ${type} ${on ? '' : 'dim'}" title="${type === 'usb' ? 'USB' : 'WiFi'} ${tr(on ? '(đang dùng)' : '(dự phòng)')}">${icon(type)}</span>`);
  }
  if (d.root) badges.push('<span class="badge root" title="Có root">R</span>');
  $('.badges', t.el).innerHTML = badges.join('') + batteryHtml(d);
  setAspect(t, d);
  updateTileOverlay(t, d);
}

function updateTileOverlay(t, d) {
  if (!d) return;
  let html = '';
  let err = false;
  if (d.pending) {
    html = `${icon('shield')}<b>${esc(d.status)}</b><span>${esc(d.error)}</span>`;
    err = true;
  } else if (d.status === 'offline') {
    html = `${icon('plug')}<b>Mất kết nối</b>`;
    err = true;
  } else if (d.zoomed && d.status === 'online') {
    // đang phóng to: ô lưới không truyền hình (màn hình lớn có luồng nét riêng)
    html = `${icon('maximize')}<b>Đang xem ở màn hình lớn</b>`;
  } else if (d.paused && d.status === 'online') {
    // tạm dừng xem riêng máy này: không truyền hình nhưng vẫn điều khiển/đồng bộ được
    html = `${icon('pause')}<b>Tạm dừng xem</b><span>Vẫn điều khiển được</span>`;
  } else if (d.status === 'connecting' || !t.decoder || !t.decoder.hasFrame) {
    html = `<div class="spin"></div><span>${esc(d.error || 'Đang kết nối…')}</span>`;
    if (d.status === 'online' && !d.error) html = '<div class="spin"></div>';
  }
  if (!S.live && !html) html = `${icon('pause')}`;
  t.ov.innerHTML = html;
  t.ov.classList.toggle('hidden', !html);
  t.ov.classList.toggle('err', err);
  t.ov.classList.toggle('soft', (!!d.paused || !!d.zoomed) && !d.pending && d.status === 'online');
  t.el.classList.toggle('paused', !S.live || !!d.paused);
}

function renderTiles() {
  const grid = $('#grid');
  const list = sortedDevices();
  let shown = 0;
  const order = [];
  for (const d of list) {
    let t = S.tiles.get(d.id);
    if (!t) t = createTile(d);
    updateTile(t, d);
    const show = matchFilter(d);
    t.el.classList.toggle('hidden', !show);
    t.el.classList.toggle('sel', S.selected.has(d.id));
    t.el.classList.toggle('active', S.viewerId === d.id);
    if (show) shown++;
    order.push(t.el);
  }
  // sắp xếp lại DOM khi thứ tự thay đổi (canvas giữ nguyên nội dung)
  const sig = order.map((e) => e.dataset.id).join(',');
  if (sig !== S.order) {
    S.order = sig;
    for (const el of order) grid.appendChild(el);
  }
  const empty = $('#empty');
  if (!S.devices.size) {
    empty.innerHTML = `${icon('phone')}<div><b>Chưa có thiết bị</b></div><div>Cắm điện thoại qua USB (bật Gỡ lỗi USB) hoặc kết nối WiFi ở thanh bên.</div>`;
  } else if (!shown) {
    empty.innerHTML = `${icon('search')}<div>Không có máy nào khớp bộ lọc.</div>`;
  }
  empty.classList.toggle('hidden', shown > 0);
}

function refreshSelectionClasses() {
  for (const [id, t] of S.tiles) t.el.classList.toggle('sel', S.selected.has(id));
  if (S.filter === 'sel') renderTiles();
  renderStats();
  renderSidebarCounts();
}

// ---------------- thanh bên ----------------
function renderSidebar() {
  const devs = [...S.devices.values()].filter((d) => !d.soloHidden);
  const cnt = (fn) => devs.filter(fn).length;
  const filters = [
    ['all', 'grid', 'Tất cả', devs.length],
    ['sel', 'checksq', 'Đang chọn', S.selected.size],
    ['usb', 'usb', 'USB', cnt((d) => d.transport === 'usb')],
    ['wifi', 'wifi', 'WiFi', cnt((d) => d.transport === 'wifi')],
    ['bad', 'plug', 'Lỗi / mất kết nối', cnt((d) => d.status !== 'online')],
    ['paused', 'pause', 'Đang tạm dừng xem', cnt((d) => d.paused)],
  ];
  $('#filters').innerHTML = filters.map(([k, ic, label, n]) => `<div class="flt ${S.filter === k ? 'on' : ''}" data-f="${k}">${icon(ic)}<span>${label}</span><span class="n" data-cnt="${k}">${n}</span>
    ${k !== 'sel' ? `<span class="gsel"><button data-gsel="${k}" title="Chọn tất cả máy trong mục này">chọn</button></span>` : ''}</div>`).join('');
  $('#groups').innerHTML = S.groups.length ? S.groups.map((g) => {
    const k = 'group:' + g;
    return `<div class="flt ${S.filter === k ? 'on' : ''}" data-f="${esc(k)}">${icon('tag')}<span>${esc(g)}</span><span class="n">${cnt((d) => d.groups.includes(g))}</span>
      <span class="gsel"><button data-gsel="${esc(k)}" title="Chọn cả nhóm">chọn</button><button data-gdel="${esc(g)}" title="Xoá nhóm">${icon('x')}</button></span></div>`;
  }).join('') : '<div class="flt" style="cursor:default;font-size:12px">Chưa có nhóm. Chọn máy → ⋯ → Thêm vào nhóm.</div>';
}

function renderSidebarCounts() {
  const el = $('[data-cnt="sel"]');
  if (el) el.textContent = S.selected.size;
}

function selectWhere(fn, add = false) {
  if (!add) S.selected.clear();
  for (const d of S.devices.values()) if (!d.pending && !d.soloHidden && fn(d)) S.selected.add(d.id);
  refreshSelectionClasses();
}

// ---------------- chế độ "Chỉ hiển thị các máy này" ----------------
async function setSolo(ids) {
  try {
    if (ids && ids.length) {
      if (!S.solo) lsSet('tileWBeforeSolo', S.tileW);
      await api('/api/solo', { ids });
      toast(`Chỉ hiển thị ${ids.length} máy — các máy khác đã ẩn và tạm dừng xem`, 'ok');
    } else {
      await api('/api/solo', { ids: null });
      toast('Đã hiển thị lại tất cả máy', 'ok');
    }
  } catch (e) { toast(e.message, 'err'); }
}

function updateSoloUi() {
  const b = $('#soloBtn');
  b.classList.toggle('hidden', !S.solo);
  if (S.solo) $('#soloCount').textContent = S.solo.length;
}

function onSoloChanged() {
  // ít máy thì phóng to ô cho vừa màn hình; thoát chế độ thì trả lại kích thước cũ
  requestAnimationFrame(() => {
    if (S.solo) $('#btnFit').click();
    else { const w = lsGet('tileWBeforeSolo', null); if (w) setTileW(w); }
  });
}
$('#soloBtn').onclick = () => setSolo(null);

function filterFn(k) {
  if (k === 'all') return () => true;
  if (k === 'usb') return (d) => d.transport === 'usb';
  if (k === 'wifi') return (d) => d.transport === 'wifi';
  if (k === 'bad') return (d) => d.status !== 'online';
  if (k === 'paused') return (d) => d.paused;
  if (k.startsWith('group:')) { const g = k.slice(6); return (d) => d.groups.includes(g); }
  return () => false;
}

$('.sidebar').addEventListener('click', async (e) => {
  const gsel = e.target.closest('[data-gsel]');
  if (gsel) { e.stopPropagation(); selectWhere(filterFn(gsel.dataset.gsel)); toast(`Đã chọn ${S.selected.size} máy`); return; }
  const gdel = e.target.closest('[data-gdel]');
  if (gdel) {
    e.stopPropagation();
    if (await confirmBox('Xoá nhóm', `Xoá nhóm "${esc(gdel.dataset.gdel)}"? (Không ảnh hưởng tới máy)`, 'Xoá', true)) {
      await api('/api/group', { action: 'delete', name: gdel.dataset.gdel });
      if (S.filter === 'group:' + gdel.dataset.gdel) setFilter('all');
    }
    return;
  }
  const f = e.target.closest('[data-f]');
  if (f) setFilter(f.dataset.f);
});

function setFilter(f) {
  S.filter = f;
  lsSet('filter', f);
  renderAll();
}

$('#search').addEventListener('input', (e) => { S.search = e.target.value.trim(); renderTiles(); });

// ====================================================================
// Đăng ký luồng: chỉ nhận video của ô đang hiển thị
// ====================================================================
const observer = new IntersectionObserver((entries) => {
  for (const en of entries) {
    const d = S.devices.get(en.target.dataset.id);
    if (!d) continue;
    if (en.isIntersecting) S.visible.add(d.vid); else S.visible.delete(d.vid);
  }
  updateSubs();
}, { root: $('#gridWrap'), rootMargin: '150px' });

let subsTimer = null;
function updateSubs(now) {
  clearTimeout(subsTimer);
  subsTimer = setTimeout(() => {
    const vis = TEST_MODE ? [...S.byVid.keys()] : [...S.visible];
    const v = S.live && (TEST_MODE || !document.hidden) ? vis.filter((vid) => S.byVid.has(vid) && !S.byVid.get(vid).paused && !S.byVid.get(vid).zoomed).sort((a, b) => a - b) : [];
    const sig = v.join(',');
    if (sig === S.subsSent) return;
    const prev = new Set(S.subsSent ? S.subsSent.split(',').map(Number) : []);
    for (const vid of v) {
      if (!prev.has(vid)) { const d = S.byVid.get(vid); const t = d && S.tiles.get(d.id); if (t && t.decoder) t.decoder.resume(); }
    }
    S.subsSent = sig;
    sendWs({ t: 'sub', v });
  }, now ? 0 : 120);
}
document.addEventListener('visibilitychange', () => updateSubs(true));

// ====================================================================
// Chọn máy: click / Shift / kéo khung
// ====================================================================
const gridWrap = $('#gridWrap');
let drag = null;

function visibleTileEls() { return $$('.tile:not(.hidden)', $('#grid')); }

gridWrap.addEventListener('pointerdown', (e) => {
  closeCtx();
  const tileEl = e.target.closest('.tile');
  const onScreen = e.target.closest('.screen');
  const d = tileEl && S.devices.get(tileEl.dataset.id);
  // thao tác trên màn hình nhỏ: máy ngoài nhóm → chọn riêng máy này; máy trong nhóm → giữ nhóm (đồng bộ)
  // (chuột phải chỉ mở menu, không đổi lựa chọn)
  if (tileEl && onScreen && !e.ctrlKey) {
    if (e.button !== 2 && d && !d.pending && !S.selected.has(d.id)) selectOnly(d.id);
    return;
  }
  if (e.button !== 0) return;
  drag = { x: e.clientX, y: e.clientY, tileId: tileEl ? tileEl.dataset.id : null, moved: false, add: e.ctrlKey || e.shiftKey, base: new Set(S.selected), shift: e.shiftKey, ctrl: e.ctrlKey };
  gridWrap.setPointerCapture(e.pointerId);
});

gridWrap.addEventListener('pointermove', (e) => {
  if (!drag) return;
  if (!drag.moved && Math.hypot(e.clientX - drag.x, e.clientY - drag.y) < 6) return;
  drag.moved = true;
  let mq = $('.marquee');
  if (!mq) { mq = document.createElement('div'); mq.className = 'marquee'; document.body.appendChild(mq); }
  const x1 = Math.min(drag.x, e.clientX); const y1 = Math.min(drag.y, e.clientY);
  const x2 = Math.max(drag.x, e.clientX); const y2 = Math.max(drag.y, e.clientY);
  Object.assign(mq.style, { left: x1 + 'px', top: y1 + 'px', width: (x2 - x1) + 'px', height: (y2 - y1) + 'px' });
  S.selected = drag.add ? new Set(drag.base) : new Set();
  for (const el of visibleTileEls()) {
    const r = el.getBoundingClientRect();
    if (r.right > x1 && r.left < x2 && r.bottom > y1 && r.top < y2) {
      const d = S.devices.get(el.dataset.id);
      if (d && !d.pending) S.selected.add(d.id);
    }
  }
  // tự cuộn khi kéo sát mép
  const wr = gridWrap.getBoundingClientRect();
  if (e.clientY > wr.bottom - 30) gridWrap.scrollTop += 20;
  else if (e.clientY < wr.top + 30) gridWrap.scrollTop -= 20;
  refreshSelectionClasses();
});

gridWrap.addEventListener('pointerup', (e) => {
  if (!drag) return;
  const dd = drag;
  drag = null;
  const mq = $('.marquee');
  if (mq) mq.remove();
  if (dd.moved) return;
  if (!dd.tileId) { if (!dd.add) { S.selected.clear(); refreshSelectionClasses(); } return; }
  const d = S.devices.get(dd.tileId);
  if (!d || d.pending) return;
  // Click = chọn 1 máy · Ctrl+click = thêm/bớt vào nhóm · Shift+click = chọn cả dãy
  if (dd.ctrl || dd.shift) toggleSelect(d.id, dd.shift);
  else {
    // giữ nhóm ban đầu qua cả 2 lần click của cú nhấp đúp
    const now = performance.now();
    const isSecondClick = lastPlainClick && lastPlainClick.id === d.id && now - lastPlainClick.at < 700
      && S.selected.size === 1 && S.selected.has(d.id);
    if (!isSecondClick) {
      lastPlainClick = { id: d.id, prev: new Set(S.selected), at: now };
    }
    selectOnly(d.id);
  }
});

let lastPlainClick = null;

function selectOnly(id) {
  if (S.selected.size !== 1 || !S.selected.has(id)) S.selected = new Set([id]);
  S.lastClickedId = id;
  refreshSelectionClasses();
}

function toggleSelect(id, range) {
  if (range && S.lastClickedId) {
    const els = visibleTileEls().map((x) => x.dataset.id);
    const a = els.indexOf(S.lastClickedId); const b = els.indexOf(id);
    if (a >= 0 && b >= 0) {
      for (let i = Math.min(a, b); i <= Math.max(a, b); i++) {
        const dv = S.devices.get(els[i]);
        if (dv && !dv.pending) S.selected.add(els[i]);
      }
    }
  } else if (S.selected.has(id)) S.selected.delete(id);
  else S.selected.add(id);
  S.lastClickedId = id;
  refreshSelectionClasses();
}

gridWrap.addEventListener('dblclick', (e) => {
  // pointer capture đổi target của sự kiện → lấy phần tử thật dưới con trỏ
  const under = document.elementFromPoint(e.clientX, e.clientY);
  const tileEl = under && under.closest('.tile');
  if (!tileEl) return;
  const d = S.devices.get(tileEl.dataset.id);
  if (!d || d.pending) return;
  if (under.closest('.screen')) return; // nhấp đúp trên màn hình = chạm 2 lần vào điện thoại (không phóng to)
  // nhấp đúp chỉ để mở màn hình lớn: trả lại nhóm đang chọn trước cú click đầu tiên
  if (lastPlainClick && lastPlainClick.id === d.id && performance.now() - lastPlainClick.at < 700) {
    S.selected = lastPlainClick.prev;
    lastPlainClick = null;
    refreshSelectionClasses();
  }
  openViewer(d.id);
});

gridWrap.addEventListener('contextmenu', (e) => {
  const tileEl = e.target.closest('.tile');
  e.preventDefault();
  if (!tileEl) return;
  const d = S.devices.get(tileEl.dataset.id);
  if (d) tileMenu(d, e.clientX, e.clientY);
});

// ====================================================================
// Điều khiển cảm ứng (ô lưới ở chế độ Thao tác + màn hình lớn)
// ====================================================================
// Đồng bộ tự bật khi đang chọn từ 2 máy trở lên (có thể tắt tạm bằng nút Đồng bộ / F3)
function syncActive() {
  return !S.syncOff && S.selected.size > 1;
}

function targetsFor(vid) {
  const set = new Set([vid]);
  const dev = S.byVid.get(vid);
  // thao tác trên một máy thuộc nhóm → lặp lại trên cả nhóm
  if (syncActive() && dev && S.selected.has(dev.id)) {
    for (const id of S.selected) { const d = S.devices.get(id); if (isOnline(d)) set.add(d.vid); }
  }
  return [...set];
}

function normPoint(canvas, el, cx, cy) {
  const r = el.getBoundingClientRect();
  const vw = canvas.width || 9; const vh = canvas.height || 18;
  const scale = Math.min(r.width / vw, r.height / vh);
  const w = vw * scale; const hh = vh * scale;
  const ox = r.left + (r.width - w) / 2; const oy = r.top + (r.height - hh) / 2;
  return { x: Math.max(0, Math.min(1, (cx - ox) / w)), y: Math.max(0, Math.min(1, (cy - oy) / hh)) };
}

function attachTouch(el, getCtx, { wheelNeedsShift = false, rightClickBack = true } = {}) {
  let active = null;
  let lastMove = 0;
  let pending = null;
  let moveTimer = null;

  const flushMove = () => {
    moveTimer = null;
    if (!active || !pending) return;
    const p = pending; pending = null;
    lastMove = performance.now();
    if (active.pinch) {
      sendWs({ t: 'touch', v: active.v, a: 2, x: p.x, y: p.y, p: 1 });
      sendWs({ t: 'touch', v: active.v, a: 2, x: 1 - p.x, y: 1 - p.y, p: 2 });
    } else sendWs({ t: 'touch', v: active.v, a: 2, x: p.x, y: p.y });
  };

  el.addEventListener('pointerdown', (e) => {
    const ctx = getCtx(e);
    if (!ctx) return;
    e.preventDefault();
    e.stopPropagation();
    el.focus && el.focus({ preventScroll: true });
    S.kbdVid = ctx.vid;
    const v = targetsFor(ctx.vid);
    if (e.button === 2) { if (rightClickBack) sendWs({ t: 'act', v, n: 'back' }); return; }
    if (e.button === 3) { sendWs({ t: 'act', v, n: 'back' }); return; } // nút bên chuột (Back)
    if (e.button === 1) { sendWs({ t: 'act', v, n: 'home' }); return; }
    if (e.button !== 0) return;
    const box = ctx.el || el;
    const p = normPoint(ctx.canvas, box, e.clientX, e.clientY);
    active = { v, canvas: ctx.canvas, box, pinch: e.altKey, id: e.pointerId };
    el.setPointerCapture(e.pointerId);
    if (active.pinch) {
      sendWs({ t: 'touch', v, a: 0, x: p.x, y: p.y, p: 1 });
      sendWs({ t: 'touch', v, a: 0, x: 1 - p.x, y: 1 - p.y, p: 2 });
    } else sendWs({ t: 'touch', v, a: 0, x: p.x, y: p.y });
  });
  el.addEventListener('pointermove', (e) => {
    if (!active || e.pointerId !== active.id) return;
    pending = normPoint(active.canvas, active.box, e.clientX, e.clientY);
    const dt = performance.now() - lastMove;
    if (dt >= 12) flushMove();
    else if (!moveTimer) moveTimer = setTimeout(flushMove, 12 - dt);
  });
  const up = (e) => {
    if (!active || e.pointerId !== active.id) return;
    if (pending) flushMove();
    const p = normPoint(active.canvas, active.box, e.clientX, e.clientY);
    if (active.pinch) {
      sendWs({ t: 'touch', v: active.v, a: 1, x: p.x, y: p.y, p: 1 });
      sendWs({ t: 'touch', v: active.v, a: 1, x: 1 - p.x, y: 1 - p.y, p: 2 });
    } else sendWs({ t: 'touch', v: active.v, a: 1, x: p.x, y: p.y });
    active = null;
  };
  el.addEventListener('pointerup', up);
  el.addEventListener('pointercancel', up);
  el.addEventListener('wheel', (e) => {
    // ô nhỏ: lăn chuột thường để cuộn danh sách máy, Shift+lăn mới cuộn trong điện thoại
    if (wheelNeedsShift && !e.shiftKey) return;
    const ctx = getCtx(e);
    if (!ctx) return;
    e.preventDefault();
    const p = normPoint(ctx.canvas, ctx.el || el, e.clientX, e.clientY);
    const k = e.deltaMode === 1 ? 1 / 3 : 1 / 100;
    // khi giữ Shift trình duyệt chuyển deltaY sang deltaX → coi như cuộn dọc
    const dy = wheelNeedsShift ? (e.deltaY || e.deltaX) : e.deltaY;
    const dx = wheelNeedsShift ? 0 : e.deltaX;
    sendWs({ t: 'scroll', v: targetsFor(ctx.vid), x: p.x, y: p.y, h: Math.max(-16, Math.min(16, -dx * k)), vs: Math.max(-16, Math.min(16, -dy * k)) });
  }, { passive: false });
  el.addEventListener('contextmenu', (e) => { if (getCtx(e)) e.preventDefault(); });
  // chặn nút bên chuột làm trình duyệt "quay lại trang"
  el.addEventListener('mouseup', (e) => { if (e.button === 3 || e.button === 4) e.preventDefault(); });
}

// ô lưới: màn hình nhỏ luôn thao tác trực tiếp, không cần chuyển chế độ
attachTouch(gridWrap, (e) => {
  if (e.ctrlKey) return null; // Ctrl+click = thêm/bớt máy vào nhóm
  const screen = e.target.closest('.screen');
  const tileEl = e.target.closest('.tile');
  if (!screen || !tileEl) return null;
  const d = S.devices.get(tileEl.dataset.id);
  if (!isOnline(d)) return null;
  return { vid: d.vid, canvas: S.tiles.get(d.id).canvas, el: screen };
}, { wheelNeedsShift: true, rightClickBack: false }); // chuột phải trên ô = menu

gridWrap.addEventListener('pointerover', (e) => {
  const tileEl = e.target.closest('.tile');
  const d = tileEl && S.devices.get(tileEl.dataset.id);
  S.hoverVid = d && isOnline(d) && e.target.closest('.screen') ? d.vid : null;
});
gridWrap.addEventListener('pointerleave', () => { S.hoverVid = null; });

// ====================================================================
// Bàn phím → điện thoại
// ====================================================================
function keyboardTargetVid() {
  if (S.viewerId && (document.activeElement === $('#vScreen') || viewer.hover)) return S.devices.get(S.viewerId)?.vid ?? null;
  if (S.hoverVid != null) return S.hoverVid;
  return null;
}

let textBuf = '';
let textTimer = null;
let textVids = null;
function queueText(vids, ch) {
  if (textVids && textVids.join() !== vids.join()) flushText();
  textVids = vids;
  textBuf += ch;
  clearTimeout(textTimer);
  textTimer = setTimeout(flushText, 25);
}
function flushText() {
  clearTimeout(textTimer);
  if (textBuf && textVids) sendWs({ t: 'text', v: textVids, s: textBuf });
  textBuf = '';
  textVids = null;
}

// ---------- Ctrl+V: dán clipboard máy tính vào tất cả máy đang chọn ----------
// Máy nhận: mọi máy đang chọn (đang online). Chưa chọn máy nào → máy đang trỏ chuột / màn hình lớn.
// Khi đã tắt tạm Đồng bộ (F3) mà đang trỏ vào 1 máy → chỉ dán vào máy đó.
function pasteTargets() {
  const kv = keyboardTargetVid();
  if (S.syncOff && kv != null) return [kv];
  const set = new Set();
  for (const id of S.selected) { const d = S.devices.get(id); if (isOnline(d)) set.add(d.vid); }
  if (!set.size && kv != null) set.add(kv);
  return [...set];
}

function pasteToPhones(vids, text) {
  if (!text) { toast('Clipboard máy tính đang trống (chỉ dán được văn bản)', 'err'); return; }
  flushText();
  sendWs({ t: 'text', v: vids, s: text, mode: 'paste' });
  const preview = text.length > 40 ? text.slice(0, 40) + '…' : text;
  toast(`Đã dán vào ${vids.length} máy: "${preview}"`, 'ok');
}

let pendingPaste = null; // { vids, timer } — chờ sự kiện paste của trình duyệt, nếu không có thì đọc clipboard trực tiếp

document.addEventListener('paste', (e) => {
  if (!pendingPaste) return;
  const { vids, timer } = pendingPaste;
  clearTimeout(timer);
  pendingPaste = null;
  e.preventDefault();
  pasteToPhones(vids, e.clipboardData ? e.clipboardData.getData('text/plain') : '');
});

document.addEventListener('keydown', async (e) => {
  if (modalOpen()) return;
  const tag = (e.target.tagName || '').toLowerCase();
  if (tag === 'input' || tag === 'textarea' || tag === 'select') return;

  if (e.ctrlKey && !e.shiftKey && !e.altKey && e.code === 'KeyV') {
    const vids = pasteTargets();
    if (!vids.length) return;
    // không chặn mặc định để trình duyệt phát sự kiện 'paste' (không cần xin quyền clipboard)
    if (pendingPaste) clearTimeout(pendingPaste.timer);
    const timer = setTimeout(async () => {
      if (!pendingPaste || pendingPaste.timer !== timer) return;
      pendingPaste = null;
      try { pasteToPhones(vids, await navigator.clipboard.readText()); } catch (_) { toast('Trình duyệt chặn đọc clipboard', 'err'); }
    }, 200);
    pendingPaste = { vids, timer };
    return;
  }

  const vid = keyboardTargetVid();
  if (vid != null) {
    const v = targetsFor(vid);
    if (e.ctrlKey && e.code === 'KeyC') { e.preventDefault(); sendWs({ t: 'act', v: [vid], n: 'copy' }); return; }
    if (['F3', 'F5', 'F11', 'F12'].includes(e.key)) { /* để phím tắt app xử lý */ } else {
      const code = androidKeycode(e);
      if (code) {
        e.preventDefault();
        flushText();
        sendWs({ t: 'key', v, k: code, a: 'down', m: metaOf(e) });
        return;
      }
      if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault();
        queueText(v, e.key);
        return;
      }
    }
  }

  // phím tắt ứng dụng
  if (e.key === 'F3') { e.preventDefault(); setSyncOff(!S.syncOff); return; }
  if (e.ctrlKey && e.code === 'KeyA') { e.preventDefault(); selectWhere((d) => matchFilter(d)); return; }
  if (e.key === 'Escape') {
    if (S.viewerId) closeViewer();
    else { S.selected.clear(); refreshSelectionClasses(); }
  }
});

document.addEventListener('keyup', (e) => {
  if (modalOpen()) return;
  const tag = (e.target.tagName || '').toLowerCase();
  if (tag === 'input' || tag === 'textarea' || tag === 'select') return;
  const vid = keyboardTargetVid();
  if (vid == null) return;
  if (e.ctrlKey && (e.code === 'KeyV' || e.code === 'KeyC')) return;
  const code = androidKeycode(e);
  if (code) { e.preventDefault(); sendWs({ t: 'key', v: targetsFor(vid), k: code, a: 'up', m: metaOf(e) }); }
});

// ====================================================================
// Màn hình lớn (luồng nét)
// ====================================================================
const viewer = { vid: null, decoder: null, w: 0, h: 0, hover: false };

function openViewer(id) {
  const d = S.devices.get(id);
  if (!d || d.pending) return;
  if (viewer.decoder) viewer.decoder.close();
  S.viewerId = id;
  viewer.vid = d.vid;
  viewer.w = d.w; viewer.h = d.h;
  const canvas = $('#vCanvas');
  canvas.width = 9; canvas.height = 18;
  $('#vOv').classList.remove('hidden');
  viewer.decoder = new StreamDecoder(canvas, {
    hw: 'no-preference',
    onNeedKey: () => sendWs({ t: 'kf', v: viewer.vid, kind: 'hq' }),
    onFirstFrame: () => $('#vOv').classList.add('hidden'),
    onResize: (w, h) => { viewer.w = w; viewer.h = h; layoutViewer(); },
  });
  $('#viewer').classList.remove('hidden');
  sendWs({ t: 'hq', v: d.vid });
  updateViewerHead();
  layoutViewer();
  for (const [tid, t] of S.tiles) t.el.classList.toggle('active', tid === id);
  setTimeout(() => $('#vScreen').focus({ preventScroll: true }), 50);
}

function closeViewer() {
  sendWs({ t: 'hq', v: null });
  if (viewer.decoder) viewer.decoder.close();
  viewer.decoder = null;
  viewer.vid = null;
  S.viewerId = null;
  $('#viewer').classList.add('hidden');
  for (const t of S.tiles.values()) t.el.classList.remove('active');
}

function updateViewerHead() {
  if (!S.viewerId) return;
  const d = S.devices.get(S.viewerId);
  if (!d) return;
  $('#vTitle').textContent = `#${String(d.num).padStart(2, '0')} ${displayName(d)}`;
  $('#vSub').textContent = `${d.model} · Android ${d.android} · ${d.transport ? d.transport.toUpperCase() : '—'}${d.battery != null ? ' · ' + d.battery + '%' : ''}`;
  const n = targetsFor(d.vid).length;
  const sh = $('#vSync');
  sh.textContent = `Đồng bộ ${n} máy`;
  sh.classList.toggle('hidden', n <= 1);
}

function layoutViewer() {
  if (!S.viewerId) return;
  const w = viewer.w || 9; const hgt = viewer.h || 18.5;
  const availH = innerHeight - 48 - 46 - 44 - 18;
  const maxW = Math.max(240, innerWidth * 0.5);
  let H = availH;
  let W = H * w / hgt;
  if (W > maxW) { W = maxW; H = W * hgt / w; }
  const sc = $('#vScreen');
  sc.style.width = Math.round(W) + 'px';
  sc.style.height = Math.round(H) + 'px';
}
addEventListener('resize', layoutViewer);

const vScreen = $('#vScreen');
attachTouch(vScreen, () => (viewer.vid != null && S.viewerId ? { vid: viewer.vid, canvas: $('#vCanvas') } : null));
vScreen.addEventListener('pointerenter', () => { viewer.hover = true; });
vScreen.addEventListener('pointerleave', () => { viewer.hover = false; });

$('#vClose').onclick = closeViewer;
function stepViewer(dir) {
  const ids = visibleTileEls().map((e) => e.dataset.id).filter((id) => !S.devices.get(id)?.pending);
  if (!ids.length) return;
  const i = ids.indexOf(S.viewerId);
  openViewer(ids[(i + dir + ids.length) % ids.length]);
}
$('#vPrev').onclick = () => stepViewer(-1);
$('#vNext').onclick = () => stepViewer(1);

$('#viewer').addEventListener('click', async (e) => {
  const b = e.target.closest('[data-vact],[data-vcmd]');
  if (!b || !S.viewerId) return;
  const d = S.devices.get(S.viewerId);
  if (!d) return;
  if (b.dataset.vact) { sendWs({ t: 'act', v: targetsFor(d.vid), n: b.dataset.vact }); return; }
  const c = b.dataset.vcmd;
  if (c === 'paste') {
    try { const text = await navigator.clipboard.readText(); if (text) sendWs({ t: 'text', v: targetsFor(d.vid), s: text, mode: 'paste' }); } catch (_) { toast('Không đọc được clipboard', 'err'); }
  } else if (c === 'shot') {
    const a = document.createElement('a'); a.href = `/api/shot?id=${encodeURIComponent(d.id)}&dl=1`; a.click();
  } else if (c === 'apps') dlgApps([d.id], d.id);
  else if (c === 'files') dlgFiles(d);
  else if (c === 'info') dlgInfo(d);
  else if (c === 'scrcpy') api('/api/scrcpy', { id: d.id }).then((r) => toast(r.msg)).catch((er) => toast(er.message, 'err'));
});

// ====================================================================
// Thanh trên: chế độ, đồng bộ, kích thước...
// ====================================================================

function updateSyncUi() {
  const on = syncActive();
  const btn = $('#syncBtn');
  btn.classList.toggle('on', on);
  btn.classList.toggle('off', S.syncOff);
  $('#app').classList.toggle('sync-on', on);
  $('#syncCount').textContent = on ? ` (${S.selected.size})` : S.syncOff ? ': tắt' : '';
  btn.title = S.syncOff
    ? 'Đồng bộ đang TẮT — thao tác chỉ áp dụng cho máy đang chạm. Bấm (F3) để bật lại.'
    : 'Tự động đồng bộ khi chọn từ 2 máy trở lên (Ctrl+click để chọn nhóm). Bấm (F3) để tắt tạm.';
}
function setSyncOff(off) {
  S.syncOff = off;
  lsSet('syncOff', off);
  if (!off && S.selected.size < 2) toast('Đồng bộ tự bật khi chọn nhóm từ 2 máy (Ctrl+click)');
  renderStats();
}
$('#syncBtn').onclick = () => setSyncOff(!S.syncOff);

function setTileW(w) {
  S.tileW = Math.max(110, Math.min(460, Math.round(w)));
  document.documentElement.style.setProperty('--tile-w', S.tileW + 'px');
  $('#tileSize').value = S.tileW;
  lsSet('tileW', S.tileW);
}
$('#tileSize').addEventListener('input', (e) => setTileW(+e.target.value));
$('#btnFit').onclick = () => {
  const n = visibleTileEls().length || 1;
  const wr = gridWrap.getBoundingClientRect();
  const W = wr.width - 20; const H = wr.height - 20;
  const ratio = 18.5 / 9; const head = 26; const gap = 10;
  let best = 110;
  for (let cols = 1; cols <= n; cols++) {
    const tw = (W - gap * (cols - 1)) / cols;
    const rows = Math.ceil(n / cols);
    const th = tw * ratio + head;
    if (rows * th + gap * (rows - 1) <= H) { best = Math.max(best, tw); break; }
  }
  if (best === 110) {
    // không vừa hết → chọn theo chiều cao
    const cols = Math.ceil(Math.sqrt(n * (W / H) * ratio));
    best = (W - gap * (cols - 1)) / cols;
  }
  setTileW(Math.floor(best) - 1);
};

$('#sortBy').value = S.sort;
$('#sortBy').onchange = (e) => { S.sort = e.target.value; lsSet('sort', S.sort); renderTiles(); };

function setLive(on) {
  S.live = on;
  const b = $('#btnLive');
  b.innerHTML = on ? `${icon('eye')}<span>Đang xem</span>` : `${icon('pause')}<span>Đã tạm dừng</span>`;
  b.classList.toggle('primary', !on);
  for (const [id, t] of S.tiles) { t.el.classList.toggle('paused', !on); updateTileOverlay(t, S.devices.get(id)); }
  updateSubs(true);
}
$('#btnLive').onclick = () => setLive(!S.live);

function applyTheme(t) {
  document.documentElement.dataset.theme = t;
  $('#btnTheme').innerHTML = icon(t === 'light' ? 'moon' : 'sun');
  lsSet('theme', t);
}
$('#btnTheme').onclick = () => applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light');
$('#btnSidebar').onclick = () => { $('#app').classList.toggle('no-sidebar'); lsSet('sidebar', !$('#app').classList.contains('no-sidebar')); };
$('#btnSettings').onclick = dlgSettings;

// ngôn ngữ giao diện
{
  const sel = $('#langSel');
  sel.innerHTML = LANGS.map((l) => `<option value="${l.code}">${l.name}</option>`).join('');
  sel.value = currentLang();
  sel.onchange = () => setLang(sel.value);
}

// ủng hộ tác giả
$('#btnDonate').onclick = dlgDonate;
function dlgDonate() {
  const m = modal({
    title: 'Ủng hộ tác giả',
    wide: true,
    body: `<div class="donate-intro">
        <div>ControlPhone miễn phí và mã nguồn mở.</div>
        <div>Nếu phần mềm giúp ích cho bạn, hãy ủng hộ để tác giả có thêm động lực duy trì và phát triển. Cảm ơn bạn!</div>
        <div data-no-i18n><a href="${esc(DONATE.github)}" target="_blank" rel="noopener">${esc(DONATE.github.replace(/^https?:\/\//, ''))}</a></div>
      </div>
      <div class="donate-grid">
        <div class="donate-card">
          <div class="donate-h">PayPal</div>
          <img src="img/donate-paypal.svg" alt="PayPal QR" class="donate-qr">
          <div class="donate-addr" data-no-i18n>${esc(DONATE.paypal.replace(/^https?:\/\//, ''))}</div>
          <div class="donate-acts">
            <a class="btn primary" href="${esc(DONATE.paypal)}" target="_blank" rel="noopener">${icon('heart')}<span>Mở PayPal</span></a>
            <button class="btn" data-copy="${esc(DONATE.paypal)}">${icon('copy')}<span>Sao chép</span></button>
          </div>
        </div>
        <div class="donate-card">
          <div class="donate-h">BNB / ETH</div>
          <img src="img/donate-crypto.svg" alt="BNB / ETH QR" class="donate-qr">
          <div class="donate-addr mono" data-no-i18n>${esc(DONATE.evm)}</div>
          <div class="donate-acts">
            <button class="btn primary" data-copy="${esc(DONATE.evm)}">${icon('copy')}<span>Sao chép địa chỉ</span></button>
          </div>
          <div class="donate-note">Mạng: BNB Smart Chain (BEP-20) hoặc Ethereum (ERC-20). Hãy kiểm tra đúng mạng trước khi gửi.</div>
        </div>
      </div>`,
    actions: [{ label: 'Đóng', primary: true }],
  });
  m.el.querySelectorAll('[data-copy]').forEach((b) => {
    b.onclick = () => navigator.clipboard.writeText(b.dataset.copy)
      .then(() => toast('Đã sao chép', 'ok'))
      .catch(() => toast('Không sao chép được — hãy bôi đen và sao chép thủ công', 'err'));
  });
}

// ====================================================================
// Thao tác hàng loạt
// ====================================================================
function selIds({ onlineOnly = true, quiet = false } = {}) {
  const ids = [...S.selected].filter((id) => { const d = S.devices.get(id); return d && !d.pending && (!onlineOnly || d.status !== 'offline'); });
  if (!ids.length && !quiet) toast('Hãy chọn ít nhất 1 máy (click vào ô, kéo khung, hoặc Ctrl+A)', 'err');
  return ids;
}
function selVids() {
  return selIds().map((id) => S.devices.get(id)).filter(isOnline).map((d) => d.vid);
}

async function batch(op, ids, params, title, opt = {}) {
  if (!ids.length) return null;
  try {
    const r = await api('/api/batch', { op, ids, params, title });
    if (opt.openResults) pendingOpen.add(r.taskId);
    return r;
  } catch (e) { toast(e.message, 'err'); return null; }
}

$('#actionbar').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  if (b.dataset.sel) {
    if (b.dataset.sel === 'all') selectWhere((d) => matchFilter(d));
    else if (b.dataset.sel === 'none') { S.selected.clear(); refreshSelectionClasses(); }
    else {
      const next = new Set();
      for (const d of S.devices.values()) if (!d.pending && matchFilter(d) && !S.selected.has(d.id)) next.add(d.id);
      S.selected = next; refreshSelectionClasses();
    }
    return;
  }
  if (b.dataset.act) {
    const v = selVids();
    if (v.length) sendWs({ t: 'act', v, n: b.dataset.act });
    return;
  }
  const c = b.dataset.cmd;
  if (c === 'text') dlgText();
  else if (c === 'paste') {
    const v = selVids();
    if (v.length) navigator.clipboard.readText().then((t) => pasteToPhones(v, t)).catch(() => toast('Trình duyệt chặn đọc clipboard — hãy dùng Ctrl+V', 'err'));
  }
  else if (c === 'apps') { const ids = selIds(); if (ids.length) dlgApps(ids); }
  else if (c === 'apk') { if (selIds().length) pickApks(null); }
  else if (c === 'push') { if (selIds().length) dlgPush(); }
  else if (c === 'url') dlgUrl();
  else if (c === 'shell') dlgShell();
  else if (c === 'shot') batch('screenshot', selIds(), {}, 'Chụp màn hình');
  else if (c === 'more') moreMenu(b);
});

function moreMenu(btn) {
  const r = btn.getBoundingClientRect();
  ctxMenu(r.left, r.bottom + 4, [
    { label: 'Thêm vào nhóm…', icon: 'tag', onClick: () => dlgGroup(selIds()) },
    { label: 'Bỏ khỏi nhóm…', icon: 'tag', onClick: () => dlgGroup(selIds(), true) },
    { label: 'Đánh số lại theo thứ tự đang hiển thị', icon: 'hash', onClick: renumber },
    '-',
    { label: 'Gửi clipboard máy tính tới máy chọn', icon: 'clipboard', onClick: sendClipboard },
    { label: 'Kéo thanh thông báo', icon: 'bell', onClick: () => sendWs({ t: 'act', v: selVids(), n: 'notif' }) },
    { label: 'Xoay màn hình', icon: 'rotate', onClick: () => sendWs({ t: 'act', v: selVids(), n: 'rotate' }) },
    { label: 'Luôn sáng khi cắm sạc: BẬT', icon: 'sun', onClick: () => batch('stayawake', selIds(), { on: true }, 'Luôn sáng') },
    { label: 'Luôn sáng khi cắm sạc: TẮT', icon: 'moon', onClick: () => batch('stayawake', selIds(), { on: false }, 'Tắt luôn sáng') },
    { label: 'Độ sáng màn hình…', icon: 'sun', onClick: dlgBrightness },
    '-',
    { label: 'USB → WiFi (giữ kết nối không dây)', icon: 'wifi', onClick: toWifi },
    { label: 'WiFi → USB (tắt ADB qua WiFi)', icon: 'usb', onClick: () => toUsb(selIds()) },
    { label: 'Ngắt WiFi ADB', icon: 'wifioff', onClick: () => batch('disconnectwifi', selIds(), {}, 'Ngắt WiFi') },
    { label: 'ROOT: kiểm tra quyền root', icon: 'shield', onClick: () => batch('rootcheck', selIds(), {}, 'Kiểm tra root', { openResults: true }) },
    { label: 'ROOT: bật ADB WiFi cố định (cổng 5555)', icon: 'shield', onClick: rootAdbWifi },
    '-',
    { label: 'Chỉ hiển thị các máy đã chọn', icon: 'eye', onClick: () => { const ids = selIds(); if (ids.length) setSolo(ids); } },
    S.solo ? { label: 'Hiện lại tất cả máy', icon: 'grid', onClick: () => setSolo(null) } : null,
    { label: 'Tạm dừng xem các máy đã chọn', icon: 'pause', onClick: () => { const ids = selIds(); if (ids.length) setPaused(ids, true); } },
    { label: 'Tiếp tục xem các máy đã chọn', icon: 'play', onClick: () => { const ids = selIds(); if (ids.length) setPaused(ids, false); } },
    { label: 'Kết nối lại luồng hình', icon: 'refresh', onClick: () => { const ids = selIds(); if (ids.length) api('/api/restart', { ids }).then(() => toast('Đang kết nối lại…')); } },
    { label: 'Khởi động lại điện thoại…', icon: 'power', danger: true, onClick: rebootSel },
  ]);
}

/** Máy chịu tác động khi thao tác từ menu của máy d: cả nhóm nếu d thuộc nhóm đang đồng bộ, ngược lại chỉ d. */
function menuTargets(d) {
  return targetsFor(d.vid).map((v) => S.byVid.get(v)?.id).filter(Boolean);
}

// Lệnh ADB soạn sẵn (quản lý / xem thông tin — không phải tự động hoá)
const ADB_PRESETS = [
  { label: 'Thông tin máy', icon: 'info', show: true,
    cmd: 'echo "Model: $(getprop ro.product.model)"; echo "Android: $(getprop ro.build.version.release) (SDK $(getprop ro.build.version.sdk))"; echo "Serial: $(getprop ro.serialno)"; echo "Bản ROM: $(getprop ro.build.display.id)"' },
  { label: 'Pin & nhiệt độ', icon: 'zap', show: true, cmd: "dumpsys battery | grep -E 'level|temperature|status|powered|health'" },
  { label: 'Bộ nhớ trong', icon: 'folder', show: true, cmd: 'df -h /data /sdcard 2>/dev/null' },
  { label: 'RAM', icon: 'grid', show: true, cmd: "grep -E 'MemTotal|MemAvailable' /proc/meminfo" },
  { label: 'IP / WiFi đang kết nối', icon: 'wifi', show: true, cmd: "ip -f inet addr show wlan0 | grep inet; dumpsys wifi | grep -m1 'mWifiInfo' | sed 's/, /\\n/g' | grep -E 'SSID|RSSI|Link speed'" },
  { label: 'Ứng dụng đang mở', icon: 'apps', show: true, cmd: "dumpsys activity activities | grep -m1 -E 'mResumedActivity|topResumedActivity'" },
  { label: 'Danh sách ứng dụng đã cài', icon: 'package', show: true, cmd: "pm list packages -3 | sed 's/package://' | sort" },
  { label: 'Độ phân giải màn hình', icon: 'phone', show: true, cmd: 'wm size; wm density' },
  { label: 'Thời gian chạy (uptime)', icon: 'refresh', show: true, cmd: 'uptime' },
  '-',
  // Android 10: game không tự ẩn thanh điều hướng → ép chế độ toàn màn hình theo từng ứng dụng (policy_control, bị bỏ từ Android 11)
  { label: 'Ẩn thanh 3 nút cho ứng dụng đang mở (Android 10)', icon: 'phone', show: true,
    cmd: String.raw`if [ "$(getprop ro.build.version.sdk)" -ge 30 ]; then echo "Android 11+: không cần/không hỗ trợ (game tự ẩn thanh điều hướng)"; else P=$(dumpsys window | grep -m1 mCurrentFocus | sed 's/.* \([^ /]*\)\/.*/\1/'); case "$P" in *mCurrentFocus*|"") echo "Không xác định được ứng dụng đang mở";; *) C=$(settings get global policy_control); case "$C" in immersive.navigation=*) L=${'$'}{C#immersive.navigation=};; *) L="";; esac; case ",$L," in *",$P,"*) ;; *) L="${'$'}{L:+$L,}$P";; esac; settings put global policy_control "immersive.navigation=$L"; echo "Đã ẩn thanh 3 nút trong: $L";; esac; fi` },
  { label: 'Hiện lại thanh 3 nút (Android 10)', icon: 'phone', show: true,
    cmd: 'settings delete global policy_control >/dev/null; echo "Đã hiện lại thanh điều hướng ở mọi ứng dụng"' },
  '-',
  { label: 'Bật WiFi', icon: 'wifi', cmd: 'svc wifi enable && echo "Đã bật WiFi"' },
  { label: 'Tắt WiFi', icon: 'wifioff', cmd: 'svc wifi disable && echo "Đã tắt WiFi"', confirm: 'Máy đang nối ADB qua WiFi sẽ mất kết nối.' },
  { label: 'Bật dữ liệu di động', icon: 'zap', cmd: 'svc data enable && echo "Đã bật dữ liệu"' },
  { label: 'Tắt dữ liệu di động', icon: 'stop', cmd: 'svc data disable && echo "Đã tắt dữ liệu"' },
  { label: 'Dọn bộ nhớ đệm (cache) ứng dụng', icon: 'eraser', cmd: 'pm trim-caches 999G && echo "Đã dọn cache"' },
  { label: 'Đóng ứng dụng chạy nền', icon: 'stop', cmd: 'am kill-all && echo "Đã đóng ứng dụng nền"' },
  // chỉ giả lập mức pin hiển thị (giữ đến khi khôi phục hoặc khởi động lại máy)
  { label: 'Đặt pin 100% (giả lập)', icon: 'zap', cmd: 'dumpsys battery set level 100 && echo "Đã đặt pin 100%"' },
  { label: 'Khôi phục pin thật', icon: 'refresh', cmd: 'dumpsys battery reset && echo "Đã khôi phục pin thật"' },
];

function adbMenu(d, x, y) {
  const ids = menuTargets(d);
  const n = ids.length > 1 ? ` — ${ids.length} máy` : '';
  ctxMenu(x, y, [
    ...ADB_PRESETS.map((p) => (p === '-' ? '-' : {
      label: p.label, icon: p.icon,
      onClick: async () => {
        if (p.confirm && !await confirmBox(p.label, `${esc(p.confirm)}<br>Thực hiện trên ${ids.length} máy?`, 'Thực hiện', true)) return;
        batch('shell', ids, { cmd: p.cmd }, p.label + n, { openResults: !!p.show });
      },
    })),
    '-',
    { label: 'Lệnh tuỳ chỉnh…', icon: 'terminal', onClick: () => dlgShell(ids) },
    { label: 'Khởi động lại máy…', icon: 'power', danger: true, onClick: async () => {
      if (await confirmBox('Khởi động lại', `Khởi động lại ${ids.length} máy?`, 'Khởi động lại', true)) batch('reboot', ids, {}, 'Khởi động lại');
    } },
  ]);
}

function tileMenu(d, x, y) {
  if (d.pending) {
    ctxMenu(x, y, [{ label: d.error || d.status, icon: 'info' }]);
    return;
  }
  const one = [d.id];
  const ids = menuTargets(d);
  const n = ids.length > 1 ? ` (${ids.length} máy)` : '';
  const shellOn = (cmd, title) => batch('shell', ids, { cmd }, title + n);
  ctxMenu(x, y, [
    // 1. chỉ phóng to máy được chuột phải; nếu thuộc nhóm thì thao tác vẫn đồng bộ cả nhóm
    { label: `Phóng to #${d.num}`, icon: 'maximize', onClick: () => openViewer(d.id) },
    { label: `Cài APK${n}…`, icon: 'package', onClick: () => pickApks(ids) },
    { label: `Nhập tệp vào máy${n}…`, icon: 'upload', onClick: () => dlgPush(null, ids) },
    { label: 'Xuất tệp ra máy tính…', icon: 'download', onClick: () => dlgFiles(d) },
    { label: `Lệnh ADB${n}  ▸`, icon: 'terminal', onClick: () => adbMenu(d, x, y) },
    { label: `Mở cài đặt mạng${n}`, icon: 'wifi', onClick: () => shellOn('am start -a android.settings.WIRELESS_SETTINGS 2>&1 | tail -1', 'Cài đặt mạng') },
    { label: `Mở Cài đặt${n}`, icon: 'settings', onClick: () => shellOn('am start -a android.settings.SETTINGS 2>&1 | tail -1', 'Cài đặt') },
    d.paused
      ? { label: `Tiếp tục xem${n}`, icon: 'play', onClick: () => setPaused(ids, false) }
      : { label: `Tạm dừng xem${n}`, icon: 'pause', onClick: () => setPaused(ids, true) },
    { label: ids.length > 1 ? `Chỉ hiển thị nhóm này (${ids.length} máy)` : 'Chỉ hiển thị máy này', icon: 'eye', onClick: () => setSolo(ids) },
    S.solo ? { label: 'Hiện lại tất cả máy', icon: 'grid', onClick: () => setSolo(null) } : null,
    '-',
    { label: 'Đổi tên…', icon: 'type', onClick: () => renameDevice(d) },
    { label: 'Đổi số thứ tự…', icon: 'hash', onClick: () => renumberDevice(d) },
    { label: 'Thêm vào nhóm…', icon: 'tag', onClick: () => dlgGroup(one) },
    { label: 'Ứng dụng…', icon: 'apps', onClick: () => dlgApps(one, d.id) },
    { label: 'Thông tin', icon: 'info', onClick: () => dlgInfo(d) },
    { label: 'Xem ảnh chụp màn hình', icon: 'camera', onClick: () => window.open(`/api/shot?id=${encodeURIComponent(d.id)}`, '_blank') },
    { label: 'Mở bằng scrcpy gốc', icon: 'monitor', onClick: () => api('/api/scrcpy', { id: d.id }).then((r) => toast(r.msg)).catch((e) => toast(e.message, 'err')) },
    '-',
    d.transports.some((t) => t.type === 'wifi')
      ? { label: 'WiFi → USB', icon: 'usb', onClick: () => toUsb(one) }
      : { label: 'USB → WiFi', icon: 'wifi', onClick: () => batch('tcpip', one, {}, 'USB → WiFi', { openResults: true }) },
    { label: 'Kết nối lại luồng hình', icon: 'refresh', onClick: () => api('/api/restart', { ids: one }) },
    { label: 'Khởi động lại máy', icon: 'power', danger: true, onClick: async () => { if (await confirmBox('Khởi động lại', `Khởi động lại máy "${esc(displayName(d))}"?`, 'Khởi động lại', true)) batch('reboot', one, {}, 'Khởi động lại'); } },
  ]);
}

/** Tạm dừng xem: điện thoại ngừng gửi hình (giảm tải adb/USB/máy tính) nhưng vẫn nhận điều khiển & đồng bộ. */
async function setPaused(ids, paused) {
  try {
    const r = await api('/api/pause', { ids, paused });
    toast(paused ? `Đã tạm dừng xem ${r.changed} máy (vẫn điều khiển được)` : `Đã tiếp tục xem ${r.changed} máy`, 'ok');
  } catch (e) { toast(e.message, 'err'); }
}

async function renameDevice(d) {
  const v = await promptBox('Đổi tên máy', `Tên hiển thị cho #${d.num} (${d.model})`, d.label, d.model);
  if (v !== null) api('/api/label', { id: d.id, label: v.trim() }).catch((e) => toast(e.message, 'err'));
}
async function renumberDevice(d) {
  const v = await promptBox('Đổi số thứ tự', 'Số thứ tự mới', String(d.num));
  if (v !== null) api('/api/num', { id: d.id, num: v }).catch((e) => toast(e.message, 'err'));
}
async function renumber() {
  const ids = visibleTileEls().map((e) => e.dataset.id).filter((id) => !S.devices.get(id)?.pending);
  if (!ids.length) return;
  if (await confirmBox('Đánh số lại', `Đánh số lại ${ids.length} máy theo thứ tự đang hiển thị (1 → ${ids.length})?`)) {
    await api('/api/renumber', { ids });
    toast('Đã đánh số lại', 'ok');
  }
}
async function sendClipboard() {
  const v = selVids();
  if (!v.length) return;
  try {
    const text = await navigator.clipboard.readText();
    sendWs({ t: 'clipset', v, s: text });
    toast(`Đã gửi clipboard tới ${v.length} máy`, 'ok');
  } catch (_) { toast('Không đọc được clipboard', 'err'); }
}
async function rebootSel() {
  const ids = selIds();
  if (!ids.length) return;
  if (await confirmBox('Khởi động lại', `Khởi động lại ${ids.length} máy?`, 'Khởi động lại', true)) batch('reboot', ids, {}, 'Khởi động lại');
}
async function toWifi() {
  const ids = selIds();
  if (!ids.length) return;
  batch('tcpip', ids, {}, 'USB → WiFi', { openResults: true });
}
async function toUsb(ids) {
  if (!ids.length) return;
  // máy chỉ có WiFi (không cắm cáp) sẽ mất kết nối cho tới khi cắm USB
  const wifiOnly = ids.filter((id) => !S.devices.get(id)?.transports.some((t) => t.type === 'usb')).length;
  if (wifiOnly && !await confirmBox('WiFi → USB', `${wifiOnly} máy chưa cắm cáp USB sẽ mất kết nối cho tới khi cắm cáp. Tiếp tục?`, 'Chuyển về USB', true)) return;
  batch('tousb', ids, {}, 'WiFi → USB', { openResults: true });
}
async function rootAdbWifi() {
  const ids = selIds();
  if (!ids.length) return;
  if (await confirmBox('ADB WiFi cố định (root)', `Bật ADB qua WiFi cố định (cổng 5555) trên ${ids.length} máy có root? ADB sẽ khởi động lại (mất kết nối vài giây), sau đó có thể kết nối WiFi ngay cả sau khi khởi động lại máy.`)) {
    batch('rootadbwifi', ids, {}, 'ADB WiFi cố định', { openResults: true });
  }
}

// ---------------- hộp thoại ----------------
function dlgText() {
  const v = selVids();
  if (!v.length) { selIds(); return; }
  const m = modal({
    title: `Gửi văn bản tới ${v.length} máy`,
    body: `<label class="field"><span>Nội dung (hỗ trợ tiếng Việt, emoji). Hãy chạm vào ô nhập trên điện thoại trước.</span>
      <textarea class="inp" rows="5" id="txt"></textarea></label>
      <label class="check-row"><input type="checkbox" id="txtEnter"> Nhấn Enter sau khi gửi</label>
      <div class="note">Văn bản được dán qua clipboard nên gõ được mọi ký tự có dấu.</div>`,
    actions: [
      { label: 'Đóng' },
      {
        label: `${icon('type')} Gửi`, primary: true, onClick: () => {
          const s = $('#txt', m.el).value;
          if (!s) return false;
          sendWs({ t: 'text', v: selVids(), s, mode: 'paste', enter: $('#txtEnter', m.el).checked });
          toast(`Đã gửi tới ${selVids().length} máy`, 'ok');
          return false;
        },
      },
    ],
  });
}

function dlgUrl() {
  const ids = selIds();
  if (!ids.length) return;
  const m = modal({
    title: `Mở URL trên ${ids.length} máy`,
    body: '<label class="field"><span>Đường dẫn</span><input class="inp" id="url" placeholder="https://..."></label>',
    actions: [{ label: 'Huỷ' }, {
      label: 'Mở', primary: true, onClick: () => {
        let u = $('#url', m.el).value.trim();
        if (!u) return false;
        if (!/^[a-z][\w+.-]*:/i.test(u)) u = 'https://' + u;
        batch('url', selIds(), { url: u }, 'Mở URL');
      },
    }],
  });
}

function dlgShell(targetIds) {
  const ids = targetIds || selIds();
  if (!ids.length) return;
  const anyRoot = ids.some((id) => S.devices.get(id)?.root);
  const hist = lsGet('shellHist', []);
  const m = modal({
    title: `ADB shell trên ${ids.length} máy`,
    body: `<label class="field"><span>Lệnh</span><input class="inp" id="cmd" list="shHist" placeholder="vd: getprop ro.build.version.release"></label>
      <datalist id="shHist">${hist.map((x) => `<option value="${esc(x)}">`).join('')}</datalist>
      <label class="check-row"><input type="checkbox" id="asRoot" ${anyRoot ? '' : 'disabled'}> Chạy với quyền root (su -c) ${anyRoot ? '' : '— không có máy root'}</label>
      <div class="note">Kết quả của từng máy sẽ hiện ra khi chạy xong.</div>`,
    actions: [{ label: 'Huỷ' }, {
      label: `${icon('terminal')} Chạy`, primary: true, onClick: () => {
        const cmd = $('#cmd', m.el).value.trim();
        if (!cmd) return false;
        lsSet('shellHist', [cmd, ...hist.filter((x) => x !== cmd)].slice(0, 30));
        batch('shell', ids, { cmd, root: $('#asRoot', m.el).checked }, 'Shell: ' + cmd.slice(0, 40), { openResults: true });
      },
    }],
  });
}

function dlgBrightness() {
  const ids = selIds();
  if (!ids.length) return;
  const m = modal({
    title: `Độ sáng (${ids.length} máy)`,
    body: '<label class="field"><span>Mức sáng (0–255). Mức thấp giúp tiết kiệm pin khi treo máy.</span><input type="range" min="0" max="255" value="40" id="br"><b id="brv">40</b></label>',
    actions: [{ label: 'Huỷ' }, { label: 'Áp dụng', primary: true, onClick: () => batch('brightness', selIds(), { value: +$('#br', m.el).value }, 'Độ sáng') }],
  });
  $('#br', m.el).oninput = (e) => { $('#brv', m.el).textContent = e.target.value; };
}

async function uploadAll(files) {
  const out = [];
  for (const f of files) {
    const t = toastProgress(`Đang tải lên ${f.name}…`);
    try {
      out.push(await uploadFile(f, (p) => t.set(`Đang tải lên ${f.name}… ${Math.round(p * 100)}%`)));
    } finally { t.close(); }
  }
  return out;
}

function toastProgress(msg) {
  const box = $('#toasts');
  const el = document.createElement('div');
  el.className = 'toast';
  el.textContent = msg;
  box.appendChild(el);
  return { set: (m) => { el.textContent = m; }, close: () => el.remove() };
}

async function installApks(files, targetIds) {
  const ids = targetIds || selIds();
  if (!ids.length || !files.length) return;
  const ups = await uploadAll(files).catch((e) => { toast(e.message, 'err'); return []; });
  const n = ids.length > 1 ? ` (${ids.length} máy)` : '';
  for (const u of ups) batch('install', ids, { token: u.token }, `Cài ${u.name}${n}`, { openResults: false });
}
let apkTargets = null; // máy sẽ cài khi chọn file APK (null = các máy đang chọn)
function pickApks(ids) {
  apkTargets = ids || null;
  $('#fileApk').click();
}
$('#fileApk').onchange = (e) => { installApks([...e.target.files], apkTargets); apkTargets = null; e.target.value = ''; };

function dlgPush(preFiles, targetIds) {
  const ids = targetIds || selIds();
  if (!ids.length) return;
  let files = preFiles || [];
  const m = modal({
    title: `Gửi tệp tới ${ids.length} máy`,
    body: `<div class="field"><span>Tệp</span><div style="display:flex;gap:8px;align-items:center"><button class="btn" id="pick">${icon('file')} Chọn tệp…</button><span id="fl" class="meta"></span></div></div>
      <label class="field"><span>Thư mục trên điện thoại</span><input class="inp" id="dir" value="${esc(lsGet('pushDir', '/sdcard/Download/'))}"></label>
      <div class="note">Mẹo: kéo thả tệp bất kỳ vào cửa sổ để gửi nhanh (tệp .apk sẽ được cài đặt).</div>`,
    actions: [{ label: 'Huỷ' }, {
      label: `${icon('upload')} Gửi`, primary: true, onClick: async () => {
        if (!files.length) { toast('Chưa chọn tệp', 'err'); return false; }
        const dir = $('#dir', m.el).value.trim() || '/sdcard/Download/';
        lsSet('pushDir', dir);
        const ups = await uploadAll(files).catch((e) => { toast(e.message, 'err'); return []; });
        for (const u of ups) batch('push', ids, { token: u.token, dir, name: u.name }, `Gửi ${u.name}`);
      },
    }],
  });
  const showFiles = () => { $('#fl', m.el).textContent = files.length ? files.map((f) => `${f.name} (${fmtSize(f.size)})`).join(', ') : 'chưa chọn'; };
  showFiles();
  $('#pick', m.el).onclick = () => {
    const inp = $('#fileAny');
    inp.onchange = () => { files = [...inp.files]; inp.value = ''; showFiles(); };
    inp.click();
  };
}

async function dlgApps(ids, sourceId) {
  const src = S.devices.get(sourceId || ids[0]);
  if (!src) return;
  const targetsLabel = () => (sourceId ? (targetsFor(src.vid).length > 1 ? `${targetsFor(src.vid).length} máy (đồng bộ)` : '1 máy') : `${ids.length} máy`);
  const targetIds = () => (sourceId ? targetsFor(src.vid).map((v) => S.byVid.get(v)?.id).filter(Boolean) : ids);
  const m = modal({
    title: 'Ứng dụng',
    wide: true,
    body: `<div class="pathbar"><input class="inp" id="af" placeholder="Lọc hoặc nhập tên gói (vd: com.android.chrome)"><button class="btn primary" id="aOpen">${icon('play')} Mở gói đã nhập</button></div>
      <div class="note"><div>Danh sách lấy từ máy #${src.num} ${esc(displayName(src))}</div><div>Áp dụng cho: <b id="atl"></b></div></div>
      <div class="list" id="al"><div class="li">Đang tải…</div></div>`,
  });
  $('#atl', m.el).textContent = targetsLabel();
  let apps = [];
  const render = () => {
    const q = $('#af', m.el).value.trim().toLowerCase();
    const list = apps.filter((a) => !q || a.toLowerCase().includes(q));
    $('#al', m.el).innerHTML = list.length ? list.map((a) => `<div class="li" data-pkg="${esc(a)}">${icon('package')}<span class="nm">${esc(a)}</span>
      <span class="acts"><button class="btn sm" data-a="launch">${icon('play')}Mở</button><button class="btn sm" data-a="stopapp">${icon('stop')}Dừng</button>
      <button class="btn sm" data-a="clearapp">${icon('eraser')}Xoá dữ liệu</button><button class="btn sm danger" data-a="uninstall">${icon('trash')}Gỡ</button></span></div>`).join('')
      : '<div class="li">Không có ứng dụng (người dùng cài) nào.</div>';
  };
  $('#af', m.el).oninput = render;
  const run = async (op, pkg) => {
    const ids2 = targetIds();
    if (op === 'uninstall' && !await confirmBox('Gỡ cài đặt', `Gỡ "${esc(pkg)}" trên ${ids2.length} máy?`, 'Gỡ', true)) return;
    if (op === 'clearapp' && !await confirmBox('Xoá dữ liệu', `Xoá toàn bộ dữ liệu của "${esc(pkg)}" trên ${ids2.length} máy?`, 'Xoá', true)) return;
    const titles = { launch: 'Mở', stopapp: 'Dừng', clearapp: 'Xoá dữ liệu', uninstall: 'Gỡ' };
    await batch(op, ids2, { pkg }, `${titles[op]} ${pkg}`, { openResults: op === 'uninstall' || op === 'clearapp' });
    if (op === 'uninstall') { apps = apps.filter((a) => a !== pkg); render(); }
  };
  $('#al', m.el).onclick = (e) => {
    const b = e.target.closest('[data-a]');
    if (b) run(b.dataset.a, b.closest('[data-pkg]').dataset.pkg);
  };
  $('#aOpen', m.el).onclick = () => { const p = $('#af', m.el).value.trim(); if (p) run('launch', p); };
  try {
    apps = (await api('/api/apps', { id: src.id })).apps;
    render();
  } catch (e) { $('#al', m.el).innerHTML = `<div class="li">${esc(e.message)}</div>`; }
}

async function dlgFiles(d) {
  let cur = lsGet('filesPath', '/sdcard/');
  const m = modal({
    title: `Tệp — #${d.num} ${displayName(d)}`,
    wide: true,
    body: `<div class="pathbar"><button class="btn icon" id="fUp" title="Lên thư mục cha">${icon('up')}</button><input class="inp" id="fPath"><button class="btn icon" id="fGo" title="Làm mới">${icon('refresh')}</button>
      <button class="btn primary" id="fUpl">${icon('upload')} Tải lên đây</button></div>
      <label class="check-row"><input type="checkbox" id="fAll"> Khi tải lên: gửi tới tất cả máy đang chọn (${S.selected.size}) thay vì chỉ máy này</label>
      <div class="list" id="fl"><div class="li">Đang tải…</div></div>`,
  });
  const load = async (p) => {
    $('#fl', m.el).innerHTML = '<div class="li">Đang tải…</div>';
    try {
      const r = await api('/api/files', { id: d.id, path: p });
      cur = r.path;
      lsSet('filesPath', cur);
      $('#fPath', m.el).value = cur;
      $('#fl', m.el).innerHTML = r.items.length ? r.items.map((it) => `<div class="li ${it.dir ? 'dir' : ''}" data-name="${esc(it.name)}" data-dir="${it.dir ? 1 : 0}">
        ${icon(it.dir ? 'folder' : 'file')}<span class="nm">${esc(it.name)}</span>
        <span class="meta">${it.dir ? '' : fmtSize(it.size)}</span><span class="meta">${it.mtime ? new Date(it.mtime).toLocaleString('vi-VN') : ''}</span>
        <span class="acts">${it.dir ? '' : `<button class="btn sm icon" data-fa="dl" title="Tải về">${icon('download')}</button>`}<button class="btn sm icon danger" data-fa="rm" title="Xoá">${icon('trash')}</button></span></div>`).join('')
        : '<div class="li">(Thư mục trống)</div>';
    } catch (e) {
      $('#fl', m.el).innerHTML = `<div class="li">${esc(e.message)}</div>`;
    }
  };
  $('#fGo', m.el).onclick = () => load($('#fPath', m.el).value.trim());
  $('#fPath', m.el).onkeydown = (e) => { if (e.key === 'Enter') { e.stopPropagation(); e.preventDefault(); load(e.target.value.trim()); } };
  $('#fUp', m.el).onclick = () => { const p = cur.replace(/\/$/, ''); load(p.slice(0, p.lastIndexOf('/') + 1) || '/'); };
  $('#fl', m.el).onclick = async (e) => {
    const li = e.target.closest('.li[data-name]');
    if (!li) return;
    const full = cur + li.dataset.name;
    const fa = e.target.closest('[data-fa]');
    if (fa && fa.dataset.fa === 'dl') {
      const a = document.createElement('a'); a.href = `/api/pull?id=${encodeURIComponent(d.id)}&path=${encodeURIComponent(full)}`; a.click();
    } else if (fa && fa.dataset.fa === 'rm') {
      if (await confirmBox('Xoá', `Xoá "${esc(full)}" trên máy này?`, 'Xoá', true)) {
        try { await api('/api/delete', { id: d.id, path: full }); load(cur); } catch (er) { toast(er.message, 'err'); }
      }
    } else if (li.dataset.dir === '1' && e.target.closest('.nm, svg')) load(full + '/');
  };
  $('#fUpl', m.el).onclick = () => {
    const inp = $('#fileAny');
    inp.onchange = async () => {
      const files = [...inp.files]; inp.value = '';
      const ids = $('#fAll', m.el).checked ? selIds() : [d.id];
      if (!ids.length) return;
      const ups = await uploadAll(files).catch((er) => { toast(er.message, 'err'); return []; });
      for (const u of ups) await batch('push', ids, { token: u.token, dir: cur, name: u.name }, `Gửi ${u.name}`);
      setTimeout(() => load(cur), 1500);
    };
    inp.click();
  };
  load(cur);
}

function dlgInfo(d) {
  const rows = [
    ['Số thứ tự', d.num], ['Tên', d.label || '—'], ['Model', `${d.manufacturer} ${d.model}`], ['Android', `${d.android} (SDK ${d.sdk})`],
    ['Serial phần cứng', d.id], ['Kết nối', d.transports.map((x) => `${x.type.toUpperCase()}: ${x.serial}`).join('<br>')],
    ['Đang dùng', d.transport ? d.transport.toUpperCase() + ' · ' + esc(d.serial) : '—'], ['IP WiFi', d.ip || '—'],
    ['Pin', d.battery != null ? `${d.battery}%${d.charging ? ' (đang sạc)' : ''}${d.temp ? ' · ' + d.temp + '°C' : ''}` : '—'],
    ['Màn hình', d.screenOn == null ? '—' : d.screenOn ? 'Đang bật' : 'Đang tắt'], ['Root', d.root ? 'Có' : 'Không'],
    ['Luồng lưới', d.w ? `${d.w}×${d.h}` : '—'], ['Nhóm', d.groups.join(', ') || '—'], ['Trạng thái', `${d.status}${d.error ? ' — ' + esc(d.error) : ''}`],
  ];
  modal({
    title: `Thông tin #${d.num}`,
    body: `<div class="kv">${rows.map(([k, v]) => `<div>${k}</div><div>${typeof v === 'string' && v.includes('<br>') ? v : (k === 'Kết nối' || k === 'Trạng thái' || k === 'Đang dùng' ? v : esc(v))}</div>`).join('')}</div>`,
    actions: [
      { label: `${icon('refresh')} Cập nhật`, onClick: () => { sendWs({ t: 'info', v: [d.vid] }); toast('Đang cập nhật…'); } },
      { label: 'Đóng', primary: true },
    ],
  });
}

function dlgGroup(ids, remove = false) {
  if (!ids.length) return;
  const m = modal({
    title: remove ? `Bỏ ${ids.length} máy khỏi nhóm` : `Thêm ${ids.length} máy vào nhóm`,
    body: `<label class="field"><span>${remove ? 'Chọn nhóm' : 'Chọn nhóm có sẵn hoặc nhập tên nhóm mới'}</span>
      <input class="inp" id="gname" list="glist" placeholder="vd: Nhóm A"></label>
      <datalist id="glist">${S.groups.map((g) => `<option value="${esc(g)}">`).join('')}</datalist>
      <div style="display:flex;gap:6px;flex-wrap:wrap">${S.groups.map((g) => `<button class="btn sm" data-g="${esc(g)}">${icon('tag')}${esc(g)}</button>`).join('')}</div>`,
    actions: [{ label: 'Huỷ' }, {
      label: remove ? 'Bỏ khỏi nhóm' : 'Thêm', primary: true, onClick: async () => {
        const name = $('#gname', m.el).value.trim();
        if (!name) return false;
        await api('/api/group', { action: remove ? 'remove' : 'add', name, ids });
        toast(remove ? 'Đã bỏ khỏi nhóm' : `Đã thêm vào nhóm "${name}"`, 'ok');
      },
    }],
  });
  m.el.querySelectorAll('[data-g]').forEach((b) => { b.onclick = () => { $('#gname', m.el).value = b.dataset.g; }; });
}

async function dlgSettings() {
  const st = (await api('/api/state')).settings;
  const presets = {
    light: { thumbMaxSize: 360, thumbBitRate: 300000, thumbFps: 10 },
    balanced: { thumbMaxSize: 480, thumbBitRate: 600000, thumbFps: 15 },
    sharp: { thumbMaxSize: 720, thumbBitRate: 1500000, thumbFps: 24 },
  };
  const m = modal({
    title: 'Cài đặt',
    wide: true,
    body: `
      <div class="field"><span>Chất lượng lưới (xem nhiều máy) — mức nhẹ giúp chạy được 100 máy</span>
        <div style="display:flex;gap:6px"><button class="btn sm" data-pre="light">Siêu nhẹ (360p/10fps)</button><button class="btn sm" data-pre="balanced">Cân bằng (480p/15fps)</button><button class="btn sm" data-pre="sharp">Rõ nét (720p/24fps)</button></div></div>
      <div class="field-row">
        <label class="field"><span>Độ phân giải tối đa (px cạnh dài)</span><input class="inp" type="number" id="thumbMaxSize" step="8" min="160" max="1920"></label>
        <label class="field"><span>Bitrate (kbps)</span><input class="inp" type="number" id="thumbBitRate" min="100" max="20000"></label>
        <label class="field"><span>FPS tối đa</span><input class="inp" type="number" id="thumbFps" min="1" max="60"></label>
      </div>
      <div class="field"><span>Màn hình lớn (1 máy)</span></div>
      <div class="field-row">
        <label class="field"><span>Độ phân giải tối đa</span><input class="inp" type="number" id="hqMaxSize" step="8" min="320" max="2560"></label>
        <label class="field"><span>Bitrate (kbps)</span><input class="inp" type="number" id="hqBitRate" min="500" max="40000"></label>
        <label class="field"><span>FPS tối đa</span><input class="inp" type="number" id="hqFps" min="5" max="60"></label>
      </div>
      <div class="field-row">
        <label class="field"><span>Ưu tiên kết nối khi máy có cả USB và WiFi</span><select class="inp" id="preferTransport"><option value="usb">USB (ổn định hơn)</option><option value="wifi">WiFi</option></select></label>
        <label class="field"><span>Số máy khởi động luồng cùng lúc</span><input class="inp" type="number" id="maxParallelStart" min="1" max="30"></label>
        <label class="field"><span>Số máy chạy thao tác cùng lúc (cài APK…)</span><input class="inp" type="number" id="maxParallelJobs" min="1" max="50"></label>
      </div>
      <label class="field"><span>Thư mục lưu ảnh chụp</span><input class="inp" id="screenshotDir"></label>
      <label class="check-row"><input type="checkbox" id="screenOffOnConnect"> Tự tắt màn hình điện thoại khi kết nối (vẫn xem &amp; điều khiển được, tiết kiệm pin — thoát app màn hình tự bật lại)</label>
      <label class="check-row"><input type="checkbox" id="powerOnConnect"> Đánh thức điện thoại khi kết nối</label>
      <label class="check-row"><input type="checkbox" id="autoReconnectWifi"> Tự kết nối lại các máy WiFi đã từng kết nối</label>
      <label class="field"><span>Giải mã video trên máy tính</span><select class="inp" id="decHw"><option value="prefer-software">CPU (khuyên dùng — chạy được 100 máy)</option><option value="no-preference">Tự động</option><option value="prefer-hardware">GPU</option></select></label>
      <div class="note help-list">
        <div>Thay đổi chất lượng lưới sẽ kết nối lại luồng của tất cả máy.</div>
        <div>Chuột trên màn hình nhỏ = ngón tay (chạm, vuốt, nhấn giữ).</div>
        <div>Chuột phải: menu (Phóng to, Cài APK, Nhập/Xuất tệp, Lệnh ADB, Cài đặt).</div>
        <div>Nút bên chuột: Quay lại · Chuột giữa: Home.</div>
        <div>Shift + lăn chuột: cuộn trong điện thoại · Alt + kéo: thu phóng 2 ngón.</div>
        <div>Gõ phím khi trỏ chuột trên ô: chữ được gửi vào máy đó.</div>
        <div>Click: chọn 1 máy · Ctrl + click: thêm/bớt máy vào nhóm · Kéo khung: chọn nhiều máy.</div>
        <div>Khi chọn từ 2 máy, thao tác trên một máy trong nhóm sẽ tự đồng bộ cho cả nhóm (F3: tắt/bật tạm).</div>
        <div>Ctrl + A: chọn tất cả · Esc: bỏ chọn · Ctrl + V: dán vào các máy đang chọn.</div>
      </div>`,
    actions: [{ label: 'Huỷ' }, {
      label: 'Lưu', primary: true, onClick: async () => {
        const g = (id) => $('#' + id, m.el);
        const body = {
          thumbMaxSize: +g('thumbMaxSize').value, thumbBitRate: +g('thumbBitRate').value * 1000, thumbFps: +g('thumbFps').value,
          hqMaxSize: +g('hqMaxSize').value, hqBitRate: +g('hqBitRate').value * 1000, hqFps: +g('hqFps').value,
          preferTransport: g('preferTransport').value, maxParallelStart: +g('maxParallelStart').value, maxParallelJobs: +g('maxParallelJobs').value,
          screenshotDir: g('screenshotDir').value.trim(), powerOnConnect: g('powerOnConnect').checked, screenOffOnConnect: g('screenOffOnConnect').checked, autoReconnectWifi: g('autoReconnectWifi').checked,
        };
        const hw = g('decHw').value;
        if (hw !== S.decoderHw) {
          S.decoderHw = hw; lsSet('decoderHw2', hw);
          for (const t of S.tiles.values()) { if (t.decoder) { t.decoder.close(); t.decoder = null; } }
          S.subsSent = ''; updateSubs(true);
        }
        await api('/api/settings', body);
        toast('Đã lưu cài đặt', 'ok');
      },
    }],
  });
  const set = (id, v) => { const el = $('#' + id, m.el); if (el.type === 'checkbox') el.checked = !!v; else el.value = v; };
  for (const k of ['thumbMaxSize', 'thumbFps', 'hqMaxSize', 'hqFps', 'preferTransport', 'maxParallelStart', 'maxParallelJobs', 'screenshotDir', 'powerOnConnect', 'screenOffOnConnect', 'autoReconnectWifi']) set(k, st[k]);
  set('thumbBitRate', Math.round(st.thumbBitRate / 1000));
  set('hqBitRate', Math.round(st.hqBitRate / 1000));
  set('decHw', S.decoderHw);
  m.el.querySelectorAll('[data-pre]').forEach((b) => {
    b.onclick = () => { const p = presets[b.dataset.pre]; set('thumbMaxSize', p.thumbMaxSize); set('thumbBitRate', p.thumbBitRate / 1000); set('thumbFps', p.thumbFps); };
  });
}

// ---------------- kết nối ----------------
$('#btnConnect').onclick = async () => {
  const addr = $('#connAddr').value.trim();
  if (!addr) { toast('Nhập địa chỉ IP (vd 192.168.1.10:5555)', 'err'); return; }
  const r = await api('/api/connect', { addr }).catch((e) => ({ ok: false, msg: e.message }));
  toast(r.msg || (r.ok ? 'Đã kết nối' : 'Lỗi'), r.ok ? 'ok' : 'err', 5000);
};
$('#connAddr').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('#btnConnect').click(); });
$('#btnToWifi').onclick = toWifi;
$('#btnScan').onclick = async () => {
  const st = await api('/api/state');
  const sub = st.subnets[0]?.subnet || '192.168.1';
  const m = modal({
    title: 'Quét mạng LAN tìm điện thoại',
    body: `<div class="field-row"><label class="field"><span>Dải mạng (3 số đầu)</span><input class="inp" id="sn" value="${esc(sub)}"></label>
      <label class="field"><span>Cổng ADB</span><input class="inp" id="sp" type="number" value="5555"></label></div>
      <div class="note">Máy tính: ${st.subnets.map((s) => `${esc(s.name)} ${esc(s.address)}`).join(' · ') || 'không tìm thấy mạng'}<br>
      Điện thoại cần bật ADB qua TCP (đã từng "USB → WiFi" hoặc root "ADB WiFi cố định").</div>
      <div id="sr"></div>`,
    actions: [{ label: 'Đóng' }, {
      label: `${icon('scan')} Quét`, primary: true, onClick: async () => {
        $('#sr', m.el).innerHTML = '<div class="note">Đang quét 254 địa chỉ…</div>';
        try {
          const r = await api('/api/scan', { subnet: $('#sn', m.el).value.trim(), port: +$('#sp', m.el).value });
          $('#sr', m.el).innerHTML = `<div class="note">Tìm thấy ${r.found} thiết bị mở cổng ADB.</div>` + (r.results.length ? `<div class="results">${r.results.map((x) => `<div class="res ${x.ok ? '' : 'fail'}"><span class="ic">${icon(x.ok ? 'check' : 'x')}</span><span>${esc(x.addr)}</span><pre>${esc(x.msg)}</pre></div>`).join('')}</div>` : '');
        } catch (e) { $('#sr', m.el).innerHTML = `<div class="note warn">${esc(e.message)}</div>`; }
        return false;
      },
    }],
  });
};
$('#btnPair').onclick = () => {
  const m = modal({
    title: 'Ghép nối gỡ lỗi không dây (Android 11+)',
    body: `<div class="note">Trên điện thoại: Tuỳ chọn nhà phát triển → Gỡ lỗi không dây → Ghép nối thiết bị bằng mã ghép nối. Nhập IP:cổng và mã 6 số hiển thị.</div>
      <div class="field-row"><label class="field"><span>IP:cổng ghép nối</span><input class="inp" id="pa" placeholder="192.168.1.20:37099"></label>
      <label class="field"><span>Mã ghép nối</span><input class="inp" id="pc" placeholder="123456"></label></div>
      <label class="field"><span>Sau khi ghép: IP:cổng kết nối (ở màn hình Gỡ lỗi không dây)</span><input class="inp" id="pk" placeholder="192.168.1.20:41234"></label>`,
    actions: [{ label: 'Đóng' }, {
      label: 'Ghép nối & kết nối', primary: true, onClick: async () => {
        const r = await api('/api/pair', { addr: $('#pa', m.el).value, code: $('#pc', m.el).value }).catch((e) => ({ ok: false, msg: e.message }));
        toast(r.msg, r.ok ? 'ok' : 'err', 6000);
        const k = $('#pk', m.el).value.trim();
        if (r.ok && k) {
          const c = await api('/api/connect', { addr: k }).catch((e) => ({ ok: false, msg: e.message }));
          toast(c.msg, c.ok ? 'ok' : 'err', 6000);
        }
        return !r.ok ? false : undefined;
      },
    }],
  });
};
$('#btnOtg').onclick = () => {
  const d = S.devices.get([...S.selected][0]);
  const usbSerial = d ? (d.transports.find((x) => x.type === 'usb') || {}).serial : '';
  const m = modal({
    title: 'Chế độ OTG (giả lập bàn phím/chuột qua USB)',
    body: `<div class="note"><div>OTG (scrcpy --otg) điều khiển điện thoại qua USB như bàn phím và chuột vật lý, không cần bật Gỡ lỗi USB.</div>
      <div>Không có hình ảnh: hãy nhìn trực tiếp màn hình điện thoại. Hữu ích cho máy chưa bật ADB hoặc bị khoá ADB.</div><br>
      <div>Lưu ý Windows: thiết bị không được đồng thời bị ADB chiếm. Nếu báo lỗi truy cập USB, hãy rút các kết nối ADB của máy đó hoặc bấm "Khởi động lại ADB" sau khi tắt Gỡ lỗi USB.</div></div>
      <label class="field"><span>Serial USB (để trống nếu chỉ cắm 1 máy)</span><input class="inp" id="os" value="${esc(usbSerial || '')}"></label>`,
    actions: [{ label: 'Đóng' }, {
      label: `${icon('usb')} Mở OTG`, primary: true, onClick: async () => {
        const r = await api('/api/otg', { serial: $('#os', m.el).value.trim() }).catch((e) => ({ ok: false, msg: e.message }));
        toast(r.msg, r.ok ? 'ok' : 'err');
      },
    }],
  });
};
$('#btnAdbRestart').onclick = async () => {
  if (!await confirmBox('Khởi động lại ADB', 'Tất cả kết nối sẽ bị ngắt và tự kết nối lại sau vài giây.', 'Khởi động lại')) return;
  await api('/api/adb-restart', {}).catch((e) => toast(e.message, 'err'));
  toast('ADB đã khởi động lại', 'ok');
};
$('#btnRemoveOffline').onclick = () => api('/api/remove-offline', {}).then(() => toast('Đã xoá các máy mất kết nối khỏi danh sách'));

// Chẩn đoán: so sánh các kết nối adb nhìn thấy với các ô trên màn hình (tìm máy bị thiếu)
$('#btnDiag').onclick = dlgDiag;
async function dlgDiag() {
  const m = modal({
    title: 'Chẩn đoán kết nối',
    wide: true,
    body: '<div id="dg"><div class="note">Đang tải…</div></div>',
    actions: [
      { label: `${icon('refresh')} Làm mới`, onClick: () => { load(); return false; } },
      { label: `${icon('copy')} Sao chép báo cáo`, onClick: () => { copyReport(); return false; } },
      { label: 'Đóng', primary: true },
    ],
  });
  let last = null;
  const load = async () => {
    try { last = await api('/api/diag'); } catch (e) { $('#dg', m.el).innerHTML = `<div class="note warn">${esc(e.message)}</div>`; return; }
    const r = last;
    const rows = r.rows.map((x) => {
      const tile = x.device && x.device.num ? `#${String(x.device.num).padStart(2, '0')}` : '—';
      const ok = x.state === 'device' && x.device && x.device.status === 'online';
      return `<div class="li"><span class="ic" style="color:${ok ? 'var(--ok)' : 'var(--warn)'}">${icon(ok ? 'check' : 'info')}</span>
        <span class="nm" data-no-i18n style="font-family:Consolas,monospace">${esc(x.addr)}</span>
        <span class="meta" data-no-i18n>${esc(x.state)}${x.tid ? ' · t' + esc(x.tid) : ''}</span>
        <span class="meta" data-no-i18n style="min-width:42px;text-align:right">${tile}</span>
        <span class="meta" style="max-width:340px;overflow:hidden;text-overflow:ellipsis" title="${esc(x.error || '')}">${x.probing ? esc('Đang đọc thông tin…') : esc(x.error || (x.device ? x.device.status : 'Chưa có ô'))}</span></div>`;
    }).join('');
    $('#dg', m.el).innerHTML = `<div class="kv">
        <div>Kết nối ADB</div><div data-no-i18n><b>${r.adbConnections}</b></div>
        <div>Máy sẵn sàng</div><div data-no-i18n><b>${r.deviceConnections}</b></div>
        <div>Số ô trên màn hình</div><div data-no-i18n><b>${r.tiles}</b></div>
        <div>Serial bị trùng</div><div data-no-i18n><b>${r.duplicateSerials}</b></div>
      </div>
      <div class="note" data-no-i18n>${esc(r.adbPath)}<br>${esc(r.adbVersion)}</div>
      <div class="note">Nếu thiếu máy: chụp bảng này hoặc bấm Sao chép báo cáo rồi gửi cho tác giả.</div>
      <div class="list">${rows || '<div class="li">—</div>'}</div>`;
  };
  const copyReport = () => {
    if (!last) return;
    const lines = [`ControlPhone diag — adb=${last.adbConnections} device=${last.deviceConnections} tiles=${last.tiles} dupSerials=${last.duplicateSerials}`,
      last.adbPath, last.adbVersion,
      ...last.rows.map((x) => `${x.addr}\t${x.state}\tt${x.tid}\t${x.device ? (x.device.num ? '#' + x.device.num : x.device.id) + ' ' + x.device.status : '-'}\t${x.error || ''}`)];
    navigator.clipboard.writeText(lines.join('\n')).then(() => toast('Đã sao chép báo cáo', 'ok')).catch(() => toast('Không đọc được clipboard', 'err'));
  };
  load();
}
$('#btnNewGroup').onclick = async () => {
  const name = await promptBox('Tạo nhóm', 'Tên nhóm', '', 'vd: Nhóm A');
  if (name && name.trim()) {
    await api('/api/group', { action: 'create', name: name.trim() });
    if (S.selected.size && await confirmBox('Thêm máy', `Thêm ${S.selected.size} máy đang chọn vào nhóm "${esc(name.trim())}"?`, 'Thêm')) {
      await api('/api/group', { action: 'add', name: name.trim(), ids: [...S.selected] });
    }
  }
};

// ====================================================================
// Tiến trình thao tác
// ====================================================================
const pendingOpen = new Set();
function onTask(task) {
  S.tasks.set(task.id, task);
  let el = document.querySelector(`.task[data-id="${task.id}"]`);
  if (!el) {
    el = h(`<div class="task" data-id="${task.id}"><div class="t1"><span class="tt"></span><button class="x" title="Đóng">${icon('x')}</button></div><div class="bar"><i></i></div><div class="t2"></div></div>`);
    $('#tasks').prepend(el);
    el.onclick = (e) => {
      if (e.target.closest('.x')) { el.remove(); return; }
      showTaskResults(S.tasks.get(task.id));
    };
    while ($('#tasks').children.length > 6) $('#tasks').lastElementChild.remove();
  }
  $('.tt', el).textContent = task.title;
  $('.bar i', el).style.width = (task.total ? (task.done / task.total) * 100 : 100) + '%';
  $('.t2', el).innerHTML = `<span>${task.done}/${task.total}</span><span class="ok">✓ ${task.ok}</span>${task.fail ? `<span class="bad">✗ ${task.fail}</span>` : ''}<span style="margin-left:auto">${task.finished ? 'Xong · bấm để xem' : 'Đang chạy…'}</span>`;
  el.classList.toggle('done', task.finished);
  el.classList.toggle('has-fail', task.fail > 0);
  if (task.finished) {
    if (pendingOpen.has(task.id)) { pendingOpen.delete(task.id); showTaskResults(task); }
    if (task.op === 'screenshot' && task.ok) toast(`Đã lưu ${task.ok} ảnh chụp`, 'ok');
    if (!task.fail) setTimeout(() => el.remove(), 8000);
  }
}

function showTaskResults(task) {
  if (!task) return;
  const res = [...task.results].sort((a, b) => (a.ok - b.ok) || a.name.localeCompare(b.name, 'vi', { numeric: true }));
  modal({
    title: `${task.title} — ${task.ok}/${task.total} thành công`,
    wide: true,
    body: `<div class="results">${res.map((r) => `<div class="res ${r.ok ? '' : 'fail'}"><span class="ic">${icon(r.ok ? 'check' : 'x')}</span><span>${esc(r.name)}</span><pre>${esc(r.msg || (r.ok ? 'OK' : 'Lỗi'))}</pre></div>`).join('') || '<div class="res">Không có máy nào.</div>'}</div>`,
    actions: [
      ...(task.op === 'screenshot' ? [{ label: `${icon('folder')} Mở thư mục ảnh`, onClick: () => { api('/api/open-folder', { which: 'screenshots' }); return false; } }] : []),
      ...(task.fail ? [{ label: 'Chọn các máy lỗi', onClick: () => { S.selected = new Set(task.results.filter((r) => !r.ok).map((r) => r.id)); refreshSelectionClasses(); } }] : []),
      { label: 'Đóng', primary: true },
    ],
  });
}

// ====================================================================
// Kéo thả tệp
// ====================================================================
let dragDepth = 0;
addEventListener('dragenter', (e) => {
  if (!e.dataTransfer || ![...e.dataTransfer.types].includes('Files')) return;
  dragDepth++;
  if (!$('.drop-ov')) document.body.appendChild(h(`<div class="drop-ov">Thả tệp: .apk → cài đặt · tệp khác → gửi vào /sdcard/Download (${S.selected.size} máy đang chọn)</div>`));
});
addEventListener('dragleave', () => { dragDepth = Math.max(0, dragDepth - 1); if (!dragDepth) $('.drop-ov')?.remove(); });
addEventListener('dragover', (e) => e.preventDefault());
addEventListener('drop', (e) => {
  e.preventDefault();
  dragDepth = 0;
  $('.drop-ov')?.remove();
  const files = [...(e.dataTransfer?.files || [])];
  if (!files.length) return;
  if (!selIds().length) return;
  const apks = files.filter((f) => /\.apk$/i.test(f.name));
  const others = files.filter((f) => !/\.apk$/i.test(f.name));
  if (apks.length) installApks(apks);
  if (others.length) dlgPush(others);
});

// ====================================================================
// Khởi động
// ====================================================================
hydrateIcons(document);
applyTheme(lsGet('theme', 'dark'));
if (lsGet('sidebar', true) === false) $('#app').classList.add('no-sidebar');
setTileW(S.tileW);
if (!webCodecsSupported()) {
  modal({ title: 'Trình duyệt không hỗ trợ', body: '<div>Cần Microsoft Edge hoặc Google Chrome bản mới (hỗ trợ WebCodecs) để xem màn hình. Hãy mở bằng ControlPhone.bat.</div>' });
}
api('/api/state').then((st) => {
  S.settings = st.settings;
  $('#sideFoot').innerHTML = `ADB: ${esc(st.adb)}<br>scrcpy-server 5.0`;
  $('#wifiHist').innerHTML = (st.wifiHistory || []).map((x) => `<option value="${esc(x)}">`).join('');
}).catch(() => {});
connectWs();
window.__cp = { S, viewer }; // hỗ trợ chẩn đoán từ DevTools
