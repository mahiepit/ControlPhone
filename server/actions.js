// Các thao tác quản lý hàng loạt (cài APK, đẩy file, shell, chụp màn hình, WiFi, root...)
'use strict';
const fs = require('fs');
const os = require('os');
const net = require('net');
const path = require('path');
const { spawn } = require('child_process');
const adb = require('./adb');
const store = require('./store');
const P = require('./protocol');
const { Limiter } = require('./devices');

const q = adb.shellQuote;
const UPLOAD_DIR = path.join(store.DATA_DIR, 'uploads');

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function safeName(s) {
  return String(s).replace(/[\\/:*?"<>|\s]+/g, '_').slice(0, 80);
}

function uploadPath(token) {
  if (!/^[\w.-]+$/.test(token)) throw new Error('token không hợp lệ');
  const p = path.join(UPLOAD_DIR, token);
  if (!fs.existsSync(p)) throw new Error('file tải lên không còn');
  return p;
}

function outOf(r) {
  return ((r.stdout || '') + (r.stderr ? '\n' + r.stderr : '')).trim();
}

/** Thao tác cho từng thiết bị. Trả về {ok, msg}. */
const OPS = {
  async shell(d, ctx, p) {
    const cmd = p.root ? `su -c ${q(p.cmd)}` : p.cmd;
    const out = await adb.shell(d.activeSerial, `${cmd} 2>&1`, (p.timeout || 30) * 1000);
    return { ok: true, msg: out.trim() };
  },

  async install(d, ctx, p) {
    const file = uploadPath(p.token);
    const r = await adb.run([...adb.sel(d.activeSerial), 'install', '-r', '-d', file], 10 * 60 * 1000);
    const out = outOf(r);
    return { ok: /Success/.test(out), msg: out.split('\n').slice(-2).join(' ') };
  },

  async uninstall(d, ctx, p) {
    const out = await adb.shell(d.activeSerial, `pm uninstall ${q(p.pkg)} 2>&1`, 60000);
    return { ok: /Success/.test(out), msg: out.trim() };
  },

  async push(d, ctx, p) {
    const file = uploadPath(p.token);
    const dir = (p.dir || '/sdcard/Download/').replace(/\/?$/, '/');
    const remote = dir + (p.name || path.basename(file).replace(/^[0-9a-f]+-/, ''));
    const r = await adb.run([...adb.sel(d.activeSerial), 'push', file, remote], 10 * 60 * 1000);
    const ok = r.code === 0;
    if (ok) {
      const s = ctx.manager.controlSession(d);
      if (s) s.send(P.scanFile(remote));
    }
    return { ok, msg: ok ? remote : outOf(r) };
  },

  async url(d, ctx, p) {
    const out = await adb.shell(d.activeSerial, `am start -a android.intent.action.VIEW -d ${q(p.url)} 2>&1`, 15000);
    return { ok: !/Error/.test(out), msg: out.trim().split('\n').pop() };
  },

  async launch(d, ctx, p) {
    const s = ctx.manager.controlSession(d);
    if (s) { s.send(P.startApp(p.pkg)); return { ok: true, msg: 'đã gửi lệnh mở' }; }
    const out = await adb.shell(d.activeSerial, `monkey -p ${q(p.pkg)} -c android.intent.category.LAUNCHER 1 2>&1`, 15000);
    return { ok: !/No activities|Error/.test(out), msg: out.trim().split('\n').pop() };
  },

  async stopapp(d, ctx, p) {
    await adb.shell(d.activeSerial, `am force-stop ${q(p.pkg)}`, 15000);
    return { ok: true, msg: 'đã dừng' };
  },

  async clearapp(d, ctx, p) {
    const out = await adb.shell(d.activeSerial, `pm clear ${q(p.pkg)} 2>&1`, 30000);
    return { ok: /Success/.test(out), msg: out.trim() };
  },

  async reboot(d, ctx, p) {
    const mode = ['recovery', 'bootloader'].includes(p.mode) ? p.mode : '';
    await adb.run([...adb.sel(d.activeSerial), 'reboot', ...(mode ? [mode] : [])], 20000);
    return { ok: true, msg: 'đang khởi động lại' };
  },

  async screenshot(d, ctx) {
    const png = await adb.execOut(d.activeSerial, 'screencap -p', 30000);
    if (png.length < 100 || png[1] !== 0x50) throw new Error('ảnh không hợp lệ');
    const dir = store.settings.screenshotDir;
    fs.mkdirSync(dir, { recursive: true });
    const meta = store.deviceMeta(d.id);
    const file = path.join(dir, `${String(meta.num).padStart(3, '0')}_${safeName(meta.label || d.info.model || d.id)}_${stamp()}.png`);
    fs.writeFileSync(file, png);
    return { ok: true, msg: file };
  },

  // USB → WiFi: bật adb tcpip 5555 rồi kết nối qua IP wlan0
  async tcpip(d, ctx) {
    let ip = d.info.ip;
    if (!ip) {
      const out = await adb.shell(d.activeSerial, 'ip -f inet addr show wlan0 | grep -m1 inet', 8000);
      const m = out.match(/inet\s+(\d+\.\d+\.\d+\.\d+)/);
      ip = m ? m[1] : '';
    }
    if (!ip) return { ok: false, msg: 'Máy chưa kết nối WiFi' };
    const target = `${ip}:5555`;
    if ([...d.transports.keys()].includes(target)) return { ok: true, msg: 'đã có kết nối WiFi ' + target };
    if (d.transports.get(d.activeSerial) === 'usb') {
      // adb tcpip khởi động lại adbd: máy root phải có khoá trong adb_keys trước, nếu không sẽ bị hỏi uỷ quyền lại
      if (d.info.root) {
        const k = await adb.persistAdbKey(d.activeSerial);
        if (!k.ok) return { ok: false, msg: 'Chưa chuyển WiFi (tránh mất uỷ quyền): ' + k.msg };
      }
      await adb.run([...adb.sel(d.activeSerial), 'tcpip', '5555'], 15000);
      await new Promise((r) => setTimeout(r, 2500));
    }
    let r;
    for (let i = 0; i < 4; i++) {
      r = await adb.run(['connect', target], 10000);
      if (/connected to/.test(r.stdout)) break;
      await new Promise((res) => setTimeout(res, 1500));
    }
    const ok = /connected to/.test(r.stdout);
    if (ok) {
      const meta = store.deviceMeta(d.id);
      meta.wifi = target;
      if (!store.state.wifiHistory.includes(target)) store.state.wifiHistory.push(target);
      store.save();
    }
    let msg = outOf(r);
    if (!ok && /refused|10061/.test(msg)) {
      msg += '\n→ ROM của máy này chặn ADB qua TCP (hay gặp ở Samsung Android 11+). Hãy bật "Gỡ lỗi không dây" trong Tuỳ chọn nhà phát triển rồi dùng "Ghép nối mã".';
    }
    return { ok, msg };
  },

  // WiFi → USB: ngắt kết nối WiFi, không tự nối lại nữa; máy có cáp chuyển ngay sang USB.
  // KHÔNG khởi động lại adbd (adb usb / setprop): máy chưa "Luôn cho phép" sẽ hỏi uỷ quyền lại và mất cả USB.
  async tousb(d) {
    const wifi = [...d.transports.entries()].filter(([, t]) => t === 'wifi').map(([s]) => s);
    const usb = [...d.transports.entries()].find(([, t]) => t === 'usb');
    const meta = store.deviceMeta(d.id);
    if (!wifi.length && !meta.wifi) return { ok: true, msg: 'Đang dùng USB' };
    delete meta.wifi;
    store.save();
    // Android 11+ "Gỡ lỗi không dây": adb tự nối lại máy đã ghép nối → phải tắt hẳn (không khởi động lại adbd)
    const serial = usb ? usb[0] : d.activeSerial;
    const wl = await adb.shell(serial, 'settings get global adb_wifi_enabled 2>/dev/null', 8000).catch(() => '');
    if (wl.trim() === '1') await adb.shell(serial, 'settings put global adb_wifi_enabled 0', 8000).catch(() => {});
    for (const s of wifi) await adb.run(['disconnect', adb.parseAddr(s).serial], 8000);
    return { ok: true, msg: usb ? 'Đã chuyển về USB' : 'Đã ngắt WiFi ADB — cắm cáp USB để dùng tiếp' };
  },

  // ROOT: bật ADB qua WiFi cố định cổng 5555 (giữ cả sau khi khởi động lại)
  async rootadbwifi(d) {
    if (!d.info.root) return { ok: false, msg: 'Máy không có root' };
    const out = await adb.shell(d.activeSerial,
      "su -c 'setprop persist.adb.tcp.port 5555; setprop service.adb.tcp.port 5555; echo SET; setprop ctl.restart adbd' 2>&1", 20000).catch((e) => 'SET ' + e.message);
    if (!/SET/.test(out)) return { ok: false, msg: out.trim() };
    // chờ adbd khởi động lại rồi thử kết nối WiFi
    await new Promise((r) => setTimeout(r, 5000));
    const ip = d.info.ip;
    if (!ip) return { ok: true, msg: 'Đã đặt cổng 5555 (máy chưa có IP WiFi)' };
    const r = await adb.run(['connect', `${ip}:5555`], 10000);
    const ok = /connected to/.test(r.stdout);
    if (ok) {
      const meta = store.deviceMeta(d.id);
      meta.wifi = `${ip}:5555`;
      store.save();
    }
    return { ok, msg: ok ? `Đã bật ADB WiFi cố định: ${ip}:5555` : `Đã đặt cổng 5555 nhưng không kết nối được (${outOf(r)}). ROM có thể chặn ADB TCP.` };
  },

  // Cấy khoá uỷ quyền của PC vào máy (cần root) → không bao giờ hỏi "Cho phép gỡ lỗi USB" lại
  async pushkey(d) {
    if (!d.info.root) return { ok: false, msg: 'Máy không có root — hãy tick "Luôn cho phép từ máy tính này" một lần trên máy' };
    return adb.persistAdbKey(d.activeSerial);
  },

  async rootcheck(d) {
    const out = await adb.shell(d.activeSerial, "su -c 'id' 2>&1", 15000).catch((e) => e.message);
    const ok = /uid=0/.test(out);
    d.info.root = ok;
    return { ok, msg: ok ? 'Có quyền root' : 'Không có root: ' + out.trim() };
  },

  async stayawake(d, ctx, p) {
    const v = p.on ? 7 : 0;
    await adb.shell(d.activeSerial, `settings put global stay_on_while_plugged_in ${v}`, 10000);
    return { ok: true, msg: p.on ? 'Luôn sáng khi cắm sạc' : 'Tắt luôn sáng' };
  },

  async brightness(d, ctx, p) {
    const v = Math.max(0, Math.min(255, p.value | 0));
    await adb.shell(d.activeSerial, `settings put system screen_brightness_mode 0; settings put system screen_brightness ${v}`, 10000);
    return { ok: true, msg: 'Độ sáng ' + v };
  },
};

class Actions {
  constructor(manager, broadcast) {
    this.manager = manager;
    this.broadcast = broadcast;
    this.taskSeq = 1;
    fs.mkdirSync(UPLOAD_DIR, { recursive: true });
    // dọn file tải lên cũ (> 1 ngày)
    for (const f of fs.readdirSync(UPLOAD_DIR)) {
      const p = path.join(UPLOAD_DIR, f);
      try { if (Date.now() - fs.statSync(p).mtimeMs > 86400000) fs.unlinkSync(p); } catch (_) { /* bỏ qua */ }
    }
  }

  runBatch(op, ids, params, title) {
    const fn = OPS[op];
    if (!fn) throw new Error('Thao tác không hỗ trợ: ' + op);
    const devices = ids.map((id) => this.manager.get(id)).filter((d) => d && d.activeSerial && !d.pending);
    const task = {
      id: this.taskSeq++, op, title: title || op, total: devices.length, done: 0, ok: 0, fail: 0,
      results: [], startedAt: Date.now(), finished: false,
    };
    const lim = new Limiter(store.settings.maxParallelJobs || 10);
    let lastEmit = 0;
    const emit = (force) => {
      const now = Date.now();
      if (!force && now - lastEmit < 250) return;
      lastEmit = now;
      this.broadcast({ t: 'task', task });
    };
    emit(true);
    Promise.all(devices.map((d) => lim.run(async () => {
      let r;
      try { r = await fn(d, { manager: this.manager }, params || {}); } catch (e) { r = { ok: false, msg: e.message }; }
      task.done++;
      if (r.ok) task.ok++; else task.fail++;
      task.results.push({ id: d.id, vid: d.vid, name: this.manager.label(d), ok: r.ok, msg: String(r.msg || '').slice(0, 4000) });
      emit(false);
    }))).then(() => {
      task.finished = true;
      emit(true);
      this.manager.changed();
    });
    return task;
  }

  // ---------- WiFi / kết nối ----------
  async connect(addr) {
    addr = String(addr || '').trim();
    if (!addr) throw new Error('Thiếu địa chỉ');
    if (!/:\d+$/.test(addr)) addr += ':5555';
    const r = await adb.run(['connect', addr], 12000);
    const ok = /connected to/.test(r.stdout);
    if (ok && !store.state.wifiHistory.includes(addr)) { store.state.wifiHistory.push(addr); store.save(); }
    return { ok, msg: outOf(r) };
  }

  async pair(addr, code) {
    const r = await adb.run(['pair', String(addr).trim(), String(code).trim()], 20000);
    return { ok: /Successfully paired/i.test(r.stdout), msg: outOf(r) };
  }

  localSubnets() {
    const res = [];
    for (const [name, list] of Object.entries(os.networkInterfaces())) {
      for (const a of list || []) {
        if (a.family !== 'IPv4' || a.internal) continue;
        if (/^169\.254\./.test(a.address)) continue;
        if (/vEthernet|VirtualBox|VMware|WSL|Hyper-V/i.test(name)) continue;
        res.push({ name, address: a.address, subnet: a.address.split('.').slice(0, 3).join('.') });
      }
    }
    return res;
  }

  /** Quét LAN tìm cổng ADB rồi kết nối. subnet dạng "192.168.1" */
  async scan(subnet, port = 5555) {
    const subnets = subnet ? [subnet] : [...new Set(this.localSubnets().map((s) => s.subnet))];
    const hosts = [];
    for (const sn of subnets) for (let i = 1; i < 255; i++) hosts.push(`${sn}.${i}`);
    const open = [];
    const lim = new Limiter(128);
    await Promise.all(hosts.map((h) => lim.run(() => new Promise((resolve) => {
      const s = net.connect({ host: h, port, timeout: 500 });
      const end = (ok) => { s.destroy(); if (ok) open.push(`${h}:${port}`); resolve(); };
      s.on('connect', () => end(true));
      s.on('timeout', () => end(false));
      s.on('error', () => end(false));
    }))));
    const connectedSerials = new Set(this.manager.serialStates.keys());
    const results = [];
    const lim2 = new Limiter(10);
    await Promise.all(open.map((addr) => lim2.run(async () => {
      if (connectedSerials.has(addr)) { results.push({ addr, ok: true, msg: 'đã kết nối sẵn' }); return; }
      const r = await this.connect(addr);
      results.push({ addr, ...r });
    })));
    return { subnets, found: open.length, results };
  }

  // ---------- công cụ scrcpy gốc ----------
  launchScrcpy(serial, extra = [], title) {
    const exe = path.join(adb.VENDOR_DIR, 'scrcpy.exe');
    if (!fs.existsSync(exe)) throw new Error('Không thấy scrcpy.exe');
    // scrcpy.exe chỉ nhận serial (không nhận transport id)
    const args = [...(serial ? ['-s', adb.parseAddr(serial).serial] : []), ...extra];
    if (title) args.push('--window-title', title);
    const child = spawn(exe, args, {
      cwd: adb.VENDOR_DIR, detached: true, stdio: 'ignore', windowsHide: false,
      env: { ...process.env, ADB: adb.ADB_PATH },
    });
    child.unref();
    return { ok: true, msg: 'Đã mở scrcpy ' + args.join(' ') };
  }

  // ---------- tệp ----------
  async listFiles(d, dir) {
    dir = dir || '/sdcard/';
    const out = await adb.shell(d.activeSerial,
      `cd ${q(dir)} 2>&1 && stat -c '%F|%s|%Y|%n' * .[!.]* 2>/dev/null`, 15000);
    if (/No such file|Permission denied|Not a directory/.test(out.split('\n')[0] || '')) throw new Error(out.trim());
    const items = [];
    for (const line of out.split('\n')) {
      const parts = line.split('|');
      if (parts.length < 4) continue;
      const name = parts.slice(3).join('|');
      if (!name || name === '*' || name === '.[!.]*') continue;
      items.push({
        name,
        dir: /directory/.test(parts[0]) || /symbolic link/.test(parts[0]),
        link: /symbolic link/.test(parts[0]),
        size: parseInt(parts[1], 10) || 0,
        mtime: (parseInt(parts[2], 10) || 0) * 1000,
      });
    }
    items.sort((a, b) => (b.dir - a.dir) || a.name.localeCompare(b.name));
    return { path: dir.replace(/\/?$/, '/'), items };
  }

  async deletePath(d, p) {
    if (!p || p === '/' || !p.startsWith('/sdcard/') && !p.startsWith('/storage/') && !p.startsWith('/data/local/tmp/')) {
      throw new Error('Chỉ cho phép xoá trong /sdcard, /storage hoặc /data/local/tmp');
    }
    return adb.shell(d.activeSerial, `rm -rf ${q(p)} 2>&1`, 30000);
  }

  async listApps(d) {
    const out = await adb.shell(d.activeSerial, 'pm list packages -3 2>/dev/null', 20000);
    return out.split('\n').map((l) => l.replace(/^package:/, '').trim()).filter(Boolean).sort();
  }
}

module.exports = { Actions, UPLOAD_DIR, safeName, stamp };
