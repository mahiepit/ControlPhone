// Đóng gói control message theo giao thức scrcpy v5.0 (ControlMessageReader.java)
'use strict';

const TYPE = {
  INJECT_KEYCODE: 0,
  INJECT_TEXT: 1,
  INJECT_TOUCH_EVENT: 2,
  INJECT_SCROLL_EVENT: 3,
  BACK_OR_SCREEN_ON: 4,
  EXPAND_NOTIFICATION_PANEL: 5,
  EXPAND_SETTINGS_PANEL: 6,
  COLLAPSE_PANELS: 7,
  GET_CLIPBOARD: 8,
  SET_CLIPBOARD: 9,
  SET_DISPLAY_POWER: 10,
  ROTATE_DEVICE: 11,
  START_APP: 16,
  RESET_VIDEO: 17,
  SCAN_FILE: 22,
};

const POINTER_ID_GENERIC_FINGER = -2n;

function keycode(action, code, repeat = 0, meta = 0) {
  const b = Buffer.alloc(14);
  b.writeUInt8(TYPE.INJECT_KEYCODE, 0);
  b.writeUInt8(action, 1);
  b.writeInt32BE(code, 2);
  b.writeInt32BE(repeat, 6);
  b.writeInt32BE(meta, 10);
  return b;
}

function text(str) {
  const body = Buffer.from(str, 'utf8');
  const b = Buffer.alloc(5 + body.length);
  b.writeUInt8(TYPE.INJECT_TEXT, 0);
  b.writeUInt32BE(body.length, 1);
  body.copy(b, 5);
  return b;
}

function touch(action, pointerId, x, y, w, h, pressure) {
  const b = Buffer.alloc(32);
  b.writeUInt8(TYPE.INJECT_TOUCH_EVENT, 0);
  b.writeUInt8(action, 1);
  b.writeBigInt64BE(BigInt(pointerId), 2);
  b.writeInt32BE(x, 10);
  b.writeInt32BE(y, 14);
  b.writeUInt16BE(w, 18);
  b.writeUInt16BE(h, 20);
  b.writeUInt16BE(pressure >= 1 ? 0xffff : Math.round(pressure * 0x10000), 22);
  b.writeInt32BE(0, 24); // action button
  b.writeInt32BE(0, 28); // buttons
  return b;
}

function toI16Fixed(v) {
  // giá trị thực trong [-16, 16] → i16 fixed point của [-1, 1]
  let f = v / 16;
  if (f > 1) f = 1;
  if (f < -1) f = -1;
  const i = Math.round(f * 0x8000);
  return Math.max(-0x8000, Math.min(0x7fff, i));
}

function scroll(x, y, w, h, hScroll, vScroll) {
  const b = Buffer.alloc(21);
  b.writeUInt8(TYPE.INJECT_SCROLL_EVENT, 0);
  b.writeInt32BE(x, 1);
  b.writeInt32BE(y, 5);
  b.writeUInt16BE(w, 9);
  b.writeUInt16BE(h, 11);
  b.writeInt16BE(toI16Fixed(hScroll), 13);
  b.writeInt16BE(toI16Fixed(vScroll), 15);
  b.writeInt32BE(0, 17);
  return b;
}

function backOrScreenOn(action) {
  return Buffer.from([TYPE.BACK_OR_SCREEN_ON, action]);
}

function empty(type) {
  return Buffer.from([type]);
}

function getClipboard(copyKey = 0) {
  return Buffer.from([TYPE.GET_CLIPBOARD, copyKey]);
}

let clipboardSeq = 1n;
function setClipboard(str, paste) {
  const body = Buffer.from(str, 'utf8');
  const b = Buffer.alloc(14 + body.length);
  b.writeUInt8(TYPE.SET_CLIPBOARD, 0);
  b.writeBigInt64BE(clipboardSeq++, 1);
  b.writeUInt8(paste ? 1 : 0, 9);
  b.writeUInt32BE(body.length, 10);
  body.copy(b, 14);
  return b;
}

function setDisplayPower(on) {
  return Buffer.from([TYPE.SET_DISPLAY_POWER, on ? 1 : 0]);
}

function startApp(name) {
  const body = Buffer.from(name, 'utf8').subarray(0, 255);
  return Buffer.concat([Buffer.from([TYPE.START_APP, body.length]), body]);
}

function scanFile(p) {
  const body = Buffer.from(p, 'utf8');
  const b = Buffer.alloc(5 + body.length);
  b.writeUInt8(TYPE.SCAN_FILE, 0);
  b.writeUInt32BE(body.length, 1);
  body.copy(b, 5);
  return b;
}

// Android KeyEvent codes thường dùng
const KEY = {
  HOME: 3, BACK: 4, CALL: 5, ENDCALL: 6, DPAD_UP: 19, DPAD_DOWN: 20, DPAD_LEFT: 21, DPAD_RIGHT: 22,
  VOLUME_UP: 24, VOLUME_DOWN: 25, POWER: 26, CAMERA: 27, TAB: 61, SPACE: 62, ENTER: 66, DEL: 67,
  MENU: 82, NOTIFICATION: 83, SEARCH: 84, MEDIA_PLAY_PAUSE: 85, PAGE_UP: 92, PAGE_DOWN: 93,
  ESCAPE: 111, FORWARD_DEL: 112, MOVE_HOME: 122, MOVE_END: 123, VOLUME_MUTE: 164,
  APP_SWITCH: 187, BRIGHTNESS_DOWN: 220, BRIGHTNESS_UP: 221, SLEEP: 223, WAKEUP: 224,
};

module.exports = {
  TYPE, KEY, POINTER_ID_GENERIC_FINGER,
  keycode, text, touch, scroll, backOrScreenOn, empty, getClipboard, setClipboard,
  setDisplayPower, startApp, scanFile,
};
