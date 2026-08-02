namespace PazeE.Compiler.CodeGen.X64;

/// <summary>x64 寄存器编码（0..15）与尺寸。</summary>
public readonly struct Reg
{
    public readonly byte Index;   // 0..15
    public readonly byte Size;    // 8,16,32,64
    public Reg(byte index, byte size) { Index = index; Size = size; }
    public bool IsExtended => Index >= 8;
    public override string ToString() => Name(Index, Size);
    public static string Name(int i, int size) => (size switch { 8 => r8[i], 16 => r16[i], 32 => r32[i], _ => r64[i] });
    private static readonly string[] r64 = { "rax","rcx","rdx","rbx","rsp","rbp","rsi","rdi","r8","r9","r10","r11","r12","r13","r14","r15" };
    private static readonly string[] r32 = { "eax","ecx","edx","ebx","esp","ebp","esi","edi","r8d","r9d","r10d","r11d","r12d","r13d","r14d","r15d" };
    private static readonly string[] r16 = { "ax","cx","dx","bx","sp","bp","si","di","r8w","r9w","r10w","r11w","r12w","r13w","r14w","r15w" };
    private static readonly string[] r8 = { "al","cl","dl","bl","spl","bpl","sil","dil","r8b","r9b","r10b","r11b","r12b","r13b","r14b","r15b" };
}

public static class R
{
    public static Reg Rax = new(0, 64); public static Reg Rcx = new(1, 64); public static Reg Rdx = new(2, 64);
    public static Reg Rbx = new(3, 64); public static Reg Rsp = new(4, 64); public static Reg Rbp = new(5, 64);
    public static Reg Rsi = new(6, 64); public static Reg Rdi = new(7, 64);
    public static Reg R8 = new(8, 64); public static Reg R9 = new(9, 64); public static Reg R10 = new(10, 64);
    public static Reg R11 = new(11, 64); public static Reg R12 = new(12, 64); public static Reg R13 = new(13, 64);
    public static Reg R14 = new(14, 64); public static Reg R15 = new(15, 64);

    public static Reg Eax = new(0, 32); public static Reg Ecx = new(1, 32); public static Reg Edx = new(2, 32);
    public static Reg Ebx = new(3, 32); public static Reg Esi = new(6, 32); public static Reg Edi = new(7, 32);
    public static Reg Ebp = new(5, 32);

    public static Reg Al = new(0, 8); public static Reg Cl = new(1, 8); public static Reg Dl = new(2, 8);

    public static Reg Of64(byte i) => new(i, 64);
    public static Reg Of32(byte i) => new(i, 32);
    public static Reg Of8(byte i) => new(i, 8);
}

/// <summary>内存操作数。</summary>
public readonly struct Mem
{
    public readonly Reg? Base;     // null = 无基址
    public readonly Reg? Index;    // null = 无变址
    public readonly byte Scale;    // 1,2,4,8
    public readonly long Disp;
    public readonly bool RipRelative;
    public readonly string? Symbol;  // RIP 相对符号引用（生成重定位）
    public readonly int SymAddend;
    public Mem(Reg? b, Reg? i, byte scale, long disp, bool rip = false, string? sym = null, int addend = 0)
    { Base = b; Index = i; Scale = scale; Disp = disp; RipRelative = rip; Symbol = sym; SymAddend = addend; }
    public static Mem Rip(string sym, int addend = 0) => new(null, null, 1, 0, true, sym, addend);
    public static Mem BaseDisp(Reg b, long d) => new(b, null, 1, d);
    public static Mem BaseIndex(Reg b, Reg i, byte s) => new(b, i, s, 0);
    public static Mem BaseIndexDisp(Reg b, Reg i, byte s, long d) => new(b, i, s, d);
}
