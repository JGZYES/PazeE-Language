using System.Text;
using PazeE.Compiler.CodeGen.Arm64;
using PazeE.Compiler.Parser;

namespace PazeE.Compiler.Binary;

/// <summary>macOS Mach-O 64 (AArch64) 可执行文件写入器（MH_EXECUTE，固定基址 0x100000000）。
/// 独立于 x64 MachOWriter，不共享任何逻辑。用 LC_MAIN 指定 main 入口（dyld 提供 _start），
/// 无需自带入口桩。外部 libc 符号通过 __stubs（ADRP X16+LDR X16+BR X16，12 字节）
/// + __got 非懒绑定（dyld 加载时按 BIND opcodes 绑定），加载 /usr/lib/libSystem.dylib。
/// 符号地址引用经 ADRP+ADD 物化（AdrpAdd fixup），.data 指针初始化用 Abs64 绝对地址（非 PIE）。</summary>
public sealed class MachOWriterArm64 : IExecutableWriter
{
    public Platform Platform => Platform.MacOS;

    private const long TextVmaddr = 0x100000000L;
    private const int Page = 0x1000;
    private const string Dylib = "/usr/lib/libSystem.dylib";
    private const string Dyld = "/usr/lib/dyld";
    // ARM64 stub：ADRP X16 + LDR X16 + BR X16 = 12 字节
    private const int StubSize = 12;

    // Mach-O 常量
    private const uint MH_MAGIC_64 = 0xFEEDFACF;
    private const uint CPU_TYPE_ARM64 = 0x0100000C;
    private const int CPU_SUBTYPE_ARM64_ALL = 0;
    private const int MH_EXECUTE = 2;
    private const uint LC_SEGMENT_64 = 0x19;
    private const uint LC_SYMTAB = 0x02;
    private const uint LC_LOAD_DYLIB = 0x0C;
    private const uint LC_LOAD_DYLINKER = 0x0E;
    private const uint LC_DYSYMTAB = 0x0B;
    private const uint LC_MAIN = 0x80000028;
    private const int VM_PROT_READ = 1, VM_PROT_WRITE = 2, VM_PROT_EXECUTE = 4;
    private const int S_NON_LAZY_SYMBOL_POINTERS = 0x06;
    private const byte N_EXT = 0x01, N_SECT = 0x0e;
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
        // macOS GUI 蹦床：ARM64 上 NSRect(32B)>16B 按 AAPCS64 也按指针传递，
        // 与 PazeE 的结构体指针传递一致，蹦床只需保存 LR → BL objc_msgSend → 恢复 LR → RET。
        bool needTrampoline = externals.Remove("paze_mac_send_rect");
        int nExt = externals.Count;

        // main 偏移（LC_MAIN 入口）
        long mainOff = img.Symbols.TryGetValue(img.EntrySymbol, out var mainSym) ? mainSym.Offset : 0;

        // ---- 1. __stubs 占位（每个外部符号 12 字节）----
        int stubsOff = text.Count;
        text.AddRange(new byte[StubSize * nExt]);

        // ---- 1b. paze_mac_send_rect 蹦床（ARM64: STP/BL/LDP/RET，16 字节）----
        // ARM64 上 NSRect(32B) 按 AAPCS64 指针传递，与 PazeE 一致，直接转发给 objc_msgSend。
        int trampolineOff = -1;
        if (needTrampoline)
        {
            trampolineOff = text.Count;
            // STP X29, X30, [SP, #-16]!  (保存 FP/LR)
            Write32At(text, 0xA9BF7BFD);
            // BL objc_msgSend (占位，ExtCall26 fixup 填充)
            int blOff = text.Count;
            text.AddRange(new byte[4]);
            // LDP X29, X30, [SP], #16  (恢复 FP/LR)
            Write32At(text, 0xA8C17BFD);
            // RET
            Write32At(text, 0xD65F03C0);
            // 注册 fixup：BL objc_msgSend → ExtCall26（解析到 stub）
            img.AddFixup(img.Text, blOff, FixupKind.ExtCall26, "objc_msgSend");
            // 定义符号
            img.DefineSymbol("paze_mac_send_rect", img.Text, trampolineOff, true, true);
        }

        // ---- 2. __got 占位（nExt 个 8 字节非懒绑定槽）----
        var got = new List<byte>(new byte[8 * nExt]);

        // ---- 3. 字符串表 ----
        var strtab = new List<byte>();
        strtab.Add(0);
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
        var symtab = new List<byte>();
        symtab.AddRange(new byte[16]); // [0] null
        WriteNlist(symtab, mainStrx, N_SECT, 1, 0, 0); // main 本地定义
        int mainSymIdx = 1;
        int undefStart = 2;
        for (int i = 0; i < nExt; i++)
            WriteNlist(symtab, nameStrx[i], N_EXT, 0, 0, 0); // 外部 UNDEF

        // ---- 5. indirect symbol table（__got 的 nExt 项）----
        var indirect = new List<byte>();
        for (int i = 0; i < nExt; i++)
            Write32At(indirect, undefStart + i);

        // ---- 6. BIND opcodes（非懒绑定 __got，segment __DATA index=1）----
        var bind = new List<byte>();
        bind.Add((byte)(BIND_OPCODE_SET_DYLIB_ORDINAL_IMM | 1));
        bind.Add((byte)(BIND_OPCODE_SET_TYPE_IMM | BIND_TYPE_POINTER));
        for (int i = 0; i < nExt; i++)
        {
            bind.Add(BIND_OPCODE_SET_SYMBOL_NAME);
            bind.AddRange(Encoding.ASCII.GetBytes(externals[i]));
            bind.Add(0);
            bind.Add((byte)(BIND_OPCODE_SET_SEGMENT_AND_OFFSET_ULEB128 | 1)); // seg=1 (__DATA)
            WriteUleb128(bind, (ulong)(i * 8));
            bind.Add(BIND_OPCODE_DO_BIND);
        }
        bind.Add(BIND_OPCODE_DONE);

        // ============ 计算各 load command 大小 ============
        int segTextCmd = 72 + 3 * 80;   // __text, __stubs, __cstring
        int segDataCmd = 72 + 3 * 80;   // __got, __data, __bss
        int segLinkCmd = 72 + 0 * 80;   // __LINKEDIT 无节
        int lcDylinker = AlignPadded(12 + Dyld.Length + 1, 8);
        int lcDylib = AlignPadded(24 + Dylib.Length + 1, 8);
        int lcMain = 24;
        int lcSymtab = 24;
        int lcDysymtab = 80;
        int sizeofcmds = segTextCmd + segDataCmd + segLinkCmd + lcDylinker + lcDylib + lcMain + lcSymtab + lcDysymtab;
        int ncmds = 8;

        int headerSize = 32;
        int textFileOff = headerSize + sizeofcmds;
        long textVmaddr = TextVmaddr + textFileOff;
        long stubsVmaddr = textVmaddr + stubsOff;
        long cstringFileOff = textFileOff + text.Count;
        long cstringVmaddr = textVmaddr + text.Count;
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

        long bindOff = linkSegFileOff;
        long symtabOff = bindOff + bind.Count;
        long indirectOff = symtabOff + symtab.Count;
        long strtabOff = indirectOff + indirect.Count;
        long linkSegFileEnd = strtabOff + strtab.Count;
        long linkSegVmEnd = linkSegVmaddr + (linkSegFileEnd - linkSegFileOff);

        // ---- 填充 main 符号 n_value ----
        long mainVmaddr = textVmaddr + mainOff;
        Write64At(symtab, mainSymIdx * 16 + 8, mainVmaddr);

        // ---- 填充 __stubs（ADRP X16,page(got); LDR X16,[X16,#lo12]; BR X16）----
        for (int i = 0; i < nExt; i++)
        {
            int p = stubsOff + i * StubSize;
            long sv = stubsVmaddr + i * StubSize;
            long gotSlot = gotVmaddr + i * 8;
            long pcPage = sv & ~0xFFFL;
            long tgtPage = gotSlot & ~0xFFFL;
            long pageDelta = (tgtPage - pcPage) >> 12;
            int immlo = (int)(pageDelta & 3);
            int immhi = (int)((pageDelta >> 2) & 0x7FFFF);
            int lo12 = (int)(gotSlot & 0xFFF);
            Write32At(text, p + 0, Arm64Emitter.EncodeAdrp(immlo, immhi, 16));   // ADRP X16
            Write32At(text, p + 4, Arm64Emitter.EncodeLdrImm(16, 16, lo12 / 8));  // LDR X16,[X16,#lo12]
            Write32At(text, p + 8, Arm64Emitter.EncodeBr(16));                    // BR X16
        }

        // ---- 解析 fixup ----
        foreach (var f in img.Fixups)
        {
            var list = f.Section == img.Data ? data : f.Section == img.RData ? cstring : text;
            long secBase = f.Section == img.Text ? textVmaddr
                : f.Section == img.RData ? cstringVmaddr
                : f.Section == img.Data ? dataVmaddr : bssVmaddr;
            long fixVmaddr = secBase + f.Offset;

            if (f.Kind == FixupKind.Call26)
            {
                long target = SymVmaddr(f.Symbol, img, textVmaddr, cstringVmaddr, dataVmaddr, bssVmaddr, stubsVmaddr, externals);
                int imm26 = (int)((target - fixVmaddr) >> 2);
                Write32At(list, f.Offset, Arm64Emitter.EncodeBl(imm26));
            }
            else if (f.Kind == FixupKind.ExtCall26)
            {
                // 先检查是否为写入器定义的符号（如 paze_mac_send_rect 蹦床）
                long target;
                if (img.Symbols.TryGetValue(f.Symbol, out var defSym) && defSym.Section == img.Text)
                    target = textVmaddr + defSym.Offset;
                else
                {
                    int idx = externals.IndexOf(f.Symbol);
                    target = stubsVmaddr + idx * StubSize;
                }
                int imm26 = (int)((target - fixVmaddr) >> 2);
                Write32At(list, f.Offset, Arm64Emitter.EncodeBl(imm26));
            }
            else if (f.Kind == FixupKind.AdrpAdd)
            {
                long target = SymVmaddr(f.Symbol, img, textVmaddr, cstringVmaddr, dataVmaddr, bssVmaddr, stubsVmaddr, externals);
                long pcPage = fixVmaddr & ~0xFFFL;
                long tgtPage = target & ~0xFFFL;
                long pageDelta = (tgtPage - pcPage) >> 12;
                int immlo = (int)(pageDelta & 3);
                int immhi = (int)((pageDelta >> 2) & 0x7FFFF);
                int lo12 = (int)(target & 0xFFF);
                int rd = Read32(list, f.Offset) & 0x1F;
                Write32At(list, f.Offset, Arm64Emitter.EncodeAdrp(immlo, immhi, rd));
                Write32At(list, f.Offset + 4, Arm64Emitter.EncodeAddImm(rd, rd, lo12, true));
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
        Write32At(f2, CPU_TYPE_ARM64);
        Write32At(f2, (uint)CPU_SUBTYPE_ARM64_ALL);
        Write32At(f2, (uint)MH_EXECUTE);
        Write32At(f2, (uint)ncmds);
        Write32At(f2, (uint)sizeofcmds);
        Write32At(f2, 0); // flags（非 PIE）
        Write32At(f2, 0); // reserved

        // ---- LC_SEGMENT_64 __TEXT ----
        long textSegVmsize = textSegVmEnd - TextVmaddr;
        WriteSegment64(f2, "__TEXT", TextVmaddr, textSegVmsize, 0, textSegFileEnd, VM_PROT_READ | VM_PROT_EXECUTE, VM_PROT_READ | VM_PROT_EXECUTE, 3);
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
        Write32At(f2, 12);
        var dyldName = Encoding.ASCII.GetBytes(Dyld + "\0");
        f2.AddRange(dyldName);
        while (f2.Count % 8 != 0) f2.Add(0);

        // ---- LC_LOAD_DYLIB ----
        Write32At(f2, LC_LOAD_DYLIB);
        Write32At(f2, (uint)lcDylib);
        Write32At(f2, 24);
        Write32At(f2, 0);
        Write32At(f2, 0x10000);
        Write32At(f2, 0);
        var dylibName = Encoding.ASCII.GetBytes(Dylib + "\0");
        f2.AddRange(dylibName);
        while (f2.Count % 8 != 0) f2.Add(0);

        // ---- LC_MAIN ----
        Write32At(f2, LC_MAIN);
        Write32At(f2, 24);
        Write64At(f2, textFileOff + mainOff); // entryoff
        Write64At(f2, 0);

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
        Write32At(f2, 1);  // ilocalsym
        Write32At(f2, 1);  // nlocalsym
        Write32At(f2, 2);  // iextdefsym
        Write32At(f2, 0);  // nextdefsym
        Write32At(f2, 2);  // iundefsym
        Write32At(f2, (uint)nExt); // nundefsym
        Write32At(f2, 0); Write32At(f2, 0);
        Write32At(f2, 0); Write32At(f2, 0);
        Write32At(f2, 0); Write32At(f2, 0);
        Write32At(f2, (uint)indirectOff); Write32At(f2, (uint)nExt);
        Write32At(f2, 0); Write32At(f2, 0);
        Write32At(f2, 0); Write32At(f2, 0);

        // ---- __TEXT 段数据 ----
        f2.AddRange(text);
        f2.AddRange(cstring);

        // ---- __DATA 段数据 ----
        while (f2.Count < dataSegFileOff) f2.Add(0);
        f2.AddRange(got);
        f2.AddRange(data);

        // ---- __LINKEDIT 段数据 ----
        while (f2.Count < linkSegFileOff) f2.Add(0);
        f2.AddRange(bind);
        f2.AddRange(symtab);
        f2.AddRange(indirect);
        f2.AddRange(strtab);

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
        if (idx >= 0) return stubsVm + idx * StubSize;
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
        Write32At(f, 0);
        Write32At(f, 0);
        Write32At(f, (uint)flags);
        Write32At(f, (uint)reserved1);
        Write32At(f, 0);
        Write32At(f, 0);
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

    private static int Read32(List<byte> b, int off) => b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);

    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void Write32At(List<byte> b, uint v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, (int)v); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
