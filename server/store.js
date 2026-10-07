// Lưu cấu hình + thông tin thiết bị (tên, số thứ tự, nhóm) vào data/config.json
'use strict';
const fs = require('fs');
const path = require('path');

const DATA_DIR = path.join(__dirname, '..', 'data');
const FILE = path.join(DATA_DIR, 'config.json');

const DEFAULT_SETTINGS = {
  // Luồng lưới (xem nhiều máy) — nhẹ
  thumbMaxSize: 480,
  thumbBitRate: 600000,
  thumbFps: 15,
  // Luồng xem lớn (1 máy) — nét
  hqMaxSize: 1280,
  hqBitRate: 4000000,
  hqFps: 30,
  powerOnConnect: true,
  screenOffOnConnect: true, // tự tắt màn hình thật của điện thoại khi kết nối (vẫn xem được)
  preferTransport: 'usb', // usb | wifi
  autoReconnectWifi: true,
  maxParallelStart: 6,
  maxParallelJobs: 10,
  screenshotDir: path.join(__dirname, '..', 'screenshots'),
};

let state = {
  settings: { ...DEFAULT_SETTINGS },
  devices: {}, // hwId -> { label, num, groups: [], wifi: 'ip:port' }
  groups: [], // tên nhóm
  wifiHistory: [], // các địa chỉ ip:port từng kết nối
  nextNum: 1,
};

function load() {
  try {
    const raw = JSON.parse(fs.readFileSync(FILE, 'utf8'));
    state = {
      ...state,
      ...raw,
      settings: { ...DEFAULT_SETTINGS, ...(raw.settings || {}) },
    };
  } catch (_) { /* lần đầu chạy */ }
}

let saveTimer = null;
function save() {
  if (saveTimer) return;
  saveTimer = setTimeout(() => {
    saveTimer = null;
    fs.mkdirSync(DATA_DIR, { recursive: true });
    const tmp = FILE + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(state, null, 2));
    fs.renameSync(tmp, FILE);
  }, 300);
}

function deviceMeta(hwId) {
  if (!state.devices[hwId]) {
    state.devices[hwId] = { label: '', num: state.nextNum++, groups: [] };
    save();
  }
  return state.devices[hwId];
}

load();

module.exports = {
  get state() { return state; },
  get settings() { return state.settings; },
  DEFAULT_SETTINGS, DATA_DIR, save, deviceMeta,
};
