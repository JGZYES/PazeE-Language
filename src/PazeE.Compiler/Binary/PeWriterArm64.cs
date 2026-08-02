using System.Text;
using PazeE.Compiler.CodeGen.Arm64;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;

namespace PazeE.Compiler.Binary;

/// <summary>Windows PE32+ (ARM64) 可执行文件写入器。
/// 独立于 x64 PeWriter，不共享任何逻辑。自带 ARM64 入口桩（bl main → bl ExitProcess），
/// 从 msvcrt.dll/kernel32.dll 导入外部符号，通过 .text 中的导入 thunk
/// （ADRP X16,page(IAT); LDR X17,[X16,#lo12(IAT)]; BR X17）实现外部调用。
/// 符号地址引用经 ADRP+ADD 物化（AdrpAdd fixup），.data 指针初始化用 Abs64 绝对地址。</summary>
public sealed class PeWriterArm64 : IExecutableWriter
{
    public Platform Platform => Platform.Windows;

    private const long ImageBase = 0x140000000;
    private const int SectionAlign = 0x1000;
    private const int FileAlign = 0x200;
    private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;
    // 每个 ARM64 导入 thunk：ADRP X16 + LDR X17 + BR X17 = 12 字节
    private const int ThunkSize = 12;

    public byte[] Write(ObjectImage img)
    {
        var text = new List<byte>(img.Text.Data);
        var rdata = new List<byte>(img.RData.Data);
        var data = new List<byte>(img.Data.Data);
        int bssSize = img.Bss.BssSize;

        // ---- 1. 追加 ARM64 入口桩 ----
        var (stubCode, stubFx) = StartupStubs.Arm64Windows(img.HasArgcArgv);
        int stubOff = text.Count;
        text.AddRange(stubCode);
        int entryRva = stubOff;

        var fixups = new List<(ImageSection sec, int off, FixupKind kind, string sym)>(img.Fixups.Select(f => (f.Section, f.Offset, f.Kind, f.Symbol)));
        foreach (var f in stubFx)
            fixups.Add((img.Text, stubOff + f.Offset, f.Kind, f.Symbol));

        var externals = new List<string>(img.Externals);
        if (!externals.Contains("ExitProcess")) externals.Add("ExitProcess");
        bool isGui = externals.Any(e => LibcDecls.DllOf(e) is "user32.dll" or "gdi32.dll" or "dwmapi.dll");

        // ---- 2. 每个外部符号的导入 thunk（ADRP X16; LDR X17,[X16,#lo12]; BR X17）----
        var thunkOff = new Dictionary<string, int>();
        foreach (var ext in externals)
        {
            thunkOff[ext] = text.Count;
            text.AddRange(new byte[ThunkSize]); // 占位
        }

        // ---- 3. 在 .rdata 构造导入表（与 x64 PeWriter 相同的结构）----
        var groups = externals.GroupBy(LibcDecls.DllOf).Select(g => (dll: g.Key, funcs: g.ToList())).ToList();

        int iatBase = rdata.Count;
        var iatOff = new Dictionary<string, int>();
        var iltOffsets = new List<(string dll, int off, int count)>();
        var iatOffsets = new List<(string dll, int off, int count)>();

        foreach (var g in groups)
        {
            iltOffsets.Add((g.dll, rdata.Count, g.funcs.Count));
            foreach (var fn in g.funcs) rdata.AddRange(new byte[8]);
            rdata.AddRange(new byte[8]);
        }
        foreach (var g in groups)
        {
            iatOffsets.Add((g.dll, rdata.Count, g.funcs.Count));
            for (int i = 0; i < g.funcs.Count; i++)
            {
                iatOff[g.funcs[i]] = rdata.Count;
                rdata.AddRange(new byte[8]);
            }
            rdata.AddRange(new byte[8]);
        }

        var hnOff = new Dictionary<string, int>();
        foreach (var g in groups)
            foreach (var fn in g.funcs)
            {
                hnOff[fn] = rdata.Count;
                rdata.AddRange(new byte[] { 0, 0 });
                var nb = Encoding.ASCII.GetBytes(fn);
                rdata.AddRange(nb);
                rdata.Add(0);
                if ((nb.Length + 3) % 2 != 0) rdata.Add(0);
            }

        var dllNameOff = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            dllNameOff[g.dll] = rdata.Count;
            rdata.AddRange(Encoding.ASCII.GetBytes(g.dll));
            rdata.Add(0);
            if (g.dll.Length % 2 != 0) rdata.Add(0);
        }

        int descOff = rdata.Count;
        foreach (var g in groups) rdata.AddRange(new byte[20]);
        rdata.AddRange(new byte[20]);
        int descSize = (groups.Count + 1) * 20;

        // ---- 4. 计算段 RVA ----
        int textRva = SectionAlign;
        int rdataRva = Align(textRva + text.Count, SectionAlign);
        int dataRva = Align(rdataRva + rdata.Count, SectionAlign);
        int bssRva = Align(dataRva + data.Count, SectionAlign);
        int sizeOfImage = Align(bssRva + bssSize, SectionAlign);

        // 填充 IAT/ILT
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
            Write32At(rdata, d + 0, rdataRva + ilt.off);
            Write32At(rdata, d + 4, 0);
            Write32At(rdata, d + 8, 0);
            Write32At(rdata, d + 12, rdataRva + dllNameOff[g.dll]);
            Write32At(rdata, d + 16, rdataRva + iat.off);
        }

        // ---- 5. 填充 thunk（ADRP X16,page(IAT); LDR X17,[X16,#lo12]; BR X17）----
        foreach (var ext in externals)
        {
            int tOff = thunkOff[ext];
            long thunkVa = ImageBase + textRva + tOff;
            long iatSlotVa = ImageBase + rdataRva + iatOff[ext];
            long pcPage = thunkVa & ~0xFFFL;
            long tgtPage = iatSlotVa & ~0xFFFL;
            long pageDelta = (tgtPage - pcPage) >> 12;
            int immlo = (int)(pageDelta & 3);
            int immhi = (int)((pageDelta >> 2) & 0x7FFFF);
            int lo12 = (int)(iatSlotVa & 0xFFF);
            Write32At(text, tOff + 0, Arm64Emitter.EncodeAdrp(immlo, immhi, 16));   // ADRP X16
            Write32At(text, tOff + 4, Arm64Emitter.EncodeLdrImm(17, 16, lo12 / 8));  // LDR X17,[X16,#lo12]
            Write32At(text, tOff + 8, Arm64Emitter.EncodeBr(17));                    // BR X17
        }

        // ---- 6. 解析所有 fixup ----
        foreach (var (sec, off, kind, sym) in fixups)
        {
            var list = sec == img.Data ? data : sec == img.RData ? rdata : text;
            int secRva = sec == img.Text ? textRva : sec == img.RData ? rdataRva : sec == img.Data ? dataRva : bssRva;
            long fixVa = ImageBase + secRva + off;

            if (kind == FixupKind.Call26)
            {
                int targetRva = TargetRva(sym, img, textRva, rdataRva, dataRva, bssRva, thunkOff);
                long targetVa = ImageBase + targetRva;
                int imm26 = (int)((targetVa - fixVa) >> 2);
                Write32At(list, off, Arm64Emitter.EncodeBl(imm26));
            }
            else if (kind == FixupKind.ExtCall26)
            {
                int thunkRva = textRva + thunkOff[sym];
                long targetVa = ImageBase + thunkRva;
                int imm26 = (int)((targetVa - fixVa) >> 2);
                Write32At(list, off, Arm64Emitter.EncodeBl(imm26));
            }
            else if (kind == FixupKind.AdrpAdd)
            {
                int targetRva = TargetRva(sym, img, textRva, rdataRva, dataRva, bssRva, thunkOff);
                long targetVa = ImageBase + targetRva;
                long pcPage = fixVa & ~0xFFFL;
                long tgtPage = targetVa & ~0xFFFL;
                long pageDelta = (tgtPage - pcPage) >> 12;
                int immlo = (int)(pageDelta & 3);
                int immhi = (int)((pageDelta >> 2) & 0x7FFFF);
                int lo12 = (int)(targetVa & 0xFFF);
                int rd = Read32(list, off) & 0x1F;
                Write32At(list, off, Arm64Emitter.EncodeAdrp(immlo, immhi, rd));
                Write32At(list, off + 4, Arm64Emitter.EncodeAddImm(rd, rd, lo12, true));
            }
            else if (kind == FixupKind.Abs64)
            {
                int targetRva = TargetRva(sym, img, textRva, rdataRva, dataRva, bssRva, thunkOff);
                long va = ImageBase + targetRva;
                Write64At(list, off, va);
            }
        }

        // ---- 7. 装配 PE 文件 ----
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
        var secs = new List<(string name, List<byte> data, int vsize, int rva, int flags, bool isBss)>();
        secs.Add((".text", text, text.Count, textRva, unchecked((int)0x60000020), false));
        secs.Add((".rdata", rdata, rdata.Count, rdataRva, unchecked((int)0x40000040), false));
        if (data.Count > 0) secs.Add((".data", data, data.Count, dataRva, unchecked((int)0xC0000040), false));
        if (bssSize > 0) secs.Add((".bss", new List<byte>(), bssSize, bssRva, unchecked((int)0xC0000080), true));

        int secCount = secs.Count;
        int optHdrSize = 240;
        int headersSize = 4 + 20 + optHdrSize + secCount * 40;
        int sizeOfHeaders = Align(headersSize + 0x40, FileAlign);

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

        // ---- DOS 头 ----
        f.AddRange(new byte[0x40]);
        f[0] = (byte)'M'; f[1] = (byte)'Z';
        Write32At(f, 0x3C, 0x40);

        // ---- PE 签名 ----
        f.AddRange(new byte[] { (byte)'P', (byte)'E', 0, 0 });

        // ---- COFF 头 ----
        Write16At(f, IMAGE_FILE_MACHINE_ARM64);
        Write16At(f, (ushort)secCount);
        Write32At(f, 0);
        Write32At(f, 0);
        Write32At(f, 0);
        Write16At(f, (ushort)optHdrSize);
        Write16At(f, 0x0022);

        // ---- Optional Header PE32+ ----
        Write16At(f, 0x020B);
        f.Add(14); f.Add(0);
        Write32At(f, sizeOfCode);
        Write32At(f, sizeOfInitData);
        Write32At(f, sizeOfUninit);
        Write32At(f, entryRva);
        Write32At(f, textRva);
        Write64At(f, ImageBase);
        Write32At(f, SectionAlign);
        Write32At(f, FileAlign);
        Write16At(f, 6); Write16At(f, 0);
        Write16At(f, 0); Write16At(f, 0);
        Write16At(f, 6); Write16At(f, 0);
        Write32At(f, 0);
        Write32At(f, sizeOfImage);
        Write32At(f, sizeOfHeaders);
        Write32At(f, 0);
        Write16At(f, (ushort)(isGui ? 2 : 3)); // Subsystem = GUI(2) or CONSOLE(3)
        Write16At(f, 0);
        Write64At(f, 0x100000);
        Write64At(f, 0x1000);
        Write64At(f, 0x100000);
        Write64At(f, 0x1000);
        Write32At(f, 0);
        Write32At(f, 16);
        for (int i = 0; i < 16; i++)
        {
            if (i == 1) { Write32At(f, importRva); Write32At(f, importSize); }
            else { Write32At(f, 0); Write32At(f, 0); }
        }

        // ---- 段头 ----
        for (int si = 0; si < secCount; si++)
        {
            var s = secs[si];
            var nb = new byte[8];
            var nameBytes = Encoding.ASCII.GetBytes(s.name);
            Array.Copy(nameBytes, nb, Math.Min(nameBytes.Length, 8));
            f.AddRange(nb);
            Write32At(f, s.vsize);
            Write32At(f, s.rva);
            Write32At(f, s.isBss ? 0 : Align(s.data.Count, FileAlign));
            Write32At(f, s.isBss ? 0 : rawOffsets[si]);
            Write32At(f, 0); Write32At(f, 0);
            Write16At(f, 0); Write16At(f, 0);
            Write32At(f, s.flags);
        }

        while (f.Count < sizeOfHeaders) f.Add(0);

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

    private static int Read32(List<byte> b, int off) => b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);

    private static void Write16At(List<byte> b, int off, ushort v) { Ensure(b, off + 2); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void Write16At(List<byte> b, ushort v) { int o = b.Count; b.AddRange(new byte[2]); Write16At(b, o, v); }
    private static void Write32At(List<byte> b, int off, int v) { Ensure(b, off + 4); b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
    private static void Write32At(List<byte> b, int v) { int o = b.Count; b.AddRange(new byte[4]); Write32At(b, o, v); }
    private static void Write64At(List<byte> b, int off, long v) { Ensure(b, off + 8); for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (i * 8)); }
    private static void Write64At(List<byte> b, long v) { int o = b.Count; b.AddRange(new byte[8]); Write64At(b, o, v); }
    private static void Ensure(List<byte> b, int len) { while (b.Count < len) b.Add(0); }
}
