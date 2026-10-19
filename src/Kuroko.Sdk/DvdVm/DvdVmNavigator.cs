namespace Kuroko.DvdVm;

// DVD-Video ナビゲーション状態機械（エンジン非依存）。DvdVmInterpreter が返す遷移(DvdVmTransition)を、
// 具体的な再生アクション(IDvdVmBackend)へ写像する「唯一の正典スイッチ」。DVD を直接読む再生エンジンと
// krk の再生エンジンが同じ意味論を共有するためのもの（エンジンごとに別実装にすると取りこぼしの温床になる）。
//
// 責務境界：
//  ・本クラス＝「遷移種別 → どのアクションを呼ぶか」のみ（媒体I/O・復号・出力・入力は一切触らない）。
//  ・IDvdVmBackend＝タイトル/メニューの解決・PRE/表示・再生・Resume 等、format 固有の実行（DVD=VOB / Kuroko=mkv）。
//  ・VmContext＝現在いる PGC の文脈（メニュー/タイトル、ドメイン、セル数、POST命令、Next/Prev/GoUp、program→cell）。
// 典拠：DVD-Video の VM 命令の形式と、市販機・市販ディスクでの照合。

// 現在いる PGC の文脈。TailPGC(POST)・自PGC内リンク・GoUp/Next/Prev・None/Break の落とし所判定に使う。
public sealed class VmContext
{
    public bool IsMenu { get; init; }                 // メニューPGC か タイトルPGC か
    public bool VmgmDomain { get; init; }             // メニュー時：VMGM(ディスクメニュー) か VTSM か
    public int CellCount { get; init; }               // >0：表示可能メニュー／再生可能タイトル
    public IReadOnlyList<byte[]> PostCommands { get; init; } = System.Array.Empty<byte[]>(); // TailPGC で実行する POST
    public int NextPgcn { get; init; }                // LinkSub 10（Kuroko モデルは未保持＝0）
    public int PrevPgcn { get; init; }                // LinkSub 11
    public int GoUpPgcn { get; init; }                // LinkSub 12
    public int[] ProgramEntryCells { get; init; } = System.Array.Empty<int>(); // program(1-based)→開始セル(1-based)
    public int CurrentCell { get; init; }             // 現在表示/再生中のセル index（Top/Next/Prev PG・Cell の基準）

    public int ProgramToCellIndex(int program1Based)
        => (program1Based >= 1 && program1Based <= ProgramEntryCells.Length)
            ? ProgramEntryCells[program1Based - 1] - 1
            : System.Math.Max(0, program1Based - 1);

    // セル index が属する 1-based プログラム番号。
    public int CellToProgram(int cellIndex)
    {
        int prog = 1;
        for (int p = 0; p < ProgramEntryCells.Length; p++)
            if (ProgramEntryCells[p] - 1 <= cellIndex) prog = p + 1; else break;
        return prog;
    }

    // 現在セルの属するプログラムから delta（-1/0/+1）だけ移動した先のプログラム開始セル index。
    // プログラム表が無ければセル単位で移動（各ページ＝1セルのメニュー相当）。範囲はクランプ。
    public int ProgramNavCell(int cellIndex, int delta)
    {
        int last = System.Math.Max(0, CellCount - 1);
        int nprog = ProgramEntryCells.Length;
        if (nprog <= 0) return System.Math.Clamp(cellIndex + delta, 0, last);
        int prog = System.Math.Clamp(CellToProgram(cellIndex) + delta, 1, nprog);
        return System.Math.Clamp(ProgramToCellIndex(prog), 0, last);
    }
}

// ナビゲータが駆動する抽象アクション。各エンジンが自分の媒体・パイプラインで実装する。
// アクションは「起動したら return」でよい（非同期にスレッドを立て直す等はエンジン側の自由）。
public interface IDvdVmBackend
{
    DvdVmRegisters Registers { get; }   // GPRM/SPRM（TailPGC の POST 実行やボタン反映に使う）
    int CurrentVts { get; }             // JumpVTS_*/VTSM 解決の現VTS文脈

    void PlayDiscTitle(int titleNumber, int ptt);   // JumpTT：ディスク通しタイトル番号
    void PlayVtsTitle(int vtsTitle, int ptt);       // JumpVTS_TT/PTT：現VTS内タイトル番号
    void ShowVmgmMenu(int menuType);                // JumpSS/CallSS VMGM メニュー（種別）
    void ShowVtsmMenu(int vts, int menuType);       // JumpSS/CallSS VTSM メニュー
    void ShowVmgmPgc(int pgcNumber);                // JumpSS/CallSS VMGM PGC 直接
    void GoToMenuPgcNumber(int pgcNumber, bool vmgmDomain); // メニュードメインの LinkPGCN / Next/Prev/GoUp PGC
    // タイトルドメインの LinkPGCN / Next/Prev/GoUp PGC：現VTSのタイトルドメイン(VTS_PGCIT)の PGC#n へ。
    // 仕様上 Link 系は「現ドメイン内」のリンクで、タイトルPGCから出た LinkPGCN の先はタイトル番号を
    // 持たない中間PGC のことがある（市販ディスクの例：あるタイトルセットの PGC#2/#4＝通過専用）。メニュードメインで解決すると
    // 別のPGCへ飛んで GPRM 状態機械が脱線する（閉路＝無限再帰の真因）。
    void GoToTitlePgcNumber(int pgcNumber);
    void RunFirstPlay();                            // JumpSS/CallSS FP
    void SaveResumePoint();                         // CallSS 前の本編位置退避
    void Resume();                                  // RSM：退避点へ復帰
    void ExitToMain();                              // メニューの落とし所（表示物なし）→本編/Resume へ
    void EndTitle();                                // タイトル終端（Exit/None）→次タイトル or 再生終了
    void DisplayCurrentMenu(int cellIndex);         // 自PGC表示リンク/再表示：現メニューPGCを指定セルから表示
    void PlayCurrentTitle(int cellIndex);           // 自PGC内リンク：現タイトルを指定セルから再生（POSTのループ等）

    // ── メニュー終端ライフサイクル（OnMenuEnded から呼ぶ）。器の差だけを吸収する ──────────────
    void HoldMenuStill();                    // 最終メニューフレームを保持して入力待ち（提示は継続）
    void ScheduleMenuStillAdvance(int seconds); // 有限静止：seconds 秒保持後に次セル/POST へ自動送り
    void LoopMenu(int cellIndex);            // 背景モーションメニューを指定セルから継目なく再生し直す（ループ）
}

// メニューPGC（またはセグメント）の再生が自然終端したときの、DVD-Video 由来の判断材料。
// エンジンは「器（VOB/mkv）から取得した事実」だけを詰める。解釈（保持/ループ/POST/送り）は OnMenuEnded が行う。
public readonly struct MenuEndInfo
{
    public int StopCellStill { get; init; }                 // 停止セルの静止：0=なし / -1=無限 / >0=秒数
    public bool StillImagePage { get; init; }               // 停止セルの映像が静止画（1〜2フレーム）か
    public IReadOnlyList<byte[]> CellCommand { get; init; } // 停止セルのセル終端命令（0 or 1 個）
    public int StopCellIndex { get; init; }                 // 停止セルの index
    public int LastCellIndex { get; init; }                 // PGC 内の最終セル index
    public int SegmentStartCell { get; init; }              // 再生したセル列の先頭（背景ループのループ先）
    public bool HasAudio { get; init; }                     // このメニュー再生に音声（BGM 等）があるか。静止画でも音声が
                                                            // あれば自リンクどおりループして音声を鳴らし直す（保持しない）。

    public MenuEndInfo() { CellCommand = System.Array.Empty<byte[]>(); }
}

public static class DvdVmNavigator
{
    // 診断トレース（エンジンが自身のロガーへ配線する。未配線＝無効）。メニュー終端解釈の分岐を追う用途。
    public static System.Action<string>? Trace;

    // ルートメニュー種別（DvdMenuType.Root=3 相当）。コントローラを媒体非依存にするため定数で持つ
    // （DvdCore の DvdMenuType には依存しない）。
    private const int MenuTypeRoot = 3;

    // 遷移を駆動する。depth はナビゲータ内部の再帰(TailPGC等)上限。エンジン側の再入場(PRE)は各自で guard する。
    public static void Drive(DvdVmTransition t, VmContext ctx, int depth, IDvdVmBackend b)
    {
        if (depth > 24) { Fallthrough(ctx, b); return; }
        // 遷移先が選択ボタンを指定していれば SPRM[8] へ反映（遷移先メニューの初期選択）。
        if (t.Button > 0) b.Registers.SetSprmButton(t.Button);
        switch (t.Kind)
        {
            case DvdVmTransitionKind.JumpTT: b.PlayDiscTitle(t.A, 1); return;
            case DvdVmTransitionKind.JumpVTS_TT: b.PlayVtsTitle(t.A, 1); return;
            case DvdVmTransitionKind.JumpVTS_PTT: b.PlayVtsTitle(t.A, t.B); return;
            case DvdVmTransitionKind.JumpSS_VMGM_PGC: b.ShowVmgmPgc(t.A); return;
            case DvdVmTransitionKind.JumpSS_FP: b.RunFirstPlay(); return;
            case DvdVmTransitionKind.JumpSS_VTSM: b.ShowVtsmMenu(t.A > 0 ? t.A : b.CurrentVts, t.C); return;
            case DvdVmTransitionKind.JumpSS_VMGM_Menu: b.ShowVmgmMenu(t.A); return;
            case DvdVmTransitionKind.CallSS_VMGM_PGC: b.SaveResumePoint(); b.ShowVmgmPgc(t.A); return;
            case DvdVmTransitionKind.CallSS_FP: b.SaveResumePoint(); b.RunFirstPlay(); return;
            case DvdVmTransitionKind.CallSS_VTSM:
                b.SaveResumePoint(); b.ShowVtsmMenu(b.CurrentVts, t.A >= 2 && t.A <= 7 ? t.A : MenuTypeRoot); return;
            case DvdVmTransitionKind.CallSS_VMGM_Menu:
                b.SaveResumePoint(); b.ShowVmgmMenu(t.A); return; // VMGM メニュー（当該種別）
            // LinkPGCN は「現ドメイン内」の PGC リンク：メニュー中はメニュードメイン、タイトル中は
            // 現VTSのタイトルドメイン(VTS_PGCIT)で解決する（ドメインを跨がない）。
            case DvdVmTransitionKind.LinkPGCN:
                if (ctx.IsMenu) b.GoToMenuPgcNumber(t.A, ctx.VmgmDomain); else b.GoToTitlePgcNumber(t.A);
                return;
            case DvdVmTransitionKind.LinkSub: DriveLinkSub(t.A, ctx, depth, b); return;
            case DvdVmTransitionKind.LinkPGN: SelfLink(ctx, ctx.ProgramToCellIndex(t.A), b); return;
            case DvdVmTransitionKind.LinkCN: SelfLink(ctx, t.A - 1, b); return;
            case DvdVmTransitionKind.LinkPTTN: SelfLink(ctx, 0, b); return;
            case DvdVmTransitionKind.None:
            case DvdVmTransitionKind.Break: Fallthrough(ctx, b); return;
            case DvdVmTransitionKind.Exit: if (ctx.IsMenu) b.ExitToMain(); else b.EndTitle(); return;
            default: Fallthrough(ctx, b); return;
        }
    }

    // 現ドメイン内の PGC 番号リンク（LinkPGCN / Next/Prev/GoUp PGC 共通）。
    private static void GoToPgcInDomain(VmContext ctx, int pgcNumber, IDvdVmBackend b)
    {
        if (ctx.IsMenu) b.GoToMenuPgcNumber(pgcNumber, ctx.VmgmDomain);
        else b.GoToTitlePgcNumber(pgcNumber);
    }

    // 自PGC内リンク（LinkCN/LinkPGN/LinkPTTN）：メニューはそのPGCを表示、タイトルはそのセルから再生。
    private static void SelfLink(VmContext ctx, int cellIndex, IDvdVmBackend b)
    {
        if (ctx.IsMenu) b.DisplayCurrentMenu(System.Math.Max(0, cellIndex));
        else b.PlayCurrentTitle(System.Math.Max(0, cellIndex));
    }

    // None/Break/未対応の落とし所：メニュー(セル有り)→表示、メニュー(セル無し)→本編へ、タイトル→終端。
    private static void Fallthrough(VmContext ctx, IDvdVmBackend b)
    {
        if (ctx.IsMenu) { if (ctx.CellCount > 0) b.DisplayCurrentMenu(0); else b.ExitToMain(); }
        else b.EndTitle();
    }

    // LinkSub（byte7 下位5bit の種別）。
    private static void DriveLinkSub(int type, VmContext ctx, int depth, IDvdVmBackend b)
    {
        switch (type)
        {
            case 13: Drive(DvdVmInterpreter.Execute(ctx.PostCommands, b.Registers), ctx, depth + 1, b); return; // TailPGC＝POST
            case 16: b.Resume(); return;                                        // RSM
            // Next/Prev/GoUp PGC も LinkPGCN 同様「現ドメイン内」の PGC リンク。
            case 10: GoToPgcInDomain(ctx, ctx.NextPgcn, b); return;  // NextPGC
            case 11: GoToPgcInDomain(ctx, ctx.PrevPgcn, b); return;  // PrevPGC
            case 12: GoToPgcInDomain(ctx, ctx.GoUpPgcn, b); return;  // GoUpPGC
            // Top/Next/Prev Cell（1/2/3）・Top/Next/Prev PG（5/6/7）：多ページメニュー等のページ移動。
            // 現在セル(ctx.CurrentCell)基準で隣接セル/プログラムへ SelfLink（メニュー＝表示、タイトル＝再生）。
            case 1: SelfLink(ctx, ctx.ProgramNavCell(ctx.CurrentCell, 0), b); return;  // TopCell（現PG先頭）
            case 2: SelfLink(ctx, ctx.CurrentCell + 1, b); return;                     // NextCell
            case 3: SelfLink(ctx, ctx.CurrentCell - 1, b); return;                     // PrevCell
            case 5: SelfLink(ctx, ctx.ProgramNavCell(ctx.CurrentCell, 0), b); return;  // TopPG
            case 6: SelfLink(ctx, ctx.ProgramNavCell(ctx.CurrentCell, +1), b); return; // NextPG
            case 7: SelfLink(ctx, ctx.ProgramNavCell(ctx.CurrentCell, -1), b); return; // PrevPG
            case 9: Fallthrough(ctx, b); return;                                       // TopPGC＝現PGCを先頭から
            default: Fallthrough(ctx, b); return;
        }
    }

    // メニューPGC（セグメント）の再生が末尾まで到達したときの唯一の解釈点。DVD-Video の意味論（セル終端命令→
    // 静止→POST、静止画の自己ループは保持、背景モーションはループ）を一元化する。両エンジン（VOB/mkv）は器の差だけを
    // IDvdVmBackend の primitive（HoldMenuStill/ScheduleMenuStillAdvance/LoopMenu）で吸収する。
    public static void OnMenuEnded(VmContext ctx, MenuEndInfo info, IDvdVmBackend b)
    {
        Trace?.Invoke($"OnMenuEnded cellCmd={info.CellCommand.Count} still={info.StopCellStill} stillImg={info.StillImagePage} " +
                      $"post={ctx.PostCommands.Count} stopIdx={info.StopCellIndex} lastIdx={info.LastCellIndex} segStart={info.SegmentStartCell}");
        // 0. 停止セルが無限静止（still_time=0xFF）＝ユーザ入力待ち。DVD-Video ではセル静止はセル終端で適用され、
        //    セル終端命令は「静止が明けてから」実行される。無限静止は自動では明けない（StillOff/ボタン操作待ち）ので、
        //    ここでセル終端命令を実行してはいけない。実行すると例えば LinkTailPGC→POST空→None→Fallthrough→
        //    DisplayCurrentMenu でメニューが再構築され続け、選択が毎回 FOSL へ戻る（市販ディスクの例：あるメニュー PGC＝全セル
        //    still=inf・cmdNr=LinkTailPGC・POST空）。DVD を直接読む再生エンジンは入場時に still≠0 なら再生を始めず保持する
        //    ため元々ここへ来ない。krk（Kuroko）は静止セルも1枚再生して終端発火するのでここで受け止める。
        if (info.StopCellStill == -1)
        { Trace?.Invoke("OnMenuEnded -> HoldMenuStill (infinite still, cellCmd deferred)"); b.HoldMenuStill(); return; }
        // 1. 停止セルのセル終端命令（cell_command_nr）。制御移行すれば従う。
        if (info.CellCommand.Count > 0)
        {
            var t = DvdVmInterpreter.Execute(info.CellCommand, b.Registers);
            Trace?.Invoke($"OnMenuEnded cellCmd -> {t.Kind} A={t.A} selfLink={IsSelfLink(t.Kind)}");
            if (IsSelfLink(t.Kind))
            {
                // 自PGC内リンク＝ループ。ただし静止画1枚**かつ音声なし**のページは作り直さず保持する（毎ループの
                // 再構築でハイライトが明滅するのを防ぐ。視覚結果は同一＝1枚＋ハイライトの保持）。音声(BGM)がある静止画は
                // ループして音声を鳴らし直す（保持すると2周目以降 BGM が止まって無音になる）。
                if (info.StillImagePage && !info.HasAudio) { Trace?.Invoke("OnMenuEnded -> HoldMenuStill (cellCmd selfLink, stillImg, noAudio)"); b.HoldMenuStill(); return; }
                Trace?.Invoke($"OnMenuEnded -> LoopMenu({SelfLinkCell(t, ctx)}) (cellCmd selfLink)"); b.LoopMenu(SelfLinkCell(t, ctx)); return;
            }
            if (!IsNoTransition(t.Kind)) { Trace?.Invoke($"OnMenuEnded -> Drive({t.Kind}) (cellCmd)"); Drive(t, ctx, 0, b); return; }
            // 制御移行しない（None/Break/Exit）セル命令：still==0 の非末尾セルなら次セルへ続行。
            if (info.StopCellStill == 0 && info.StopCellIndex < info.LastCellIndex)
            { Trace?.Invoke($"OnMenuEnded -> LoopMenu({info.StopCellIndex + 1}) (cellCmd noTransition, next cell)"); b.LoopMenu(info.StopCellIndex + 1); return; }
            // それ以外は下の静止/POST 判定へ落ちる。
        }
        // 2. 停止セルの明示的な静止（有限）。無限静止は手順0で処理済み。
        //    ※有限静止も本来はセル終端命令より先に効く（N秒保持→命令実行）が、ScheduleMenuStillAdvance 後の
        //      命令実行が未配線のため現状は手順1の後に置く。
        if (info.StopCellStill > 0) { Trace?.Invoke("OnMenuEnded -> HoldMenuStill+ScheduleAdvance (finite still)"); b.HoldMenuStill(); b.ScheduleMenuStillAdvance(info.StopCellStill); return; } // 有限静止
        // 3. 末尾（still==0）＝PGC の POST。制御移行すれば従う。
        if (ctx.PostCommands.Count > 0)
        {
            var t = DvdVmInterpreter.Execute(ctx.PostCommands, b.Registers);
            Trace?.Invoke($"OnMenuEnded post -> {t.Kind} selfLink={IsSelfLink(t.Kind)}");
            if (IsSelfLink(t.Kind))
            {
                if (info.StillImagePage && !info.HasAudio) { b.HoldMenuStill(); return; }
                b.LoopMenu(SelfLinkCell(t, ctx)); return;
            }
            if (!IsNoTransition(t.Kind)) { Drive(t, ctx, 0, b); return; }
        }
        // 4. 遷移先なし：静止画（音声なし）＝保持（入力待ち）／モーション or 音声あり＝背景ループ（セル列先頭から）。
        if (info.StillImagePage && !info.HasAudio) { Trace?.Invoke("OnMenuEnded -> HoldMenuStill (fallback stillImg, noAudio)"); b.HoldMenuStill(); return; }
        Trace?.Invoke($"OnMenuEnded -> LoopMenu({info.SegmentStartCell}) (fallback motion loop)"); b.LoopMenu(info.SegmentStartCell);
    }

    private static bool IsSelfLink(DvdVmTransitionKind k)
        => k is DvdVmTransitionKind.LinkPGN or DvdVmTransitionKind.LinkCN or DvdVmTransitionKind.LinkPTTN;

    private static bool IsNoTransition(DvdVmTransitionKind k)
        => k is DvdVmTransitionKind.None or DvdVmTransitionKind.Break or DvdVmTransitionKind.Exit;

    // 自PGC内リンクの飛び先セル index（LinkPGN=プログラム→開始セル／LinkCN=セル番号(1-based)／LinkPTTN=先頭）。
    private static int SelfLinkCell(DvdVmTransition t, VmContext ctx) => t.Kind switch
    {
        DvdVmTransitionKind.LinkPGN => System.Math.Max(0, ctx.ProgramToCellIndex(t.A)),
        DvdVmTransitionKind.LinkCN => System.Math.Max(0, t.A - 1),
        _ => 0,
    };
}
