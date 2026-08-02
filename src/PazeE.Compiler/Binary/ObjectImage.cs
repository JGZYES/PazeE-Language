namespace PazeE.Compiler.Binary;

/// <summary>目标平台无关的可执行映像模型：段 + 定义符号 + 外部符号 + 重定位。</summary>
public sealed class ObjectImage
{
    public readonly List<ImageSection> Sections = new();
    public readonly Dictionary<string, DefinedSymbol> Symbols = new();
    /// <summary>外部符号（libc 等），按首次出现顺序。</summary>
    public readonly List<string> Externals = new();
    public readonly List<Fixup> Fixups = new();
    public string EntrySymbol = "main";
    public bool HasArgcArgv;

    public ImageSection Text { get; }
    public ImageSection Data { get; }
    public ImageSection RData { get; }
    public ImageSection Bss { get; }

    public ObjectImage()
    {
        Sections.Add(Text = new ImageSection(".text", SectionFlags.Exec));
        Sections.Add(RData = new ImageSection(".rdata", SectionFlags.None));
        Sections.Add(Data = new ImageSection(".data", SectionFlags.Write));
        Sections.Add(Bss = new ImageSection(".bss", SectionFlags.Write | SectionFlags.Bss));
    }

    public void AddExternal(string name)
    {
        if (!Externals.Contains(name)) Externals.Add(name);
    }

    public int AddString(string value)
    {
        string sym = "$str." + Symbols.Count(s => s.Key.StartsWith("$str."));
        // 字符串去重
        foreach (var kv in Symbols)
            if (kv.Key.StartsWith("$str.") && kv.Value.StringValue == value) return kv.Value.Offset;
        var bytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes(value)) { 0 };
        int off = RData.Data.Count;
        RData.Data.AddRange(bytes);
        var ds = new DefinedSymbol(sym, RData, off, true, false) { StringValue = value, Size = bytes.Count };
        Symbols[sym] = ds;
        return off;
    }

    public DefinedSymbol DefineSymbol(string name, ImageSection sec, int offset, bool global, bool isFunc)
    {
        var ds = new DefinedSymbol(name, sec, offset, global, isFunc);
        Symbols[name] = ds;
        return ds;
    }

    public void AddFixup(ImageSection sec, int offset, FixupKind kind, string symbol, int addend = 0)
        => Fixups.Add(new Fixup(sec, offset, kind, symbol, addend));
}

public enum SectionFlags { None = 0, Write = 1, Exec = 2, Bss = 4 }

public sealed class ImageSection
{
    public string Name;
    public SectionFlags Flags;
    public readonly List<byte> Data = new();
    public int BssSize;
    public ImageSection(string name, SectionFlags flags) { Name = name; Flags = flags; }
    public bool IsBss => (Flags & SectionFlags.Bss) != 0;
    public int VirtualSize => IsBss ? BssSize : Data.Count;
}

public sealed class DefinedSymbol
{
    public string Name;
    public ImageSection Section;
    public int Offset;
    public bool Global;
    public bool Function;
    public int Size;
    public string? StringValue;
    public DefinedSymbol(string name, ImageSection section, int offset, bool global, bool function)
    { Name = name; Section = section; Offset = offset; Global = global; Function = function; }
}

public enum FixupKind
{
    // ---- x86-64 后端使用 ----
    Rel32,      // 32 位 PC 相对位移（call / RIP 相对访存）
    ExtSlot32,  // 32 位 PC 相对 → PLT 桩（外部函数调用）
    Abs64,      // 64 位绝对地址（.data 指针初始化等；非 PIE）
    // ---- ARM64 (AArch64) 后端使用（独立逻辑，不与 x86 共用）----
    Call26,     // BL imm26（本地函数调用）
    ExtCall26,  // BL imm26 → PLT[i] 桩（外部函数调用）
    AdrpAdd,    // ADRP(off)+ADD(off+4) 物化符号地址到寄存器
}

public readonly record struct Fixup(ImageSection Section, int Offset, FixupKind Kind, string Symbol, int Addend);
