using System.Security.Cryptography;
using System.Text;

namespace PazeE.Compiler.Binary;

/// <summary>生成 macOS ad-hoc 代码签名（SuperBlob + CodeDirectory，SHA-256）。
/// macOS 11+（尤其 Apple Silicon）要求所有可执行文件至少有 ad-hoc 签名，否则内核 SIGKILL。
/// 代码签名 blob 使用大端字节序（CSBlob 规范），与 Mach-O 主体的小端序不同。</summary>
internal static class MachOCodeSignature
{
    private const uint CSMAGIC_EMBEDDED_SIGNATURE = 0xFade0CC0; // SuperBlob
    private const uint CSMAGIC_CODEDIRECTORY = 0xFade0C02;      // CodeDirectory
    private const uint CSSLOT_CODEDIRECTORY = 0;                 // blob index type
    private const int CS_PAGE_SIZE = 4096;
    private const byte CS_PAGE_SHIFT = 12;                       // log2(4096)
    private const byte CS_HASHTYPE_SHA256 = 2;
    private const byte CS_HASHSIZE_SHA256 = 32;
    private const string DefaultIdentifier = "paze-compiled";

    // CodeDirectory 固定部分（version 0x20001，无 scatterOffset/teamOffset）
    private const int CdFixedLen = 44;
    // SuperBlob 头(12) + 1 个 BlobIndex(8)
    private const int SbHeaderLen = 12 + 8;

    /// <summary>计算代码签名 blob 的确切大小（字节），用于在写入头之前确定 __LINKEDIT 段尺寸。</summary>
    public static int ComputeBlobSize(int codeLimit, string? identifier = null)
    {
        int identLen = Encoding.ASCII.GetBytes((identifier ?? DefaultIdentifier) + "\0").Length;
        int nCodeSlots = (codeLimit + CS_PAGE_SIZE - 1) / CS_PAGE_SIZE;
        int cdLength = CdFixedLen + identLen + nCodeSlots * CS_HASHSIZE_SHA256;
        return SbHeaderLen + cdLength;
    }

    /// <summary>生成代码签名 blob（SuperBlob 包裹 CodeDirectory）。
    /// fileData: 整个文件内容（从 0 到签名 blob 起始位置），codeLimit: 文件中代码部分的字节数（=签名 blob 起始偏移）。</summary>
    public static byte[] Build(byte[] fileData, int codeLimit, string? identifier = null)
    {
        string ident = identifier ?? DefaultIdentifier;
        byte[] identBytes = Encoding.ASCII.GetBytes(ident + "\0");
        int identLen = identBytes.Length;
        int nCodeSlots = (codeLimit + CS_PAGE_SIZE - 1) / CS_PAGE_SIZE;

        int identOffset = CdFixedLen;
        int hashOffset = CdFixedLen + identLen;
        int cdLength = hashOffset + nCodeSlots * CS_HASHSIZE_SHA256;

        // ---- CodeDirectory ----
        var cd = new List<byte>(cdLength);
        Write32BE(cd, CSMAGIC_CODEDIRECTORY);    // magic
        Write32BE(cd, (uint)cdLength);            // length
        Write32BE(cd, 0x20001);                   // version
        Write32BE(cd, 0);                         // flags (ad-hoc)
        Write32BE(cd, (uint)hashOffset);          // hashOffset
        Write32BE(cd, (uint)identOffset);         // identOffset
        Write32BE(cd, 0);                         // nSpecialSlots
        Write32BE(cd, (uint)nCodeSlots);          // nCodeSlots
        Write32BE(cd, (uint)codeLimit);           // codeLimit
        cd.Add(CS_HASHSIZE_SHA256);               // hashSize
        cd.Add(CS_HASHTYPE_SHA256);               // hashType
        cd.Add(0);                                // platform
        cd.Add(CS_PAGE_SHIFT);                    // pageSize (log2)
        Write32BE(cd, 0);                         // spare2
        cd.AddRange(identBytes);                  // identifier (null-terminated)
        // 代码页 SHA-256 哈希
        using var sha256 = SHA256.Create();
        for (int i = 0; i < nCodeSlots; i++)
        {
            int pageOff = i * CS_PAGE_SIZE;
            int pageLen = Math.Min(CS_PAGE_SIZE, codeLimit - pageOff);
            byte[] hash = sha256.ComputeHash(fileData, pageOff, pageLen);
            cd.AddRange(hash);
        }

        // ---- SuperBlob ----
        int sbLength = SbHeaderLen + cdLength;
        var sb = new List<byte>(sbLength);
        Write32BE(sb, CSMAGIC_EMBEDDED_SIGNATURE); // magic
        Write32BE(sb, (uint)sbLength);              // length
        Write32BE(sb, 1);                           // count (1 blob)
        Write32BE(sb, CSSLOT_CODEDIRECTORY);        // index[0].type
        Write32BE(sb, (uint)SbHeaderLen);           // index[0].offset
        sb.AddRange(cd);                            // CodeDirectory

        return sb.ToArray();
    }

    private static void Write32BE(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24));
        b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }
}
