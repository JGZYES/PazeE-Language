using System.Globalization;
using System.Text;

namespace PazeE.Compiler.Lexer;

/// <summary>把源码字符流切分为 token。处理注释与行续接，不处理宏展开（交给 Preprocessor）。</summary>
public sealed class Lexer
{
    private readonly string _src;
    private readonly string _file;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private bool _lineStart = true;
    public Diagnostics Diag { get; }

    // color RGB 状态机：识别 color.N.M.K 序列，让其中的数字后的 '.' 不被误判为浮点小数点。
    // 0=none, 1=sawColor, 2=sawColorDot, 3=inRgb
    private int _colorPhase;

    public Lexer(string source, string file, Diagnostics diag)
    {
        _src = source;
        _file = file;
        Diag = diag;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTriviaAndComments();
            if (_pos >= _src.Length)
            {
                tokens.Add(MakeEof());
                break;
            }
            var t = NextToken();
            if (t.HasValue) tokens.Add(t.Value);
        }
        return tokens;
    }

    private Token MakeEof() => new(TokenKind.Eof, "", SourceRange.From(_file, _line, _col)) { AtLineStart = _lineStart };

    private void Advance(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            if (_pos >= _src.Length) return;
            char c = _src[_pos++];
            if (c == '\n') { _line++; _col = 1; _lineStart = true; }
            else { _col++; if (!char.IsWhiteSpace(c)) _lineStart = false; }
        }
    }

    private char Peek(int o = 0) => (_pos + o < _src.Length) ? _src[_pos + o] : '\0';

    private void SkipTriviaAndComments()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            // 行续接：反斜杠 + 换行。必须完整消费 \<换行>，否则残留的 \n 会被下面的空白
            // 分支经 Advance() 处理，从而把 _lineStart 置 true，导致 #define 多行宏体在
            // 第一个续行处被 Directive 误判为结束（宏体被截断）。
            if (c == '\\' && _pos + 1 < _src.Length && (_src[_pos + 1] == '\n' || _src[_pos + 1] == '\r'))
            {
                _pos++;                       // 跳过 '\'
                if (_pos < _src.Length && _src[_pos] == '\r') _pos++;  // 跳过 '\r'（Windows CRLF）
                if (_pos < _src.Length && _src[_pos] == '\n') { _pos++; _line++; _col = 1; }  // 跳过并推进 '\n'
                // 注意：不置 _lineStart = true —— 续行属于同一逻辑行
                continue;
            }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v')
            {
                Advance();
                continue;
            }
            // 行注释
            if (c == '/' && Peek(1) == '/')
            {
                while (_pos < _src.Length && _src[_pos] != '\n') Advance();
                continue;
            }
            // 块注释
            if (c == '/' && Peek(1) == '*')
            {
                int startLine = _line, startCol = _col;
                Advance(2);
                while (_pos < _src.Length && !(_src[_pos] == '*' && Peek(1) == '/')) Advance();
                if (_pos >= _src.Length)
                {
                    Diag.Error(SourceRange.From(_file, startLine, startCol), "未终止的块注释");
                    return;
                }
                Advance(2);
                continue;
            }
            break;
        }
    }

    private Token? NextToken()
    {
        int line = _line, col = _col;
        bool atLineStart = _lineStart;
        char c = _src[_pos];

        Token tok;
        // # 预处理标记（仅行首）
        if (c == '#' && atLineStart)
        {
            Advance();
            tok = new Token(TokenKind.Hash, "#", SourceRange.From(_file, line, col)) { AtLineStart = true };
        }
        // 标识符 / 关键字
        else if (c == '_' || char.IsLetter(c))
        {
            var sb = new StringBuilder();
            while (_pos < _src.Length && (_src[_pos] == '_' || char.IsLetterOrDigit(_src[_pos])))
            { sb.Append(_src[_pos]); Advance(); }
            var text = sb.ToString();
            var kind = Keywords.Lookup(text) ?? TokenKind.Identifier;
            tok = new Token(kind, text, SourceRange.From(_file, line, col)) { AtLineStart = atLineStart };
        }
        // 数字
        else if (char.IsDigit(c))
            tok = LexNumber(line, col, atLineStart);
        // 字符字面量
        else if (c == '\'')
            tok = LexChar(line, col, atLineStart);
        // 字符串字面量
        else if (c == '"')
            tok = LexString(line, col, atLineStart);
        // 运算符 / 标点
        else
            tok = LexPunct(line, col, atLineStart);

        UpdateColorPhase(tok);
        return tok;
    }

    /// <summary>更新 color RGB 状态机。识别 color.N.M.K 序列，使序列内数字后的 '.' 不被当浮点小数点。</summary>
    private void UpdateColorPhase(Token t)
    {
        switch (t.Kind)
        {
            case TokenKind.Identifier:
                _colorPhase = (t.Text == "color") ? 1 : 0;
                break;
            case TokenKind.Dot:
                if (_colorPhase == 1) _colorPhase = 2;          // color.  → 等待数字
                else if (_colorPhase != 3) _colorPhase = 0;     // RGB 序列中的分隔点保持 3
                break;
            case TokenKind.IntLiteral:
                if (_colorPhase == 2) _colorPhase = 3;          // color.N → 进入 RGB
                else if (_colorPhase != 3) _colorPhase = 0;
                break;
            default:
                _colorPhase = 0;
                break;
        }
    }

    private Token LexNumber(int line, int col, bool atLineStart)
    {
        var sb = new StringBuilder();
        long value = 0;
        bool hex = false;
        if (_src[_pos] == '0' && (_pos + 1 < _src.Length) && (_src[_pos + 1] == 'x' || _src[_pos + 1] == 'X'))
        {
            hex = true;
            sb.Append(_src[_pos]); sb.Append(_src[_pos + 1]); Advance(2);
            while (_pos < _src.Length && IsHexDigit(_src[_pos]))
            { sb.Append(_src[_pos]); value = value * 16 + HexVal(_src[_pos]); Advance(); }
        }
        else if (_src[_pos] == '0' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]))
        {
            // 八进制
            sb.Append(_src[_pos]); Advance();
            while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '7')
            { sb.Append(_src[_pos]); value = value * 8 + (_src[_pos] - '0'); Advance(); }
        }
        else
        {
            while (_pos < _src.Length && char.IsDigit(_src[_pos]))
            { sb.Append(_src[_pos]); value = value * 10 + (_src[_pos] - '0'); Advance(); }
        }
        // 整数后缀 u/U/l/L 组合（忽略语义，统一 long）
        while (_pos < _src.Length && "uUlL".IndexOf(_src[_pos]) >= 0) { sb.Append(_src[_pos]); Advance(); }

        // 兼容浮点字面量场景：遇到 '.' 或 'e' 视为错误（v1 不支持 double）
        // 例外：color RGB 语法 color.N.M.K 中，数字后的 '.' 是分量分隔符，不是小数点，此时不报错
        if (_pos < _src.Length && (_src[_pos] == '.' || _src[_pos] == 'e' || _src[_pos] == 'E'))
        {
            bool isColorRgbDot = _src[_pos] == '.' && (_colorPhase == 2 || _colorPhase == 3);
            if (!isColorRgbDot)
                Diag.Error(SourceRange.From(_file, line, col), "浮点字面量暂不支持（v1 仅整型）");
        }

        return new Token(TokenKind.IntLiteral, sb.ToString(), SourceRange.From(_file, line, col))
        { IntValue = value, AtLineStart = atLineStart };
    }

    private Token LexChar(int line, int col, bool atLineStart)
    {
        Advance(); // '
        long val = ReadCharEscape();
        if (_pos >= _src.Length || _src[_pos] != '\'')
            Diag.Error(SourceRange.From(_file, line, col), "未终止的字符字面量");
        else Advance();
        return new Token(TokenKind.CharLiteral, "'" + ((char)val) + "'", SourceRange.From(_file, line, col))
        { IntValue = val, AtLineStart = atLineStart };
    }

    private Token LexString(int line, int col, bool atLineStart)
    {
        Advance(); // "
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != '"')
        {
            if (_src[_pos] == '\n') { Diag.Error(SourceRange.From(_file, line, col), "未终止的字符串字面量"); break; }
            sb.Append((char)ReadCharEscape());
        }
        if (_pos < _src.Length && _src[_pos] == '"') Advance();
        string s = sb.ToString();
        return new Token(TokenKind.StringLiteral, s, SourceRange.From(_file, line, col))
        { StringValue = s, AtLineStart = atLineStart };
    }

    /// <summary>读取一个字符或转义序列，返回其码点。</summary>
    private long ReadCharEscape()
    {
        if (_pos >= _src.Length) return 0;
        char c = _src[_pos];
        if (c != '\\') { Advance(); return c; }
        Advance(); // backslash
        if (_pos >= _src.Length) return '\\';
        char e = _src[_pos]; Advance();
        return e switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => 0,
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            'a' => '\a',
            'b' => '\b',
            'f' => '\f',
            'v' => '\v',
            'x' => ReadHexEscape(),
            _ when e >= '0' && e <= '7' => ReadOctalEscape(e),
            _ => e,
        };
    }

    private long ReadOctalEscape(char first)
    {
        long val = first - '0';
        for (int i = 0; i < 2 && _pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '7'; i++)
        { val = val * 8 + (_src[_pos] - '0'); Advance(); }
        return val & 0xff;
    }

    private long ReadHexEscape()
    {
        long val = 0;
        int count = 0;
        while (count < 2 && _pos < _src.Length && IsHexDigit(_src[_pos]))
        { val = val * 16 + HexVal(_src[_pos]); Advance(); count++; }
        return val & 0xff;
    }

    private static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' : (char.ToLower(c, CultureInfo.InvariantCulture) - 'a' + 10);

    private Token LexPunct(int line, int col, bool atLineStart)
    {
        char c = _src[_pos]; Advance();
        TokenKind kind;
        string text = c.ToString();
        switch (c)
        {
            case '+': kind = Match('+') ? TokenKind.PlusPlus : Match('=') ? TokenKind.PlusAssign : TokenKind.Plus; break;
            case '-': kind = Match('-') ? TokenKind.MinusMinus : Match('=') ? TokenKind.MinusAssign : Match('>') ? TokenKind.Arrow : TokenKind.Minus; break;
            case '*': kind = Match('=') ? TokenKind.StarAssign : TokenKind.Star; break;
            case '/': kind = Match('=') ? TokenKind.SlashAssign : TokenKind.Slash; break;
            case '%': kind = Match('=') ? TokenKind.PercentAssign : TokenKind.Percent; break;
            case '=': kind = Match('=') ? TokenKind.Eq : TokenKind.Assign; break;
            case '!': kind = Match('=') ? TokenKind.NotEq : TokenKind.Not; break;
            case '<': kind = Match('<') ? (Match('=') ? TokenKind.ShlAssign : TokenKind.Shl) : Match('=') ? TokenKind.Le : TokenKind.Lt; break;
            case '>': kind = Match('>') ? (Match('=') ? TokenKind.ShrAssign : TokenKind.Shr) : Match('=') ? TokenKind.Ge : TokenKind.Gt; break;
            case '&': kind = Match('&') ? TokenKind.AndAnd : Match('=') ? TokenKind.AndAssign : TokenKind.Amp; break;
            case '|': kind = Match('|') ? TokenKind.OrOr : Match('=') ? TokenKind.OrAssign : TokenKind.Pipe; break;
            case '^': kind = Match('=') ? TokenKind.XorAssign : TokenKind.Caret; break;
            case '~': kind = TokenKind.Tilde; break;
            case '?': kind = TokenKind.Question; break;
            case ':': kind = TokenKind.Colon; break;
            case ';': kind = TokenKind.Semicolon; break;
            case ',': kind = TokenKind.Comma; break;
            case '.': kind = TokenKind.Dot; break;
            case '(': kind = TokenKind.LParen; break;
            case ')': kind = TokenKind.RParen; break;
            case '[': kind = TokenKind.LBracket; break;
            case ']': kind = TokenKind.RBracket; break;
            case '{': kind = TokenKind.LBrace; break;
            case '}': kind = TokenKind.RBrace; break;
            case '#': kind = TokenKind.Hash; break;
            default:
                Diag.Error(SourceRange.From(_file, line, col), $"非法字符 '{c}'");
                kind = TokenKind.Unknown; break;
        }
        return new Token(kind, text, SourceRange.From(_file, line, col)) { AtLineStart = atLineStart };
    }

    private bool Match(char c)
    {
        if (_pos < _src.Length && _src[_pos] == c) { Advance(); return true; }
        return false;
    }
}
