using System.Text;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;

namespace PazeE.Compiler.Binary;

/// <summary>Windows PE32+ (x64) 可执行文件写入器。
/// 自带入口桩（call main → ExitProcess），从 msvcrt.dll/kernel32.dll 导入外部符号，
/// 通过 .text 中的跳转 thunk（jmp [rip+IAT]）实现外部调用。</summary>
public sealed class PeWriter : IExecutableWriter
{
    public Platform Platform => Platform.Windows;

    private const long ImageBase = 0x140000000;
    private const int SectionAlign = 0x1000;
    private const int FileAlign = 0x200;

    public byte[] Write(ObjectImage img)
    {
        var text = new List<byte>(img.Text.Data);
        var rdata = new List<byte>(img.RData.Data);
        var data = new List<byte>(img.Data.Data);
        int bssSize = img.Bss.BssSize;

        // ---- 1. 追加入口桩 ----
        var (stubCode, stubFx) = StartupStubs.Windows(img.HasArgcArgv);
        int stubOff = text.Count;
        text.AddRange(stubCode);
        int entryRva = stubOff; // 相对 .text（后面加 textRVA）

        // 桩的 fixup 翻译为 image fixup（基于 .text 绝对偏移）
        var fixups = new List<(ImageSection sec, int off, FixupKind kind, string sym)>(img.Fixups.Select(f => (f.Section, f.Offset, f.Kind, f.Symbol)));
        foreach (var f in stubFx)
            fixups.Add((img.Text, stubOff + f.Offset, f.Kind, f.Symbol));

        // 确保 ExitProcess 在外部符号表
        var externals = new List<string>(img.Externals);
        if (!externals.Contains("ExitProcess")) externals.Add("ExitProcess");
        // 检测 GUI 程序：若导入了 user32/gdi32/dwmapi 函数，则使用 GUI 子系统（无控制台窗口）
        bool isGui = externals.Any(e => LibcDecls.DllOf(e) is "user32.dll" or "gdi32.dll" or "dwmapi.dll");

        // ---- 2. 追加每个外部符号的跳转 thunk：FF 25 disp32（jmp [rip+IAT]）----
        var thunkOff = new Dictionary<string, int>();
        foreach (var ext in externals)
        {
            thunkOff[ext] = text.Count;
            text.Add(0xFF); text.Add(0x25);
            text.AddRange(new byte[4]); // disp32 占位
        }

        // ---- 3. 在 .rdata 构造导入表 ----
        // 分组（保持外部顺序）
        var groups = externals.GroupBy(LibcDecls.DllOf).Select(g => (dll: g.Key, funcs: g.ToList())).ToList();

        // 先记录各函数 hint/name、ILT、IAT、描述符的偏移
        int iatBase = rdata.Count;
        var iatOff = new Dictionary<string, int>();      // 每个函数 IAT 槽相对 .rdata 偏移
        var iltOffsets = new List<(string dll, int off, int count)>();
        var iatOffsets = new List<(string dll, int off, int count)>();

        // ILT + IAT（每组：count 个 8 字节项 + 1 个 0 终止项）
        foreach (var g in groups)
        {
            iltOffsets.Add((g.dll, rdata.Count, g.funcs.Count));
            foreach (var fn in g.funcs) rdata.AddRange(new byte[8]); // 占位
            rdata.AddRange(new byte[8]); // 终止
        }
        foreach (var g in groups)
        {
            iatOffsets.Add((g.dll, rdata.Count, g.funcs.Count));
            for (int i = 0; i < g.funcs.Count; i++)
            {
                iatOff[g.funcs[i]] = rdata.Count;
                rdata.AddRange(new byte[8]); // 占位
            }
            rdata.AddRange(new byte[8]); // 终止
        }

        // hint/name 表
        var hnOff = new Dictionary<string, int>();
        foreach (var g in groups)
            foreach (var fn in g.funcs)
            {
                hnOff[fn] = rdata.Count;
                rdata.AddRange(new byte[] { 0, 0 }); // hint=0
                var nb = Encoding.ASCII.GetBytes(fn);
                rdata.AddRange(nb);
                rdata.Add(0);
                if ((nb.Length + 3) % 2 != 0) rdata.Add(0); // 偶对齐
            }

        // DLL 名
        var dllNameOff = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            dllNameOff[g.dll] = rdata.Count;
            rdata.AddRange(Encoding.ASCII.GetBytes(g.dll));
            rdata.Add(0);
            if (g.dll.Length % 2 != 0) rdata.Add(0);
        }

        // 导入描述符表
        int descOff = rdata.Count;
        foreach (var g in groups)
        {
            rdata.AddRange(new byte[20]); // 占位
        }
        rdata.AddRange(new byte[20]); // 全零终止
        int descSize = (groups.Count + 1) * 20;

        // ---- 4. 计算段 RVA / 原始偏移 ----
        int textRva = SectionAlign;
        int rdataRva = Align(textRva + text.Count, SectionAlign);
        int dataRva = Align(rdataRva + rdata.Count, SectionAlign);
        int bssRva = Align(dataRva + data.Count, SectionAlign);
        int sizeOfImage = Align(bssRva + bssSize, SectionAlign);

        // 填充 IAT/ILT：每项 = hint/name 的 RVA
        foreach (var g in groups)
        {
            var ilt = iltOffsets.First(x => x.dll == g.dll);
            for (int i = 0; i < g.funcs.Count; i++)
                Write64At(rdata, ilt.off + i * 8, rdataRva + hnOff[g.funcs[i]]);
            var iat = iatOffsets.First(x => x.dll == g.dll);
            for (int i = 0; i < g.funcs.Count; i++)
                Write64At(rdata, iat.off + i * 8, rdataRva + hnOff[g.funcs[i]]);
        }

        // 填充导入描述符
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            int d = descOff + gi * 20;
            var ilt = iltOffsets.First(x => x.dll == g.dll);
            var iat = iatOffsets.First(x => x.dll == g.dll);
            Write32At(rdata, d + 0, rdataRva + ilt.off);   // OriginalFirstThunk
            Write32At(rdata, d + 4, 0);                     // TimeDateStamp
            Write32At(rdata, d + 8, 0);                     // ForwarderChain
            Write32At(rdata, d + 12, rdataRva + dllNameOff[g.dll]); // Name
            Write32At(rdata, d + 16, rdataRva + iat.off);   // FirstThunk
        }

        // ---- 5. 解析所有 fixup ----
        foreach (var (sec, off, kind, sym) in fixups)
        {
            var list = sec == img.Data ? data : sec == img.RData ? rdata : text;
            int secRva = sec == img.Text ? textRva : sec == img.RData ? rdataRva : sec == img.Data ? dataRva : bssRva;
            if (kind == FixupKind.Rel32)
            {
                int target = TargetRva(sym, img, textRva, rdataRva, dataRva, bssRva, thunkOff);
                Write32At(list, off, target - (secRva + off + 4));
            }
            else if (kind == FixupKind.ExtSlot32)
            {
                int thunkRva = textRva + thunkOff[sym];
                Write32At(list, off, thunkRva - (secRva + off + 4));
            }
            else if (kind == FixupKind.Abs64)
            {
                long va = ImageBase + TargetRva(sym, img, textRva, rdataRva, dataRva, bssRva, thunkOff);
                Write64At(list, off, va);
            }
        }

        // 填充 thunk disp32 → IAT 槽
        foreach (var ext in externals)
        {
            int tOff = thunkOff[ext];
            int iatSlotRva = rdataRva + iatOff[ext];
            int disp = iatSlotRva - (textRva + tOff + 6);
            Write32At(text, tOff + 2, disp);
        }

        // ---- 6. 装配 PE 文件 ----
        return AssemblePe(text, rdata, data, bssSize, textRva, rdataRva, dataRva, bssRva, sizeOfImage, entryRva + textRva, rdataRva + descOff, descSize, isGui);
    }

    private static int TargetRva(string sym, ObjectImage img, int textRva, int rdataRva, int dataRva, int bssRva, Dictionary<string, int> thunkOff)
    {
        if (img.Symbols.TryGetValue(sym, out var ds))
        {
            int baseRva = ds.Section == img.Text ? textRva : ds.Section == img.RData ? rdataRva : ds.Section == img.Data ? dataRva : bssRva;
            return baseRva + ds.Offset;
        }
        if (thunkOff.TryGetValue(sym, out var t)) return textRva + t;
        return textRva;
    }

    private static byte[] AssemblePe(List<byte> text, List<byte> rdata, List<byte> data, int bssSize,
        int textRva, int rdataRva, int dataRva, int bssRva, int sizeOfImage, int entryRva, int importRva, int importSize, bool isGui)
    {
        // 段表（仅非空段；.rdata 永远非空因含导入表）
        var secs = new List<(string name, List<byte> data, int vsize, int rva, int flags, bool isBss)>();
        secs.Add((".text", text, text.Count, textRva, unchecked((int)0x60000020), false));
        secs.Add((".rdata", rdata, rdata.Count, rdataRva, unchecked((int)0x40000040), false));
        if (data.Count > 0) secs.Add((".data", data, data.Count, dataRva, unchecked((int)0xC0000040), false));
        if (bssSize > 0) secs.Add((".bss", new List<byte>(), bssSize, bssRva, unchecked((int)0xC0000080), true));

        int secCount = secs.Count;
        int optHdrSize = 240;
        int headersSize = 4 /*PE sig*/ + 20 /*COFF*/ + optHdrSize + secCount * 40;
        int sizeOfHeaders = Align(headersSize + 0x40 /*DOS*/, FileAlign); // DOS 占 0x40，PE 紧随

        // 计算各段原始偏移
        int rawOff = sizeOfHeaders;
        foreach (var s in secs)
        {
            if (s.isBss) continue;
            // 占位记录（下面统一写）
        }
        var rawOffsets = new int[secCount];
        int cur = sizeOfHeaders;
        for (int i = 0; i < secCount; i++)
        {
            if (secs[i].isBss) { rawOffsets[i] = 0; continue; }
            rawOffsets[i] = cur;
            cur += Align(secs[i].data.Count, FileAlign);
        }

        int sizeOfCode = Align(text.Count, FileAlign);
        int sizeOfInitData = 0;
        foreach (var s in secs) if (!s.isBss && s.name != ".text") sizeOfInitData += Align(s.data.Count, FileAlign);
        int sizeOfUninit = bssSize > 0 ? Align(bssSize, FileAlign) : 0;

        var f = new List<byte>();

        // ---- DOS 头（64 字节） ----
        f.AddRange(new byte[0x40]);
        f[0] = (byte)'M'; f[1] = (byte)'Z';
        Write32At(f, 0x3C, 0x40); // e_lfanew → PE 签名

        // ---- PE 签名 ----
        f.AddRange(new byte[] { (byte)'P', (byte)'E', 0, 0 });

        // ---- COFF 头（20 字节） ----
        Write16At(f, 0x8664);             // Machine = AMD64
        Write16At(f, (ushort)secCount);   // NumberOfSections
        Write32At(f, 0);                  // TimeDateStamp
        Write32At(f, 0);                  // PointerToSymbolTable
        Write32At(f, 0);                  // NumberOfSymbols
        Write16At(f, (ushort)optHdrSize); // SizeOfOptionalHeader
        Write16At(f, 0x0022);             // Characteristics: EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

        // ---- Optional Header PE32+（240 字节）----
        Write16At(f, 0x020B);             // Magic = PE32+
        f.Add(14); f.Add(0);              // Linker version
        Write32At(f, sizeOfCode);         // SizeOfCode
        Write32At(f, sizeOfInitData);     // SizeOfInitializedData
        Write32At(f, sizeOfUninit);       // SizeOfUninitializedData
        Write32At(f, entryRva);           // AddressOfEntryPoint
        Write32At(f, textRva);            // BaseOfCode
        Write64At(f, ImageBase);          // ImageBase
        Write32At(f, SectionAlign);       // SectionAlignment
        Write32At(f, FileAlign);          // FileAlignment
        Write16At(f, 6); Write16At(f, 0); // OS version
        Write16At(f, 0); Write16At(f, 0); // Image version
        Write16At(f, 6); Write16At(f, 0); // Subsystem version
        Write32At(f, 0);                  // Win32VersionValue
        Write32At(f, sizeOfImage);        // SizeOfImage
        Write32At(f, sizeOfHeaders);      // SizeOfHeaders
        Write32At(f, 0);                  // CheckSum
        Write16At(f, (ushort)(isGui ? 2 : 3)); // Subsystem = GUI(2) or CONSOLE(3)
        Write16At(f, 0);                  // DllCharacteristics
        Write64At(f, 0x100000);           // SizeOfStackReserve
        Write64At(f, 0x1000);             // SizeOfStackCommit
        Write64At(f, 0x100000);           // SizeOfHeapReserve
        Write64At(f, 0x1000);             // SizeOfHeapCommit
        Write32At(f, 0);                  // LoaderFlags
        Write32At(f, 16);                 // NumberOfRvaAndSizes
        // DataDirectory[16]（每个 8 字节）
        for (int i = 0; i < 16; i++)
        {
            if (i == 1) { Write32At(f, importRva); Write32At(f, importSize); } // Import
            else { Write32At(f, 0); Write32At(f, 0); }
        }

        // ---- 段头（40 字节/段） ----
        for (int si = 0; si < secCount; si++)
        {
            var s = secs[si];
            var nb = new byte[8];
            var nameBytes = Encoding.ASCII.GetBytes(s.name);
            Array.Copy(nameBytes, nb, Math.Min(nameBytes.Length, 8));
            f.AddRange(nb);
            Write32At(f, s.vsize);                                   // VirtualSize
            Write32At(f, s.rva);                                     // VirtualAddress
            Write32At(f, s.isBss ? 0 : Align(s.data.Count, FileAlign)); // SizeOfRawData
            Write32At(f, s.isBss ? 0 : rawOffsets[si]);              // PointerToRawData
            Write32At(f, 0); Write32At(f, 0);                        // relocations, linenumbers
            Write16At(f, 0); Write16At(f, 0);                        // counts
            Write32At(f, s.flags);                                   // Characteristics
        }

        // 填充到 sizeOfHeaders
        while (f.Count < sizeOfHeaders) f.Add(0);

        // ---- 段原始数据 ----
        for (int i = 0; i < secCount; i++)
        {
            if (secs[i].isBss) continue;
            while (f.Count < rawOffsets[i]) f.Add(0);
            f.AddRange(secs[i].data);
            int pad = Align(secs[i].data.Count, FileAlign) - secs[i].data.Count;
            for (int k = 0; k < pad; k++) f.Add(0);
        }

        return f.ToArray();
    }

    private static int Align(int v, int a) => a <= 1 ? v : (v + a - 1) & ~(a - 1);

    private static void Write16At(List<byte> b, int off, ushort v) { Ensure(b, off + 2); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); Write16At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
