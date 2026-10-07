// Đóng gói bản phát hành "tải về là chạy" cho Windows: dist/ControlPhone-v<version>-win64.zip
// Gồm: mã nguồn, node_modules, scrcpy (vendor) và node.exe di động (không cần cài Node.js).
// Chạy: npm run pack
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
const name = `ControlPhone-v${pkg.version}-win64`;
const DIST = path.join(ROOT, 'dist');
const STAGE = path.join(DIST, name);

const copy = (rel, dest = rel) => {
  const src = path.join(ROOT, rel);
  if (!fs.existsSync(src)) throw new Error('Thiếu: ' + rel);
  fs.cpSync(src, path.join(STAGE, dest), { recursive: true });
};

fs.rmSync(STAGE, { recursive: true, force: true });
fs.mkdirSync(STAGE, { recursive: true });

for (const f of ['server', 'public', 'ControlPhone.bat', 'package.json', 'README.md', 'README.vi.md', 'LICENSE', 'NOTICE.md']) copy(f);
copy('tools/setup.js');
copy('node_modules/ws');
copy('vendor/scrcpy-win64-v5.0');

// node.exe di động (bản đang dùng để đóng gói) + giấy phép của Node.js
const nodeExe = process.execPath;
fs.mkdirSync(path.join(STAGE, 'node'), { recursive: true });
fs.copyFileSync(nodeExe, path.join(STAGE, 'node', 'node.exe'));
const nodeLicense = path.join(path.dirname(nodeExe), 'LICENSE');
if (fs.existsSync(nodeLicense)) fs.copyFileSync(nodeLicense, path.join(STAGE, 'node', 'LICENSE'));

const zip = path.join(DIST, name + '.zip');
fs.rmSync(zip, { force: true });
// tar.exe có sẵn trên Windows 10+ (nhanh, ổn định); Compress-Archive của PowerShell 5.1 đôi khi treo
try {
  execFileSync('tar', ['-a', '-c', '-f', zip, '-C', DIST, name], { stdio: 'inherit' });
} catch (e) {
  fs.rmSync(zip, { force: true });
  execFileSync('powershell', ['-NoProfile', '-Command', `Compress-Archive -Path '${STAGE}' -DestinationPath '${zip}' -CompressionLevel Optimal`], { stdio: 'inherit' });
}
console.log(`Đã tạo ${zip} (${(fs.statSync(zip).size / 1048576).toFixed(1)} MB) — Node ${process.version}`);
