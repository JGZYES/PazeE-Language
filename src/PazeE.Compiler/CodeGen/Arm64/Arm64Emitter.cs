namespace PazeE.Compiler.CodeGen.Arm64;

/// <summary>AArch64 指令编码器（独立于 X64Emitter，不共享任何逻辑）。
/// 产出定长 4 字节小端机器码到内部 List&lt;byte&gt;。提供 codegen 所需的全部指令；
/// 标签/分支用带 kind 的 Patch 记录，Finish() 按 A64 各分支 imm 宽度回填
/// （B/BL=imm26，B.cond/CBZ/CBNZ=imm19；目标相对指令自身，无 +4）。
/// 所有算术/逻辑运算均在 64 位（X 寄存器）上进行；窄类型由 Load 的符号/零扩展与 Store 的截断处理
/// （与 X64 后端在 Rax(64 位) 上运算、按宽度加载的策略一致）。
/// 注意：A64 指令编码常量（如 0xF9400000）超过 int.MaxValue，故 Emit32/Write32/Read32 及
/// LoadOp/StoreOp 等均用 uint 表示指令字，避免 C# 整型溢出。</summary>
public sealed class Arm64Emitter
{
    private readonly List<byte> _code = new();
    private readonly List<Patch> _patches = new();
    private readonly Dictionary<int, int> _labels = new();
    private int _nextLabel;

    public int Position => _code.Count;
    public IReadOnlyList<byte> Code => _code;
    public byte[] ToArray() => _code.ToArray();

    public void Emit32(uint v)
    {
        _code.Add((byte)v);
        _code.Add((byte)(v >> 8));
        _code.Add((byte)(v >> 16));
        _code.Add((byte)(v >> 24));
    }
    private void Write32(int off, uint v)
    {
        _code[off] = (byte)v;
        _code[off + 1] = (byte)(v >> 8);
        _code[off + 2] = (byte)(v >> 16);
        _code[off + 3] = (byte)(v >> 24);
    }
    private static uint Read32(List<byte> b, int off) => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    // ---- 标签 ----
    public int NewLabel() => _nextLabel++;
    public void MarkLabel(int l) { _labels[l] = _code.Count; }
    public void Finish()
    {
        foreach (var p in _patches)
        {
            if (!_labels.TryGetValue(p.Label, out int target)) target = 0;
            int delta = target - p.Off;
            uint cur = Read32(_code, p.Off);
            switch (p.Kind)
            {
                case PatchKind.B:
                    Write32(p.Off, 0x14000000u | (uint)Imm26(delta));
                    break;
                case PatchKind.BL:
                    Write32(p.Off, 0x94000000u | (uint)Imm26(delta));
                    break;
                case PatchKind.Bcc:
                    Write32(p.Off, (cur & 0xFF00001Fu) | (uint)((Imm19(delta) & 0x7FFFF) << 5));
                    break;
                case PatchKind.Cbz:
                case PatchKind.Cbnz:
                    Write32(p.Off, (cur & 0xFF00001Fu) | (uint)((Imm19(delta) & 0x7FFFF) << 5));
                    break;
            }
        }
    }
    private static int Imm26(int d) => (d >> 2) & 0x3FFFFFF;
    private static int Imm19(int d) => (d >> 2) & 0x7FFFF;

    private enum PatchKind { B, BL, Bcc, Cbz, Cbnz }
    private readonly record struct Patch(int Off, int Label, PatchKind Kind);

    // ============ MOV / 立即数 ============
    public void MovRR(Arm64Reg dst, Arm64Reg src)
    {   // ORR Rd, XZR, Rm
        Emit32(0xAA0003E0u | (uint)(src.Index << 16) | dst.Index);
    }

    public void MovImm(Arm64Reg dst, long imm)
    {
        // MOVZ 低 16 位（同时清零高位），再按需 MOVK
        Emit32(0xD2800000u | (uint)(((int)(imm & 0xFFFF)) << 5) | dst.Index);
        for (int hw = 1; hw <= 3; hw++)
        {
            int chunk = (int)((imm >> (16 * hw)) & 0xFFFF);
            if (chunk != 0)
                Emit32(0xF2800000u | (uint)(hw << 21) | (uint)(chunk << 5) | dst.Index);
        }
    }

    // ============ 算术/逻辑（reg,reg）============
    public void Add(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x8B000000u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void Sub(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0xCB000000u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void And(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x8A000000u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void Orr(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0xAA000000u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void Eor(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0xCA000000u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void Cmp(Arm64Reg a, Arm64Reg b) => Emit32(0xEB00001Fu | (uint)(b.Index << 16) | (uint)(a.Index << 5));     // SUBS XZR
    public void Tst(Arm64Reg a, Arm64Reg b) => Emit32(0xEA00001Fu | (uint)(b.Index << 16) | (uint)(a.Index << 5));     // ANDS XZR
    public void Mul(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x1B007C00u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);  // MADD Ra=XZR
    public void Sdiv(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x9AC00C00u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void Udiv(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x9AC00800u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    /// <summary>d = a - b*d 的低 ... 实为 MSUB：d = Ra - Rn*Rm。用于取模：d=Ra, a=Ra(被除数), b=除数, ra=商。</summary>
    public void Msub(Arm64Reg d, Arm64Reg b, Arm64Reg a, Arm64Reg ra) => Emit32(0x1B008000u | (uint)(b.Index << 16) | (uint)(ra.Index << 10) | (uint)(a.Index << 5) | d.Index);

    public void Neg(Arm64Reg d, Arm64Reg s) => Emit32(0xCB0003E0u | (uint)(s.Index << 16) | d.Index);   // SUB d,XZR,s
    public void Mvn(Arm64Reg d, Arm64Reg s) => Emit32(0xAA2003E0u | (uint)(s.Index << 16) | d.Index);   // ORN d,XZR,s

    // ---- 移位（reg）----
    public void LslReg(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x9AC02000u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void LsrReg(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x9AC02400u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);
    public void AsrReg(Arm64Reg d, Arm64Reg a, Arm64Reg b) => Emit32(0x9AC02800u | (uint)(b.Index << 16) | (uint)(a.Index << 5) | d.Index);

    // ---- 移位（imm，64 位）----
    public void LslImm(Arm64Reg d, Arm64Reg s, int shift)
    {
        if (shift == 0) { if (!SameReg(d, s)) MovRR(d, s); return; }
        int immr = (64 - shift) & 63, imms = 63 - shift;
        Emit32(0xD3400000u | (uint)(immr << 16) | (uint)(imms << 10) | (uint)(s.Index << 5) | d.Index);  // UBFM (LSL)
    }
    public void LsrImm(Arm64Reg d, Arm64Reg s, int shift)
    {
        if (shift == 0) { if (!SameReg(d, s)) MovRR(d, s); return; }
        Emit32(0xD340FC00u | (uint)(shift << 16) | (uint)(s.Index << 5) | d.Index);  // UBFM imms=63
    }
    public void AsrImm(Arm64Reg d, Arm64Reg s, int shift)
    {
        if (shift == 0) { if (!SameReg(d, s)) MovRR(d, s); return; }
        Emit32(0x9340FC00u | (uint)(shift << 16) | (uint)(s.Index << 5) | d.Index);  // SBFM imms=63
    }

    /// <summary>符号/零扩展窄寄存器到 64 位：SXTB/SXTH/SXTW 或 UXTB/UXTH/UXTW。</summary>
    public void Extend(Arm64Reg d, Arm64Reg s, int fromSize, bool unsigned)
    {
        // UBFM/SBFM。fromSize:1→byte,2→half,4→word
        int imms = (fromSize == 1) ? 7 : (fromSize == 2) ? 15 : 31;
        uint baseOp = unsigned ? 0xD3400000u : 0x93400000u;  // UBFM / SBFM
        int immr = 0;
        Emit32(baseOp | (uint)(immr << 16) | (uint)(imms << 10) | (uint)(s.Index << 5) | d.Index);
    }

    // ============ 立即数算术（12 位）============
    public void AddImm(Arm64Reg d, Arm64Reg s, long imm)
    {
        if (imm >= 0 && imm <= 4095)
            Emit32(0x91000000u | (uint)((int)imm << 10) | (uint)(s.Index << 5) | d.Index);
        else if (imm >= -4095 && imm < 0)
            Emit32(0xD1000000u | (uint)((int)(-imm) << 10) | (uint)(s.Index << 5) | d.Index);  // SUB
        else
        { MovImm(AR.X2, imm); Emit32(0x8B000000u | (2u << 16) | (uint)(s.Index << 5) | d.Index); }  // ADD d,s,X2
    }
    public void SubImm(Arm64Reg d, Arm64Reg s, long imm)
    {
        if (imm >= 0 && imm <= 4095)
            Emit32(0xD1000000u | (uint)((int)imm << 10) | (uint)(s.Index << 5) | d.Index);
        else if (imm >= -4095 && imm < 0)
            Emit32(0x91000000u | (uint)((int)(-imm) << 10) | (uint)(s.Index << 5) | d.Index);  // ADD
        else
        { MovImm(AR.X2, imm); Emit32(0xCB000000u | (2u << 16) | (uint)(s.Index << 5) | d.Index); }  // SUB d,s,X2
    }
    public void CmpImm(Arm64Reg a, long imm)
    {
        if (imm >= 0 && imm <= 4095)
            Emit32(0xF1000000u | (uint)((int)imm << 10) | (uint)(a.Index << 5) | 31u);  // SUBS XZR,a,#imm
        else
        { MovImm(AR.X2, imm); Emit32(0xEB00001Fu | (2u << 16) | (uint)(a.Index << 5)); }  // CMP a,X2
    }
    public void AndImm(Arm64Reg d, Arm64Reg s, long imm)
    {
        // AND 立即数编码较复杂（位域 N:imms:immr）；用 MovImm+AND(reg) 规避
        MovImm(AR.X2, imm);
        Emit32(0x8A000000u | (2u << 16) | (uint)(s.Index << 5) | d.Index);
    }

    // ============ 访存 ============
    /// <summary>加载。size=1/2/4/8 字节；sign=true 时符号扩展到 64 位，否则零扩展。</summary>
    public void Load(Arm64Reg dst, Arm64Mem m, int size, bool sign)
    {
        if (m.Index.HasValue)
        {
            // 寄存器变址 LDR（零扩展），供 CopyBytes 的 qword(8)/byte(1) 循环使用
            Emit32(LoadRegOp(size) | (uint)(m.Index.Value.Index << 16) | (uint)(m.IndexScale << 12) | (uint)(m.Base!.Value.Index << 5) | dst.Index);
            return;
        }
        var bse = m.Base!.Value;
        long d = m.Disp;
        if (d >= 0 && (d % size) == 0 && (d / size) <= 4095)
        {
            Emit32(LoadOp(size, sign) | (uint)((int)(d / size) << 10) | (uint)(bse.Index << 5) | dst.Index);
        }
        else
        {
            MaterializeAddr(AR.X2, bse, d);
            Emit32(LoadOp(size, sign) | (0u << 10) | (uint)(AR.X2.Index << 5) | dst.Index);
        }
    }
    public void Store(Arm64Reg src, Arm64Mem m, int size)
    {
        if (m.Index.HasValue)
        {
            Emit32(StoreRegOp(size) | (uint)(m.Index.Value.Index << 16) | (uint)(m.IndexScale << 12) | (uint)(m.Base!.Value.Index << 5) | src.Index);
            return;
        }
        var bse = m.Base!.Value;
        long d = m.Disp;
        if (d >= 0 && (d % size) == 0 && (d / size) <= 4095)
        {
            Emit32(StoreOp(size) | (uint)((int)(d / size) << 10) | (uint)(bse.Index << 5) | src.Index);
        }
        else
        {
            MaterializeAddr(AR.X2, bse, d);
            Emit32(StoreOp(size) | (0u << 10) | (uint)(AR.X2.Index << 5) | src.Index);
        }
    }
    private static uint LoadOp(int size, bool sign) => (size, sign) switch
    {
        (8, _)    => 0xF9400000u,  // LDR Xt
        (4, true) => 0xB9800000u,  // LDRSW
        (4, false)=> 0xB9400000u,  // LDR Wt（零扩展到 Xt）
        (2, true) => 0x79800000u,  // LDRSH
        (2, false)=> 0x79400000u,  // LDRH
        (1, true) => 0x39800000u,  // LDRSB
        (1, false)=> 0x39400000u,  // LDRB
        _ => 0xF9400000u,
    };
    private static uint StoreOp(int size) => size switch
    {
        8 => 0xF9000000u,  // STR Xt
        4 => 0xB9000000u,  // STR Wt
        2 => 0x79000000u,  // STRH
        1 => 0x39000000u,  // STRB
        _ => 0xF9000000u,
    };
    private static uint LoadRegOp(int size) => size switch   // 寄存器变址 LDR（零扩展）
    {
        8 => 0xF8606800u, 4 => 0xB8606800u, 2 => 0x78606800u, 1 => 0x38606800u, _ => 0xF8606800u,
    };
    private static uint StoreRegOp(int size) => size switch  // 寄存器变址 STR
    {
        8 => 0xF8206800u, 4 => 0xB8206800u, 2 => 0x78206800u, 1 => 0x38206800u, _ => 0xF8206800u,
    };
    private void MaterializeAddr(Arm64Reg tmp, Arm64Reg bse, long d)
    {
        if (d == 0) { if (!SameReg(tmp, bse)) MovRR(tmp, bse); }
        else if (d >= 1 && d <= 4095) Emit32(0x91000000u | (uint)((int)d << 10) | (uint)(bse.Index << 5) | tmp.Index);
        else if (d >= -4095 && d <= -1) Emit32(0xD1000000u | (uint)((int)(-d) << 10) | (uint)(bse.Index << 5) | tmp.Index);
        else { MovImm(tmp, d); Emit32(0x8B000000u | (uint)(tmp.Index << 16) | (uint)(bse.Index << 5) | tmp.Index); }
    }

    // ============ 符号地址物化 ============
    /// <summary>发射 ADRP+ADD 占位（Rd=dst），返回 ADRP 指令偏移（供 codegen 记 AdrpAdd fixup）。
    /// 实际页/偏移由 ElfWriterArm64/PeWriterArm64/MachOWriterArm64 在 fixup 解析时回填。</summary>
    public int AdrpAdd(Arm64Reg dst, string sym)
    {
        int off = _code.Count;
        Emit32(0x90000000u | dst.Index);  // ADRP 占位
        Emit32(0x91000000u | (uint)(dst.Index << 5) | dst.Index);  // ADD dst,dst,#0 占位
        return off;
    }

    // ============ 控制流 ============
    /// <summary>发射 BL（imm26 占位），返回指令偏移（供 codegen 记 Call26/ExtCall26 fixup）。</summary>
    public int Bl() { int off = _code.Count; Emit32(0x94000000u); return off; }
    public void Blr(Arm64Reg r) => Emit32(0xD63F0000u | (uint)(r.Index << 5));
    public void Br(Arm64Reg r) => Emit32(0xD61F0000u | (uint)(r.Index << 5));
    public void Ret() => Emit32(0xD65F03C0u);  // RET X30
    public void Nop() => Emit32(0xD503201Fu);
    public void Svc() => Emit32(0xD4000001u);  // SVC #0

    public void B(int label) { _patches.Add(new(_code.Count, label, PatchKind.B)); Emit32(0x14000000u); }
    public void Bl(int label) { _patches.Add(new(_code.Count, label, PatchKind.BL)); Emit32(0x94000000u); }
    public void Bcc(ACond cc, int label) { _patches.Add(new(_code.Count, label, PatchKind.Bcc)); Emit32(0x54000000u | (uint)cc); }
    public void Cbz(Arm64Reg r, int label) { _patches.Add(new(_code.Count, label, PatchKind.Cbz)); Emit32(0xB4000000u | r.Index); }
    public void Cbnz(Arm64Reg r, int label) { _patches.Add(new(_code.Count, label, PatchKind.Cbnz)); Emit32(0xB5000000u | r.Index); }
    public void Cset(ACond cc, Arm64Reg dst)
    {   // CSINV dst, XZR, XZR, invert(cc)
        Emit32(0xDA9F03E0u | (uint)(((int)cc ^ 1) << 12) | dst.Index);
    }

    /// <summary>Test Bit and Branch if Non-Zero (TBNZ)。</summary>
    public void Tbnz(Arm64Reg r, int bit, int label)
    {
        _patches.Add(new(_code.Count, label, PatchKind.Bcc)); // 复用 Bcc patch 类型
        Emit32(0x37000000u | (uint)((bit & 0x3F) << 5) | r.Index);
    }

    /// <summary>Test Bit and Branch if Zero (TBZ)。</summary>
    public void Tbz(Arm64Reg r, int bit, int label)
    {
        _patches.Add(new(_code.Count, label, PatchKind.Bcc));
        Emit32(0x36000000u | (uint)((bit & 0x3F) << 5) | r.Index);
    }

    // ============ 栈帧 ============
    public void StpPre(Arm64Reg rt1, Arm64Reg rt2, Arm64Reg bse, int imm)  // STP rt1,rt2,[bse,#imm]!  imm 须为 8 倍数
    {
        int imm7 = imm / 8;
        Emit32(0xA9800000u | (uint)((imm7 & 0x7F) << 15) | (uint)(rt2.Index << 10) | (uint)(bse.Index << 5) | rt1.Index);
    }
    public void LdpPost(Arm64Reg rt1, Arm64Reg rt2, Arm64Reg bse, int imm) // LDP rt1,rt2,[bse],#imm
    {
        int imm7 = imm / 8;
        Emit32(0xA8C00000u | (uint)((imm7 & 0x7F) << 15) | (uint)(rt2.Index << 10) | (uint)(bse.Index << 5) | rt1.Index);
    }
    public void SubSp(int imm) => Emit32(0xD1000000u | (uint)((int)imm << 10) | (uint)(AR.SP.Index << 5) | AR.SP.Index);  // SUB SP,SP,#imm
    public void AddSp(int imm) => Emit32(0x91000000u | (uint)((int)imm << 10) | (uint)(AR.SP.Index << 5) | AR.SP.Index);  // ADD SP,SP,#imm

    // ============ 静态编码辅助（供三个 ARM 写入器复用，编码 PLT/桩/fixup 回填）============
    // 返回 int（unchecked），因写入器的 Write32At 接收 int；位模式正确即可正确写出小端字节。
    public static int EncodeBl(int imm26) => unchecked((int)(0x94000000u | (uint)(imm26 & 0x3FFFFFF)));
    public static int EncodeAdrp(int immlo, int immhi, int rd) => unchecked((int)(0x90000000u | ((uint)(immlo & 3) << 29) | ((uint)(immhi & 0x7FFFF) << 5) | (uint)(rd & 0x1F)));
    public static int EncodeAddImm(int rd, int rn, int imm12, bool sf) => unchecked((int)((sf ? 0x91000000u : 0x11000000u) | ((uint)(imm12 & 0xFFF) << 10) | ((uint)(rn & 0x1F) << 5) | (uint)(rd & 0x1F)));
    /// <summary>64 位 LDR (imm12 scaled) 用于 PLT 桩加载 GOT 槽。</summary>
    public static int EncodeLdrImm(int rt, int rn, int imm12) => unchecked((int)(0xF9400000u | ((uint)(imm12 & 0xFFF) << 10) | ((uint)(rn & 0x1F) << 5) | (uint)(rt & 0x1F)));
    public static int EncodeBr(int rn) => unchecked((int)(0xD61F0000u | ((uint)(rn & 0x1F) << 5)));

    private static bool SameReg(Arm64Reg a, Arm64Reg b) => a.Index == b.Index && a.Size == b.Size;
}

/// <summary>A64 条件码（B.cond / CSET）。</summary>
public enum ACond
{
    EQ = 0, NE = 1, CS = 2, CC = 3, MI = 4, PL = 5, VS = 6, VC = 7,
    HI = 8, LS = 9, GE = 10, LT = 11, GT = 12, LE = 13, AL = 14,
    HS = 16, LO = 17,
}
