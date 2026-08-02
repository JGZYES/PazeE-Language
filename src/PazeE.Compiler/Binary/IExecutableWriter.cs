using PazeE.Compiler.Binary;
using PazeE.Compiler.Parser;

namespace PazeE.Compiler.Binary;

/// <summary>可执行文件写入器：把 ObjectImage 落盘为指定平台原生可执行文件。</summary>
public interface IExecutableWriter
{
    Platform Platform { get; }
    byte[] Write(ObjectImage image);
}
