using PazeE.Compiler.Binary;
using PazeE.Compiler.Lexer;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Semantic;

namespace PazeE.Compiler.CodeGen.Arm64;

/// <summary>把语义分析后的 AST 翻译为 AArch64 原生机器码，填充 ObjectImage。
/// 独立于 X64 后端，不共享任何逻辑。AAPCS64 ABI（三平台一致）：X0..X7 传参、X0 返回、
/// X19..X30 被调用者保存、SP 16 字节对齐、无 shadow space。
/// 策略：局部变量驻栈帧（Sema 已布局），表达式求值结果落 X0，二元运算用 1-push 约定，
/// 全部算术在 64 位进行（加载时按符号/零扩展），存储时按类型宽度截断。
/// 全局/字符串符号地址通过 ADRP+ADD 物化到 X9（记 AdrpAdd fixup），再经该寄存器访存。</summary>
public sealed class Arm64CodeGenerator : ICodeGenerator
{
    public TargetInfo Target { get; }
    private readonly AbiArm64 _abi;
    private ObjectImage _img = null!;
    private Arm64Emitter _e = null!;
    private Sema _sema = null!;

    private readonly Dictionary<int, string> _stringSyms = new();

    // 循环/跳转上下文
    private readonly Stack<(int cont, int brk)> _loops = new();
    private readonly Stack<int> _switchEnd = new();
    private FunctionDecl? _curFunc;
    private int _funcEnd;

    // 寄存器角色：X0=结果/左值，X1=次操作数，X2=临时/缩放，X9=符号地址，X10/X11=CopyBytes，X29=FP，SP=栈指针
    private static readonly Arm64Reg X0 = AR.X0, X1 = AR.X1, X2 = AR.X2, X3 = AR.X3, X4 = AR.X4;
    private static readonly Arm64Reg X9 = AR.X9, X10 = AR.X10, X11 = AR.X11, X29 = AR.X29, SP = AR.SP;

    public Arm64CodeGenerator(TargetInfo target) { Target = target; _abi = new AbiArm64(); }

    public ObjectImage Generate(TranslationUnit unit, Sema sema)
    {
        _sema = sema;
        _img = new ObjectImage { EntrySymbol = "main" };
        _e = new Arm64Emitter();

        RegisterStrings();
        RegisterGlobals(unit);
        // 死代码消除：只编译从 main 可达的函数，避免 paze.h 中未引用的 gui_*
        // 实现拉入 Win32 extern 而误判 PE 子系统为 GUI。
        var reachable = AstHelpers.ReachableFunctions(unit.Decls);
        foreach (var d in Flatten(unit.Decls))
            if (d is FunctionDecl f && f.Body != null && reachable.Contains(f.Name)) GenFunction(f);

        _e.Finish();
        _img.Text.Data.AddRange(_e.Code);   // 将已回填的机器码追加到映像 .text
        return _img;
    }

    private static IEnumerable<Decl> Flatten(List<Decl> decls)
    {
        foreach (var d in decls) { if (d is DeclGroup g) foreach (var dd in g.Decls) yield return dd; else yield return d; }
    }

    // ---------------- 字符串字面量 → .rdata ----------------
    private void RegisterStrings()
    {
        foreach (var sl in _sema.Strings)
        {
            if (_stringSyms.ContainsKey(sl.StringId)) continue;
            string sym = "$str" + sl.StringId;
            int off = _img.RData.Data.Count;
            var bytes = System.Text.Encoding.UTF8.GetBytes(sl.Value);
            _img.RData.Data.AddRange(bytes);
            _img.RData.Data.Add(0);
            _img.DefineSymbol(sym, _img.RData, off, false, false);
            _stringSyms[sl.StringId] = sym;
        }
    }

    // ---------------- 全局变量 / 外部符号 ----------------
    private void RegisterGlobals(TranslationUnit unit)
    {
        foreach (var d in Flatten(unit.Decls))
        {
            if (d is VarDecl v && !v.IsTypedef) RegisterGlobalVar(v);
            else if (d is FunctionDecl f)
            {
                if (f.Name == "main" && f.Params.Count == 2) _img.HasArgcArgv = true;
                // extern 函数的 AddExternal 推迟到 GenCall（仅实际调用时才导入），
                // 避免 paze.h 中未引用的 extern 声明（如 Win32 GUI 函数）被误导入，
                // 进而导致非 GUI 程序被误判为 Windows GUI 子系统。
            }
        }
        // static 局部变量：按内部链接全局发射（mangled 名，不导出）
        foreach (var sv in _sema.StaticLocals)
            EmitDefinedGlobal(sv.MangledName ?? sv.Name, sv.Type, sv.Init, false);
    }

    private void RegisterGlobalVar(VarDecl v)
    {
        var sym = v.Sym;
        if (sym == null) return;
        if (sym.IsExtern && !sym.IsDefined) { _img.AddExternal(v.Name); return; }
        EmitDefinedGlobal(v.Name, v.Type, v.Init, true);
    }

    /// <summary>发射一个已定义全局（含 static 局部，global=false 表示内部链接不导出）。</summary>
    private void EmitDefinedGlobal(string name, CType type, Expr? init, bool global)
    {
        if (init == null)
        {
            // BSS
            int off = _img.Bss.BssSize;
            _img.Bss.BssSize += AlignUp(SizeOf(type), SizeOf(type) > 0 ? type.Align : 8);
            _img.DefineSymbol(name, _img.Bss, off, global, false);
            return;
        }

        // .data：求常量初始值
        int dataOff = _img.Data.Data.Count;
        if (init is InitListExpr il && il.HasDesignators && (type is ArrayType || type is StructType))
        {
            int total = SizeOf(type);
            for (int k = 0; k < total; k++) _img.Data.Data.Add(0);
            PlaceInitList(il, type, dataOff, null, WriteGlobalElement);
        }
        else if (type is ArrayType at)
        {
            int total = SizeOf(at);
            if (init is StringLiteral sl && at.Element is IntegerType { Kind: IntKind.Char })
            {
                var b = System.Text.Encoding.UTF8.GetBytes(sl.Value);
                _img.Data.Data.AddRange(b);
                for (int i = b.Length; i < total; i++) _img.Data.Data.Add(0);
            }
            else if (init is InitListExpr ilArr)
            {
                int es = SizeOf(at.Element);
                for (int i = 0; i < at.Length; i++)
                {
                    if (i < ilArr.Elements.Count) EmitConstBytes(ilArr.Elements[i], at.Element);
                    else for (int k = 0; k < es; k++) _img.Data.Data.Add(0);
                }
            }
            else { for (int i = 0; i < total; i++) _img.Data.Data.Add(0); }
        }
        else if (type is StructType st)
        {
            int total = SizeOf(st);
            if (init is InitListExpr ilSt)
            {
                for (int i = 0; i < st.Fields.Count; i++)
                {
                    var fld = st.Fields[i];
                    if (i < ilSt.Elements.Count) EmitConstBytes(ilSt.Elements[i], fld.Type);
                    else for (int k = 0; k < SizeOf(fld.Type); k++) _img.Data.Data.Add(0);
                }
                int filled = st.Fields.Count > 0 ? st.Fields[^1].Offset + SizeOf(st.Fields[^1].Type) : 0;
                for (int i = filled; i < total; i++) _img.Data.Data.Add(0);
            }
            else { for (int i = 0; i < total; i++) _img.Data.Data.Add(0); }
        }
        else
        {
            // 标量
            if (init is StringLiteral slp && type is PointerType)
            {
                string ssym = GetOrAddStringSym(slp.Value);
                int slot = _img.Data.Data.Count;
                for (int i = 0; i < 8; i++) _img.Data.Data.Add(0);
                _img.AddFixup(_img.Data, slot, FixupKind.Abs64, ssym, 0);
            }
            else
            {
                long val = TryConst(init) ?? 0;
                EmitScalarBytes(val, type);
            }
        }
        _img.DefineSymbol(name, _img.Data, dataOff, global, false);
    }

    private void EmitConstBytes(Expr e, CType t)
    {
        if (e is StringLiteral sl && t is ArrayType at && at.Element is IntegerType { Kind: IntKind.Char })
        {
            var b = System.Text.Encoding.UTF8.GetBytes(sl.Value);
            _img.Data.Data.AddRange(b);
            for (int i = b.Length; i < SizeOf(t); i++) _img.Data.Data.Add(0);
            return;
        }
        long val = TryConst(e) ?? 0;
        EmitScalarBytes(val, t);
    }
    private void EmitScalarBytes(long val, CType t)
    {
        int sz = SizeOf(t);
        for (int i = 0; i < sz; i++) _img.Data.Data.Add((byte)(val >> (i * 8)));
    }
    private string GetOrAddStringSym(string value)
    {
        foreach (var kv in _stringSyms)
            if (_img.Symbols[kv.Value].StringValue == value) return kv.Value;
        int id = _stringSyms.Count;
        string sym = "$str" + id;
        int off = _img.RData.Data.Count;
        var b = System.Text.Encoding.UTF8.GetBytes(value);
        _img.RData.Data.AddRange(b); _img.RData.Data.Add(0);
        var ds = _img.DefineSymbol(sym, _img.RData, off, false, false);
        ds.StringValue = value;
        _stringSyms[id] = sym;
        return sym;
    }

    // ==================== 初始化列表（设计符 / 复合字面量）====================
    /// <summary>按 cursor 模型放置初始化列表元素。zeroFill 为 null 时假定区域已 0 填充（全局）。</summary>
    private void PlaceInitList(InitListExpr il, CType type, int baseOff, Action<int, int>? zeroFill, Action<Expr, CType, int> place)
    {
        int total = SizeOf(type);
        zeroFill?.Invoke(baseOff, total);
        int cursorElem = 0, cursorField = 0;
        ArrayType? at0 = type as ArrayType;
        StructType? st0 = type as StructType;
        for (int i = 0; i < il.Elements.Count; i++)
        {
            var desigs = i < il.Designators.Count ? il.Designators[i] : Array.Empty<Designator>();
            var val = il.Elements[i];
            int off = baseOff;
            CType cur = type;
            if (desigs.Length > 0)
            {
                foreach (var d in desigs)
                {
                    if (d.Index.HasValue && cur is ArrayType at)
                    { off += (int)d.Index.Value * SizeOf(at.Element); cur = at.Element; }
                    else if (d.Field != null && cur is StructType st)
                    {
                        var f = st.Fields.FirstOrDefault(x => x.Name == d.Field);
                        if (f != null) { off += f.Offset; cur = f.Type; }
                    }
                }
                if (at0 != null && desigs[0].Index.HasValue) cursorElem = (int)desigs[0].Index.Value + 1;
                else if (st0 != null && desigs[0].Field != null)
                {
                    int fi = st0.Fields.FindIndex(x => x.Name == desigs[0].Field);
                    if (fi >= 0) cursorField = fi + 1;
                }
            }
            else
            {
                if (at0 != null) { off = baseOff + cursorElem * SizeOf(at0.Element); cur = at0.Element; cursorElem++; }
                else if (st0 != null)
                {
                    while (cursorField < st0.Fields.Count && string.IsNullOrEmpty(st0.Fields[cursorField].Name)) cursorField++;
                    if (cursorField < st0.Fields.Count)
                    { var f = st0.Fields[cursorField]; off = baseOff + f.Offset; cur = f.Type; cursorField++; }
                }
            }
            place(val, cur, off);
        }
    }

    /// <summary>全局 .data：在 absOff 处写入一个元素的常量字节。</summary>
    private void WriteGlobalElement(Expr val, CType cur, int absOff)
    {
        if (val is InitListExpr nested && (cur is ArrayType || cur is StructType))
        { PlaceInitList(nested, cur, absOff, null, WriteGlobalElement); return; }
        if (val is StringLiteral sl)
        {
            if (cur is ArrayType at && at.Element is IntegerType { Kind: IntKind.Char })
            {
                var b = System.Text.Encoding.UTF8.GetBytes(sl.Value);
                for (int i = 0; i < SizeOf(cur); i++)
                    _img.Data.Data[absOff + i] = i < b.Length ? b[i] : (byte)0;
                return;
            }
            if (cur is PointerType)
            {
                string ssym = GetOrAddStringSym(sl.Value);
                for (int i = 0; i < 8; i++) _img.Data.Data[absOff + i] = 0;
                _img.AddFixup(_img.Data, absOff, FixupKind.Abs64, ssym, 0);
                return;
            }
        }
        long cv = TryConst(val) ?? 0;
        int sz = SizeOf(cur);
        for (int i = 0; i < sz; i++) _img.Data.Data[absOff + i] = (byte)(cv >> (i * 8));
    }

    /// <summary>局部栈：在 absOff（相对 X29）处 0 填充 size 字节。</summary>
    private void ZeroStack(int baseOff, int size)
    {
        int i = 0;
        var longT = TypeFactory.Long(Target);
        while (i + 8 <= size) { StoreConstToStack(baseOff + i, 0, longT); i += 8; }
        var charT = TypeFactory.Char(Target);
        while (i < size) { StoreConstToStack(baseOff + i, 0, charT); i++; }
    }

    /// <summary>局部栈：在 absOff（相对 X29）处放置一个元素。</summary>
    private void WriteLocalElement(Expr val, CType cur, int absOff)
    {
        if (val is InitListExpr nested && (cur is ArrayType || cur is StructType))
        { PlaceInitList(nested, cur, absOff, ZeroStack, WriteLocalElement); return; }
        if (val is StringLiteral sl)
        {
            if (cur is ArrayType at && at.Element is IntegerType { Kind: IntKind.Char })
            {
                var b = System.Text.Encoding.UTF8.GetBytes(sl.Value);
                for (int i = 0; i < SizeOf(cur); i++)
                    StoreConstToStack(absOff + i, i < b.Length ? b[i] : 0, at.Element);
                return;
            }
            if (cur is PointerType)
            {
                string ssym = GetOrAddStringSym(sl.Value);
                GenSymbolAddr(X0, ssym);
                StoreTyped(Arm64Mem.BaseDisp(X29, absOff), X0, cur);
                return;
            }
        }
        if (cur is ArrayType || cur is StructType)
        { ZeroStack(absOff, SizeOf(cur)); return; }
        long? cv = TryConst(val);
        if (cv.HasValue) StoreConstToStack(absOff, cv.Value, cur);
        else { GenValue(val); StoreTyped(Arm64Mem.BaseDisp(X29, absOff), X0, cur); }
    }

    // ==================== 复合字面量 → 匿名静态全局 ====================
    private int _clitCounter;
    private string GetOrCreateCompoundLiteral(CompoundLiteralExpr cl)
    {
        string sym = "$clit." + _clitCounter++;
        var type = cl.LitType ?? cl.Type!;
        int total = SizeOf(type);
        int baseOff = _img.Data.Data.Count;
        for (int k = 0; k < total; k++) _img.Data.Data.Add(0);
        if (cl.Init is InitListExpr il)
            PlaceInitList(il, type, baseOff, null, WriteGlobalElement);
        else if (cl.Init != null)
            WriteGlobalElement(cl.Init, type, baseOff);
        _img.DefineSymbol(sym, _img.Data, baseOff, false, false);
        return sym;
    }

    private long? TryConst(Expr? e)
    {
        switch (e)
        {
            case null: return 0;
            case IntLiteral il: return il.Value;
            case CharLiteral cl: return cl.Value;
            case SizeofExpr sz: return sz.SizeOfType != null ? SizeOf(sz.SizeOfType) : (sz.Expr != null ? SizeOf(sz.Expr.Type!) : 0);
            case CastExpr ce: return TryConst(ce.Operand);
            case UnaryExpr u:
                {
                    var v = TryConst(u.Operand);
                    if (v == null) return null;
                    return u.Op switch { TokenKind.Minus => -v, TokenKind.Plus => v, TokenKind.Tilde => ~v, TokenKind.Not => v == 0 ? 1 : 0, _ => v };
                }
            case BinaryExpr b:
                {
                    var l = TryConst(b.Left); var r = TryConst(b.Right);
                    if (l == null || r == null) return null;
                    return b.Op switch
                    {
                        TokenKind.Plus => l + r, TokenKind.Minus => l - r, TokenKind.Star => l * r,
                        TokenKind.Slash => r == 0 ? 0 : l / r, TokenKind.Percent => r == 0 ? 0 : l % r,
                        TokenKind.Shl => l << (int)r, TokenKind.Shr => l >> (int)r,
                        TokenKind.Amp => l & r, TokenKind.Pipe => l | r, TokenKind.Caret => l ^ r,
                        TokenKind.Eq => l == r ? 1 : 0, TokenKind.NotEq => l != r ? 1 : 0,
                        TokenKind.Lt => l < r ? 1 : 0, TokenKind.Le => l <= r ? 1 : 0,
                        TokenKind.Gt => l > r ? 1 : 0, TokenKind.Ge => l >= r ? 1 : 0,
                        TokenKind.AndAnd => l != 0 && r != 0 ? 1 : 0, TokenKind.OrOr => l != 0 || r != 0 ? 1 : 0,
                        _ => null
                    };
                }
            default: return null;
        }
    }

    // ---------------- 函数 ----------------
    private void GenFunction(FunctionDecl f)
    {
        _curFunc = f;
        _funcEnd = _e.NewLabel();
        int entry = _e.Position;
        _img.DefineSymbol(f.Name, _img.Text, entry, true, true);

        // prologue: STP X29,X30,[SP,#-16]!; MOV X29,SP; SUB SP,SP,#FrameSize
        _e.StpPre(X29, AR.X30, SP, -16);
        _e.AddImm(X29, SP, 0);          // ADD X29, SP, #0 （MOV FP,SP；SP 仅在 ADD/SUB 中合法）
        if (f.FrameSize > 0) _e.SubSp(f.FrameSize);

        // 参数溢出到栈槽：按 Sema 的 AllocateSlot 顺序复现偏移
        // AAPCS64：[X29]=旧 X29，[X29+8]=LR，[X29+16]=第一栈参（与 x86 SysV 一致，无 shadow）
        int regCount = Math.Min(f.Params.Count, _abi.IntArgRegs.Length);
        int stackBase = 16;
        int cur = 0;
        for (int i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            int size = p.Type.Size; if (size <= 0) size = 8;
            cur = AlignUp(cur + size, p.Type.Align > 0 ? p.Type.Align : 8);
            int off = -cur;
            var addr = Arm64Mem.BaseDisp(X29, off);
            // 取参数值（结构体参数为副本指针，标量为值）
            if (i < regCount)
                _e.MovRR(X0, AR.Of64((byte)i));
            else
            {
                int j = i - regCount;
                _e.Load(X0, Arm64Mem.BaseDisp(X29, stackBase + j * 8), 8, false);
            }
            if (p.Type is StructType)
            {
                // 结构体按值传递：X0=副本指针，复制 size 字节到本地栈槽
                _e.MovRR(X10, X0);
                _e.AddImm(X11, X29, off);
                CopyBytesRegReg(size);
            }
            else StoreTyped(addr, X0, p.Type);
        }

        GenBlock(f.Body!, declareScope: false);

        _e.MarkLabel(_funcEnd);
        // epilogue: MOV SP,X29; LDP X29,X30,[SP],#16; RET
        _e.AddImm(SP, X29, 0);
        _e.LdpPost(X29, AR.X30, SP, 16);
        _e.Ret();
    }

    // ---------------- 语句 ----------------
    private void GenBlock(BlockStmt block, bool declareScope = true)
    {
        foreach (var item in block.Items)
        {
            switch (item)
            {
                case Stmt s: GenStmt(s); break;
                case VarDecl v: GenLocalVar(v); break;
                case DeclGroup dg:
                    foreach (var dd in dg.Decls)
                        if (dd is VarDecl vdg) GenLocalVar(vdg);
                    break;
                case TypedefDecl: case StructDecl: case EnumDecl: break;
            }
        }
    }

    private void GenStmt(Stmt s)
    {
        switch (s)
        {
            case BlockStmt b: GenBlock(b); break;
            case ExprStmt es: if (es.Expr != null) GenValue(es.Expr); break;
            case NullStmt: break;
            case DeclStmt ds: GenLocalVar(ds.Decl); break;
            case IfStmt i:
                {
                    GenValue(i.Cond);
                    int elseL = _e.NewLabel(), endL = _e.NewLabel();
                    _e.Cbz(X0, elseL);
                    GenStmt(i.Then);
                    _e.B(endL);
                    _e.MarkLabel(elseL);
                    if (i.Else != null) GenStmt(i.Else);
                    _e.MarkLabel(endL);
                    break;
                }
            case WhileStmt w:
                {
                    int begin = _e.NewLabel(), end = _e.NewLabel();
                    _loops.Push((begin, end));
                    _e.MarkLabel(begin);
                    GenValue(w.Cond);
                    _e.Cbz(X0, end);
                    GenStmt(w.Body);
                    _e.B(begin);
                    _e.MarkLabel(end);
                    _loops.Pop();
                    break;
                }
            case DoWhileStmt dw:
                {
                    int begin = _e.NewLabel(), cont = _e.NewLabel(), end = _e.NewLabel();
                    _loops.Push((cont, end));
                    _e.MarkLabel(begin);
                    GenStmt(dw.Body);
                    _e.MarkLabel(cont);
                    GenValue(dw.Cond);
                    _e.Cbnz(X0, begin);
                    _e.MarkLabel(end);
                    _loops.Pop();
                    break;
                }
            case ForStmt fo:
                {
                    int begin = _e.NewLabel(), cont = _e.NewLabel(), end = _e.NewLabel();
                    if (fo.Init is VarDecl fv) GenLocalVar(fv);
                    else if (fo.Init is Expr fe) GenValue(fe);
                    _loops.Push((cont, end));
                    _e.MarkLabel(begin);
                    if (fo.Cond != null) { GenValue(fo.Cond); _e.Cbz(X0, end); }
                    GenStmt(fo.Body);
                    _e.MarkLabel(cont);
                    if (fo.Update != null) GenValue(fo.Update);
                    _e.B(begin);
                    _e.MarkLabel(end);
                    _loops.Pop();
                    break;
                }
            case ReturnStmt rs:
                if (rs.Value != null) GenValue(rs.Value);
                _e.B(_funcEnd);
                break;
            case BreakStmt:
                if (_switchEnd.Count > 0 && _loops.Count == 0) _e.B(_switchEnd.Peek());
                else if (_loops.Count > 0) _e.B(_loops.Peek().brk);
                break;
            case ContinueStmt:
                if (_loops.Count > 0) _e.B(_loops.Peek().cont);
                break;
            case SwitchStmt sw: GenSwitch(sw); break;
            case LabelStmt ls: GenStmt(ls.Body); break;
            case GotoStmt: break; // v1 不实现 goto
            case CaseStmt: break; // 由 GenSwitch 处理
        }
    }

    private void GenSwitch(SwitchStmt sw)
    {
        GenValue(sw.Expr);
        int end = _e.NewLabel();
        _switchEnd.Push(end);
        var caseLabels = new List<int>();
        int defaultLabel = -1;
        foreach (var cs in sw.Cases)
        {
            int lab = _e.NewLabel();
            caseLabels.Add(lab);
            if (cs.IsDefault) defaultLabel = lab;
            else
            {
                long val = TryConst(cs.Value) ?? 0;
                _e.CmpImm(X0, val);
                _e.Bcc(ACond.EQ, lab);
            }
        }
        _e.B(defaultLabel >= 0 ? defaultLabel : end);
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            _e.MarkLabel(caseLabels[i]);
            _loops.Push((0, end)); // break 上下文（continue 不可用）
            foreach (var st in sw.Cases[i].Body) GenStmt(st);
            _loops.Pop();
        }
        _e.MarkLabel(end);
        _switchEnd.Pop();
    }

    // ---------------- 局部变量初始化 ----------------
    private void GenLocalVar(VarDecl v)
    {
        if (v.IsTypedef) return;
        if (v.Init == null) return;
        int off = v.Sym?.Offset ?? v.StackOffset;
        var type = v.Type;

        if (type is ArrayType at)
        {
            int baseOff = off;
            int es = SizeOf(at.Element);
            if (v.Init is StringLiteral sl && at.Element is IntegerType { Kind: IntKind.Char })
            {
                var b = System.Text.Encoding.UTF8.GetBytes(sl.Value);
                for (int i = 0; i < at.Length; i++)
                {
                    long val = i < b.Length ? b[i] : 0;
                    StoreConstToStack(baseOff + i, val, at.Element);
                }
            }
            else if (v.Init is InitListExpr il)
            {
                for (int i = 0; i < at.Length; i++)
                {
                    long val = i < il.Elements.Count ? (TryConst(il.Elements[i]) ?? 0) : 0;
                    StoreConstToStack(baseOff + i * es, val, at.Element);
                }
            }
        }
        else if (type is StructType st)
        {
            if (v.Init is InitListExpr il)
            {
                for (int i = 0; i < st.Fields.Count; i++)
                {
                    var fld = st.Fields[i];
                    if (i < il.Elements.Count)
                    {
                        long val = TryConst(il.Elements[i]) ?? 0;
                        StoreConstToStack(off + fld.Offset, val, fld.Type);
                    }
                }
            }
        }
        else
        {
            // 标量
            if (v.Init is StringLiteral slp && type is PointerType)
            {
                string ssym = GetOrAddStringSym(slp.Value);
                GenSymbolAddr(X0, ssym);
                StoreTyped(Arm64Mem.BaseDisp(X29, off), X0, type);
            }
            else
            {
                GenValue(v.Init);
                StoreTyped(Arm64Mem.BaseDisp(X29, off), X0, type);
            }
        }
    }

    private void StoreConstToStack(int off, long val, CType t)
    {
        int sz = SizeOf(t);
        _e.MovImm(X0, val);
        _e.Store(X0, Arm64Mem.BaseDisp(X29, off), sz);
    }

    // ==================== 表达式：地址 / 值 ====================
    private void GenAddr(Expr e)
    {
        switch (e)
        {
            case IdentifierRef id:
                if (id.Sym == null) { _e.MovImm(X0, 0); break; }
                if (id.Sym.IsGlobal) GenSymbolAddr(X0, id.Name);
                else _e.AddImm(X0, X29, id.Sym.Offset);
                break;
            case IndexExpr ix:
                if (ix.Array.Type is ArrayType) GenAddr(ix.Array);
                else GenValue(ix.Array);
                PushTmp64();
                GenValue(ix.Index);
                PopTmp64To(X1);
                ScaleIndex(X0, X1, ElementType(ix.Array.Type));
                break;
            case MemberExpr m:
                {
                    CType baseType = m.Arrow ? (m.Expr.Type is PointerType pt ? pt.Element : m.Expr.Type) : m.Expr.Type!;
                    var (_, foff) = FieldInfo(baseType, m.Name);
                    if (m.Arrow) GenValue(m.Expr); else GenAddr(m.Expr);
                    if (foff != 0) _e.AddImm(X0, X0, foff);
                    break;
                }
            case UnaryExpr u when u.Op == TokenKind.Star:
                GenValue(u.Operand);
                break;
            default:
                _e.MovImm(X0, 0);
                break;
        }
    }

    private void ScaleIndex(Arm64Reg idx, Arm64Reg bse, CType elemType)
    {
        int es = SizeOf(elemType);
        if (es == 1) _e.Add(X0, bse, idx);
        else if (es == 2 || es == 4 || es == 8)
        {
            _e.LslImm(X0, idx, es == 2 ? 1 : es == 4 ? 2 : 3);
            _e.Add(X0, X0, bse);
        }
        else { _e.MovImm(X2, es); _e.Mul(X0, idx, X2); _e.Add(X0, X0, bse); }
    }

    private void GenValue(Expr e)
    {
        switch (e)
        {
            case IntLiteral il: _e.MovImm(X0, il.Value); break;
            case CharLiteral cl: _e.MovImm(X0, cl.Value); break;
            case StringLiteral sl:
                {
                    string sym = _stringSyms.TryGetValue(sl.StringId, out var s) ? s : GetOrAddStringSym(sl.Value);
                    GenSymbolAddr(X0, sym); break;
                }
            case IdentifierRef id: GenIdentifierValue(id); break;
            case UnaryExpr u: GenUnary(u); break;
            case BinaryExpr b: GenBinary(b); break;
            case AssignExpr a: GenAssign(a); break;
            case ConditionalExpr c: GenConditional(c); break;
            case CallExpr c: GenCall(c); break;
            case IndexExpr ix: GenAddr(ix); LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), ElementType(ix.Array.Type)); break;
            case MemberExpr m:
                {
                    GenAddr(m);
                    CType baseType = m.Arrow ? (m.Expr.Type is PointerType pt ? pt.Element : m.Expr.Type) : m.Expr.Type!;
                    var (fld, _) = FieldInfo(baseType, m.Name);
                    if (fld == null) { _e.MovImm(X0, 0); break; }
                    if (fld.IsBitField) { GenBitFieldRead(fld); break; }
                    if (fld.Type is ArrayType or StructType) break; // 衰减为地址
                    LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), fld.Type);
                    break;
                }
            case CastExpr ce:
                GenValue(ce.Operand);
                ExtendTo(ce.TargetType);
                break;
            case SizeofExpr sz:
                _e.MovImm(X0, sz.SizeOfType != null ? SizeOf(sz.SizeOfType) : (sz.Expr?.Type != null ? SizeOf(sz.Expr.Type) : 0));
                break;
            case CommaExpr cm: GenValue(cm.Left); GenValue(cm.Right); break;
            case StmtExpr se: GenBlock(se.Body); break; // 末表达式值已在 X0
            default: _e.MovImm(X0, 0); break;
        }
    }

    private void GenIdentifierValue(IdentifierRef id)
    {
        var sym = id.Sym;
        if (sym == null) { _e.MovImm(X0, 0); return; }
        if (sym.Kind == SymKind.Func) { GenSymbolAddr(X0, id.Name); return; }
        if (sym.Type is ArrayType or StructType)
        {
            if (sym.IsGlobal) GenSymbolAddr(X0, id.Name);
            else _e.AddImm(X0, X29, sym.Offset);
            return;
        }
        // 标量
        if (sym.IsGlobal) { GenSymbolAddr(X9, id.Name); LoadTyped(X0, Arm64Mem.BaseDisp(X9, 0), sym.Type); }
        else LoadTyped(X0, Arm64Mem.BaseDisp(X29, sym.Offset), sym.Type);
    }

    private void GenUnary(UnaryExpr u)
    {
        switch (u.Op)
        {
            case TokenKind.Amp:
                GenAddr(u.Operand);
                break;
            case TokenKind.Star:
                GenValue(u.Operand);
                LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), u.Type);
                break;
            case TokenKind.Minus:
                GenValue(u.Operand); _e.Neg(X0, X0); break;
            case TokenKind.Plus:
                GenValue(u.Operand); break;
            case TokenKind.Tilde:
                GenValue(u.Operand); _e.Mvn(X0, X0); break;
            case TokenKind.Not:
                GenValue(u.Operand);
                _e.Tst(X0, X0);
                _e.Cset(ACond.EQ, X0);
                break;
            case TokenKind.PlusPlus:
            case TokenKind.MinusMinus:
                GenIncDec(u);
                break;
            default:
                GenValue(u.Operand);
                break;
        }
    }

    private void GenIncDec(UnaryExpr u)
    {
        GenAddr(u.Operand);          // X0 = 地址
        PushTmp64();                 // 保存地址（16 对齐槽）
        LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), u.Operand.Type);  // X0 = 旧值
        long step = (u.Operand.Type is PointerType pt) ? SizeOf(pt.Element) : 1;
        if (!u.Prefix) _e.MovRR(X10, X0); // 后置：X10 保存旧值用于返回（此函数内无 call，X10 安全）
        if (u.Op == TokenKind.PlusPlus) _e.AddImm(X0, X0, step); else _e.SubImm(X0, X0, step);
        PopTmp64To(X1);             // X1 = 地址
        StoreTyped(Arm64Mem.BaseDisp(X1, 0), X0, u.Operand.Type);
        if (!u.Prefix) _e.MovRR(X0, X10);  // 后置返回旧值
    }

    private void GenBinary(BinaryExpr b)
    {
        // 指针算术（数组在此衰减为指针）
        if (b.Op == TokenKind.Plus && IsPtrLike(b.Left.Type) && b.Right.Type!.IsInteger) { GenPtrArith(b.Left, b.Right, +1); return; }
        if (b.Op == TokenKind.Plus && IsPtrLike(b.Right.Type) && b.Left.Type!.IsInteger) { GenPtrArith(b.Right, b.Left, +1); return; }
        if (b.Op == TokenKind.Minus && IsPtrLike(b.Left.Type) && b.Right.Type!.IsInteger) { GenPtrArith(b.Left, b.Right, -1); return; }
        if (b.Op == TokenKind.Minus && IsPtrLike(b.Left.Type) && IsPtrLike(b.Right.Type)) { GenPtrDiff(b.Left, b.Right); return; }

        bool cmp = IsComparison(b.Op);
        bool commutative = b.Op is TokenKind.Plus or TokenKind.Star or TokenKind.Amp or TokenKind.Pipe or TokenKind.Caret;

        if (b.Op == TokenKind.AndAnd) { GenLogicalAnd(b); return; }
        if (b.Op == TokenKind.OrOr) { GenLogicalOr(b); return; }

        if (cmp)
        {
            GenValue(b.Left); PushTmp64();
            GenValue(b.Right); PopTmp64To(X1);   // left=X1, right=X0
            _e.Cmp(X1, X0);
            ACond cc = CmpCond(b.Op, IsUnsigned(b.Left.Type!));
            _e.Cset(cc, X0);
            return;
        }

        if (commutative)
        {
            GenValue(b.Left); PushTmp64();
            GenValue(b.Right); PopTmp64To(X1);   // left=X1, right=X0
            switch (b.Op)
            {
                case TokenKind.Plus: _e.Add(X0, X0, X1); break;
                case TokenKind.Star: _e.Mul(X0, X0, X1); break;
                case TokenKind.Amp: _e.And(X0, X0, X1); break;
                case TokenKind.Pipe: _e.Orr(X0, X0, X1); break;
                case TokenKind.Caret: _e.Eor(X0, X0, X1); break;
            }
            return;
        }

        // 非交换：right 先求值入 X1，left 入 X0
        GenValue(b.Right); _e.MovRR(X1, X0);
        GenValue(b.Left);  // X0 = left, X1 = right
        switch (b.Op)
        {
            case TokenKind.Minus: _e.Sub(X0, X0, X1); break;
            case TokenKind.Slash:
                if (IsUnsigned(b.Left.Type!)) _e.Udiv(X0, X0, X1);
                else _e.Sdiv(X0, X0, X1);
                break;
            case TokenKind.Percent:
                if (IsUnsigned(b.Left.Type!)) { _e.Udiv(X2, X0, X1); _e.Msub(X0, X1, X2, X0); }
                else { _e.Sdiv(X2, X0, X1); _e.Msub(X0, X1, X2, X0); }
                break;
            case TokenKind.Shl: _e.LslReg(X0, X0, X1); break;
            case TokenKind.Shr: if (IsUnsigned(b.Left.Type!)) _e.LsrReg(X0, X0, X1); else _e.AsrReg(X0, X0, X1); break;
        }
    }

    private void GenPtrArith(Expr ptr, Expr idx, int sign)
    {
        GenValue(ptr); PushTmp64();
        GenValue(idx);                 // X0 = index
        int es = SizeOf(ElementType(ptr.Type!));   // 支持 PointerType 与 ArrayType（已衰减）
        _e.MovImm(X2, es);
        _e.Mul(X0, X0, X2);
        PopTmp64To(X1);               // X1 = ptr
        if (sign > 0) _e.Add(X0, X0, X1); else { _e.Sub(X1, X1, X0); _e.MovRR(X0, X1); }
    }
    private void GenPtrDiff(Expr l, Expr r)
    {
        GenValue(l); PushTmp64();
        GenValue(r); PopTmp64To(X1);      // left=X1, right=X0
        _e.Sub(X1, X1, X0); _e.MovRR(X0, X1);
        int es = SizeOf(ElementType(l.Type!));
        if (es != 1) { _e.MovImm(X2, es); _e.Sdiv(X0, X0, X2); }
    }

    private void GenLogicalAnd(BinaryExpr b)
    {
        int falseL = _e.NewLabel(), endL = _e.NewLabel();
        GenValue(b.Left); _e.Cbz(X0, falseL);
        GenValue(b.Right); _e.Cbz(X0, falseL);
        _e.MovImm(X0, 1); _e.B(endL);
        _e.MarkLabel(falseL); _e.MovImm(X0, 0);
        _e.MarkLabel(endL);
    }
    private void GenLogicalOr(BinaryExpr b)
    {
        int trueL = _e.NewLabel(), endL = _e.NewLabel();
        GenValue(b.Left); _e.Cbnz(X0, trueL);
        GenValue(b.Right); _e.Cbnz(X0, trueL);
        _e.MovImm(X0, 0); _e.B(endL);
        _e.MarkLabel(trueL); _e.MovImm(X0, 1);
        _e.MarkLabel(endL);
    }

    // ==================== 位域 ====================
    /// <summary>若赋值目标为位域成员，返回其 Field；否则 null。</summary>
    private static Field? BitFieldOfTarget(Expr target)
    {
        if (target is MemberExpr m)
        {
            var bt = m.Arrow ? (m.Expr.Type is PointerType pt ? pt.Element : null) : m.Expr.Type;
            if (bt is StructType st)
            {
                var (f, _) = AstHelpers.FindField(st, m.Name);
                return f?.IsBitField == true ? f : null;
            }
        }
        return null;
    }

    /// <summary>读取位域：X0=单元地址 → X0=位域值（已符号/零扩展）。</summary>
    private void GenBitFieldRead(Field fld)
    {
        int w = fld.BitWidth, bo = fld.BitOffset;
        long mask = w >= 64 ? -1L : ((1L << w) - 1);
        LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), fld.Type);   // 单元值（按类型符号/零扩展）
        if (bo > 0) _e.LsrImm(X0, X0, bo);                   // 逻辑右移到单元起始位
        if (w < 64) { _e.MovImm(X2, mask); _e.And(X0, X0, X2); } // 隔离 W 位
        if (!IsUnsigned(fld.Type) && w < 64)                  // 有符号位域符号扩展
        { _e.LslImm(X0, X0, 64 - w); _e.AsrImm(X0, X0, 64 - w); }
    }

    /// <summary>位域赋值：target 为位域 MemberExpr，读-改-写单元，结果 X0=(value&mask)（按位域类型扩展）。</summary>
    private void GenBitFieldAssign(Expr target, Expr value, Field fld)
    {
        int w = fld.BitWidth, bo = fld.BitOffset;
        long mask = w >= 64 ? -1L : ((1L << w) - 1);
        long storeMask = mask << bo;
        GenValue(value);                 // X0 = value
        PushTmp64();                     // [value]  副本1（结果用）
        PushTmp64();                     // [value, value] 副本2（存储用）
        GenAddr(target);                 // X0 = &单元
        PushTmp64();                     // [v, v, &unit]
        PopTmp64To(X10);                 // X10 = &unit ; [v, v]
        PopTmp64To(X0);                  // X0 = value(副本2) ; [v]
        _e.MovImm(X2, mask); _e.And(X0, X0, X2);          // X0 = value & mask
        if (bo > 0) _e.LslImm(X0, X0, bo);                // X0 = (value&mask) << bo
        PushTmp64();                     // [v, shifted]
        _e.MovRR(X0, X10);
        LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), fld.Type); // X0 = 旧单元值
        _e.MovImm(X2, storeMask); _e.Mvn(X2, X2); _e.And(X0, X0, X2); // 清位域位
        PopTmp64To(X1);                  // X1 = shifted ; [v]
        _e.Orr(X0, X0, X1);              // X0 = 最终单元
        StoreTyped(Arm64Mem.BaseDisp(X10, 0), X0, fld.Type);
        // 结果 = value & mask（按位域类型扩展）
        PopTmp64To(X0);                  // X0 = value(副本1) ; []
        _e.MovImm(X2, mask); _e.And(X0, X0, X2);
        if (!IsUnsigned(fld.Type) && w < 64)
        { _e.LslImm(X0, X0, 64 - w); _e.AsrImm(X0, X0, 64 - w); }
    }

    private void GenAssign(AssignExpr a)
    {
        if (a.Op == TokenKind.Assign)
        {
            var bf = BitFieldOfTarget(a.Target);
            if (bf != null) { GenBitFieldAssign(a.Target, a.Value, bf); return; }
            GenAddr(a.Target); PushTmp64();
            GenValue(a.Value);           // X0 = value
            PopTmp64To(X1);             // X1 = addr
            StoreTyped(Arm64Mem.BaseDisp(X1, 0), X0, a.Target.Type!);
            return;
        }
        // 复合赋值
        GenAddr(a.Target); PushTmp64();                  // 保存地址
        LoadTyped(X0, Arm64Mem.BaseDisp(X0, 0), a.Target.Type!);  // 旧值
        PushTmp64();                                      // 保存旧值
        GenValue(a.Value);                                 // X0 = 右值
        PopTmp64To(X1);                                       // X1 = 旧值
        // 对于指针 +=/-=，右值需缩放
        TokenKind baseOp = a.Op switch
        {
            TokenKind.PlusAssign => TokenKind.Plus, TokenKind.MinusAssign => TokenKind.Minus,
            TokenKind.StarAssign => TokenKind.Star, TokenKind.SlashAssign => TokenKind.Slash,
            TokenKind.PercentAssign => TokenKind.Percent, TokenKind.ShlAssign => TokenKind.Shl,
            TokenKind.ShrAssign => TokenKind.Shr, TokenKind.AndAssign => TokenKind.Amp,
            TokenKind.OrAssign => TokenKind.Pipe, TokenKind.XorAssign => TokenKind.Caret, _ => a.Op
        };
        if (a.Target.Type is PointerType pt && (baseOp == TokenKind.Plus || baseOp == TokenKind.Minus))
        {
            _e.MovImm(X2, SizeOf(pt.Element)); _e.Mul(X0, X0, X2);
        }
        switch (baseOp)
        {
            case TokenKind.Plus: _e.Add(X0, X0, X1); break;
            case TokenKind.Minus: _e.Sub(X1, X1, X0); _e.MovRR(X0, X1); break;
            case TokenKind.Star: _e.Mul(X0, X0, X1); break;
            case TokenKind.Slash:
                _e.MovRR(X2, X0); _e.MovRR(X0, X1);
                if (IsUnsigned(a.Target.Type!)) _e.Udiv(X0, X0, X2); else _e.Sdiv(X0, X0, X2);
                break;
            case TokenKind.Percent:
                _e.MovRR(X2, X0); _e.MovRR(X0, X1);
                if (IsUnsigned(a.Target.Type!)) { _e.Udiv(X3, X0, X2); _e.Msub(X0, X2, X3, X0); }
                else { _e.Sdiv(X3, X0, X2); _e.Msub(X0, X2, X3, X0); }
                break;
            case TokenKind.Shl: _e.MovRR(X2, X0); _e.MovRR(X0, X1); _e.MovRR(X1, X2); _e.LslReg(X0, X0, X1); break;
            case TokenKind.Shr: _e.MovRR(X2, X0); _e.MovRR(X0, X1); _e.MovRR(X1, X2); if (IsUnsigned(a.Target.Type!)) _e.LsrReg(X0, X0, X1); else _e.AsrReg(X0, X0, X1); break;
            case TokenKind.Amp: _e.And(X0, X0, X1); break;
            case TokenKind.Pipe: _e.Orr(X0, X0, X1); break;
            case TokenKind.Caret: _e.Eor(X0, X0, X1); break;
        }
        PopTmp64To(X1);                                       // 地址
        StoreTyped(Arm64Mem.BaseDisp(X1, 0), X0, a.Target.Type!);
    }

    private void GenConditional(ConditionalExpr c)
    {
        int elseL = _e.NewLabel(), endL = _e.NewLabel();
        GenValue(c.Cond); _e.Cbz(X0, elseL);
        GenValue(c.Then); _e.B(endL);
        _e.MarkLabel(elseL); GenValue(c.Else);
        _e.MarkLabel(endL);
    }

    // ---- 栈临时保存（保持 AAPCS64 的 16 字节栈对齐）----
    // 用 sub/add sp,16 代替 str/ldp：每次保存占用 16 字节槽，
    // 保证在任意嵌套表达式求值中 sp 始终 16 字节对齐（call 前 sp%16==0）。
    private void PushTmp64() { _e.SubSp(16); _e.Store(X0, Arm64Mem.BaseDisp(SP, 0), 8); }
    private void PopTmp64To(Arm64Reg r) { _e.Load(r, Arm64Mem.BaseDisp(SP, 0), 8, false); _e.AddSp(16); }

    // ---- 内存复制：[X10] -> [X11]，size 字节，临时用 X0/X2/X3/X4。
    // 仅破坏 X0/X2/X3/X4/X10/X11（均 caller-saved），不碰 X1/X5..X7 等参数寄存器，
    // 故可在函数 prologue 与 GenCallArgs 中安全使用。用于结构体按值传参。----
    private void CopyBytesRegReg(int size)
    {
        int qwords = size / 8;
        if (qwords > 0)
        {
            _e.MovImm(X2, qwords);
            int loop = _e.NewLabel(), end = _e.NewLabel();
            _e.MarkLabel(loop);
            _e.Cbz(X2, end);
            _e.SubImm(X2, X2, 1);
            _e.Load(X0, Arm64Mem.BaseIndex(X10, X2, 3), 8, false);   // LDR X0,[X10,X2,LSL #3]
            _e.Store(X0, Arm64Mem.BaseIndex(X11, X2, 3), 8);
            _e.B(loop);
            _e.MarkLabel(end);
        }
        int rem = size % 8;
        if (rem > 0)
        {
            int baseOff = qwords * 8;
            _e.AddImm(X3, X10, baseOff);   // X3 = src + baseOff
            _e.AddImm(X4, X11, baseOff);   // X4 = dst + baseOff
            _e.MovImm(X2, rem);
            int loop = _e.NewLabel(), end = _e.NewLabel();
            _e.MarkLabel(loop);
            _e.Cbz(X2, end);
            _e.SubImm(X2, X2, 1);
            _e.Load(X0, Arm64Mem.BaseIndex(X3, X2, 0), 1, false);    // LDRB X0,[X3,X2]
            _e.Store(X0, Arm64Mem.BaseIndex(X4, X2, 0), 1);
            _e.B(loop);
            _e.MarkLabel(end);
        }
    }

    // 内置函数列表：这些函数在静态链接模式下由运行时提供
    private static readonly HashSet<string> BuiltinFuncs = new()
    {
        "printf", "puts", "putchar", "exit", "malloc", "free",
        "strlen", "memcpy", "memset", "syscall_write", "syscall_exit",
        "syscall_brk", "syscall_read"
    };

    // ---------------- 函数调用 ----------------
    private void GenCall(CallExpr c)
    {
        // printf 颜色集成：拦截 printf/fprintf/sprintf，提取编译期 color 常量参数，
        // 用 ANSI 24-bit 转义码包装格式串，并从参数列表移除颜色参数。
        List<Expr> args = c.Args;
        if (c.Callee is IdentifierRef cid && (cid.Name == "printf" || cid.Name == "fprintf" || cid.Name == "sprintf"))
        {
            var colored = TryColorPrintfArgs(c, cid.Name);
            if (colored != null) args = colored;
        }

        if (c.Callee is IdentifierRef id && id.Sym is { Kind: SymKind.Func } sym)
        {
            int outBytes = GenCallArgs(args);
            bool isBuiltin = BuiltinFuncs.Contains(sym.Name);
            // 内置函数强制使用 Call26 (直接调用)
            bool ext = !isBuiltin && (sym.IsExtern || !sym.IsDefined);
            if (ext) _img.AddExternal(sym.Name); // 仅实际调用的 extern 函数才导入
            // AAPCS64 无 AL=0 约定（不像 x86 SysV 变参需设 AL），直接 BL
            int off = _e.Bl();
            _img.AddFixup(_img.Text, off, ext ? FixupKind.ExtCall26 : FixupKind.Call26, sym.Name);
            if (outBytes != 0) _e.AddSp(outBytes);
        }
        else
        {
            // 函数指针
            GenValue(c.Callee); PushTmp64();
            int outBytes = GenCallArgs(args);
            _e.Load(X0, Arm64Mem.BaseDisp(SP, outBytes), 8, false); // 取回 callee（outBytes 为 16 对齐，槽对齐）
            _e.Blr(X0);
            if (outBytes != 0) _e.AddSp(outBytes);
            _e.AddSp(16); // 弹出 callee（16 对齐槽）
        }
    }

    /// <summary>若 printf/fprintf/sprintf 调用含编译期 color 常量参数，返回替换后的参数列表
    /// （格式串被 ANSI 转义码包装，颜色参数被移除）；否则返回 null。</summary>
    private List<Expr>? TryColorPrintfArgs(CallExpr c, string name)
    {
        int fmtIdx = name == "printf" ? 0 : 1;  // fprintf/sprintf 格式串在第二参数
        if (c.Args.Count <= fmtIdx) return null;
        if (c.Args[fmtIdx] is not StringLiteral fmtSl) return null;

        bool hasColor = false;
        long firstColor = 0;
        var newArgs = new List<Expr>();
        for (int i = 0; i < c.Args.Count; i++)
        {
            if (i == fmtIdx) { newArgs.Add(fmtSl); continue; }  // 占位，循环后替换
            var cv = TryConst(c.Args[i]);
            if (cv.HasValue && ColorNames.IsColorValue(cv.Value))
            {
                if (!hasColor) { firstColor = cv.Value; hasColor = true; }
                // 丢弃颜色参数（它是修饰符，不对应 % 占位符）
            }
            else
            {
                newArgs.Add(c.Args[i]);
            }
        }
        if (!hasColor) return null;

        // 构造 ANSI 24-bit 前景色包装的新格式串：\x1b[38;2;R;G;Bm<fmt>\x1b[0m
        var (r, g, b) = ColorNames.Unpack(firstColor);
        string newFmt = "\x1b[38;2;" + r + ";" + g + ";" + b + "m" + fmtSl.Value + "\x1b[0m";
        // StringId = -1 保证不与 Sema 分配的 StringId（0..N-1）冲突，
        // GenValue 会走 GetOrAddStringSym(newFmt) 注册新字符串。
        newArgs[fmtIdx] = new StringLiteral(newFmt) { StringId = -1 };
        return newArgs;
    }

    private int GenCallArgs(List<Expr> args)
    {
        int n = args.Count;
        int regCount = Math.Min(n, _abi.IntArgRegs.Length);  // X0..X7
        int nStack = n - regCount;
        int shadow = _abi.ShadowSpace;   // AAPCS64 = 0

        // 结构体按值传递：在栈上开副本区，参数槽传副本指针（占一个槽，符合 ABI）。
        var structOff = new Dictionary<int, int>(); // argIndex -> 副本相对 sp 偏移
        int structCopyBytes = 0;
        for (int i = 0; i < n; i++)
        {
            if (args[i].Type is StructType)
            {
                int sz = SizeOf(args[i].Type!);
                structOff[i] = structCopyBytes;
                structCopyBytes += AlignUp(sz, 8);
            }
        }

        int outBytes = AlignUp(shadow + nStack * 8 + structCopyBytes, 16);
        if (outBytes != 0) _e.SubSp(outBytes);

        int copyBase = shadow + nStack * 8;

        // 先复制结构体内容到副本区（求值顺序：结构体在标量之前，避免标量寄存器被 CopyBytes 破坏）
        foreach (var kv in structOff)
        {
            GenAddr(args[kv.Key]);                  // X0 = 结构体地址
            int sz = SizeOf(args[kv.Key].Type!);
            _e.MovRR(X10, X0);
            _e.AddImm(X11, SP, copyBase + kv.Value);
            CopyBytesRegReg(sz);
        }

        // 求值每个参数存到参数槽（结构体参数存副本指针）
        for (int i = 0; i < n; i++)
        {
            if (args[i].Type is StructType)
                _e.AddImm(X0, SP, copyBase + structOff[i]);
            else
                GenValue(args[i]); // X0 = arg
            int slot = i < regCount ? i * 8 : shadow + (i - regCount) * 8;
            _e.Store(X0, Arm64Mem.BaseDisp(SP, slot), 8);
        }
        // 把寄存器参数从溢出槽载入对应寄存器
        for (int i = 0; i < regCount; i++)
        {
            var r = AR.Of64((byte)i);
            _e.Load(r, Arm64Mem.BaseDisp(SP, i * 8), 8, false);
        }
        return outBytes;
    }

    // ==================== 类型 / 编码辅助 ====================
    private void LoadTyped(Arm64Reg dst, Arm64Mem m, CType t)
    {
        int sz = SizeOf(t); bool uns = IsUnsigned(t);
        _e.Load(dst, m, sz, !uns);
    }
    private void StoreTyped(Arm64Mem m, Arm64Reg src, CType t)
    {
        int sz = SizeOf(t);
        _e.Store(src, m, sz);
    }
    /// <summary>物化符号地址到寄存器：发射 ADRP+ADD 并记 AdrpAdd fixup（写入器回填页/偏移）。</summary>
    private void GenSymbolAddr(Arm64Reg dst, string sym)
    {
        int off = _e.AdrpAdd(dst, sym);
        _img.AddFixup(_img.Text, off, FixupKind.AdrpAdd, sym, 0);
    }
    private void ExtendTo(CType t)
    {
        int sz = SizeOf(t);
        bool uns = IsUnsigned(t);
        // 当前 X0 持有 64 位值；按目标宽度符号/零扩展
        if (sz == 1 || sz == 2 || sz == 4) _e.Extend(X0, X0, sz, uns);
    }

    private static int SizeOf(CType t) => t switch
    {
        ArrayType at => at.Element.Size * (int)at.Length,
        StructType st => st.Size,
        VoidType => 1,
        _ => t.Size
    };
    private static bool IsUnsigned(CType t) => t is IntegerType it ? it.Unsigned : (t is EnumType ? false : true);
    private static CType ElementType(CType t) => t is ArrayType at ? at.Element : (t is PointerType pt ? pt.Element : t);
    private static bool IsPtrLike(CType? t) => t is PointerType or ArrayType;
    private static readonly IntegerType FallbackInt = new(IntKind.Int, false, 4, 4);
    /// <summary>递归查找字段（含匿名成员），返回 (字段, 累加偏移)。未找到 field=null。</summary>
    private static (Field? field, int offset) FieldInfo(CType structType, string name)
        => structType is StructType st ? AstHelpers.FindField(st, name) : (null, 0);
    private static bool IsComparison(TokenKind k) => k is TokenKind.Eq or TokenKind.NotEq or TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge;
    private static ACond CmpCond(TokenKind op, bool uns) => (op, uns) switch
    {
        (TokenKind.Eq, _) => ACond.EQ, (TokenKind.NotEq, _) => ACond.NE,
        (TokenKind.Lt, false) => ACond.LT, (TokenKind.Le, false) => ACond.LE,
        (TokenKind.Gt, false) => ACond.GT, (TokenKind.Ge, false) => ACond.GE,
        (TokenKind.Lt, true) => ACond.CC, (TokenKind.Le, true) => ACond.LS,
        (TokenKind.Gt, true) => ACond.HI, (TokenKind.Ge, true) => ACond.CS,
        _ => ACond.EQ
    };
    private static int AlignUp(int v, int a) => a <= 1 ? v : (v + a - 1) & ~(a - 1);
}
