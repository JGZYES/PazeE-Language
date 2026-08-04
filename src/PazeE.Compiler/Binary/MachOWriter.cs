using System.Text;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;

namespace PazeE.Compiler.Binary;

/// <summary>macOS Mach-O 64 (x86-64) 可执行文件写入器（MH_EXECUTE，固定基址 0x100000000）。
/// 用 LC_MAIN 指定 main 入口（dyld 提供 _start，返回后调用 exit），无需自带入口桩。
/// 外部 libc 符号通过 __stubs（jmp [rip+__got]）+ __got 非懒绑定（dyld 加载时按 BIND opcodes 绑定），
/// 加载 /usr/lib/libSystem.dylib 与 /usr/lib/dyld。Abs64 直接填固定虚拟地址（非 PIE）。</summary>
public sealed class MachOWriter : IExecutableWriter
{
    public Platform Platform => Platform.MacOS;

    private const long TextVmaddr = 0x100000000L;
    private const int Page = 0x1000;
    private const string Dylib = "/usr/lib/libSystem.dylib";
    private const string Dyld = "/usr/lib/dyld";

    // Mach-O 常量
    private const uint MH_MAGIC_64 = 0xFEEDFACF;
    private const uint CPU_TYPE_X86_64 = 0x01000007;
    private const int CPU_SUBTYPE_ALL = 3;
    private const int MH_EXECUTE = 2;
    private const uint LC_SEGMENT_64 = 0x19;
    private const uint LC_SYMTAB = 0x02;
    private const uint LC_LOAD_DYLIB = 0x0C;
    private const uint LC_LOAD_DYLINKER = 0x0E;
    private const uint LC_DYSYMTAB = 0x0B;
    private const uint LC_MAIN = 0x80000028;
    private const uint LC_CODE_SIGNATURE = 0x1D;
    private const uint LC_BUILD_VERSION = 0x32;
    private const uint LC_UUID = 0x1B;
    private const int VM_PROT_READ = 1, VM_PROT_WRITE = 2, VM_PROT_EXECUTE = 4;
    private const int S_NON_LAZY_SYMBOL_POINTERS = 0x06;
    // nlist n_type
    private const byte N_EXT = 0x01, N_SECT = 0x0e;
    // BIND opcodes
    private const byte BIND_OPCODE_DONE = 0x00;
    private const byte BIND_OPCODE_SET_SYMBOL_NAME = 0x01;
    private const byte BIND_OPCODE_SET_TYPE_IMM = 0x02;
    private const byte BIND_OPCODE_SET_SEGMENT_AND_OFFSET_ULEB128 = 0x03;
    private const byte BIND_OPCODE_DO_BIND = 0x0A;
    private const byte BIND_OPCODE_SET_DYLIB_ORDINAL_IMM = 0x0C;
    private const int BIND_TYPE_POINTER = 1;

    public byte[] Write(ObjectImage img)
    {
        var text = new List<byte>(img.Text.Data);
        var cstring = new List<byte>(img.RData.Data);
        var data = new List<byte>(img.Data.Data);
        int bssSize = img.Bss.BssSize;
        var externals = new List<string>(img.Externals);
        // macOS GUI 蹦床：paze_mac_send_rect 按值传递 NSRect(32B) 给 objc_msgSend。
        // x86-64 SysV ABI 要求 NSRect 在栈上按值传递，但 PazeE 按指针传结构体。
        // 蹦床由写入器生成（非外部符号），从 externals 移除以避免创建 stub。
        bool needTrampoline = externals.Remove("paze_mac_send_rect");
        int nExt = externals.Count;

        // main 偏移（LC_MAIN 入口）
        long mainOff = img.Symbols.TryGetValue(img.EntrySymbol, out var mainSym) ? mainSym.Offset : 0;

        // ---- 1. __stubs 占位（每个外部符号 6 字节：FF 25 disp32）----
        int stubsOff = text.Count;
        int stubSize = 6;
        text.AddRange(new byte[stubSize * nExt]);

        // ---- 1b. paze_mac_send_rect 蹦床（x86-64 NSRect 按值传递适配）----
        // 入参(SysV): rdi=recv, rsi=sel, rdx=rect_ptr, rcx=a4, r8=a5, r9=a6
        // 调用: objc_msgSend(rdi, rsi, NSRect@stack, rdx=a4, rcx=a5, r8=a6)
        // 蹦床从 [rdx] 复制 32B NSRect 到栈顶，重排寄存器后调用 objc_msgSend。
        int trampolineOff = -1;
        if (needTrampoline)
        {
            trampolineOff = text.Count;
            // push rbp; mov rbp,rsp; sub rsp,48 (32 NSRect + 16 对齐填充)
            text.AddRange(new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x30 });
            // mov rax,[rdx];  mov [rsp],rax
            text.AddRange(new byte[] { 0x48, 0x8B, 0x02, 0x48, 0x89, 0x04, 0x24 });
            // mov rax,[rdx+8];  mov [rsp+8],rax
            text.AddRange(new byte[] { 0x48, 0x8B, 0x42, 0x08, 0x48, 0x89, 0x44, 0x24, 0x08 });
            // mov rax,[rdx+16]; mov [rsp+16],rax
            text.AddRange(new byte[] { 0x48, 0x8B, 0x42, 0x10, 0x48, 0x89, 0x44, 0x24, 0x10 });
            // mov rax,[rdx+24]; mov [rsp+24],rax
            text.AddRange(new byte[] { 0x48, 0x8B, 0x42, 0x18, 0x48, 0x89, 0x44, 0x24, 0x18 });
            // mov rdx,rcx; mov rcx,r8; mov r8,r9  (重排：a4→rdx, a5→rcx, a6→r8)
            text.AddRange(new byte[] { 0x48, 0x89, 0xCA, 0x4C, 0x89, 0xC1, 0x4D, 0x89, 0xC8 });
            // xor eax,eax  (AL=0, 无 XMM 参)
            text.AddRange(new byte[] { 0x31, 0xC0 });
            // call objc_msgSend (E8 + rel32 占位)
            text.Add(0xE8);
            int callRel32Off = text.Count;
            text.AddRange(new byte[4]);
            // leave; ret
            text.AddRange(new byte[] { 0xC9, 0xC3 });
            // 注册 fixup：蹦床内的 call objc_msgSend → ExtSlot32（解析到 stub）
            img.AddFixup(img.Text, callRel32Off, FixupKind.ExtSlot32, "objc_msgSend");
            // 定义符号：paze_mac_send_rect → 蹦床在 __text 中的偏移
            img.DefineSymbol("paze_mac_send_rect", img.Text, trampolineOff, true, true);
        }

        // ---- 2. __got 占位（nExt 个 8 字节非懒绑定槽）----
        var got = new List<byte>(new byte[8 * nExt]);

        // ---- 3. 字符串表 ----
        var strtab = new List<byte>();
        strtab.Add(0); // [0] 空名
        int mainStrx = strtab.Count;
        strtab.AddRange(Encoding.ASCII.GetBytes("main"));
        strtab.Add(0);
        var nameStrx = new int[nExt];
        for (int i = 0; i < nExt; i++)
        {
            nameStrx[i] = strtab.Count;
            strtab.AddRange(Encoding.ASCII.GetBytes(externals[i]));
            strtab.Add(0);
        }

        // ---- 4. 符号表（nlist_64，16 字节/项）----
        // [0]=null, [1]=main(本地), [2..]=外部 UNDEF
        var symtab = new List<byte>();
        symtab.AddRange(new byte[16]); // [0] null
        WriteNlist(symtab, mainStrx, N_SECT, 1, 0, 0); // main 本地定义（n_value 稍后填）
        int mainSymIdx = 1;
        int undefStart = 2;
        for (int i = 0; i < nExt; i++)
            WriteNlist(symtab, nameStrx[i], N_EXT, 0, 0, 0); // 外部 UNDEF

        // ---- 5. indirect symbol table（__got 的 nExt 项，指向 UNDEF 符号索引）----
        var indirect = new List<byte>();
        for (int i = 0; i < nExt; i++)
            Write32At(indirect, undefStart + i);

        // ---- 6. BIND opcodes（非懒绑定 __got，segment __DATA index=1）----
        var bind = new List<byte>();
        bind.Add((byte)(BIND_OPCODE_SET_DYLIB_ORDINAL_IMM | 1)); // ordinal=1 (libSystem)
        bind.Add((byte)(BIND_OPCODE_SET_TYPE_IMM | BIND_TYPE_POINTER));
        for (int i = 0; i < nExt; i++)
        {
            bind.Add(BIND_OPCODE_SET_SYMBOL_NAME);
            bind.AddRange(Encoding.ASCII.GetBytes(externals[i]));
            bind.Add(0);
            bind.Add((byte)(BIND_OPCODE_SET_SEGMENT_AND_OFFSET_ULEB128 | 1)); // seg=1 (__DATA)
            WriteUleb128(bind, (ulong)(i * 8)); // __got 内偏移
            bind.Add(BIND_OPCODE_DO_BIND); // 绑定并前进 8
        }
        bind.Add(BIND_OPCODE_DONE);
        // 绑定信息需至少 1 字节且对齐？保持原样，LINKEDIT 内偏移由 fileoff 决定。

        // ============ 计算各 load command 大小 ============
        // LC_SEGMENT_64: 72 + nsects*80
        int segTextCmd = 72 + 3 * 80;   // __text, __stubs, __cstring
        int segDataCmd = 72 + 3 * 80;   // __got, __data, __bss
        int segLinkCmd = 72 + 0 * 80;   // __LINKEDIT 无节
        // 变长字符串命令的总尺寸必须 8 字节对齐（命令起始处已 8 对齐，
        // 故按“固定部分 + 名称(含 \0)”整体向上取整到 8）。仅对 name 单独取整
        // 会在固定部分非 8 对齐时（如 dylinker 的 12 字节）少算填充，导致
        // cmdsize 与实际字节不符，dyld 按 cmdsize 步进会错位。
        int lcDylinker = AlignPadded(12 + Dyld.Length + 1, 8);
        int lcDylib = AlignPadded(24 + Dylib.Length + 1, 8);
        int lcMain = 24;
        int lcSymtab = 24;
        int lcDysymtab = 80;
        int lcCodeSig = 16; // linkedit_data_command（cmd+cmdsize+dataoff+datasize）
        int lcBuildVer = 24; // build_version_command（cmd+cmdsize+platform+minos+sdk+ntools）
        int lcUuid = 24;     // uuid_command（cmd+cmdsize+uuid[16]）
        int sizeofcmds = segTextCmd + segDataCmd + segLinkCmd + lcDylinker + lcDylib + lcMain + lcSymtab + lcDysymtab + lcCodeSig + lcBuildVer + lcUuid;
        int ncmds = 11;

        int headerSize = 32;
        int textFileOff = headerSize + sizeofcmds; // __text 文件偏移
        long textVmaddr = TextVmaddr + textFileOff;
        long stubsVmaddr = textVmaddr + stubsOff;
        long cstringFileOff = textFileOff + text.Count;
        long cstringVmaddr = stubsVmaddr + (text.Count - stubsOff); // = textVmaddr + text.Count
        long textSegFileEnd = cstringFileOff + cstring.Count;
        long textSegVmEnd = cstringVmaddr + cstring.Count;

        // __DATA 段
        long dataSegFileOff = Align(textSegFileEnd, Page);
        long dataSegVmaddr = Align(textSegVmEnd, Page);
        long gotFileOff = dataSegFileOff;
        long gotVmaddr = dataSegVmaddr;
        long dataFileOff = gotFileOff + got.Count;
        long dataVmaddr = gotVmaddr + got.Count;
        long bssVmaddr = dataVmaddr + data.Count;
        long dataSegFileEnd = dataFileOff + data.Count;
        long dataSegVmEnd = bssVmaddr + bssSize;

        // __LINKEDIT 段
        long linkSegFileOff = Align(dataSegFileEnd, Page);
        long linkSegVmaddr = Align(dataSegVmEnd, Page);

        // LINKEDIT 内布局：bind, symtab, indirect, strtab —— 每个数据结构 8 字节对齐，
        // 否则 dyld 报 "mis-aligned LINKEDIT content" 并拒绝加载（SIGKILL）。
        long bindOff = linkSegFileOff;
        long symtabOff = Align(bindOff + bind.Count, 8);
        long indirectOff = Align(symtabOff + symtab.Count, 8);
        long strtabOff = Align(indirectOff + indirect.Count, 8);
        // 代码签名（ad-hoc）：16 字节对齐后附加到 __LINKEDIT 末尾
        long sigOff = Align(strtabOff + strtab.Count, 16);
        int codeLimit = (int)sigOff;
        int sigBlobSize = MachOCodeSignature.ComputeBlobSize(codeLimit);
        long linkSegFileEnd = sigOff + sigBlobSize;
        long linkSegVmEnd = linkSegVmaddr + (linkSegFileEnd - linkSegFileOff);

        // ---- 填充 main 符号 n_value ----
        long mainVmaddr = textVmaddr + mainOff;
        Write64At(symtab, mainSymIdx * 16 + 8, mainVmaddr);

        // ---- 填充 __stubs（jmp [rip+__got slot]）----
        for (int i = 0; i < nExt; i++)
        {
            int p = stubsOff + i * stubSize;
            long sv = stubsVmaddr + i * stubSize;
            long gotSlot = gotVmaddr + i * 8;
            text[p] = 0xFF; text[p + 1] = 0x25;
            Write32At(text, p + 2, (int)(gotSlot - (sv + 6)));
        }

        // ---- 解析 fixup ----
        foreach (var f in img.Fixups)
        {
            var list = f.Section == img.Data ? data : f.Section == img.RData ? cstring : text;
            long secBase = f.Section == img.Text ? textVmaddr
                : f.Section == img.RData ? cstringVmaddr
                : f.Section == img.Data ? dataVmaddr : bssVmaddr;
            long fixVmaddr = secBase + f.Offset;

            if (f.Kind == FixupKind.Rel32)
            {
                long target = SymVmaddr(f.Symbol, img, textVmaddr, cstringVmaddr, dataVmaddr, bssVmaddr, stubsVmaddr, externals);
                Write32At(list, f.Offset, (int)(target - (fixVmaddr + 4)));
            }
            else if (f.Kind == FixupKind.ExtSlot32)
            {
                // 先检查是否为写入器定义的符号（如 paze_mac_send_rect 蹦床）
                if (img.Symbols.TryGetValue(f.Symbol, out var defSym) && defSym.Section == img.Text)
                {
                    long target = textVmaddr + defSym.Offset;
                    Write32At(list, f.Offset, (int)(target - (fixVmaddr + 4)));
                }
                else
                {
                    int idx = externals.IndexOf(f.Symbol);
                    long target = stubsVmaddr + idx * stubSize;
                    Write32At(list, f.Offset, (int)(target - (fixVmaddr + 4)));
                }
            }
            else if (f.Kind == FixupKind.Abs64)
            {
                long target = SymVmaddr(f.Symbol, img, textVmaddr, cstringVmaddr, dataVmaddr, bssVmaddr, stubsVmaddr, externals);
                Write64At(list, f.Offset, target);
            }
        }

        // ============ 装配 Mach-O ============
        var f2 = new List<byte>();

        // ---- mach_header_64 (32) ----
        Write32At(f2, MH_MAGIC_64);
        Write32At(f2, CPU_TYPE_X86_64);
        Write32At(f2, (uint)CPU_SUBTYPE_ALL);
        Write32At(f2, (uint)MH_EXECUTE);
        Write32At(f2, (uint)ncmds);
        Write32At(f2, (uint)sizeofcmds);
        Write32At(f2, 0); // flags（非 PIE）
        Write32At(f2, 0); // reserved

        // ---- LC_SEGMENT_64 __TEXT ----
        // vmsize 必须页对齐（4096 倍数），内核按页映射内存，非页对齐 vmsize 会被 AMFI 拒绝。
        long textSegVmsize = Align(textSegVmEnd, Page) - TextVmaddr;
        WriteSegment64(f2, "__TEXT", TextVmaddr, textSegVmsize,
            0, textSegFileEnd, VM_PROT_READ | VM_PROT_EXECUTE, VM_PROT_READ | VM_PROT_EXECUTE, 3);
        WriteSection64(f2, "__text", "__TEXT", textVmaddr, text.Count, textFileOff, 4, 0, 0);
        WriteSection64(f2, "__stubs", "__TEXT", stubsVmaddr, text.Count - stubsOff, textFileOff + stubsOff, 0, 0, 0);
        WriteSection64(f2, "__cstring", "__TEXT", cstringVmaddr, cstring.Count, cstringFileOff, 0, 0, 0);

        // ---- LC_SEGMENT_64 __DATA ----
        long dataSegVmsize = dataSegVmEnd - dataSegVmaddr;
        long dataSegFilesize = dataSegFileEnd - dataSegFileOff;
        WriteSegment64(f2, "__DATA", dataSegVmaddr, dataSegVmsize, dataSegFileOff, dataSegFilesize,
            VM_PROT_READ | VM_PROT_WRITE, VM_PROT_READ | VM_PROT_WRITE, 3);
        WriteSection64(f2, "__got", "__DATA", gotVmaddr, got.Count, gotFileOff, 3, S_NON_LAZY_SYMBOL_POINTERS, 0);
        WriteSection64(f2, "__data", "__DATA", dataVmaddr, data.Count, dataFileOff, 3, 0, 0);
        WriteSection64(f2, "__bss", "__DATA", bssVmaddr, bssSize, 0, 3, 0x01 /*S_ZEROFILL*/, 0);

        // ---- LC_SEGMENT_64 __LINKEDIT ----
        WriteSegment64(f2, "__LINKEDIT", linkSegVmaddr, linkSegVmEnd - linkSegVmaddr,
            linkSegFileOff, linkSegFileEnd - linkSegFileOff, VM_PROT_READ, VM_PROT_READ, 0);

        // ---- LC_LOAD_DYLINKER ----
        Write32At(f2, LC_LOAD_DYLINKER);
        Write32At(f2, (uint)lcDylinker);
        Write32At(f2, 12); // name offset
        var dyldName = Encoding.ASCII.GetBytes(Dyld + "\0");
        f2.AddRange(dyldName);
        while (f2.Count % 8 != 0) f2.Add(0);

        // ---- LC_LOAD_DYLIB ----
        Write32At(f2, LC_LOAD_DYLIB);
        Write32At(f2, (uint)lcDylib);
        Write32At(f2, 24); // name offset
        Write32At(f2, 0);  // timestamp
        Write32At(f2, 0x10000); // current_version
        Write32At(f2, 0);  // compatible_version
        var dylibName = Encoding.ASCII.GetBytes(Dylib + "\0");
        f2.AddRange(dylibName);
        while (f2.Count % 8 != 0) f2.Add(0);

        // ---- LC_MAIN ----
        Write32At(f2, LC_MAIN);
        Write32At(f2, 24);
        Write64At(f2, textFileOff + mainOff); // entryoff（相对 __TEXT 段起始 = 文件偏移）
        Write64At(f2, 0); // stacksize

        // ---- LC_SYMTAB ----
        Write32At(f2, LC_SYMTAB);
        Write32At(f2, 24);
        Write32At(f2, (uint)symtabOff);
        Write32At(f2, (uint)(symtab.Count / 16));
        Write32At(f2, (uint)strtabOff);
        Write32At(f2, (uint)strtab.Count);

        // ---- LC_DYSYMTAB ----
        Write32At(f2, LC_DYSYMTAB);
        Write32At(f2, 80);
        Write32At(f2, 1);  // ilocalsym (main)
        Write32At(f2, 1);  // nlocalsym
        Write32At(f2, 2);  // iextdefsym
        Write32At(f2, 0);  // nextdefsym
        Write32At(f2, 2);  // iundefsym
        Write32At(f2, (uint)nExt); // nundefsym
        Write32At(f2, 0); Write32At(f2, 0); // itocoff, ntoc
        Write32At(f2, 0); Write32At(f2, 0); // modtaboff, nmodtab
        Write32At(f2, 0); Write32At(f2, 0); // extrefsymoff, nextrefsyms
        Write32At(f2, (uint)indirectOff); Write32At(f2, (uint)nExt); // indirectsymoff, nindirectsyms
        Write32At(f2, 0); Write32At(f2, 0); // extreloff, nextrel
        Write32At(f2, 0); Write32At(f2, 0); // locreloff, nlocrel

        // ---- LC_BUILD_VERSION ----
        Write32At(f2, LC_BUILD_VERSION);
        Write32At(f2, 24);            // cmdsize
        Write32At(f2, 1);             // platform = PLATFORM_MACOS
        Write32At(f2, 0x000B0000);    // minos = macOS 11.0.0
        Write32At(f2, 0x000E0000);    // sdk = macOS 14.0.0
        Write32At(f2, 0);             // ntools = 0

        // ---- LC_UUID ----（macOS dyld 要求，缺失则 dyld_info 报 "missing LC_UUID"）
        byte[] uuid = Guid.NewGuid().ToByteArray();
        Write32At(f2, LC_UUID);
        Write32At(f2, 24);            // cmdsize
        f2.AddRange(uuid);            // 16 字节 UUID

        // ---- LC_CODE_SIGNATURE ----
        Write32At(f2, LC_CODE_SIGNATURE);
        Write32At(f2, 16);                    // cmdsize (linkedit_data_command)
        Write32At(f2, (uint)sigOff);          // dataoff
        Write32At(f2, (uint)sigBlobSize);     // datasize

        // ---- __TEXT 段数据 ----
        f2.AddRange(text);
        f2.AddRange(cstring);

        // ---- __DATA 段数据 ----
        while (f2.Count < dataSegFileOff) f2.Add(0);
        f2.AddRange(got);
        f2.AddRange(data);
        // __bss 无文件内容

        // ---- __LINKEDIT 段数据 ----（各子表之间 8 字节对齐填充）
        while (f2.Count < linkSegFileOff) f2.Add(0);
        f2.AddRange(bind);
        while (f2.Count < symtabOff) f2.Add(0);
        f2.AddRange(symtab);
        while (f2.Count < indirectOff) f2.Add(0);
        f2.AddRange(indirect);
        while (f2.Count < strtabOff) f2.Add(0);
        f2.AddRange(strtab);
        // 代码签名（ad-hoc SHA-256）：对文件 [0..codeLimit) 逐页哈希
        while (f2.Count < sigOff) f2.Add(0);
        byte[] sigBlob = MachOCodeSignature.Build(f2.ToArray(), codeLimit);
        f2.AddRange(sigBlob);

        return f2.ToArray();
    }

    private static long SymVmaddr(string sym, ObjectImage img,
        long textVm, long cstringVm, long dataVm, long bssVm, long stubsVm, List<string> externals)
    {
        if (img.Symbols.TryGetValue(sym, out var ds))
        {
            long baseVm = ds.Section == img.Text ? textVm
                : ds.Section == img.RData ? cstringVm
                : ds.Section == img.Data ? dataVm : bssVm;
            return baseVm + ds.Offset;
        }
        int idx = externals.IndexOf(sym);
        if (idx >= 0) return stubsVm + idx * 6;
        return textVm;
    }

    private static void WriteSegment64(List<byte> f, string segname, long vmaddr, long vmsize,
        long fileoff, long filesize, int maxprot, int initprot, int nsects)
    {
        Write32At(f, LC_SEGMENT_64);
        Write32At(f, (uint)(72 + nsects * 80));
        var nb = new byte[16];
        var sn = Encoding.ASCII.GetBytes(segname);
        Array.Copy(sn, nb, Math.Min(sn.Length, 16));
        f.AddRange(nb);
        Write64At(f, vmaddr);
        Write64At(f, vmsize);
        Write64At(f, fileoff);
        Write64At(f, filesize);
        Write32At(f, (uint)maxprot);
        Write32At(f, (uint)initprot);
        Write32At(f, (uint)nsects);
        Write32At(f, 0);
    }

    private static void WriteSection64(List<byte> f, string sectname, string segname, long addr, long size,
        long offset, int align, int flags, int reserved1)
    {
        var nb1 = new byte[16]; var sn1 = Encoding.ASCII.GetBytes(sectname);
        Array.Copy(sn1, nb1, Math.Min(sn1.Length, 16)); f.AddRange(nb1);
        var nb2 = new byte[16]; var sn2 = Encoding.ASCII.GetBytes(segname);
        Array.Copy(sn2, nb2, Math.Min(sn2.Length, 16)); f.AddRange(nb2);
        Write64At(f, addr);
        Write64At(f, size);
        Write32At(f, (uint)offset);
        Write32At(f, (uint)align);
        Write32At(f, 0); // reloff
        Write32At(f, 0); // nreloc
        Write32At(f, (uint)flags);
        Write32At(f, (uint)reserved1);
        Write32At(f, 0); // reserved2
        Write32At(f, 0); // reserved3
    }

    private static void WriteNlist(List<byte> s, int strx, byte type, byte sect, short desc, long value)
    {
        Write32At(s, strx);
        s.Add(type);
        s.Add(sect);
        Write16At(s, (ushort)desc);
        Write64At(s, value);
    }

    private static void WriteUleb128(List<byte> b, ulong v)
    {
        while (v >= 0x80) { b.Add((byte)((v & 0x7F) | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }

    private static long Align(long v, int a) => a <= 1 ? v : (v + a - 1) & ~(long)(a - 1);
    private static int AlignPadded(int len, int a) => (len + a - 1) & ~(a - 1);

    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void Write32At(List<byte> b, uint v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, (int)v); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
