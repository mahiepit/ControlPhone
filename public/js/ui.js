// Tiện ích giao diện: hộp thoại, thông báo, menu ngữ cảnh
import { icon } from './icons.js';

export function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function h(html) {
  const t = document.createElement('template');
  t.innerHTML = html.trim();
  hydrateIcons(t.content);
  return t.content.firstElementChild;
}

export function hydrateIcons(root) {
  root.querySelectorAll('i[data-icon]').forEach((el) => {
    el.outerHTML = icon(el.dataset.icon);
  });
}

export function toast(msg, type = '', ms = 3200) {
  const box = document.getElementById('toasts');
  const el = document.createElement('div');
  el.className = 'toast ' + type;
  el.textContent = msg;
  box.appendChild(el);
  setTimeout(() => el.remove(), ms);
}

let openModals = 0;
export function modalOpen() { return openModals > 0; }

/**
 * modal({title, body: string|Element, wide, actions:[{label, primary, danger, onClick(close) -> false để giữ mở}]})
 */
export function modal({ title, body, wide = false, actions = [], onClose }) {
  const bg = h(`<div class="modal-bg"><div class="modal ${wide ? 'wide' : ''}">
    <div class="modal-h"><span>${esc(title)}</span><button class="btn icon ghost x" title="Đóng">${icon('x')}</button></div>
    <div class="modal-b"></div>
    <div class="modal-f"></div></div></div>`);
  const b = bg.querySelector('.modal-b');
  if (typeof body === 'string') { b.innerHTML = body; hydrateIcons(b); } else if (body) b.appendChild(body);
  const f = bg.querySelector('.modal-f');
  if (!actions.length) f.remove();
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    openModals--;
    bg.remove();
    document.removeEventListener('keydown', onKey, true);
    onClose && onClose();
  };
  for (const a of actions) {
    const btn = document.createElement('button');
    btn.className = 'btn' + (a.primary ? ' primary' : '') + (a.danger ? ' danger solid' : '');
    btn.innerHTML = a.label;
    btn.onclick = async () => {
      if (!a.onClick) return close();
      btn.disabled = true;
      try {
        const r = await a.onClick(close, bg);
        if (r !== false) close();
      } finally { btn.disabled = false; }
    };
    f.appendChild(btn);
  }
  const onKey = (e) => {
    if (e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); close(); }
    if (e.key === 'Enter' && e.target.tagName === 'INPUT' && actions.length) {
      const prim = f.querySelector('.btn.primary, .btn.danger');
      if (prim) { e.preventDefault(); prim.click(); }
    }
  };
  document.addEventListener('keydown', onKey, true);
  bg.querySelector('.x').onclick = close;
  bg.addEventListener('mousedown', (e) => { if (e.target === bg) bg.dataset.down = '1'; });
  bg.addEventListener('mouseup', (e) => { if (e.target === bg && bg.dataset.down) close(); delete bg.dataset.down; });
  document.body.appendChild(bg);
  openModals++;
  const first = bg.querySelector('input:not([type=checkbox]):not([type=radio]):not([type=file]), textarea, select');
  if (first) first.focus();
  return { el: bg, body: b, close };
}

export function confirmBox(title, text, okLabel = 'Đồng ý', danger = false) {
  return new Promise((resolve) => {
    let ok = false;
    modal({
      title,
      body: `<div>${text}</div>`,
      actions: [
        { label: 'Huỷ' },
        { label: okLabel, primary: !danger, danger, onClick: () => { ok = true; } },
      ],
      onClose: () => resolve(ok),
    });
  });
}

export function promptBox(title, label, value = '', placeholder = '') {
  return new Promise((resolve) => {
    let result = null;
    const m = modal({
      title,
      body: `<label class="field"><span>${esc(label)}</span><input class="inp" value="${esc(value)}" placeholder="${esc(placeholder)}"></label>`,
      actions: [
        { label: 'Huỷ' },
        { label: 'Lưu', primary: true, onClick: () => { result = m.body.querySelector('input').value; } },
      ],
      onClose: () => resolve(result),
    });
    setTimeout(() => m.body.querySelector('input').select(), 40);
  });
}

let ctxEl = null;
export function closeCtx() {
  if (ctxEl) { ctxEl.remove(); ctxEl = null; }
}

/** items: [{label, icon, onClick, danger}] hoặc '-' */
export function ctxMenu(x, y, items) {
  closeCtx();
  const el = document.createElement('div');
  el.className = 'ctx';
  for (const it of items) {
    if (it === '-') { el.appendChild(document.createElement('hr')); continue; }
    if (!it) continue;
    const b = document.createElement('button');
    if (it.danger) b.className = 'danger';
    b.innerHTML = (it.icon ? icon(it.icon) : '<span style="width:16px"></span>') + `<span>${esc(it.label)}</span>`;
    b.onclick = () => { closeCtx(); it.onClick && it.onClick(); };
    el.appendChild(b);
  }
  document.body.appendChild(el);
  const r = el.getBoundingClientRect();
  el.style.left = Math.min(x, innerWidth - r.width - 8) + 'px';
  el.style.top = Math.max(8, Math.min(y, innerHeight - r.height - 8)) + 'px';
  ctxEl = el;
}

// Một bộ đóng menu duy nhất cho mọi menu: bấm ra ngoài menu đang mở thì đóng.
// (Trước đây mỗi menu tự gắn bộ đóng riêng; mở menu con khiến bộ đóng cũ còn sót lại và
// đóng nhầm menu mới ngay khi nhấn chuột → lệnh trong menu "không ăn".)
document.addEventListener('mousedown', (e) => {
  if (ctxEl && !ctxEl.contains(e.target)) closeCtx();
}, true);

export async function api(path, body) {
  const r = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const j = await r.json().catch(() => ({ ok: false, error: 'Phản hồi không hợp lệ' }));
  if (!r.ok || j.error) throw new Error(j.error || ('HTTP ' + r.status));
  return j;
}

export function fmtSize(n) {
  if (n < 1024) return n + ' B';
  if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
  if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
  return (n / 1073741824).toFixed(2) + ' GB';
}

export function uploadFile(file, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/upload?name=' + encodeURIComponent(file.name));
    xhr.upload.onprogress = (e) => onProgress && e.lengthComputable && onProgress(e.loaded / e.total);
    xhr.onload = () => {
      try {
        const j = JSON.parse(xhr.responseText);
        if (j.ok) resolve(j); else reject(new Error(j.error || 'Tải lên lỗi'));
      } catch (e) { reject(e); }
    };
    xhr.onerror = () => reject(new Error('Tải lên lỗi'));
    xhr.send(file);
  });
}
