namespace PazeE.Compiler.Runtime;

/// <summary>内置标准库声明（paze.h 内容）与 libc 函数元数据。
/// 这些函数由各平台写入器解析为对应动态库导入（Windows: msvcrt.dll/kernel32.dll；Linux: libc.so.6；macOS: libSystem.dylib）。</summary>
public static class LibcDecls
{
    /// <summary>虚拟系统头 paze.h 的源码，编译时自动注入。</summary>
    public const string PazeHeader = """
#ifndef PAZE_H
#define PAZE_H

/* I/O */
int printf(const char *fmt, ...);
int fprintf(void *fp, const char *fmt, ...);
int sprintf(char *buf, const char *fmt, ...);
int scanf(const char *fmt, ...);
int sscanf(const char *s, const char *fmt, ...);
int puts(const char *s);
int putchar(int c);
int getchar(void);
int fputs(const char *s, void *fp);
char *fgets(char *buf, int n, void *fp);

/* 内存 */
void *malloc(unsigned long n);
void *calloc(unsigned long n, unsigned long sz);
void free(void *p);
void *memcpy(void *d, const void *s, unsigned long n);
void *memset(void *d, int c, unsigned long n);
int memcmp(const void *a, const void *b, unsigned long n);

/* 字符串 */
unsigned long strlen(const char *s);
char *strcpy(char *d, const char *s);
char *strncpy(char *d, const char *s, unsigned long n);
char *strcat(char *d, const char *s);
int strcmp(const char *a, const char *b);
int atoi(const char *s);
long atol(const char *s);

/* 进程 */
void exit(int code);
void abort(void);

/* 时间 —— time_get_utc() 默认 UTC+8；time_get_utc_tz(tz) 指定时区（分钟偏移，如 480=UTC+8，0=UTC，-480=UTC-8） */
struct paze_tm { int year, month, day, hour, minute, second, weekday; };

/* 底层 UTC 原始秒数（无时区偏移）：各平台实现 */
#ifdef _WIN32
extern long long _time64(void *timer);
long long time_utc_raw(void) { return _time64(0); }
#elif defined(__linux__)
extern long long time(void *t);
long long time_utc_raw(void) { return time(0); }
#elif defined(__leonos__)
/* LeonOS 4：由 Los4Runtime 静态生成（open /dev/leonos_system + ioctl TIME_INFO） */
long long time_utc_raw(void);
#else
long long time_utc_raw(void) { return 0; }
#endif

long long time_get_utc_tz(int tz_minutes) {
    return time_utc_raw() + (long long)tz_minutes * 60;
}
long long time_get_utc(void) {
    return time_get_utc_tz(480);
}

void time_get_tm_tz(struct paze_tm *out, int tz_minutes) {
    long long t = time_get_utc_tz(tz_minutes);
    long long days = t / 86400;
    long long secs = t % 86400;
    if (secs < 0) { secs += 86400; days -= 1; }
    out->hour = (int)(secs / 3600);
    out->minute = (int)((secs % 3600) / 60);
    out->second = (int)(secs % 60);
    out->weekday = (int)(((days % 7) + 4 + 7) % 7);  /* 1970-01-01 = 周四(4) */
    long long year = 1970;
    int is_leap;
    long long dy;
    while (1) {
        is_leap = ((year % 4 == 0 && year % 100 != 0) || year % 400 == 0);
        dy = is_leap ? 366 : 365;
        if (days < dy) break;
        days -= dy;
        year++;
    }
    out->year = (int)year;
    int mdays[12];
    mdays[0] = 31; mdays[1] = is_leap ? 29 : 28; mdays[2] = 31;
    mdays[3] = 30; mdays[4] = 31; mdays[5] = 30;
    mdays[6] = 31; mdays[7] = 31; mdays[8] = 30;
    mdays[9] = 31; mdays[10] = 30; mdays[11] = 31;
    int month = 0;
    while (days >= mdays[month]) { days -= mdays[month]; month++; }
    out->month = month + 1;
    out->day = (int)(days + 1);
}
void time_get_tm(struct paze_tm *out) {
    time_get_tm_tz(out, 480);
}

/* ============ GUI ============ */
struct gui_event { int type, key, mouse_x, mouse_y, button, window_id; };

#ifdef _WIN32
/* ---- Win32 外部函数（user32/gdi32/dwmapi）---- */
extern int RegisterClassA(void *wc);
extern void *CreateWindowExA(unsigned int, const char *, const char *, unsigned int,
    int, int, int, int, void *, void *, void *, void *);
extern int ShowWindow(void *, int);
extern int UpdateWindow(void *);
extern int GetMessageA(void *, void *, unsigned int, unsigned int);
extern int PeekMessageA(void *, void *, unsigned int, unsigned int, unsigned int);
extern int TranslateMessage(void *);
extern long long DispatchMessageA(void *);
extern long long DefWindowProcA(void *, unsigned int, unsigned long long, long long);
extern int DestroyWindow(void *);
extern void PostQuitMessage(int);
extern void *BeginPaint(void *, void *);
extern int EndPaint(void *, void *);
extern unsigned int SetTextColor(void *, unsigned int);
extern unsigned int SetBkColor(void *, unsigned int);
extern int TextOutA(void *, int, int, const char *, int);
extern int InvalidateRect(void *, void *, int);
extern int DwmSetWindowAttribute(void *, int, void *, int);

/* ---- Win32 常量 ---- */
#define WS_OVERLAPPEDWINDOW 0x00CF0000
#define SW_SHOW 5
#define WM_PAINT 0x000F
#define WM_DESTROY 0x0002
#define WM_KEYDOWN 0x0100
#define WM_LBUTTONDOWN 0x0201
#define WM_QUIT 0x0012

/* ---- 内部结构体（布局与 Win64 WNDCLASSA / MSG / PAINTSTRUCT 一致）---- */
struct paze_wc {
    unsigned int style;       /* 0  */
    long long wndproc;        /* 8  */
    int cbClsExtra;           /* 16 */
    int cbWndExtra;           /* 20 */
    long long hInstance;      /* 24 */
    long long hIcon;          /* 32 */
    long long hCursor;        /* 40 */
    long long hbrBackground;  /* 48 */
    long long menuName;       /* 56 */
    long long className;      /* 64 */
};
struct paze_msg {
    void *hwnd;               /* 0  */
    unsigned int message;     /* 8  */
    unsigned long long wParam;/* 16 */
    long long lParam;         /* 24 */
    unsigned int time;        /* 32 */
    int pt_x;                 /* 36 */
    int pt_y;                 /* 40 */
};
struct paze_ps { void *hdc; char rest[64]; };

/* ---- 内部状态 ---- */
static void *gui_hwnd;
static char gui_text[1024];
static int gui_text_x, gui_text_y;
static unsigned int gui_fg, gui_bg;
static int gui_class_registered;
static char gui_class_name[] = "PazeEWindow";

/* ---- 窗口过程（Win64 ABI 兼容：保存 rbp，仅用 caller-saved 寄存器）---- */
static long long paze_wndproc(void *h, unsigned int msg, unsigned long long w, long long l) {
    if (msg == WM_PAINT) {
        struct paze_ps ps;
        void *dc = BeginPaint(h, &ps);
        SetTextColor(dc, gui_fg);
        SetBkColor(dc, gui_bg);
        int len = 0;
        while (gui_text[len]) len++;
        TextOutA(dc, gui_text_x, gui_text_y, gui_text, len);
        EndPaint(h, &ps);
        return 0;
    }
    if (msg == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcA(h, msg, w, l);
}

int gui_init(void) {
    if (gui_class_registered) return 0;
    struct paze_wc wc;
    memset(&wc, 0, sizeof(struct paze_wc));
    wc.style = 3;               /* CS_HREDRAW | CS_VREDRAW */
    wc.wndproc = paze_wndproc;
    wc.hbrBackground = 6;       /* COLOR_WINDOW + 1 */
    wc.className = gui_class_name;
    if (RegisterClassA(&wc) == 0) return -1;
    gui_class_registered = 1;
    return 0;
}

int gui_window_create(const char *title, int width, int height) {
    if (gui_init() < 0) return -1;
    gui_hwnd = CreateWindowExA(0, gui_class_name, title, WS_OVERLAPPEDWINDOW,
        100, 100, width, height, 0, 0, 0, 0);
    if (gui_hwnd == 0) return -1;
    /* Win11 DWM：圆角 + 暗色标题栏 */
    int corner = 2;             /* DWMWCP_ROUND */
    DwmSetWindowAttribute(gui_hwnd, 33, &corner, 4);
    int dark = 1;               /* DWMWA_USE_IMMERSIVE_DARK_MODE */
    DwmSetWindowAttribute(gui_hwnd, 20, &dark, 4);
    gui_fg = 0;
    gui_bg = 0xFFFFFF;
    gui_text[0] = 0;
    ShowWindow(gui_hwnd, SW_SHOW);
    UpdateWindow(gui_hwnd);
    return 0;
}

int gui_window_text(int win, int x, int y, const char *text, unsigned int fg, unsigned int bg) {
    gui_text_x = x;
    gui_text_y = y;
    gui_fg = fg;
    gui_bg = bg;
    int i = 0;
    while (text[i] && i < 1023) { gui_text[i] = text[i]; i++; }
    gui_text[i] = 0;
    if (gui_hwnd) InvalidateRect(gui_hwnd, 0, 1);
    return 0;
}

int gui_window_present(int win) {
    if (gui_hwnd) UpdateWindow(gui_hwnd);
    return 0;
}

int gui_event_poll(struct gui_event *ev) {
    struct paze_msg msg;
    ev->type = 0;
    ev->window_id = 0;
    if (PeekMessageA(&msg, 0, 0, 0, 1)) {
        TranslateMessage(&msg);
        DispatchMessageA(&msg);
        if (msg.message == WM_QUIT) { ev->type = 4; return 1; }
        if (msg.message == WM_DESTROY) { ev->type = 1; return 1; }
        if (msg.message == WM_KEYDOWN) { ev->type = 2; ev->key = (int)msg.wParam; return 1; }
        if (msg.message == WM_LBUTTONDOWN) { ev->type = 3; ev->mouse_x = msg.pt_x; ev->mouse_y = msg.pt_y; ev->button = 1; return 1; }
        return 1;
    }
    return 0;
}

int gui_event_wait(struct gui_event *ev, int timeout_ms) {
    struct paze_msg msg;
    ev->window_id = 0;
    int r = GetMessageA(&msg, 0, 0, 0);
    if (r <= 0) { ev->type = 4; return 1; }
    TranslateMessage(&msg);
    DispatchMessageA(&msg);
    if (msg.message == WM_QUIT) { ev->type = 4; return 1; }
    if (msg.message == WM_DESTROY) { ev->type = 1; return 1; }
    if (msg.message == WM_KEYDOWN) { ev->type = 2; ev->key = (int)msg.wParam; return 1; }
    if (msg.message == WM_LBUTTONDOWN) { ev->type = 3; ev->mouse_x = msg.pt_x; ev->mouse_y = msg.pt_y; ev->button = 1; return 1; }
    ev->type = 0;
    return 1;
}

int gui_window_destroy(int win) {
    if (gui_hwnd) { DestroyWindow(gui_hwnd); gui_hwnd = 0; }
    return 0;
}

int gui_cleanup(void) {
    PostQuitMessage(0);
    return 0;
}

#elif defined(__linux__)
/* Linux X11 GUI — libX11 动态链接（ElfWriter 多 DT_NEEDED）*/
extern void *XOpenDisplay(const char *name);
extern int XCloseDisplay(void *disp);
extern int XDefaultScreen(void *disp);
extern unsigned long XRootWindow(void *disp, int screen);
extern unsigned long XWhitePixel(void *disp, int screen);
extern unsigned long XBlackPixel(void *disp, int screen);
extern unsigned long XCreateSimpleWindow(void *disp, unsigned long parent,
    int x, int y, unsigned int w, unsigned int h, unsigned int border,
    unsigned long border_pixel, unsigned long bg_pixel);
extern int XDestroyWindow(void *disp, unsigned long win);
extern int XStoreName(void *disp, unsigned long win, const char *name);
extern int XSelectInput(void *disp, unsigned long win, long event_mask);
extern int XMapWindow(void *disp, unsigned long win);
extern int XFlush(void *disp);
extern void *XDefaultGC(void *disp, int screen);
extern int XSetForeground(void *disp, void *gc, unsigned long color);
extern int XSetBackground(void *disp, void *gc, unsigned long color);
extern int XDrawString(void *disp, unsigned long win, void *gc, int x, int y,
    const char *str, int len);
extern int XPending(void *disp);
extern int XNextEvent(void *disp, void *event);
extern unsigned long XInternAtom(void *disp, const char *name, int only_if_exists);
extern int XSetWMProtocols(void *disp, unsigned long win, unsigned long *protocols, int count);

#define PAZE_KeyPressMask   (1L<<0)
#define PAZE_ButtonPressMask (1L<<2)
#define PAZE_ExposureMask   (1L<<15)
#define PAZE_StructureNotifyMask (1L<<17)

/* XEvent 缓冲（XEvent 联合体最大 192 字节）*/
struct paze_xevent { int type; char raw[188]; };

static void *gui_display;
static int gui_screen;
static unsigned long gui_window;
static void *gui_gc;
static unsigned long gui_wm_delete;

/* 把 X11 事件翻译为 gui_event。
   XEvent 字段偏移（X11 协议固定布局）：
   KeyPress(2):      keycode @ 84 (unsigned int)
   ButtonPress(4):   x @ 64, y @ 68, button @ 80
   ClientMessage(33): data.l[0] @ 40 (Atom) */
static void gui_translate_event(struct paze_xevent *xev, struct gui_event *ev) {
    char *base = (char*)xev;
    if (xev->type == 2) {            /* KeyPress */
        ev->type = 2;
        ev->key = *(int*)(base + 84);
    } else if (xev->type == 4) {     /* ButtonPress */
        ev->type = 3;
        ev->mouse_x = *(int*)(base + 64);
        ev->mouse_y = *(int*)(base + 68);
        ev->button = *(int*)(base + 80);
    } else if (xev->type == 33) {    /* ClientMessage — WM_DELETE_WINDOW */
        long long d0 = *(long long*)(base + 40);
        if (d0 == (long long)gui_wm_delete) ev->type = 1;
        else ev->type = 0;
    } else {
        ev->type = 0;
    }
}

int gui_init(void) {
    if (gui_display) return 0;
    gui_display = XOpenDisplay(0);
    if (!gui_display) return -1;
    gui_screen = XDefaultScreen(gui_display);
    gui_gc = XDefaultGC(gui_display, gui_screen);
    gui_wm_delete = XInternAtom(gui_display, "WM_DELETE_WINDOW", 1);
    return 0;
}

int gui_window_create(const char *title, int width, int height) {
    if (gui_init() < 0) return -1;
    unsigned long root = XRootWindow(gui_display, gui_screen);
    unsigned long bg = XWhitePixel(gui_display, gui_screen);
    gui_window = XCreateSimpleWindow(gui_display, root, 100, 100,
        (unsigned int)width, (unsigned int)height, 0, bg, bg);
    XStoreName(gui_display, gui_window, title);
    XSelectInput(gui_display, gui_window,
        PAZE_ExposureMask | PAZE_KeyPressMask | PAZE_ButtonPressMask | PAZE_StructureNotifyMask);
    if (gui_wm_delete) {
        unsigned long prots[1];
        prots[0] = gui_wm_delete;
        XSetWMProtocols(gui_display, gui_window, prots, 1);
    }
    XMapWindow(gui_display, gui_window);
    XFlush(gui_display);
    return 0;
}

int gui_window_text(int win, int x, int y, const char *text, unsigned int fg, unsigned int bg) {
    if (!gui_display) return -1;
    XSetForeground(gui_display, gui_gc, (unsigned long)fg);
    XSetBackground(gui_display, gui_gc, (unsigned long)bg);
    int len = 0;
    while (text[len]) len++;
    XDrawString(gui_display, gui_window, gui_gc, x, y, text, len);
    XFlush(gui_display);
    return 0;
}

int gui_window_present(int win) {
    if (gui_display) XFlush(gui_display);
    return 0;
}

int gui_event_poll(struct gui_event *ev) {
    if (!gui_display) { ev->type = 0; return 0; }
    ev->type = 0; ev->window_id = 0; ev->key = 0;
    if (XPending(gui_display) > 0) {
        struct paze_xevent xev;
        XNextEvent(gui_display, &xev);
        gui_translate_event(&xev, ev);
    }
    return ev->type != 0 ? 1 : 0;
}

int gui_event_wait(struct gui_event *ev, int timeout_ms) {
    if (!gui_display) { ev->type = 4; return 1; }
    ev->type = 0; ev->window_id = 0; ev->key = 0;
    struct paze_xevent xev;
    XNextEvent(gui_display, &xev);    /* 阻塞等待 */
    gui_translate_event(&xev, ev);
    return 1;
}

int gui_window_destroy(int win) {
    if (gui_display && gui_window) {
        XDestroyWindow(gui_display, gui_window);
        XFlush(gui_display);
        gui_window = 0;
    }
    return 0;
}

int gui_cleanup(void) {
    if (gui_display) { XCloseDisplay(gui_display); gui_display = 0; }
    return 0;
}

#elif defined(__APPLE__)
/* ============ macOS Cocoa GUI via Objective-C runtime ============ */
/* 所有 ObjC runtime 函数在 libSystem.dylib 中，AppKit 通过 dlopen 动态加载。
   x86-64: NSRect(32B) 需按值在栈上传递，但 PazeE 按指针传结构体，
   故 paze_mac_send_rect 蹦床（MachOWriter 生成）处理 ABI 转换。
   ARM64: NSRect(32B)>16B 按 AAPCS64 也按指针传递，与 PazeE 一致，蹦床退化为 b objc_msgSend。 */
extern void *dlopen(const char *name, int flags);
extern void *objc_getClass(const char *name);
extern void *sel_registerName(const char *name);
extern long long objc_msgSend(void *self, void *sel, ...);
extern void *objc_allocateClassPair(void *superclass, const char *name, unsigned long extra);
extern int class_addMethod(void *cls, void *sel, void *imp, const char *types);
extern void objc_registerClassPair(void *cls);
/* 蹦床：把 rect_ptr 指向的 32 字节 NSRect 按值传递给 objc_msgSend */
extern long long paze_mac_send_rect(void *recv, void *sel, void *rect_ptr,
    long long a4, long long a5, long long a6);

#define RTLD_NOW 2
#define NSWindowStyleMaskTitled 1
#define NSWindowStyleMaskClosable 2
#define NSWindowStyleMaskMiniaturizable 4
#define NSWindowStyleMaskResizable 8
#define NSBackingStoreBuffered 2
#define NSKeyDown 10
#define NSApplicationDefined 15

/* int → IEEE 754 double 位模式（PazeE 不支持 double，用整数构造位模式） */
static long long paze_int_to_double(int n) {
    if (n == 0) return 0;
    int sign = 0;
    unsigned int val = (unsigned int)n;
    if (n < 0) { sign = 1; val = (unsigned int)(-n); }
    int e = 0;
    unsigned int tmp = val;
    while (tmp > 1) { tmp >>= 1; e++; }
    long long mantissa;
    if (e <= 52) mantissa = (long long)(val & ((1u << e) - 1)) << (52 - e);
    else mantissa = (long long)(val >> (e - 52));
    long long bits = ((long long)(1023 + e) << 52) | mantissa;
    if (sign) bits |= (1LL << 63);
    return bits;
}

static void *gui_app;
static void *gui_window;
static void *gui_textview;
static int gui_initialized;

/* 选择器（编译期无法注册，运行时 sel_registerName） */
static void *sel_sharedApplication;
static void *sel_setActivationPolicy;
static void *sel_activateIgnoringOtherApps;
static void *sel_alloc;
static void *sel_init;
static void *sel_initWithContentRect;
static void *sel_setTitle;
static void *sel_setContentView;
static void *sel_makeKeyAndOrderFront;
static void *sel_stringWithUTF8String;
static void *sel_initWithFrame;
static void *sel_setString;
static void *sel_run;
static void *sel_nextEventMatchingMask;
static void *sel_sendEvent;
static void *sel_type;
static void *sel_keyCode;
static void *sel_setDelegate;
static void *sel_distantFuture;
static void *sel_close;
static void *sel_terminate;

/* AppDelegate: windowShouldClose → terminate */
static long long paze_app_terminate(void *self, void *cmd, void *sender) {
    return 1;
}

static void gui_register_sels(void) {
    sel_sharedApplication = sel_registerName("sharedApplication");
    sel_setActivationPolicy = sel_registerName("setActivationPolicy:");
    sel_activateIgnoringOtherApps = sel_registerName("activateIgnoringOtherApps:");
    sel_alloc = sel_registerName("alloc");
    sel_init = sel_registerName("init");
    sel_initWithContentRect = sel_registerName("initWithContentRect:styleMask:backing:defer:");
    sel_setTitle = sel_registerName("setTitle:");
    sel_setContentView = sel_registerName("setContentView:");
    sel_makeKeyAndOrderFront = sel_registerName("makeKeyAndOrderFront:");
    sel_stringWithUTF8String = sel_registerName("stringWithUTF8String:");
    sel_initWithFrame = sel_registerName("initWithFrame:");
    sel_setString = sel_registerName("setString:");
    sel_run = sel_registerName("run");
    sel_nextEventMatchingMask = sel_registerName("nextEventMatchingMask:untilDate:inMode:dequeue:");
    sel_sendEvent = sel_registerName("sendEvent:");
    sel_type = sel_registerName("type");
    sel_keyCode = sel_registerName("keyCode");
    sel_setDelegate = sel_registerName("setDelegate:");
    sel_distantFuture = sel_registerName("distantFuture");
    sel_close = sel_registerName("close");
    sel_terminate = sel_registerName("terminate:");
}

int gui_init(void) {
    if (gui_initialized) return 0;
    dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);
    void *appClass = objc_getClass("NSApplication");
    if (!appClass) return -1;
    gui_app = (void *)objc_msgSend(appClass, sel_sharedApplication);
    objc_msgSend(gui_app, sel_setActivationPolicy, 0);
    gui_register_sels();
    /* AppDelegate: applicationShouldTerminateAfterLastWindowClosed: → YES */
    void *nsObj = objc_getClass("NSObject");
    void *dlgCls = objc_allocateClassPair(nsObj, "PazeAppDelegate", 0);
    class_addMethod(dlgCls, sel_registerName("applicationShouldTerminateAfterLastWindowClosed:"),
        (void *)paze_app_terminate, "q@:@");
    objc_registerClassPair(dlgCls);
    void *dlg = (void *)objc_msgSend(dlgCls, sel_alloc);
    dlg = (void *)objc_msgSend(dlg, sel_init);
    objc_msgSend(gui_app, sel_setDelegate, dlg);
    objc_msgSend(gui_app, sel_activateIgnoringOtherApps, 1);
    gui_initialized = 1;
    return 0;
}

int gui_window_create(const char *title, int width, int height) {
    if (gui_init() < 0) return -1;
    void *winCls = objc_getClass("NSWindow");
    if (!winCls) return -1;
    long long rect[4];
    rect[0] = paze_int_to_double(100);
    rect[1] = paze_int_to_double(100);
    rect[2] = paze_int_to_double(width);
    rect[3] = paze_int_to_double(height);
    unsigned long style = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
                        | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable;
    void *win = (void *)objc_msgSend(winCls, sel_alloc);
    win = (void *)paze_mac_send_rect(win, sel_initWithContentRect, rect,
        (long long)style, (long long)NSBackingStoreBuffered, 0);
    void *strCls = objc_getClass("NSString");
    void *titleStr = (void *)objc_msgSend(strCls, sel_stringWithUTF8String, title);
    objc_msgSend(win, sel_setTitle, titleStr);
    gui_window = win;
    /* NSTextView 显示文字 */
    void *tvCls = objc_getClass("NSTextView");
    long long tvRect[4];
    tvRect[0] = paze_int_to_double(0);
    tvRect[1] = paze_int_to_double(0);
    tvRect[2] = paze_int_to_double(width);
    tvRect[3] = paze_int_to_double(height);
    void *tv = (void *)objc_msgSend(tvCls, sel_alloc);
    tv = (void *)paze_mac_send_rect(tv, sel_initWithFrame, tvRect, 0, 0, 0);
    gui_textview = tv;
    objc_msgSend(win, sel_setContentView, tv);
    objc_msgSend(win, sel_makeKeyAndOrderFront, 0);
    return 0;
}

int gui_window_text(int win, int x, int y, const char *text, unsigned int fg, unsigned int bg) {
    if (!gui_textview) return -1;
    void *strCls = objc_getClass("NSString");
    void *str = (void *)objc_msgSend(strCls, sel_stringWithUTF8String, text);
    objc_msgSend(gui_textview, sel_setString, str);
    return 0;
}

int gui_window_present(int win) { return 0; }

int gui_event_poll(struct gui_event *ev) {
    ev->type = 0; ev->window_id = 0; ev->key = 0;
    if (!gui_app) { ev->type = 4; return 1; }
    /* nil untilDate = 非阻塞 */
    void *event = (void *)objc_msgSend(gui_app, sel_nextEventMatchingMask,
        (long long)0xFFFFFFFF, 0, 0, 1);
    if (!event) return 0;
    int et = (int)objc_msgSend(event, sel_type);
    objc_msgSend(gui_app, sel_sendEvent, event);
    if (et == NSKeyDown) { ev->type = 2; ev->key = (int)objc_msgSend(event, sel_keyCode); }
    else if (et == NSApplicationDefined) { ev->type = 4; return 1; }
    return ev->type != 0 ? 1 : 0;
}

int gui_event_wait(struct gui_event *ev, int timeout_ms) {
    ev->type = 0; ev->window_id = 0; ev->key = 0;
    if (!gui_app) { ev->type = 4; return 1; }
    void *dateCls = objc_getClass("NSDate");
    void *future = (void *)objc_msgSend(dateCls, sel_distantFuture);
    void *event = (void *)objc_msgSend(gui_app, sel_nextEventMatchingMask,
        (long long)0xFFFFFFFF, future, 0, 1);
    if (!event) { ev->type = 4; return 1; }
    int et = (int)objc_msgSend(event, sel_type);
    objc_msgSend(gui_app, sel_sendEvent, event);
    if (et == NSKeyDown) { ev->type = 2; ev->key = (int)objc_msgSend(event, sel_keyCode); }
    else if (et == NSApplicationDefined) { ev->type = 4; return 1; }
    return 1;
}

int gui_window_destroy(int win) {
    if (gui_window) { objc_msgSend(gui_window, sel_close); gui_window = 0; }
    return 0;
}

int gui_cleanup(void) {
    if (gui_app) objc_msgSend(gui_app, sel_terminate, 0);
    return 0;
}

#elif defined(__leonos__)
/* Los4 GUI — 阶段5 实现（Los4Runtime 静态生成 + ioctl）*/
int gui_init(void);
int gui_window_create(const char *title, int width, int height);
int gui_window_text(int win, int x, int y, const char *text, unsigned int fg, unsigned int bg);
int gui_window_present(int win);
int gui_event_poll(struct gui_event *ev);
int gui_event_wait(struct gui_event *ev, int timeout_ms);
int gui_window_destroy(int win);
int gui_cleanup(void);

#else
/* 不支持 GUI 的平台 — 桩实现 */
int gui_init(void) { return -1; }
int gui_window_create(const char *title, int width, int height) { return -1; }
int gui_window_text(int win, int x, int y, const char *text, unsigned int fg, unsigned int bg) { return -1; }
int gui_window_present(int win) { return -1; }
int gui_event_poll(struct gui_event *ev) { ev->type = 0; return 0; }
int gui_event_wait(struct gui_event *ev, int timeout_ms) { ev->type = 0; return 0; }
int gui_window_destroy(int win) { return -1; }
int gui_cleanup(void) { return -1; }
#endif

#endif
""";

    /// <summary>已知变参 libc 函数（System V ABI 下调用前需置 AL=0）。</summary>
    public static readonly HashSet<string> Variadic = new()
    {
        "printf", "fprintf", "sprintf", "scanf", "sscanf"
    };

    /// <summary>Windows 下函数所属 DLL（默认 msvcrt.dll）。</summary>
    public static string DllOf(string func) => func switch
    {
        "ExitProcess" or "GetCommandLineA" or "GetStdHandle" or "WriteConsoleA" or "WriteFile" => "kernel32.dll",
        // user32.dll — 窗口、消息、绘制
        "RegisterClassA" or "CreateWindowExA" or "ShowWindow" or "UpdateWindow"
            or "GetMessageA" or "PeekMessageA" or "TranslateMessage" or "DispatchMessageA"
            or "DefWindowProcA" or "DestroyWindow" or "PostQuitMessage"
            or "BeginPaint" or "EndPaint" or "GetDC" or "ReleaseDC" or "InvalidateRect"
            or "SetWindowTextA" => "user32.dll",
        // gdi32.dll — 文本绘制、颜色
        "SetTextColor" or "SetBkColor" or "TextOutA" => "gdi32.dll",
        // dwmapi.dll — Win11 DWM 圆角/暗色模式
        "DwmSetWindowAttribute" => "dwmapi.dll",
        _ => "msvcrt.dll"
    };

    /// <summary>Linux 下所需动态库。</summary>
    public const string LinuxLibc = "libc.so.6";

    /// <summary>Linux 下函数所属动态库（X11 函数 → libX11.so.6，其余 → libc.so.6）。</summary>
    public static string LinuxLibOf(string func) => func switch
    {
        "XOpenDisplay" or "XCloseDisplay" or "XDefaultScreen" or "XRootWindow"
            or "XWhitePixel" or "XBlackPixel" or "XCreateSimpleWindow" or "XDestroyWindow"
            or "XStoreName" or "XSelectInput" or "XMapWindow" or "XFlush"
            or "XDefaultGC" or "XSetForeground" or "XSetBackground" or "XDrawString"
            or "XPending" or "XNextEvent" or "XInternAtom" or "XSetWMProtocols" => "libX11.so.6",
        _ => LinuxLibc
    };

    /// <summary>macOS 下所需动态库。</summary>
    public const string DarwinLib = "/usr/lib/libSystem.dylib";
}
