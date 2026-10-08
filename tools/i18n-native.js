// Liệt kê câu tiếng Việt trong bản native (native/**/*.cs, *.xaml) còn thiếu trong public/js/lang/*.js
// Dùng: node tools/i18n-native.js [--json out.json]
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const VI = /[ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ]/i;
const keys = new Set();

function walk(dir, out = []) {
  for (const f of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj', '.vs'].includes(f.name)) continue;
    const p = path.join(dir, f.name);
    if (f.isDirectory()) walk(p, out); else if (/\.(cs|xaml)$/.test(f.name)) out.push(p);
  }
  return out;
}

function unescapeCs(s) {
  return s.replace(/\\(["\\nrt])/g, (m, c) => ({ n: '\n', r: '\r', t: '\t' }[c] || c));
}

for (const file of walk(path.join(ROOT, 'native'))) {
  const src = fs.readFileSync(file, 'utf8');
  if (file.endsWith('.xaml')) {
    for (const m of src.matchAll(/\b(?:Text|Content|ToolTip|Tag|Title|Header)="([^"]*)"/g)) {
      const v = m[1].replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>');
      if (VI.test(v)) keys.add(v.trim());
    }
    continue;
  }
  // bỏ chú thích
  const code = src.replace(/\/\/[^\n]*/g, (c) => (c.includes('"') && /"[^"]*"/.test(c.split('//')[0]) ? c : ''));
  for (const m of code.matchAll(/(\$?)"((?:[^"\\\n]|\\.)*)"/g)) {
    if (m[1]) continue; // chuỗi nội suy $"…" (không phải câu dịch)
    const v = unescapeCs(m[2]);
    if (!VI.test(v)) continue;
    if (/\b(settings|svc |am |pm |dumpsys|getprop|su -c|setprop|grep|echo)\b/.test(v) && !/^[^a-z]*[A-ZĐ]/.test(v.slice(0, 1))) {
      for (const e of v.matchAll(/echo "([^"$]*?)"/g)) keys.add(e[1]);
      continue;
    }
    if (/^(echo|if \[|settings|dumpsys|ip -f|pm |am |svc |df |grep |wm |uptime)/.test(v)) continue;
    // câu nhiều dòng (ghi chú) → từng dòng là một khoá
    for (const line of v.split('\n')) { const t = line.replace(/^• /, '').trim(); if (t && VI.test(t)) keys.add(t); }
  }
}

const langDir = path.join(ROOT, 'public/js/lang');
const out = {};
for (const f of fs.readdirSync(langDir)) {
  const txt = fs.readFileSync(path.join(langDir, f), 'utf8');
  const dict = JSON.parse(txt.slice(txt.indexOf('{'), txt.lastIndexOf('}') + 1).replace(/,\s*}$/, '}'));
  const miss = [...keys].filter((k) => !(k in dict)).sort();
  out[f] = miss;
  console.log(`${f}: thiếu ${miss.length}`);
}
if (process.argv[2] === '--json') fs.writeFileSync(process.argv[3], JSON.stringify(out['en.js'], null, 1));
else out['en.js'].forEach((k) => console.log('   - ' + k));
