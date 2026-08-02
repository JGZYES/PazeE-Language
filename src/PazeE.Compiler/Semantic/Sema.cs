using PazeE.Compiler.Lexer;
using PazeE.Compiler.Parser;

namespace PazeE.Compiler.Semantic;

/// <summary>语义分析：名字解析、类型推导与检查、栈帧布局，并收集字符串字面量与外部符号。</summary>
public sealed class Sema
{
    public Diagnostics Diag { get; }
    public TargetInfo Target { get; }
    private readonly ScopeStack _scopes = new();
    public readonly Dictionary<string, Symbol> Globals = new();
    public readonly List<Symbol> Externs = new();
    public readonly List<StringLiteral> Strings = new();
    private readonly Dictionary<string, int> _stringIds = new();

    /// <summary>函数内的 static 局部变量（由 CodeGen 按内部链接全局发射）。</summary>
    public readonly List<VarDecl> StaticLocals = new();
    private int _staticCounter;

    private int _curOffset;
    private FunctionDecl? _curFunc;

    public Sema(Diagnostics diag, TargetInfo target) { Diag = diag; Target = target; }

    public bool Analyze(TranslationUnit unit)
    {
        RegisterGlobals(unit);
        foreach (var d in unit.Decls)
            if (d is FunctionDecl f && f.Body != null) AnalyzeFunction(f);
        return !Diag.HasErrors;
    }

    // ---------------- 全局注册 ----------------
    private void RegisterGlobals(TranslationUnit unit)
    {
        foreach (var d in Flatten(unit.Decls))
        {
            switch (d)
            {
                case FunctionDecl f:
                    {
                        bool ext = f.Storage == StorageClass.Extern || f.Body == null;
                        if (Globals.TryGetValue(f.Name, out var ex))
                        {
                            if (ex.IsExtern && f.Body != null) { ex.IsExtern = false; ex.IsDefined = true; ex.Func = f; f.Sym = ex; }
                        }
                        else
                        {
                            var sym = new Symbol(f.Name, MakeFunctionType(f), SymKind.Func)
                            { IsGlobal = true, IsExtern = ext, IsDefined = f.Body != null, Func = f, Storage = f.Storage };
                            Globals[f.Name] = sym;
                            f.Sym = sym;
                            if (ext) Externs.Add(sym);
                        }
                        break;
                    }
                case VarDecl v when !v.IsTypedef:
                    {
                        v.Type = ResolveType(v.Type);
                        bool ext = v.Storage == StorageClass.Extern;
                        v.IsGlobal = true;
                        if (Globals.TryGetValue(v.Name, out var exv) && exv.IsExtern && v.Init != null)
                        { exv.IsExtern = false; exv.IsDefined = true; v.Sym = exv; }
                        else if (!Globals.ContainsKey(v.Name))
                        {
                            var sym = new Symbol(v.Name, v.Type, SymKind.Var)
                            { IsGlobal = true, IsExtern = ext, IsDefined = v.Init != null, Storage = v.Storage };
                            Globals[v.Name] = sym;
                            v.Sym = sym;
                            if (ext && !Externs.Any(e => e.Name == v.Name)) Externs.Add(sym);
                        }
                        else v.Sym = Globals[v.Name];
                        break;
                    }
            }
        }
    }

    private static IEnumerable<Decl> Flatten(List<Decl> decls)
    {
        foreach (var d in decls)
        {
            if (d is DeclGroup g) { foreach (var dd in g.Decls) yield return dd; }
            else yield return d;
        }
    }

    private CType MakeFunctionType(FunctionDecl f)
    {
        var prms = f.Params.Select(p => new ParamType(p.Name, p.Type)).ToList();
        return new FunctionType(f.ReturnType, prms, f.Variadic);
    }

    // ---------------- 函数体分析 ----------------
    private void AnalyzeFunction(FunctionDecl f)
    {
        _curFunc = f;
        _curOffset = 0;
        _scopes.Push();
        // 参数入栈帧
        foreach (var p in f.Params)
        {
            p.Type = ResolveType(p.Type);
            var pt = p.Type is ArrayType at ? new PointerType(at.Element, Target.PointerSize) : p.Type;
            AllocateSlot(pt);
            var sym = new Symbol(p.Name, pt, SymKind.Param) { Offset = -_curOffset };
            _scopes.Add(sym);
            p.Type = pt;
        }
        if (f.Body != null) VisitBlock(f.Body, declareScope: false);
        _scopes.Pop();

        int shadow = Target.Abi == Abi.Win64 ? 32 : 0;
        f.FrameSize = Align(_curOffset + shadow, 16);
    }

    private void AllocateSlot(CType type)
    {
        int size = type is ArrayType ? type.Size : type.Size;
        if (size <= 0) size = Target.PointerSize;
        _curOffset = Align(_curOffset + size, type.Align > 0 ? type.Align : 8);
    }

    private static int Align(int v, int a) => a <= 1 ? v : (v + a - 1) & ~(a - 1);

    private void VisitBlock(BlockStmt block, bool declareScope = true)
    {
        if (declareScope) _scopes.Push();
        foreach (var item in block.Items)
        {
            switch (item)
            {
                case Stmt s: VisitStmt(s); break;
                case VarDecl v: DeclareLocal(v); break;
                case TypedefDecl td: _scopes.Add(new Symbol(td.Name, td.Type, SymKind.Typedef)); break;
                case DeclGroup dg:
                    foreach (var dd in dg.Decls)
                    {
                        if (dd is VarDecl vdg) DeclareLocal(vdg);
                        else if (dd is TypedefDecl tdd) _scopes.Add(new Symbol(tdd.Name, tdd.Type, SymKind.Typedef));
                    }
                    break;
                case StructDecl: case EnumDecl: break;
            }
        }
        if (declareScope) _scopes.Pop();
    }

    private void DeclareLocal(VarDecl v)
    {
        if (v.IsTypedef) { _scopes.Add(new Symbol(v.Name, v.Type, SymKind.Typedef)); return; }
        v.Type = ResolveType(v.Type);   // 解析 typeof(expr)/typeof(type)
        // static 局部：分配内部链接全局存储（.data/.bss），跨调用保持状态。mangled 名保证唯一。
        if (v.Storage == StorageClass.Static)
        {
            string mangled = $"$static.{_curFunc?.Name ?? "_global"}.{v.Name}.{_staticCounter++}";
            v.IsGlobal = true;
            v.MangledName = mangled;
            var sym = new Symbol(v.Name, v.Type, SymKind.Local)
            { IsGlobal = true, IsExtern = false, IsDefined = true, Storage = StorageClass.Static, MangledName = mangled, Offset = 0 };
            v.Sym = sym;
            _scopes.Add(sym);
            StaticLocals.Add(v);
            if (v.Init != null) v.Init.Type = VisitExpr(v.Init);
            return;
        }
        AllocateSlot(v.Type);
        v.StackOffset = -_curOffset;
        v.IsGlobal = false;
        var sym2 = new Symbol(v.Name, v.Type, SymKind.Local) { Offset = v.StackOffset };
        v.Sym = sym2;
        _scopes.Add(sym2);
        if (v.Init != null)
        {
            if (v.Init is InitListExpr) { /* codegen 处理 */ }
            else { v.Init.Type = VisitExpr(v.Init); }
            CheckInitConvertible(v.Init.Type, v.Type, v.Range);
        }
    }

    // ---------------- 语句 ----------------
    private void VisitStmt(Stmt s)
    {
        switch (s)
        {
            case BlockStmt b: VisitBlock(b); break;
            case ExprStmt es: if (es.Expr != null) es.Expr.Type = VisitExpr(es.Expr); break;
            case NullStmt: break;
            case IfStmt i: i.Cond.Type = VisitExpr(i.Cond); VisitStmt(i.Then); if (i.Else != null) VisitStmt(i.Else); break;
            case WhileStmt w: w.Cond.Type = VisitExpr(w.Cond); VisitStmt(w.Body); break;
            case DoWhileStmt dw: dw.Cond.Type = VisitExpr(dw.Cond); VisitStmt(dw.Body); break;
            case ForStmt fo:
                _scopes.Push();
                if (fo.Init is VarDecl fv) DeclareLocal(fv);
                else if (fo.Init is Expr fe) fe.Type = VisitExpr(fe);
                if (fo.Cond != null) fo.Cond.Type = VisitExpr(fo.Cond);
                if (fo.Update != null) fo.Update.Type = VisitExpr(fo.Update);
                VisitStmt(fo.Body);
                _scopes.Pop();
                break;
            case SwitchStmt sw: sw.Expr.Type = VisitExpr(sw.Expr); VisitBlock(sw.Body, false); break;
            case CaseStmt cs: if (cs.Value != null) cs.Value.Type = VisitExpr(cs.Value); foreach (var st in cs.Body) VisitStmt(st); break;
            case BreakStmt: case ContinueStmt: break;
            case ReturnStmt rs: if (rs.Value != null) rs.Value.Type = VisitExpr(rs.Value); break;
            case DeclStmt ds: DeclareLocal(ds.Decl); break;
            case LabelStmt ls: VisitStmt(ls.Body); break;
            case GotoStmt: break;
        }
    }

    // ---------------- 表达式（返回类型，同时设置 Expr.Type） ----------------
    private CType VisitExpr(Expr e)
    {
        var t = VisitExprInner(e);
        e.Type = t;
        return t;
    }

    private CType VisitExprInner(Expr e)
    {
        switch (e)
        {
            case IntLiteral il:
                return il.Value >= int.MinValue && il.Value <= int.MaxValue
                    ? TypeFactory.Int(Target)
                    : TypeFactory.Long(Target);
            case CharLiteral: return TypeFactory.Int(Target);
            case StringLiteral sl: { sl.StringId = AddString(sl.Value); return new PointerType(TypeFactory.Char(Target), Target.PointerSize); }
            case IdentifierRef id:
                {
                    var sym = _scopes.Find(id.Name) ?? (Globals.TryGetValue(id.Name, out var g) ? g : null);
                    if (sym == null) { Diag.Error(id.Range, $"未声明的标识符 '{id.Name}'"); return TypeFactory.Int(Target); }
                    id.Sym = sym;
                    return sym.Kind == SymKind.Func ? sym.Type : sym.Type;
                }
            case UnaryExpr u:
                {
                    var ot = VisitExpr(u.Operand);
                    switch (u.Op)
                    {
                        case TokenKind.Amp:
                            if (!IsLValue(u.Operand)) Diag.Error(u.Range, "'&' 需要左值");
                            return new PointerType(ot, Target.PointerSize);
                        case TokenKind.Star:
                            if (ot is not PointerType pt) { Diag.Error(u.Range, "'*' 需要指针类型"); return TypeFactory.Int(Target); }
                            return pt.Element;
                        case TokenKind.Not: return TypeFactory.Int(Target);
                        case TokenKind.Plus: case TokenKind.Minus: case TokenKind.Tilde:
                            return IntegerPromote(ot);
                        case TokenKind.PlusPlus: case TokenKind.MinusMinus:
                            if (!IsLValue(u.Operand)) Diag.Error(u.Range, "++/-- 需要左值");
                            return ot;
                        default: return ot;
                    }
                }
            case BinaryExpr b:
                {
                    var lt = VisitExpr(b.Left);
                    var rt = VisitExpr(b.Right);
                    return BinaryResultType(b.Op, lt, rt);
                }
            case AssignExpr a:
                {
                    var tt = VisitExpr(a.Target);
                    VisitExpr(a.Value);
                    if (!IsLValue(a.Target)) Diag.Error(a.Range, "赋值目标需要左值");
                    return tt;
                }
            case ConditionalExpr c:
                { VisitExpr(c.Cond); var tt = VisitExpr(c.Then); VisitExpr(c.Else); return tt; }
            case CallExpr c:
                {
                    var ct = VisitExpr(c.Callee);
                    foreach (var arg in c.Args) VisitExpr(arg);
                    var ft = UnwrapFuncType(ct);
                    if (ft == null) { Diag.Error(c.Range, "调用非函数类型"); return TypeFactory.Int(Target); }
                    return ft.Return is VoidType ? TypeFactory.Int(Target) : ft.Return;
                }
            case IndexExpr ix:
                {
                    var at = VisitExpr(ix.Array); VisitExpr(ix.Index);
                    var elem = ElementTypeOf(at);
                    return elem ?? TypeFactory.Int(Target);
                }
            case MemberExpr m:
                {
                    var st = VisitExpr(m.Expr);
                    var baseType = m.Arrow ? (st is PointerType pp ? pp.Element : st) : st;
                    if (baseType is StructType structT)
                    {
                        var (field, _) = AstHelpers.FindField(structT, m.Name);
                        if (field == null) { Diag.Error(m.Range, $"结构体无成员 '{m.Name}'"); return TypeFactory.Int(Target); }
                        return field.Type;
                    }
                    Diag.Error(m.Range, "'.'/'->' 需要结构体类型");
                    return TypeFactory.Int(Target);
                }
            case CastExpr ce:
                { VisitExpr(ce.Operand); ce.TargetType = ResolveType(ce.TargetType); return ce.TargetType; }
            case SizeofExpr sz:
                {
                    if (sz.SizeOfType != null) { sz.SizeOfType = ResolveType(sz.SizeOfType); return TypeFactory.Long(Target, true); }
                    VisitExpr(sz.Expr!);
                    return TypeFactory.Long(Target, true);
                }
            case CommaExpr cm: { VisitExpr(cm.Left); return VisitExpr(cm.Right); }
            case InitListExpr il: { foreach (var el in il.Elements) VisitExpr(el); return TypeFactory.Int(Target); }
            case CompoundLiteralExpr cl:
                {
                    if (cl.Init is InitListExpr clIl) foreach (var el in clIl.Elements) VisitExpr(el);
                    else if (cl.Init != null) VisitExpr(cl.Init);
                    return cl.LitType ?? TypeFactory.Int(Target);
                }
            case StmtExpr se:
                {
                    VisitBlock(se.Body);
                    // 结果类型 = 块末最后一个表达式语句的类型
                    for (int i = se.Body.Items.Count - 1; i >= 0; i--)
                    {
                        if (se.Body.Items[i] is ExprStmt es && es.Expr != null && es.Expr.Type != null)
                            return es.Expr.Type;
                    }
                    return TypeFactory.Int(Target);
                }
            default: return TypeFactory.Int(Target);
        }
    }

    /// <summary>解析 TypeofType 占位类型：typeof(expr) 经语义分析得类型；typeof(type) 递归解析。
    /// 在声明、转型、sizeof 等类型位置调用，使后续布局/代码生成拿到真实尺寸。</summary>
    private CType ResolveType(CType? t)
    {
        if (t is not TypeofType tot) return t!;
        if (tot.Resolved != null) return tot.Resolved;
        if (tot.TypeArg != null) tot.Resolved = ResolveType(tot.TypeArg);
        else if (tot.Expr != null) tot.Resolved = VisitExpr(tot.Expr);
        else tot.Resolved = TypeFactory.Int(Target);
        return tot.Resolved;
    }

    private FunctionType? UnwrapFuncType(CType t)
    {
        if (t is FunctionType ft) return ft;
        if (t is PointerType pt && pt.Element is FunctionType pft) return pft;
        return null;
    }

    private CType? ElementTypeOf(CType t)
    {
        if (t is ArrayType at) return at.Element;
        if (t is PointerType pt) return pt.Element;
        return null;
    }

    private static bool IsPtrLike(CType t) => t.IsPointer || t.IsArray;
    private CType Decay(CType t) => t is ArrayType at ? new PointerType(at.Element, Target.PointerSize) : t;

    private CType BinaryResultType(TokenKind op, CType lt, CType rt)
    {
        // 指针运算（数组在此衰减为指针）
        bool lp = IsPtrLike(lt), rp = IsPtrLike(rt);
        if ((lp || rp) && (op == TokenKind.Plus || op == TokenKind.Minus))
        {
            if (lp && rt.IsInteger) return Decay(lt);
            if (lt.IsInteger && rp && op == TokenKind.Plus) return Decay(rt);
            if (lp && rp && op == TokenKind.Minus) return TypeFactory.Long(Target);
        }
        if (op == TokenKind.AndAnd || op == TokenKind.OrOr
            || op == TokenKind.Eq || op == TokenKind.NotEq
            || op == TokenKind.Lt || op == TokenKind.Le || op == TokenKind.Gt || op == TokenKind.Ge)
            return TypeFactory.Int(Target);
        if (op == TokenKind.Shl || op == TokenKind.Shr) return IntegerPromote(lt);
        // 整型 usual conversion
        return UsualArith(lt, rt);
    }

    private CType IntegerPromote(CType t)
    {
        if (t is IntegerType it)
        {
            if (it.Kind == IntKind.Char || it.Kind == IntKind.Short)
                return TypeFactory.Int(Target, it.Unsigned && Target.IntSize >= it.Size);
            return it;
        }
        return t;
    }

    private CType UsualArith(CType a, CType b)
    {
        a = IntegerPromote(a); b = IntegerPromote(b);
        if (a is not IntegerType ia) return a;
        if (b is not IntegerType ib) return b;
        if (ia.Size > ib.Size) return ia;
        if (ib.Size > ia.Size) return ib;
        if (ia.Unsigned != ib.Unsigned) return new IntegerType(ia.Kind, true, ia.Size, ia.Align);
        return ia;
    }

    private bool IsLValue(Expr e) => e is IdentifierRef { Sym: { Kind: not SymKind.Func } }
        or IndexExpr or MemberExpr or UnaryExpr { Op: TokenKind.Star };

    private void CheckInitConvertible(CType? from, CType to, SourceRange r)
    {
        if (from == null) return;
        bool ok = from.IsInteger || from.IsPointer || to.IsArray || to.IsStruct || from.IsArray || from.IsEnum;
        if (!ok) Diag.Warning(r, $"初始化类型 '{from}' → '{to}' 可能不兼容");
    }

    private int AddString(string value)
    {
        if (_stringIds.TryGetValue(value, out var id)) return id;
        id = Strings.Count;
        _stringIds[value] = id;
        Strings.Add(new StringLiteral(value) { StringId = id });
        return id;
    }

    public int SizeofType(CType t) => t.Size;
}
