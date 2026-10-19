using System.Text;

namespace Kuroko;

// DVD-Video VM 命令（8バイト）を人間可読のニーモニックへ逆アセンブルする。
// 典拠は DVD-Video の VM 命令の形式。再生時の実行は DvdVm.DvdVmInterpreter が行い、
// こちらは Kuroko の JSON へ添える「読める注釈」を作るための表示専用（GPLコードは参照しない）。
//
// 命令群＝byte0 bit7-5：0=特殊/goto, 1=Link/Jump, 2=SetSystem, 3=SetGPRM, 4-6=Set/Compare/Link結合, 7=予約。
public static class DvdVmDisassembler
{
    // 8バイト命令 → ニーモニック1行。未知命令は "db <hex>" で返す（壊さない）。
    public static string ToMnemonic(byte[] c)
    {
        if (c == null || c.Length < 8) return "db " + Hex(c);
        int group = (c[0] >> 5) & 0x7;
        try
        {
            return group switch
            {
                0 => Group0(c),
                1 => Group1(c),
                2 => Group2(c),
                3 => Group3(c),
                4 or 5 or 6 => Group456(c, group),
                _ => "reserved " + Hex(c),
            };
        }
        catch { return "db " + Hex(c); }
    }

    public static string Hex(byte[]? c)
    {
        if (c == null) return "";
        var sb = new StringBuilder(c.Length * 2);
        foreach (var b in c) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static string Reg(int id) =>
        id <= 0x0F ? $"GPRM{id}" : (id >= 0x80 && id <= 0x97 ? $"SPRM{id - 0x80}" : $"R0x{id:X2}");

    private static readonly string[] CmpOps = { "true", "&", "==", "!=", ">=", ">", "<=", "<" };

    // 条件（比較）部の文字列。imm=byte1.bit7 で右辺が即値か。left=byte3, immRight=bytes4-5, regRight=byte5。
    private static string Cond(byte[] c)
    {
        int cmp = (c[1] >> 4) & 0x7;
        if (cmp == 0) return "";
        bool imm = (c[1] & 0x80) != 0;
        string left = Reg(c[3]);
        string right = imm ? $"0x{(c[4] << 8) | c[5]:X4}" : Reg(c[5]);
        return $"if ({left} {CmpOps[cmp]} {right}) ";
    }

    // 群0：特殊命令＝byte1 bit3-0（0=NOP,1=Goto,2=Break,3=SetTmpPML）。上位bitは条件。
    private static string Group0(byte[] c)
    {
        int sub = c[1] & 0x0F;
        string cond = Cond(c);
        return sub switch
        {
            0 => cond.Length > 0 ? cond + "Nop" : "Nop",
            1 => $"{cond}Goto line {c[7]}",
            2 => $"{cond}Break",
            3 => $"{cond}SetTmpPML {c[6] & 0x0F} -> line {c[7]}",
            _ => "db " + Hex(c),
        };
    }

    // 群1：byte0 bit4=0 なら Link、1 なら Jump/Call/Exit。種別は byte1 bit3-0。
    private static string Group1(byte[] c)
    {
        int sub = c[1] & 0x0F;
        bool jump = (c[0] & 0x10) != 0;
        if (!jump)
        {
            // Link（種別 byte1 bit3-0：1=LinkSub / 4=LinkPGCN / 5=LinkPTTN / 6=LinkPGN / 7=LinkCN）。
            int lbtn = (c[6] >> 2) & 0x3F;
            string bs = lbtn != 0 ? $" (btn {lbtn})" : "";
            if (sub == 4) return $"{Cond(c)}LinkPGCN {(c[6] << 8) | c[7]}";
            if (sub == 1) return $"{Cond(c)}Link {LinkSubset(c[7] & 0x1F)}{bs}";
            if (sub == 5) return $"{Cond(c)}LinkPTTN {c[7]}{bs}";
            if (sub == 6) return $"{Cond(c)}LinkPGN {c[7]}{bs}";
            if (sub == 7) return $"{Cond(c)}LinkCN {c[7]}{bs}";
            return "Link? " + Hex(c);
        }
        // Jump / Call / Exit（条件形式2：byte1 bit6-4=cmp, bytes6-7=2レジスタ）
        string jcond = JumpCond(c);
        return sub switch
        {
            1 => $"Exit",
            2 => $"{jcond}JumpTT {c[5]}",
            3 => $"{jcond}JumpVTS_TT {c[5]}",
            5 => $"{jcond}JumpVTS_PTT ttn={c[5]} ptt={(c[2] << 8) | c[3]}",
            6 => $"{jcond}{JumpSS(c)}",
            8 => $"{jcond}{CallSS(c)}",
            _ => "Jump? " + Hex(c),
        };
    }

    // Jump/Call の条件形式2（レジスタ同士の比較。bytes6-7）。
    private static string JumpCond(byte[] c)
    {
        int cmp = (c[1] >> 4) & 0x7;
        if (cmp == 0) return "";
        return $"if ({Reg(c[6])} {CmpOps[cmp]} {Reg(c[7])}) ";
    }

    private static string LinkSubset(int kind) => kind switch
    {
        0 => "NoLink",
        1 => "TopCell", 2 => "NextCell", 3 => "PrevCell",
        5 => "TopPG", 6 => "NextPG", 7 => "PrevPG",
        9 => "TopPGC", 10 => "NextPGC", 11 => "PrevPGC", 12 => "GoUpPGC", 13 => "TailPGC",
        16 => "RSM",
        _ => $"kind{kind}",
    };

    // JumpSS：byte5 bit7-6 が遷移先形式。
    private static string JumpSS(byte[] c)
    {
        int form = (c[5] >> 6) & 0x3;
        int menu = c[5] & 0x1F;
        return form switch
        {
            0 => "JumpSS FP",
            1 => $"JumpSS VMGM menu={MenuName(menu)}",
            2 => $"JumpSS VTSM vts={c[4]} ttn={c[3]} menu={MenuName(menu)}",
            3 => $"JumpSS VMGM PGC {(c[2] << 8) | c[3]}",
            _ => "JumpSS?",
        };
    }

    // CallSS：byte4=rsm_cell, byte5 bit7-6 が形式。
    private static string CallSS(byte[] c)
    {
        int form = (c[5] >> 6) & 0x3;
        int menu = c[5] & 0x1F;
        string rsm = c[4] != 0 ? $" rsm_cell={c[4]}" : "";
        return form switch
        {
            0 => $"CallSS FP{rsm}",
            1 => $"CallSS VMGM menu={MenuName(menu)}{rsm}",
            2 => $"CallSS VTSM menu={MenuName(menu)}{rsm}",
            3 => $"CallSS VMGM PGC {(c[2] << 8) | c[3]}{rsm}",
            _ => "CallSS?",
        };
    }

    private static string MenuName(int m) => m switch
    {
        2 => "Title", 3 => "Root", 4 => "Subpicture", 5 => "Audio", 6 => "Angle", 7 => "Chapter",
        _ => m.ToString(),
    };

    // 群2：SetSystem。byte0 bit4=即値フラグ。0x40/0x50=NOP, 0x41/0x51=SetSTN, 0x46/0x56=SetHL_BTNN,
    //  subop は byte0 bit3-0：1=SetSTN, 2=SetNVTMR, 3=SetGPRMMD, 4=SetAMXMD, 6=SetHL_BTNN。
    private static string Group2(byte[] c)
    {
        int subop = c[0] & 0x0F;
        bool imm = (c[0] & 0x10) != 0;
        string cond = Group2Link(c, out string link);
        string body = subop switch
        {
            0 => "Nop",
            1 => SetStn(c, imm),
            2 => SetNvtmr(c, imm),
            3 => SetGprmMd(c, imm),
            4 => SetAmxmd(c, imm),
            6 => imm ? $"SetHL_BTNN btn={(((c[4] << 8) | c[5]) >> 10) & 0x3F}" : $"SetHL_BTNN = {Reg(c[5])}",
            _ => "SetSystem? " + Hex(c),
        };
        return cond + body + link;
    }

    private static string SetStn(byte[] c, bool imm)
    {
        var parts = new List<string>();
        if ((c[3] & 0x80) != 0) parts.Add(imm ? $"audio={c[3] & 0x7F}" : $"audio={Reg(c[3] & 0x7F)}");
        if ((c[4] & 0x80) != 0)
        {
            if (imm) parts.Add($"subp={c[4] & 0x1F}{(((c[4] & 0x40) != 0) ? " on" : "")}");
            else parts.Add($"subp={Reg(c[4] & 0x7F)}");
        }
        if ((c[5] & 0x80) != 0) parts.Add(imm ? $"angle={c[5] & 0x7F}" : $"angle={Reg(c[5] & 0x7F)}");
        return "SetSTN " + (parts.Count > 0 ? string.Join(",", parts) : "-");
    }

    private static string SetNvtmr(byte[] c, bool imm)
    {
        string val = imm ? $"0x{(c[2] << 8) | c[3]:X4}" : Reg(c[3]);
        int pgcn = ((c[4] & 0x7F) << 8) | c[5];
        return $"SetNVTMR timer={val} -> PGCN {pgcn}";
    }

    private static string SetGprmMd(byte[] c, bool imm)
    {
        string val = imm ? $"0x{(c[2] << 8) | c[3]:X4}" : Reg(c[3]);
        int dst = c[5] & 0x0F;
        bool counter = (c[5] & 0x80) != 0;
        return $"SetGPRMMD GPRM{dst}{(counter ? "(counter)" : "")} = {val}";
    }

    private static string SetAmxmd(byte[] c, bool imm)
    {
        string val = imm ? $"0x{(c[4] << 8) | c[5]:X4}" : Reg(c[5]);
        return $"SetAMXMD = {val}";
    }

    // 群2の bytes6-7：条件形式2（cmp!=0）または Set 後の結合 Link（byte1 bit3-0=種別）。
    private static string Group2Link(byte[] c, out string link)
    {
        link = "";
        int cmp = (c[1] >> 4) & 0x7;
        if (cmp != 0) return $"if ({Reg(c[6])} {CmpOps[cmp]} {Reg(c[7])}) ";
        int sub = c[1] & 0x0F;
        if (sub == 4) link = $", LinkPGCN {(c[6] << 8) | c[7]}";
        else if (sub == 1) link = $", Link {LinkSubset(c[7] & 0x1F)}";
        return "";
    }

    // 群3：SetGPRM。演算=byte0 bit3-0、即値=byte0 bit4、宛先=byte3 bit3-0、ソース=bytes4-5(即値)/byte5(reg)。
    private static readonly string[] Ops = { "nop", "mov", "swp", "add", "sub", "mul", "div", "mod", "rnd", "and", "or", "xor", "?", "?", "?", "?" };
    private static string Group3(byte[] c)
    {
        int op = c[0] & 0x0F;
        bool imm = (c[0] & 0x10) != 0;
        int dst = c[3] & 0x0F;
        string src = imm ? $"0x{(c[4] << 8) | c[5]:X4}" : Reg(c[5]);
        // 条件形式3：byte1 bit6-4=cmp（左辺byte2、右辺 byte7(reg)/bytes6-7(imm)）。Set後Link：byte1 bit3-0。
        int cmp = (c[1] >> 4) & 0x7;
        string cond = "";
        if (cmp != 0)
        {
            bool cimm = (c[1] & 0x80) != 0;
            string right = cimm ? $"0x{(c[6] << 8) | c[7]:X4}" : Reg(c[7]);
            cond = $"if ({Reg(c[2])} {CmpOps[cmp]} {right}) ";
        }
        string link = "";
        if (cmp == 0)
        {
            int sub = c[1] & 0x0F;
            if (sub == 4) link = $", LinkPGCN {(c[6] << 8) | c[7]}";
            else if (sub == 1) link = $", Link {LinkSubset(c[7] & 0x1F)}";
        }
        string setOp = op == 1 ? $"GPRM{dst} = {src}" : $"GPRM{dst} {Ops[op]}= {src}";
        return cond + setOp + link;
    }

    // 群4-6：Set・Compare・Link 結合（§9.1）。詳細割り当ては複雑なので要点のみ表示。
    private static string Group456(byte[] c, int group)
    {
        int setOp = c[0] & 0x0F;
        bool simm = (c[0] & 0x10) != 0;
        int cmp = (c[1] >> 4) & 0x7;
        bool cimm = (c[1] & 0x80) != 0;
        int dst = c[1] & 0x0F;
        string link = $"Link {LinkSubset(c[7] & 0x1F)}";
        int hl = (c[6] >> 2) & 0x3F;
        if (hl != 0) link += $" (btn {hl})";
        string name = group switch { 4 => "SetCLnk", 5 => "CSetCLnk", _ => "CmpSetLnk" };
        string set = simm ? $"GPRM{dst} {Ops[setOp]} 0x{(c[2] << 8) | c[3]:X4}" : $"GPRM{dst} {Ops[setOp]} {Reg(c[3])}";
        string cmpStr = CmpOps[cmp == 0 ? 0 : cmp];
        return $"{name}[{set}; cmp {cmpStr}; {link}]";
    }
}
