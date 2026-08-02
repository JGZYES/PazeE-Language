namespace PazeE.Compiler.Lexer;

public readonly struct Token
{
    public TokenKind Kind { get; init; }
    public string Text { get; init; }
    public SourceRange Range { get; init; }
    /// <summary>整型字面量值（含字符字面量）。支持十进制/十六进制/八进制。</summary>
    public long IntValue { get; init; }
    /// <summary>字符串字面量解码后的内容（不含引号）。</summary>
    public string? StringValue { get; init; }
    /// <summary>是否在哈希行首（用于预处理）。</summary>
    public bool AtLineStart { get; init; }

    /// <summary>预处理宏展开隐藏集（防止宏递归展开）。</summary>
    public HashSet<string>? Hide { get; init; }

    public Token(TokenKind kind, string text, SourceRange range)
    {
        Kind = kind;
        Text = text;
        Range = range;
        IntValue = 0;
        StringValue = null;
        AtLineStart = false;
    }

    public bool Is(TokenKind k) => Kind == k;
    public bool IsOneOf(params TokenKind[] ks) => ks.Contains(Kind);

    public override string ToString() => Kind == TokenKind.Identifier || Kind == TokenKind.IntLiteral
        ? $"{Kind}({Text})"
        : Kind.ToString();
}

public static class Keywords
{
    private static readonly Dictionary<string, TokenKind> Map = new(StringComparer.Ordinal)
    {
        ["void"] = TokenKind.KwVoid,
        ["char"] = TokenKind.KwChar,
        ["short"] = TokenKind.KwShort,
        ["int"] = TokenKind.KwInt,
        ["long"] = TokenKind.KwLong,
        ["unsigned"] = TokenKind.KwUnsigned,
        ["signed"] = TokenKind.KwSigned,
        ["const"] = TokenKind.KwConst,
        ["static"] = TokenKind.KwStatic,
        ["extern"] = TokenKind.KwExtern,
        ["register"] = TokenKind.KwRegister,
        ["volatile"] = TokenKind.KwVolatile,
        ["auto"] = TokenKind.KwAuto,
        ["_Bool"] = TokenKind.KwBool,
        ["struct"] = TokenKind.KwStruct,
        ["union"] = TokenKind.KwUnion,
        ["enum"] = TokenKind.KwEnum,
        ["typedef"] = TokenKind.KwTypedef,
        ["sizeof"] = TokenKind.KwSizeof,
        ["typeof"] = TokenKind.KwTypeof,
        ["__typeof__"] = TokenKind.KwTypeof,
        ["return"] = TokenKind.KwReturn,
        ["if"] = TokenKind.KwIf,
        ["else"] = TokenKind.KwElse,
        ["while"] = TokenKind.KwWhile,
        ["do"] = TokenKind.KwDo,
        ["for"] = TokenKind.KwFor,
        ["switch"] = TokenKind.KwSwitch,
        ["case"] = TokenKind.KwCase,
        ["default"] = TokenKind.KwDefault,
        ["break"] = TokenKind.KwBreak,
        ["continue"] = TokenKind.KwContinue,
        ["goto"] = TokenKind.KwGoto,
    };

    public static TokenKind? Lookup(string ident) => Map.TryGetValue(ident, out var k) ? k : null;
}
