using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;

namespace PazeE.Compiler.Binary;

/// <summary>LeonOS 4 ELF64 (x86-64) 静态可执行文件写入器。
/// 无动态链接（无 PLT/GOT/.dynsym/.dynamic/PT_INTERP），运行时函数由 Los4Runtime 静态注入 .text。
/// 系统调用通过 int 0x80（与 Linux x86_64 同调用号），ELF 布局与 doomlauncher.elf 一致：
/// 3 个 PT_LOAD（.text RX / .rodata R / .data+.bss RW）+ PT_GNU_STACK，基址 0x400000。</summary>
public sealed class ElfWriterLos4 : IExecutableWriter
{
    public Platform Platform => Platform.Los4;

    private const long BaseAddr = 0x400000L;
    private const int Page = 0x1000;

    private const ushort ET_EXEC = 2;
    private const ushort EM_X86_64 = 62;
    private const int PT_LOAD = 1, PT_GNU_STACK = 0x6474e551;
    private const int PF_X = 1, PF_W = 2, PF_R = 4;

    public byte[] Write(ObjectImage img)
    {
        var text = new List<byte>(img.Text.Data);
        var rodata = new List<byte>(img.RData.Data);
        var data = new List<byte>(img.Data.Data);

        // ---- 2. 静态运行时（先于布局，以便获取 BSS 大小）----
        var (rtCode, rtOffsets, rdataRefs, bssRefs, rtBssSize) = Los4Runtime.Generate();

        // BSS = 用户 BSS + 运行时 BSS（gui_fd/gui_win_id 等持久状态）
        int userBssSize = img.Bss.BssSize;
        int bssSize = userBssSize + rtBssSize;

        // ---- 1. _start 入口桩 ----
        var (stubCode, stubFx) = StartupStubs.Los4(img.HasArgcArgv);
        int stubOff = text.Count;
        text.AddRange(stubCode);
        long entryOff = stubOff; // 入口 = _start 桩

        // ---- 2b. 运行时代码追加到 .text ----
        int rtBase = text.Count; // 运行时在 .text 中的偏移
        text.AddRange(rtCode);

        // ---- 2b. 运行时引用的 .rodata 字符串 → 追加到 rodata，记录偏移 ----
        // 运行时函数（如 time_utc_raw）需要访问字符串常量（设备路径等），
        // 这些字符串不在用户代码的 RData 中，需单独注入并回填绝对地址。
        var rdataStrOff = new Dictionary<string, long>(); // content → rodata 内偏移
        foreach (var (off, content) in rdataRefs)
        {
            if (!rdataStrOff.ContainsKey(content))
            {
                rdataStrOff[content] = rodata.Count;
                foreach (char c in content) rodata.Add((byte)c);
                rodata.Add(0); // null terminator
            }
        }

        // ---- 3. 收集所有 fixup ----
        var fixups = new List<(ImageSection sec, int off, FixupKind kind, string sym)>();
        foreach (var f in img.Fixups) fixups.Add((f.Section, f.Offset, f.Kind, f.Symbol));
        foreach (var f in stubFx) fixups.Add((img.Text, stubOff + f.Offset, f.Kind, f.Symbol));

        // ============ 段布局（file_offset = vaddr - BaseAddr + firstSegOff）============
        // doomlauncher.elf 布局: .text@0x1000/v0x400000, .rodata@page, .data@page
        int ehdrSize = 64;
        int phnum = 4; // LOAD×3 + GNU_STACK
        int phdrsSize = phnum * 56;
        int firstSegOff = Page; // .text 从第一个页开始（0x1000）

        long textVaddr = BaseAddr;
        long entryVaddr = textVaddr + entryOff;
        long seg1End = textVaddr + text.Count;

        long rodataVaddr = Align(seg1End, Page);
        long rodataOff = rodataVaddr - BaseAddr + firstSegOff;
        long seg2End = rodataVaddr + rodata.Count;

        long dataVaddr = Align(seg2End, Page);
        long dataOff = dataVaddr - BaseAddr + firstSegOff;
        long bssVaddr = dataVaddr + data.Count;
        long seg3FileEnd = dataVaddr + data.Count;
        long seg3MemEnd = seg3FileEnd + bssSize;

        // ============ 解析 fixup ============
        foreach (var (sec, off, kind, sym) in fixups)
        {
            var list = sec == img.Data ? data : sec == img.RData ? rodata : text;
            long secBase = sec == img.Text ? textVaddr : sec == img.RData ? rodataVaddr : sec == img.Data ? dataVaddr : bssVaddr;
            long fixVaddr = secBase + off;

            if (kind == FixupKind.Rel32)
            {
                long target = SymVaddr(sym, img, textVaddr, rodataVaddr, dataVaddr, bssVaddr, rtBase, rtOffsets);
                Write32At(list, off, (int)(target - (fixVaddr + 4)));
            }
            else if (kind == FixupKind.ExtSlot32)
            {
                // 外部函数 → 解析到运行时函数（无 PLT）
                long target = textVaddr + rtBase + rtOffsets[sym];
                Write32At(list, off, (int)(target - (fixVaddr + 4)));
            }
            else if (kind == FixupKind.Abs64)
            {
                long target = SymVaddr(sym, img, textVaddr, rodataVaddr, dataVaddr, bssVaddr, rtBase, rtOffsets);
                Write64At(list, off, target);
            }
        }

        // ---- 运行时 RData 字符串引用回填 ----
        // 运行时代码中 mov r64, &str 的 8 字节占位 → 填入字符串在 .rodata 的虚拟地址
        foreach (var (off, content) in rdataRefs)
        {
            long strVaddr = rodataVaddr + rdataStrOff[content];
            Write64At(text, rtBase + off, strVaddr);
        }

        // ---- 运行时 BSS 全局引用回填 ----
        // 运行时 BSS 紧跟用户 BSS：全局在 [bssVaddr + userBssSize + bssOff] 处。
        foreach (var (off, name, bssOff) in bssRefs)
        {
            long globalVaddr = bssVaddr + userBssSize + bssOff;
            Write64At(text, rtBase + off, globalVaddr);
        }

        // ============ 装配 ELF ============
        return Assemble(text, rodata, data, bssSize, entryVaddr, phnum,
            textVaddr, firstSegOff, rodataVaddr, rodataOff, dataVaddr, dataOff,
            seg3MemEnd - dataVaddr);
    }

    private static long SymVaddr(string sym, ObjectImage img,
        long textVaddr, long rodataVaddr, long dataVaddr, long bssVaddr,
        int rtBase, Dictionary<string, int> rtOffsets)
    {
        if (img.Symbols.TryGetValue(sym, out var ds))
        {
            long baseVaddr = ds.Section == img.Text ? textVaddr
                : ds.Section == img.RData ? rodataVaddr
                : ds.Section == img.Data ? dataVaddr : bssVaddr;
            return baseVaddr + ds.Offset;
        }
        if (rtOffsets.TryGetValue(sym, out int rtOff))
            return textVaddr + rtBase + rtOff;
        return textVaddr; // fallback
    }

    private static byte[] Assemble(List<byte> text, List<byte> rodata, List<byte> data,
        int bssSize, long entryVaddr, int phnum,
        long textVaddr, long textOff, long rodataVaddr, long rodataOff,
        long dataVaddr, long dataOff, long seg3MemSz)
    {
        var f = new List<byte>();

        // ---- ELF header (64) ----
        byte[] ident = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        f.AddRange(ident);
        Write16At(f, ET_EXEC);
        Write16At(f, EM_X86_64);
        Write32At(f, 1);                  // e_version
        Write64At(f, entryVaddr);         // e_entry
        Write64At(f, 64);                 // e_phoff
        Write64At(f, 0);                  // e_shoff（无节头表）
        Write32At(f, 0);                  // e_flags
        Write16At(f, 64);                 // e_ehsize
        Write16At(f, 56);                 // e_phentsize
        Write16At(f, (ushort)phnum);      // e_phnum
        Write16At(f, 64);                 // e_shentsize
        Write16At(f, 0);                  // e_shnum
        Write16At(f, 0);                  // e_shstrndx

        // ---- Program headers ----
        // PH[0]: PT_LOAD RX — .text
        WritePhdr(f, PT_LOAD, PF_R | PF_X, textOff, textVaddr, text.Count, text.Count, Page);
        // PH[1]: PT_LOAD R — .rodata
        WritePhdr(f, PT_LOAD, PF_R, rodataOff, rodataVaddr, rodata.Count, rodata.Count, Page);
        // PH[2]: PT_LOAD RW — .data + .bss
        WritePhdr(f, PT_LOAD, PF_R | PF_W, dataOff, dataVaddr, data.Count, seg3MemSz, Page);
        // PH[3]: PT_GNU_STACK RW (non-exec stack)
        WritePhdr(f, PT_GNU_STACK, PF_R | PF_W, 0, 0, 0, 0, Page);

        // ---- 段数据 ----
        // 填充到 .text 偏移
        while (f.Count < textOff) f.Add(0);
        f.AddRange(text);

        // .rodata
        while (f.Count < rodataOff) f.Add(0);
        f.AddRange(rodata);

        // .data
        while (f.Count < dataOff) f.Add(0);
        f.AddRange(data);
        // .bss 无文件内容

        return f.ToArray();
    }

    private static void WritePhdr(List<byte> f, int type, int flags, long off, long vaddr, long filesz, long memsz, long align)
    {
        Write32At(f, type);
        Write32At(f, flags);
        Write64At(f, off);
        Write64At(f, vaddr);
        Write64At(f, vaddr);   // p_paddr = p_vaddr
        Write64At(f, filesz);
        Write64At(f, memsz);
        Write64At(f, align);
    }

    private static long Align(long v, int a) => a <= 1 ? v : (v + a - 1) & ~(long)(a - 1);

    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
