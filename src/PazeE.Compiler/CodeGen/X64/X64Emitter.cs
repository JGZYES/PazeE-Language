namespace PazeE.Compiler.CodeGen.X64;

/// <summary>x64 指令编码器：把汇编指令编码为机器码字节，写入目标字节流。
/// 设计要点：统一使用 rel32 跳转、REX 前缀按需生成、ModR/M+SIB+disp 完整支持。
/// 局部分支用标签（emit 后回填），跨符号引用（call/数据）由调用方记录 Fixup 交写入器解析。</summary>
public sealed class X64Emitter
{
    private readonly List<byte> _code;
    private readonly List<Patch> _patches = new();
    private readonly Dictionary<int, int> _labels = new();
    private int _labelCounter;

    private readonly struct Patch(int off, int label)
    {
        public readonly int Off = off;
        public readonly int Label = label;
    }

    public X64Emitter(List<byte> target) { _code = target; }
    public int Position => _code.Count;
    public IReadOnlyList<byte> Code => _code;

    // ---------------- 基础写 ----------------
    public void Emit8(byte b) => _code.Add(b);
    public void Emit16(ushort v) { _code.Add((byte)v); _code.Add((byte)(v >> 8)); }
    public void Emit32(int v) { _code.Add((byte)v); _code.Add((byte)(v >> 8)); _code.Add((byte)(v >> 16)); _code.Add((byte)(v >> 24)); }
    public void Emit64(long v) { for (int i = 0; i < 8; i++) _code.Add((byte)(v >> (i * 8))); }

    // ---------------- 标签 ----------------
    public int NewLabel() => _labelCounter++;
    public void MarkLabel(int l) { _labels[l] = _code.Count; }

    /// <summary>回填所有局部分支跳转。必须在所有代码生成完毕后调用一次。</summary>
    public void Finish()
    {
        foreach (var p in _patches)
        {
            int target = _labels.TryGetValue(p.Label, out var t) ? t : 0;
            int rel = target - (p.Off + 4);
            _code[p.Off] = (byte)rel;
            _code[p.Off + 1] = (byte)(rel >> 8);
            _code[p.Off + 2] = (byte)(rel >> 16);
            _code[p.Off + 3] = (byte)(rel >> 24);
        }
    }

    // ---------------- REX / ModR/M / SIB ----------------
    private void Rex(byte w, byte r, byte x, byte b)
    {
        if (w != 0 || r != 0 || x != 0 || b != 0)
            _code.Add((byte)(0x40 | (w << 3) | (r << 2) | (x << 1) | b));
    }
    private void ModRM(byte mod, byte reg, byte rm) => _code.Add((byte)((mod << 6) | ((reg & 7) << 3) | (rm & 7)));
    private void Sib(byte ss, byte index, byte bse) => _code.Add((byte)((ss << 6) | ((index & 7) << 3) | (bse & 7)));

    /// <summary>编码内存操作数的 ModR/M[+SIB+disp]，reg 字段为给定寄存器编号。REX 由调用方先写。</summary>
    private void EncodeModRmFor(byte regField, Mem m)
    {
        int bse = m.Base.HasValue ? m.Base.Value.Index : -1;
        int idx = m.Index.HasValue ? m.Index.Value.Index : -1;

        if (m.RipRelative) { ModRM(0b00, regField, 5); Emit32((int)m.Disp); return; }

        bool needSib = bse == 4 || idx >= 0;
        if (bse < 0 && idx < 0)
        {
            ModRM(0b00, regField, 4); Sib(0, 4, 5); Emit32((int)m.Disp); return;
        }

        byte mod;
        if (bse == 5) mod = InDisp8(m.Disp) ? (byte)0b01 : (byte)0b10;        // rbp/r13 不能用 mod=00
        else if (m.Disp == 0) mod = 0b00;
        else if (InDisp8(m.Disp)) mod = 0b01;
        else mod = 0b10;

        int rm = needSib ? 4 : bse;
        ModRM(mod, regField, (byte)rm);
        if (needSib)
        {
            byte ss = m.Scale switch { 2 => 1, 4 => 2, 8 => 3, _ => 0 };
            int sibBase = bse < 0 ? 5 : bse;
            int sibIndex = idx < 0 ? 4 : idx;
            Sib(ss, (byte)sibIndex, (byte)sibBase);
        }
        if (mod == 0b01) Emit8((byte)m.Disp);
        else if (mod == 0b10) Emit32((int)m.Disp);
    }
    private static bool InDisp8(long d) => d >= -128 && d <= 127;

    private void RexFor(byte w, byte regField, Mem m)
    {
        int bse = m.Base.HasValue ? m.Base.Value.Index : -1;
        int idx = m.Index.HasValue ? m.Index.Value.Index : -1;
        Rex(w, (byte)(regField >> 3), idx >= 0 ? (byte)(idx >> 3) : (byte)0, bse >= 0 ? (byte)(bse >> 3) : (byte)0);
    }

    // ---------------- MOV ----------------
    public void MovRR(Reg dst, Reg src)
    {
        bool w = dst.Size == 64;
        // 0x89 = MOV r/m, r：reg=src，rm=dst（与 EmitRR 的 r/m,r 约定一致）
        Rex(w ? (byte)1 : (byte)0, (byte)(src.Index >> 3), 0, (byte)(dst.Index >> 3));
        _code.Add(0x89);
        ModRM(0b11, src.Index, dst.Index);
    }
    public void MovFromMem(Reg dst, Mem m) // 0x8B r, r/m
    {
        RexFor(dst.Size == 64 ? (byte)1 : (byte)0, dst.Index, m);
        _code.Add(0x8B);
        EncodeModRmFor(dst.Index, m);
    }
    public void MovToMem(Mem m, Reg src)   // 0x89 r/m, r
    {
        RexFor(src.Size == 64 ? (byte)1 : (byte)0, src.Index, m);
        _code.Add(0x89);
        EncodeModRmFor(src.Index, m);
    }
    public void Lea(Reg dst, Mem m)         // 0x8D r, r/m
    {
        RexFor(1, dst.Index, m);
        _code.Add(0x8D);
        EncodeModRmFor(dst.Index, m);
    }

    public void MovImm(Reg dst, long imm)
    {
        bool w = dst.Size == 64;
        if (w && imm >= int.MinValue && imm <= int.MaxValue)
        {
            Rex(1, 0, 0, (byte)(dst.Index >> 3)); _code.Add(0xC7); ModRM(0b11, 0, dst.Index); Emit32((int)imm);
        }
        else if (!w)
        {
            Rex(0, 0, 0, (byte)(dst.Index >> 3)); _code.Add((byte)(0xB8 + (dst.Index & 7))); Emit32((int)imm);
        }
        else
        {
            Rex(1, 0, 0, (byte)(dst.Index >> 3)); _code.Add((byte)(0xB8 + (dst.Index & 7))); Emit64(imm);
        }
    }

    // ---------------- 算术 / 逻辑 ----------------
    private void EmitRR(byte opcodeMr, Reg dst, Reg src, bool op64) // r/m, r 形式，mod=11
    {
        Rex(op64 ? (byte)1 : (byte)0, (byte)(src.Index >> 3), 0, (byte)(dst.Index >> 3));
        _code.Add(opcodeMr);
        ModRM(0b11, src.Index, dst.Index);
    }
    private void EmitRm(byte opcode, Reg dst, Mem m, bool op64) // r, r/m 形式
    {
        RexFor(op64 ? (byte)1 : (byte)0, dst.Index, m); _code.Add(opcode); EncodeModRmFor(dst.Index, m);
    }
    private void EmitMr(byte opcode, Reg src, Mem m, bool op64) // r/m, r 形式
    {
        RexFor(op64 ? (byte)1 : (byte)0, src.Index, m); _code.Add(opcode); EncodeModRmFor(src.Index, m);
    }

    public void Add(Reg dst, Reg src) => EmitRR(0x01, dst, src, dst.Size == 64);
    public void Sub(Reg dst, Reg src) => EmitRR(0x29, dst, src, dst.Size == 64);
    public void And(Reg dst, Reg src) => EmitRR(0x21, dst, src, dst.Size == 64);
    public void Or(Reg dst, Reg src) => EmitRR(0x09, dst, src, dst.Size == 64);
    public void Xor(Reg dst, Reg src) => EmitRR(0x31, dst, src, dst.Size == 64);
    public void Cmp(Reg dst, Reg src) => EmitRR(0x39, dst, src, dst.Size == 64);
    public void Test(Reg a, Reg b) => EmitRR(0x85, a, b, a.Size == 64);
    public void Add(Reg dst, Mem m) => EmitRm(0x03, dst, m, dst.Size == 64);
    public void Sub(Reg dst, Mem m) => EmitRm(0x2B, dst, m, dst.Size == 64);
    public void Cmp(Reg dst, Mem m) => EmitRm(0x3B, dst, m, dst.Size == 64);
    public void Mov(Reg dst, Mem m) => MovFromMem(dst, m);
    public void Mov(Mem m, Reg src) => MovToMem(m, src);

    public void AddImm(Reg dst, long imm) => EmitAluImm(0, dst, imm);
    public void SubImm(Reg dst, long imm) => EmitAluImm(5, dst, imm);
    public void CmpImm(Reg dst, long imm) => EmitAluImm(7, dst, imm);
    public void AndImm(Reg dst, long imm) => EmitAluImm(4, dst, imm, force32: true);
    private void EmitAluImm(byte sub, Reg dst, long imm, bool force32 = false)
    {
        bool op64 = dst.Size == 64;
        Rex(op64 ? (byte)1 : (byte)0, 0, 0, (byte)(dst.Index >> 3));
        if (!force32 && InImm8(imm)) { _code.Add(0x83); ModRM(0b11, sub, dst.Index); Emit8((byte)imm); }
        else { _code.Add(0x81); ModRM(0b11, sub, dst.Index); Emit32((int)imm); }
    }
    private static bool InImm8(long v) => v >= -128 && v <= 127;

    public void Imul(Reg dst, Reg src) // REX.W 0F AF /r
    {
        Rex(1, (byte)(dst.Index >> 3), 0, (byte)(src.Index >> 3));
        _code.Add(0x0F); _code.Add(0xAF); ModRM(0b11, dst.Index, src.Index);
    }
    public void Idiv(Reg src) { Rex(1, 0, 0, (byte)(src.Index >> 3)); _code.Add(0xF7); ModRM(0b11, 7, src.Index); }
    public void Div(Reg src) { Rex(1, 0, 0, (byte)(src.Index >> 3)); _code.Add(0xF7); ModRM(0b11, 6, src.Index); }
    public void Cqo() { _code.Add(0x48); _code.Add(0x99); }
    public void Cdq() { _code.Add(0x99); }

    public void Neg(Reg r) { Rex(r.Size == 64 ? (byte)1 : (byte)0, 0, 0, (byte)(r.Index >> 3)); _code.Add(0xF7); ModRM(0b11, 3, r.Index); }
    public void Not(Reg r) { Rex(r.Size == 64 ? (byte)1 : (byte)0, 0, 0, (byte)(r.Index >> 3)); _code.Add(0xF7); ModRM(0b11, 2, r.Index); }

    public void ShlCl(Reg r) => Shift(4, r, -1);
    public void ShrCl(Reg r) => Shift(5, r, -1);
    public void SarCl(Reg r) => Shift(7, r, -1);
    public void ShlImm(Reg r, byte c) => Shift(4, r, c);
    public void ShrImm(Reg r, byte c) => Shift(5, r, c);
    public void SarImm(Reg r, byte c) => Shift(7, r, c);
    private void Shift(byte sub, Reg r, int count)
    {
        Rex(r.Size == 64 ? (byte)1 : (byte)0, 0, 0, (byte)(r.Index >> 3));
        _code.Add(count < 0 ? (byte)0xD3 : (byte)0xC1);
        ModRM(0b11, sub, r.Index);
        if (count >= 0) Emit8((byte)count);
    }

    // ---------------- MOVZX / MOVSX ----------------
    public void Movzx8(Reg dst, Reg src) { Rex(1, (byte)(dst.Index >> 3), 0, (byte)(src.Index >> 3)); _code.Add(0x0F); _code.Add(0xB6); ModRM(0b11, dst.Index, src.Index); }
    public void Movsx8(Reg dst, Reg src) { Rex(1, (byte)(dst.Index >> 3), 0, (byte)(src.Index >> 3)); _code.Add(0x0F); _code.Add(0xBE); ModRM(0b11, dst.Index, src.Index); }
    public void Movsx32(Reg dst, Reg src) { Rex(1, (byte)(dst.Index >> 3), 0, (byte)(src.Index >> 3)); _code.Add(0x63); ModRM(0b11, dst.Index, src.Index); }
    public void Movzx32(Reg dst, Reg src) { Rex(0, (byte)(dst.Index >> 3), 0, (byte)(src.Index >> 3)); _code.Add(0x8B); ModRM(0b11, dst.Index, src.Index); }

    private void LoadExt(byte op2, Reg dst, Mem m) { RexFor(1, dst.Index, m); _code.Add(0x0F); _code.Add(op2); EncodeModRmFor(dst.Index, m); }
    public void Movzx8M(Reg dst, Mem m) => LoadExt(0xB6, dst, m);
    public void Movsx8M(Reg dst, Mem m) => LoadExt(0xBE, dst, m);
    public void Movzx16M(Reg dst, Mem m) => LoadExt(0xB7, dst, m);
    public void Movsx16M(Reg dst, Mem m) => LoadExt(0xBF, dst, m);
    public void Movsx32M(Reg dst, Mem m) { RexFor(1, dst.Index, m); _code.Add(0x63); EncodeModRmFor(dst.Index, m); }
    public void Movzx32M(Reg dst, Mem m) { RexFor(0, dst.Index, m); _code.Add(0x8B); EncodeModRmFor(dst.Index, m); }
    public void Mov64M(Reg dst, Mem m) { RexFor(1, dst.Index, m); _code.Add(0x8B); EncodeModRmFor(dst.Index, m); }

    // 存储（按宽度）
    public void Store8(Mem m, Reg src)
    {
        // 8 位寄存器：访问 spl/bpl/sil/dil 需 REX（W=0）；扩展寄存器也需 REX
        bool needRex = src.Index >= 8 || (m.Base.HasValue && m.Base.Value.Index >= 8) || (m.Index.HasValue && m.Index.Value.Index >= 8);
        if (needRex) Rex(0, (byte)(src.Index >> 3), m.Index.HasValue ? (byte)(m.Index.Value.Index >> 3) : (byte)0, m.Base.HasValue ? (byte)(m.Base.Value.Index >> 3) : (byte)0);
        _code.Add(0x88);
        EncodeModRmFor(src.Index, m);
    }
    public void Store16(Mem m, Reg src) { _code.Add(0x66); EmitMr(0x89, src, m, false); }
    public void Store32(Mem m, Reg src) => EmitMr(0x89, src, m, false);
    public void Store64(Mem m, Reg src) => EmitMr(0x89, src, m, true);

    // ---------------- PUSH / POP ----------------
    public void Push(Reg r) { if (r.Index >= 8) Rex(0, 0, 0, 1); _code.Add((byte)(0x50 + (r.Index & 7))); }
    public void Pop(Reg r) { if (r.Index >= 8) Rex(0, 0, 0, 1); _code.Add((byte)(0x58 + (r.Index & 7))); }
    public void PushImm32(int v) { _code.Add(0x68); Emit32(v); }

    // ---------------- 控制流 ----------------
    public void Ret() => _code.Add(0xC3);
    public void Leave() => _code.Add(0xC9);
    public void Nop() => _code.Add(0x90);

    /// <summary>近调用（rel32），返回 rel32 字段偏移，供调用方记录 Fixup。</summary>
    public int CallRel() { _code.Add(0xE8); int off = _code.Count; Emit32(0); return off; }
    public void CallReg(Reg r) { if (r.Index >= 8) Rex(0, 0, 0, 1); _code.Add(0xFF); ModRM(0b11, 2, r.Index); }

    public void Jmp(int label) { _code.Add(0xE9); _patches.Add(new Patch(_code.Count, label)); Emit32(0); }
    public void Jcc(Cond cc, int label) { _code.Add(0x0F); _code.Add((byte)(0x80 | (int)cc)); _patches.Add(new Patch(_code.Count, label)); Emit32(0); }

    public void Setcc(Cond cc, Reg r)
    {
        if (r.Index >= 8 || r.Index is 4 or 5 or 6 or 7) Rex(0, 0, 0, (byte)(r.Index >> 3));
        _code.Add(0x0F); _code.Add((byte)(0x90 | (int)cc)); ModRM(0b11, 0, r.Index);
    }
}

/// <summary>条件码（与 Jcc/Setcc 共用）。</summary>
public enum Cond
{
    O = 0, NO = 1, B = 2, AE = 3, E = 4, NE = 5, BE = 6, A = 7,
    S = 8, NS = 9, P = 10, NP = 11, L = 12, GE = 13, LE = 14, G = 15,
}
