// Tải scrcpy chính hãng (server + adb + scrcpy.exe) về thư mục vendor/ và kiểm tra SHA-256.
// Chạy: node tools/setup.js   (ControlPhone.bat tự gọi khi thiếu vendor)
'use strict';
const fs = require('fs');
const path = require('path');
const https = require('https');
const crypto = require('crypto');
const { execFileSync } = require('child_process');

const VERSION = 'v5.0';
const FILE = `scrcpy-win64-${VERSION}.zip`;
const URL = `https://github.com/Genymobile/scrcpy/releases/download/${VERSION}/${FILE}`;
const SHA256 = '44c10d9e82f20ea67227d14d37bf9fbe3603117c5736df3f514544a02ba20a73';

const ROOT = path.join(__dirname, '..');
const VENDOR = path.join(ROOT, 'vendor');
const TARGET = path.join(VENDOR, `scrcpy-win64-${VERSION}`);

function download(url, dest, redirects = 5) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'ControlPhone-setup' } }, (res) => {
      if ([301, 302, 303, 307, 308].includes(res.statusCode) && res.headers.location && redirects > 0) {
        res.resume();
        resolve(download(res.headers.location, dest, redirects - 1));
        return;
      }
      if (res.statusCode !== 200) { reject(new Error(`HTTP ${res.statusCode} — ${url}`)); return; }
      const total = +res.headers['content-length'] || 0;
      let got = 0;
      const out = fs.createWriteStream(dest);
      res.on('data', (d) => {
        got += d.length;
        if (total) process.stdout.write(`\r  ${(got / 1048576).toFixed(1)} / ${(total / 1048576).toFixed(1)} MB`);
      });
      res.pipe(out);
      out.on('finish', () => { process.stdout.write('\n'); resolve(); });
      out.on('error', reject);
    }).on('error', reject);
  });
}

(async () => {
  if (fs.existsSync(path.join(TARGET, 'scrcpy-server'))) {
    console.log(`scrcpy ${VERSION} đã có sẵn: ${TARGET}`);
    return;
  }
  fs.mkdirSync(VENDOR, { recursive: true });
  const zip = path.join(VENDOR, FILE);
  console.log(`Đang tải scrcpy ${VERSION} từ GitHub (Genymobile/scrcpy)...`);
  await download(URL, zip);
  const hash = crypto.createHash('sha256').update(fs.readFileSync(zip)).digest('hex');
  if (hash !== SHA256) {
    fs.unlinkSync(zip);
    throw new Error(`Sai mã SHA-256 (${hash}) — tệp tải về không đúng, đã xoá.`);
  }
  console.log('SHA-256 hợp lệ. Đang giải nén...');
  execFileSync('powershell', ['-NoProfile', '-Command', `Expand-Archive -Force -LiteralPath '${zip}' -DestinationPath '${VENDOR}'`], { stdio: 'inherit' });
  fs.unlinkSync(zip);
  console.log(`Xong: ${TARGET}`);
})().catch((e) => {
  console.error('Lỗi cài đặt scrcpy:', e.message);
  process.exit(1);
});
