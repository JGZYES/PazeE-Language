using System.Text;

namespace PazeE.Compiler.Lexer;

/// <summary>预处理：#include / #define / #undef / #ifdef/#ifndef/#if/#elif/#else/#endif + 宏展开。</summary>
public sealed class Preprocessor
{
    public Diagnostics Diag { get; }
    private readonly List<string> _includeDirs;
    private readonly Func<string, string?> _systemHeaderProvider;
    private readonly Dictionary<string, Macro> _macros = new();
    private readonly HashSet<string> _includedFiles = new();

    public Preprocessor(Diagnostics diag, IEnumerable<string> includeDirs, Func<string, string?> systemHeaderProvider)
    {
        Diag = diag;
        _includeDirs = includeDirs.ToList();
        _systemHeaderProvider = systemHeaderProvider;
    }

    public List<Token> Run(string source, string file)
    {
        var toks = new Lexer(source, file, Diag).Tokenize();
        var result = ProcessUnit(toks, file);
        // 确保以 EOF 终止（递归 include 不需要，仅顶层补一个）
        if (result.Count == 0 || result[^1].Kind != TokenKind.Eof)
            result.Add(new Token(TokenKind.Eof, "", SourceRange.Empty));
        return result;
    }

    private sealed class Macro
    {
        public bool IsFunction;
        public bool IsVariadic;   // 函数式宏末尾有 ... → 支持 __VA_ARGS__
        public List<string> Params = new();
        public List<Token> Body = new();
    }

    private bool IsMacro(string n) => _macros.ContainsKey(n);
    private Macro? GetMacro(string n) => _macros.GetValueOrDefault(n);

    private List<Token> ProcessUnit(List<Token> toks, string file) => new Ctx(this, file).Run(toks);

    private sealed class Ctx
    {
        private readonly Preprocessor _o;
        private readonly string _file;
        private readonly Stack<Cond> _cond = new();
        public Ctx(Preprocessor o, string file) { _o = o; _file = file; }
        private record struct Cond(bool Active, bool AnyTaken);
        private bool Active => _cond.All(c => c.Active);

        public List<Token> Run(List<Token> toks)
        {
            var output = new List<Token>();
            int i = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Kind == TokenKind.Eof) break;
                if (t.Kind == TokenKind.Hash && t.AtLineStart)
                {
                    i = Directive(toks, i + 1, output);
                    continue;
                }
                if (!Active) { i++; continue; }
                output.Add(t);
                i++;
            }
            return _o.Expand(output);
        }

        private int Directive(List<Token> toks, int i, List<Token> output)
        {
            var line = new List<Token>();
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Kind == TokenKind.Eof) break;
                if (t.AtLineStart || t.Kind == TokenKind.Hash) break;
                line.Add(t);
                i++;
            }
            if (line.Count == 0) return i;
            string dir = line[0].Text;
            var args = line.Skip(1).ToList();
            var range = line[0].Range;

            switch (dir)
            {
                case "include": if (Active) DoInclude(args, output, range); break;
                case "define": if (Active) DoDefine(args); break;
                case "undef": if (Active) { if (args.Count > 0) _o._macros.Remove(args[0].Text); } break;
                case "ifdef": DoIfDef(args, false); break;
                case "ifndef": DoIfDef(args, true); break;
                case "if": DoIf(args); break;
                case "elif": DoElif(args); break;
                case "else": DoElse(); break;
                case "endif": if (_cond.Count > 0) _cond.Pop(); else _o.Diag.Error(range, "#endif 无匹配 #if"); break;
                case "error": if (Active) _o.Diag.Error(range, "#error: " + string.Join(' ', args.Select(a => a.Text))); break;
                case "pragma": case "line": case "warning": break;
                default: if (Active) _o.Diag.Warning(range, $"未知预处理指令 '#{dir}'"); break;
            }
            return i;
        }

        private void DoInclude(List<Token> args, List<Token> output, SourceRange range)
        {
            if (args.Count == 0) { _o.Diag.Error(range, "#include 缺少文件名"); return; }
            string fname; bool system; var first = args[0];
            if (first.Kind == TokenKind.StringLiteral) { fname = first.StringValue ?? ""; system = false; }
            else if (first.Text == "<")
            {
                var sb = new StringBuilder();
                foreach (var t in args.Skip(1)) { if (t.Text == ">") break; sb.Append(t.Text); }
                fname = sb.ToString(); system = true;
            }
            else { fname = string.Join("", args.Select(a => a.Text)); system = false; }

            string? content = Resolve(fname, system);
            if (content == null) { _o.Diag.Error(range, $"找不到头文件 '{fname}'"); return; }
            string key = (system ? "sys:" : "usr:") + fname;
            if (!_o._includedFiles.Add(key)) return;
            var sub = new Lexer(content, fname, _o.Diag).Tokenize();
            output.AddRange(_o.ProcessUnit(sub, fname));
        }

        private string? Resolve(string fname, bool system)
        {
            if (!system)
            {
                var baseDir = Path.GetDirectoryName(_file);
                if (baseDir != null && File.Exists(Path.Combine(baseDir, fname))) return File.ReadAllText(Path.Combine(baseDir, fname));
                foreach (var d in _o._includeDirs)
                {
                    var p = Path.Combine(d, fname);
                    if (File.Exists(p)) return File.ReadAllText(p);
                }
            }
            return _o._systemHeaderProvider(fname);
        }

        private void DoDefine(List<Token> args)
        {
            if (args.Count == 0) return;
            string name = args[0].Text;
            var m = new Macro();
            int idx = 1;
            // 函数式宏：name 紧跟 '('（用列位置判定无空白）
            if (idx < args.Count && args[idx].Kind == TokenKind.LParen
                && args[idx].Range.StartCol == args[0].Range.StartCol + name.Length)
            {
                m.IsFunction = true; idx++;
                while (idx < args.Count && args[idx].Kind != TokenKind.RParen)
                {
                    if (args[idx].Kind == TokenKind.Comma) { idx++; continue; }
                    if (args[idx].Text == "...") { m.IsVariadic = true; idx++; continue; }
                    m.Params.Add(args[idx].Text); idx++;
                }
                if (idx < args.Count && args[idx].Kind == TokenKind.RParen) idx++;
            }
            m.Body = args.Skip(idx).ToList();
            _o._macros[name] = m;
        }

        private void DoIfDef(List<Token> args, bool neg)
        {
            bool parent = Active;
            bool def = args.Count > 0 && _o.IsMacro(args[0].Text);
            bool cond = def ^ neg;
            _cond.Push(new Cond(parent && cond, parent && cond));
        }

        private void DoIf(List<Token> args)
        {
            bool parent = Active;
            bool cond = parent && EvalIf(args);
            _cond.Push(new Cond(cond, cond));
        }

        private void DoElif(List<Token> args)
        {
            if (_cond.Count == 0) { _o.Diag.Error(SourceRange.Empty, "#elif 无匹配 #if"); return; }
            var top = _cond.Pop();
            bool parent = Active;
            bool cond = parent && !top.AnyTaken && EvalIf(args);
            _cond.Push(new Cond(cond, top.AnyTaken || cond));
        }

        private void DoElse()
        {
            if (_cond.Count == 0) { _o.Diag.Error(SourceRange.Empty, "#else 无匹配 #if"); return; }
            var top = _cond.Pop();
            bool parent = Active;
            bool cond = parent && !top.AnyTaken;
            _cond.Push(new Cond(cond, top.AnyTaken || cond));
        }

        private bool EvalIf(List<Token> args)
        {
            var toks = new List<Token>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Text == "defined")
                {
                    int j = i + 1;
                    if (j < args.Count && args[j].Kind == TokenKind.LParen) j++;
                    if (j >= args.Count) break;
                    string nm = args[j].Text;
                    long v = _o.IsMacro(nm) ? 1 : 0;
                    toks.Add(new Token(TokenKind.IntLiteral, v.ToString(), args[i].Range) { IntValue = v });
                    i = j; continue;
                }
                toks.Add(args[i]);
            }
            toks = _o.Expand(toks);
            return new ConstEvaluator(toks, _o.Diag).Evaluate() != 0;
        }
    }

    // ---------------- 宏展开 ----------------
    internal List<Token> Expand(List<Token> input)
    {
        var work = new List<Token>(input);
        var output = new List<Token>();
        int i = 0;
        while (i < work.Count)
        {
            var t = work[i];
            if (t.Kind == TokenKind.Identifier && _macros.TryGetValue(t.Text, out var m) && (t.Hide == null || !t.Hide.Contains(t.Text)))
            {
                if (!m.IsFunction)
                {
                    var body = Substitute(m, new List<List<Token>>(), t.Hide, t.Text, null);
                    work.RemoveAt(i);
                    work.InsertRange(i, body);
                    continue;
                }
                int j = i + 1;
                if (j < work.Count && work[j].Kind == TokenKind.LParen)
                {
                    var (args, end) = CollectArgs(work, j);
                    // 可变参数宏：命名参数之后的实参（含分隔逗号）合并为 __VA_ARGS__
                    List<Token>? vaArgs = null;
                    if (m.IsVariadic)
                    {
                        int named = m.Params.Count;
                        vaArgs = new List<Token>();
                        if (args.Count > named)
                        {
                            for (int k = named; k < args.Count; k++)
                            {
                                if (k > named) vaArgs.Add(new Token(TokenKind.Comma, ",", args[k].Count > 0 ? args[k][0].Range : SourceRange.Empty));
                                vaArgs.AddRange(args[k]);
                            }
                        }
                    }
                    var body = Substitute(m, args, t.Hide, t.Text, vaArgs);
                    work.RemoveRange(i, end - i + 1);
                    work.InsertRange(i, body);
                    continue;
                }
                output.Add(t); i++; continue;
            }
            output.Add(t); i++;
        }
        return output;
    }

    private (List<List<Token>> args, int end) CollectArgs(List<Token> toks, int lp)
    {
        var args = new List<List<Token>>();
        var cur = new List<Token>();
        int depth = 1; int i = lp + 1;
        while (i < toks.Count && depth > 0)
        {
            var t = toks[i];
            if (t.Kind == TokenKind.LParen) { depth++; cur.Add(t); }
            else if (t.Kind == TokenKind.RParen) { depth--; if (depth == 0) break; cur.Add(t); }
            else if (t.Kind == TokenKind.Comma && depth == 1) { args.Add(cur); cur = new(); }
            else cur.Add(t);
            i++;
        }
        args.Add(cur);
        if (args.Count == 1 && args[0].Count == 0) args.Clear();
        return (args, i);
    }

    private List<Token> Substitute(Macro m, List<List<Token>> args, HashSet<string>? parentHide, string name, List<Token>? vaArgs)
    {
        var newHide = new HashSet<string>(parentHide ?? Enumerable.Empty<string>()) { name };
        var result = new List<Token>();
        var body = m.Body;
        // 预展开 __VA_ARGS__（与命名参数一致：实参需完全宏展开后再代入）
        List<Token>? expandedVa = null;
        if (m.IsVariadic && vaArgs != null && vaArgs.Count > 0)
            expandedVa = Expand(new List<Token>(vaArgs));

        bool IsVa(int idx) => idx < body.Count && body[idx].Kind == TokenKind.Identifier && body[idx].Text == "__VA_ARGS__";

        for (int i = 0; i < body.Count; i++)
        {
            var t = body[i];

            // GNU , ## __VA_ARGS__ 逗号省略：当 __VA_ARGS__ 为空时省略其前的逗号
            // body 模式：Comma Hash Hash __VA_ARGS__
            if (m.IsVariadic && t.Kind == TokenKind.Comma && i + 3 < body.Count
                && body[i + 1].Kind == TokenKind.Hash && body[i + 2].Kind == TokenKind.Hash && IsVa(i + 3))
            {
                if (expandedVa == null || expandedVa.Count == 0)
                {
                    // 空实参：省略逗号与 ## 与 __VA_ARGS__，什么都不 emit
                }
                else
                {
                    // 非空：保留逗号，emit __VA_ARGS__，丢弃 ##
                    result.Add(t with { Hide = newHide });
                    foreach (var a in expandedVa) result.Add(a with { Hide = newHide });
                }
                i += 3; // 跳过 ## __VA_ARGS__（循环再 +1）
                continue;
            }

            // 裸 ## __VA_ARGS__ （无前导逗号）：仅 paste，MVP 视为直接拼接 __VA_ARGS__
            if (m.IsVariadic && t.Kind == TokenKind.Hash && i + 2 < body.Count
                && body[i + 1].Kind == TokenKind.Hash && IsVa(i + 2))
            {
                if (expandedVa != null) foreach (var a in expandedVa) result.Add(a with { Hide = newHide });
                i += 2;
                continue;
            }

            // __VA_ARGS__ 直接出现
            if (m.IsVariadic && t.Kind == TokenKind.Identifier && t.Text == "__VA_ARGS__")
            {
                if (expandedVa != null) foreach (var a in expandedVa) result.Add(a with { Hide = newHide });
                continue;
            }

            if (t.Kind == TokenKind.Identifier)
            {
                int pIdx = m.Params.IndexOf(t.Text);
                if (pIdx >= 0 && pIdx < args.Count)
                {
                    var expanded = Expand(new List<Token>(args[pIdx]));
                    foreach (var a in expanded) result.Add(a with { Hide = newHide });
                    continue;
                }
            }
            result.Add(t with { Hide = newHide });
        }
        return result;
    }
}

/// <summary>#if 常量表达式求值器（整数）。</summary>
internal sealed class ConstEvaluator
{
    private readonly List<Token> _toks;
    private int _i;
    private readonly Diagnostics _diag;
    public ConstEvaluator(List<Token> toks, Diagnostics diag) { _toks = toks; _diag = diag; }

    public long Evaluate() => Ternary();
    private long Ternary() { var c = LogicalOr(); if (Peek("?")) { _i++; var a = Ternary(); Expect(":"); var b = Ternary(); return c != 0 ? a : b; } return c; }
    private long LogicalOr() { var l = LogicalAnd(); while (Peek("||")) { _i++; var r = LogicalAnd(); l = (l != 0 || r != 0) ? 1 : 0; } return l; }
    private long LogicalAnd() { var l = BitOr(); while (Peek("&&")) { _i++; var r = BitOr(); l = (l != 0 && r != 0) ? 1 : 0; } return l; }
    private long BitOr() { var l = BitXor(); while (Peek("|")) { _i++; l |= BitXor(); } return l; }
    private long BitXor() { var l = BitAnd(); while (Peek("^")) { _i++; l ^= BitAnd(); } return l; }
    private long BitAnd() { var l = Equality(); while (Peek("&")) { _i++; l &= Equality(); } return l; }
    private long Equality() { var l = Rel(); while (true) { if (Peek("==")) { _i++; l = l == Rel() ? 1 : 0; } else if (Peek("!=")) { _i++; l = l != Rel() ? 1 : 0; } else break; } return l; }
    private long Rel() { var l = Shift(); while (true) { string? op = PeekRel(); if (op == null) break; _i++; var r = Shift(); l = op switch { "<" => l < r ? 1 : 0, "<=" => l <= r ? 1 : 0, ">" => l > r ? 1 : 0, ">=" => l >= r ? 1 : 0, _ => l }; } return l; }
    private long Shift() { var l = Add(); while (true) { if (Peek("<<")) { _i++; l <<= (int)Add(); } else if (Peek(">>")) { _i++; l >>= (int)Add(); } else break; } return l; }
    private long Add() { var l = Mul(); while (true) { if (Peek("+")) { _i++; l += Mul(); } else if (Peek("-")) { _i++; l -= Mul(); } else break; } return l; }
    private long Mul() { var l = Unary(); while (true) { if (Peek("*")) { _i++; l *= Unary(); } else if (Peek("/")) { _i++; var r = Unary(); l = r == 0 ? 0 : l / r; } else if (Peek("%")) { _i++; var r = Unary(); l = r == 0 ? 0 : l % r; } else break; } return l; }
    private long Unary()
    {
        if (Peek("!")) { _i++; return Unary() == 0 ? 1 : 0; }
        if (Peek("~")) { _i++; return ~Unary(); }
        if (Peek("-")) { _i++; return -Unary(); }
        if (Peek("+")) { _i++; return Unary(); }
        return Primary();
    }
    private long Primary()
    {
        if (_i >= _toks.Count) return 0;
        var t = _toks[_i];
        if (t.Kind == TokenKind.IntLiteral || t.Kind == TokenKind.CharLiteral) { _i++; return t.IntValue; }
        if (t.Kind == TokenKind.LParen) { _i++; var v = Ternary(); if (Peek(")")) _i++; return v; }
        _i++; return 0;
    }
    private bool Peek(string op) => _i < _toks.Count && _toks[_i].Text == op;
    private void Expect(string op) { if (Peek(op)) _i++; }
    private string? PeekRel() => _i < _toks.Count && _toks[_i].Text is "<" or "<=" or ">" or ">=" ? _toks[_i].Text : null;
}
