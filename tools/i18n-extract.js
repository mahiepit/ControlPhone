// Trích các câu chữ tiếng Việt hiển thị trên giao diện (để dịch).
// Dùng: node tools/i18n-extract.js  → in danh sách khoá còn thiếu trong public/js/lang/*.js
// Cần acorn: npm i -D acorn acorn-walk (chỉ dùng khi phát triển)
'use strict';
const fs = require('fs');
const path = require('path');
const acorn = require('acorn');
const walk = require('acorn-walk');

const ROOT = path.join(__dirname, '..');
const VI = /[ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ]/i;
const keys = new Set();

function addSegments(text) {
  // mô phỏng DOM: tách theo thẻ HTML, mỗi đoạn văn bản là 1 khoá (đã cắt khoảng trắng 2 đầu)
  const noTags = text.replace(/<(script|style)[\s\S]*?<\/\1>/gi, '');
  for (const seg of noTags.split(/<[^>]*>/)) {
    const s = seg.replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
    if (s && VI.test(s)) keys.add(s);
  }
  // thuộc tính title / placeholder trong HTML
  for (const m of text.matchAll(/\b(?:title|placeholder)="([^"$]*?)"/g)) if (VI.test(m[1])) keys.add(m[1].trim());
}

function fromJs(file, { echoOnly = false } = {}) {
  const src = fs.readFileSync(file, 'utf8');
  const ast = acorn.parse(src, { ecmaVersion: 'latest', sourceType: file.includes('public') ? 'module' : 'script', allowHashBang: true });
  const handle = (str) => {
    if (!VI.test(str)) return;
    // lệnh shell: chỉ lấy phần echo "..."
    const echos = [...str.matchAll(/echo "([^"$]*?)"/g)].map((m) => m[1]);
    if (!/</.test(str) && /\b(settings|svc|am |pm |dumpsys|getprop|su -c|setprop)\b/.test(str)) { echos.forEach(addSegments); return; }
    if (!echoOnly) addSegments(str);
  };
  walk.full(ast, (node) => {
    if (node.type === 'Literal' && typeof node.value === 'string') handle(node.value);
    if (node.type === 'TemplateLiteral') {
      // ${...} → {0},{1}... (khớp theo mẫu khi hiển thị)
      let s = '';
      node.quasis.forEach((q, i) => { s += q.value.cooked; if (i < node.expressions.length) s += `{${i}}`; });
      handle(s);
    }
  });
}

for (const f of ['public/js/app.js', 'public/js/ui.js', 'public/js/decoder.js']) fromJs(path.join(ROOT, f));
for (const f of fs.readdirSync(path.join(ROOT, 'server'))) fromJs(path.join(ROOT, 'server', f));
addSegments(fs.readFileSync(path.join(ROOT, 'public/index.html'), 'utf8'));
// chuỗi tiếng Việt không dấu (bộ lọc dấu không bắt được)
for (const k of ['Theo pin', 'Pin']) keys.add(k);

// biến ở đầu/cuối thường là icon/HTML → thêm bản không có biến (DOM chỉ chứa phần chữ)
for (const k of [...keys]) {
  const stripped = k.replace(/^(\{\d+\}\s*)+/, '').replace(/(\s*\{\d+\})+$/, '').trim();
  if (stripped && stripped !== k && VI.test(stripped) && !/\{\d+\}/.test(stripped)) keys.add(stripped);
}
// bỏ các khoá chỉ là placeholder / ký hiệu
const list = [...keys].filter((k) => k.replace(/\{\d+\}/g, '').trim().length > 1).sort();

const out = process.argv[2];
if (out) {
  fs.writeFileSync(out, JSON.stringify(list, null, 1));
  console.log(`${list.length} khoá → ${out}`);
} else {
  const langDir = path.join(ROOT, 'public/js/lang');
  for (const f of fs.existsSync(langDir) ? fs.readdirSync(langDir) : []) {
    const txt = fs.readFileSync(path.join(langDir, f), 'utf8');
    const dict = JSON.parse(txt.slice(txt.indexOf('{'), txt.lastIndexOf('}') + 1).replace(/,\s*}$/, '}'));
    const miss = list.filter((k) => !(k in dict));
    console.log(`${f}: ${Object.keys(dict).length} khoá, thiếu ${miss.length}`);
    miss.slice(0, 30).forEach((k) => console.log('   - ' + k));
  }
  if (!fs.existsSync(langDir)) console.log(list.length + ' khoá');
}
