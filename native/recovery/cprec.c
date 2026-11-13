// ControlPhone recovery helper: chạy trong recovery (TWRP...) qua adb, nơi không có Android runtime nên scrcpy-server
// không chạy được.
//   cprec stream [maxdim] [interval_ms]
// stdout: gói nhị phân (little-endian)
//   "CPI1" u16 fbW u16 fbH u8 touch(0 không có, 1 cảm ứng thật, 2 cảm ứng ảo uinput) u8 keys(1 có phím cứng) u16 0
//   "CPF1" u16 w u16 h u32 len  + len byte RLE: (u8 n, b, g, r) = n pixel liền nhau cùng màu
// stdin: lệnh 8 byte: u8 cmd, u8 a, u16 x, u16 y, u16 0
//   'T' a=0 chạm xuống, 1 di chuyển, 2 nhả; x,y = 0..65535 theo chiều rộng/cao màn hình
//   'K' a=1 nhấn / 0 nhả, x = mã phím Linux (116 nguồn, 115 âm lượng +, 114 âm lượng -)
//   'U' tạo cảm ứng ảo (uinput) — recovery chỉ quét /dev/input lúc khởi động nên phải khởi động lại giao diện recovery
//   'F' gửi lại nguyên khung hình
// Màn hình đọc bằng mmap framebuffer (/dev/graphics/fb0): driver decon của Samsung không cho read().
// Build: native/recovery/build.ps1 (NDK, static) → cprec-arm64, cprec-arm.
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <linux/fb.h>
#include <linux/input.h>
#include <linux/uinput.h>

#define BIT(arr, n) ((arr[(n) / 8] >> ((n) % 8)) & 1)

static int fb_fd = -1;
static uint8_t *fb_map = NULL;
static size_t fb_len = 0;
static struct fb_var_screeninfo vi;
static struct fb_fix_screeninfo fi;

static int touch_fd = -1, touch_mode = 0, touch_slot = 0, touch_tid = 0, touch_btn = 0, touch_major = 0, touch_press = 0;
static int tx_min, tx_max, ty_min, ty_max, next_tid = 1, uinput_fd = -1;
static int key_fds[8], key_n = 0;
static uint8_t key_bits[8][KEY_MAX / 8 + 1];

static int write_all(int fd, const void *buf, size_t n) {
    const uint8_t *p = buf;
    while (n > 0) {
        ssize_t w = write(fd, p, n);
        if (w < 0) { if (errno == EINTR) continue; return -1; }
        p += w; n -= (size_t)w;
    }
    return 0;
}

static void emit(int fd, int type, int code, int value) {
    struct input_event ev;
    memset(&ev, 0, sizeof ev);
    ev.type = (uint16_t)type; ev.code = (uint16_t)code; ev.value = value;
    if (write(fd, &ev, sizeof ev) < 0) { /* thiết bị đã mất: bỏ qua */ }
}

static int fb_open(void) {
    if (fb_map) { munmap(fb_map, fb_len); fb_map = NULL; }
    if (fb_fd >= 0) { close(fb_fd); fb_fd = -1; }
    fb_fd = open("/dev/graphics/fb0", O_RDONLY);
    if (fb_fd < 0) fb_fd = open("/dev/fb0", O_RDONLY);
    if (fb_fd < 0) return -1;
    if (ioctl(fb_fd, FBIOGET_VSCREENINFO, &vi) < 0 || ioctl(fb_fd, FBIOGET_FSCREENINFO, &fi) < 0) return -1;
    fb_len = fi.smem_len ? fi.smem_len : (size_t)fi.line_length * vi.yres_virtual;
    fb_map = mmap(NULL, fb_len, PROT_READ, MAP_SHARED, fb_fd, 0);
    if (fb_map == MAP_FAILED) { fb_map = NULL; return -1; }
    return 0;
}

// thiết bị cảm ứng thật + thiết bị có phím cứng
static void scan_inputs(void) {
    DIR *d = opendir("/dev/input");
    struct dirent *de;
    if (!d) return;
    while ((de = readdir(d))) {
        char path[64];
        uint8_t absb[ABS_MAX / 8 + 1], keyb[KEY_MAX / 8 + 1], propb[INPUT_PROP_MAX / 8 + 1];
        if (strncmp(de->d_name, "event", 5)) continue;
        snprintf(path, sizeof path, "/dev/input/%s", de->d_name);
        int fd = open(path, O_RDWR);
        if (fd < 0) continue;
        memset(absb, 0, sizeof absb); memset(keyb, 0, sizeof keyb); memset(propb, 0, sizeof propb);
        ioctl(fd, EVIOCGBIT(EV_ABS, sizeof absb), absb);
        ioctl(fd, EVIOCGBIT(EV_KEY, sizeof keyb), keyb);
        ioctl(fd, EVIOCGPROP(sizeof propb), propb);
        int keep = 0;
        if (touch_fd < 0 && BIT(absb, ABS_MT_POSITION_X) && BIT(absb, ABS_MT_POSITION_Y)) {
            struct input_absinfo ax, ay;
            if (ioctl(fd, EVIOCGABS(ABS_MT_POSITION_X), &ax) == 0 && ioctl(fd, EVIOCGABS(ABS_MT_POSITION_Y), &ay) == 0 && ax.maximum > ax.minimum && ay.maximum > ay.minimum) {
                touch_fd = fd; touch_mode = 1; keep = 1;
                tx_min = ax.minimum; tx_max = ax.maximum; ty_min = ay.minimum; ty_max = ay.maximum;
                touch_slot = BIT(absb, ABS_MT_SLOT); touch_tid = BIT(absb, ABS_MT_TRACKING_ID);
                touch_major = BIT(absb, ABS_MT_TOUCH_MAJOR); touch_press = BIT(absb, ABS_MT_PRESSURE);
                touch_btn = BIT(keyb, BTN_TOUCH);
            }
        }
        if (!keep && key_n < 8 && (BIT(keyb, KEY_POWER) || BIT(keyb, KEY_VOLUMEUP) || BIT(keyb, KEY_VOLUMEDOWN) || BIT(keyb, KEY_HOME) || BIT(keyb, KEY_BACK))) {
            memcpy(key_bits[key_n], keyb, sizeof keyb);
            key_fds[key_n++] = fd; keep = 1;
        }
        if (!keep) close(fd);
    }
    closedir(d);
}

// cảm ứng ảo kiểu B (slot + tracking id), toạ độ = pixel màn hình
static int make_uinput(void) {
    struct uinput_user_dev ud;
    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) return -1;
    ioctl(fd, UI_SET_EVBIT, EV_SYN); ioctl(fd, UI_SET_EVBIT, EV_KEY); ioctl(fd, UI_SET_EVBIT, EV_ABS);
    ioctl(fd, UI_SET_KEYBIT, BTN_TOUCH);
    // phím điều hướng: máy không có phím cứng tương ứng thì bơm qua thiết bị ảo này
    ioctl(fd, UI_SET_KEYBIT, KEY_HOME); ioctl(fd, UI_SET_KEYBIT, KEY_BACK); ioctl(fd, UI_SET_KEYBIT, KEY_POWER);
    ioctl(fd, UI_SET_KEYBIT, KEY_VOLUMEUP); ioctl(fd, UI_SET_KEYBIT, KEY_VOLUMEDOWN);
    ioctl(fd, UI_SET_ABSBIT, ABS_MT_SLOT); ioctl(fd, UI_SET_ABSBIT, ABS_MT_TRACKING_ID);
    ioctl(fd, UI_SET_ABSBIT, ABS_MT_POSITION_X); ioctl(fd, UI_SET_ABSBIT, ABS_MT_POSITION_Y);
    ioctl(fd, UI_SET_ABSBIT, ABS_MT_TOUCH_MAJOR); ioctl(fd, UI_SET_ABSBIT, ABS_MT_PRESSURE);
    ioctl(fd, UI_SET_PROPBIT, INPUT_PROP_DIRECT);
    memset(&ud, 0, sizeof ud);
    snprintf(ud.name, UINPUT_MAX_NAME_SIZE, "controlphone_touch");
    ud.id.bustype = BUS_VIRTUAL; ud.id.vendor = 0x1209; ud.id.product = 0x4350; ud.id.version = 1;
    ud.absmax[ABS_MT_SLOT] = 9; ud.absmax[ABS_MT_TRACKING_ID] = 65535;
    ud.absmax[ABS_MT_POSITION_X] = (int)vi.xres - 1; ud.absmax[ABS_MT_POSITION_Y] = (int)vi.yres - 1;
    ud.absmax[ABS_MT_TOUCH_MAJOR] = 255; ud.absmax[ABS_MT_PRESSURE] = 255;
    if (write(fd, &ud, sizeof ud) != sizeof ud || ioctl(fd, UI_DEV_CREATE) < 0) { close(fd); return -1; }
    touch_fd = fd; touch_mode = 2; uinput_fd = fd;
    tx_min = 0; tx_max = (int)vi.xres - 1; ty_min = 0; ty_max = (int)vi.yres - 1;
    touch_slot = touch_tid = touch_major = touch_press = touch_btn = 1;
    return 0;
}

static void touch(int action, int nx, int ny) {
    if (touch_fd < 0) return;
    int x = tx_min + (int)((int64_t)nx * (tx_max - tx_min) / 65535), y = ty_min + (int)((int64_t)ny * (ty_max - ty_min) / 65535);
    if (action == 2) { // nhả
        if (touch_slot) emit(touch_fd, EV_ABS, ABS_MT_SLOT, 0);
        if (touch_tid) emit(touch_fd, EV_ABS, ABS_MT_TRACKING_ID, -1);
        if (touch_btn) emit(touch_fd, EV_KEY, BTN_TOUCH, 0);
        if (!touch_slot) emit(touch_fd, EV_SYN, SYN_MT_REPORT, 0);
        emit(touch_fd, EV_SYN, SYN_REPORT, 0);
        return;
    }
    if (touch_slot) emit(touch_fd, EV_ABS, ABS_MT_SLOT, 0);
    if (action == 0 && touch_tid) emit(touch_fd, EV_ABS, ABS_MT_TRACKING_ID, next_tid++ & 0x7fff);
    emit(touch_fd, EV_ABS, ABS_MT_POSITION_X, x);
    emit(touch_fd, EV_ABS, ABS_MT_POSITION_Y, y);
    if (touch_major) emit(touch_fd, EV_ABS, ABS_MT_TOUCH_MAJOR, 6);
    if (touch_press) emit(touch_fd, EV_ABS, ABS_MT_PRESSURE, 60);
    if (action == 0 && touch_btn) emit(touch_fd, EV_KEY, BTN_TOUCH, 1);
    if (!touch_slot) emit(touch_fd, EV_SYN, SYN_MT_REPORT, 0);
    emit(touch_fd, EV_SYN, SYN_REPORT, 0);
}

static void key(int code, int down) {
    for (int i = 0; i < key_n; i++)
        if (code <= KEY_MAX && BIT(key_bits[i], code)) {
            emit(key_fds[i], EV_KEY, code, down ? 1 : 0);
            emit(key_fds[i], EV_SYN, SYN_REPORT, 0);
            return;
        }
    if (uinput_fd >= 0 && (code == KEY_HOME || code == KEY_BACK || code == KEY_POWER || code == KEY_VOLUMEUP || code == KEY_VOLUMEDOWN)) {
        emit(uinput_fd, EV_KEY, code, down ? 1 : 0);
        emit(uinput_fd, EV_SYN, SYN_REPORT, 0);
    }
}

static void send_info(void) {
    uint8_t p[12] = { 'C', 'P', 'I', '1' };
    p[4] = vi.xres & 0xff; p[5] = (vi.xres >> 8) & 0xff; p[6] = vi.yres & 0xff; p[7] = (vi.yres >> 8) & 0xff;
    p[8] = (uint8_t)touch_mode; p[9] = key_n > 0;
    write_all(1, p, sizeof p);
}

static inline uint32_t chan(uint32_t px, const struct fb_bitfield *f) {
    uint32_t v = (px >> f->offset) & ((1u << f->length) - 1);
    return f->length >= 8 ? v >> (f->length - 8) : v << (8 - f->length);
}

static long now_ms(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return ts.tv_sec * 1000L + ts.tv_nsec / 1000000L; }

int main(int argc, char **argv) {
    if (argc < 2 || strcmp(argv[1], "stream")) { fprintf(stderr, "usage: cprec stream [maxdim] [interval_ms]\n"); return 2; }
    int maxdim = argc > 2 ? atoi(argv[2]) : 1600, interval = argc > 3 ? atoi(argv[3]) : 150;
    if (maxdim < 160) maxdim = 160;
    if (interval < 30) interval = 30;
    if (fb_open() < 0) { fprintf(stderr, "CPREC_ERR fb: %s\n", strerror(errno)); return 3; }
    scan_inputs();
    send_info();

    int bpp = vi.bits_per_pixel / 8;
    int longest = vi.xres > vi.yres ? (int)vi.xres : (int)vi.yres, f = (longest + maxdim - 1) / maxdim;
    if (f < 1) f = 1;
    int w = (int)vi.xres / f, h = (int)vi.yres / f;
    uint8_t *cur = malloc((size_t)w * h * 3), *prev = malloc((size_t)w * h * 3), *out = malloc((size_t)w * h * 4 + 16);
    if (!cur || !prev || !out || bpp < 2 || bpp > 4) { fprintf(stderr, "CPREC_ERR format %d bpp\n", vi.bits_per_pixel); return 3; }
    int have_prev = 0, frames = 0;
    long next = now_ms();

    for (;;) {
        struct pollfd pf = { .fd = 0, .events = POLLIN };
        long wait = next - now_ms();
        int pr = poll(&pf, 1, wait > 0 ? (int)wait : 0);
        if (pr > 0) {
            uint8_t c[8];
            size_t got = 0;
            while (got < sizeof c) { ssize_t r = read(0, c + got, sizeof c - got); if (r <= 0) return 0; got += (size_t)r; } // PC đóng kết nối
            int x = c[2] | (c[3] << 8), y = c[4] | (c[5] << 8);
            if (c[0] == 'T') touch(c[1], x, y);
            else if (c[0] == 'K') key(x, c[1]);
            else if (c[0] == 'U') { if (touch_fd < 0) make_uinput(); send_info(); }
            else if (c[0] == 'F') have_prev = 0;
            next = now_ms() + 25; // vừa có thao tác: chụp sớm
            continue;
        }
        next = now_ms() + interval;
        // recovery khởi động lại / đổi chế độ màn hình có thể đổi vùng nhớ: ánh xạ lại định kỳ
        if (frames % 20 == 0 && fb_open() < 0) { usleep(300000); continue; }
        frames++;
        struct fb_var_screeninfo v2;
        if (ioctl(fb_fd, FBIOGET_VSCREENINFO, &v2) == 0) vi.yoffset = v2.yoffset;
        size_t base = (size_t)vi.yoffset * fi.line_length;
        if (base + (size_t)fi.line_length * vi.yres > fb_len) base = 0;
        uint8_t *o = cur;
        for (int yy = 0; yy < h; yy++) {
            const uint8_t *row = fb_map + base + (size_t)(yy * f) * fi.line_length;
            for (int xx = 0; xx < w; xx++) {
                const uint8_t *p = row + (size_t)(xx * f) * bpp;
                uint32_t px = bpp == 4 ? (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24
                            : bpp == 3 ? (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 : (uint32_t)p[0] | (uint32_t)p[1] << 8;
                *o++ = (uint8_t)chan(px, &vi.blue); *o++ = (uint8_t)chan(px, &vi.green); *o++ = (uint8_t)chan(px, &vi.red);
            }
        }
        if (have_prev && memcmp(cur, prev, (size_t)w * h * 3) == 0) continue;
        // RLE
        size_t n = (size_t)w * h, i = 0, len = 0;
        uint8_t *q = out + 12;
        while (i < n) {
            const uint8_t *p = cur + i * 3;
            size_t run = 1;
            while (run < 255 && i + run < n && p[run * 3] == p[0] && p[run * 3 + 1] == p[1] && p[run * 3 + 2] == p[2]) run++;
            q[len++] = (uint8_t)run; q[len++] = p[0]; q[len++] = p[1]; q[len++] = p[2];
            i += run;
        }
        memcpy(out, "CPF1", 4);
        out[4] = w & 0xff; out[5] = (w >> 8) & 0xff; out[6] = h & 0xff; out[7] = (h >> 8) & 0xff;
        out[8] = len & 0xff; out[9] = (len >> 8) & 0xff; out[10] = (len >> 16) & 0xff; out[11] = (len >> 24) & 0xff;
        if (write_all(1, out, len + 12) < 0) return 0;
        uint8_t *t = prev; prev = cur; cur = t; have_prev = 1;
    }
}
