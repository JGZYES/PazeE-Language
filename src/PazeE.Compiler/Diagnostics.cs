namespace PazeE.Compiler;

/// <summary>编译期诊断（错误/警告）收集器。</summary>
public sealed class Diagnostics
{
    private readonly List<Diagnostic> _items = new();
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);
    public IReadOnlyList<Diagnostic> Items => _items;

    public void Error(SourceRange range, string message) =>
        _items.Add(new Diagnostic(Severity.Error, range, message));

    public void Warning(SourceRange range, string message) =>
        _items.Add(new Diagnostic(Severity.Warning, range, message));

    public void Print(TextWriter writer)
    {
        foreach (var d in _items)
        {
            var where = d.Range.IsEmpty ? "" : $"{d.Range.File}({d.Range.StartLine},{d.Range.StartCol}): ";
            writer.WriteLine($"{d.Severity.ToString().ToLower()} {where}{d.Message}");
        }
    }
}

public enum Severity { Error, Warning }

public readonly record struct Diagnostic(Severity Severity, SourceRange Range, string Message);

/// <summary>源码位置范围（行/列，1-based）。</summary>
public readonly record struct SourceRange(string File, int StartLine, int StartCol, int EndLine, int EndCol)
{
    public static readonly SourceRange Empty = new("", 0, 0, 0, 0);
    public bool IsEmpty => StartLine == 0 && StartCol == 0;

    public static SourceRange From(string file, int line, int col) =>
        new(file, line, col, line, col);

    public SourceRange Merge(SourceRange other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new SourceRange(File, StartLine, StartCol, other.EndLine, other.EndCol);
    }
}
