using System.Text;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;

namespace PazeE.Compiler.Binary;

/// <summary>Linux ELF64 (x86-64) 可执行文件写入器（ET_EXEC，固定基址 0x400000）。
/// 自带 _start 入口桩（call main → exit syscall），外部 libc 符号通过 .plt + .got.plt
/// 懒解析（.rela.plt 用 R_X86_64_JUMP_SLOT 重定位），动态链接器为 /lib64/ld-linux-x86-64.so.2。
/// 绝对地址引用（Abs64）直接填固定虚拟地址（非 PIE，无需运行时重定位）。</summary>
public sealed class ElfWriter : IExecutableWriter
{
    public Platform Platform => Platform.Linux;

    private const long BaseAddr = 0x400000L;
    private const int Page = 0x1000;
    private const string Interp = "/lib64/ld-linux-x86-64.so.2";

    // ELF 常量
    private const ushort ET_EXEC = 2;
    private const ushort EM_X86_64 = 62;
    private const int PT_LOAD = 1, PT_INTERP = 3, PT_DYNAMIC = 2, PT_GNU_STACK = 0x6474e551;
    private const int PF_X = 1, PF_W = 2, PF_R = 4;
    private const int DT_NULL = 0, DT_NEEDED = 1, DT_PLTRELSZ = 2, DT_PLTGOT = 3,
        DT_STRTAB = 5, DT_SYMTAB = 6, DT_RELA = 7, DT_STRSZ = 10, DT_SYMENT = 11,
        DT_PLTREL = 20, DT_JMPREL = 23;
    private const int DT_RELA_KIND = 7;
    private const int R_X86_64_JUMP_SLOT = 7;

    public byte[] Write(ObjectImage img)
    {
        var text = new List<byte>(img.Text.Data);
        var rodata = new List<byte>();
        var data = new List<byte>(img.Data.Data);
        int bssSize = img.Bss.BssSize;
        var externals = new List<string>(img.Externals);
        int nExt = externals.Count;

        // ---- 1. 入口桩 ----
        var (stubCode, stubFx) = StartupStubs.Linux(img.HasArgcArgv);
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

        // ---- 4. .dynstr（"\0" + 各 soname\0 + 各外部符号名\0）----
        // 多 DT_NEEDED：按 LinuxLibOf 把外部符号分组到 libc.so.6 / libX11.so.6 等。
        var dynstr = new List<byte>();
        dynstr.Add(0);
        var sonames = new List<string>();               // 唯一 soname（首次出现顺序）
        var sonameOff = new Dictionary<string, int>();   // soname → .dynstr 内偏移
        foreach (var ext in externals)
        {
            string lib = LibcDecls.LinuxLibOf(ext);
            if (!sonameOff.ContainsKey(lib))
            {
                sonameOff[lib] = dynstr.Count;
                sonames.Add(lib);
                dynstr.AddRange(Encoding.ASCII.GetBytes(lib));
                dynstr.Add(0);
            }
        }
        var nameOff = new Dictionary<string, int>();
        foreach (var ext in externals)
        {
            nameOff[ext] = dynstr.Count;
            dynstr.AddRange(Encoding.ASCII.GetBytes(ext));
            dynstr.Add(0);
        }

        // ---- 5. .dynsym（[0] 空 + nExt 个 UNDEF 符号）----
        var dynsym = new List<byte>();
        dynsym.AddRange(new byte[24]); // st_name=0, UNDEF
        foreach (var ext in externals)
            WriteSym(dynsym, nameOff[ext], 0x12 /*GLOBAL|FUNC*/, 0 /*UNDEF*/, 0, 0);

        // ---- 6. .got.plt（3 + nExt 个 8 字节槽；[0]=_DYNAMIC, [1]=[2]=0, [3..]=PLT[i]+6）----
        int gotPltCount = 3 + nExt;
        var gotPlt = new List<byte>(new byte[gotPltCount * 8]);

        // ---- 7. .rela.plt（nExt 个 Elf64_Rela，R_X86_64_JUMP_SLOT）----
        var relaPlt = new List<byte>();
        for (int i = 0; i < nExt; i++) relaPlt.AddRange(new byte[24]);

        // ---- 8. .dynamic（占位，vaddr 算出后填）----
        // NEEDED×N（每个唯一 soname 一条）+ PLTRELSZ/PLTGOT/STRTAB/SYMTAB/STRSZ/SYMENT/PLTREL/JMPREL/NULL
        int dynCount = sonames.Count + 9;
        var dynamic = new List<byte>();
        dynamic.AddRange(new byte[dynCount * 16]);

        // ============ 段布局（file_offset = vaddr - BaseAddr）============
        int ehdrSize = 64;
        int phnum = 6; // LOAD×3, INTERP, DYNAMIC, GNU_STACK
        int phdrsSize = phnum * 56;
        int phoff = ehdrSize;

        long textVaddr = BaseAddr + ehdrSize + phdrsSize; // .text 紧跟 phdrs
        long pltVaddr = textVaddr + pltOff;
        long entryVaddr = textVaddr + entryOff;
        long seg1End = textVaddr + text.Count; // vaddr

        long rodataVaddr = Align(seg1End, Page);
        long rodataOff = rodataVaddr - BaseAddr;
        long rdataBaseVaddr = rodataVaddr + interpBytes.Length; // img.RData 符号基准
        long seg2End = rodataVaddr + rodata.Count;

        long dataVaddr = Align(seg2End, Page);
        long dataOff = dataVaddr - BaseAddr;
        long bssVaddr = dataVaddr + data.Count; // bss 紧跟 data（同段，memsz）
        long gotPltVaddr = dataVaddr + data.Count;
        long dynamicVaddr = gotPltVaddr + gotPlt.Count;
        long dynsymVaddr = dynamicVaddr + dynamic.Count;
        long dynstrVaddr = dynsymVaddr + dynsym.Count;
        long relaPltVaddr = dynstrVaddr + dynstr.Count;
        long seg3FileEnd = relaPltVaddr + relaPlt.Count;
        long seg3MemEnd = seg3FileEnd + bssSize;

        // ============ 填充 .got.plt ============
        Write64At(gotPlt, 0, dynamicVaddr);       // [0] = _DYNAMIC
        // [1],[2] = 0（ld.so 填 link_map / resolver）
        for (int i = 0; i < nExt; i++)
        {
            int slot = (3 + i) * 8;
            long pushAddr = pltVaddr + 16 + i * 16 + 6; // PLT[i] 的 push 指令地址
            Write64At(gotPlt, slot, pushAddr);
        }

        // ============ 填充 .rela.plt ============
        for (int i = 0; i < nExt; i++)
        {
            long rOffset = gotPltVaddr + (3 + i) * 8;
            long rInfo = ((long)(i + 1) << 32) | R_X86_64_JUMP_SLOT; // sym 索引 = i+1
            Write64At(relaPlt, i * 24 + 0, rOffset);
            Write64At(relaPlt, i * 24 + 8, rInfo);
            Write64At(relaPlt, i * 24 + 16, 0);
        }

        // ============ 填充 .dynamic ============
        // 每个 DT_NEEDED 指向 .dynstr 中的 soname；其余为 PLT/GOT/SYMTAB 元数据。
        var dt = new List<(int tag, long val)>();
        foreach (var lib in sonames)
            dt.Add((DT_NEEDED, sonameOff[lib]));
        dt.Add((DT_PLTRELSZ, relaPlt.Count));
        dt.Add((DT_PLTGOT, gotPltVaddr));
        dt.Add((DT_STRTAB, dynstrVaddr));
        dt.Add((DT_SYMTAB, dynsymVaddr));
        dt.Add((DT_STRSZ, dynstr.Count));
        dt.Add((DT_SYMENT, 24));
        dt.Add((DT_PLTREL, DT_RELA_KIND));
        dt.Add((DT_JMPREL, relaPltVaddr));
        dt.Add((DT_NULL, 0));
        for (int i = 0; i < dt.Count; i++)
        {
            Write64At(dynamic, i * 16 + 0, dt[i].tag);
            Write64At(dynamic, i * 16 + 8, dt[i].val);
        }

        // ============ 填充 PLT ============
        // PLT[0]: push [.got.plt+8]; jmp [.got.plt+16]; nop×4
        WriteBytesAt(text, pltOff + 0, new byte[] { 0xFF, 0x35 });
        Write32At(text, pltOff + 2, (int)(gotPltVaddr + 8 - (pltVaddr + 6)));
        WriteBytesAt(text, pltOff + 6, new byte[] { 0xFF, 0x25 });
        Write32At(text, pltOff + 8, (int)(gotPltVaddr + 16 - (pltVaddr + 12)));
        WriteBytesAt(text, pltOff + 12, new byte[] { 0x90, 0x90, 0x90, 0x90 });
        // PLT[i] (i=0..nExt-1): jmp [.got.plt+(3+i)*8]; push i; jmp PLT[0]
        for (int i = 0; i < nExt; i++)
        {
            int p = pltOff + 16 + i * 16;
            long pv = pltVaddr + 16 + i * 16;
            WriteBytesAt(text, p + 0, new byte[] { 0xFF, 0x25 });
            Write32At(text, p + 2, (int)(gotPltVaddr + (3 + i) * 8 - (pv + 6)));
            WriteBytesAt(text, p + 6, new byte[] { 0x68 });
            Write32At(text, p + 7, i);
            WriteBytesAt(text, p + 11, new byte[] { 0xE9 });
            Write32At(text, p + 12, (int)(pltVaddr - (pv + 16)));
        }

        // ============ 解析 fixup ============
        foreach (var (sec, off, kind, sym) in fixups)
        {
            var list = sec == img.Data ? data : sec == img.RData ? rodata : text;
            // 该 fixup 所在段的 vaddr 基准（注意 .rdata 符号基准含 interp 前缀）
            long secBase;
            if (sec == img.Text) secBase = textVaddr;
            else if (sec == img.RData) secBase = rdataBaseVaddr;
            else if (sec == img.Data) secBase = dataVaddr;
            else secBase = bssVaddr;
            long fixVaddr = secBase + off;

            if (kind == FixupKind.Rel32)
            {
                long target = SymVaddr(sym, img, textVaddr, rdataBaseVaddr, dataVaddr, bssVaddr, pltVaddr, externals);
                Write32At(list, off, (int)(target - (fixVaddr + 4)));
            }
            else if (kind == FixupKind.ExtSlot32)
            {
                int idx = externals.IndexOf(sym);
                long target = pltVaddr + 16 + idx * 16; // PLT[i]
                Write32At(list, off, (int)(target - (fixVaddr + 4)));
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
        long seg1End = BaseAddr + 64 + phnum * 56 + text.Count;
        long seg1Vaddr = BaseAddr;
        long seg1Off = 0;
        long seg1FileSz = 64 + phnum * 56 + text.Count;

        var f = new List<byte>();

        // ---- ELF header (64) ----
        byte[] ident = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        f.AddRange(ident);
        Write16At(f, ET_EXEC);
        Write16At(f, EM_X86_64);
        Write32At(f, 1);                 // e_version
        Write64At(f, entryVaddr);        // e_entry
        Write64At(f, phoff);             // e_phoff
        Write64At(f, 0);                 // e_shoff（不写节头表）
        Write32At(f, 0);                 // e_flags
        Write16At(f, 64);                // e_ehsize
        Write16At(f, 56);                // e_phentsize
        Write16At(f, (ushort)phnum);     // e_phnum
        Write16At(f, 64);                // e_shentsize
        Write16At(f, 0);                 // e_shnum
        Write16At(f, 0);                 // e_shstrndx

        // ---- Program headers ----
        // PT_LOAD 1 (R+X): ehdr + phdrs + .text + .plt
        WritePhdr(f, PT_LOAD, PF_R | PF_X, seg1Off, seg1Vaddr, seg1FileSz, seg1FileSz, Page);
        // PT_INTERP
        WritePhdr(f, PT_INTERP, PF_R, rodataOff + interpOff, rodataVaddr + interpOff, interpSize, interpSize, 1);
        // PT_LOAD 2 (R): .rodata（含 interp）
        WritePhdr(f, PT_LOAD, PF_R, rodataOff, rodataVaddr, rodata.Count, rodata.Count, Page);
        // PT_LOAD 3 (R+W): .data + .got.plt + .dynamic + .dynsym + .dynstr + .rela.plt + .bss
        WritePhdr(f, PT_LOAD, PF_R | PF_W, dataOff, dataVaddr, seg3FileSz, seg3MemSz, Page);
        // PT_DYNAMIC
        WritePhdr(f, PT_DYNAMIC, PF_R | PF_W, dataOff + (data.Count + gotPlt.Count), dataVaddr + data.Count + gotPlt.Count, dynamic.Count, dynamic.Count, 8);
        // PT_GNU_STACK (RW, no exec)
        WritePhdr(f, PT_GNU_STACK, PF_R | PF_W, 0, 0, 0, 0, Page);

        // ---- 段数据 ----
        // .text 紧跟 phdrs（当前 f 已含 ehdr+phdrs，直接追加 text）
        f.AddRange(text);

        // .rodata（填充到 rodataOff）
        while (f.Count < rodataOff) f.Add(0);
        f.AddRange(rodata);

        // .data（填充到 dataOff）
        while (f.Count < dataOff) f.Add(0);
        f.AddRange(data);
        f.AddRange(gotPlt);
        f.AddRange(dynamic);
        f.AddRange(dynsym);
        f.AddRange(dynstr);
        f.AddRange(relaPlt);
        // .bss 无文件内容

        return f.ToArray();
    }

    private static void WritePhdr(List<byte> f, int type, int flags, long off, long vaddr, long filesz, long memsz, long align)
    {
        Write32At(f, type);
        Write32At(f, flags);
        int o1 = f.Count; Write64At(f, o1, off);
        int o2 = f.Count; Write64At(f, o2, vaddr);
        int o3 = f.Count; Write64At(f, o3, vaddr);   // p_paddr = p_vaddr
        int o4 = f.Count; Write64At(f, o4, filesz);
        int o5 = f.Count; Write64At(f, o5, memsz);
        int o6 = f.Count; Write64At(f, o6, align);
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

    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void WriteBytesAt(List<byte> b, int off, byte[] bytes) { Ensure(b, off + bytes.Length); for (int i = 0; i < bytes.Length; i++) b[off + i] = bytes[i]; }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
