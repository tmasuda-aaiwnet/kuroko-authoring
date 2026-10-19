namespace Kuroko.DvdVm;

// DVD-Video ナビゲーション仮想マシン（仕様忠実な命令インタプリタ）。
// 命令の符号化は DVD-Video の VM 命令の形式（公開されている資料＋市販機・市販ディスクでの照合）に従う。
// クリーンルーム：GPL コード（libdvdnav 等）は参照せず、公開仕様の「事実」に基づき実装する。
//
// 命令は 8 バイト。群 = byte0 bit7-5（0=特殊/goto, 1=Link/Jump, 2=SetSystem, 3=SetGPRM,
// 4=SetCLnk, 5=CSetCLnk, 6=CmpSetLnk＝Set/Compare/Link 結合, 7=予約）。群0〜6を仕様どおり実装する。
// 群7（予約）のみ未対応命令としてスキップ（NOP相当）。
// 注：VM命令としては全てデコード・レジスタ反映するが、次の2つは「効果の適用」が対象外：
//   ・ペアレンタル制御（SetTmpPML の一時レベルは保持するが、視聴制限のゲーティングは行わない）。
//   ・カラオケ音声ミキシング（SetAMXMD で SPRM[11] は設定するが、ミックス再生には反映しない）。

// 汎用/システムパラメータレジスタ。セッションを通じて保持する（メニュー選択が PGC 遷移を跨いで効く）。
public sealed class DvdVmRegisters
{
    public ushort[] Gprm { get; } = new ushort[16];
    public ushort[] Sprm { get; } = new ushort[24];

    public DvdVmRegisters()
    {
        // SPRM[13]＝パレンタルレベル（1〜8。8＝制限なし）。既定の 0 は「どのレベルも許可されていない」に
        // 相当し、ディスクの PRE がパレンタル判定で再生を止めてしまう（市販ディスクの例：コントローラー有効時に
        // 再生できなかった）。市販機と同じく**制限なし＝8**で始める。
        Sprm[13] = 8;
    }

    // SetTmpPML で設定される一時 parental level（次操作向け）。ゲーティング自体は VM 外。
    public int TempParentalLevel { get; set; }

    // レジスタID：0x00-0x0F=GPRM[0..15]、0x80-0x97=SPRM[0..23]。
    public ushort Read(int id)
        => id >= 0x80 ? Sprm[(id - 0x80) & 0x1F] : Gprm[id & 0x0F];

    public void SetSprmButton(int button1Based)
        => Sprm[8] = (ushort)((button1Based & 0x3F) << 10);

    public int SelectedButton => (Sprm[8] >> 10) & 0x3F;

    // 再生エンジン（演者）から通知される「タイトル再生中の音声/字幕トラック選択」を SPRM へ反映する黒子側API。
    // 市販機では音声/字幕ボタン(UOP)が SPRM[1]/[2] を更新し、状態別メニュー（PRE で GPRM=SPRM 同期）が現在の
    // 音声/字幕を表示する。同じ挙動にするため、エンジンの音声/字幕選択時にこれを呼んで SPRM を更新する。
    // logicalAudioNumber = IFO 音声ストリーム番号（SPRM[1]）。範囲外（none 等）は据え置き。
    public void NotifyAudioStreamSelected(int logicalAudioNumber)
    {
        if (logicalAudioNumber >= 0 && logicalAudioNumber <= 7)
            Sprm[1] = (ushort)logicalAudioNumber;
    }

    // logicalSubtitleNumber = IFO 字幕ストリーム番号、display=表示ON。SPRM[2]: bit6(0x40)=表示・下位6bit=番号。
    // オフは表示bitのみクリア（番号は保持）。
    public void NotifySubtitleStreamSelected(int logicalSubtitleNumber, bool display)
    {
        Sprm[2] = (ushort)(display && logicalSubtitleNumber >= 0
            ? (0x40 | (logicalSubtitleNumber & 0x3F))
            : (Sprm[2] & 0x3F));
    }

    // GPRM のカウンタモード（true=毎秒+1で自動加算する「カウンタ」）。SetGPRMMD(byte5 bit7) で設定/解除。
    public bool[] GprmCounter { get; } = new bool[16];

    // 毎秒処理：カウンタモードの GPRM を +1（16bit ラップ）。ナビタイマ(SPRM[9]/[10])の満了遷移は
    // 再生文脈が要るため VM 外（エンジン）で行い、ここでは GPRM カウンタの加算のみを担う。
    public void AdvanceCountersOneSecond()
    {
        for (int i = 0; i < Gprm.Length; i++)
            if (GprmCounter[i])
                Gprm[i] = (ushort)(Gprm[i] + 1);
    }

    // ── 再生位置（レジューム）への保存・復元────────────────
    // ディスクモードで再開するときは First Play やメニューを通らずタイトルへ直接入るので、ディスクの PRE が見る
    // GPRM（メニューで選んだ状態など）と SPRM を、保存しておいたもので戻す。形式は版付きの 1 行の文字列：
    //   "v1;g=<GPRM 16 個の 4 桁 16 進>;s=<SPRM 24 個の 4 桁 16 進>;c=<カウンタモードのビット（16 進 4 桁）>"
    public string ExportState()
    {
        var sb = new System.Text.StringBuilder("v1;g=", 5 + 16 * 4 + 3 + 24 * 4 + 7);
        foreach (var v in Gprm) sb.Append(v.ToString("X4"));
        sb.Append(";s=");
        foreach (var v in Sprm) sb.Append(v.ToString("X4"));
        int mask = 0;
        for (int i = 0; i < GprmCounter.Length; i++) if (GprmCounter[i]) mask |= 1 << i;
        sb.Append(";c=").Append(mask.ToString("X4"));
        return sb.ToString();
    }

    // ExportState の文字列を戻す。読めなければ何もせず false（古い／壊れた履歴は無視して既定のまま再開する）。
    public bool ImportState(string? state)
    {
        if (string.IsNullOrEmpty(state) || !state.StartsWith("v1;", StringComparison.Ordinal)) return false;
        string? g = null, s = null, c = null;
        foreach (var part in state.Split(';'))
        {
            if (part.StartsWith("g=", StringComparison.Ordinal)) g = part[2..];
            else if (part.StartsWith("s=", StringComparison.Ordinal)) s = part[2..];
            else if (part.StartsWith("c=", StringComparison.Ordinal)) c = part[2..];
        }
        if (g is null || s is null || c is null || g.Length != 16 * 4 || s.Length != 24 * 4) return false;
        var gv = new ushort[16]; var sv = new ushort[24];
        for (int i = 0; i < 16; i++)
            if (!ushort.TryParse(g.AsSpan(i * 4, 4), System.Globalization.NumberStyles.HexNumber, null, out gv[i])) return false;
        for (int i = 0; i < 24; i++)
            if (!ushort.TryParse(s.AsSpan(i * 4, 4), System.Globalization.NumberStyles.HexNumber, null, out sv[i])) return false;
        if (!int.TryParse(c, System.Globalization.NumberStyles.HexNumber, null, out int mask)) return false;
        Array.Copy(gv, Gprm, 16);
        Array.Copy(sv, Sprm, 24);
        for (int i = 0; i < GprmCounter.Length; i++) GprmCounter[i] = (mask & (1 << i)) != 0;
        return true;
    }
}

public enum DvdVmTransitionKind
{
    None,            // フォールスルー（制御移行なし）
    Break,           // 命令列終了
    Exit,            // 再生停止
    // 同一ドメイン内の Link
    LinkPGCN,        // A = PGC番号
    LinkPTTN,        // A = PTT番号
    LinkPGN,         // A = プログラム番号
    LinkCN,          // A = セル番号
    LinkSub,         // A = リンク種別(13=TailPGC, 10=NextPGC, 11=PrevPGC, 12=GoUpPGC, 16=RSM, ...)
    // タイトル/メニューへの Jump
    JumpTT,          // A = ディスク全体タイトル番号
    JumpVTS_TT,      // A = VTS内タイトル番号
    JumpVTS_PTT,     // A = VTS内タイトル番号, B = PTT(章)
    JumpSS_FP,       // First Play
    JumpSS_VMGM_Menu,// A = メニュー種別（VMGM のエントリメニュー）
    JumpSS_VTSM,     // A = VTS番号, B = VTS_TTN, C = メニュー種別
    JumpSS_VMGM_PGC, // A = VMGM の PGC 番号（直接指定）
    // CallSS（メニュー等へ移り、Resume 用の再開点 rsm_cell=B を保存。RSM 復元は呼び出し元の対応に依存）
    CallSS_FP,
    CallSS_VMGM_Menu, // A = メニュー種別
    CallSS_VTSM,      // A = メニュー種別
    CallSS_VMGM_PGC,  // A = VMGM の PGC 番号
}

public readonly record struct DvdVmTransition(
    DvdVmTransitionKind Kind, int A = 0, int B = 0, int C = 0, int Button = 0);

public static class DvdVmInterpreter
{
    // 命令列（PGC の PRE/POST/CELL、または First Play）を実行し、最初の制御移行を返す。
    // 制御移行が無ければ None（呼び出し側が次段＝セル再生/メニュー表示などを決める）。
    public static DvdVmTransition Execute(IReadOnlyList<byte[]> commands, DvdVmRegisters reg)
    {
        int pc = 0, guard = 0;
        while (pc >= 0 && pc < commands.Count && guard++ < 20000)
        {
            var c = commands[pc];
            if (c.Length < 8) { pc++; continue; }
            var (transferred, t, nextPc) = ExecuteOne(c, reg, pc);
            if (transferred)
                return t;
            pc = nextPc;
        }
        return new DvdVmTransition(DvdVmTransitionKind.None);
    }

    // 1命令を実行。戻り値 transferred=true なら制御移行（t を返す）。false なら nextPc へ進む。
    private static (bool transferred, DvdVmTransition t, int nextPc) ExecuteOne(byte[] c, DvdVmRegisters reg, int pc)
    {
        int group = (c[0] >> 5) & 0x7;
        return group switch
        {
            0 => ExecGroup0(c, reg, pc),
            1 => ExecGroup1(c, reg, pc),
            2 => ExecGroup2(c, reg, pc),
            3 => ExecGroup3(c, reg, pc),
            4 or 5 or 6 => ExecGroup456(c, reg, pc), // Set・Compare・Link 結合（§9.1）
            _ => (false, default, pc + 1), // 群7（予約）は未対応命令としてスキップ。
        };
    }

    // 比較演算（byte1 bit6-4 の値）。
    private static bool EvalCompare(int cmp, int left, int right) => cmp switch
    {
        0 => true,                       // 無条件
        1 => (left & right) != 0,        // ビット積
        2 => left == right,
        3 => left != right,
        4 => left >= right,
        5 => left > right,
        6 => left <= right,
        7 => left < right,
        _ => true,
    };

    // Link サブ命令（byte1 下位4bit = 種別）を遷移に組み立てる。群1の単独 Link と、
    // 群2/3の「Set の後に Link」結合形式で共通（引数は bytes6-7、ボタンは byte6 bit7-2）。
    // 種別0は転送なし（ボタンのみ更新）→ null。DVD-Video の VM 命令の形式に従う。
    private static DvdVmTransition? TryBuildLink(int sub, byte[] c, DvdVmRegisters reg)
    {
        int button = (c[6] >> 2) & 0x3F;
        switch (sub)
        {
            case 1: // LinkSub（byte7 bit4-0 = リンク種別、byte6 bit7-2 = ボタン）
                return new DvdVmTransition(DvdVmTransitionKind.LinkSub, A: c[7] & 0x1F, Button: button);
            case 4: // LinkPGCN（bytes6-7 = PGCN、ボタン欄なし）
                return new DvdVmTransition(DvdVmTransitionKind.LinkPGCN, A: (c[6] << 8) | c[7]);
            case 5: // LinkPTTN（byte7 = PTT、byte6 bit7-2 = ボタン）
                return new DvdVmTransition(DvdVmTransitionKind.LinkPTTN, A: c[7], Button: button);
            case 6: // LinkPGN
                return new DvdVmTransition(DvdVmTransitionKind.LinkPGN, A: c[7], Button: button);
            case 7: // LinkCN
                return new DvdVmTransition(DvdVmTransitionKind.LinkCN, A: c[7], Button: button);
            default: // 0=ボタンのみ更新（転送なし）
                if (button > 0) reg.SetSprmButton(button);
                return null;
        }
    }

    // 群4-6 の結合 Link（Link subset 固定）：byte7 bit4-0=種別、byte6 bit7-2=ボタン。§9.1-c。
    // 種別0は転送なし（ボタンのみ更新）→ null。
    private static DvdVmTransition? TryBuildLinkSubset(byte[] c, DvdVmRegisters reg)
    {
        int type = c[7] & 0x1F;
        int button = (c[6] >> 2) & 0x3F;
        if (type == 0)
        {
            if (button > 0) reg.SetSprmButton(button);
            return null;
        }
        return new DvdVmTransition(DvdVmTransitionKind.LinkSub, A: type, Button: button);
    }

    // 群4-6：Set・Compare・Link 結合形式。
    // 群4=SetCLnk（Set→比較→条件Link）、群5=CSetCLnk（比較→条件でSet+Link）、群6=CmpSetLnk（比較→条件Set→無条件Link）。
    private static (bool, DvdVmTransition, int) ExecGroup456(byte[] c, DvdVmRegisters reg, int pc)
    {
        int group = (c[0] >> 5) & 0x7;       // 4 / 5 / 6
        int setOp = c[0] & 0x0F;             // Set 演算
        bool simmed = (c[0] & 0x10) != 0;    // Set ソース即値フラグ
        int cmp = (c[1] >> 4) & 0x07;        // 比較演算
        bool cimmed = (c[1] & 0x80) != 0;    // 比較右辺即値フラグ
        int gTarget = c[1] & 0x0F;           // Set 対象 GPRM

        // オペランド配置（§9.1-a/b）。setSrcReg は SWP の交換相手レジスタID（即値ソース時は -1）。
        int setSource, cmpLeft = 0, cmpRight, setSrcReg;
        if (group == 4)
        {
            // bytes2-3=Setソース、bytes4-5=比較右辺、比較左辺=Set対象GPRM（Set後の更新値）。
            setSource = simmed ? ((c[2] << 8) | c[3]) : reg.Read(c[3]);
            setSrcReg = simmed ? -1 : c[3];
            cmpRight = cimmed ? ((c[4] << 8) | c[5]) : reg.Read(c[5]);
        }
        else if (!simmed)
        {
            // 群5/6・Setソースがレジスタ：byte2=Setソース、byte3=比較左辺、byte5/即値=比較右辺。
            setSource = reg.Read(c[2]);
            setSrcReg = c[2];
            cmpLeft = reg.Read(c[3]);
            cmpRight = cimmed ? ((c[4] << 8) | c[5]) : reg.Read(c[5]);
        }
        else
        {
            // 群5/6・Setソースが即値：bytes2-3=Set即値、byte4=比較左辺、byte5=比較右辺（cimmed=1は無効）。
            setSource = (c[2] << 8) | c[3];
            setSrcReg = -1;
            cmpLeft = reg.Read(c[4]);
            cmpRight = reg.Read(c[5]);
        }

        var link = TryBuildLinkSubset(c, reg);
        switch (group)
        {
            case 4: // Set → 更新後の対象GPRMを比較 → 真ならLink
                ApplyGprmOp(reg, gTarget, setOp, setSource, setSrcReg);
                if ((cmp == 0 || EvalCompare(cmp, reg.Gprm[gTarget], cmpRight)) && link is { } l4)
                    return (true, l4, pc + 1);
                return (false, default, pc + 1);
            case 5: // 比較 → 真ならSetしてLink
                if (cmp == 0 || EvalCompare(cmp, cmpLeft, cmpRight))
                {
                    ApplyGprmOp(reg, gTarget, setOp, setSource, setSrcReg);
                    if (link is { } l5) return (true, l5, pc + 1);
                }
                return (false, default, pc + 1);
            default: // 群6：比較 → 真ならSet → 判定にかかわらずLink
                if (cmp == 0 || EvalCompare(cmp, cmpLeft, cmpRight))
                    ApplyGprmOp(reg, gTarget, setOp, setSource, setSrcReg);
                if (link is { } l6) return (true, l6, pc + 1);
                return (false, default, pc + 1);
        }
    }

    // 群0：特殊命令（NOP/Goto/Break/SetTmpPML）。条件形式1（左=byte3, 右=即値bytes4-5/レジスタbyte5）。
    private static (bool, DvdVmTransition, int) ExecGroup0(byte[] c, DvdVmRegisters reg, int pc)
    {
        int sub = c[1] & 0x0F;
        int cmp = (c[1] >> 4) & 0x07;
        bool imm = (c[1] & 0x80) != 0;
        int right = imm ? ((c[4] << 8) | c[5]) : reg.Read(c[5]);
        bool cond = cmp == 0 || EvalCompare(cmp, reg.Read(c[3]), right);

        switch (sub)
        {
            case 3: // SetTmpPML（一時 parental level = byte6 bit3-0 を保持。成功時 byte7 行へ）
                if (cond)
                    reg.TempParentalLevel = c[6] & 0x0F;
                goto case 1;
            case 1: // Goto（行=byte7, 1-based）
                if (cond && c[7] >= 1)
                    return (false, default, c[7] - 1);
                return (false, default, pc + 1);
            case 2: // Break
                if (cond)
                    return (true, new DvdVmTransition(DvdVmTransitionKind.Break), pc + 1);
                return (false, default, pc + 1);
            default: // 0=NOP / その他
                return (false, default, pc + 1);
        }
    }

    // 群1：Link（byte0 0x20-0x2F）/ Jump・Call・Exit（0x30-0x3F）。
    private static (bool, DvdVmTransition, int) ExecGroup1(byte[] c, DvdVmRegisters reg, int pc)
    {
        bool isJump = (c[0] & 0x10) != 0;
        int sub = c[1] & 0x0F;
        int cmp = (c[1] >> 4) & 0x07;
        bool imm = (c[1] & 0x80) != 0;

        bool cond = isJump
            ? cmp == 0 || EvalCompare(cmp, reg.Read(c[6]), reg.Read(c[7]))         // Jump/Call：byte6,byte7 の2レジスタ
            : cmp == 0 || EvalCompare(cmp, reg.Read(c[3]),                          // Link：byte3 vs 即値/byte5
                                      imm ? ((c[4] << 8) | c[5]) : reg.Read(c[5]));

        if (!cond)
            return (false, default, pc + 1);

        if (!isJump)
        {
            var link = TryBuildLink(sub, c, reg);
            return link is { } lk ? (true, lk, pc + 1) : (false, default, pc + 1);
        }
        else
        {
            switch (sub)
            {
                case 0: // Exit
                    return (true, new DvdVmTransition(DvdVmTransitionKind.Exit), pc + 1);
                case 2: // JumpTT（TTN = byte5）
                    return (true, new DvdVmTransition(DvdVmTransitionKind.JumpTT, A: c[5]), pc + 1);
                case 3: // JumpVTS_TT（VTS_TTN = byte5）
                    return (true, new DvdVmTransition(DvdVmTransitionKind.JumpVTS_TT, A: c[5]), pc + 1);
                case 5: // JumpVTS_PTT（VTS_TTN = byte5, PTT = bytes2-3）
                    return (true, new DvdVmTransition(DvdVmTransitionKind.JumpVTS_PTT, A: c[5], B: (c[2] << 8) | c[3]), pc + 1);
                case 6: // JumpSS
                    return (true, DecodeJumpSS(c), pc + 1);
                case 8: // CallSS
                    return (true, DecodeCallSS(c), pc + 1);
                default:
                    return (false, default, pc + 1);
            }
        }
    }

    // JumpSS：byte5 bit7-6 = 遷移先形式。
    private static DvdVmTransition DecodeJumpSS(byte[] c)
    {
        int domain = (c[5] >> 6) & 0x3;
        int menuType = c[5] & 0x1F;
        return domain switch
        {
            0 => new DvdVmTransition(DvdVmTransitionKind.JumpSS_FP),
            1 => new DvdVmTransition(DvdVmTransitionKind.JumpSS_VMGM_Menu, A: menuType == 0 ? 2 : menuType),
            2 => new DvdVmTransition(DvdVmTransitionKind.JumpSS_VTSM, A: c[4], B: c[3], C: menuType), // A=VTS番号, B=VTS_TTN, C=menu
            _ => new DvdVmTransition(DvdVmTransitionKind.JumpSS_VMGM_PGC, A: (c[2] << 8) | c[3]),       // 3=VMGM PGC直接
        };
    }

    // CallSS：byte5 bit7-6 = 遷移先形式、byte4 = rsm_cell（Resume 再開点）。
    private static DvdVmTransition DecodeCallSS(byte[] c)
    {
        int domain = (c[5] >> 6) & 0x3;
        int menuType = c[5] & 0x1F;
        int rsmCell = c[4];
        return domain switch
        {
            0 => new DvdVmTransition(DvdVmTransitionKind.CallSS_FP, B: rsmCell),
            1 => new DvdVmTransition(DvdVmTransitionKind.CallSS_VMGM_Menu, A: menuType == 0 ? 2 : menuType, B: rsmCell),
            2 => new DvdVmTransition(DvdVmTransitionKind.CallSS_VTSM, A: menuType, B: rsmCell),
            _ => new DvdVmTransition(DvdVmTransitionKind.CallSS_VMGM_PGC, A: (c[2] << 8) | c[3], B: rsmCell),
        };
    }

    // 群2：SetSystem（SetSTN / SetHL_BTNN 等）。条件形式2（byte6,byte7 の2レジスタ）。
    private static (bool, DvdVmTransition, int) ExecGroup2(byte[] c, DvdVmRegisters reg, int pc)
    {
        int subop = c[0] & 0x0F;
        int cmp = (c[1] >> 4) & 0x07;
        bool imm = (c[0] & 0x10) != 0;
        bool cond = cmp == 0 || EvalCompare(cmp, reg.Read(c[6]), reg.Read(c[7]));

        if (cond)
            ApplySetSystem(subop, c, reg, imm);

        // 比較なし（cmp==0）で byte1 下位4bit に Link 種別があれば、Set 後に Link（bytes6-7=引数）。
        if (cmp == 0)
        {
            var link = TryBuildLink(c[1] & 0x0F, c, reg);
            if (link is { } lk)
                return (true, lk, pc + 1);
        }
        return (false, default, pc + 1);
    }

    private static void ApplySetSystem(int subop, byte[] c, DvdVmRegisters reg, bool imm)
    {
        switch (subop)
        {
            case 1: // SetSTN（音声=byte3, 字幕=byte4, アングル=byte5。各 bit7=設定フラグ）
                if ((c[3] & 0x80) != 0)
                    reg.Sprm[1] = (ushort)(imm ? (c[3] & 0x7F) : reg.Gprm[c[3] & 0x0F]);
                if ((c[4] & 0x80) != 0)
                    reg.Sprm[2] = (ushort)(imm ? (c[4] & 0x7F) : reg.Gprm[c[4] & 0x0F]);
                if ((c[5] & 0x80) != 0)
                    reg.Sprm[3] = (ushort)(imm ? (c[5] & 0x7F) : reg.Gprm[c[5] & 0x0F]);
                break;
            case 2: // SetNVTMRMD（§9.3-a）：SPRM[9]=タイマ秒（bytes2-3/byte3）、SPRM[10]=満了遷移先PGCN
                reg.Sprm[9] = imm ? (ushort)((c[2] << 8) | c[3]) : reg.Read(c[3]);
                reg.Sprm[10] = (ushort)(((c[4] & 0x7F) << 8) | c[5]);
                break;
            case 3: // SetGPRMMD（§9.3-c）：GPRM[byte5 bit3-0]=値（bytes2-3/byte3）、byte5 bit7=カウンタモード
            {
                int gp = c[5] & 0x0F;
                reg.Gprm[gp] = imm ? (ushort)((c[2] << 8) | c[3]) : reg.Read(c[3]);
                reg.GprmCounter[gp] = (c[5] & 0x80) != 0; // 1=カウンタモード（毎秒+1）、0=通常レジスタ
                break;
            }
            case 4: // SetAMXMD（§9.3-b）：SPRM[11]=カラオケ音声ミキシング（bytes4-5/byte5）
                reg.Sprm[11] = imm ? (ushort)((c[4] << 8) | c[5]) : reg.Read(c[5]);
                break;
            case 6: // SetHL_BTNN（即値=bytes4-5 既に<<10 / レジスタ=byte5）
                reg.Sprm[8] = imm ? (ushort)((c[4] << 8) | c[5]) : reg.Read(c[5]);
                break;
            default:
                break;
        }
    }

    // 群3：SetGPRM（演算 = byte0 bit3-0、即値フラグ = byte0 bit4、宛先 = byte3、オペランド = bytes4-5/byte5）。
    private static (bool, DvdVmTransition, int) ExecGroup3(byte[] c, DvdVmRegisters reg, int pc)
    {
        int op = c[0] & 0x0F;
        bool imm = (c[0] & 0x10) != 0;
        int cmp = (c[1] >> 4) & 0x07;
        int dest = c[3] & 0x0F;
        int operand = imm ? ((c[4] << 8) | c[5]) : reg.Read(c[5]);
        int srcRegId = imm ? -1 : c[5]; // SWP の交換相手（群3はソース=byte5）。即値時は無効。

        if (cmp == 0)
        {
            // 無条件 Set。続けて byte1 下位4bit に Link 種別があれば Set 後に Link（比較と Link は同居不可）。
            ApplyGprmOp(reg, dest, op, operand, srcRegId);
            var link = TryBuildLink(c[1] & 0x0F, c, reg);
            if (link is { } lk)
                return (true, lk, pc + 1);
        }
        else
        {
            // 条件形式3（§9.2）：比較左辺=byte2、比較右辺=byte7(レジスタ)/bytes6-7(即値, byte1 bit7=1)。
            // 比較が真のときだけ Set を実行する（Link はなし）。
            bool cimmed = (c[1] & 0x80) != 0;
            int cmpLeft = reg.Read(c[2]);
            int cmpRight = cimmed ? ((c[6] << 8) | c[7]) : reg.Read(c[7]);
            if (EvalCompare(cmp, cmpLeft, cmpRight))
                ApplyGprmOp(reg, dest, op, operand, srcRegId);
        }
        return (false, default, pc + 1);
    }

    // dest=対象GPRM、op=演算、operand=解決済みソース値、srcRegId=ソースのレジスタID（SWP用）。
    // SWP の交換相手レジスタは群ごとに位置が異なる（群3=byte5 / 群4=byte3 / 群5・6=byte2）ため、
    // バイトから再導出せず呼び出し側が解決した srcRegId を受け取る。即値ソース時は srcRegId<0。
    private static void ApplyGprmOp(DvdVmRegisters reg, int dest, int op, int operand, int srcRegId)
    {
        ref ushort d = ref reg.Gprm[dest];
        switch (op)
        {
            case 1: d = (ushort)operand; break;                                   // mov
            case 2: // swp（GPRM同士のみ）：dest と src を交換
                if (srcRegId >= 0x00 && srcRegId <= 0x0F)
                    (reg.Gprm[dest], reg.Gprm[srcRegId]) = (reg.Gprm[srcRegId], reg.Gprm[dest]);
                break;
            case 3: d = (ushort)(d + operand); break;                             // add
            case 4: d = (ushort)(d >= operand ? d - operand : 0); break;          // sub（下限0）
            case 5: d = (ushort)(d * operand); break;                             // mul
            case 6: d = (ushort)(operand != 0 ? d / operand : 0xFFFF); break;     // div
            case 7: d = (ushort)(operand != 0 ? d % operand : d); break;          // mod
            case 8: d = (ushort)(operand > 0 ? System.Random.Shared.Next(1, operand + 1) : 0); break; // rnd
            case 9: d = (ushort)(d & operand); break;                             // and
            case 10: d = (ushort)(d | operand); break;                            // or
            case 11: d = (ushort)(d ^ operand); break;                            // xor
            default: break;
        }
    }
}
