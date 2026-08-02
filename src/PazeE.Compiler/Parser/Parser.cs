using PazeE.Compiler.Lexer;

namespace PazeE.Compiler.Parser;

public sealed class Parser
{
    private readonly List<Token> _toks;
    private int _i;
    public Diagnostics Diag { get; }
    private readonly TargetInfo _target;
    private readonly HashSet<string> _typedefs = new();
    private readonly Dictionary<string, CType> _typedefTypes = new();
    private readonly Dictionary<(string, bool), StructType> _structs = new();
    private readonly Dictionary<string, long> _enumConstants = new();

    public Parser(List<Token> tokens, Diagnostics diag, TargetInfo target) { _toks = tokens; Diag = diag; _target = target; }

    private int _peekCount;
    private static readonly Token EofSentinel = new(TokenKind.Eof, "", SourceRange.Empty);
    private Token Peek(int o = 0)
    {
        if (++_peekCount > 5_000_000)
        {
            var st = Environment.StackTrace;
            var lines = st.Split('\n').Take(12);
            throw new InvalidOperationException($"Parser 卡住：i={_i}/{_toks.Count}, token='{(_i < _toks.Count ? _toks[_i].Text : "<eof>")}'\n堆栈:\n{string.Join('\n', lines)}");
        }
        return _i + o < _toks.Count ? _toks[_i + o] : EofSentinel;
    }
    private bool Check(TokenKind k) { if (Peek().Kind == k) { _i++; return true; } return false; }
    private Token Expect(TokenKind k, string what)
    {
        if (Peek().Kind != k) { Error(Peek().Range, $"期望 {what}，但遇到 '{Peek().Text}'"); return Peek(); }
        return _toks[_i++];
    }
    private void Error(SourceRange r, string m) => Diag.Error(r, m);
    private bool IsEllipsis() => Peek().Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Dot && Peek(2).Kind == TokenKind.Dot;
    private void ConsumeEllipsis() { _i += 3; }

    public TranslationUnit Parse()
    {
        var unit = new TranslationUnit();
        while (Peek().Kind != TokenKind.Eof)
        {
            if (Peek().Kind == TokenKind.Semicolon) { _i++; continue; }
            var d = ParseExternalDecl();
            if (d != null) unit.Decls.Add(d);
            else if (Peek().Kind != TokenKind.Eof) _i++;
        }
        return unit;
    }

    public sealed class _DeclGroupMarker { } // 占位（DeclGroup 已移至 Ast.cs 顶层）

    // ============ 外部声明 ============
    private Decl? ParseExternalDecl()
    {
        var range = Peek().Range;
        bool isTypedef = false;
        StorageClass sc = StorageClass.None;
        if (!ParseDeclSpecifiers(out var base_, ref sc, ref isTypedef, out var structDecl, out var enumDecl))
            return null;

        if (Peek().Kind == TokenKind.Semicolon)
        {
            _i++;
            if (structDecl != null) return structDecl;
            if (enumDecl != null) return enumDecl;
            return null;
        }

        var (type, name) = ParseDeclarator(base_!);
        if (name.Length == 0) Error(range, "声明缺少标识符");

        if (Peek().Kind == TokenKind.LBrace && type is FunctionType ftDef)
            return MakeFunctionDecl(base_!, name, ftDef, sc, range, ParseBlock());

        // 声明（可能是函数声明或变量声明，逗号分隔）
        var first = MakeDecl(type, name, sc, isTypedef, range);
        if (first is VarDecl vd) vd.Init = ParseInitValue();
        var decls = new List<Decl> { first };
        while (Check(TokenKind.Comma))
        {
            var (t2, n2) = ParseDeclarator(base_!);
            var d2 = MakeDecl(t2, n2, sc, isTypedef, range);
            if (d2 is VarDecl vd2) vd2.Init = ParseInitValue();
            decls.Add(d2);
        }
        Expect(TokenKind.Semicolon, ";");
        return decls.Count == 1 ? decls[0] : new DeclGroup(decls);
    }

    private Decl MakeDecl(CType type, string name, StorageClass sc, bool isTypedef, SourceRange r)
    {
        if (isTypedef) { _typedefs.Add(name); _typedefTypes[name] = type; return new TypedefDecl(name, type, r); }
        if (type is FunctionType ft) return MakeFunctionDecl(ft.Return, name, ft, sc, r, null);
        return new VarDecl(type, name, r) { Storage = sc };
    }

    private FunctionDecl MakeFunctionDecl(CType ret, string name, FunctionType ft, StorageClass sc, SourceRange r, BlockStmt? body)
    {
        var decl = new FunctionDecl(ret, name, r) { Storage = sc, Variadic = ft.Variadic, Body = body };
        foreach (var p in ft.Params) decl.Params.Add(new Param(p.Type, p.Name ?? ""));
        return decl;
    }

    // ============ 声明说明符 ============
    private bool ParseDeclSpecifiers(out CType? base_, ref StorageClass sc, ref bool isTypedef,
        out StructDecl? structDecl, out EnumDecl? enumDecl)
    {
        base_ = null; structDecl = null; enumDecl = null;
        bool unsig = false;
        IntKind? kind = null;
        int longCount = 0;
        bool any = false;

        while (true)
        {
            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.KwExtern: sc = StorageClass.Extern; _i++; any = true; continue;
                case TokenKind.KwStatic: sc = StorageClass.Static; _i++; any = true; continue;
                case TokenKind.KwRegister: case TokenKind.KwAuto: case TokenKind.KwVolatile: case TokenKind.KwSigned:
                    _i++; any = true; continue;
                case TokenKind.KwUnsigned: unsig = true; _i++; any = true; continue;
                case TokenKind.KwConst: _i++; any = true; continue;
                case TokenKind.KwBool: base_ = new IntegerType(IntKind.Char, true, _target.CharSize, _target.CharAlign); _i++; any = true; continue;
                case TokenKind.KwVoid: base_ = VoidType.Instance; _i++; any = true; continue;
                case TokenKind.KwChar: kind = IntKind.Char; _i++; any = true; continue;
                case TokenKind.KwShort: kind = IntKind.Short; _i++; any = true; continue;
                case TokenKind.KwInt: kind = IntKind.Int; _i++; any = true; continue;
                case TokenKind.KwLong: longCount++; _i++; any = true; continue;
                case TokenKind.KwTypedef: isTypedef = true; _i++; any = true; continue;
                case TokenKind.KwStruct: case TokenKind.KwUnion:
                    base_ = ParseStructOrUnion(t.Kind == TokenKind.KwUnion, out structDecl); any = true; continue;
                case TokenKind.KwEnum:
                    base_ = ParseEnum(out enumDecl); any = true; continue;
                case TokenKind.KwTypeof:
                    {
                        // typeof(expr) / typeof(type-name)：产生 TypeofType 占位类型，由 Sema.ResolveType 解析
                        _i++;
                        Expect(TokenKind.LParen, "'(' after typeof");
                        var tot = new TypeofType();
                        if (IsTypeName(Peek())) tot.TypeArg = ParseTypeName();
                        else tot.Expr = ParseExpression();
                        Expect(TokenKind.RParen, "')'");
                        base_ = tot; any = true; continue;
                    }
                default:
                    if (t.Kind == TokenKind.Identifier && _typedefs.Contains(t.Text))
                    { base_ = ResolveTypedef(t.Text); _i++; any = true; continue; }
                    goto done;
            }
            done: break;
        }

        if (base_ == null && (kind != null || longCount > 0 || unsig))
        {
            IntKind k = longCount >= 2 ? IntKind.Long : (kind ?? IntKind.Int);
            int size = k switch
            {
                IntKind.Char => _target.CharSize,
                IntKind.Short => _target.ShortSize,
                IntKind.Int => _target.IntSize,
                IntKind.Long => longCount >= 2 ? 8 : _target.LongSize,
                _ => _target.IntSize
            };
            int align = k switch
            {
                IntKind.Char => _target.CharAlign,
                IntKind.Short => _target.ShortAlign,
                IntKind.Int => _target.IntAlign,
                IntKind.Long => longCount >= 2 ? 8 : _target.LongAlign,
                _ => _target.IntAlign
            };
            base_ = new IntegerType(k, unsig, size, align);
        }
        if (base_ == null && !any) { Error(Peek().Range, "缺少类型说明符"); base_ = TypeFactory.Int(_target); return false; }
        if (base_ == null) { Error(Peek().Range, "声明缺少类型"); base_ = TypeFactory.Int(_target); }
        return true;
    }

    private CType ResolveTypedef(string name) => _typedefTypes.TryGetValue(name, out var t) ? t : TypeFactory.Int(_target);

    // ============ struct / union ============
    private CType ParseStructOrUnion(bool isUnion, out StructDecl? decl)
    {
        decl = null;
        var range = Peek().Range;
        _i++;
        string? tag = null;
        if (Peek().Kind == TokenKind.Identifier) { tag = Peek().Text; _i++; }
        var st = GetOrCreateStruct(tag, isUnion);
        if (Peek().Kind == TokenKind.LBrace)
        {
            _i++;
            var fieldDecls = new List<FieldDecl>();
            while (Peek().Kind != TokenKind.RBrace && Peek().Kind != TokenKind.Eof)
            {
                StorageClass sc = StorageClass.None; bool td = false;
                if (!ParseDeclSpecifiers(out var fbase, ref sc, ref td, out _, out _)) { _i++; continue; }
                if (Peek().Kind == TokenKind.Semicolon)
                {
                    _i++;
                    // 匿名 struct/union 成员：无声明符、且基类型为已定义的 StructType
                    if (fbase is StructType anonSt && anonSt.Complete)
                        fieldDecls.Add(new FieldDecl { Type = fbase, Name = "" });
                    continue;
                }
                do
                {
                    var (ft, fn) = ParseDeclarator(fbase!);
                    var fd = new FieldDecl { Type = ft, Name = fn };
                    if (Peek().Kind == TokenKind.Colon) { _i++; fd.BitWidth = ParseAssignment(); }
                    fieldDecls.Add(fd);
                } while (Check(TokenKind.Comma));
                Expect(TokenKind.Semicolon, ";");
            }
            Expect(TokenKind.RBrace, "}");

            decl = new StructDecl(tag, range) { IsUnion = isUnion };
            st.Fields.Clear();
            foreach (var fd in fieldDecls)
            {
                var fld = new Field { Name = fd.Name, Type = fd.Type };
                if (fd.BitWidth != null)
                {
                    fld.IsBitField = true;
                    fld.BitWidth = (int)EvalConst(fd.BitWidth);
                }
                st.Fields.Add(fld);
                decl.Fields.Add(fd);
            }
            st.Layout();
        }
        else if (tag != null && !st.Complete)
        {
            decl = new StructDecl(tag, range) { IsUnion = isUnion, IsForward = true };
        }
        return st;
    }

    private StructType GetOrCreateStruct(string? tag, bool isUnion)
    {
        if (tag != null && _structs.TryGetValue((tag, isUnion), out var s)) return s;
        var st = new StructType { Tag = tag, IsUnion = isUnion };
        if (tag != null) _structs[(tag, isUnion)] = st;
        return st;
    }

    // ============ enum ============
    private CType ParseEnum(out EnumDecl? decl)
    {
        decl = null;
        var range = Peek().Range;
        _i++;
        string? tag = null;
        if (Peek().Kind == TokenKind.Identifier) { tag = Peek().Text; _i++; }
        var et = new EnumType(_target.IntSize) { Tag = tag };
        if (Peek().Kind == TokenKind.LBrace)
        {
            _i++;
            decl = new EnumDecl(tag, range);
            long next = 0;
            while (Peek().Kind != TokenKind.RBrace && Peek().Kind != TokenKind.Eof)
            {
                if (Peek().Kind != TokenKind.Identifier) { _i++; continue; }
                string nm = Peek().Text; _i++;
                if (Check(TokenKind.Assign)) next = EvalConst(ParseAssignment());
                et.Constants[nm] = next;
                decl.Constants.Add((nm, null));
                _enumConstants[nm] = next;
                next++;
                if (!Check(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RBrace, "}");
        }
        return et;
    }

    private long EvalConst(Expr e) => e switch
    {
        IntLiteral il => il.Value,
        CharLiteral cl => cl.Value,
        UnaryExpr u => EvalUnaryConst(u),
        BinaryExpr b => EvalBinaryConst(b),
        _ => 0
    };
    private long EvalUnaryConst(UnaryExpr u)
    {
        long v = EvalConst(u.Operand);
        return u.Op switch { TokenKind.Minus => -v, TokenKind.Plus => v, TokenKind.Tilde => ~v, TokenKind.Not => v == 0 ? 1 : 0, _ => v };
    }
    private long EvalBinaryConst(BinaryExpr b)
    {
        long l = EvalConst(b.Left), r = EvalConst(b.Right);
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
            _ => 0
        };
    }

    // ============ declarator ============
    private (CType type, string name) ParseDeclarator(CType base_) => ParseDeclaratorCore(base_, false);

    private (CType type, string name) ParseDeclaratorCore(CType base_, bool isAbstract)
    {
        while (Check(TokenKind.Star))
        {
            while (Peek().Kind == TokenKind.KwConst || Peek().Kind == TokenKind.KwVolatile) _i++;
            base_ = new PointerType(base_, _target.PointerSize);
        }
        return ParseDirectDeclarator(base_, isAbstract);
    }

    private (CType type, string name) ParseDirectDeclarator(CType base_, bool isAbstract)
    {
        string name = "";

        if (Peek().Kind == TokenKind.Identifier) { name = Peek().Text; _i++; }
        else if (Peek().Kind == TokenKind.LParen && !(IsTypeName(Peek(1)) || Peek(1).Kind == TokenKind.RParen || IsEllipsis()))
        {
            _i++; // (
            var (inner2, name2) = ParseDeclaratorCore(base_, isAbstract);
            Expect(TokenKind.RParen, ")");
            var (t3, _) = ParseDeclaratorSuffix(inner2);
            return (t3, name2);
        }

        var (finalType, _) = ParseDeclaratorSuffix(base_);
        return (finalType, name);
    }

    private (CType type, string _) ParseDeclaratorSuffix(CType base_)
    {
        var ops = new List<(bool func, long len, List<ParamType>? prms, bool variadic)>();
        while (true)
        {
            if (Peek().Kind == TokenKind.LParen)
            {
                _i++;
                var prms = new List<ParamType>();
                bool variadic = false;
                if (Peek().Kind == TokenKind.RParen) { /* 无参 */ }
                else if (Peek().Kind == TokenKind.KwVoid && Peek(1).Kind == TokenKind.RParen) { _i++; }
                else
                {
                    while (true)
                    {
                        if (IsEllipsis()) { variadic = true; ConsumeEllipsis(); break; }
                        StorageClass sc = StorageClass.None; bool td = false;
                        ParseDeclSpecifiers(out var pbase, ref sc, ref td, out _, out _);
                        var (pt, pn) = ParseDeclaratorCore(pbase!, true);
                        if (pt is ArrayType at) pt = new PointerType(at.Element, _target.PointerSize);
                        prms.Add(new ParamType(pn, pt));
                        if (!Check(TokenKind.Comma)) break;
                        if (IsEllipsis()) { variadic = true; ConsumeEllipsis(); break; }
                    }
                }
                Expect(TokenKind.RParen, ")");
                ops.Add((true, 0, prms, variadic));
            }
            else if (Peek().Kind == TokenKind.LBracket)
            {
                _i++;
                long len = -1;
                if (Peek().Kind != TokenKind.RBracket) len = EvalConst(ParseAssignment());
                Expect(TokenKind.RBracket, "]");
                if (len < 0) len = 0; // 灵活数组成员 [] → Length 0（Size=0，不计入结构体大小）
                ops.Add((false, len, null, false));
            }
            else break;
        }

        var t = base_;
        for (int i = ops.Count - 1; i >= 0; i--)
        {
            if (ops[i].func) t = new FunctionType(t, ops[i].prms!, ops[i].variadic);
            else t = new ArrayType(t, ops[i].len);
        }
        return (t, "");
    }

    private bool IsTypeName(Token t) => t.Kind.IsTypeKeyword() || (t.Kind == TokenKind.Identifier && _typedefs.Contains(t.Text));

    // ============ 初始化 ============
    private Expr ParseInitValue()
    {
        if (!Check(TokenKind.Assign)) return null!;
        return ParseInitializer();
    }
    private Expr ParseInitializer()
    {
        if (Peek().Kind != TokenKind.LBrace) return ParseAssignment();
        _i++; // {
        var list = new InitListExpr();
        if (Peek().Kind != TokenKind.RBrace)
        {
            do
            {
                // 设计符链：[index] / .field，可链式（如 [2].x = ...）
                var desigs = new List<Designator>();
                while (Peek().Kind == TokenKind.LBracket || Peek().Kind == TokenKind.Dot)
                {
                    if (Peek().Kind == TokenKind.LBracket)
                    {
                        _i++; // [
                        long idx = EvalConst(ParseAssignment());
                        Expect(TokenKind.RBracket, "]");
                        desigs.Add(new Designator { Index = idx });
                    }
                    else
                    {
                        _i++; // .
                        var fn = Expect(TokenKind.Identifier, "字段名").Text;
                        desigs.Add(new Designator { Field = fn });
                    }
                }
                if (desigs.Count > 0) Expect(TokenKind.Assign, "=");
                var val = ParseInitializer();
                list.Elements.Add(val);
                list.Designators.Add(desigs.ToArray());
            } while (Check(TokenKind.Comma) && Peek().Kind != TokenKind.RBrace);
        }
        Expect(TokenKind.RBrace, "}");
        return list;
    }

    // ============ 语句 ============
    private BlockStmt ParseBlock()
    {
        var block = new BlockStmt { Range = Peek().Range };
        Expect(TokenKind.LBrace, "{");
        while (Peek().Kind != TokenKind.RBrace && Peek().Kind != TokenKind.Eof)
        {
            if (Peek().Kind == TokenKind.KwCase || Peek().Kind == TokenKind.KwDefault)
            {
                var cs = (CaseStmt)ParseStatement();
                block.Items.Add(cs);
                continue;
            }
            var item = ParseBlockItem();
            if (item != null) block.Items.Add(item);
        }
        Expect(TokenKind.RBrace, "}");
        return block;
    }

    private object? ParseBlockItem()
    {
        if (IsDeclarationStart()) return ParseLocalDecl();
        return ParseStatement();
    }

    private bool IsDeclarationStart() => Peek().Kind.IsTypeKeyword() || (Peek().Kind == TokenKind.Identifier && _typedefs.Contains(Peek().Text));

    private Decl ParseLocalDecl()
    {
        var range = Peek().Range;
        StorageClass sc = StorageClass.None; bool isTypedef = false;
        ParseDeclSpecifiers(out var base_, ref sc, ref isTypedef, out var sd, out var ed);
        if (Peek().Kind == TokenKind.Semicolon)
        {
            _i++;
            if (sd != null) return sd;
            if (ed != null) return ed;
            return new VarDecl(base_!, "_", range);
        }
        var (type, name) = ParseDeclarator(base_!);
        var first = MakeDecl(type, name, sc, isTypedef, range);
        if (first is VarDecl vd) vd.Init = ParseInitValue();
        var group = new List<Decl> { first };
        while (Check(TokenKind.Comma))
        {
            var (t2, n2) = ParseDeclarator(base_!);
            var d2 = MakeDecl(t2, n2, sc, isTypedef, range);
            if (d2 is VarDecl vd2) vd2.Init = ParseInitValue();
            group.Add(d2);
        }
        Expect(TokenKind.Semicolon, ";");
        return group.Count == 1 ? group[0] : new DeclGroup(group);
    }

    private Stmt ParseStatement()
    {
        var range = Peek().Range;
        switch (Peek().Kind)
        {
            case TokenKind.LBrace: return ParseBlock();
            case TokenKind.Semicolon: _i++; return new NullStmt { Range = range };
            case TokenKind.KwIf: return ParseIf();
            case TokenKind.KwWhile: return ParseWhile();
            case TokenKind.KwDo: return ParseDoWhile();
            case TokenKind.KwFor: return ParseFor();
            case TokenKind.KwSwitch: return ParseSwitch();
            case TokenKind.KwBreak: _i++; Expect(TokenKind.Semicolon, ";"); return new BreakStmt { Range = range };
            case TokenKind.KwContinue: _i++; Expect(TokenKind.Semicolon, ";"); return new ContinueStmt { Range = range };
            case TokenKind.KwReturn:
                _i++; Expr? val = null;
                if (Peek().Kind != TokenKind.Semicolon) val = ParseExpression();
                Expect(TokenKind.Semicolon, ";"); return new ReturnStmt { Range = range, Value = val };
            case TokenKind.KwGoto:
                _i++; var lbl = Peek().Text; _i++; Expect(TokenKind.Semicolon, ";"); return new GotoStmt { Range = range, Label = lbl };
            case TokenKind.KwCase: return ParseCaseOrDefault(false);
            case TokenKind.KwDefault: return ParseCaseOrDefault(true);
            case TokenKind.Identifier when Peek(1).Kind == TokenKind.Colon:
                {
                    var label = Peek().Text; _i++; _i++; var body = ParseStatement();
                    return new LabelStmt { Range = range, Label = label, Body = body };
                }
            default:
                {
                    var e = Peek().Kind == TokenKind.Semicolon ? null : ParseExpression();
                    Expect(TokenKind.Semicolon, ";");
                    return new ExprStmt(e) { Range = range };
                }
        }
    }

    private Stmt ParseCaseOrDefault(bool isDefault)
    {
        var r = Peek().Range; _i++;
        var cs = new CaseStmt { Range = r, IsDefault = isDefault };
        if (!isDefault) { cs.Value = ParseExpression(); }
        Expect(TokenKind.Colon, ":");
        while (Peek().Kind != TokenKind.KwCase && Peek().Kind != TokenKind.KwDefault
               && Peek().Kind != TokenKind.RBrace && Peek().Kind != TokenKind.Eof)
        {
            cs.Body.Add(ParseStatement());
        }
        return cs;
    }

    private Stmt ParseIf()
    {
        var r = Peek().Range; _i++;
        Expect(TokenKind.LParen, "("); var cond = ParseExpression(); Expect(TokenKind.RParen, ")");
        var then = ParseStatement();
        Stmt? els = null;
        if (Check(TokenKind.KwElse)) els = ParseStatement();
        return new IfStmt { Range = r, Cond = cond, Then = then, Else = els };
    }

    private Stmt ParseWhile()
    {
        var r = Peek().Range; _i++;
        Expect(TokenKind.LParen, "("); var cond = ParseExpression(); Expect(TokenKind.RParen, ")");
        return new WhileStmt { Range = r, Cond = cond, Body = ParseStatement() };
    }

    private Stmt ParseDoWhile()
    {
        var r = Peek().Range; _i++;
        var body = ParseStatement();
        Expect(TokenKind.KwWhile, "while"); Expect(TokenKind.LParen, "(");
        var cond = ParseExpression(); Expect(TokenKind.RParen, ")"); Expect(TokenKind.Semicolon, ";");
        return new DoWhileStmt { Range = r, Body = body, Cond = cond };
    }

    private Stmt ParseFor()
    {
        var r = Peek().Range; _i++;
        Expect(TokenKind.LParen, "(");
        object? init = null;
        if (Peek().Kind != TokenKind.Semicolon)
        {
            if (IsDeclarationStart()) init = ParseLocalDecl();
            else { if (Peek().Kind != TokenKind.Semicolon) init = ParseExpression(); Expect(TokenKind.Semicolon, ";"); }
        }
        else _i++;
        Expr? cond = null;
        if (Peek().Kind != TokenKind.Semicolon) cond = ParseExpression();
        Expect(TokenKind.Semicolon, ";");
        Expr? update = null;
        if (Peek().Kind != TokenKind.RParen) update = ParseExpression();
        Expect(TokenKind.RParen, ")");
        var body = ParseStatement();
        return new ForStmt { Range = r, Init = init, Cond = cond, Update = update, Body = body };
    }

    private Stmt ParseSwitch()
    {
        var r = Peek().Range; _i++;
        Expect(TokenKind.LParen, "("); var e = ParseExpression(); Expect(TokenKind.RParen, ")");
        var body = ParseBlock();
        return new SwitchStmt { Range = r, Expr = e, Body = body };
    }

    // ============ 表达式 ============
    private Expr ParseExpression()
    {
        var e = ParseAssignment();
        while (Check(TokenKind.Comma))
        {
            var right = ParseAssignment();
            e = new CommaExpr(e, right) { Range = e.Range };
        }
        return e;
    }

    private Expr ParseAssignment()
    {
        var left = ParseConditional();
        if (Peek().Kind.IsAssignment())
        {
            var op = Peek(); _i++;
            var right = ParseAssignment();
            return new AssignExpr(op.Kind, left, right) { Range = left.Range };
        }
        return left;
    }

    private Expr ParseConditional()
    {
        var c = ParseBinary(0);
        if (Check(TokenKind.Question))
        {
            var then = ParseExpression();
            Expect(TokenKind.Colon, ":");
            var els = ParseConditional();
            return new ConditionalExpr(c, then, els) { Range = c.Range };
        }
        return c;
    }

    private static readonly (TokenKind, int)[] Prec =
    {
        (TokenKind.OrOr, 1), (TokenKind.AndAnd, 2),
        (TokenKind.Pipe, 3), (TokenKind.Caret, 4), (TokenKind.Amp, 5),
        (TokenKind.Eq, 6), (TokenKind.NotEq, 6),
        (TokenKind.Lt, 7), (TokenKind.Le, 7), (TokenKind.Gt, 7), (TokenKind.Ge, 7),
        (TokenKind.Shl, 8), (TokenKind.Shr, 8),
        (TokenKind.Plus, 9), (TokenKind.Minus, 9),
        (TokenKind.Star, 10), (TokenKind.Slash, 10), (TokenKind.Percent, 10),
    };
    private static int PrecOf(TokenKind k)
    {
        foreach (var (kk, p) in Prec) if (kk == k) return p;
        return -1;
    }

    private Expr ParseBinary(int minPrec)
    {
        var left = ParseUnary();
        while (true)
        {
            int p = PrecOf(Peek().Kind);
            if (p < minPrec || p < 0) break;
            var op = Peek(); _i++;
            var right = ParseBinary(p + 1);
            left = new BinaryExpr(op.Kind, left, right) { Range = left.Range };
        }
        return left;
    }

    private Expr ParseUnary()
    {
        var k = Peek().Kind;
        switch (k)
        {
            case TokenKind.PlusPlus: case TokenKind.MinusMinus:
                { var op = Peek(); _i++; var e = ParseUnary(); return new UnaryExpr(op.Kind, e, true) { Range = op.Range }; }
            case TokenKind.Plus: case TokenKind.Minus: case TokenKind.Not: case TokenKind.Tilde:
            case TokenKind.Star: case TokenKind.Amp:
                { var op = Peek(); _i++; var e = ParseUnary(); return new UnaryExpr(op.Kind, e, true) { Range = op.Range }; }
            case TokenKind.KwSizeof:
                {
                    var r = Peek().Range; _i++;
                    if (Check(TokenKind.LParen) && IsTypeName(Peek()))
                    {
                        var t = ParseTypeName();
                        Expect(TokenKind.RParen, ")");
                        return new SizeofExpr(null, t) { Range = r };
                    }
                    var e = ParseUnary();
                    return new SizeofExpr(e, null) { Range = r };
                }
            default: return ParseCastOrPostfix();
        }
    }

    private Expr ParseCastOrPostfix()
    {
        if (Peek().Kind == TokenKind.LParen && IsTypeName(Peek(1)))
        {
            int save = _i;
            _i++; // (
            var t = ParseTypeName();
            if (Peek().Kind == TokenKind.RParen)
            {
                _i++; // )
                // 复合字面量：(type){ init-list }
                if (Peek().Kind == TokenKind.LBrace)
                {
                    var init = ParseInitializer();
                    return new CompoundLiteralExpr { LitType = t, Init = init, Range = _toks[save].Range };
                }
                var nk = Peek().Kind;
                if (nk is TokenKind.Identifier or TokenKind.IntLiteral or TokenKind.CharLiteral
                    or TokenKind.StringLiteral or TokenKind.LParen or TokenKind.Minus or TokenKind.Not or TokenKind.Tilde
                    or TokenKind.Star or TokenKind.Amp or TokenKind.PlusPlus or TokenKind.MinusMinus or TokenKind.KwSizeof)
                {
                    var operand = ParseUnary();
                    return new CastExpr(t, operand) { Range = _toks[save].Range };
                }
            }
            _i = save;
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            switch (Peek().Kind)
            {
                case TokenKind.LParen:
                    {
                        _i++;
                        var call = new CallExpr(e) { Range = e.Range };
                        if (Peek().Kind != TokenKind.RParen)
                        {
                            do { call.Args.Add(ParseAssignment()); }
                            while (Check(TokenKind.Comma) && Peek().Kind != TokenKind.RParen);
                        }
                        Expect(TokenKind.RParen, ")");
                        e = call; continue;
                    }
                case TokenKind.LBracket:
                    { _i++; var idx = ParseExpression(); Expect(TokenKind.RBracket, "]"); e = new IndexExpr(e, idx) { Range = e.Range }; continue; }
                case TokenKind.Dot:
                    { _i++; var name = Expect(TokenKind.Identifier, "成员名").Text; e = new MemberExpr(e, name, false) { Range = e.Range }; continue; }
                case TokenKind.Arrow:
                    { _i++; var name = Expect(TokenKind.Identifier, "成员名").Text; e = new MemberExpr(e, name, true) { Range = e.Range }; continue; }
                case TokenKind.PlusPlus: case TokenKind.MinusMinus:
                    { var op = Peek(); _i++; e = new UnaryExpr(op.Kind, e, false) { Range = e.Range }; continue; }
                default: return e;
            }
        }
    }

    private Expr ParsePrimary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.IntLiteral: _i++; return new IntLiteral(t.IntValue) { Range = t.Range };
            case TokenKind.CharLiteral: _i++; return new CharLiteral(t.IntValue) { Range = t.Range };
            case TokenKind.StringLiteral:
                {
                    _i++;
                    var s = t.StringValue ?? "";
                    while (Peek().Kind == TokenKind.StringLiteral) { s += Peek().StringValue; _i++; }
                    return new StringLiteral(s) { Range = t.Range };
                }
            case TokenKind.Identifier:
                {
                    // color.red / color.255.0.0 → 折叠为 IntLiteral(0x80RRGGBB)
                    if (t.Text == "color" && Peek(1).Kind == TokenKind.Dot)
                        return ParseColorExpr(t.Range);
                    if (_enumConstants.TryGetValue(t.Text, out long ev)) { _i++; return new IntLiteral(ev) { Range = t.Range }; }
                    _i++; return new IdentifierRef(t.Text) { Range = t.Range };
                }
            case TokenKind.LParen:
                {
                    _i++;
                    // GNU 语句表达式 ({ ... })：值为块末表达式的值
                    if (Peek().Kind == TokenKind.LBrace)
                    {
                        var body = ParseBlock();
                        Expect(TokenKind.RParen, ")");
                        return new StmtExpr(body) { Range = t.Range };
                    }
                    var e = ParseExpression(); Expect(TokenKind.RParen, ")"); return e;
                }
            default:
                Error(t.Range, $"意外的 token '{t.Text}'");
                _i++;
                return new IntLiteral(0) { Range = t.Range };
        }
    }

    // color.red / color.255.0.0 → IntLiteral(0x80RRGGBB)，见 ColorNames。
    private Expr ParseColorExpr(SourceRange range)
    {
        _i++; // 消耗 'color'
        Expect(TokenKind.Dot, "'.'");
        var next = Peek();
        if (next.Kind == TokenKind.Identifier)
        {
            string name = next.Text;
            _i++;
            var v = ColorNames.ResolveNamed(name);
            if (v == null)
            {
                Error(range, $"未知颜色名 '{name}'");
                return new IntLiteral(0x80000000L) { Range = range };
            }
            return new IntLiteral(v.Value) { Range = range };
        }
        if (next.Kind == TokenKind.IntLiteral)
        {
            int r = (int)next.IntValue; _i++;
            Expect(TokenKind.Dot, "'.'");
            int g = (int)Expect(TokenKind.IntLiteral, "G 分量").IntValue;
            Expect(TokenKind.Dot, "'.'");
            int b = (int)Expect(TokenKind.IntLiteral, "B 分量").IntValue;
            return new IntLiteral(ColorNames.PackRgb(r, g, b)) { Range = range };
        }
        Error(range, "无效的 color 语法，期望颜色名（如 color.red）或 RGB（如 color.255.0.0）");
        _i++;
        return new IntLiteral(0x80000000L) { Range = range };
    }

    private CType ParseTypeName()
    {
        StorageClass sc = StorageClass.None; bool td = false;
        ParseDeclSpecifiers(out var base_, ref sc, ref td, out _, out _);
        if (Peek().Kind == TokenKind.RParen || Peek().Kind == TokenKind.Comma) return base_!;
        var (t, _) = ParseDeclaratorCore(base_!, true);
        return t;
    }
}
