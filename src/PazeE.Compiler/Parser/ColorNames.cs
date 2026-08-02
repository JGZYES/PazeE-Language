namespace PazeE.Compiler.Parser;

/// <summary>color 命名空间支持：把 color.red / color.255.0.0 折叠为编译期常量。
/// 颜色编码：0x80RRGGBB（高位 0x80 作为 sentinel，与负数 0xFF... 区分，编译期可识别）。
/// Sema / CodeGen 对此完全透明——只看到一个 IntLiteral。</summary>
public static class ColorNames
{
    private const long Sentinel = 0x80000000L;

    /// <summary>把 RGB 三分量打包成 0x80RRGGBB。</summary>
    public static long PackRgb(int r, int g, int b)
    {
        uint packed = 0x80000000u
                    | ((uint)(r & 0xFF) << 16)
                    | ((uint)(g & 0xFF) << 8)
                    | (uint)(b & 0xFF);
        return (long)packed;
    }

    /// <summary>判断一个常量是否为 color 编码（高位 sentinel）。</summary>
    public static bool IsColorValue(long v) => (v & 0xFF000000L) == Sentinel;

    /// <summary>从 0x80RRGGBB 解出 R/G/B。</summary>
    public static (int r, int g, int b) Unpack(long v) =>
        ((int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));

    private static readonly Dictionary<string, long> _named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"]   = PackRgb(0x00, 0x00, 0x00),
        ["white"]   = PackRgb(0xFF, 0xFF, 0xFF),
        ["red"]     = PackRgb(0xFF, 0x00, 0x00),
        ["green"]   = PackRgb(0x00, 0xFF, 0x00),
        ["blue"]    = PackRgb(0x00, 0x00, 0xFF),
        ["yellow"]  = PackRgb(0xFF, 0xFF, 0x00),
        ["cyan"]    = PackRgb(0x00, 0xFF, 0xFF),
        ["magenta"] = PackRgb(0xFF, 0x00, 0xFF),
        ["gray"]    = PackRgb(0x80, 0x80, 0x80),
        ["grey"]    = PackRgb(0x80, 0x80, 0x80),
        ["orange"]  = PackRgb(0xFF, 0xA5, 0x00),
        ["pink"]    = PackRgb(0xFF, 0xC0, 0xCB),
        ["purple"]  = PackRgb(0x80, 0x00, 0x80),
        ["brown"]   = PackRgb(0xA5, 0x2A, 0x2A),
        ["navy"]    = PackRgb(0x00, 0x00, 0x80),
        ["teal"]    = PackRgb(0x00, 0x80, 0x80),
        ["olive"]   = PackRgb(0x80, 0x80, 0x00),
        ["maroon"]  = PackRgb(0x80, 0x00, 0x00),
        ["silver"]  = PackRgb(0xC0, 0xC0, 0xC0),
        ["gold"]    = PackRgb(0xFF, 0xD7, 0x00),
        ["lime"]    = PackRgb(0xBF, 0xFF, 0x00),
        ["indigo"]  = PackRgb(0x4B, 0x00, 0x82),
        ["violet"]  = PackRgb(0xEE, 0x82, 0xEE),
        ["darkred"]     = PackRgb(0x8B, 0x00, 0x00),
        ["darkgreen"]   = PackRgb(0x00, 0x64, 0x00),
        ["darkblue"]    = PackRgb(0x00, 0x00, 0x8B),
        ["lightred"]    = PackRgb(0xFF, 0xA0, 0xA0),
        ["lightgreen"]  = PackRgb(0x90, 0xEE, 0x90),
        ["lightblue"]   = PackRgb(0xAD, 0xD8, 0xE6),
        ["lightgray"]   = PackRgb(0xD3, 0xD3, 0xD3),
        ["lightgrey"]   = PackRgb(0xD3, 0xD3, 0xD3),
        ["darkgray"]    = PackRgb(0xA9, 0xA9, 0xA9),
        ["darkgrey"]    = PackRgb(0xA9, 0xA9, 0xA9),
    };

    /// <summary>按名称解析命名颜色，未知名返回 null。</summary>
    public static long? ResolveNamed(string name) =>
        _named.TryGetValue(name, out var v) ? v : null;
}
