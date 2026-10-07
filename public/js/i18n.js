// Đa ngôn ngữ: văn bản gốc trong code là tiếng Việt; các file public/js/lang/<mã>.js chứa bản dịch
// theo dạng { "câu tiếng Việt": "bản dịch" }. Câu có biến dùng {0}, {1}... và được khớp theo mẫu.
// Bộ dịch tự động dịch mọi văn bản trên giao diện (kể cả thông báo từ máy chủ) qua MutationObserver.

export const LANGS = [
  { code: 'vi', name: 'Tiếng Việt' },
  { code: 'en', name: 'English' },
  { code: 'zh-CN', name: '简体中文' },
  { code: 'zh-TW', name: '繁體中文' },
  { code: 'de', name: 'Deutsch' },
  { code: 'fr', name: 'Français' },
];

let lang = 'vi';
let dict = null; // Map câu gốc → bản dịch
let patterns = []; // [{ re, out }]

function detectLang() {
  try {
    const saved = localStorage.getItem('cp.lang');
    if (saved && LANGS.some((l) => l.code === saved)) return saved;
  } catch (_) { /* bỏ qua */ }
  const nav = (navigator.language || 'en').toLowerCase();
  if (nav.startsWith('vi')) return 'vi';
  if (nav.startsWith('zh')) return /tw|hk|mo|hant/.test(nav) ? 'zh-TW' : 'zh-CN';
  if (nav.startsWith('de')) return 'de';
  if (nav.startsWith('fr')) return 'fr';
  return 'en';
}

export function currentLang() { return lang; }

export function setLang(code) {
  try { localStorage.setItem('cp.lang', code); } catch (_) { /* bỏ qua */ }
  location.reload();
}

const escRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function compile(src) {
  dict = new Map();
  patterns = [];
  for (const [k, v] of Object.entries(src)) {
    if (!v) continue;
    if (/\{\d+\}/.test(k)) {
      // mẫu có biến: "Đã chọn {0} máy" → /^Đã chọn (.+?) máy$/
      const order = [];
      const re = new RegExp('^' + escRe(k).replace(/\\\{(\d+)\\\}/g, (_, n) => { order.push(n); return '([\\s\\S]*?)'; }) + '$');
      patterns.push({ re, order, out: v, len: k.length });
    }
    dict.set(k, v);
  }
  patterns.sort((a, b) => b.len - a.len); // mẫu dài (cụ thể) trước
}

/** Dịch một chuỗi tiếng Việt; trả về nguyên văn nếu không có bản dịch. t('Đã chọn {0} máy', 3) */
export function t(text, ...args) {
  let out = text;
  if (dict) {
    const hit = dict.get(text);
    if (hit) out = hit;
    else if (!args.length) out = translateDynamic(text) ?? text;
  }
  return args.length ? out.replace(/\{(\d+)\}/g, (m, i) => (args[i] !== undefined ? args[i] : m)) : out;
}

function translateDynamic(text) {
  for (const p of patterns) {
    const m = p.re.exec(text);
    if (!m) continue;
    const vals = {};
    p.order.forEach((n, i) => { vals[n] = m[i + 1]; });
    return p.out.replace(/\{(\d+)\}/g, (s, n) => (vals[n] !== undefined ? translateValue(vals[n]) : s));
  }
  return null;
}

// giá trị chèn vào mẫu cũng có thể là câu tiếng Việt (vd. lỗi lồng nhau)
function translateValue(v) {
  if (!VI.test(v)) return v;
  const [, lead, core, trail] = /^(\s*)([\s\S]*?)(\s*)$/.exec(v); // giữ khoảng trắng 2 đầu
  const tr = dict.get(core) || translateDynamic(core);
  return tr ? lead + tr + trail : v;
}

const VI = /[ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ]/i;

function translateString(s) {
  if (!s) return null;
  const [, lead, core, trail] = /^(\s*)([\s\S]*?)(\s*)$/.exec(s);
  const norm = core.replace(/\s+/g, ' ');
  // khớp chính xác: dịch cả chuỗi không dấu; khớp theo mẫu: chỉ với chuỗi có dấu tiếng Việt
  const tr = dict.get(norm) ?? (VI.test(norm) ? translateDynamic(norm) : null);
  return tr == null ? null : lead + tr + trail;
}

const ATTRS = ['title', 'placeholder', 'aria-label'];
const SKIP = new Set(['SCRIPT', 'STYLE', 'TEXTAREA', 'INPUT', 'CANVAS']);

function translateNode(node) {
  if (node.nodeType === Node.TEXT_NODE) {
    const p = node.parentNode;
    if (!p || SKIP.has(p.nodeName) || (p.closest && p.closest('[data-no-i18n]'))) return;
    const tr = translateString(node.nodeValue);
    if (tr != null && tr !== node.nodeValue) node.nodeValue = tr;
    return;
  }
  if (node.nodeType !== Node.ELEMENT_NODE || node.hasAttribute('data-no-i18n')) return;
  translateAttrs(node); // placeholder/title của cả ô nhập
  if (SKIP.has(node.nodeName)) return;
  for (const c of node.childNodes) translateNode(c);
}

function translateAttrs(el) {
  for (const a of ATTRS) {
    const v = el.getAttribute && el.getAttribute(a);
    if (!v) continue;
    const tr = translateString(v);
    if (tr != null && tr !== v) el.setAttribute(a, tr);
  }
}

/** Nạp ngôn ngữ đã chọn và bật bộ dịch tự động cho toàn trang. */
export async function initI18n() {
  lang = detectLang();
  document.documentElement.lang = lang;
  if (lang === 'vi') return;
  try {
    const mod = await import(`./lang/${lang}.js`);
    compile(mod.default || {});
  } catch (e) {
    console.warn('Không nạp được ngôn ngữ', lang, e);
    return;
  }
  document.title = t(document.title);
  translateNode(document.body);
  new MutationObserver((muts) => {
    for (const m of muts) {
      if (m.type === 'characterData') translateNode(m.target);
      else if (m.type === 'attributes') translateAttrs(m.target);
      else m.addedNodes.forEach(translateNode);
    }
  }).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ATTRS });
}
