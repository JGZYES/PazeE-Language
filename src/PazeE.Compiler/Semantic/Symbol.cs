using PazeE.Compiler.Parser;

namespace PazeE.Compiler.Semantic;

public enum SymKind { Var, Func, Param, Local, Typedef, EnumConst }

public sealed class Symbol
{
    public string Name;
    public CType Type;
    public SymKind Kind;
    public StorageClass Storage;
    /// <summary>局部/参数：相对 rbp 的栈偏移（负值）。全局：0。</summary>
    public int Offset;
    public bool IsGlobal;
    public bool IsExtern;
    public bool IsDefined;
    public FunctionDecl? Func;
    /// <summary>static 局部变量的内部链接 mangled 名；非空时 CodeGen 用此名寻址（而非 Name）。</summary>
    public string? MangledName;
    public Symbol(string name, CType type, SymKind kind) { Name = name; Type = type; Kind = kind; }

    public override string ToString() => $"{Kind} {Type} {Name}";
}

/// <summary>作用域栈。</summary>
public sealed class ScopeStack
{
    private readonly List<Dictionary<string, Symbol>> _scopes = new();
    public void Push() => _scopes.Add(new Dictionary<string, Symbol>());
    public void Pop() => _scopes.RemoveAt(_scopes.Count - 1);
    public void Add(Symbol s) => _scopes[^1][s.Name] = s;
    public bool AddUnique(Symbol s, Diagnostics diag, SourceRange r)
    {
        if (_scopes[^1].ContainsKey(s.Name)) { diag.Error(r, $"重复声明 '{s.Name}'"); return false; }
        _scopes[^1][s.Name] = s; return true;
    }
    public Symbol? Find(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return null;
    }
}
