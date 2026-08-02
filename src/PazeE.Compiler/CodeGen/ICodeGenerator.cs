using PazeE.Compiler.Binary;
using PazeE.Compiler.Parser;
using PazeE.Compiler.Semantic;

namespace PazeE.Compiler.CodeGen;

/// <summary>代码生成器接口：将语义分析后的 AST 翻译为目标平台原生机器码映像。</summary>
public interface ICodeGenerator
{
    /// <summary>目标平台信息。</summary>
    TargetInfo Target { get; }

    /// <summary>对翻译单元进行代码生成，返回填充好的可执行映像。</summary>
    ObjectImage Generate(TranslationUnit unit, Sema sema);
}
