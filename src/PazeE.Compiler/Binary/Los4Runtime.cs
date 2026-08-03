namespace PazeE.Compiler.Binary;

/// <summary>LeonOS 4 静态运行时：通过 syscall 指令提供标准 C 库函数实现。
/// 所有函数以 System V AMD64 ABI 调用，注入到 .text 段末尾，由 ElfWriterLos4 解析外部符号引用。
/// LeonOS 4 系统调用约定与 Linux x86_64 一致（rax=调用号, rdi/rsi/rdx/r10/r8/r9=参数），使用 syscall 指令（0x0F 0x05）。
/// GUI 设备由 LeonOS shell 预打开为 fd=3，所有 GUI 函数直接使用 fd=3（与 libc.a 行为一致）。</summary>
public static class Los4Runtime
{
    public const int SYS_read = 0;
    public const int SYS_write = 1;
    public const int SYS_open = 2;
    public const int SYS_close = 3;
    public const int SYS_ioctl = 16;
    public const int SYS_mmap = 9;
    public const int SYS_exit = 60;

    // LeonOS 4 设备路径与 ioctl 码（来自 leonos/system.h）
    private const string Los4SystemDevice = "0:/dev/leonos_system";
    private const long LEONOS_IOCTL_TIME_INFO = 0x4c54494dL;

    // LeonOS 4 GUI 设备路径与 ioctl 码（来自 leonos/gui.h）
    private const string Los4GuiDevice = "0:/dev/leonos_gui";
    private const long LEONOS_GUI_IOCTL_CREATE_WINDOW   = 0x4c475743L;
    private const long LEONOS_GUI_IOCTL_FB_TEXT         = 0x4c464254L;
    private const long LEONOS_GUI_IOCTL_WINDOW_EVENT    = 0x4c475745L;
    private const long LEONOS_GUI_IOCTL_WAIT_WINDOW_EVENT = 0x4c475457L;
    private const long LEONOS_GUI_IOCTL_DESTROY_WINDOW  = 0x4c475744L;
    private const long LEONOS_GUI_IOCTL_PRESENT_WINDOW  = 0x4c475046L;

    // LeonOS GUI 应用事件类型（来自 gui.h LEONOS_GUI_APP_EVENT_*）
    private const int LEONOS_GUI_APP_EVENT_CLOSE        = 1;
    private const int LEONOS_GUI_APP_EVENT_MOUSE_BUTTON = 6;
    private const int LEONOS_GUI_APP_EVENT_KEY_DOWN     = 7;

    // mmap 相关常量（与 leonos/syscall.h 一致）
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int MAP_PRIVATE = 0x02;
    private const int MAP_ANONYMOUS = 0x20;

    /// <summary>PSF1 8×16 位图字体的 ASCII 部分（128 字形 × 16 字节 = 2048 字节），
    /// 从 lat15_vga16.psf 提取，用于 los2w 模拟器像素文本渲染（PRESENT_WINDOW）。</summary>
    private static readonly byte[] FontData = new byte[2048] {
        0x00,0x00,0x3C,0x42,0x99,0xA5,0xA1,0xA1,0xA5,0x99,0x42,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xFF,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xDB,0xDB,0x00,0x00,0x00,0x00,
        0x00,0x00,0xF1,0x5B,0x55,0x51,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x10,0x38,0x7C,0xFE,0x7C,0x38,0x10,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0xCC,0xCF,0xED,0xFF,0xFC,0xDF,0xCC,0xCC,0xCC,0xCC,0x00,0x00,0x00,0x00,
        0x00,0x00,0x03,0x3E,0x66,0xCF,0xDB,0xDB,0xF3,0x66,0x7C,0xC0,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x18,0x3C,0x3C,0x18,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x66,0x3C,0x66,0x66,0x66,0x3C,0x66,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x18,0x18,0x18,0x00,0x18,0x18,0x18,0x18,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x6C,0x6C,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x38,0x44,0xBA,0xB2,0xAA,0x44,0x38,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x7C,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x70,0xD8,0x30,0x18,0xD8,0x70,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x0C,0x18,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x18,0x0C,0x38,0x00,
        0x00,0x30,0x70,0x30,0x30,0x30,0x78,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0xE0,0x30,0x62,0x36,0xEC,0x18,0x30,0x66,0xCE,0x9E,0x3E,0x06,0x06,0x00,0x00,
        0x60,0x30,0x00,0x10,0x38,0x6C,0xC6,0xC6,0xFE,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x66,0x66,0x66,0x66,0x66,0x66,0x66,0x00,0x66,0x66,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7F,0xDB,0xDB,0xDB,0x7B,0x1B,0x1B,0x1B,0x1B,0x1B,0x00,0x00,0x00,0x00,
        0x00,0x7C,0xC6,0x60,0x38,0x6C,0xC6,0xC6,0x6C,0x38,0x0C,0xC6,0x7C,0x00,0x00,0x00,
        0x0C,0x18,0x00,0x10,0x38,0x6C,0xC6,0xC6,0xFE,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x10,0x38,0x6C,0x10,0x38,0x6C,0xC6,0xC6,0xFE,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x3C,0x7E,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x7E,0x3C,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x0C,0x06,0xFF,0x06,0x0C,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x30,0x60,0xFF,0x60,0x30,0x00,0x00,0x00,0x00,0x00,0x00,
        0x76,0xDC,0x00,0x10,0x38,0x6C,0xC6,0xC6,0xFE,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x30,0x18,0x00,0xFE,0x66,0x62,0x68,0x78,0x68,0x62,0x66,0xFE,0x00,0x00,0x00,0x00,
        0x10,0x38,0x44,0xFE,0x66,0x62,0x68,0x78,0x68,0x62,0x66,0xFE,0x00,0x00,0x00,0x00,
        0x6C,0x6C,0x00,0xFE,0x66,0x62,0x68,0x78,0x68,0x62,0x66,0xFE,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x3C,0x3C,0x3C,0x18,0x18,0x18,0x00,0x18,0x18,0x00,0x00,0x00,0x00,
        0x00,0x66,0x66,0x66,0x24,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x6C,0x6C,0xFE,0x6C,0x6C,0x6C,0xFE,0x6C,0x6C,0x00,0x00,0x00,0x00,
        0x18,0x18,0x7C,0xC6,0xC2,0xC0,0x7C,0x06,0x06,0x86,0xC6,0x7C,0x18,0x18,0x00,0x00,
        0x00,0x00,0x00,0x00,0xC2,0xC6,0x0C,0x18,0x30,0x60,0xC6,0x86,0x00,0x00,0x00,0x00,
        0x00,0x00,0x38,0x6C,0x6C,0x38,0x76,0xDC,0xCC,0xCC,0xCC,0x76,0x00,0x00,0x00,0x00,
        0x00,0x30,0x30,0x30,0x20,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x0C,0x18,0x30,0x30,0x30,0x30,0x30,0x30,0x18,0x0C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x30,0x18,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x18,0x30,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x66,0x3C,0xFF,0x3C,0x66,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x18,0x18,0x7E,0x18,0x18,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x18,0x18,0x18,0x30,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xFE,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x18,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x02,0x06,0x0C,0x18,0x30,0x60,0xC0,0x80,0x00,0x00,0x00,0x00,
        0x00,0x00,0x38,0x6C,0xC6,0xC6,0xD6,0xD6,0xC6,0xC6,0x6C,0x38,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x38,0x78,0x18,0x18,0x18,0x18,0x18,0x18,0x7E,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0x06,0x0C,0x18,0x30,0x60,0xC0,0xC6,0xFE,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0x06,0x06,0x3C,0x06,0x06,0x06,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x0C,0x1C,0x3C,0x6C,0xCC,0xFE,0x0C,0x0C,0x0C,0x1E,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFE,0xC0,0xC0,0xC0,0xFC,0x06,0x06,0x06,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x38,0x60,0xC0,0xC0,0xFC,0xC6,0xC6,0xC6,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFE,0xC6,0x06,0x06,0x0C,0x18,0x30,0x30,0x30,0x30,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0xC6,0xC6,0x7C,0xC6,0xC6,0xC6,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0xC6,0xC6,0x7E,0x06,0x06,0x06,0x0C,0x78,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x18,0x18,0x00,0x00,0x00,0x18,0x18,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x18,0x18,0x00,0x00,0x00,0x18,0x18,0x30,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x06,0x0C,0x18,0x30,0x60,0x30,0x18,0x0C,0x06,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x7E,0x00,0x00,0x7E,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x60,0x30,0x18,0x0C,0x06,0x0C,0x18,0x30,0x60,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0xC6,0x0C,0x18,0x18,0x18,0x00,0x18,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x7C,0xC6,0xC6,0xDE,0xDE,0xDE,0xDC,0xC0,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x10,0x38,0x6C,0xC6,0xC6,0xFE,0xC6,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFC,0x66,0x66,0x66,0x7C,0x66,0x66,0x66,0x66,0xFC,0x00,0x00,0x00,0x00,
        0x00,0x00,0x3C,0x66,0xC2,0xC0,0xC0,0xC0,0xC0,0xC2,0x66,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xF8,0x6C,0x66,0x66,0x66,0x66,0x66,0x66,0x6C,0xF8,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFE,0x66,0x62,0x68,0x78,0x68,0x60,0x62,0x66,0xFE,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFE,0x66,0x62,0x68,0x78,0x68,0x60,0x60,0x60,0xF0,0x00,0x00,0x00,0x00,
        0x00,0x00,0x3C,0x66,0xC2,0xC0,0xC0,0xDE,0xC6,0xC6,0x66,0x3A,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xC6,0xC6,0xC6,0xFE,0xC6,0xC6,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x3C,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x1E,0x0C,0x0C,0x0C,0x0C,0x0C,0xCC,0xCC,0xCC,0x78,0x00,0x00,0x00,0x00,
        0x00,0x00,0xE6,0x66,0x66,0x6C,0x78,0x78,0x6C,0x66,0x66,0xE6,0x00,0x00,0x00,0x00,
        0x00,0x00,0xF0,0x60,0x60,0x60,0x60,0x60,0x60,0x62,0x66,0xFE,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xEE,0xFE,0xFE,0xD6,0xC6,0xC6,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xE6,0xF6,0xFE,0xDE,0xCE,0xC6,0xC6,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFC,0x66,0x66,0x66,0x7C,0x60,0x60,0x60,0x60,0xF0,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xD6,0xDE,0x7C,0x0C,0x0E,0x00,0x00,
        0x00,0x00,0xFC,0x66,0x66,0x66,0x7C,0x6C,0x66,0x66,0x66,0xE6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7C,0xC6,0xC6,0x60,0x38,0x0C,0x06,0xC6,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x7E,0x7E,0x5A,0x18,0x18,0x18,0x18,0x18,0x18,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0x6C,0x38,0x10,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xC6,0xC6,0xC6,0xD6,0xD6,0xD6,0xFE,0xEE,0x6C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xC6,0xC6,0x6C,0x7C,0x38,0x38,0x7C,0x6C,0xC6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x66,0x66,0x66,0x66,0x3C,0x18,0x18,0x18,0x18,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0xFE,0xC6,0x86,0x0C,0x18,0x30,0x60,0xC2,0xC6,0xFE,0x00,0x00,0x00,0x00,
        0x00,0x00,0x3C,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x80,0xC0,0xE0,0x70,0x38,0x1C,0x0E,0x06,0x02,0x00,0x00,0x00,0x00,
        0x00,0x00,0x3C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x3C,0x00,0x00,0x00,0x00,
        0x10,0x38,0x6C,0xC6,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xFF,0x00,0x00,
        0x30,0x30,0x18,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x78,0x0C,0x7C,0xCC,0xCC,0xCC,0x76,0x00,0x00,0x00,0x00,
        0x00,0x00,0xE0,0x60,0x60,0x78,0x6C,0x66,0x66,0x66,0x66,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x7C,0xC6,0xC0,0xC0,0xC0,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x1C,0x0C,0x0C,0x3C,0x6C,0xCC,0xCC,0xCC,0xCC,0x76,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x7C,0xC6,0xFE,0xC0,0xC0,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x38,0x6C,0x64,0x60,0xF0,0x60,0x60,0x60,0x60,0xF0,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x76,0xCC,0xCC,0xCC,0xCC,0xCC,0x7C,0x0C,0xCC,0x78,0x00,
        0x00,0x00,0xE0,0x60,0x60,0x6C,0x76,0x66,0x66,0x66,0x66,0xE6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x18,0x00,0x38,0x18,0x18,0x18,0x18,0x18,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x06,0x06,0x00,0x0E,0x06,0x06,0x06,0x06,0x06,0x06,0x66,0x66,0x3C,0x00,
        0x00,0x00,0xE0,0x60,0x60,0x66,0x6C,0x78,0x78,0x6C,0x66,0xE6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x38,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x3C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xEC,0xFE,0xD6,0xD6,0xD6,0xD6,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xDC,0x66,0x66,0x66,0x66,0x66,0x66,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x7C,0xC6,0xC6,0xC6,0xC6,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xDC,0x66,0x66,0x66,0x66,0x66,0x7C,0x60,0x60,0xF0,0x00,
        0x00,0x00,0x00,0x00,0x00,0x76,0xCC,0xCC,0xCC,0xCC,0xCC,0x7C,0x0C,0x0C,0x1E,0x00,
        0x00,0x00,0x00,0x00,0x00,0xDC,0x76,0x66,0x60,0x60,0x60,0xF0,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x7C,0xC6,0x60,0x38,0x0C,0xC6,0x7C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x10,0x30,0x30,0xFC,0x30,0x30,0x30,0x30,0x36,0x1C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0x76,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x66,0x66,0x66,0x66,0x66,0x3C,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xC6,0xC6,0xD6,0xD6,0xD6,0xFE,0x6C,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xC6,0x6C,0x38,0x38,0x38,0x6C,0xC6,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xC6,0xC6,0xC6,0xC6,0xC6,0xC6,0x7E,0x06,0x0C,0xF8,0x00,
        0x00,0x00,0x00,0x00,0x00,0xFE,0xCC,0x18,0x30,0x60,0xC6,0xFE,0x00,0x00,0x00,0x00,
        0x00,0x00,0x0E,0x18,0x18,0x18,0x70,0x18,0x18,0x18,0x18,0x0E,0x00,0x00,0x00,0x00,
        0x00,0x00,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x00,0x00,0x00,0x00,
        0x00,0x00,0x70,0x18,0x18,0x18,0x0E,0x18,0x18,0x18,0x18,0x70,0x00,0x00,0x00,0x00,
        0x00,0x00,0x76,0xDC,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x30,0x18,0x00,0x3C,0x18,0x18,0x18,0x18,0x18,0x18,0x18,0x3C,0x00,0x00,0x00,0x00
    };

    public const int HeapPoolSize = 0x100000; // 1MB BSS 堆池

    public static (byte[] code, Dictionary<string, int> offsets, List<(int off, string content)> rdataRefs, List<(int off, byte[] data)> rdataBinRefs, List<(int off, string name, int bssOff)> bssRefs, int bssSize) Generate()
    {
        var b = new Rt();

        // 分配 BSS 堆池与分配指针
        b.AllocBss("heap_pool", HeapPoolSize);
        b.AllocBss("heap_ptr", 8);
        b.AllocBss("gui_fd", 4);         // GUI 设备 fd（打开后存储）
        b.AllocBss("gui_win_id", 4);     // 当前窗口 ID

        var o = new Dictionary<string, int>();
        void Reg(string n, Action<Rt> g) { if (!o.ContainsKey(n)) { o[n] = b.Pos; b.MarkFunc(n); g(b); } }

        Reg("exit", GenExit);
        Reg("abort", GenAbort);
        Reg("putchar", GenPutchar);
        Reg("puts", GenPuts);
        Reg("printf", GenPrintf);
        Reg("strlen", GenStrlen);
        Reg("memcpy", GenMemcpy);
        Reg("memset", GenMemset);
        Reg("memcmp", GenMemcmp);
        Reg("strcmp", GenStrcmp);
        Reg("strcpy", GenStrcpy);
        Reg("strncpy", GenStrncpy);
        Reg("strcat", GenStrcat);
        Reg("atoi", GenAtoi);
        Reg("atol", GenAtol);
        Reg("malloc", GenMalloc);
        Reg("free", GenFree);
        Reg("calloc", GenCalloc);
        Reg("getchar", GenGetchar);
        Reg("fputs", GenFputs);
        Reg("fgets", GenFgets);
        Reg("fprintf", GenFprintf);
        Reg("sprintf", GenSprintf);
        Reg("scanf", GenScanf);
        Reg("sscanf", GenSscanf);
        Reg("time_utc_raw", GenTimeGetUtcRaw);
        Reg("open", GenOpen);
        Reg("close", GenClose);
        // Los4 GUI 辅助函数（GUI 逻辑由 LibcDecls.cs C 代码实现，仅提供底层 ioctl + 字体指针）
        Reg("paze_ioctl", GenPazeIoctl);
        Reg("paze_font_ptr", GenPazeFontPtr);
        return (b.Finish(), o, b.RdataRefs, b.RdataBinRefs, b.BssRefs, b.BssSize);
    }

    // ============ Los4 GUI 底层辅助 ============

    /// int paze_ioctl(int fd, unsigned long req, void *arg) — ioctl 系统调用封装。
    /// 参数已在 SysV ABI 寄存器中（rdi=fd, rsi=req, rdx=arg），直接发 syscall。
    /// </summary>
    private static void GenPazeIoctl(Rt b)
    {
        b.MovEax(SYS_ioctl); b.Syscall(); b.Ret();
    }

    /// unsigned char *paze_font_ptr(void) — 返回 PSF 字体数据在 .rodata 中的地址。
    /// FontData（2048 字节 = 128 字形 × 16 字节）由 ElfWriterLos4 追加到 rodata。
    /// </summary>
    private static void GenPazeFontPtr(Rt b)
    {
        b.MovImm64RdataBinRef(0, FontData); // rax = &FontData (rodata)
        b.Ret();
    }

    // ============ 时间 ============
    // long long time_utc_raw(void) — 返回 UTC unix 秒数
    // open("0:/dev/leonos_system", O_RDONLY) → fd; ioctl(fd, TIME_INFO, &buf); 读 buf.unix_seconds; close(fd)
    private static void GenTimeGetUtcRaw(Rt b)
    {
        // 栈帧: [rbp-8]=fd, [rbp-80..-17]=leonos_time_info buf (48 字节，留 64)
        b.PushRbp(); b.MovRbpRsp();
        b.SubRsp(80);

        // open(path, O_RDONLY=0, 0): rdi=path, rsi=0, rdx=0
        b.MovImm64RdataRef(7, Los4SystemDevice);  // rdi = "0:/dev/leonos_system"
        b.Xor(6, 6);                               // rsi = 0 (O_RDONLY)
        b.Xor(2, 2);                               // rdx = 0 (mode)
        b.MovEax(SYS_open); b.Syscall();             // rax = fd
        b.StoreRbp(-8, 0);                         // [rbp-8] = fd

        // ioctl(fd, LEONOS_IOCTL_TIME_INFO, &buf): rdi=fd, rsi=request, rdx=&buf
        b.LoadRbp(7, -8);                          // rdi = fd
        b.MovImm64(6, LEONOS_IOCTL_TIME_INFO);     // rsi = request
        b.LeaRbp(2, -80);                          // rdx = &buf
        b.MovEax(SYS_ioctl); b.Syscall();

        // rax = buf.unix_seconds (偏移 0)
        b.LoadRbp(0, -80);

        // close(fd)（保留 rax）
        b.PushR(0);
        b.LoadRbp(7, -8);
        b.MovEax(SYS_close); b.Syscall();
        b.PopR(0);                                 // rax = unix_seconds

        b.Leave(); b.Ret();
    }

    // ============ GUI ============
    // LeonOS 4 GUI：fd=3 硬编码（由 shell 预打开），ioctl 实现。
    // 与 libc.a 行为完全一致：所有 GUI 函数直接使用 fd=3，无需 open/close。
    // 系统调用使用 syscall 指令（0x0F 0x05），寄存器约定同 Linux x86_64。
    // 结构体布局（来自 leonos/gui.h，通过反编译 libc.a 验证）：
    //   leonos_gui_create   { u32 width, height; char *title, *text; u32 flags; }  = 32 字节
    //   leonos_fb_text      { u32 x, y, fg, bg; char *text; }                      = 24 字节
    //   leonos_gui_app_event{ u32 window_id, type; i32 x,y,dx,dy; u32 width,height;
    //                          u8 buttons, keycode, pressed, reserved; }           = 36 字节
    //   leonos_gui_wait_app_event { event[36]; u32 timeout_ms; }                   = 40 字节
    // ioctl arg 约定（反编译 libc.a 确认）：CREATE/FB_TEXT/WINDOW_EVENT/WAIT 传结构体指针；
    //   DESTROY_WINDOW 传 window_id 值（非指针）。

    // int gui_init(void) — 打开 GUI 设备 fd（los2w shell 不预打开 fd=3）。
    private static void GenGuiInit(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        // open("0:/dev/leonos_gui", O_RDWR=2, 0): rdi=path, rsi=2, rdx=0
        b.MovImm64RdataRef(7, Los4GuiDevice);
        b.MovImm64(6, 2);                      // rsi = O_RDWR
        b.Xor(2, 2);                           // rdx = 0 (mode)
        b.MovEax(SYS_open); b.Syscall();        // rax = fd
        // 保存到 BSS gui_fd
        b.MovImm64BssRef(2, "gui_fd");          // rdx = &gui_fd
        b.StoreReg32(2, 0);                     // *gui_fd = fd (int)
        b.Leave(); b.Ret();
    }

    // int gui_window_create(const char *title, const char *text, int width, int height)
    private static void GenGuiWindowCreate(Rt b)
    {
        // 参数：rdi=title(7), rsi=text(6), rdx=width(2), rcx=height(1)
        // 栈布局：[rbp-8]=title, [rbp-16]=text, [rbp-24]=width, [rbp-32]=height, [rbp-64..-33]=gui_create(32 字节)
        int retFail = b.Lbl();
        b.PushRbp(); b.MovRbpRsp(); b.SubRsp(72);
        b.StoreRbp(-8, 7);                       // 保存 title
        b.StoreRbp(-16, 6);                      // 保存 text
        b.StoreRbp(-24, 2);                      // 保存 width
        b.StoreRbp(-32, 1);                      // 保存 height
        // gui_init()（打开 GUI 设备，fd 存入 BSS gui_fd）
        b.CallName("gui_init");
        // 构造 leonos_gui_create @ [rbp-64]，用 rdi 作游标逐字段填充
        b.LeaRbp(7, -64);                        // rdi = &gc
        b.LoadRbp(0, -24); b.StoreReg32(7, 0);   // gc.width  = width  ([rdi+0])
        b.AddImm(7, 4);
        b.LoadRbp(0, -32); b.StoreReg32(7, 0);   // gc.height = height ([rdi+4])
        b.AddImm(7, 4);
        b.LoadRbp(0, -8);  b.StoreReg64(7, 0);   // gc.title  = title  ([rdi+8])
        b.AddImm(7, 8);
        b.LoadRbp(0, -16); b.StoreReg64(7, 0);   // gc.text   = text   ([rdi+16]) — 初始窗口文本
        b.AddImm(7, 8);
        b.Xor(0, 0);       b.StoreReg32(7, 0);   // gc.flags  = 0      ([rdi+24])
        // ioctl(gui_fd, CREATE_WINDOW, &gc)
        b.MovImm64BssRef(7, "gui_fd");             // rdi = &gui_fd
        b.LoadReg32(7, 0);                          // rdi = gui_fd (int)
        b.MovImm64(6, LEONOS_GUI_IOCTL_CREATE_WINDOW);      // rsi = request
        b.LeaRbp(2, -64);                                   // rdx = &gc
        b.MovEax(SYS_ioctl); b.Syscall();                     // rax = window_id
        b.Test(0, 0);
        b.Jle(retFail);                          // <= 0 → -1（与 libc.a 一致：result > 0 才成功）
        b.Xor(0, 0);                             // return 0
        b.Leave(); b.Ret();
        b.Mark(retFail);
        b.MovImm64(0, -1);                       // return -1
        b.Leave(); b.Ret();
    }

    // int gui_window_text(int win, int x, int y, const char *text, unsigned int fg, unsigned int bg)
    private static void GenGuiWindowText(Rt b)
    {
        // 参数：rdi=win(7), rsi=x(6), rdx=y(2), rcx=text(1), r8=fg(8), r9=bg(9)
        // 结构 leonos_fb_text @ [rbp-24]: { u32 x, y, fg, bg; char *text; } = 24 字节
        b.PushRbp(); b.MovRbpRsp(); b.SubRsp(32);
        // 构造 fb_text：[rbp-24]=x, [rbp-20]=y, [rbp-16]=fg, [rbp-12]=bg, [rbp-8]=text
        b.LeaRbp(10, -24);                       // r10 = &fbtext（用 r10 作基址）
        b.StoreReg32(10, 6);                     // [r10+0] = x   (rsi)
        b.AddImm(10, 4); b.StoreReg32(10, 2);    // [r10+4] = y   (rdx)
        b.AddImm(10, 4); b.StoreReg32(10, 8);    // [r10+8] = fg  (r8)
        b.AddImm(10, 4); b.StoreReg32(10, 9);    // [r10+12]= bg  (r9)
        b.AddImm(10, 4); b.StoreReg64(10, 1);    // [r10+16]= text(rcx)
        // ioctl(gui_fd, FB_TEXT, &fbtext)
        b.MovImm64BssRef(7, "gui_fd");             // rdi = &gui_fd
        b.LoadReg32(7, 0);                          // rdi = gui_fd
        b.MovImm64(6, LEONOS_GUI_IOCTL_FB_TEXT);            // rsi = request
        b.LeaRbp(2, -24);                                   // rdx = &fbtext
        b.MovEax(SYS_ioctl); b.Syscall();
        b.Xor(0, 0);                             // return 0
        b.Leave(); b.Ret();
    }

    // int gui_window_present(int win) — Los4 文字模式无需 present，直接返回 0。
    private static void GenGuiWindowPresent(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.Xor(0, 0);
        b.Leave(); b.Ret();
    }

    // 把 leonos_gui_app_event(@baseReg) 翻译为 gui_event(@evReg)。
    // event 布局：[base+0]=window_id, [+4]=type, [+8]=x, [+12]=y, [+32]=buttons, [+33]=keycode
    // gui_event 布局：type(0), key(4), mouse_x(8), mouse_y(12), button(16), window_id(20)
    // 仅使用 caller-saved 寄存器 rax(0)/rcx(1)/rdx(2)，不碰 rbx/r12-r15。
    private static void GenGuiTranslateEvent(Rt b, int evReg, int baseReg)
    {
        // ev->window_id (offset 20) = event.window_id ([base+0])
        b.LoadReg32(0, baseReg);                       // eax = event.window_id
        b.AddImm(evReg, 20); b.StoreReg32(evReg, 0); b.AddImm(evReg, -20);

        // 读 event.type ([base+4]) → edx
        b.Mov(1, baseReg); b.AddImm(1, 4);             // rcx = base+4
        b.LoadReg32(2, 1);                             // edx = event.type

        // 默认 ev->type = 0
        b.Xor(0, 0); b.StoreReg32(evReg, 0);
        int notClose = b.Lbl(), notKey = b.Lbl(), notMouse = b.Lbl(), done = b.Lbl();

        // CLOSE(1) → ev->type = 1
        b.CmpDlImm(LEONOS_GUI_APP_EVENT_CLOSE);        // cmp dl, 1
        b.Jne(notClose);
        b.MovImm64(0, 1); b.StoreReg32(evReg, 0);
        b.Jmp(done);
        b.Mark(notClose);

        // KEY_DOWN(7) → ev->type = 2, ev->key = keycode ([base+33])
        b.CmpDlImm(LEONOS_GUI_APP_EVENT_KEY_DOWN);     // cmp dl, 7
        b.Jne(notKey);
        b.MovImm64(0, 2); b.StoreReg32(evReg, 0);      // ev->type = 2
        b.Mov(1, baseReg); b.AddImm(1, 33);
        b.MovzxByteReg(0, 1);                          // eax = keycode
        b.AddImm(evReg, 4); b.StoreReg32(evReg, 0); b.AddImm(evReg, -4);
        b.Jmp(done);
        b.Mark(notKey);

        // MOUSE_BUTTON(6) → ev->type = 3, mouse_x=x([base+8]), mouse_y=y([base+12]), button=buttons([base+32])
        b.CmpDlImm(LEONOS_GUI_APP_EVENT_MOUSE_BUTTON); // cmp dl, 6
        b.Jne(notMouse);
        b.MovImm64(0, 3); b.StoreReg32(evReg, 0);      // ev->type = 3
        b.Mov(1, baseReg); b.AddImm(1, 8);  b.LoadReg32(0, 1);
        b.AddImm(evReg, 8);  b.StoreReg32(evReg, 0); b.AddImm(evReg, -8);
        b.Mov(1, baseReg); b.AddImm(1, 12); b.LoadReg32(0, 1);
        b.AddImm(evReg, 12); b.StoreReg32(evReg, 0); b.AddImm(evReg, -12);
        b.Mov(1, baseReg); b.AddImm(1, 32); b.MovzxByteReg(0, 1);
        b.AddImm(evReg, 16); b.StoreReg32(evReg, 0); b.AddImm(evReg, -16);
        b.Jmp(done);
        b.Mark(notMouse);
        b.Mark(done);
    }

    // int gui_event_poll(struct gui_event *ev)
    private static void GenGuiEventPoll(Rt b)
    {
        // 参数：rdi=ev(7)
        // 结构 leonos_gui_app_event @ [rbp-48]（36 字节，清零 40 字节对齐）
        int noEvent = b.Lbl();
        b.PushRbp(); b.MovRbpRsp(); b.SubRsp(56);
        b.StoreRbp(-56, 7);                      // 保存 ev 指针到 [rbp-56]
        // 清零 event 结构
        b.Xor(0, 0);
        b.LeaRbp(1, -48);
        for (int i = 0; i < 40; i += 8) { b.StoreReg64(1, 0); b.AddImm(1, 8); }
        // ioctl(gui_fd, WINDOW_EVENT, &event)
        b.MovImm64BssRef(7, "gui_fd");             // rdi = &gui_fd
        b.LoadReg32(7, 0);                          // rdi = gui_fd
        b.MovImm64(6, LEONOS_GUI_IOCTL_WINDOW_EVENT);       // rsi = request
        b.LeaRbp(2, -48);                                   // rdx = &event
        b.MovEax(SYS_ioctl); b.Syscall();          // rax = 0(无事件) / 1(有事件)
        b.Test(0, 0);
        b.Jle(noEvent);                          // rax <= 0 → ev->type=0, return 0
        // 翻译事件
        b.LoadRbp(7, -56);                       // rdi = ev
        b.LeaRbp(10, -48);                       // r10 = &event
        GenGuiTranslateEvent(b, 7, 10);
        b.MovImm64(0, 1);                        // return 1
        b.Leave(); b.Ret();
        b.Mark(noEvent);
        b.LoadRbp(7, -56);                       // rdi = ev
        b.Xor(0, 0); b.StoreReg32(7, 0);         // ev->type = 0
        b.Xor(0, 0);                             // return 0
        b.Leave(); b.Ret();
    }

    // int gui_event_wait(struct gui_event *ev, int timeout_ms)
    private static void GenGuiEventWait(Rt b)
    {
        // 参数：rdi=ev(7), rsi=timeout_ms(6)
        // 结构 leonos_gui_wait_app_event @ [rbp-56]（40 字节）：event[36] + timeout_ms[4]
        // timeout_ms 在结构体 offset 36 = [rbp-20]
        int noEvent = b.Lbl();
        b.PushRbp(); b.MovRbpRsp(); b.SubRsp(72);
        b.StoreRbp(-64, 7);                      // 保存 ev
        b.StoreRbp(-72, 6);                      // 保存 timeout_ms
        // 清零 wait 结构（48 字节，覆盖 40 字节结构体 + 对齐填充）
        b.Xor(0, 0);
        b.LeaRbp(1, -56);
        for (int i = 0; i < 48; i += 8) { b.StoreReg64(1, 0); b.AddImm(1, 8); }
        // timeout_ms @ [rbp-56+36] = [rbp-20]
        b.LoadRbp(0, -72);
        b.LeaRbp(1, -20); b.StoreReg32(1, 0);
        // ioctl(gui_fd, WAIT_WINDOW_EVENT, &wait)
        b.MovImm64BssRef(7, "gui_fd");             // rdi = &gui_fd
        b.LoadReg32(7, 0);                          // rdi = gui_fd
        b.MovImm64(6, LEONOS_GUI_IOCTL_WAIT_WINDOW_EVENT);  // rsi = request
        b.LeaRbp(2, -56);                                   // rdx = &wait
        b.MovEax(SYS_ioctl); b.Syscall();          // rax = result
        b.Test(0, 0);
        b.Jle(noEvent);                          // rax <= 0 → 无事件（含错误），防止死循环
        // 翻译事件（event 在 [rbp-56]，ev 在 [rbp-64]）
        b.LoadRbp(7, -64);                       // rdi = ev
        b.LeaRbp(10, -56);                       // r10 = &event
        GenGuiTranslateEvent(b, 7, 10);
        b.MovImm64(0, 1);                        // return 1
        b.Leave(); b.Ret();
        b.Mark(noEvent);
        b.LoadRbp(7, -64);                       // rdi = ev
        b.Xor(0, 0); b.StoreReg32(7, 0);         // ev->type = 0
        b.Xor(0, 0);                             // return 0
        b.Leave(); b.Ret();
    }

    // int gui_window_destroy(int win)
    private static void GenGuiWindowDestroy(Rt b)
    {
        // 参数：rdi=win(7)
        // 与 libc.a 一致：ioctl(gui_fd, DESTROY_WINDOW, win_value) — window_id 作为值传递，非指针
        b.PushRbp(); b.MovRbpRsp();
        b.Mov(2, 7);                             // rdx = win（值，非指针）
        b.MovImm64BssRef(7, "gui_fd");           // rdi = &gui_fd
        b.LoadReg32(7, 0);                        // rdi = gui_fd
        b.MovImm64(6, LEONOS_GUI_IOCTL_DESTROY_WINDOW);  // rsi = request
        b.MovEax(SYS_ioctl); b.Syscall();
        b.Xor(0, 0);                             // return 0
        b.Leave(); b.Ret();
    }

    // int gui_cleanup(void) — 关闭 GUI 设备 fd。
    private static void GenGuiCleanup(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        // close(gui_fd)
        b.MovImm64BssRef(7, "gui_fd");           // rdi = &gui_fd
        b.LoadReg32(7, 0);                        // rdi = gui_fd
        b.MovEax(SYS_close); b.Syscall();
        b.Xor(0, 0);                             // return 0
        b.Leave(); b.Ret();
    }

    // ============ 函数实现 ============

    // int open(const char *path, int flags, ...) — rdi=path, rsi=flags, rdx=mode
    private static void GenOpen(Rt b)
    {
        b.MovEax(SYS_open); b.Syscall(); b.Ret();
    }

    // int close(int fd) — rdi=fd
    private static void GenClose(Rt b)
    {
        b.MovEax(SYS_close); b.Syscall(); b.Ret();
    }

    private static void GenExit(Rt b) { b.MovEax(SYS_exit); b.Syscall(); b.Ret(); }

    private static void GenAbort(Rt b) { b.MovEdi(1); b.MovEax(SYS_exit); b.Syscall(); b.Ret(); }

    private static void GenPutchar(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp(); b.SubRsp(16);
        // [rsp] = (byte)c (c is in rdi, low byte is dil)
        b.Emit(0x40, 0x88, 0x3C, 0x24); // mov [rsp], dil
        b.MovEdi(1);              // fd=stdout
        b.Emit(0x48, 0x8D, 0x34, 0x24); // lea rsi, [rsp]
        b.MovEdx(1);
        b.MovEax(SYS_write); b.Syscall();
        b.Emit(0x40, 0x0F, 0xB6, 0xC7); // movzx eax, dil
        b.Leave(); b.Ret();
    }

    private static void GenPuts(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7);              // save s (rdi)
        // strlen: rsi=s, rcx=0
        b.Mov(6, 7); b.Xor(1, 1);
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.CmpByteMemZero(6);
        b.Je(done);
        b.Inc(6); b.Inc(1);
        b.Jmp(loop);
        b.Mark(done);
        b.Mov(2, 1);             // rdx=len
        b.PopR(6);               // rsi=s
        b.MovEdi(1);             // stdout
        b.MovEax(SYS_write); b.Syscall();
        // write '\n'
        b.Push8((byte)'\n');
        b.Mov(6, 4);             // rsi=rsp
        b.MovEdx(1); b.MovEdi(1);
        b.MovEax(SYS_write); b.Syscall();
        b.AddRsp(8);
        b.Xor(0, 0);
        b.PopRbp(); b.Ret();
    }

    private static void GenPrintf(Rt b)
    {
        // 栈帧: [rbp-8..-40]=rbx,r12,r13,r14,r15; [rbp-48..-88]=args; [rbp-0x460..-88]=buf
        // 5 个 push = 40 字节，SubRsp(0x438) → rsp=rbp-0x460 (覆盖整个缓冲区)
        // 16 字节对齐: 入口 rsp≡-8(mod16)，5 push 后 rsp≡-8(mod16)，0x438≡8(mod16) → 对齐 ✓
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(3); b.PushR(12); b.PushR(13); b.PushR(14); b.PushR(15); // rsp=rbp-40
        b.SubRsp(0x438);        // rsp=rbp-0x460

        // 存参数到 [rbp-48]..[rbp-88] (逆序: arg0=rdi 在 [rbp-88])
        b.StoreRbp(-48, 9);     // [rbp-48] = r9  (arg5)
        b.StoreRbp(-56, 8);     // [rbp-56] = r8  (arg4)
        b.StoreRbp(-64, 1);     // [rbp-64] = rcx (arg3)
        b.StoreRbp(-72, 2);     // [rbp-72] = rdx (arg2)
        b.StoreRbp(-80, 6);     // [rbp-80] = rsi (arg1)
        b.StoreRbp(-88, 7);     // [rbp-88] = rdi (arg0=fmt)

        // rbx=fmt, r13=buf_start, r12=buf_pos, r14=arg_idx(从1开始)
        b.LoadRbp(3, -88);      // rbx = fmt
        b.LeaRbp(13, -0x460);   // r13 = buf start
        b.Mov(12, 13);          // r12 = buf pos
        b.MovImm64(14, 1);      // r14 = 1 (first vararg)

        int mainLoop = b.Lbl(), endLoop = b.Lbl(), copyChar = b.Lbl(), afterFmt = b.Lbl();
        int notL = b.Lbl();
        int fmtD = b.Lbl(), fmtU = b.Lbl(), fmtX = b.Lbl();
        int fmtC = b.Lbl(), fmtS = b.Lbl(), fmtPct = b.Lbl(), fmtUnknown = b.Lbl();

        b.Mark(mainLoop);
        b.MovzxByte(0, 3);      // al = *rbx
        b.Test(0, 0);
        b.Je(endLoop);
        b.CmpAlImm(0x25);       // '%'
        b.Jne(copyChar);

        b.Inc(3);               // skip '%'
        b.MovzxByte(0, 3);      // al = next char
        // skip 'l' prefix
        b.CmpAlImm(0x6C); b.Jne(notL);
        b.Inc(3); b.MovzxByte(0, 3);
        b.Mark(notL);

        b.CmpAlImm(0x64); b.Je(fmtD);  // 'd'
        b.CmpAlImm(0x69); b.Je(fmtD);  // 'i'
        b.CmpAlImm(0x75); b.Je(fmtU);  // 'u'
        b.CmpAlImm(0x78); b.Je(fmtX);  // 'x'
        b.CmpAlImm(0x63); b.Je(fmtC);  // 'c'
        b.CmpAlImm(0x73); b.Je(fmtS);  // 's'
        b.CmpAlImm(0x25); b.Je(fmtPct);// '%'
        b.Jmp(fmtUnknown);

        // %d: 有符号十进制
        b.Mark(fmtD);
        b.LoadArg(0);           // rax = arg[r14]
        b.Inc(14);
        b.FmtSigned(12);        // 写入 r12
        b.Inc(3); b.Jmp(afterFmt);

        // %u: 无符号十进制
        b.Mark(fmtU);
        b.LoadArg(0); b.Inc(14);
        b.FmtUnsigned(12);
        b.Inc(3); b.Jmp(afterFmt);

        // %x: 十六进制
        b.Mark(fmtX);
        b.LoadArg(0); b.Inc(14);
        b.FmtHex(12);
        b.Inc(3); b.Jmp(afterFmt);

        // %c: 字符
        b.Mark(fmtC);
        b.LoadArg(0); b.Inc(14);
        b.MovByteR12Al(); b.Inc(12);
        b.Inc(3); b.Jmp(afterFmt);

        // %s: 字符串
        b.Mark(fmtS);
        b.LoadArg(0); b.Inc(14); // rax = str ptr
        b.Mov(15, 0);           // r15 = str
        int sL = b.Lbl(), sD = b.Lbl();
        b.Mark(sL);
        b.MovzxByte(0, 15);
        b.Test(0, 0); b.Je(sD);
        b.MovByteR12Al(); b.Inc(12); b.Inc(15);
        b.Jmp(sL);
        b.Mark(sD);
        b.Inc(3); b.Jmp(afterFmt);

        // %%: 百分号
        b.Mark(fmtPct);
        b.MovByteR12Imm(0x25); b.Inc(12); b.Inc(3);
        b.Jmp(afterFmt);

        // 未知: 写 '%' 再写字符
        b.Mark(fmtUnknown);
        b.MovByteR12Imm(0x25); b.Inc(12);
        b.Jmp(copyChar);

        // 普通字符
        b.Mark(copyChar);
        b.MovByteR12Al(); b.Inc(12); b.Inc(3);
        b.Mark(afterFmt);
        b.Jmp(mainLoop);

        // 结束: write(1, buf, len)
        b.Mark(endLoop);
        b.Mov(2, 12); b.Sub(2, 13); // rdx = len
        b.Mov(6, 13);               // rsi = buf
        b.MovEdi(1);
        b.MovEax(SYS_write); b.Syscall();
        b.Mov(0, 12); b.Sub(0, 13); // rax = len
        b.AddRsp(0x438);
        b.PopR(15); b.PopR(14); b.PopR(13); b.PopR(12); b.PopR(3);
        b.Leave(); b.Ret();
    }

    private static void GenStrlen(Rt b)
    {
        b.Mov(0, 7);
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.CmpByteMemZero(0);
        b.Je(done);
        b.Inc(0);
        b.Jmp(loop);
        b.Mark(done);
        b.Sub(0, 7);
        b.Ret();
    }

    private static void GenMemcpy(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7); b.PushR(6); b.PushR(2);
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.Test(2, 2); b.Je(done);
        b.MovzxByte(0, 6);      // al = *s(rsi)
        b.MovByteMemReg(7, 0);  // *d(rdi) = al
        b.Inc(7); b.Inc(6); b.Dec(2);
        b.Jmp(loop);
        b.Mark(done);
        b.PopR(2); b.PopR(6); b.PopR(7);
        b.Mov(0, 7);
        b.PopRbp(); b.Ret();
    }

    private static void GenMemset(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7);
        b.MovAlFromSil();       // al = (byte)c
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.Test(2, 2); b.Je(done);
        b.MovByteMemReg(7, 0);
        b.Inc(7); b.Dec(2);
        b.Jmp(loop);
        b.Mark(done);
        b.PopR(7); b.Mov(0, 7);
        b.PopRbp(); b.Ret();
    }

    private static void GenMemcmp(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        int loop = b.Lbl(), diff = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.Test(2, 2); b.Je(done);
        b.MovzxByte(0, 7); b.MovzxByte(1, 6);
        b.CmpAlCl(); b.Jne(diff);
        b.Inc(7); b.Inc(6); b.Dec(2);
        b.Jmp(loop);
        b.Mark(diff);
        b.SubAlCl(); b.MovsxEaxAl();
        b.PopRbp(); b.Ret();
        b.Mark(done);
        b.Xor(0, 0);
        b.PopRbp(); b.Ret();
    }

    private static void GenStrcmp(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        int loop = b.Lbl(), diff = b.Lbl(), done = b.Lbl(), ret = b.Lbl();
        b.Mark(loop);
        b.MovzxByte(0, 7); b.MovzxByte(1, 6);
        b.CmpAlCl(); b.Jne(diff);
        b.Test(0, 0); b.Je(done);
        b.Inc(7); b.Inc(6); b.Jmp(loop);
        b.Mark(diff);
        b.SubAlCl(); b.MovsxEaxAl(); b.Jmp(ret);
        b.Mark(done);
        b.Xor(0, 0);
        b.Mark(ret);
        b.PopRbp(); b.Ret();
    }

    private static void GenStrcpy(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7);
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.MovzxByte(0, 6); b.MovByteMemReg(7, 0);
        b.Test(0, 0); b.Je(done);
        b.Inc(7); b.Inc(6); b.Jmp(loop);
        b.Mark(done);
        b.PopR(0);
        b.PopRbp(); b.Ret();
    }

    private static void GenStrncpy(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7);
        int loop = b.Lbl(), fill = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.Test(2, 2); b.Je(done);
        b.MovzxByte(0, 6); b.MovByteMemReg(7, 0);
        b.Inc(7); b.Inc(6); b.Dec(2);
        b.Test(0, 0); b.Jne(loop);
        b.Mark(fill);
        b.Test(2, 2); b.Je(done);
        b.MovByteMemImm(7, 0); b.Inc(7); b.Dec(2);
        b.Jmp(fill);
        b.Mark(done);
        b.PopR(0);
        b.PopRbp(); b.Ret();
    }

    private static void GenStrcat(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7);
        int fE = b.Lbl(), fnd = b.Lbl(), loop = b.Lbl(), done = b.Lbl();
        b.Mark(fE);
        b.CmpByteMemZero(7); b.Je(fnd); b.Inc(7); b.Jmp(fE);
        b.Mark(fnd);
        b.Mark(loop);
        b.MovzxByte(0, 6); b.MovByteMemReg(7, 0);
        b.Test(0, 0); b.Je(done);
        b.Inc(7); b.Inc(6); b.Jmp(loop);
        b.Mark(done);
        b.PopR(0);
        b.PopRbp(); b.Ret();
    }

    private static void GenAtoi(Rt b) => GenAtoiBase(b);
    private static void GenAtol(Rt b) => GenAtoiBase(b);

    private static void GenAtoiBase(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(3);             // save rbx
        b.Mov(3, 7);            // rbx = s
        b.Xor(0, 0);            // rax = result
        b.Xor(1, 1);            // rcx = negative flag
        int skipWs = b.Lbl(), afterSign = b.Lbl(), checkPlus = b.Lbl();
        int digitLoop = b.Lbl(), notDigit = b.Lbl(), ret = b.Lbl();

        b.Mark(skipWs);
        b.MovzxByte(2, 3); b.CmpDlImm(0x20); b.Jne(afterSign);
        b.Inc(3); b.Jmp(skipWs);
        b.Mark(afterSign);
        b.CmpDlImm(0x2D); b.Jne(checkPlus);  // '-'
        b.MovImm64(1, 1); b.Inc(3); b.Jmp(digitLoop);
        b.Mark(checkPlus);
        b.CmpDlImm(0x2B); b.Jne(digitLoop);  // '+'
        b.Inc(3);
        b.Mark(digitLoop);
        b.MovzxByte(2, 3);
        b.CmpDlImm(0x30); b.Jb(notDigit);
        b.CmpDlImm(0x39); b.Ja(notDigit);
        b.ImulRaxImm(10);
        b.SubDlImm(0x30); b.MovzxRdxDl();
        b.Add(0, 2);
        b.Inc(3); b.Jmp(digitLoop);
        b.Mark(notDigit);
        b.Test(1, 1); b.Je(ret);
        b.Neg(0);
        b.Mark(ret);
        b.PopR(3); b.PopRbp(); b.Ret();
    }

    private static void GenMalloc(Rt b)
    {
        // void *malloc(size_t size)  — rdi = size
        // BSS bump pool allocation（los4 模拟器无 mmap，用 BSS 静态池）
        // heap_ptr 初始为 0，表示尚未初始化；首次调用时初始化为 heap_pool 基址
        b.PushRbp(); b.MovRbpRsp();
        b.AddImm(7, 15); b.AndImm(7, -16); // 对齐 size 到 16 字节
        // 读取 heap_ptr，若为 0 则初始化为 heap_pool
        b.MovImm64BssRef(0, "heap_ptr");   // rax = &heap_ptr
        b.LoadReg64(0, 0);                  // rax = *heap_ptr (当前偏移)
        b.Test(0, 0);                       // 检查是否为 0
        int skipInit = b.Lbl();
        b.Jne(skipInit);
        // 初始化：heap_ptr = &heap_pool（直接用地址，不能 LoadReg64 因为 BSS 初值为 0）
        b.MovImm64BssRef(0, "heap_pool");  // rax = &heap_pool（地址本身）
        b.MovImm64BssRef(2, "heap_ptr");   // rdx = &heap_ptr
        b.StoreReg64(2, 0);                // *heap_ptr = &heap_pool
        b.Mark(skipInit);
        // 分配：新指针 = heap_ptr + size
        b.MovImm64BssRef(2, "heap_ptr");   // rdx = &heap_ptr
        b.LoadReg64(0, 2);                  // rax = *heap_ptr (当前位置)
        b.Mov(1, 7);                        // rcx = size
        b.Add(0, 1);                        // rax += size (新分配地址)
        b.StoreReg64(2, 0);                 // *heap_ptr = 新位置
        // rax = 原始指针 = 新位置 - size
        b.Mov(2, 7);                        // rdx = size
        b.Sub(0, 2);                        // rax -= size → rax = 原始指针
        b.PopRbp(); b.Ret();
    }

    private static void GenFree(Rt b) { b.Ret(); } // bump pool 无需释放

    private static void GenCalloc(Rt b)
    {
        // void *calloc(size_t nmemb, size_t size) — rdi=nmemb, rsi=size
        b.PushRbp(); b.MovRbpRsp();
        b.Imul(7, 6);                       // rdi = total = nmemb * size
        b.AddImm(7, 15); b.AndImm(7, -16);  // 对齐到 16 字节
        b.PushR(7);                         // 保存 total 供 memset 使用
        // 用 malloc 分配（通过调回 malloc 函数）
        b.CallName("malloc");
        // memset(ptr, 0, total)
        b.PopR(2);                          // rdx = total
        b.Mov(7, 0);                        // rdi = ptr (rax)
        b.Xor(0, 0);                        // al = 0
        b.PushR(0);                         // 保存 ptr
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.Test(2, 2); b.Je(done);
        b.MovByteMemReg(7, 0); b.Inc(7); b.Dec(2);
        b.Jmp(loop);
        b.Mark(done);
        b.PopR(0);                          // rax = ptr
        b.PopRbp(); b.Ret();
    }

    private static void GenGetchar(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp(); b.SubRsp(16);
        b.MovEdi(0);            // stdin
        b.Emit(0x48, 0x8D, 0x34, 0x24); // lea rsi, [rsp]
        b.MovEdx(1);
        b.MovEax(SYS_read); b.Syscall();
        b.Test(0, 0);
        int eof = b.Lbl(), ret = b.Lbl();
        b.Je(eof);
        b.Emit(0x0F, 0xB6, 0x04, 0x24); // movzx eax, byte [rsp]
        b.Jmp(ret);
        b.Mark(eof);
        b.MovImm64(0, -1);
        b.Mark(ret);
        b.Leave(); b.Ret();
    }

    private static void GenFputs(Rt b)
    {
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(7);
        b.Mov(6, 7); b.Xor(1, 1);
        int loop = b.Lbl(), done = b.Lbl();
        b.Mark(loop);
        b.CmpByteMemZero(6); b.Je(done);
        b.Inc(6); b.Inc(1); b.Jmp(loop);
        b.Mark(done);
        b.Mov(2, 1); b.PopR(6);
        b.MovEdi(1); b.MovEax(SYS_write); b.Syscall();
        b.Xor(0, 0);
        b.PopRbp(); b.Ret();
    }

    private static void GenSprintf(Rt b)
    {
        // int sprintf(char *buf, const char *fmt, ...)
        // rdi=buf, rsi=fmt, rdx/rcx/r8/r9=varargs
        // 与 GenPrintf 共享格式化逻辑，但输出到用户 buf 而非本地缓冲区+write()
        b.PushRbp(); b.MovRbpRsp();
        b.PushR(3); b.PushR(12); b.PushR(13); b.PushR(14); b.PushR(15);
        b.SubRsp(0x438);

        // 存参数到 [rbp-48]..[rbp-88]
        b.StoreRbp(-48, 9);     // arg5
        b.StoreRbp(-56, 8);     // arg4
        b.StoreRbp(-64, 1);     // arg3
        b.StoreRbp(-72, 2);     // arg2
        b.StoreRbp(-80, 6);     // arg1 (fmt)
        b.StoreRbp(-88, 7);     // arg0 (buf)

        // rbx=fmt, r13=buf_start(保存), r12=buf_pos, r14=2(第一个可变参数索引)
        b.LoadRbp(3, -80);      // rbx = fmt
        b.LoadRbp(13, -88);     // r13 = buf start
        b.Mov(12, 13);          // r12 = buf pos
        b.MovImm64(14, 2);      // r14 = 2

        int mainLoop = b.Lbl(), endLoop = b.Lbl(), copyChar = b.Lbl(), afterFmt = b.Lbl();
        int notL = b.Lbl();
        int fmtD = b.Lbl(), fmtU = b.Lbl(), fmtX = b.Lbl();
        int fmtC = b.Lbl(), fmtS = b.Lbl(), fmtPct = b.Lbl(), fmtUnknown = b.Lbl();

        b.Mark(mainLoop);
        b.MovzxByte(0, 3);
        b.Test(0, 0);
        b.Je(endLoop);
        b.CmpAlImm(0x25);
        b.Jne(copyChar);
        b.Inc(3);
        b.MovzxByte(0, 3);
        b.CmpAlImm(0x6C); b.Jne(notL);
        b.Inc(3); b.MovzxByte(0, 3);
        b.Mark(notL);
        b.CmpAlImm(0x64); b.Je(fmtD);
        b.CmpAlImm(0x69); b.Je(fmtD);
        b.CmpAlImm(0x75); b.Je(fmtU);
        b.CmpAlImm(0x78); b.Je(fmtX);
        b.CmpAlImm(0x63); b.Je(fmtC);
        b.CmpAlImm(0x73); b.Je(fmtS);
        b.CmpAlImm(0x25); b.Je(fmtPct);
        b.Jmp(fmtUnknown);

        b.Mark(fmtD);
        b.LoadArg(0); b.Inc(14);
        b.FmtSigned(12);
        b.Inc(3); b.Jmp(afterFmt);
        b.Mark(fmtU);
        b.LoadArg(0); b.Inc(14);
        b.FmtUnsigned(12);
        b.Inc(3); b.Jmp(afterFmt);
        b.Mark(fmtX);
        b.LoadArg(0); b.Inc(14);
        b.FmtHex(12);
        b.Inc(3); b.Jmp(afterFmt);
        b.Mark(fmtC);
        b.LoadArg(0); b.Inc(14);
        b.MovByteR12Al(); b.Inc(12);
        b.Inc(3); b.Jmp(afterFmt);
        b.Mark(fmtS);
        b.LoadArg(0); b.Inc(14);
        b.Mov(15, 0);
        int sL = b.Lbl(), sD = b.Lbl();
        b.Mark(sL);
        b.MovzxByte(0, 15);
        b.Test(0, 0); b.Je(sD);
        b.MovByteR12Al(); b.Inc(12); b.Inc(15);
        b.Jmp(sL);
        b.Mark(sD);
        b.Inc(3); b.Jmp(afterFmt);
        b.Mark(fmtPct);
        b.MovByteR12Imm(0x25); b.Inc(12); b.Inc(3);
        b.Jmp(afterFmt);
        b.Mark(fmtUnknown);
        b.MovByteR12Imm(0x25); b.Inc(12);
        b.Jmp(copyChar);
        b.Mark(copyChar);
        b.MovByteR12Al(); b.Inc(12); b.Inc(3);
        b.Mark(afterFmt);
        b.Jmp(mainLoop);

        // 结束: null-terminate, 返回长度
        b.Mark(endLoop);
        b.MovByteR12Imm(0);             // null-terminate
        b.Mov(0, 12); b.Sub(0, 13);     // rax = len
        b.AddRsp(0x438);
        b.PopR(15); b.PopR(14); b.PopR(13); b.PopR(12); b.PopR(3);
        b.Leave(); b.Ret();
    }

    private static void GenFgets(Rt b) { b.Xor(0, 0); b.Ret(); }
    private static void GenFprintf(Rt b) { b.Xor(0, 0); b.Ret(); }
    private static void GenScanf(Rt b) { b.Xor(0, 0); b.Ret(); }
    private static void GenSscanf(Rt b) { b.Xor(0, 0); b.Ret(); }
}

/// <summary>x86-64 机器码构建器：标签 + 基本指令发射。寄存器编号：rax=0,rcx=1,rdx=2,rbx=3,rsp=4,rbp=5,rsi=6,rdi=7,r8=8..r15=15</summary>
internal sealed class Rt
{
    internal readonly List<byte> _code = new();
    private readonly Dictionary<int, int> _labels = new();
    private readonly List<(int off, int label)> _patches = new();
    private int _nextLabel;
    // 跨函数调用：记录每个运行时函数的入口偏移，call rel32 在 Finish 时回填。
    private readonly Dictionary<string, int> _funcOffsets = new(StringComparer.Ordinal);
    private readonly List<(int off, string name)> _callPatches = new();

    internal void MarkFunc(string name) => _funcOffsets[name] = _code.Count;
    /// <summary>call rel32 —— 调用另一运行时函数（如 gui_window_create 调用 gui_init）。</summary>
    internal void CallName(string name) { _code.Add(0xE8); _callPatches.Add((_code.Count, name)); _code.Add(0); _code.Add(0); _code.Add(0); _code.Add(0); }

    /// 运行时代码中对 .rodata 字符串的绝对地址引用：(代码内偏移, 字符串内容)。
    /// 由 ElfWriterLos4 在段布局完成后修补为字符串的真实虚拟地址。
    internal readonly List<(int off, string content)> RdataRefs = new();

    /// 运行时代码中对 .rodata 二进制数据的绝对地址引用：(代码内偏移, 数据)。
    /// 由 ElfWriterLos4 在段布局完成后将数据追加到 rodata 并修补为真实虚拟地址。
    internal readonly List<(int off, byte[] data)> RdataBinRefs = new();

    /// 运行时持久状态（BSS 全局）：(代码内偏移, 全局名, 全局在运行时 BSS 区内偏移)。
    /// 由 ElfWriterLos4 在段布局完成后修补为 BSS 区的真实虚拟地址。
    internal readonly List<(int off, string name, int bssOff)> BssRefs = new();
    private readonly Dictionary<string, int> _bssGlobals = new();
    private int _bssSize;
    internal int BssSize => _bssSize;

    /// <summary>分配（或返回已分配的）BSS 全局，返回其在运行时 BSS 区内的偏移。</summary>
    internal int AllocBss(string name, int size = 8)
    {
        if (_bssGlobals.TryGetValue(name, out int off)) return off;
        off = _bssSize;
        _bssSize += size;
        _bssGlobals[name] = off;
        return off;
    }

    /// <summary>mov r64, &bss_global —— 发射 REX + 操作码 + 8 字节占位 0，并记录引用。</summary>
    internal void MovImm64BssRef(int reg, string name)
    {
        byte rex = 0x48; if (reg >= 8) rex |= 0x01;
        _code.Add(rex);
        _code.Add((byte)(0xB8 + (reg & 7)));
        int bssOff = _bssGlobals.TryGetValue(name, out var o) ? o : 0;
        BssRefs.Add((_code.Count, name, bssOff));
        for (int i = 0; i < 8; i++) _code.Add(0);
    }

    // ---- 通用 [reg] 内存访问（mod=00，处理 rsp SIB / rbp disp8 特例）----
    private void EmitModRM(int reg, int rm, int mod)
    {
        int r = reg & 7, m = rm & 7;
        if (m == 4) { _code.Add((byte)((mod << 6) | (r << 3) | 4)); _code.Add(0x24); }       // [rsp] 需 SIB
        else if (m == 5 && mod == 0) { _code.Add((byte)((1 << 6) | (r << 3) | 5)); _code.Add(0); } // [rbp] → [rbp+0]
        else _code.Add((byte)((mod << 6) | (r << 3) | m));
    }
    /// <summary>mov r32, [r64]（4 字节加载）</summary>
    internal void LoadReg32(int dst, int memReg) { _code.Add(RexRR(dst, memReg, w: false)); _code.Add(0x8B); EmitModRM(dst, memReg, 0); }
    /// <summary>mov [r64], r32（4 字节存储）</summary>
    internal void StoreReg32(int memReg, int src) { _code.Add(RexRR(src, memReg, w: false)); _code.Add(0x89); EmitModRM(src, memReg, 0); }
    /// <summary>mov r64, [r64]（8 字节加载）</summary>
    internal void LoadReg64(int dst, int memReg) { _code.Add(RexRR(dst, memReg, w: true)); _code.Add(0x8B); EmitModRM(dst, memReg, 0); }
    /// <summary>mov [r64], r64（8 字节存储）</summary>
    internal void StoreReg64(int memReg, int src) { _code.Add(RexRR(src, memReg, w: true)); _code.Add(0x89); EmitModRM(src, memReg, 0); }
    /// <summary>mov byte [r64+disp8], r8（仅 al=0/cl=1/dl=2）</summary>
    internal void StoreByteReg(int memReg, int valReg)
    {
        bool needRex = memReg >= 8 || valReg >= 8;
        if (needRex) { byte rex = 0x40; if (valReg >= 8) rex |= 0x44; if (memReg >= 8) rex |= 0x01; _code.Add(rex); }
        _code.Add(0x88);
        EmitModRM(valReg, memReg, 0);
    }
    /// <summary>movzx r32, byte [r64]</summary>
    internal void MovzxByteReg(int dst, int memReg)
    {
        _code.Add(RexRR(dst, memReg, w: false));
        _code.Add(0x0F); _code.Add(0xB6);
        EmitModRM(dst, memReg, 0);
    }

    internal int Pos => _code.Count;

    internal void Emit(params byte[] bytes) => _code.AddRange(bytes);

    // ---- 标签 ----
    internal int Lbl() => _nextLabel++;
    internal void Mark(int label) => _labels[label] = _code.Count;
    private void PatchRel32(int off, int label) => _patches.Add((off, label));
    private void Jcc(byte op2, int label) { _code.Add(0x0F); _code.Add(op2); PatchRel32(_code.Count, label); _code.Add(0); _code.Add(0); _code.Add(0); _code.Add(0); }

    internal void Jmp(int l) { _code.Add(0xE9); PatchRel32(_code.Count, l); _code.Add(0); _code.Add(0); _code.Add(0); _code.Add(0); }
    internal void Je(int l) => Jcc(0x84, l);
    internal void Jne(int l) => Jcc(0x85, l);
    internal void Jb(int l) => Jcc(0x82, l);
    internal void Ja(int l) => Jcc(0x87, l);
    internal void Jge(int l) => Jcc(0x8D, l);
    internal void Jle(int l) => Jcc(0x8E, l);

    // ---- 基础 ----
    internal void Ret() => _code.Add(0xC3);
    internal void Syscall() { _code.Add(0xCD); _code.Add(0x80); }  // int 0x80 (LeonOS 4 使用 int 0x80 而非 syscall)
    internal void Leave() => _code.Add(0xC9);
    internal void PushRbp() => _code.Add(0x55);
    internal void PopRbp() => _code.Add(0x5D);
    internal void MovRbpRsp() => Emit(0x48, 0x89, 0xE5);

    // ---- Push/Pop r64 ----
    internal void PushR(int r) { if (r >= 8) _code.Add(0x41); _code.Add((byte)(0x50 + (r & 7))); }
    internal void PopR(int r) { if (r >= 8) _code.Add(0x41); _code.Add((byte)(0x58 + (r & 7))); }
    internal void Push8(byte v) { _code.Add(0x6A); _code.Add(v); }

    // ---- Sub/Add RSP ----
    internal void SubRsp(int imm) { Emit(0x48, 0x83, 0xEC, (byte)imm); }
    internal void AddRsp(int imm) { Emit(0x48, 0x83, 0xC4, (byte)imm); }

    // ---- REX for reg-reg ----
    private byte RexRR(int src, int dst, bool w = true)
    {
        byte rex = (byte)(w ? 0x48 : 0x40);
        if (src >= 8) rex |= 0x04;
        if (dst >= 8) rex |= 0x01;
        return rex;
    }

    // ---- mov r64, r64 ----
    internal void Mov(int dst, int src) { _code.Add(RexRR(src, dst)); _code.Add(0x89); _code.Add((byte)(0xC0 | ((src & 7) << 3) | (dst & 7))); }

    // ---- mov r32, imm32 (zero-extends to r64) ----
    internal void MovEax(int imm) { _code.Add(0xB8); Emit32(imm); }
    internal void MovEdi(int imm) { _code.Add(0xBF); Emit32(imm); }
    internal void MovEdx(int imm) { _code.Add(0xBA); Emit32(imm); }
    internal void MovImm64(int reg, long imm)
    {
        // mov r64, imm64: REX.W (+ REX.B for r8-r15) + B8+rd + 8 bytes
        byte rex = 0x48; // REX.W
        if (reg >= 8) rex |= 0x01; // REX.B
        _code.Add(rex);
        _code.Add((byte)(0xB8 + (reg & 7)));
        for (int i = 0; i < 8; i++) _code.Add((byte)(imm >> (i * 8)));
    }

    /// mov r64, &str —— 发射 REX + 操作码 + 8 字节占位 0，并记录引用。
    /// ElfWriterLos4 在确定 .rodata 布局后回填字符串的真实虚拟地址。
    internal void MovImm64RdataRef(int reg, string content)
    {
        byte rex = 0x48; // REX.W
        if (reg >= 8) rex |= 0x01; // REX.B
        _code.Add(rex);
        _code.Add((byte)(0xB8 + (reg & 7)));
        RdataRefs.Add((_code.Count, content)); // 记录 8 字节占位起始偏移
        for (int i = 0; i < 8; i++) _code.Add(0);
    }

    /// mov r64, &bin —— 发射 REX + 操作码 + 8 字节占位 0，并记录二进制数据引用。
    /// ElfWriterLos4 在确定 .rodata 布局后将数据追加到 rodata 并回填真实虚拟地址。
    internal void MovImm64RdataBinRef(int reg, byte[] data)
    {
        byte rex = 0x48; // REX.W
        if (reg >= 8) rex |= 0x01; // REX.B
        _code.Add(rex);
        _code.Add((byte)(0xB8 + (reg & 7)));
        RdataBinRefs.Add((_code.Count, data)); // 记录 8 字节占位起始偏移
        for (int i = 0; i < 8; i++) _code.Add(0);
    }

    // ---- xor r64, r64 ----
    internal void Xor(int dst, int src) { _code.Add(RexRR(src, dst)); _code.Add(0x31); _code.Add((byte)(0xC0 | ((src & 7) << 3) | (dst & 7))); }
    internal void XorEdiEdi() => Emit(0x48, 0x31, 0xFF);

    // ---- inc/dec r64 ----
    internal void Inc(int r) { _code.Add(RexRR(r, r)); _code.Add(0xFF); _code.Add((byte)(0xC0 | (r & 7))); }
    internal void Dec(int r) { _code.Add(RexRR(r, r)); _code.Add(0xFF); _code.Add((byte)(0xC8 | (r & 7))); }

    // ---- add r64, r64 ----
    internal void Add(int dst, int src) { _code.Add(RexRR(src, dst)); _code.Add(0x01); _code.Add((byte)(0xC0 | ((src & 7) << 3) | (dst & 7))); }
    internal void AddImm(int r, int imm) { _code.Add(RexRR(r, r)); _code.Add(0x83); _code.Add((byte)(0xC0 | (r & 7))); _code.Add((byte)imm); }

    // ---- sub r64, r64 ----
    internal void Sub(int dst, int src) { _code.Add(RexRR(src, dst)); _code.Add(0x29); _code.Add((byte)(0xC0 | ((src & 7) << 3) | (dst & 7))); }

    // ---- and r64, imm8 ----
    internal void AndImm(int r, int imm) { _code.Add(RexRR(r, r)); _code.Add(0x83); _code.Add((byte)(0xE0 | (r & 7))); _code.Add((byte)imm); }

    // ---- imul r64, r64 ----
    internal void Imul(int dst, int src) { _code.Add(RexRR(src, dst)); _code.Add(0x0F); _code.Add(0xAF); _code.Add((byte)(0xC0 | ((dst & 7) << 3) | (src & 7))); }
    internal void ImulRaxImm(int imm) { Emit(0x48, 0x69, 0xC0); Emit32(imm); }

    // ---- neg r64 ----
    internal void Neg(int r) { _code.Add(RexRR(r, r)); _code.Add(0xF7); _code.Add((byte)(0xD8 | (r & 7))); }

    // ---- test r64, r64 ----
    internal void Test(int a, int b) { _code.Add(RexRR(b, a)); _code.Add(0x85); _code.Add((byte)(0xC0 | ((b & 7) << 3) | (a & 7))); }

    // ---- cmp ----
    internal void CmpAlCl() => Emit(0x38, 0xC8);
    internal void CmpAlImm(int imm) { _code.Add(0x3C); _code.Add((byte)imm); }
    internal void CmpDlImm(int imm) { Emit(0x80, 0xFA, (byte)imm); }
    internal void SubDlImm(int imm) { Emit(0x80, 0xEA, (byte)imm); } // sub dl, imm8

    // ---- cmp byte [r64], 0 ----
    internal void CmpByteMemZero(int r)
    {
        // 80 /7 ib: cmp byte [r], 0
        if (r >= 8) _code.Add(0x41);
        // r=4(rsp) or r=5(rbp) 需要 SIB 或 mod 调整
        if (r == 4) Emit(0x80, 0x3C, 0x24, 0x00);       // [rsp]
        else if (r == 5) Emit(0x80, 0x7D, 0x00, 0x00);   // [rbp+0]
        else { _code.Add(0x80); _code.Add((byte)(0x38 | (r & 7))); _code.Add(0x00); }
    }

    // ---- movzx r32, byte [r64] ----
    internal void MovzxByte(int dst, int src)
    {
        _code.Add(RexRR(dst, src, w: false));
        _code.Add(0x0F); _code.Add(0xB6);
        if (src == 4) { _code.Add((byte)(0x04 | ((dst & 7) << 3))); _code.Add(0x24); } // [rsp] SIB
        else if (src == 5) { _code.Add((byte)(0x45 | ((dst & 7) << 3))); _code.Add(0x00); } // [rbp+0]
        else _code.Add((byte)(0x00 | ((dst & 7) << 3) | (src & 7)));
    }

    // ---- mov byte [r64], r8 (al=0, cl=1, dl=2) ----
    internal void MovByteMemReg(int memReg, int valReg)
    {
        // 88 /r: mov byte [r/m], r8
        bool needRex = memReg >= 8 || valReg >= 8;
        if (needRex) { byte rex = 0x40; if (valReg >= 8) rex |= 0x44; if (memReg >= 8) rex |= 0x01; _code.Add(rex); }
        _code.Add(0x88);
        if (memReg == 4) { _code.Add((byte)(0x04 | ((valReg & 7) << 3))); _code.Add(0x24); }
        else if (memReg == 5) { _code.Add((byte)(0x45 | ((valReg & 7) << 3))); _code.Add(0x00); }
        else _code.Add((byte)((valReg & 7) << 3 | (memReg & 7)));
    }

    // ---- mov byte [r64], imm8 ----
    internal void MovByteMemImm(int memReg, int imm)
    {
        if (memReg >= 8) _code.Add(0x41);
        _code.Add(0xC6);
        if (memReg == 4) { _code.Add(0x04); _code.Add(0x24); }
        else if (memReg == 5) { _code.Add(0x45); _code.Add(0x00); }
        else _code.Add((byte)(memReg & 7));
        _code.Add((byte)imm);
    }

    // ---- al/cl 操作 ----
    internal void SubAlCl() => Emit(0x28, 0xC8);     // sub al, cl
    internal void MovsxEaxAl() => Emit(0x48, 0x0F, 0xBE, 0xC0); // movsx rax, al
    internal void MovzxRdxDl() => Emit(0x0F, 0xB6, 0xD2);      // movzx edx, dl
    internal void MovAlFromSil() => Emit(0x40, 0x88, 0xF0);     // mov al, sil

    // ---- [rbp+disp] 存储/加载 ----
    internal void StoreRbp(int disp, int reg)
    {
        // mov [rbp+disp], r64
        _code.Add(RexRR(reg, 5));
        _code.Add(0x89);
        if (disp >= -128 && disp <= 127) { _code.Add((byte)(0x45 | ((reg & 7) << 3))); _code.Add((byte)disp); }
        else { _code.Add((byte)(0x85 | ((reg & 7) << 3))); Emit32(disp); }
    }

    internal void LoadRbp(int dstReg, int disp)
    {
        // mov r64, [rbp+disp]
        _code.Add(RexRR(dstReg, 5));
        _code.Add(0x8B);
        if (disp >= -128 && disp <= 127) { _code.Add((byte)(0x45 | ((dstReg & 7) << 3))); _code.Add((byte)disp); }
        else { _code.Add((byte)(0x85 | ((dstReg & 7) << 3))); Emit32(disp); }
    }

    internal void LeaRbp(int dstReg, int disp)
    {
        _code.Add(RexRR(dstReg, 5));
        _code.Add(0x8D);
        if (disp >= -128 && disp <= 127) { _code.Add((byte)(0x45 | ((dstReg & 7) << 3))); _code.Add((byte)disp); }
        else { _code.Add((byte)(0x85 | ((dstReg & 7) << 3))); Emit32(disp); }
    }

    // ---- printf 专用: 加载第 r14 个参数到 rax ----
    // arg[N] = [rbp + N*8 - 0x58], N 在 r14
    internal void LoadArg(int dstReg)
    {
        // mov rax, [rbp + r14*8 - 0x58]
        // REX: W=1, R=0(rax), X=1(r14), B=0(rbp)
        // ModRM: mod=01, reg=rax(0), r/m=100(SIB)
        // SIB: scale=3(×8), index=r14(6), base=rbp(5)
        // disp8 = -0x58 = 0xA8
        Emit(0x4A, 0x8B, 0x44, 0xF5, 0xA8);
    }

    // ---- printf 专用: mov byte [r12], al ----
    internal void MovByteR12Al() => Emit(0x42, 0x88, 0x04, 0x24); // REX.B=1(r12), 88, ModRM=04(SIB), SIB=24(base=r12&7, index=none)

    internal void MovByteR12Imm(int imm)
    {
        // mov byte [r12], imm8
        Emit(0x43, 0xC6, 0x04, 0x24, (byte)imm);
    }

    // ---- 数字格式化 (写入 r12 指向的 buffer) ----

    internal void FmtSigned(int bufReg)
    {
        // rax = number, r12 = buffer pos
        // 用 rsi 标记起始, rdx:rcx 做除法
        int positive = Lbl(), done = Lbl();
        Test(0, 0);
        Jns(positive);
        // 负数: 写 '-', neg rax
        MovByteR12Imm(0x2D); // '-'
        Inc(12);
        Neg(0);
        Mark(positive);
        FmtUnsigned(bufReg);
    }

    internal void FmtUnsigned(int bufReg)
    {
        // rax = number, r12 = buffer pos
        // 逐位除 10, 逆序写入, 然后反转
        PushR(6);              // save rsi
        Mov(6, 12);            // rsi = start (for later reversal)
        int loop = Lbl(), done = Lbl();
        Mark(loop);
        Emit(0x48, 0x31, 0xD2); // xor rdx, rdx
        Emit(0x48, 0xC7, 0xC1, 0x0A, 0x00, 0x00, 0x00); // mov rcx, 10
        Emit(0x48, 0xF7, 0xF1); // div rcx → rax=商, rdx=余数
        Emit(0x80, 0xC2, 0x30); // add dl, '0'
        MovByteR12Dl();
        Inc(12);
        Test(0, 0);
        Jne(loop);             // if rax != 0, continue
        Mark(done);
        // 反转 [rsi, r12)
        Mov(1, 12); Dec(1);    // rcx = end-1
        int rev = Lbl(), revDone = Lbl();
        Mark(rev);
        Cmp(6, 1);             // cmp rsi, rcx
        Jge(revDone);
        MovzxByte(0, 6);       // al = [rsi]
        PushR(0);              // save al
        MovzxByte(0, 1);       // al = [rcx]
        MovByteMemReg(6, 0);   // [rsi] = al
        PopR(0);               // al = old [rsi]
        MovByteMemReg(1, 0);   // [rcx] = al
        Inc(6); Dec(1);
        Jmp(rev);
        Mark(revDone);
        PopR(6);               // restore rsi
    }

    internal void FmtHex(int bufReg)
    {
        // rax = number, r12 = buffer pos
        PushR(6);
        Mov(6, 12);            // rsi = start
        int loop = Lbl(), done = Lbl();
        Mark(loop);
        Emit(0x48, 0x31, 0xD2); // xor rdx, rdx
        Emit(0x48, 0xC7, 0xC1, 0x10, 0x00, 0x00, 0x00); // mov rcx, 16
        Emit(0x48, 0xF7, 0xF1); // div rcx
        // dl = remainder (0-15)
        Emit(0x80, 0xC2, 0x30); // add dl, '0'
        Emit(0x80, 0xFA, 0x3A); // cmp dl, ':'
        int lt10 = Lbl();
        Jb(lt10);               // if dl < ':' (i.e., dl <= '9'), skip
        Emit(0x80, 0xC2, 0x27); // add dl, 0x27 ('a' - '0' - 10 = 39)
        Mark(lt10);
        MovByteR12Dl();
        Inc(12);
        Test(0, 0);
        Jne(loop);
        Mark(done);
        // 反转
        Mov(1, 12); Dec(1);
        int rev = Lbl(), revDone = Lbl();
        Mark(rev);
        Cmp(6, 1); Jge(revDone);
        MovzxByte(0, 6); PushR(0);
        MovzxByte(0, 1); MovByteMemReg(6, 0);
        PopR(0); MovByteMemReg(1, 0);
        Inc(6); Dec(1); Jmp(rev);
        Mark(revDone);
        PopR(6);
    }

    // ---- mov byte [r12], dl ----
    internal void MovByteR12Dl()
    {
        // REX: B=1 (r12), 88 /r with SIB
        // dl = reg 2, r12 = base (low 3 = 4)
        Emit(0x42, 0x88, 0x14, 0x24); // mov [r12], dl
    }

    // ---- cmp r64, r64 ----
    internal void Cmp(int a, int b) { _code.Add(RexRR(b, a)); _code.Add(0x39); _code.Add((byte)(0xC0 | ((b & 7) << 3) | (a & 7))); }

    // ---- Jns (jump if not sign) ----
    internal void Jns(int l) => Jcc(0x89, l);
    // ---- Js (jump if sign / negative) ----
    internal void Js(int l) => Jcc(0x88, l);

    private void Emit32(int v) { _code.Add((byte)v); _code.Add((byte)(v >> 8)); _code.Add((byte)(v >> 16)); _code.Add((byte)(v >> 24)); }

    internal byte[] Finish()
    {
        foreach (var (off, label) in _patches)
        {
            if (!_labels.TryGetValue(label, out int target))
                target = 0; // unresolved → 0 (should not happen)
            int rel = target - (off + 4);
            _code[off] = (byte)rel;
            _code[off + 1] = (byte)(rel >> 8);
            _code[off + 2] = (byte)(rel >> 16);
            _code[off + 3] = (byte)(rel >> 24);
        }
        // 解析跨函数 call rel32（如 gui_window_create → gui_init）
        foreach (var (off, name) in _callPatches)
        {
            int target = _funcOffsets.TryGetValue(name, out int t) ? t : 0;
            int rel = target - (off + 4);
            _code[off] = (byte)rel;
            _code[off + 1] = (byte)(rel >> 8);
            _code[off + 2] = (byte)(rel >> 16);
            _code[off + 3] = (byte)(rel >> 24);
        }
        return _code.ToArray();
    }
}
