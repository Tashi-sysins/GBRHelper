using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using GBRHelper.Translation;

namespace GBRHelper.Ui;

/// <summary>
/// GBR のメイン画面への小さな手出し。
///   ① GBRHelper の「GBR の画面を開く」を押したら、GBR の「自動採集」（Auto-Gather）タブを開く（RequestAutoGatherTab）。
///   ② GBR の自動採集タブの「Artisan から読み込む」の上あたりに「GBRHelperを開く」ボタンを置く。
///
/// 【なぜフックか】GBR には外からタブを選ぶ口も、画面に部品を足す口も無い（GatherBuddy/Gui/Interface.cs）。
///   日本語表示（GbrTextHooks）と同じく、cimgui.dll の公開関数を横取りして、GBR のメイン画面の中でだけ手を入れる。GBR 本体には手を入れない。
/// 【どの関数か】日本語表示が横取りしていない 2 つだけ（同じ関数を重ねて横取りすると、どちらが先に呼ばれるかで「元の英語のラベル」で
///   見分ける処理が食い違うため）。
///   ・igBeginTabBar：GBR のタブバー（"ConfigTabs###GatherBuddyConfigTabs"。Interface.cs:54）が始まった直後に、
///     タブバーの「次に選ぶタブ」（ImGuiTabBar.NextSelectedTabId）を自動採集のタブにする。ImGui はタブバーを始めると並べ直し待ちになり
///     （imgui 1.88 imgui_widgets.cpp:7461）、最初のタブを描くときに並べ直して、次に選ぶタブを選んだタブにする（同 8158-8161・7587-7590）。
///     そのため同じフレームで自動採集のタブが開く。日本語表示はタブの ID を元のまま訳すので、訳していても同じ ID で選べる。
///   ・igBeginChild_Str：自動採集タブの左の一覧の子窓（"AutoGatherListSelector"。Interface.AutoGatherTab.cs:489）を始める直前に、
///     ボタンを 1 行描く。ボタンの分だけ、一覧と詳細が 1 行下がる。
/// 【安全】
///   ・GBR のメイン画面（窓の ID は "###GatherBuddyMain" から作られる）がいま描いている窓のときだけ手を入れる。ほかの窓は元のまま本物へ渡す。
///   ・途中で例外が出たら、元の引数のまま本物を呼ぶ。例外は数えて、最初の 1 件だけ記録する。
///   ・フックは ImGui を描くスレッドの上（Plugin.OnDraw）で置き、プラグインの終了で外す。置けなかったら置き直さない（①は開くだけ、②はボタン無し）。
/// すべて ImGui を描くスレッド（ゲームの本体のスレッド）で呼ばれる。
/// </summary>
public sealed unsafe class GbrWindowExtras : IDisposable
{
    /// <summary>GBR のメイン画面のタブバーの名前（GatherBuddy/Gui/Interface.cs:54）。</summary>
    public const string TabBarId = "ConfigTabs###GatherBuddyConfigTabs";

    /// <summary>自動採集タブの前に GBR が積む ID（GatherBuddy/Gui/Interface.AutoGatherTab.cs:477）。</summary>
    public const string AutoGatherPushId = "AutoGatherLists";

    /// <summary>自動採集タブのラベル（同 478）。日本語表示では「自動採集」と出るが、ID は元のラベルから作られる。</summary>
    public const string AutoGatherTabLabel = "Auto-Gather";

    /// <summary>自動採集タブの左の一覧の子窓（同 489）。</summary>
    public const string ListSelectorChildId = "AutoGatherListSelector";

    /// <summary>一覧と詳細のあいだの仕切りの幅（同 496。GBR は倍率を掛けない）。</summary>
    public const float SplitterWidth = 4f;

    /// <summary>GBR の画面に置くボタン（見えない部分は GBR の部品と ID が重ならないための名前）。</summary>
    public const string OpenHelperLabel = "GBRHelperを開く##gbrhelperOpenFromGbr";

    /// <summary>自動採集タブを選ぶのを諦めるまでの時間（GBR の画面が開くのを待つ上限。開いたかは毎フレーム確かめる）。</summary>
    public static readonly TimeSpan SelectLimit = TimeSpan.FromSeconds(5);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BeginTabBarFn(nint strId, int flags);

    // Vector2 の値渡しは x64 では 8 バイトなので long で受けて、そのまま渡す（GbrTextHooks と同じ）。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BeginChildStrFn(nint strId, long size, byte border, int flags);

    private static readonly byte[] TabBarIdUtf8 = Encoding.UTF8.GetBytes(TabBarId);
    private static readonly byte[] ListSelectorUtf8 = Encoding.UTF8.GetBytes(ListSelectorChildId);

    private readonly Action openHelper;
    private readonly uint gbrWindowId;
    private Hook<BeginTabBarFn>? hTabBar;
    private Hook<BeginChildStrFn>? hChild;
    private bool disposed;

    /// <summary>自動採集タブを選ぶのを待っている間の、諦める時刻（待っていなければ null）。</summary>
    private DateTime? selectUntil;

    /// <summary>ボタンを描いたフレーム（同じフレームで二度描かない）。</summary>
    private int buttonFrame = -1;

    private GbrWindowExtras(Action openHelper)
    {
        this.openHelper = openHelper;
        this.gbrWindowId = ImGuiP.ImHashStr(GbrTextHooks.TargetWindowIdPart);
    }

    /// <summary>直近の自動採集タブの選択の結果（デバッグに出す）。</summary>
    public string LastSelect { get; private set; } = "";

    /// <summary>GBR の画面にボタンを描いた回数（デバッグに出す。描いているかの確かめ）。</summary>
    public int ButtonDraws { get; private set; }

    /// <summary>差し込みの途中で出た例外の数と、最初の例外。</summary>
    public int ErrorCount { get; private set; }

    public string FirstError { get; private set; } = string.Empty;

    /// <summary>置けなかった公開関数（無ければ空）。</summary>
    public string Missing { get; private set; } = string.Empty;

    /// <summary>フックを置く。cimgui.dll が無い・どちらの関数も置けなければ例外（呼ぶ側が記録して、置き直さない）。</summary>
    public static GbrWindowExtras Install(Action openHelper)
    {
        var x = new GbrWindowExtras(openHelper);
        try
        {
            var module = GetModuleHandleW("cimgui.dll");
            if (module == 0)
                throw new InvalidOperationException("cimgui.dll が読み込まれていません（Dalamud の ImGui が見つからない）");
            var missing = new StringBuilder();
            x.hTabBar = Place<BeginTabBarFn>(module, "igBeginTabBar", x.TabBarDetour, missing);
            x.hChild = Place<BeginChildStrFn>(module, "igBeginChild_Str", x.ChildDetour, missing);
            x.Missing = missing.ToString();
            if (x.hTabBar is null && x.hChild is null)
                throw new InvalidOperationException("フックを置けませんでした：" + x.Missing);
        }
        catch
        {
            x.Dispose();
            throw;
        }

        return x;
    }

    private static Hook<T>? Place<T>(nint module, string name, T detour, StringBuilder missing)
        where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(module, name, out var address) || address == 0)
        {
            missing.Append($"{name}（cimgui.dll に見つからない）");
            return null;
        }

        var hook = Svc.Hooks.HookFromAddress(address, detour);
        hook.Enable();
        return hook;
    }

    /// <summary>GBR のメイン画面がいま開いているか（このフレームか前のフレームに描かれた）。</summary>
    public static bool IsGbrWindowOpen()
    {
        var w = ImGuiP.FindWindowByID(ImGuiP.ImHashStr(GbrTextHooks.TargetWindowIdPart)).Handle;
        return w != null && (w->Active != 0 || w->WasActive != 0);
    }

    /// <summary>
    /// 次に GBR のタブバーが描かれたとき、自動採集タブを開く（上限 SelectLimit。選べたかは次のフレームのタブバーで確かめる）。
    /// フックが無ければ false（GBR の画面を開くだけになる）。
    /// </summary>
    public bool RequestAutoGatherTab()
    {
        if (this.disposed || this.hTabBar is null)
            return false;
        this.selectUntil = DateTime.UtcNow + SelectLimit;
        return true;
    }

    // ------------------------------------------------------------------
    // ① 自動採集タブを開く

    /// <summary>タブを選ぶ一手の結果。</summary>
    public enum SelectStep
    {
        /// <summary>もう自動採集タブが選ばれている。</summary>
        Done,

        /// <summary>次に選ぶタブにした（このフレームで開く）。</summary>
        Requested,

        /// <summary>タブバーにまだタブが無い（このゲームで初めて GBR の画面を開いたフレーム）。次のフレームで選ぶ。</summary>
        Waiting,

        /// <summary>タブバーに自動採集タブが無い（GBR が変わった）。</summary>
        NotFound,
    }

    /// <summary>
    /// 自動採集タブの ID。GBR はタブバーの中で「AutoGatherLists」を積んでからタブを出すので、
    /// ID ＝ ImHashStr("Auto-Gather", ImHashStr("AutoGatherLists", タブバーの ID))（タブバーはその ID を積んでからタブを出す。imgui_widgets.cpp:7434）。
    /// </summary>
    public static uint AutoGatherTabId(uint tabBarId)
        => ImGuiP.ImHashStr(AutoGatherTabLabel, ImGuiP.ImHashStr(AutoGatherPushId, tabBarId));

    /// <summary>
    /// タブバーが始まった直後（最初のタブより前）に呼ぶ。自動採集タブが選ばれていなければ、次に選ぶタブにする。
    /// タブバーにタブがあるのに自動採集タブが無ければ何もしない（違う ID を選ぶと、そのフレームはどのタブも開かないため）。
    /// タブがまだ 1 つも無い（このゲームで初めて GBR の画面を開いた）フレームでは何もしない：ImGui はそのフレームの並べ直しで
    /// 「選んだタブが見つからない」として選択を消し（imgui_widgets.cpp:7725-7726）、先頭のタブの中身を出す（同 8249）。次のフレームで選ぶ。
    /// </summary>
    public static SelectStep Select(ImGuiTabBar* bar)
    {
        var id = AutoGatherTabId(bar->ID);
        if (bar->SelectedTabId == id)
            return SelectStep.Done;
        if (bar->Tabs.Size == 0)
            return SelectStep.Waiting;
        if (!HasTab(bar, id))
            return SelectStep.NotFound;
        bar->NextSelectedTabId = id;
        return SelectStep.Requested;
    }

    private static bool HasTab(ImGuiTabBar* bar, uint id)
    {
        for (var i = 0; i < bar->Tabs.Size; i++)
            if (bar->Tabs.Data[i].ID == id)
                return true;
        return false;
    }

    private byte TabBarDetour(nint strId, int flags)
    {
        var ret = this.hTabBar!.Original(strId, flags);
        try
        {
            if (ret != 0 && this.selectUntil is { } until && this.InGbrWindow() && SameText(strId, TabBarIdUtf8))
                this.OnGbrTabBar(until);
        }
        catch (Exception ex)
        {
            this.Record(ex);
        }

        return ret;
    }

    private void OnGbrTabBar(DateTime until)
    {
        var bar = ImGui.GetCurrentContext().Handle->CurrentTabBar;
        if (bar == null)
            return;
        switch (Select(bar))
        {
            case SelectStep.Done:
                this.selectUntil = null;
                this.LastSelect = $"{DateTime.Now:HH:mm:ss} 自動採集タブを開きました";
                return;
            case SelectStep.NotFound:
                this.selectUntil = null;
                this.LastSelect = $"{DateTime.Now:HH:mm:ss} GBR の画面に自動採集タブが見つかりません";
                Svc.Log.Warning("[GBRHelper] GBR の画面に自動採集タブ（Auto-Gather）が見つかりません。GBR の画面の作りが変わった可能性があります");
                return;
        }

        if (DateTime.UtcNow > until)
        {
            this.selectUntil = null;
            this.LastSelect = $"{DateTime.Now:HH:mm:ss} 自動採集タブを開けませんでした（{SelectLimit.TotalSeconds:F0} 秒）";
            Svc.Log.Warning($"[GBRHelper] GBR の自動採集タブを {SelectLimit.TotalSeconds:F0} 秒のあいだ開けませんでした");
        }
    }

    // ------------------------------------------------------------------
    // ② 「GBRHelperを開く」ボタン

    /// <summary>
    /// ボタンの左端（画面の座標）。「Artisan から読み込む」の左端にそろえる。
    ///   詳細の左端 ＝ 一覧の左端 ＋ 一覧の幅 ＋ 間隔 ＋ 仕切り（4）＋ 間隔（GBR Interface.AutoGatherTab.cs:489-505。仕切りの前後は SameLine）
    ///   「Artisan から読み込む」の左端 ＝ 詳細の左端 ＋ メニューバーの余白 ＋ コピーのボタンの幅 ＋ 間隔
    ///     メニューバーの余白は ItemSpacing.x（詳細の子窓は WindowPadding を 0 にして作られる：ElliLib ItemDetailsWindow。
    ///     余白は WindowPadding.x と ItemSpacing.x の大きい方：imgui Begin の MenuBarOffset）。コピーのボタンの幅は FrameHeight（GBR Interface.Values.cs:37）。
    /// 一覧の幅が分からない（0 以下＝自動の大きさ）ときは一覧の左端。本物の ImGui で同じ形を描いて、ずれないことを試験で確かめている。
    /// </summary>
    public static float OpenButtonX(float listLeft, float listWidth, float spacingX, float frameHeight)
        => listWidth <= 0 ? listLeft : listLeft + listWidth + spacingX + SplitterWidth + spacingX + spacingX + frameHeight + spacingX;

    /// <summary>
    /// 一覧の子窓を始める直前に呼ぶ。ボタンを 1 行描き、次の部品（一覧）の左端を元の位置に戻す。押されたら true。
    /// listWidth は GBR が一覧の子窓に渡す幅。
    /// </summary>
    public static bool DrawOpenButton(float listWidth)
    {
        var start = ImGui.GetCursorScreenPos();
        var x = OpenButtonX(start.X, listWidth, ImGui.GetStyle().ItemSpacing.X, ImGui.GetFrameHeight());
        ImGui.SetCursorScreenPos(new Vector2(x, start.Y));
        var pressed = ImGui.Button(OpenHelperLabel);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("GBRHelper の画面を開きます。");
        // ボタンのあと ImGui は次の行の左端へ進む。一覧が元と同じ左端から始まるようにそろえる。
        ImGui.SetCursorScreenPos(new Vector2(start.X, ImGui.GetCursorScreenPos().Y));
        return pressed;
    }

    private byte ChildDetour(nint strId, long size, byte border, int flags)
    {
        try
        {
            if (this.InGbrWindow() && SameText(strId, ListSelectorUtf8))
            {
                var frame = ImGui.GetFrameCount();
                if (frame != this.buttonFrame)
                {
                    this.buttonFrame = frame;
                    this.ButtonDraws++;
                    if (DrawOpenButton(BitConverter.Int32BitsToSingle((int)(size & 0xFFFFFFFF))))
                        this.openHelper();
                }
            }
        }
        catch (Exception ex)
        {
            this.Record(ex);
        }

        return this.hChild!.Original(strId, size, border, flags);
    }

    // ------------------------------------------------------------------

    /// <summary>いま描いている窓（子窓ではなく、部品を置く先の窓）が GBR のメイン画面か。</summary>
    private bool InGbrWindow()
    {
        var ctx = ImGui.GetCurrentContext().Handle;
        return ctx != null && ctx->CurrentWindow != null && ctx->CurrentWindow->ID == this.gbrWindowId;
    }

    /// <summary>NUL で終わる UTF-8 の文字列が expected と同じか。</summary>
    public static bool SameText(nint text, byte[] expected)
    {
        if (text == 0)
            return false;
        var p = (byte*)text;
        for (var i = 0; i < expected.Length; i++)
            if (p[i] != expected[i])
                return false;
        return p[expected.Length] == 0;
    }

    private void Record(Exception ex)
    {
        this.ErrorCount++;
        if (this.ErrorCount == 1)
        {
            this.FirstError = $"{ex.GetType().Name}: {ex.Message}";
            Svc.Log.Error($"[GBRHelper] GBR の画面への差し込みで例外: {ex}");
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        this.selectUntil = null;
        foreach (var h in new IDisposable?[] { this.hTabBar, this.hChild })
        {
            try
            {
                h?.Dispose();
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"[GBRHelper] GBR の画面への差し込みのフックを外せませんでした: {ex}");
            }
        }

        this.hTabBar = null;
        this.hChild = null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string moduleName);
}
