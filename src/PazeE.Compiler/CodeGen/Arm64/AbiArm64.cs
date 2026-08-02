namespace PazeE.Compiler.CodeGen.Arm64;

/// <summary>AArch64 调用约定（AAPCS64 / Linux）。
/// 独立于 X64 的 AbiInfo，不共享逻辑。
/// - 整型参数寄存器 X0..X7（8 个），返回值 X0
/// - 调用者保存：X0..X7, X9..X15, X16/X17(IP0/IP1, 留给 PLT，codegen 不主动用), X18(平台寄存器, 不用)
/// - 被调用者保存：X19..X28, X29(FP), X30(LR)
/// - 无 shadow space；SP 在所有公共边界须 16 字节对齐
/// 结构体按值沿用 PazeE 自定义约定（传副本指针，被调用者 prologue 复制），与 X64 一致。</summary>
public sealed class AbiArm64
{
    public byte[] IntArgRegs => new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };             // X0..X7
    public byte ReturnReg => 0;                                                       // X0
    public byte[] CallerSaved => new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    public byte[] CalleeSaved => new byte[] { 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };
    public int ShadowSpace => 0;
    public bool StackAligned16 => true;
}
