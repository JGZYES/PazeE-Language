namespace PazeE.Compiler.Lexer;

public enum TokenKind
{
    // 特殊
    Eof,
    Unknown,

    // 字面量
    IntLiteral,
    CharLiteral,
    StringLiteral,

    // 标识符
    Identifier,

    // 关键字
    KwVoid, KwChar, KwShort, KwInt, KwLong, KwUnsigned, KwSigned, KwConst, KwStatic, KwExtern, KwRegister, KwVolatile, KwAuto, KwBool,
    KwStruct, KwUnion, KwEnum, KwTypedef, KwSizeof, KwTypeof, KwReturn, KwIf, KwElse, KwWhile, KwDo, KwFor, KwSwitch, KwCase, KwDefault,
    KwBreak, KwContinue, KwGoto,

    // 运算符与标点
    Plus, Minus, Star, Slash, Percent,
    PlusPlus, MinusMinus,
    Assign,
    PlusAssign, MinusAssign, StarAssign, SlashAssign, PercentAssign,
    ShlAssign, ShrAssign, AndAssign, OrAssign, XorAssign,
    Eq, NotEq, Lt, Le, Gt, Ge,
    AndAnd, OrOr, Not,
    Amp, Pipe, Caret, Tilde,
    Shl, Shr,
    Question, Colon, Semicolon, Comma, Dot, Arrow,
    LParen, RParen, LBracket, RBracket, LBrace, RBrace,
    Hash,  // 供预处理使用，词法后通常不保留
}

public static class TokenKindExtensions
{
    public static bool IsTypeKeyword(this TokenKind k) => k switch
    {
        TokenKind.KwVoid or TokenKind.KwChar or TokenKind.KwShort or TokenKind.KwInt or TokenKind.KwLong
        or TokenKind.KwUnsigned or TokenKind.KwSigned or TokenKind.KwConst or TokenKind.KwStruct
        or TokenKind.KwUnion or TokenKind.KwEnum or TokenKind.KwTypedef or TokenKind.KwBool or TokenKind.KwTypeof => true,
        _ => false,
    };

    public static bool IsAssignment(this TokenKind k) => k switch
    {
        TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign
        or TokenKind.SlashAssign or TokenKind.PercentAssign or TokenKind.ShlAssign or TokenKind.ShrAssign
        or TokenKind.AndAssign or TokenKind.OrAssign or TokenKind.XorAssign => true,
        _ => false,
    };
}
