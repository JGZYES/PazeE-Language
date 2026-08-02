using PazeE.Compiler.Parser;

namespace PazeE.Compiler.CodeGen.X64;

/// <summary>x64 调用约定：Win64 与 System V AMD64。</summary>
public sealed class AbiInfo
{
    public Abi Abi { get; }
    public int IntReturnRegCount => 1;
    /// <summary>整型参数寄存器顺序。</summary>
    public byte[] IntArgRegs { get; }
    /// <summary>调用者保存寄存器（函数内可自由使用，无需保存）。</summary>
    public byte[] CallerSaved { get; }
    /// <summary>被调用者保存寄存器（使用前需保存、返回前恢复）。</summary>
    public byte[] CalleeSaved { get; }
    /// <summary>Win64 需要 32 字节 shadow space。</summary>
    public int ShadowSpace { get; }
    /// <summary>栈在 call 指令时需 16 字节对齐。</summary>
    public bool StackAligned16 => true;

    private AbiInfo(Abi abi, byte[] args, byte[] caller, byte[] callee, int shadow)
    { Abi = abi; IntArgRegs = args; CallerSaved = caller; CalleeSaved = callee; ShadowSpace = shadow; }

    public static AbiInfo Win64 => new(Abi.Win64,
        new byte[] { 1, 2, 8, 9 },                         // rcx, rdx, r8, r9
        new byte[] { 0, 1, 2, 8, 9, 10, 11 },              // rax, rcx, rdx, r8-r11
        new byte[] { 3, 5, 6, 7, 12, 13, 14, 15 },         // rbx, rbp, rsi, rdi, r12-r15
        32);

    public static AbiInfo SysV => new(Abi.SysV,
        new byte[] { 7, 6, 2, 1, 8, 9 },                   // rdi, rsi, rdx, rcx, r8, r9
        new byte[] { 0, 1, 2, 8, 9, 10, 11 },              // rax, rcx, rdx, r8-r11
        new byte[] { 3, 5, 12, 13, 14, 15 },               // rbx, rbp, r12-r15
        0);

    public static AbiInfo For(Platform p) => p == Platform.Windows ? Win64 : SysV;
}
