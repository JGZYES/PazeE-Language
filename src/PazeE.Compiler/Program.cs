using PazeE.Compiler.Binary;
using PazeE.Compiler.CodeGen;
using PazeE.Compiler.CodeGen.Arm64;
using PazeE.Compiler.CodeGen.X64;
using PazeE.Compiler.Lexer;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Runtime;
using PazeE.Compiler.Semantic;

namespace PazeE.Compiler;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine($"paze {BuildInfo.Version} ({BuildInfo.Channel})");

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }
        if (args[0] is "-v" or "--version") return 0;

        // 解析参数
        string? srcFile = null;
        string? outFile = null;
        Platform target = HostPlatform();
        Arch arch = Arch.Amd;
        string emit = "exe";
        bool printAst = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-o": outFile = args[++i]; break;
                case "--target": target = ParsePlatform(args[++i]); break;
                case "-arch":
                case "--arch":
                    arch = ParseArch(args[++i]);
                    break;
                case "--emit": emit = args[++i]; break;
                case "--print-ast": printAst = true; break;
                default:
                    if (srcFile != null) { Console.Error.WriteLine($"意外的参数 '{a}'"); return 1; }
                    srcFile = a;
                    break;
            }
        }
        if (srcFile == null) { PrintUsage(); return 1; }

        var diag = new Diagnostics();
        var targetInfo = new TargetInfo { Platform = target, Arch = arch };

        // 预处理（自动注入 paze.h）
        string source;
        try { source = File.ReadAllText(srcFile); }
        catch (Exception e) { Console.Error.WriteLine($"无法读取源文件 '{srcFile}': {e.Message}"); return 1; }

        var srcDir = Path.GetDirectoryName(Path.GetFullPath(srcFile)) ?? "";
        var pp = new Preprocessor(diag, new[] { srcDir }, name => name == "paze.h" ? LibcDecls.PazeHeader : null);
        // 按目标平台注入预定义宏，让 paze.h 用 #ifdef 切换平台实现
        string platformDefs = target switch
        {
            Platform.Windows => "#define _WIN32 1\n#define _WIN64 1\n",
            Platform.Linux => "#define __linux__ 1\n#define __linux 1\n",
            Platform.MacOS => "#define __APPLE__ 1\n#define __MACH__ 1\n",
            Platform.Los4 => "#define __leonos__ 1\n#define __los4__ 1\n",
            _ => ""
        };
        platformDefs += arch switch
        {
            Arch.Arm => "#define __arm64__ 1\n#define __aarch64__ 1\n",
            _ => "#define __x86_64__ 1\n#define __amd64__ 1\n"
        };
        var tokens = pp.Run(platformDefs + "#include <paze.h>\n" + source, srcFile);

        if (diag.HasErrors) { diag.Print(Console.Error); return 1; }

        // 解析
        var parser = new PazeE.Compiler.Parser.Parser(tokens, diag, targetInfo);
        var unit = parser.Parse();
        if (diag.HasErrors) { diag.Print(Console.Error); return 1; }

        if (printAst) PrintAst(unit);

        // 语义分析
        var sema = new Sema(diag, targetInfo);
        if (!sema.Analyze(unit)) { diag.Print(Console.Error); return 1; }

        if (emit == "ast") return 0;

        // 代码生成
        ICodeGenerator cg = arch == Arch.Arm
            ? new Arm64CodeGenerator(targetInfo)
            : new X64CodeGenerator(targetInfo);
        var img = cg.Generate(unit, sema);

        if (emit == "asm") { DumpAsm(img); return 0; }

        // 写入可执行文件
        IExecutableWriter writer = (target, arch) switch
        {
            (Platform.Windows, Arch.Arm) => new PeWriterArm64(),
            (Platform.Windows, _) => new PeWriter(),
            (Platform.Linux, Arch.Arm) => new ElfWriterArm64(),
            (Platform.Linux, _) => new ElfWriter(),
            (Platform.MacOS, Arch.Arm) => new MachOWriterArm64(),
            (Platform.MacOS, _) => new MachOWriter(),
            (Platform.Los4, _) => new ElfWriterLos4(),
            _ => new PeWriter()
        };

        byte[] exe;
        try { exe = writer.Write(img); }
        catch (NotImplementedException) { Console.Error.WriteLine($"平台 {target} 的写入器尚未实现"); return 1; }

        outFile ??= DefaultOutputName(srcFile, target);
        File.WriteAllBytes(outFile, exe);
        string archName = arch == Arch.Arm ? "ARM64" : "x86-64";
        Console.WriteLine($"已生成 {outFile}（{exe.Length} 字节，{target}/{archName}）");
        return 0;
    }

    private static Platform HostPlatform() => Environment.OSVersion.Platform == PlatformID.Unix
        ? (Directory.Exists("/System/Library/Frameworks") ? Platform.MacOS : Platform.Linux)
        : Platform.Windows;

    private static Platform ParsePlatform(string s) => s.ToLowerInvariant() switch
    {
        "win" or "windows" => Platform.Windows,
        "linux" => Platform.Linux,
        "mac" or "macos" => Platform.MacOS,
        "los4" or "leonos4" or "leonos" => Platform.Los4,
        _ => Platform.Windows
    };

    private static Arch ParseArch(string s) => s.ToLowerInvariant() switch
    {
        "arm" or "arm64" or "aarch64" => Arch.Arm,
        "amd" or "amd64" or "x64" or "x86-64" => Arch.Amd,
        _ => Arch.Amd
    };

    private static string DefaultOutputName(string src, Platform target)
    {
        var baseName = Path.GetFileNameWithoutExtension(src);
        return target == Platform.Windows ? baseName + ".exe" : baseName;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法: paze <source.pe> [选项]");
        Console.WriteLine("  -o <out>            指定输出文件名");
        Console.WriteLine("  --target <platform> win|linux|macos|los4（默认宿主）");
        Console.WriteLine("  --arch <arch>       amd|arm（默认 amd=x86-64；arm=AArch64 三平台）");
        Console.WriteLine("  --emit <kind>       ast|asm|exe（默认 exe）");
        Console.WriteLine("  --print-ast         打印 AST");
        Console.WriteLine("  -v / --version      显示版本");
    }

    private static void PrintAst(TranslationUnit unit)
    {
        foreach (var d in unit.Decls)
            Console.WriteLine(d.GetType().Name + ": " + (d is FunctionDecl f ? f.Name : d is VarDecl v ? v.Name : ""));
    }

    private static void DumpAsm(ObjectImage img)
    {
        var bytes = img.Text.Data;
        Console.WriteLine($".text ({bytes.Count} 字节)");
        for (int i = 0; i < bytes.Count; i += 16)
        {
            Console.Write($"{i:X4}  ");
            for (int j = 0; j < 16 && i + j < bytes.Count; j++) Console.Write($"{bytes[i + j]:X2} ");
            Console.WriteLine();
        }
        Console.WriteLine($".rdata ({img.RData.Data.Count})  .data ({img.Data.Data.Count})  .bss ({img.Bss.BssSize})");
        Console.WriteLine($"符号: {img.Symbols.Count}  外部: {string.Join(", ", img.Externals)}");
        Console.WriteLine($"重定位: {img.Fixups.Count}");
    }
}
