namespace PazeE.Compiler.CodeGen.Arm64;

/// <summary>AArch64 寄存器编码（0..31）与宽度（32=Wn / 64=Xn）。
/// 31 在 64 位下兼作 SP/ZR：作为基址/栈指针时为 SP，其余为 ZR。
/// 本后端独立于 X64，不复用 X64Reg。</summary>
public readonly struct Arm64Reg
{
    public readonly byte Index;   // 0..31
    public readonly byte Size;    // 32 或 64
    public Arm64Reg(byte index, byte size) { Index = index; Size = size; }
    public bool Is64 => Size == 64;
    public override string ToString() => (Size == 64 ? "X" : "W") + (Index == 31 ? (Size == 64 ? "ZR" : "WZR") : Index.ToString());
}

/// <summary>常用寄存器常量。</summary>
public static class AR
{
    public static Arm64Reg X0 = new(0, 64), X1 = new(1, 64), X2 = new(2, 64), X3 = new(3, 64);
    public static Arm64Reg X4 = new(4, 64), X5 = new(5, 64), X6 = new(6, 64), X7 = new(7, 64);
    public static Arm64Reg X8 = new(8, 64), X9 = new(9, 64), X10 = new(10, 64), X11 = new(11, 64);
    public static Arm64Reg X12 = new(12, 64), X13 = new(13, 64), X14 = new(14, 64), X15 = new(15, 64);
    public static Arm64Reg X16 = new(16, 64), X17 = new(17, 64), X18 = new(18, 64), X19 = new(19, 64);
    public static Arm64Reg X29 = new(29, 64), X30 = new(30, 64);
    public static Arm64Reg XZR = new(31, 64);   // 零寄存器（64 位）
    public static Arm64Reg SP = new(31, 64);     // 栈指针（仅作基址/栈操作）

    public static Arm64Reg Of64(byte i) => new(i, 64);
    public static Arm64Reg Of32(byte i) => new(i, 32);
}

/// <summary>AArch64 内存操作数：base+disp（imm9/imm12）或 base+index(LSL #scale)。
/// 符号地址由 codegen 通过 ADRP+ADD 物化到寄存器后，以 BaseDisp(reg,0) 引用，
/// 故本结构不含符号字段（与 X64 Mem.RipRelative 不同）。</summary>
public readonly struct Arm64Mem
{
    public readonly Arm64Reg? Base;   // null = 无基址（本后端总需基址）
    public readonly Arm64Reg? Index;  // null = 无变址
    public readonly long Disp;        // base+disp 的位移
    public readonly byte IndexScale;  // 变址左移：0/1/2/3（1/2/4/8 字节）
    public Arm64Mem(Arm64Reg? b, Arm64Reg? i, long disp, byte scale = 0)
    { Base = b; Index = i; Disp = disp; IndexScale = scale; }
    public static Arm64Mem BaseDisp(Arm64Reg b, long d) => new(b, null, d);
    public static Arm64Mem BaseIndex(Arm64Reg b, Arm64Reg i, byte scale) => new(b, i, 0, scale);
    public static Arm64Mem BaseIndexDisp(Arm64Reg b, Arm64Reg i, byte scale, long d) => new(b, i, d, scale);
}
