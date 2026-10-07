// Client ADB tối giản: nói chuyện trực tiếp với adb server (cổng 5037) qua socket
// để mở shell / localabstract mà không cần sinh thêm tiến trình adb.exe cho mỗi máy.
'use strict';
const net = require('net');
const fs = require('fs');
const path = require('path');
const { execFile, execFileSync } = require('child_process');

const ADB_HOST = '127.0.0.1';
const ADB_PORT = 5037;
const ROOT = path.join(__dirname, '..');
const VENDOR_DIR = path.join(ROOT, 'vendor', 'scrcpy-win64-v5.0');

function resolveAdbPath() {
  if (process.env.ADB && fs.existsSync(process.env.ADB)) return process.env.ADB;
  // Ưu tiên adb trong PATH (tránh xung đột phiên bản adb server đang chạy)
  try {
    const out = execFileSync('where', ['adb'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
    const first = out.split(/\r?\n/).find(Boolean);
    if (first && fs.existsSync(first.trim())) return first.trim();
  } catch (_) { /* không có trong PATH */ }
  const vendored = path.join(VENDOR_DIR, 'adb.exe');
  if (fs.existsSync(vendored)) return vendored;
  return 'adb';
}

const ADB_PATH = resolveAdbPath();

function encodeCmd(cmd) {
  const body = Buffer.from(cmd, 'utf8');
  return Buffer.concat([Buffer.from(body.length.toString(16).padStart(4, '0'), 'ascii'), body]);
}

/**
 * Mở một service ADB. serial = null cho lệnh host:*.
 * Trả về socket đã ở trạng thái "đã được adb chấp nhận" (paused, dữ liệu dư đã unshift).
 */
function openService(serial, service, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(ADB_PORT, ADB_HOST);
    sock.setNoDelay(true);
    let buf = Buffer.alloc(0);
    let stage = serial ? 0 : 1;
    let finished = false;
    const timer = setTimeout(() => fail(new Error(`adb timeout: ${service}`)), timeoutMs);

    function fail(err) {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      sock.destroy();
      reject(err);
    }
    function done() {
      finished = true;
      clearTimeout(timer);
      sock.pause();
      sock.removeListener('data', onData);
      sock.removeListener('error', fail);
      sock.removeListener('close', onEarlyClose);
      sock.on('error', () => {});
      if (buf.length) sock.unshift(buf);
      resolve(sock);
    }
    function onEarlyClose() { fail(new Error(`adb closed: ${service}`)); }
    function onData(d) {
      buf = buf.length ? Buffer.concat([buf, d]) : d;
      while (!finished) {
        if (buf.length < 4) return;
        const status = buf.toString('ascii', 0, 4);
        if (status === 'OKAY') {
          buf = buf.subarray(4);
          if (stage === 0) {
            stage = 1;
            sock.write(encodeCmd(service));
            continue;
          }
          done();
          return;
        }
        if (status === 'FAIL') {
          if (buf.length < 8) return;
          const len = parseInt(buf.toString('ascii', 4, 8), 16);
          if (buf.length < 8 + len) return;
          fail(new Error(buf.toString('utf8', 8, 8 + len)));
          return;
        }
        fail(new Error('Phản hồi adb không hợp lệ: ' + status));
        return;
      }
    }
    sock.on('data', onData);
    sock.on('error', fail);
    sock.on('close', onEarlyClose);
    sock.on('connect', () => sock.write(encodeCmd(serial ? transportCmd(serial) : service)));
  });
}

function readAll(sock, timeoutMs) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let timer = null;
    if (timeoutMs) {
      timer = setTimeout(() => { sock.destroy(); reject(new Error('Hết thời gian chờ lệnh')); }, timeoutMs);
    }
    sock.on('data', (d) => chunks.push(d));
    sock.on('close', () => { if (timer) clearTimeout(timer); resolve(Buffer.concat(chunks)); });
    sock.on('error', () => {});
    sock.resume();
  });
}

/** Chạy lệnh shell, trả về chuỗi stdout (đã gộp stderr nếu lệnh có 2>&1). */
async function shell(serial, cmd, timeoutMs = 15000) {
  const sock = await openService(serial, 'shell:' + cmd, timeoutMs);
  const out = await readAll(sock, timeoutMs);
  return out.toString('utf8').replace(/\r\n/g, '\n');
}

/** exec: luồng nhị phân sạch (dùng cho screencap, cat file). */
async function execOut(serial, cmd, timeoutMs = 30000) {
  const sock = await openService(serial, 'exec:' + cmd, timeoutMs);
  return readAll(sock, timeoutMs);
}

async function execStream(serial, cmd) {
  const sock = await openService(serial, 'exec:' + cmd);
  sock.resume();
  return sock;
}

/** Chạy adb.exe (cho push/install/connect/pair/tcpip). */
function run(args, timeoutMs = 60000) {
  return new Promise((resolve) => {
    execFile(ADB_PATH, args, { timeout: timeoutMs, maxBuffer: 64 * 1024 * 1024, windowsHide: true }, (err, stdout, stderr) => {
      resolve({
        code: err ? (typeof err.code === 'number' ? err.code : 1) : 0,
        stdout: (stdout || '').toString(),
        stderr: (stderr || '').toString() || (err && !stdout ? String(err.message) : ''),
      });
    });
  });
}

function startServer() {
  return run(['start-server'], 20000);
}

/**
 * Theo dõi danh sách thiết bị theo thời gian thực (host:track-devices-l).
 * onChange([{serial, state, tid}]) được gọi mỗi khi danh sách thay đổi. Tự kết nối lại.
 * Nhiều máy có thể trùng serial (ROM nhân bản) → mỗi kết nối được phân biệt bằng transport id;
 * khi serial bị trùng, địa chỉ của máy có dạng "serial@t<id>" (xem addrOf).
 */
function trackDevices(onChange, onError) {
  let stopped = false;
  let sock = null;
  async function loop() {
    while (!stopped) {
      try {
        sock = await openService(null, 'host:track-devices-l', 8000);
        await new Promise((resolve) => {
          let buf = Buffer.alloc(0);
          sock.on('data', (d) => {
            buf = Buffer.concat([buf, d]);
            while (buf.length >= 4) {
              const len = parseInt(buf.toString('ascii', 0, 4), 16);
              if (Number.isNaN(len) || buf.length < 4 + len) break;
              const payload = buf.toString('utf8', 4, 4 + len);
              buf = buf.subarray(4 + len);
              const raw = payload.split('\n').filter((l) => l.trim()).map((line) => {
                // "<serial>   <state> product:x model:y device:z transport_id:N"
                const parts = line.trim().split(/\s+/);
                const m = line.match(/transport_id:(\d+)/);
                return { serial: parts[0], state: parts[1] || '', tid: m ? m[1] : '' };
              });
              const count = {};
              for (const x of raw) count[x.serial] = (count[x.serial] || 0) + 1;
              const list = raw.map((x) => ({ ...x, addr: count[x.serial] > 1 && x.tid ? `${x.serial}@t${x.tid}` : x.serial }));
              try { onChange(list); } catch (e) { if (onError) onError(e); }
            }
          });
          sock.on('close', resolve);
          sock.on('error', () => {});
          sock.resume();
        });
      } catch (e) {
        if (onError) onError(e);
        // adb server có thể chưa chạy
        await startServer();
      }
      if (!stopped) await new Promise((r) => setTimeout(r, 1000));
    }
  }
  loop();
  return () => { stopped = true; if (sock) sock.destroy(); };
}

// địa chỉ "serial@t<id>" (serial bị trùng) → dùng transport id thay cho serial
function parseAddr(addr) {
  const m = /^(.*)@t(\d+)$/.exec(addr || '');
  return m ? { serial: m[1], tid: m[2] } : { serial: addr, tid: null };
}

function transportCmd(addr) {
  const { serial, tid } = parseAddr(addr);
  return tid ? `host:transport-id:${tid}` : `host:transport:${serial}`;
}

/** Tham số chọn máy cho adb.exe: ['-s', serial] hoặc ['-t', id] khi serial bị trùng. */
function sel(addr) {
  const { serial, tid } = parseAddr(addr);
  return tid ? ['-t', tid] : ['-s', serial];
}

function isWifiSerial(addr) {
  const { serial } = parseAddr(addr);
  return /:\d+$/.test(serial) || serial.includes('._adb-tls-connect._tcp') || serial.startsWith('adb-');
}

// Khoá công khai mà adb server này dùng để uỷ quyền (RSA). Dùng để "cấy" vào máy.
function pubKeyPath() {
  const home = process.env.USERPROFILE || process.env.HOME || require('os').homedir();
  for (const p of [process.env.ANDROID_VENDOR_KEYS, path.join(home, '.android', 'adbkey.pub')]) {
    if (p && fs.existsSync(p)) return p;
  }
  return '';
}

/**
 * Cấy khoá công khai của PC vào /data/misc/adb/adb_keys (CẦN ROOT) để máy luôn tin máy tính này —
 * như tick "Luôn cho phép", nhưng không cần màn hình và còn sau khi adbd/khởi động lại. Không khởi động lại adbd.
 * Trả về {ok, msg}. Gộp thêm (không xoá khoá PC khác đã có).
 */
async function persistAdbKey(serial) {
  const pub = pubKeyPath();
  if (!pub) return { ok: false, msg: 'Không thấy khoá adbkey.pub của máy tính' };
  const frag = fs.readFileSync(pub, 'utf8').trim().split(/\s+/)[0].slice(40, 80); // đoạn giữa để nhận diện
  const tmp = '/data/local/tmp/.cp_adbkey.pub';
  const push = await run([...sel(serial), 'push', pub, tmp], 15000);
  if (push.code !== 0) return { ok: false, msg: (push.stderr || push.stdout || 'push lỗi').trim() };
  const sh = `su -c 'mkdir -p /data/misc/adb; touch /data/misc/adb/adb_keys; `
    + `grep -qF ${shellQuote(frag)} /data/misc/adb/adb_keys || cat ${tmp} >> /data/misc/adb/adb_keys; `
    + `chmod 640 /data/misc/adb/adb_keys; chown system:shell /data/misc/adb/adb_keys; restorecon /data/misc/adb/adb_keys 2>/dev/null; echo CP_OK'`;
  const out = await shell(serial, sh, 15000).catch((e) => e.message);
  if (!/CP_OK/.test(out)) return { ok: false, msg: 'Cần root để ghi khoá: ' + String(out).trim().slice(0, 200) };
  const chk = await shell(serial, `su -c 'cat /data/misc/adb/adb_keys'`, 10000).catch(() => '');
  return chk.includes(frag)
    ? { ok: true, msg: 'Đã cấy khoá — máy này sẽ luôn tin máy tính, không hỏi uỷ quyền lại' }
    : { ok: false, msg: 'Ghi khoá không thành công' };
}

function shellQuote(s) {
  return "'" + String(s).replace(/'/g, "'\\''") + "'";
}

module.exports = {
  ADB_PATH, VENDOR_DIR, ROOT,
  openService, shell, execOut, execStream, run, startServer, trackDevices, isWifiSerial, shellQuote,
  parseAddr, sel, persistAdbKey, pubKeyPath,
};
