using PazeE.Compiler.Binary;

namespace PazeE.Compiler.Runtime;

/// <summary>各平台程序入口桩（_start）：调用 main 后退出。
/// 返回桩字节与桩内重定位描述（相对桩起始的偏移），由写入器追加到 .text 并翻译为 Fixup。</summary>
public static class StartupStubs
{
    public readonly record struct StubFixup(int Offset, FixupKind Kind, string Symbol);

    /// <summary>Windows：and rsp,-16；sub rsp,32（shadow space）；call main；mov ecx,eax；call ExitProcess。</summary>
    public static (byte[] code, List<StubFixup> fixups) Windows(bool hasArgcArgv)
    {
        var b = new List<byte>();
        var fx = new List<StubFixup>();
        b.AddRange(new byte[] { 0x48, 0x83, 0xE4, 0xF0 }); // and rsp, -16（强制 16 字节对齐）
        b.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x20 }); // sub rsp, 32（Win64 shadow space）
        if (hasArgcArgv)
        {
            b.AddRange(new byte[] { 0x31, 0xC9 }); // xor ecx,ecx (argc=0)
            b.AddRange(new byte[] { 0x31, 0xD2 }); // xor edx,edx (argv=NULL)
        }
        b.Add(0xE8); fx.Add(new StubFixup(b.Count, FixupKind.Rel32, "main")); b.AddRange(new byte[4]); // call main
        b.AddRange(new byte[] { 0x89, 0xC1 });     // mov ecx, eax
        b.Add(0xE8); fx.Add(new StubFixup(b.Count, FixupKind.ExtSlot32, "ExitProcess")); b.AddRange(new byte[4]); // call ExitProcess
        return (b.ToArray(), fx);
    }

    /// <summary>Linux ELF _start：call main；mov edi,eax；mov eax,60；syscall。</summary>
    public static (byte[] code, List<StubFixup> fixups) Linux(bool hasArgcArgv)
    {
        var b = new List<byte>();
        var fx = new List<StubFixup>();
        if (hasArgcArgv)
        {
            // argc 在 [rsp]，argv 在 [rsp+8]
            b.AddRange(new byte[] { 0x48, 0x8B, 0x3C, 0x24 });       // mov rdi, [rsp]
            b.AddRange(new byte[] { 0x48, 0x8D, 0x74, 0x24, 0x08 }); // lea rsi, [rsp+8]
        }
        b.Add(0xE8); fx.Add(new StubFixup(b.Count, FixupKind.Rel32, "main")); b.AddRange(new byte[4]); // call main
        b.AddRange(new byte[] { 0x89, 0xC7 });     // mov edi, eax
        b.AddRange(new byte[] { 0xB8, 0x3C, 0x00, 0x00, 0x00 }); // mov eax, 60 (exit)
        b.AddRange(new byte[] { 0x0F, 0x05 });     // syscall
        return (b.ToArray(), fx);
    }

    /// <summary>LeonOS 4 _start：call main；mov edi,eax；mov eax,60；int 0x80。
    /// LeonOS 4 使用 int 0x80（非 syscall 指令）进行系统调用，寄存器约定同 Linux x86_64。</summary>
    public static (byte[] code, List<StubFixup> fixups) Los4(bool hasArgcArgv)
    {
        var b = new List<byte>();
        var fx = new List<StubFixup>();
        b.AddRange(new byte[] { 0x48, 0x31, 0xED }); // xor rbp, rbp（ABI 要求）
        if (hasArgcArgv)
        {
            b.AddRange(new byte[] { 0x48, 0x8B, 0x3C, 0x24 });       // mov rdi, [rsp]    (argc)
            b.AddRange(new byte[] { 0x48, 0x8D, 0x74, 0x24, 0x08 }); // lea rsi, [rsp+8]  (argv)
        }
        b.Add(0xE8); fx.Add(new StubFixup(b.Count, FixupKind.Rel32, "main")); b.AddRange(new byte[4]); // call main
        b.AddRange(new byte[] { 0x89, 0xC7 });     // mov edi, eax
        b.AddRange(new byte[] { 0xB8, 0x3C, 0x00, 0x00, 0x00 }); // mov eax, 60 (SYS_exit)
        b.AddRange(new byte[] { 0xCD, 0x80 });     // int 0x80
        return (b.ToArray(), fx);
    }

    // ============ ARM64 (AArch64) 入口桩（独立逻辑，与 x86 桩不共用）============
    // AAPCS64：入口 SP 已 16 字节对齐，无 shadow space；X0..X7 传参，X0 返回。
    // A64 指令编码常量超过 int.MaxValue，故用 uint。
    private static void EmitW(List<byte> b, uint w)
    {
        b.Add((byte)w); b.Add((byte)(w >> 8)); b.Add((byte)(w >> 16)); b.Add((byte)(w >> 24));
    }

    /// <summary>Windows ARM64：(argc=0/argv=NULL)；bl main；bl ExitProcess。
    /// ExitProcess 在 kernel32.dll，由 PeWriterArm64 经 ExtCall26 解析到导入 thunk。</summary>
    public static (byte[] code, List<StubFixup> fixups) Arm64Windows(bool hasArgcArgv)
    {
        var b = new List<byte>();
        var fx = new List<StubFixup>();
        if (hasArgcArgv)
        {
            EmitW(b, 0xD2800000); // mov x0, #0  (argc=0)
            EmitW(b, 0xD2800001); // mov x1, #0  (argv=NULL)
        }
        int blMain = b.Count;
        EmitW(b, 0x94000000);     // bl main  (imm26 待 patch)
        fx.Add(new StubFixup(blMain, FixupKind.Call26, "main"));
        int blExit = b.Count;
        EmitW(b, 0x94000000);     // bl ExitProcess  (x0 已为 main 返回值)
        fx.Add(new StubFixup(blExit, FixupKind.ExtCall26, "ExitProcess"));
        return (b.ToArray(), fx);
    }

    /// <summary>Linux ARM64 _start：(argc/argv)；bl main；mov x8,#93；svc #0。
    /// aarch64 exit 系统调用号=93。仅 main 需要 Call26 fixup（exit 为 syscall）。</summary>
    public static (byte[] code, List<StubFixup> fixups) Arm64Linux(bool hasArgcArgv)
    {
        var b = new List<byte>();
        var fx = new List<StubFixup>();
        if (hasArgcArgv)
        {
            EmitW(b, 0xF94003E0); // ldr x0, [sp]      ; argc
            EmitW(b, 0x910083E1); // add x1, sp, #8    ; argv
        }
        int blMain = b.Count;
        EmitW(b, 0x94000000);     // bl main  (imm26 待 patch)
        fx.Add(new StubFixup(blMain, FixupKind.Call26, "main"));
        EmitW(b, 0xAA1F03F3);     // mov x19, x0   ; 保存返回值
        EmitW(b, 0xAA1303E0);     // mov x0, x19   ; exit code
        EmitW(b, 0xD2800BA8);     // mov x8, #93   ; aarch64 exit syscall
        EmitW(b, 0xD4000001);     // svc #0
        return (b.ToArray(), fx);
    }
}
