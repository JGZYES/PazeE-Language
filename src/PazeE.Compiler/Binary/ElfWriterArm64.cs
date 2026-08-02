using System.Text;
using PazeE.Compiler.CodeGen.Arm64;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;

namespace PazeE.Compiler.Binary;

/// <summary>Linux ELF64 (AArch64) 可执行文件写入器（ET_EXEC，固定基址 0x400000）。
/// 独立于 x86 ElfWriter，不共享任何逻辑。自带 ARM64 _start 入口桩（bl main → exit syscall），
/// 外部 libc 符号通过 .plt（ADRP+LDR+ADD+BR）+ .got.plt + .rela.plt(R_AARCH64_JUMP_SLOT)
/// 经 eager binding（DT_BIND_NOW）解析，动态链接器为 /lib/ld-linux-aarch64.so.1。
/// 符号地址引用经 ADRP+ADD 物化（AdrpAdd fixup），.data 指针初始化用 Abs64 绝对地址（非 PIE）。</summary>
public sealed class ElfWriterArm64 : IExecutableWriter
{
    public Platform Platform => Platform.Linux;

    private const long BaseAddr = 0x400000L;
    private const int Page = 0x1000;
    private const string Interp = "/lib/ld-linux-aarch64.so.1";

    // ELF 常量
    private const ushort ET_EXEC = 2;
    private const ushort EM_AARCH64 = 183;
    private const int PT_LOAD = 1, PT_INTERP = 3, PT_DYNAMIC = 2, PT_GNU_STACK = 0x6474e551;
    private const int PF_X = 1, PF_W = 2, PF_R = 4;
    private const int DT_NULL = 0, DT_NEEDED = 1, DT_PLTRELSZ = 2, DT_PLTGOT = 3,
        DT_STRTAB = 5, DT_SYMTAB = 6, DT_STRSZ = 10, DT_SYMENT = 11,
        DT_PLTREL = 20, DT_JMPREL = 23, DT_BIND_NOW = 24, DT_FLAGS = 30;
    private const int DF_BIND_NOW = 0x08;
    private const int DT_RELA_KIND = 7;
    private const int R_AARCH64_JUMP_SLOT = 1026;

    public byte[] Write(ObjectImage img)
    {
        var text = new List<byte>(img.Text.Data);
        var rodata = new List<byte>();
        var data = new List<byte>(img.Data.Data);
        int bssSize = img.Bss.BssSize;
        var externals = new List<string>(img.Externals);
        int nExt = externals.Count;

        // ---- 1. 入口桩（ARM64 _start）----
        var (stubCode, stubFx) = StartupStubs.Arm64Linux(img.HasArgcArgv);
        int stubOff = text.Count;
        text.AddRange(stubCode);
        long entryOff = stubOff;

        var fixups = new List<(ImageSection sec, int off, FixupKind kind, string sym)>();
        foreach (var f in img.Fixups) fixups.Add((f.Section, f.Offset, f.Kind, f.Symbol));
        foreach (var f in stubFx) fixups.Add((img.Text, stubOff + f.Offset, f.Kind, f.Symbol));

        // ---- 2. PLT 占位（PLT[0] + nExt 个条目，各 16 字节）----
        int pltOff = text.Count;
        text.AddRange(new byte[16 * (1 + nExt)]);

        // ---- 3. interp 字符串（放 .rodata 开头）----
        byte[] interpBytes = Encoding.ASCII.GetBytes(Interp + "\0");
        rodata.AddRange(interpBytes);
        int interpOff = 0;
        rodata.AddRange(img.RData.Data);

        // ---- 4. .dynstr ----
        var dynstr = new List<byte>();
        dynstr.Add(0);
        int neededOff = dynstr.Count;
        dynstr.AddRange(Encoding.ASCII.GetBytes(LibcDecls.LinuxLibc));
        dynstr.Add(0);
        var nameOff = new Dictionary<string, int>();
        foreach (var ext in externals)
        {
            nameOff[ext] = dynstr.Count;
            dynstr.AddRange(Encoding.ASCII.GetBytes(ext));
            dynstr.Add(0);
        }

        // ---- 5. .dynsym（[0] 空 + nExt 个 UNDEF）----
        var dynsym = new List<byte>();
        dynsym.AddRange(new byte[24]);
        foreach (var ext in externals)
            WriteSym(dynsym, nameOff[ext], 0x12 /*GLOBAL|FUNC*/, 0, 0, 0);

        // ---- 6. .got.plt（3 + nExt 槽；eager binding 下 [3..]=0 由 loader 填）----
        int gotPltCount = 3 + nExt;
        var gotPlt = new List<byte>(new byte[gotPltCount * 8]);

        // ---- 7. .rela.plt（nExt 个 Elf64_Rela，R_AARCH64_JUMP_SLOT）----
        var relaPlt = new List<byte>();
        for (int i = 0; i < nExt; i++) relaPlt.AddRange(new byte[24]);

        // ---- 8. .dynamic（占位）----
        int dynCount = 12; // NEEDED, BIND_NOW, PLTRELSZ, PLTGOT, STRTAB, SYMTAB, STRSZ, SYMENT, PLTREL, JMPREL, FLAGS, NULL
        var dynamic = new List<byte>();
        dynamic.AddRange(new byte[dynCount * 16]);

        // ============ 段布局（file_offset = vaddr - BaseAddr）============
        int ehdrSize = 64;
        int phnum = 6; // LOAD×3, INTERP, DYNAMIC, GNU_STACK
        int phdrsSize = phnum * 56;
        int phoff = ehdrSize;

        long textVaddr = BaseAddr + ehdrSize + phdrsSize;
        long pltVaddr = textVaddr + pltOff;
        long entryVaddr = textVaddr + entryOff;
        long seg1End = textVaddr + text.Count;

        long rodataVaddr = Align(seg1End, Page);
        long rodataOff = rodataVaddr - BaseAddr;
        long rdataBaseVaddr = rodataVaddr + interpBytes.Length; // img.RData 符号基准
        long seg2End = rodataVaddr + rodata.Count;

        long dataVaddr = Align(seg2End, Page);
        long dataOff = dataVaddr - BaseAddr;
        long bssVaddr = dataVaddr + data.Count;
        long gotPltVaddr = dataVaddr + data.Count;
        long dynamicVaddr = gotPltVaddr + gotPlt.Count;
        long dynsymVaddr = dynamicVaddr + dynamic.Count;
        long dynstrVaddr = dynsymVaddr + dynsym.Count;
        long relaPltVaddr = dynstrVaddr + dynstr.Count;
        long seg3FileEnd = relaPltVaddr + relaPlt.Count;
        long seg3MemEnd = seg3FileEnd + bssSize;

        // ============ 填充 .got.plt ============
        Write64At(gotPlt, 0, dynamicVaddr);  // [0] = _DYNAMIC
        // [1],[2] = 0；[3..] = 0（eager binding 由 loader 填）
        for (int i = 0; i < nExt; i++)
        {
            int slot = (3 + i) * 8;
            Write64At(gotPlt, slot, 0);  // eager binding：loader 启动时填函数地址
        }

        // ============ 填充 .rela.plt ============
        for (int i = 0; i < nExt; i++)
        {
            long rOffset = gotPltVaddr + (3 + i) * 8;
            long rInfo = ((long)(i + 1) << 32) | R_AARCH64_JUMP_SLOT;
            Write64At(relaPlt, i * 24 + 0, rOffset);
            Write64At(relaPlt, i * 24 + 8, rInfo);
            Write64At(relaPlt, i * 24 + 16, 0);
        }

        // ============ 填充 .dynamic ============
        var dt = new List<(int tag, long val)>
        {
            (DT_NEEDED, neededOff),
            (DT_BIND_NOW, 0),
            (DT_PLTRELSZ, relaPlt.Count),
            (DT_PLTGOT, gotPltVaddr),
            (DT_STRTAB, dynstrVaddr),
            (DT_SYMTAB, dynsymVaddr),
            (DT_STRSZ, dynstr.Count),
            (DT_SYMENT, 24),
            (DT_PLTREL, DT_RELA_KIND),
            (DT_JMPREL, relaPltVaddr),
            (DT_FLAGS, DF_BIND_NOW),
            (DT_NULL, 0),
        };
        for (int i = 0; i < dt.Count; i++)
        {
            Write64At(dynamic, i * 16 + 0, dt[i].tag);
            Write64At(dynamic, i * 16 + 8, dt[i].val);
        }

        // ============ 填充 PLT ============
        // PLT[0]：4 条 NOP（eager binding 无需 resolver 桩）
        for (int i = 0; i < 4; i++) Write32At(text, pltOff + i * 4, unchecked((int)0xD503201F));
        // PLT[i] (i=0..nExt-1)：ADRP X16,page(gotSlot); LDR X17,[X16,#lo12]; ADD X16,X16,#lo12; BR X17
        for (int i = 0; i < nExt; i++)
        {
            int p = pltOff + 16 + i * 16;
            long pv = pltVaddr + 16 + i * 16;
            long gotSlot = gotPltVaddr + (3 + i) * 8;
            long pcPage = pv & ~0xFFFL;
            long tgtPage = gotSlot & ~0xFFFL;
            long pageDelta = (tgtPage - pcPage) >> 12;
            int immlo = (int)(pageDelta & 3);
            int immhi = (int)((pageDelta >> 2) & 0x7FFFF);
            int lo12 = (int)(gotSlot & 0xFFF);
            Write32At(text, p + 0, Arm64Emitter.EncodeAdrp(immlo, immhi, 16));          // ADRP X16
            Write32At(text, p + 4, Arm64Emitter.EncodeLdrImm(17, 16, lo12 / 8));         // LDR X17,[X16,#lo12]
            Write32At(text, p + 8, Arm64Emitter.EncodeAddImm(16, 16, lo12, true));        // ADD X16,X16,#lo12
            Write32At(text, p + 12, Arm64Emitter.EncodeBr(17));                           // BR X17
        }

        // ============ 解析 fixup ============
        foreach (var (sec, off, kind, sym) in fixups)
        {
            var list = sec == img.Data ? data : sec == img.RData ? rodata : text;
            long secBase;
            if (sec == img.Text) secBase = textVaddr;
            else if (sec == img.RData) secBase = rdataBaseVaddr;
            else if (sec == img.Data) secBase = dataVaddr;
            else secBase = bssVaddr;
            long fixVaddr = secBase + off;

            if (kind == FixupKind.Call26)
            {
                long target = SymVaddr(sym, img, textVaddr, rdataBaseVaddr, dataVaddr, bssVaddr, pltVaddr, externals);
                int imm26 = (int)((target - fixVaddr) >> 2);
                Write32At(list, off, Arm64Emitter.EncodeBl(imm26));
            }
            else if (kind == FixupKind.ExtCall26)
            {
                int idx = externals.IndexOf(sym);
                long target = pltVaddr + 16 + idx * 16; // PLT[i]
                int imm26 = (int)((target - fixVaddr) >> 2);
                Write32At(list, off, Arm64Emitter.EncodeBl(imm26));
            }
            else if (kind == FixupKind.AdrpAdd)
            {
                long target = SymVaddr(sym, img, textVaddr, rdataBaseVaddr, dataVaddr, bssVaddr, pltVaddr, externals);
                long pcPage = fixVaddr & ~0xFFFL;
                long tgtPage = target & ~0xFFFL;
                long pageDelta = (tgtPage - pcPage) >> 12;
                int immlo = (int)(pageDelta & 3);
                int immhi = (int)((pageDelta >> 2) & 0x7FFFF);
                int lo12 = (int)(target & 0xFFF);
                int rd = Read32(list, off) & 0x1F;  // 保留原 ADRP 的 Rd
                Write32At(list, off, Arm64Emitter.EncodeAdrp(immlo, immhi, rd));
                Write32At(list, off + 4, Arm64Emitter.EncodeAddImm(rd, rd, lo12, true));
            }
            else if (kind == FixupKind.Abs64)
            {
                long target = SymVaddr(sym, img, textVaddr, rdataBaseVaddr, dataVaddr, bssVaddr, pltVaddr, externals);
                Write64At(list, off, target);
            }
        }

        // ============ 装配 ELF 文件 ============
        return Assemble(text, rodata, data, gotPlt, dynamic, dynsym, dynstr, relaPlt,
            bssSize, entryVaddr, phnum, phoff,
            rodataVaddr, rodataOff, interpOff, interpBytes.Length,
            dataVaddr, dataOff, seg3FileEnd - dataVaddr, seg3MemEnd - dataVaddr);
    }

    private static long SymVaddr(string sym, ObjectImage img,
        long textVaddr, long rdataVaddr, long dataVaddr, long bssVaddr, long pltVaddr, List<string> externals)
    {
        if (img.Symbols.TryGetValue(sym, out var ds))
        {
            long baseVaddr = ds.Section == img.Text ? textVaddr
                : ds.Section == img.RData ? rdataVaddr
                : ds.Section == img.Data ? dataVaddr : bssVaddr;
            return baseVaddr + ds.Offset;
        }
        int idx = externals.IndexOf(sym);
        if (idx >= 0) return pltVaddr + 16 + idx * 16;
        return textVaddr;
    }

    private static byte[] Assemble(List<byte> text, List<byte> rodata, List<byte> data,
        List<byte> gotPlt, List<byte> dynamic, List<byte> dynsym, List<byte> dynstr, List<byte> relaPlt,
        int bssSize, long entryVaddr, int phnum, int phoff,
        long rodataVaddr, long rodataOff, int interpOff, int interpSize,
        long dataVaddr, long dataOff, long seg3FileSz, long seg3MemSz)
    {
        long seg1Vaddr = BaseAddr;
        long seg1Off = 0;
        long seg1FileSz = 64 + phnum * 56 + text.Count;

        var f = new List<byte>();

        // ---- ELF header (64) ----
        byte[] ident = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        f.AddRange(ident);
        Write16At(f, ET_EXEC);
        Write16At(f, EM_AARCH64);
        Write32At(f, 1);                 // e_version
        Write64At(f, entryVaddr);        // e_entry
        Write64At(f, phoff);             // e_phoff
        Write64At(f, 0);                 // e_shoff
        Write32At(f, 0);                 // e_flags
        Write16At(f, 64);                // e_ehsize
        Write16At(f, 56);                // e_phentsize
        Write16At(f, (ushort)phnum);     // e_phnum
        Write16At(f, 64);                // e_shentsize
        Write16At(f, 0);                 // e_shnum
        Write16At(f, 0);                 // e_shstrndx

        // ---- Program headers ----
        WritePhdr(f, PT_LOAD, PF_R | PF_X, seg1Off, seg1Vaddr, seg1FileSz, seg1FileSz, Page);
        WritePhdr(f, PT_INTERP, PF_R, rodataOff + interpOff, rodataVaddr + interpOff, interpSize, interpSize, 1);
        WritePhdr(f, PT_LOAD, PF_R, rodataOff, rodataVaddr, rodata.Count, rodata.Count, Page);
        WritePhdr(f, PT_LOAD, PF_R | PF_W, dataOff, dataVaddr, seg3FileSz, seg3MemSz, Page);
        WritePhdr(f, PT_DYNAMIC, PF_R | PF_W, dataOff + (data.Count + gotPlt.Count), dataVaddr + data.Count + gotPlt.Count, dynamic.Count, dynamic.Count, 8);
        WritePhdr(f, PT_GNU_STACK, PF_R | PF_W, 0, 0, 0, 0, Page);

        // ---- 段数据 ----
        f.AddRange(text);
        while (f.Count < rodataOff) f.Add(0);
        f.AddRange(rodata);
        while (f.Count < dataOff) f.Add(0);
        f.AddRange(data);
        f.AddRange(gotPlt);
        f.AddRange(dynamic);
        f.AddRange(dynsym);
        f.AddRange(dynstr);
        f.AddRange(relaPlt);

        return f.ToArray();
    }

    private static void WritePhdr(List<byte> f, int type, int flags, long off, long vaddr, long filesz, long memsz, long align)
    {
        Write32At(f, type);
        Write32At(f, flags);
        Write64At(f, off);
        Write64At(f, vaddr);
        Write64At(f, vaddr);
        Write64At(f, filesz);
        Write64At(f, memsz);
        Write64At(f, align);
    }

    private static void WriteSym(List<byte> s, int name, int info, int other, int shndx, long value)
    {
        Write32At(s, name);
        s.Add((byte)info);
        s.Add((byte)other);
        Write16At(s, (ushort)shndx);
        Write64At(s, value);
        Write64At(s, 0);
    }

    private static long Align(long v, int a) => a <= 1 ? v : (v + a - 1) & ~(long)(a - 1);

    private static int Read32(List<byte> b, int off) => b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);

    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
