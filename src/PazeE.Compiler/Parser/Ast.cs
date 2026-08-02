using PazeE.Compiler.Lexer;

namespace PazeE.Compiler.Parser;

// ===================== 声明 =====================
public abstract class Decl { public SourceRange Range; }

/// <summary>逗号分隔的多个声明（如 int a, b, c;）。</summary>
public sealed class DeclGroup : Decl { public readonly List<Decl> Decls; public DeclGroup(List<Decl> d) { Decls = d; } }

public sealed class TranslationUnit : Decl
{
    public readonly List<Decl> Decls = new();
}

public enum StorageClass { None, Extern, Static }

public sealed class Param { public CType Type; public string Name; public Param(CType t, string n) { Type = t; Name = n; } }

public sealed class FunctionDecl : Decl
{
    public CType ReturnType;
    public string Name;
    public readonly List<Param> Params = new();
    public BlockStmt? Body;
    public StorageClass Storage;
    public bool Variadic;
    public PazeE.Compiler.Semantic.Symbol Sym;
    public int FrameSize;
    public FunctionDecl(CType ret, string name, SourceRange r) { ReturnType = ret; Name = name; Range = r; }
}

public sealed class VarDecl : Decl
{
    public CType Type;
    public string Name;
    public Expr? Init;
    public StorageClass Storage;
    public bool IsTypedef;
    public PazeE.Compiler.Semantic.Symbol Sym;
    public int StackOffset;
    public bool IsGlobal;
    /// <summary>static 局部变量的内部链接 mangled 名（如 $static.main.c.0）；null 表示普通变量用 Name。</summary>
    public string? MangledName;
    public VarDecl(CType t, string name, SourceRange r) { Type = t; Name = name; Range = r; }
}

public sealed class FieldDecl { public CType Type; public string Name; public Expr? BitWidth; }

public sealed class StructDecl : Decl
{
    public string? Tag;
    public readonly List<FieldDecl> Fields = new();
    public bool IsUnion;
    public bool IsForward;
    public StructDecl(string? tag, SourceRange r) { Tag = tag; Range = r; }
}

public sealed class TypedefDecl : Decl
{
    public string Name;
    public CType Type;
    public TypedefDecl(string name, CType t, SourceRange r) { Name = name; Type = t; Range = r; }
}

public sealed class EnumDecl : Decl
{
    public string? Tag;
    public readonly List<(string Name, Expr? Value)> Constants = new();
    public EnumDecl(string? tag, SourceRange r) { Tag = tag; Range = r; }
}

// ===================== 语句 =====================
public abstract class Stmt { public SourceRange Range; }
public sealed class BlockStmt : Stmt { public readonly List<object> Items = new(); } // Stmt 或 Decl(VarDecl/StructDecl/TypedefDecl/EnumDecl)
public sealed class ExprStmt : Stmt { public Expr? Expr; public ExprStmt(Expr? e) { Expr = e; } }
public sealed class NullStmt : Stmt { }
public sealed class IfStmt : Stmt { public Expr Cond; public Stmt Then; public Stmt? Else; }
public sealed class WhileStmt : Stmt { public Expr Cond; public Stmt Body; }
public sealed class DoWhileStmt : Stmt { public Stmt Body; public Expr Cond; }
public sealed class ForStmt : Stmt { public object? Init; public Expr? Cond; public Expr? Update; public Stmt Body; } // Init: Expr/VarDecl/null
public sealed class SwitchStmt : Stmt { public Expr Expr; public BlockStmt Body; public List<CaseStmt> Cases => Body.Items.OfType<CaseStmt>().ToList(); }
public sealed class CaseStmt : Stmt { public Expr? Value; public readonly List<Stmt> Body = new(); public bool IsDefault; }
public sealed class BreakStmt : Stmt { }
public sealed class ContinueStmt : Stmt { }
public sealed class ReturnStmt : Stmt { public Expr? Value; }
public sealed class DeclStmt : Stmt { public VarDecl Decl; public DeclStmt(VarDecl d) { Decl = d; } }
public sealed class GotoStmt : Stmt { public string Label; }
public sealed class LabelStmt : Stmt { public string Label; public Stmt Body; }

// ===================== 表达式 =====================
public abstract class Expr { public SourceRange Range; public CType? Type; }
public sealed class IntLiteral : Expr { public long Value; public IntLiteral(long v) { Value = v; } }
public sealed class CharLiteral : Expr { public long Value; public CharLiteral(long v) { Value = v; } }
public sealed class StringLiteral : Expr { public string Value; public int StringId; public StringLiteral(string v) { Value = v; } }
public sealed class IdentifierRef : Expr { public string Name; public PazeE.Compiler.Semantic.Symbol Sym; public IdentifierRef(string n) { Name = n; } }
public sealed class UnaryExpr : Expr { public TokenKind Op; public Expr Operand; public bool Prefix; public UnaryExpr(TokenKind op, Expr e, bool prefix) { Op = op; Operand = e; Prefix = prefix; } }
public sealed class BinaryExpr : Expr { public TokenKind Op; public Expr Left, Right; public BinaryExpr(TokenKind op, Expr l, Expr r) { Op = op; Left = l; Right = r; } }
public sealed class AssignExpr : Expr { public TokenKind Op; public Expr Target, Value; public AssignExpr(TokenKind op, Expr t, Expr v) { Op = op; Target = t; Value = v; } }
public sealed class ConditionalExpr : Expr { public Expr Cond, Then, Else; public ConditionalExpr(Expr c, Expr t, Expr e) { Cond = c; Then = t; Else = e; } }
public sealed class CallExpr : Expr { public Expr Callee; public readonly List<Expr> Args = new(); public CallExpr(Expr c) { Callee = c; } }
public sealed class IndexExpr : Expr { public Expr Array, Index; public IndexExpr(Expr a, Expr i) { Array = a; Index = i; } }
public sealed class MemberExpr : Expr { public Expr Expr; public string Name; public bool Arrow; public MemberExpr(Expr e, string n, bool arrow) { Expr = e; Name = n; Arrow = arrow; } }
public sealed class CastExpr : Expr { public CType TargetType; public Expr Operand; public CastExpr(CType t, Expr e) { TargetType = t; Operand = e; } }
public sealed class SizeofExpr : Expr { public Expr? Expr; public CType? SizeOfType; public SizeofExpr(Expr? e, CType? t) { Expr = e; SizeOfType = t; } }
public sealed class CommaExpr : Expr { public Expr Left, Right; public CommaExpr(Expr l, Expr r) { Left = l; Right = r; } }
public sealed class InitListExpr : Expr
{
    public readonly List<Expr> Elements = new();
    /// <summary>与 Elements 并行：每个元素的设计符链（.field / [index]）。空数组=普通顺序元素。</summary>
    public readonly List<Designator[]> Designators = new();
    public bool HasDesignators { get { for (int i = 0; i < Designators.Count; i++) if (Designators[i].Length > 0) return true; return false; } }
}
public sealed class StringConcatExpr : Expr { public readonly List<StringLiteral> Parts = new(); } // 相邻字符串拼接

/// <summary>设计符：.field（Field 非空）或 [index]（Index 非空）。可链式如 [2].x。</summary>
public sealed class Designator { public string? Field; public long? Index; }

/// <summary>复合字面量 (type){init}：产生匿名左值。Init 为 InitListExpr 或标量 Expr。</summary>
public sealed class CompoundLiteralExpr : Expr { public CType? LitType; public Expr Init; }

/// <summary>语句表达式 ({ stmt; expr; })：GNU 扩展。值为 body 末表达式语句的值（已在 RAX/X0）。</summary>
public sealed class StmtExpr : Expr { public BlockStmt Body; public StmtExpr(BlockStmt b) { Body = b; } }

public static class AstHelpers
{
    public static bool IsLValue(this Expr e) => e is IdentifierRef or IndexExpr or MemberExpr or UnaryExpr { Op: TokenKind.Star };

    /// <summary>在结构体/联合中按名查找字段，递归进入匿名（Name=="")嵌套 struct/union 字段。
    /// 返回 (字段, 累加字节偏移)。匿名成员的偏移叠加到子字段偏移上。未找到返回 (null, 0)。</summary>
    public static (Field? field, int offset) FindField(StructType st, string name)
    {
        foreach (var f in st.Fields)
        {
            if (f.Name == name) return (f, f.Offset);
        }
        // 递归匿名成员
        foreach (var f in st.Fields)
        {
            if (f.Name.Length == 0 && f.Type is StructType nested)
            {
                var (nf, noff) = FindField(nested, name);
                if (nf != null) return (nf, f.Offset + noff);
            }
        }
        return (null, 0);
    }

    /// <summary>死代码消除：计算从入口函数可达的所有函数定义名。
    /// 遍历每个函数体，收集对函数符号的引用（直接调用与取地址），BFS 标记从 entry 可达的函数。
    /// 未被引用的函数（如 paze.h 中未使用的 gui_*）不会被编译，避免拉入多余的外部符号
    /// （例如未用 GUI 的程序不会因 paze.h 的 gui 实现而误判为 Windows GUI 子系统）。</summary>
    public static HashSet<string> ReachableFunctions(List<Decl> decls, string entry = "main")
    {
        var defined = new Dictionary<string, FunctionDecl>();
        foreach (var d in FlattenDecls(decls))
            if (d is FunctionDecl f && f.Body != null) defined[f.Name] = f;

        var reachable = new HashSet<string>();
        if (!defined.ContainsKey(entry)) return reachable;
        var queue = new Queue<string>();
        reachable.Add(entry);
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            var refs = new HashSet<string>();
            CollectFuncRefs(defined[name].Body, refs);
            foreach (var r in refs)
                if (defined.ContainsKey(r) && reachable.Add(r)) queue.Enqueue(r);
        }
        return reachable;
    }

    private static IEnumerable<Decl> FlattenDecls(List<Decl> decls)
    {
        foreach (var d in decls)
        {
            if (d is DeclGroup g) { foreach (var dd in g.Decls) yield return dd; }
            else yield return d;
        }
    }

    private static void CollectFuncRefs(Stmt? s, HashSet<string> refs)
    {
        if (s == null) return;
        switch (s)
        {
            case BlockStmt b:
                foreach (var item in b.Items)
                {
                    switch (item)
                    {
                        case Stmt st: CollectFuncRefs(st, refs); break;
                        case VarDecl v: CollectFuncRefsFromType(v.Type, refs); CollectFuncRefs(v.Init, refs); break;
                        case DeclGroup dg:
                            foreach (var dd in dg.Decls) { if (dd is VarDecl vdgv) { CollectFuncRefsFromType(vdgv.Type, refs); CollectFuncRefs(vdgv.Init, refs); } }
                            break;
                    }
                }
                break;
            case ExprStmt es: CollectFuncRefs(es.Expr, refs); break;
            case IfStmt ifs: CollectFuncRefs(ifs.Cond, refs); CollectFuncRefs(ifs.Then, refs); CollectFuncRefs(ifs.Else, refs); break;
            case WhileStmt ws: CollectFuncRefs(ws.Cond, refs); CollectFuncRefs(ws.Body, refs); break;
            case DoWhileStmt dw: CollectFuncRefs(dw.Body, refs); CollectFuncRefs(dw.Cond, refs); break;
            case ForStmt fs:
                if (fs.Init is VarDecl fv) { CollectFuncRefsFromType(fv.Type, refs); CollectFuncRefs(fv.Init, refs); }
                else if (fs.Init is Expr fe) CollectFuncRefs(fe, refs);
                CollectFuncRefs(fs.Cond, refs); CollectFuncRefs(fs.Update, refs); CollectFuncRefs(fs.Body, refs);
                break;
            case SwitchStmt ss: CollectFuncRefs(ss.Expr, refs); CollectFuncRefs(ss.Body, refs); break;
            case CaseStmt cs: CollectFuncRefs(cs.Value, refs); foreach (var x in cs.Body) CollectFuncRefs(x, refs); break;
            case ReturnStmt rs: CollectFuncRefs(rs.Value, refs); break;
            case DeclStmt ds: CollectFuncRefsFromType(ds.Decl.Type, refs); CollectFuncRefs(ds.Decl.Init, refs); break;
            case LabelStmt ls: CollectFuncRefs(ls.Body, refs); break;
        }
    }

    private static void CollectFuncRefs(Expr? e, HashSet<string> refs)
    {
        if (e == null) return;
        switch (e)
        {
            case IdentifierRef id:
                if (id.Sym != null && id.Sym.Kind == PazeE.Compiler.Semantic.SymKind.Func) refs.Add(id.Name);
                break;
            case UnaryExpr u: CollectFuncRefs(u.Operand, refs); break;
            case BinaryExpr b: CollectFuncRefs(b.Left, refs); CollectFuncRefs(b.Right, refs); break;
            case AssignExpr a: CollectFuncRefs(a.Target, refs); CollectFuncRefs(a.Value, refs); break;
            case ConditionalExpr c: CollectFuncRefs(c.Cond, refs); CollectFuncRefs(c.Then, refs); CollectFuncRefs(c.Else, refs); break;
            case CallExpr c: CollectFuncRefs(c.Callee, refs); foreach (var arg in c.Args) CollectFuncRefs(arg, refs); break;
            case IndexExpr ix: CollectFuncRefs(ix.Array, refs); CollectFuncRefs(ix.Index, refs); break;
            case MemberExpr m: CollectFuncRefs(m.Expr, refs); break;
            case CastExpr ce: CollectFuncRefsFromType(ce.TargetType, refs); CollectFuncRefs(ce.Operand, refs); break;
            case SizeofExpr sz: CollectFuncRefs(sz.Expr, refs); break;
            case CommaExpr cm: CollectFuncRefs(cm.Left, refs); CollectFuncRefs(cm.Right, refs); break;
            case InitListExpr il: foreach (var el in il.Elements) CollectFuncRefs(el, refs); break;
            case CompoundLiteralExpr cl: CollectFuncRefs(cl.Init, refs); break;
            case StmtExpr se:
                foreach (var item in se.Body.Items)
                {
                    if (item is Stmt st) CollectFuncRefs(st, refs);
                    else if (item is VarDecl sv) { CollectFuncRefsFromType(sv.Type, refs); CollectFuncRefs(sv.Init, refs); }
                }
                break;
            case StringConcatExpr sc: break; // 仅字符串字面量，无函数引用
        }
    }

    /// <summary>从类型中收集函数引用：仅 typeof(expr) 的 expr 可能含函数调用。</summary>
    private static void CollectFuncRefsFromType(CType? t, HashSet<string> refs)
    {
        if (t is TypeofType tot && tot.Expr != null) CollectFuncRefs(tot.Expr, refs);
    }
}
