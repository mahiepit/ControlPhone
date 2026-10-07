// Ánh xạ phím máy tính → Android KeyEvent
const SPECIAL = {
  Enter: 66, NumpadEnter: 66, Backspace: 67, Delete: 112, Tab: 61, Escape: 111,
  ArrowUp: 19, ArrowDown: 20, ArrowLeft: 21, ArrowRight: 22,
  Home: 122, End: 123, PageUp: 92, PageDown: 93, Insert: 124,
};

export const META = { SHIFT: 0x41, CTRL: 0x3000, ALT: 0x12 };

export function metaOf(e) {
  return (e.shiftKey ? META.SHIFT : 0) | (e.ctrlKey ? META.CTRL : 0) | (e.altKey ? META.ALT : 0);
}

/** Trả về keycode Android cho phím đặc biệt / tổ hợp, hoặc 0 nếu nên gửi dạng văn bản. */
export function androidKeycode(e) {
  if (SPECIAL[e.code]) return SPECIAL[e.code];
  if (SPECIAL[e.key]) return SPECIAL[e.key];
  if (e.ctrlKey || e.altKey) {
    if (/^Key[A-Z]$/.test(e.code)) return 29 + (e.code.charCodeAt(3) - 65);
    if (/^Digit[0-9]$/.test(e.code)) return 7 + (e.code.charCodeAt(5) - 48);
  }
  return 0;
}
