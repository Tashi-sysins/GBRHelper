using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;

namespace GBRHelper.Translation;

/// <summary>
/// ImGui の公開関数（cimgui.dll）をフックし、GBR のメイン画面の中で描かれる英語を日本語に差し替える。
///
/// 【なぜ公開関数をフックすると訳せるか】
///   Dalamud の ImGui（Dalamud.Bindings.ImGui）は、cimgui.dll の公開関数の場所を名前で引いて呼んでいる。
///   GBR が ImGui.Checkbox("Enabled", …) と書くと、最後は cimgui.dll の igCheckbox が呼ばれる。
///   そこを横取りし、ラベルを訳に差し替えてから本物の igCheckbox を呼ぶ。GBR 本体には一切手を入れない。
///   ImGui の内部どうしの呼び出し（igCheckbox の中で文字を描く等）は公開関数を通らないので、二重には訳されない。
///
/// 【どの関数を押さえるか】（2026-10-05 に GBR 7.5.6.1・ElliLib・Dalamud.Bindings.ImGui のソースで経路を確かめた）
///   文字だけ：igTextUnformatted（Text・TextColored・TextDisabled・TextWrapped・SetTooltip・説明の吹き出しはすべてここ）
///             igRenderText（BulletText）・igTreeNodeBehavior（折りたたみの見出し。ID は Dalamud が元の文字で先に作る）
///   ID を持つ部品：igButton・igSmallButton・igCheckbox・igRadioButton_Bool・igSelectable_Bool・igSelectable_BoolPtr・
///             igCollapsingHeader_TreeNodeFlags・igCollapsingHeader_BoolPtr・igBeginTabItem・igBeginCombo・
///             igCombo_FnBoolPtr・igDragScalar（DragInt・DragFloat）・igInputTextEx（InputText・InputTextWithHint）・
///             igTableSetupColumn・igTableHeader・igColorEdit3・
///             igMenuItem_Bool・igMenuItem_BoolPtr・igBeginMenu（右クリックメニュー。2026-10-05 追加）
///   描画リストへ直接：ImDrawList_AddText_Vec2・ImDrawList_AddText_FontPtr（魚の吹き出しの枠付きの文字など。2026-10-05 追加）
///   幅を測るだけ：igCalcTextSize（GBR は英語の幅で部品を並べるため、測る文字も同じ訳にする）
///   タブの並び：igBeginTabItem の最初の呼び出しで、GBR のタブの並びが GBR のソースの順からずれていたら毎フレーム戻す（TabOrderRepair）。
///   ポップアップの名前（OpenPopup／BeginPopupModal）は訳さない。開く側と開かれる側の名前が食い違うと開かなくなるため。
///   ID を持つ部品のラベルは、ID が元と同じになるように組み立てる（ImGuiLabel.Translate）。タブの並び・開いた見出し等が変わらない。
///
/// 【どこで訳すか】いま Begin されている窓の積み重ねに GBR のメイン画面（ID は「###GatherBuddyMain」から作られる）が
///   入っているときだけ（WindowScope）。
///   それ以外の窓（他のプラグイン・GBRHelper 自身）の呼び出しは、元のまま本物へ渡す。
///
/// 【安全】
///   ・フックは「GBR の日本語表示」が有効な間だけ置き、無効化・プラグインの終了で外す。
///   ・差し替えの途中で例外が出たら、元の引数のまま本物を呼ぶ。例外は数えて、最初の 1 件だけ記録する。
///   ・どれか 1 つの公開関数が見つからなくても、ほかは置く（見つからなかった名前は画面に出す）。
///   ・二つの名前が同じ場所を指していたら、その場所は 1 回しかフックしない（同じ場所を二重に横取りしないため）。
/// すべて ImGui を描くスレッド（ゲームの本体のスレッド）で呼ばれる。
/// </summary>
public sealed unsafe class GbrTextHooks : IDisposable, ComboGetterBridge.ITextSwap
{
    /// <summary>GBR のメイン画面の窓の名前のうち、ID を決める部分（GatherBuddy/Gui/Interface.cs）。</summary>
    public const string TargetWindowIdPart = "###GatherBuddyMain";

    /// <summary>1 つの文字列として扱う長さの上限（バイト）。これより長いものは訳さない。</summary>
    private const int MaxBytes = 8192;

    /// <summary>訳の UTF-8 を覚えておく上限。</summary>
    private const int EncodeLimit = 8000;

    /// <summary>選択欄の候補の訳を置いておく上限。</summary>
    private const int PinnedLimit = 2000;

    /// <summary>訳の無かった英語を集める上限。</summary>
    public const int UntranslatedLimit = 3000;

    // ---- 公開関数の形（x64 の呼び出し規約。Vector2 の値渡しは 8 バイトなので long で受けて、そのまま渡す） ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TextUnformattedFn(nint text, nint textEnd);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderTextFn(long pos, nint text, nint textEnd, byte hideAfterHash);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte TreeNodeBehaviorFn(uint id, int flags, nint label, nint labelEnd);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ButtonFn(nint label, long size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte LabelOnlyFn(nint label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte CheckboxFn(nint label, nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte RadioButtonFn(nint label, byte active);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte SelectableFn(nint label, byte selected, int flags, long size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte SelectablePtrFn(nint label, nint selected, int flags, long size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte LabelFlagsFn(nint label, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte LabelPtrFlagsFn(nint label, nint ptr, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BeginComboFn(nint label, nint preview, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ComboFnFn(nint label, nint current, nint getter, nint data, int count, int maxHeight);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte DragScalarFn(nint label, int dataType, nint data, float speed, nint min, nint max, nint format, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte InputTextExFn(nint label, nint hint, nint buf, int bufSize, long size, int flags, nint callback, nint userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TableSetupColumnFn(nint label, int flags, float initWidth, uint userId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TableHeaderFn(nint label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ColorEdit3Fn(nint label, nint col, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CalcTextSizeFn(nint output, nint text, nint textEnd, byte hide, float wrapWidth);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte MenuItemFn(nint label, nint shortcut, byte selected, byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte MenuItemPtrFn(nint label, nint shortcut, nint selected, byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BeginMenuFn(nint label, byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddTextVec2Fn(nint drawList, long pos, uint col, nint text, nint textEnd);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddTextFontFn(nint drawList, nint font, float fontSize, long pos, uint col, nint text, nint textEnd, float wrapWidth, nint clipRect);

    private readonly TranslationTable table;
    private readonly uint targetId;
    private readonly List<IDisposable> hooks = new();
    private readonly Dictionary<string, byte[]> encoded = new(StringComparer.Ordinal);

    /// <summary>選択欄の候補の訳の置き場（場所の動かない配列）。ComboFnDetour の呼び出しの外でだけ捨てる。</summary>
    private readonly Dictionary<string, byte[]> pinned = new(StringComparer.Ordinal);

    /// <summary>ID を元と同じにしたラベル（窓の ID の種と元のラベルの組ごと）。毎フレーム作り直さないため。</summary>
    private readonly Dictionary<(uint Seed, string Label), byte[]?> labels = new();

    /// <summary>訳している文言（種類を数えるため）。</summary>
    private readonly HashSet<string> translatedKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> untranslated = new(StringComparer.Ordinal);
    private readonly List<string> missing = new();

    private Hook<TextUnformattedFn>? hText;
    private Hook<RenderTextFn>? hRenderText;
    private Hook<TreeNodeBehaviorFn>? hTreeNode;
    private Hook<ButtonFn>? hButton;
    private Hook<LabelOnlyFn>? hSmallButton;
    private Hook<CheckboxFn>? hCheckbox;
    private Hook<RadioButtonFn>? hRadio;
    private Hook<SelectableFn>? hSelectable;
    private Hook<SelectablePtrFn>? hSelectablePtr;
    private Hook<LabelFlagsFn>? hHeader;
    private Hook<LabelPtrFlagsFn>? hHeaderPtr;
    private Hook<LabelPtrFlagsFn>? hTabItem;
    private Hook<BeginComboFn>? hBeginCombo;
    private Hook<ComboFnFn>? hComboFn;
    private Hook<DragScalarFn>? hDrag;
    private Hook<InputTextExFn>? hInput;
    private Hook<TableSetupColumnFn>? hColumn;
    private Hook<TableHeaderFn>? hTableHeader;
    private Hook<ColorEdit3Fn>? hColorEdit;
    private Hook<CalcTextSizeFn>? hCalc;
    private Hook<MenuItemFn>? hMenuItem;
    private Hook<MenuItemPtrFn>? hMenuItemPtr;
    private Hook<BeginMenuFn>? hBeginMenu;
    private Hook<AddTextVec2Fn>? hAddTextVec2;
    private Hook<AddTextFontFn>? hAddTextFont;

    private bool disposed;

    /// <summary>GBR のタブの並びがずれていて戻した回数（TabOrderRepair）。dalamud.log に書くのは最初の数回だけ。</summary>
    public int TabOrderFixes { get; private set; }

    /// <summary>タブの並べ直しで例外が出たら、以後は並べ直さない（毎フレーム例外を出し続けないため）。</summary>
    private bool tabOrderBroken;

    /// <summary>dalamud.log にタブの並べ直しを書く回数の上限。</summary>
    private const int TabOrderLogLimit = 5;

    private GbrTextHooks(TranslationTable table)
    {
        this.table = table;
        this.targetId = ImGuiP.ImHashStr(TargetWindowIdPart);
    }

    /// <summary>置こうとした公開関数の数。</summary>
    public int Requested { get; private set; }

    /// <summary>置けた数。</summary>
    public int Installed => this.hooks.Count;

    /// <summary>見つからなかった・置けなかった公開関数の名前と理由。</summary>
    public IReadOnlyList<string> Missing => this.missing;

    /// <summary>
    /// GBR の画面の中で訳している文言の種類の数。
    /// ImGui は毎フレーム画面を描き直す（同じ文言が毎フレーム描かれる）ので、回数ではなく種類で数える
    /// （指摘：回数が凄い勢いで増える）。訳そのものは文言ごとに一度だけ辞書を引き、結果を覚えて使い回している。
    /// </summary>
    public int TranslatedKinds => this.translatedKinds.Count;

    /// <summary>最後に GBR の画面の中で呼ばれたときの ImGui のフレーム番号。一度も無ければ -1。</summary>
    public int LastInsideFrame { get; private set; } = -1;

    /// <summary>差し替えの途中で出た例外の数。</summary>
    public int ErrorCount { get; private set; }

    /// <summary>最初の例外の内容。</summary>
    public string FirstError { get; private set; } = string.Empty;

    /// <summary>訳の無かった英語を集めるか（辞書を足すための記録）。</summary>
    public bool CollectUntranslated { get; set; }

    /// <summary>集めた「訳の無かった英語」。</summary>
    public IReadOnlyCollection<string> Untranslated => this.untranslated;

    /// <summary>集めた記録を捨てる。</summary>
    public void ClearUntranslated() => this.untranslated.Clear();

    /// <summary>
    /// フックを置く。cimgui.dll が読み込まれていなければ例外。
    /// </summary>
    public static GbrTextHooks Install(TranslationTable table)
    {
        var h = new GbrTextHooks(table);
        try
        {
            h.InstallAll();
        }
        catch
        {
            h.Dispose();
            throw;
        }

        return h;
    }

    private void InstallAll()
    {
        var module = GetModuleHandleW("cimgui.dll");
        if (module == 0)
            throw new InvalidOperationException("cimgui.dll が読み込まれていません（Dalamud の ImGui が見つからない）");

        var used = new HashSet<nint>();

        void Add<T>(string name, T detour, out Hook<T>? hook)
            where T : Delegate
        {
            hook = null;
            this.Requested++;

            if (!NativeLibrary.TryGetExport(module, name, out var address) || address == 0)
            {
                this.missing.Add($"{name}（cimgui.dll に見つからない）");
                return;
            }

            if (!used.Add(address))
            {
                this.missing.Add($"{name}（ほかの関数と同じ場所のため置かない）");
                return;
            }

            try
            {
                hook = Svc.Hooks.HookFromAddress(address, detour);
                hook.Enable();
                this.hooks.Add(hook);
            }
            catch (Exception ex)
            {
                hook?.Dispose();
                hook = null;
                this.missing.Add($"{name}（置けない：{ex.GetType().Name}: {ex.Message}）");
            }
        }

        Add<TextUnformattedFn>("igTextUnformatted", this.TextDetour, out this.hText);
        Add<RenderTextFn>("igRenderText", this.RenderTextDetour, out this.hRenderText);
        Add<TreeNodeBehaviorFn>("igTreeNodeBehavior", this.TreeNodeDetour, out this.hTreeNode);
        Add<ButtonFn>("igButton", this.ButtonDetour, out this.hButton);
        Add<LabelOnlyFn>("igSmallButton", this.SmallButtonDetour, out this.hSmallButton);
        Add<CheckboxFn>("igCheckbox", this.CheckboxDetour, out this.hCheckbox);
        Add<RadioButtonFn>("igRadioButton_Bool", this.RadioDetour, out this.hRadio);
        Add<SelectableFn>("igSelectable_Bool", this.SelectableDetour, out this.hSelectable);
        Add<SelectablePtrFn>("igSelectable_BoolPtr", this.SelectablePtrDetour, out this.hSelectablePtr);
        Add<LabelFlagsFn>("igCollapsingHeader_TreeNodeFlags", this.HeaderDetour, out this.hHeader);
        Add<LabelPtrFlagsFn>("igCollapsingHeader_BoolPtr", this.HeaderPtrDetour, out this.hHeaderPtr);
        Add<LabelPtrFlagsFn>("igBeginTabItem", this.TabItemDetour, out this.hTabItem);
        Add<BeginComboFn>("igBeginCombo", this.BeginComboDetour, out this.hBeginCombo);
        Add<ComboFnFn>("igCombo_FnBoolPtr", this.ComboFnDetour, out this.hComboFn);
        Add<DragScalarFn>("igDragScalar", this.DragDetour, out this.hDrag);
        Add<InputTextExFn>("igInputTextEx", this.InputDetour, out this.hInput);
        Add<TableSetupColumnFn>("igTableSetupColumn", this.ColumnDetour, out this.hColumn);
        Add<TableHeaderFn>("igTableHeader", this.TableHeaderDetour, out this.hTableHeader);
        Add<ColorEdit3Fn>("igColorEdit3", this.ColorEditDetour, out this.hColorEdit);
        Add<CalcTextSizeFn>("igCalcTextSize", this.CalcDetour, out this.hCalc);

        // 右クリックメニュー（自動採集の一覧の「Move Up」「Delete List」など）。中の文字は ImGui の内部の描画で描かれ、
        // 公開関数を通らないので、項目そのものを押さえる（2026-10-05 下書き B の指摘）。ID はほかの部品と同じくラベルから作られる。
        Add<MenuItemFn>("igMenuItem_Bool", this.MenuItemDetour, out this.hMenuItem);
        Add<MenuItemPtrFn>("igMenuItem_BoolPtr", this.MenuItemPtrDetour, out this.hMenuItemPtr);
        Add<BeginMenuFn>("igBeginMenu", this.BeginMenuDetour, out this.hBeginMenu);

        // 描画リストへ直接描く文字（魚の吹き出しの枠付きの文字・効果の絞り込みの名前・影付きの文字など。ElliLib の TextFramed は
        // Dalamud の AddTextClippedEx を通って AddText_FontPtr に来る）。幅は igCalcTextSize で訳した文字で測られるので、
        // 描く文字も訳さないと「幅は日本語・文字は英語」になる（2026-10-05 下書き A・B の指摘）。
        // ImGui の内部の描画（RenderText など）は公開関数を通らないので、ここで二重には訳されない。
        Add<AddTextVec2Fn>("ImDrawList_AddText_Vec2", this.AddTextVec2Detour, out this.hAddTextVec2);
        Add<AddTextFontFn>("ImDrawList_AddText_FontPtr", this.AddTextFontDetour, out this.hAddTextFont);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        foreach (var h in this.hooks)
        {
            try
            {
                h.Dispose();
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"[GBRHelper] 翻訳のフックを外せませんでした: {ex}");
            }
        }

        this.hooks.Clear();
    }

    // ------------------------------------------------------------------
    // 差し替え（各関数）

    private void TextDetour(nint text, nint textEnd)
    {
        if (this.TryPlain(text, textEnd, out var buf, out var len))
        {
            fixed (byte* p = buf)
                this.hText!.Original((nint)p, (nint)(p + len));
            return;
        }

        this.hText!.Original(text, textEnd);
    }

    private void RenderTextDetour(long pos, nint text, nint textEnd, byte hideAfterHash)
    {
        if (this.TryMeasured(text, textEnd, hideAfterHash != 0, out var buf, out var len))
        {
            fixed (byte* p = buf)
                this.hRenderText!.Original(pos, (nint)p, (nint)(p + len), hideAfterHash);
            return;
        }

        this.hRenderText!.Original(pos, text, textEnd, hideAfterHash);
    }

    private void AddTextVec2Detour(nint drawList, long pos, uint col, nint text, nint textEnd)
    {
        if (this.TryPlain(text, textEnd, out var buf, out var len))
        {
            fixed (byte* p = buf)
                this.hAddTextVec2!.Original(drawList, pos, col, (nint)p, (nint)(p + len));
            return;
        }

        this.hAddTextVec2!.Original(drawList, pos, col, text, textEnd);
    }

    private void AddTextFontDetour(nint drawList, nint font, float fontSize, long pos, uint col, nint text, nint textEnd, float wrapWidth, nint clipRect)
    {
        if (this.TryPlain(text, textEnd, out var buf, out var len))
        {
            fixed (byte* p = buf)
                this.hAddTextFont!.Original(drawList, font, fontSize, pos, col, (nint)p, (nint)(p + len), wrapWidth, clipRect);
            return;
        }

        this.hAddTextFont!.Original(drawList, font, fontSize, pos, col, text, textEnd, wrapWidth, clipRect);
    }

    private byte TreeNodeDetour(uint id, int flags, nint label, nint labelEnd)
    {
        // ID（id）は Dalamud が元のラベルから作って渡してくる。表示の文字だけを訳すので、開閉の状態は変わらない。
        if (this.TryPlain(label, labelEnd, out var buf, out var len))
        {
            fixed (byte* p = buf)
                return this.hTreeNode!.Original(id, flags, (nint)p, (nint)(p + len));
        }

        return this.hTreeNode!.Original(id, flags, label, labelEnd);
    }

    private byte ButtonDetour(nint label, long size)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hButton!.Original((nint)p, size);
        }

        return this.hButton!.Original(label, size);
    }

    private byte SmallButtonDetour(nint label)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hSmallButton!.Original((nint)p);
        }

        return this.hSmallButton!.Original(label);
    }

    private byte MenuItemDetour(nint label, nint shortcut, byte selected, byte enabled)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hMenuItem!.Original((nint)p, shortcut, selected, enabled);
        }

        return this.hMenuItem!.Original(label, shortcut, selected, enabled);
    }

    private byte MenuItemPtrDetour(nint label, nint shortcut, nint selected, byte enabled)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hMenuItemPtr!.Original((nint)p, shortcut, selected, enabled);
        }

        return this.hMenuItemPtr!.Original(label, shortcut, selected, enabled);
    }

    private byte BeginMenuDetour(nint label, byte enabled)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hBeginMenu!.Original((nint)p, enabled);
        }

        return this.hBeginMenu!.Original(label, enabled);
    }

    private byte CheckboxDetour(nint label, nint value)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hCheckbox!.Original((nint)p, value);
        }

        return this.hCheckbox!.Original(label, value);
    }

    private byte RadioDetour(nint label, byte active)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hRadio!.Original((nint)p, active);
        }

        return this.hRadio!.Original(label, active);
    }

    private byte SelectableDetour(nint label, byte selected, int flags, long size)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hSelectable!.Original((nint)p, selected, flags, size);
        }

        return this.hSelectable!.Original(label, selected, flags, size);
    }

    private byte SelectablePtrDetour(nint label, nint selected, int flags, long size)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hSelectablePtr!.Original((nint)p, selected, flags, size);
        }

        return this.hSelectablePtr!.Original(label, selected, flags, size);
    }

    private byte HeaderDetour(nint label, int flags)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hHeader!.Original((nint)p, flags);
        }

        return this.hHeader!.Original(label, flags);
    }

    private byte HeaderPtrDetour(nint label, nint visible, int flags)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hHeaderPtr!.Original((nint)p, visible, flags);
        }

        return this.hHeaderPtr!.Original(label, visible, flags);
    }

    private byte TabItemDetour(nint label, nint open, int flags)
    {
        this.KeepTabOrder();

        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hTabItem!.Original((nint)p, open, flags);
        }

        return this.hTabItem!.Original(label, open, flags);
    }

    /// <summary>
    /// GBR のメイン画面のタブの並びを、毎フレーム GBR のソースの順に保つ（TabOrderRepair）。
    /// 要望「初期位置の場所から動かさないで」。ずれていたら戻し、最初の数回はどの並びだったかを dalamud.log に書く。
    /// GBR のメイン画面の中の、並べ替えできるタブバー（GBR の Interface.cs の ConfigTabs）だけを扱う。
    /// </summary>
    private void KeepTabOrder()
    {
        if (this.tabOrderBroken)
            return;

        try
        {
            var bar = ImGui.GetCurrentContext().Handle->CurrentTabBar;
            if (bar == null || bar->TabsActiveCount != 0 || (bar->Flags & ImGuiTabBarFlags.Reorderable) == 0 || !this.Inside())
                return;

            var frame = ImGui.GetFrameCount();
            if (!TabOrderRepair.NeedsKeep(bar, frame))
                return;

            var before = this.TabOrderFixes < TabOrderLogLimit ? TabOrderRepair.Describe(bar) : null;
            if (!TabOrderRepair.Keep(bar, frame))
                return;

            this.TabOrderFixes++;
            if (before is not null)
                Svc.Log.Information($"[GBRHelper] GBR のタブの並びがずれていたので、GBR のソースの順に戻しました（{this.TabOrderFixes} 回目）。戻す前：{before}");
        }
        catch (Exception ex)
        {
            this.tabOrderBroken = true;
            this.Fail(ex);
        }
    }

    private byte BeginComboDetour(nint label, nint preview, int flags)
    {
        var l = this.TryLabel(label, out var lb);
        var v = this.TryPlain(preview, 0, out var pb, out _);
        if (!l && !v)
            return this.hBeginCombo!.Original(label, preview, flags);

        fixed (byte* lp = lb)
        fixed (byte* pp = pb)
            return this.hBeginCombo!.Original(l ? (nint)lp : label, v ? (nint)pp : preview, flags);
    }

    /// <summary>
    /// 候補を関数（getter）で渡す選択欄（ImGui.Combo(label, ref i, string[]) など。GBR のアラームの効果音）。
    ///
    /// 【なぜ getter を包むか】
    ///   imgui 1.88 の Combo は、getter から選択中の文字と各候補の文字を取り出し、C++ の内部で BeginCombo・Selectable を呼んで描く。
    ///   内部の呼び出しは公開関数を通らないので、ほかのフックでは候補が訳せない。そこで GBR の画面の中のときだけ、
    ///   getter を ComboGetterBridge で包む。本物の getter が返した文字を TrySwap で訳し、訳の場所に差し替えて返す。
    /// </summary>
    private byte ComboFnDetour(nint label, nint current, nint getter, nint data, int count, int maxHeight)
    {
        var l = this.TryLabel(label, out var buf);

        var wrap = false;
        try
        {
            wrap = getter != 0 && this.Inside();
        }
        catch (Exception ex)
        {
            this.Fail(ex);
        }

        if (!wrap)
        {
            fixed (byte* p = buf)
                return this.hComboFn!.Original(l ? (nint)p : label, current, getter, data, count, maxHeight);
        }

        // 訳の置き場は、選択欄の呼び出しの外（ここ）でだけ捨てる。呼び出しの最中に捨てると、ImGui が使っている最中の文字が消える。
        if (this.pinned.Count >= PinnedLimit)
            this.pinned.Clear();

        return ComboGetterBridge.Invoke(this, getter, data, (wrappedGetter, context) =>
        {
            fixed (byte* p = buf)
                return this.hComboFn!.Original(l ? (nint)p : label, current, wrappedGetter, context, count, maxHeight);
        });
    }

    /// <summary>
    /// 選択欄の getter が返した文字を訳し、場所の動かない（pinned）配列に置いて、その場所を返す（ComboGetterBridge から呼ばれる）。
    /// ImGui は返った場所を getter から戻ったあとも使うので、GC で場所が動く普通の配列は使えない。
    /// </summary>
    bool ComboGetterBridge.ITextSwap.TrySwap(nint text, out nint translated)
    {
        translated = 0;
        try
        {
            if (ReadUtf8(text, 0) is not { } s)
                return false;

            var t = ImGuiLabel.TranslatePlain(s, this.table.Translate);
            if (t is null)
            {
                this.Collect(s);
                return false;
            }

            if (!this.pinned.TryGetValue(t, out var bytes))
            {
                var n = Encoding.UTF8.GetByteCount(t);
                bytes = GC.AllocateUninitializedArray<byte>(n + 1, pinned: true);
                Encoding.UTF8.GetBytes(t, 0, t.Length, bytes, 0);
                bytes[n] = 0;
                this.pinned[t] = bytes;
            }

            fixed (byte* p = bytes)
                translated = (nint)p;

            this.Hit(s);
            return true;
        }
        catch (Exception ex)
        {
            this.Fail(ex);
            translated = 0;
            return false;
        }
    }

    private byte DragDetour(nint label, int dataType, nint data, float speed, nint min, nint max, nint format, int flags)
    {
        var l = this.TryLabel(label, out var lb);
        var f = this.TryPlain(format, 0, out var fb, out _);
        if (!l && !f)
            return this.hDrag!.Original(label, dataType, data, speed, min, max, format, flags);

        fixed (byte* lp = lb)
        fixed (byte* fp = fb)
            return this.hDrag!.Original(l ? (nint)lp : label, dataType, data, speed, min, max, f ? (nint)fp : format, flags);
    }

    private byte InputDetour(nint label, nint hint, nint buf, int bufSize, long size, int flags, nint callback, nint userData)
    {
        var l = this.TryLabel(label, out var lb);
        var h = this.TryPlain(hint, 0, out var hb, out _);
        if (!l && !h)
            return this.hInput!.Original(label, hint, buf, bufSize, size, flags, callback, userData);

        fixed (byte* lp = lb)
        fixed (byte* hp = hb)
            return this.hInput!.Original(l ? (nint)lp : label, h ? (nint)hp : hint, buf, bufSize, size, flags, callback, userData);
    }

    private void ColumnDetour(nint label, int flags, float initWidth, uint userId)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                this.hColumn!.Original((nint)p, flags, initWidth, userId);
            return;
        }

        this.hColumn!.Original(label, flags, initWidth, userId);
    }

    private void TableHeaderDetour(nint label)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                this.hTableHeader!.Original((nint)p);
            return;
        }

        this.hTableHeader!.Original(label);
    }

    private byte ColorEditDetour(nint label, nint col, int flags)
    {
        if (this.TryLabel(label, out var buf))
        {
            fixed (byte* p = buf)
                return this.hColorEdit!.Original((nint)p, col, flags);
        }

        return this.hColorEdit!.Original(label, col, flags);
    }

    private void CalcDetour(nint output, nint text, nint textEnd, byte hide, float wrapWidth)
    {
        // 幅を測るだけなので「訳の無かった英語」には数えない（描く側で数える）。
        if (this.TryMeasured(text, textEnd, hide != 0, out var buf, out var len, collect: false))
        {
            fixed (byte* p = buf)
                this.hCalc!.Original(output, (nint)p, (nint)(p + len), hide, wrapWidth);
            return;
        }

        this.hCalc!.Original(output, text, textEnd, hide, wrapWidth);
    }

    // ------------------------------------------------------------------
    // 共通

    /// <summary>
    /// ID を持つ部品のラベルを訳す。訳せたら buf に、画面には訳が出て ID は元のラベルと同じになる UTF-8 のラベル（終端 0 付き）。
    /// ID の計算の種は、いまの窓の ID の積み重ねの一番上（ImGui の ImGuiWindow::GetID と同じ）。
    /// </summary>
    private bool TryLabel(nint label, out byte[]? buf)
    {
        buf = null;
        try
        {
            if (label == 0 || !this.Inside())
                return false;

            if (ReadUtf8(label, 0) is not { } s)
                return false;

            var seed = CurrentIdSeed();
            if (!this.labels.TryGetValue((seed, s), out var made))
            {
                if (this.labels.Count >= EncodeLimit)
                    this.labels.Clear();
                made = ImGuiLabel.Translate(s, seed, this.table.Translate);
                this.labels[(seed, s)] = made;
            }

            if (made is null)
            {
                this.Collect(ImGuiLabel.Split(s).Visible);
                return false;
            }

            buf = made;
            this.Hit(s);
            return true;
        }
        catch (Exception ex)
        {
            this.Fail(ex);
            buf = null;
            return false;
        }
    }

    /// <summary>ID を持たない文字を訳す。訳せたら buf に UTF-8（終端 0 付き）、len に終端を除いた長さ。</summary>
    private bool TryPlain(nint text, nint textEnd, out byte[]? buf, out int len)
    {
        buf = null;
        len = 0;
        try
        {
            if (text == 0 || !this.Inside())
                return false;

            if (ReadUtf8(text, textEnd) is not { } s)
                return false;

            var t = ImGuiLabel.TranslatePlain(s, this.table.Translate);
            if (t is null)
            {
                this.Collect(s);
                return false;
            }

            buf = this.Encode(t);
            len = buf.Length - 1;
            this.Hit(s);
            return true;
        }
        catch (Exception ex)
        {
            this.Fail(ex);
            buf = null;
            len = 0;
            return false;
        }
    }

    /// <summary>描く・測る文字を訳す（「## から後ろを隠す」指定に従う）。</summary>
    private bool TryMeasured(nint text, nint textEnd, bool hide, out byte[]? buf, out int len, bool collect = true)
    {
        buf = null;
        len = 0;
        try
        {
            if (text == 0 || !this.Inside())
                return false;

            if (ReadUtf8(text, textEnd) is not { } s)
                return false;

            var t = ImGuiLabel.TranslateMeasured(s, hide, this.table.Translate);
            if (t is null)
            {
                if (collect)
                    this.Collect(ImGuiLabel.Split(s).Visible);
                return false;
            }

            buf = this.Encode(t);
            len = buf.Length - 1;
            if (collect)
                this.Hit(s);
            return true;
        }
        catch (Exception ex)
        {
            this.Fail(ex);
            buf = null;
            len = 0;
            return false;
        }
    }

    /// <summary>訳した文言を数える（種類だけ。上限を超えたら数え直す）。</summary>
    private void Hit(string original)
    {
        if (this.translatedKinds.Count >= EncodeLimit)
            this.translatedKinds.Clear();
        this.translatedKinds.Add(original);
    }

    /// <summary>いまの窓の ID の積み重ねの一番上（ImGui が部品の ID を作るときの種。imgui.cpp:3370）。窓が無ければ 0。</summary>
    private static uint CurrentIdSeed()
    {
        var w = ImGuiP.GetCurrentWindowRead().Handle;
        if (w == null || w->IDStack.Size == 0)
            return 0;
        return w->IDStack.Data[w->IDStack.Size - 1];
    }

    /// <summary>いま描いている呼び出しが GBR のメイン画面の中か（いま Begin されている窓の積み重ねに GBR の窓があるか）。</summary>
    private bool Inside()
    {
        if (!WindowScope.IsInside(ImGuiWindowStack.Current(), this.targetId))
            return false;

        this.LastInsideFrame = ImGui.GetFrameCount();
        return true;
    }

    /// <summary>UTF-8 の文字列を読む。end が 0 なら終端 0 まで。長すぎる・空なら null。</summary>
    private static string? ReadUtf8(nint begin, nint end)
    {
        var p = (byte*)begin;
        int len;
        if (end != 0)
        {
            var n = (long)end - (long)begin;
            if (n <= 0 || n > MaxBytes)
                return null;
            len = (int)n;
        }
        else
        {
            len = 0;
            while (p[len] != 0)
            {
                if (++len > MaxBytes)
                    return null;
            }

            if (len == 0)
                return null;
        }

        return Encoding.UTF8.GetString(p, len);
    }

    /// <summary>訳を UTF-8（終端 0 付き）にする。同じ訳は作り直さない。</summary>
    private byte[] Encode(string s)
    {
        if (this.encoded.TryGetValue(s, out var b))
            return b;

        if (this.encoded.Count >= EncodeLimit)
            this.encoded.Clear();

        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        this.encoded[s] = bytes;
        return bytes;
    }

    /// <summary>訳の無かった英語を記録する（集める設定のときだけ）。英字を含むものだけ。</summary>
    private void Collect(string s)
    {
        if (!this.CollectUntranslated || this.untranslated.Count >= UntranslatedLimit)
            return;

        if (s.Length == 0 || s.Length > 1000)
            return;

        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                this.untranslated.Add(s);
                return;
            }
        }
    }

    private void Fail(Exception ex)
    {
        this.ErrorCount++;
        if (this.ErrorCount == 1)
        {
            this.FirstError = $"{ex.GetType().Name}: {ex.Message}";
            Svc.Log.Error($"[GBRHelper] 翻訳の差し替えで例外（元の文字のまま描きます）: {ex}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string moduleName);
}
