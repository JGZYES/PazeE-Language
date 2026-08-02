namespace PazeE.Compiler.Parser;

public enum Platform { Windows, Linux, MacOS, Los4 }

/// <summary>目标架构。amd = x86-64（默认），arm = AArch64（Windows/Linux/macOS 均支持）。</summary>
public enum Arch { Amd, Arm }

/// <summary>目标平台相关信息（整型尺寸、ABI、对齐）。</summary>
public sealed class TargetInfo
{
    public Platform Platform { get; init; }
    public Arch Arch { get; init; } = Arch.Amd;
    public Abi Abi => Platform == Platform.Windows ? Abi.Win64 : Abi.SysV;
    public int PointerSize => 8;
    public int PointerAlign => 8;
    // Windows 为 LLP64（long=4），Linux/macOS 为 LP64（long=8）
    public int LongSize => Platform == Platform.Windows ? 4 : 8;
    public int LongAlign => Platform == Platform.Windows ? 4 : 8;
    public int IntSize => 4; public int IntAlign => 4;
    public int ShortSize => 2; public int ShortAlign => 2;
    public int CharSize => 1; public int CharAlign => 1;
}

public enum Abi { Win64, SysV }

public enum IntKind { Char, Short, Int, Long }

/// <summary>C 类型系统基类。</summary>
public abstract class CType
{
    public abstract int Size { get; }
    public abstract int Align { get; }
    public virtual bool IsInteger => false;
    public virtual bool IsVoid => false;
    public virtual bool IsPointer => false;
    public virtual bool IsArray => false;
    public virtual bool IsFunction => false;
    public virtual bool IsStruct => false;
    public virtual bool IsEnum => false;
    public bool IsScalar => IsInteger || IsPointer;
    public bool IsArithmetic => IsInteger;
    public abstract string Name { get; }
    public abstract CType Clone();
    public override string ToString() => Name;
}

public sealed class VoidType : CType
{
    public override int Size => 1;
    public override int Align => 1;
    public override bool IsVoid => true;
    public override string Name => "void";
    public override CType Clone() => this;
    public static readonly VoidType Instance = new();
}

public sealed class IntegerType : CType
{
    public IntKind Kind { get; init; }
    public bool Unsigned { get; init; }
    private readonly int _size;
    private readonly int _align;
    public override int Size => _size;
    public override int Align => _align;
    public override bool IsInteger => true;
    public IntegerType(IntKind kind, bool unsigned, int size, int align) { Kind = kind; Unsigned = unsigned; _size = size; _align = align; }
    public override string Name => (Unsigned ? "unsigned " : "") + Kind.ToString().ToLower();
    public override CType Clone() => new IntegerType(Kind, Unsigned, _size, _align);
}

public sealed class PointerType : CType
{
    public CType Element { get; init; }
    private readonly int _size;
    public override int Size => _size;
    public override int Align => _size;
    public override bool IsPointer => true;
    public PointerType(CType element, int size = 8) { Element = element; _size = size; }
    public override string Name => Element.Name + "*";
    public override CType Clone() => new PointerType(Element, _size);
}

public sealed class ArrayType : CType
{
    public CType Element { get; init; }
    public long Length { get; init; }
    public override int Size => (int)(Element.Size * Length);
    public override int Align => Element.Align;
    public override bool IsArray => true;
    public ArrayType(CType element, long length) { Element = element; Length = length; }
    public override string Name => Element.Name + "[" + Length + "]";
    public override CType Clone() => new ArrayType(Element, Length);
}

public sealed class Field
{
    public string Name = "";
    public CType Type = null!;
    public int Offset;
    public bool IsBitField; // 是否为位域（含 :0 零宽）
    public int BitWidth;    // 位域位宽（IsBitField=true 时有效；0=零宽强制对齐）
    public int BitOffset;   // 位域在其存储单元内的起始位
}

public sealed class StructType : CType
{
    public string? Tag;
    public readonly List<Field> Fields = new();
    public bool IsUnion;
    public bool Complete;
    private int _size, _align;
    public override int Size => _size;
    public override int Align => _align;
    public override bool IsStruct => true;
    public override string Name => (IsUnion ? "union " : "struct ") + (Tag ?? "<anon>");
    public override CType Clone() => this;

    public void Layout()
    {
        int size = 0, align = 1;
        if (IsUnion)
        {
            // 联合：每个字段独立占用偏移 0；位域按单元放置但都从 0 开始
            foreach (var f in Fields)
            {
                int a = f.Type.Align;
                int fsz = f.IsBitField ? f.Type.Size : f.Type.Size;
                if (fsz > size) size = fsz;
                if (a > align) align = a;
                f.Offset = 0;
                f.BitOffset = 0;
            }
        }
        else
        {
            long bitPos = 0; // 当前位偏移（从结构体起始计）
            foreach (var f in Fields)
            {
                int a = f.Type.Align > 0 ? f.Type.Align : 1;
                if (a > align) align = a;
                if (f.IsBitField && f.BitWidth == 0)
                {
                    // 零宽位域：强制对齐到下一单元边界
                    int unitBits = f.Type.Size * 8;
                    long unitStart = (bitPos + unitBits - 1) / unitBits * unitBits;
                    bitPos = unitStart;
                    continue;
                }
                if (f.IsBitField)
                {
                    int unitBits = f.Type.Size * 8;
                    if (f.BitWidth > unitBits) f.BitWidth = unitBits; // 位宽不得超过类型宽度
                    // 当前分配单元起点（按 unitBits 对齐包含 bitPos 的单元）
                    long unitStart = (bitPos / unitBits) * unitBits;
                    if ((bitPos - unitStart) + f.BitWidth > unitBits)
                    {
                        // 当前单元放不下，进下一单元
                        bitPos = unitStart + unitBits;
                        unitStart = bitPos;
                    }
                    f.Offset = (int)(unitStart / 8);        // 单元起始字节
                    f.BitOffset = (int)(bitPos - unitStart); // 单元内起始位
                    bitPos += f.BitWidth;
                    long byteEnd = (bitPos + 7) / 8;
                    if (byteEnd > size) size = (int)byteEnd;
                }
                else
                {
                    // 普通字段：先对齐到字节边界，再按类型对齐
                    bitPos = (bitPos + 7) & ~7L;
                    long aligned = (bitPos + (a * 8 - 1)) & ~(long)((a * 8) - 1);
                    bitPos = aligned;
                    f.Offset = (int)(bitPos / 8);
                    f.BitOffset = 0;
                    bitPos += (long)f.Type.Size * 8;
                    long byteEnd2 = (bitPos + 7) / 8;
                    if (byteEnd2 > size) size = (int)byteEnd2;
                }
            }
        }
        if (align < 1) align = 1;
        size = (size + align - 1) & ~(align - 1);
        if (size == 0) size = align; // 空结构体占 1 字节
        _size = size; _align = align; Complete = true;
    }
}

public sealed class EnumType : CType
{
    public string? Tag;
    public readonly Dictionary<string, long> Constants = new();
    private readonly int _size;
    public override int Size => _size;
    public override int Align => _size;
    public override bool IsInteger => true;
    public override bool IsEnum => true;
    public EnumType(int size = 4) { _size = size; }
    public override string Name => "enum " + (Tag ?? "<anon>");
    public override CType Clone() => this;
}

public sealed class FunctionType : CType
{
    public CType Return { get; init; }
    public List<ParamType> Params { get; init; }
    public bool Variadic { get; init; }
    public override int Size => 1;
    public override int Align => 1;
    public override bool IsFunction => true;
    public override string Name => Return.Name + " (*)(" + string.Join(",", Params.Select(p => p.Type.Name)) + (Variadic ? "..." : "") + ")";
    public override CType Clone() => new FunctionType(Return, Params.ToList(), Variadic);
    public FunctionType(CType ret, List<ParamType> parms, bool variadic) { Return = ret; Params = parms; Variadic = variadic; }
}

public sealed class ParamType
{
    public string? Name;
    public CType Type;
    public ParamType(string? name, CType type) { Name = name; Type = type; }
}

/// <summary>typeof(expr)/typeof(type) 占位类型：解析期无法确定类型，由 Sema.ResolveType 解析。
/// Expr 与 TypeArg 二者之一非空：Expr=typeof(表达式)，TypeArg=typeof(类型名)。
/// Resolved 由 Sema 填充，之后 Size/Align/Name 转发至 Resolved。</summary>
public sealed class TypeofType : CType
{
    public Expr? Expr;
    public CType? TypeArg;
    public CType? Resolved;
    public override int Size => Resolved?.Size ?? 0;
    public override int Align => Resolved?.Align ?? 1;
    public override bool IsInteger => Resolved?.IsInteger ?? false;
    public override bool IsPointer => Resolved?.IsPointer ?? false;
    public override bool IsArray => Resolved?.IsArray ?? false;
    public override bool IsStruct => Resolved?.IsStruct ?? false;
    public override bool IsFunction => Resolved?.IsFunction ?? false;
    public override bool IsVoid => Resolved?.IsVoid ?? false;
    public override string Name => Resolved?.Name ?? "typeof(...)";
    public override CType Clone() => this;
}

public static class TypeFactory
{
    public static IntegerType Char(TargetInfo t, bool unsig = false) => new(IntKind.Char, unsig, t.CharSize, t.CharAlign);
    public static IntegerType Short(TargetInfo t, bool unsig = false) => new(IntKind.Short, unsig, t.ShortSize, t.ShortAlign);
    public static IntegerType Int(TargetInfo t, bool unsig = false) => new(IntKind.Int, unsig, t.IntSize, t.IntAlign);
    public static IntegerType Long(TargetInfo t, bool unsig = false) => new(IntKind.Long, unsig, t.LongSize, t.LongAlign);
    public static PointerType Pointer(CType element, TargetInfo t) => new(element, t.PointerSize);
}
