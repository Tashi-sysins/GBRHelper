using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using GBRHelper.Ipc;
using GBRHelper.Ui;

namespace GBRHelper.Features;

/// <summary>霊砂・クリスタル：キャラクターごとの、欲しい霊砂（または属性）の選択と数（設定に保存する）。</summary>
public sealed class SandChoice
{
    public HashSet<uint> Selected { get; set; } = new();
    public Dictionary<uint, int> Quantities { get; set; } = new();
}

/// <summary>
/// 霊砂・クリスタル（旧「時限素材・霊砂」）。欲しい霊砂（または属性）と数を選んで「Auto-Gatherに追加」を押すと、その原料（刻限の収集品）を
/// GBR の Auto-Gather のリストに登録し、目標数に達した原料をリストの中で無効にしていく。
///
/// 【要望で変えたこと】
///   ・時限素材（未知・伝説・刻限の通常品）はこの機能から外した（「全素材の補充」で採れるため）。
///   ・画面は AutoDuty の設定タブと同じ「押すと開く見出し」（FoldingHeader）。上から「霊砂」「クリスタル・クラスター」。霊砂は縦 2 列。
///   ・原料は 1 品 200 個で登録し、リストの上から順に採らせる（GBR の並べ替え Item Sorting Method を None にする）。
///   ・目標数に達したら、その原料をリストから消すのではなく無効にする。利用者が GBR で手で有効に戻したら、その品はもう無効にしない
///     （「追加で欲しい場合は手動で有効化」）。GBR の自動採集を止めてもリストは残す（替えるときは「停止して管理リストを削除」→ 追加し直す）。
///   ・クリスタル・クラスター：属性（ファイア〜ウォーター）ごとに欲しい数。原料はゲームデータから自動で選んで並べる（CrystalPlan）。
///     クリスタルとクラスターが両方とも欲しい数に達したら、その属性の原料を無効にする。
///
/// 【数は本プラグインが見る】GBR は霊砂・クリスタルの数を見ない（リストに載っているのは原料の収集品）。
///   採集の切れ目ごとに本人の鞄を数え、目標に達した原料を無効にする（TimedPlan.NeededSources）。
///   全部無効になると、GBR は採るものが無くなって自動採集を自分で止める（GBR の AutoGather.cs 949-953。ほかの有効なリストがあれば、その品を採り続ける）。
/// 【精選】GBR の精選（DoReduce）と「いつも全部精選する」（AlwaysReduceAllItems）を ON にする
///   （GBR は鞄の空きが少ないときや待ち時間に精選する。全部精選しないと、空きが少ないときに 1 種類しか精選しない）。
///   さらに足りない間は、採集の切れ目で本プラグインからも精選を頼み、数を早く反映する。
/// 【GBR の設定を戻す】開始時に変えた GBR の設定（並べ替え・精選・全部精選）は、止めたときに元に戻す（利用者が途中で変えていたら戻さない）。
/// 【同時に 2 つ】霊砂とクリスタルは、それぞれ 1 つずつ同時に登録できる（要望「霊砂のプリセットを設定した時、
///   クリスタルのプリセットが設定出来ないのは辞めて。どっちも設定出来るように」。前は 1 度に 1 つだった）。登録は別々に止められる。
///   GBR の設定の書き換えと戻すための記録は 2 つで共有し、最初の登録で書き換え、最後の登録を止めたときに戻す（Run・TimedRunRules）。
///   同じ種類を替えるときは、その種類を止めてから追加し直す（利用者の手順）。
/// 【ほかのリスト】ほかの Auto-Gather リストが有効でも追加できる。追加したリストは Auto-Gather の一番上へ動かし、
///   登録中も一番上に保つ（要望「ほかのリストを無効にさせるのは不便。自動で一番上に入れる」。
///   前は上から順に採らせるため、ほかのリストをすべて無効にしないと押せなかった）。
///   並べ方「なし」の GBR は、出ている刻限の品をリストの上から採り、常に採れる品はそのあとに回す（GbrAutoGatherListAccess.MoveManagedListToTop）。
///   原料が出ていない時間は、ほかのリストの品を採る。
/// </summary>
public sealed class TimedFeature(Configuration config, LiveCatalogBuilder builder,
    GatheringCompletionReader completion, GbrAutoGatherListAccess lists, GatherBuddyIpc gbr,
    GbrTimedAccess access, Func<bool> busy) : IFeature
{
    /// <summary>左ペインの名前（要望で「霊砂」→「霊砂・クリスタル」）。</summary>
    public const string FeatureName = "霊砂・クリスタル";

    /// <summary>欲しい数の初期値（指定：霊砂 100・クリスタル・クラスター 1000）。</summary>
    public const int DefaultSandQuantity = 100;
    public const int DefaultCrystalQuantity = 1000;
    public const int MinSandQuantity = 1;
    public const int MaxSandQuantity = 9999;

    public string Name => FeatureName;
    public string Description => "欲しい霊砂・クリスタルを選ぶと、その原料の収集品を GBR の Auto-Gather に登録します。目標数に達した原料はリストの中で無効にします。";
    public int SortOrder => 120;
    public bool Enabled { get => true; set { } }
    public bool HideEnableToggle => true;

    /// <summary>右ペインは AutoDuty と同じく見出しから始める（機能名と説明は左ペインのマウスオーバーで出る）。</summary>
    public bool ShowHeader => false;

    public bool Running => AnyActive;
    public bool BlocksRelay => AnyActive && reduction.Waiting;

    /// <summary>追加したあとに出す案内（霊砂。指定の文）。</summary>
    public const string SessionGuide =
        "設定した霊砂を入手したら、その霊砂を精選で得られる収集品だけがプリセットから無効化されます。追加で欲しい場合は手動で有効化するチェックを入れてください。\n" +
        "※他の霊砂が欲しい場合、「停止して管理リストを削除」ボタンを押してから、欲しい霊砂にチェックを入れて「Auto-Gatherに追加」ボタンを押してください。";

    /// <summary>追加したあとに出す案内（クリスタル。霊砂の文に合わせた）。</summary>
    public const string CrystalSessionGuide =
        "設定した属性のクリスタルとクラスターを両方入手したら、その属性を精選で得られる収集品だけがプリセットから無効化されます。追加で欲しい場合は手動で有効化するチェックを入れてください。\n" +
        "※他の属性が欲しい場合、「停止して管理リストを削除」ボタンを押してから、欲しい属性にチェックを入れて「Auto-Gatherに追加」ボタンを押してください。";

    private const string SandListName = "GBRHelper_霊砂";
    private const string CrystalListName = "GBRHelper_クリスタル";

    /// <summary>2026-10-05 以前のリストの名前。前回の復元が残っていたとき、こちらの名前のリストも消す。</summary>
    private const string OldListName = "GBRHelper_時限_優先と精選";

    private const string TagPrefix = "[GBRHelper:Timed]";

    /// <summary>画面に出す霊砂 1 種（Id は霊砂の品番）。Sources は原料（採れないものは Blocked に理由）。</summary>
    private sealed record SandRow(uint Id, string Name, IReadOnlyList<SourceRow> Sources)
    {
        public bool Gatherable => this.Sources.Any(s => s.Blocked is null);
    }

    /// <summary>画面に出す属性 1 つ（Key はその属性のクリスタルの品番）。</summary>
    private sealed record ElementRow(uint Key, string Name, uint CrystalId, uint ClusterId, IReadOnlyList<SourceRow> Sources)
    {
        public bool Gatherable => this.Sources.Any(s => s.Blocked is null);
    }

    private sealed record SourceRow(uint ItemId, string Name, int Level, GatherableCatalog.Job Job, uint UptimeHours, string? Blocked);

    private GatherableCatalog? catalog;
    private IReadOnlyList<AethersandRecipe> recipes = [];
    private IReadOnlyList<CrystalRecipe> crystalRecipes = [];
    private IReadOnlyList<SandRow> rows = [];
    private IReadOnlyList<ElementRow> elements = [];
    private string recipeError = "";
    private string crystalError = "";
    private bool sandOpen, crystalOpen;
    private readonly Dictionary<uint, int> pendingSand = [];
    private readonly Dictionary<uint, int> pendingCrystal = [];
    private Dictionary<uint, int> heldShown = [];
    private DateTime nextHeldRead;
    private readonly MaterialInventory inventory = new(lists);
    private readonly AllaganRetainerCounter retainers = new();

    // ---- 登録中の中身（霊砂・クリスタルそれぞれ。2026-10-06 から同時に登録できる） ----

    /// <summary>
    /// 登録 1 つ分（霊砂・クリスタルそれぞれ 1 つ）。リスト・目標・原料・無効にした原料・止める印は登録ごとに持つ。
    /// GBR の設定の書き換えと戻すための記録（Configuration.TimedRecoveryTag ほか）、精選の待ち（GBR の精選は鞄の中を全部精選するので 1 つ）、
    /// 状態の文は 2 つで共有する。
    /// </summary>
    private sealed class Run(bool crystal)
    {
        public readonly bool Crystal = crystal;
        public string ListName => this.Crystal ? CrystalListName : SandListName;
        public string KindName => this.Crystal ? "クリスタル・クラスター" : "霊砂";
        public bool Active;
        public bool StopRequested;
        public string StopReason = "停止しました。";
        public IReadOnlyList<TimedPlan.Goal> Goals = [];

        /// <summary>目標（Goal.ItemId）ごとに数える品。霊砂はその霊砂だけ、属性はクリスタルとクラスター（少ない方で判定）。</summary>
        public Dictionary<uint, uint[]> CountIds = [];

        public IReadOnlyList<AethersandRecipe> Recipes = [];
        public List<(uint ItemId, uint Quantity)> Written = [];

        /// <summary>このリストの原料（精選の前後で数える品）。</summary>
        public HashSet<uint> Sources = [];

        /// <summary>目標に達したので、こちらが無効にした原料。</summary>
        public readonly HashSet<uint> DisabledByUs = [];

        /// <summary>こちらが無効にしたあと、利用者が GBR で有効に戻した原料（もう無効にしない）。</summary>
        public readonly HashSet<uint> UserKept = [];

        /// <summary>まだ目標に届いていない物があるか（直近の見張りの結果。精選を頼むかに使う）。</summary>
        public bool Short;
    }

    private readonly Run sandRun = new(false);
    private readonly Run crystalRun = new(true);
    private Run[] Runs => [this.sandRun, this.crystalRun];
    private bool AnyActive => this.sandRun.Active || this.crystalRun.Active;

    /// <summary>登録中の全部の原料（精選できるか・精選の前後で数える品）。</summary>
    private HashSet<uint> ActiveSources() => Runs.Where(r => r.Active).SelectMany(r => r.Sources).ToHashSet();

    private IReadOnlyList<GatherableCatalog.Entry> eligible = [];
    private readonly ReductionWait reduction = new();
    private DateTime nextLoad, nextPoll, nextRowsRefresh;
    private RecoveryRetry recoveryRetry = new();
    private Dictionary<uint, ReductionStock>? beforeReduction;
    private object? autoInstance;
    private ulong character;
    private string status = "";

    private void Load()
    {
        nextLoad = DateTime.UtcNow.AddSeconds(5);
        catalog = builder.Build() ?? throw new InvalidOperationException(builder.LastError);
        character = Svc.PlayerState.ContentId;
        completion.InvalidateCharacter(character);
        try { recipes = access.LoadRecipes(catalog); recipeError = ""; }
        catch (Exception ex) { recipes = []; recipeError = "霊砂の対応表を読めません：" + ex.GetBaseException().Message; }
        try { crystalRecipes = access.LoadCrystalRecipes(catalog); crystalError = ""; }
        catch (Exception ex) { crystalRecipes = []; crystalError = "クリスタルの対応表を読めません：" + ex.GetBaseException().Message; }
        rows = BuildRows(catalog, recipes);
        elements = BuildElements(catalog, crystalRecipes);
        nextRowsRefresh = DateTime.UtcNow + GatheringCompletionReader.FolkloreRecheckInterval;
        nextHeldRead = default;
    }

    /// <summary>
    /// 行の「採れない理由」を作り直す（品の一覧と対応表は読み直さない）。伝承録を読んだ・レベルが上がったのを、
    /// 画面に数秒で出すため（2026-10-06。前は品の一覧を読んだときだけ作っていて、伝承録を読んでも「伝承録を読んでいません」のままだった）。
    /// 行は画面と登録の並びに使うだけで、選んだ霊砂・属性は設定（SandChoices・CrystalChoices）にあるので、作り直しても選択は変わらない。
    /// </summary>
    private void RefreshRows(GatherableCatalog cat)
    {
        nextRowsRefresh = DateTime.UtcNow + GatheringCompletionReader.FolkloreRecheckInterval;
        rows = BuildRows(cat, recipes);
        elements = BuildElements(cat, crystalRecipes);
    }

    private bool CanGather(GatherableCatalog.Entry e) => MaterialPlan.IsTimed(e) && !e.TreasureMap
        && e.Level <= MaterialInventory.Level(e.Job) && completion.FolkloreOk(e) == true;

    /// <summary>対応表のすべての霊砂を、画面に出す順（TimedPlan.SandOrder）に並べる。採れない原料には理由を付ける。</summary>
    private IReadOnlyList<SandRow> BuildRows(GatherableCatalog cat, IReadOnlyList<AethersandRecipe> list)
    {
        var entries = cat.Entries.ToLookup(e => e.ItemId);
        return TimedPlan.SandOrder(list, id => entries[id].Select(e => (int?)e.Level).Min())
            .Select(id => new SandRow(id, list.First(r => r.OutputId == id).Name,
                list.Where(r => r.OutputId == id).Select(r => r.SourceId).Distinct()
                    .Select(src => SourceOf(entries[src].ToArray(), false))
                    .OfType<SourceRow>()
                    .OrderBy(s => s.Level).ThenBy(s => s.ItemId).ToArray()))
            .ToArray();
    }

    /// <summary>属性を、クリスタルの品番の順（ファイア・アイス・ウィンド・アース・ライトニング・ウォーター）に並べる。</summary>
    private IReadOnlyList<ElementRow> BuildElements(GatherableCatalog cat, IReadOnlyList<CrystalRecipe> list)
    {
        var entries = cat.Entries.ToLookup(e => e.ItemId);
        return list.GroupBy(r => r.ElementKey).OrderBy(g => g.Key)
            .Select(g => new ElementRow(g.Key, g.First().ElementName, g.First().CrystalId, g.First().ClusterId,
                g.Select(r => r.SourceId).Distinct()
                    .Select(src => SourceOf(entries[src].ToArray(), true))
                    .OfType<SourceRow>()
                    .OrderBy(s => s.Level).ThenBy(s => s.ItemId).ToArray()))
            .ToArray();
    }

    private SourceRow? SourceOf(GatherableCatalog.Entry[] candidates, bool needUptime)
    {
        if (candidates.Length == 0)
            return null;
        var ok = candidates.FirstOrDefault(CanGather);
        var e = ok ?? candidates[0];
        var blocked = ok is null ? BlockedReason(e) : needUptime && e.UptimeHours == 0 ? "出る時刻を読めません" : null;
        return new SourceRow(e.ItemId, e.Name, e.Level, e.Job, e.UptimeHours, blocked);
    }

    private string BlockedReason(GatherableCatalog.Entry e)
    {
        var have = MaterialInventory.Level(e.Job);
        if (e.Level > have)
            return $"{JobName(e.Job)}のレベルが足りません（必要 Lv{e.Level}・いま Lv{have}）";
        return completion.FolkloreOk(e) switch
        {
            false => "伝承録を読んでいません",
            null => "伝承録を読んだか確かめられません",
            _ => "GBR の自動採集では採れません",
        };
    }

    private static string JobName(GatherableCatalog.Job job) => job == GatherableCatalog.Job.Miner ? "採掘師" : "園芸師";

    /// <summary>いまのキャラクターの選択と数。無ければ作る（ログイン前は保存しない入れ物を返す）。</summary>
    private SandChoice Choice(Dictionary<ulong, SandChoice> store)
    {
        var cid = Svc.PlayerState.ContentId;
        if (cid == 0)
            return new SandChoice();
        if (!store.TryGetValue(cid, out var choice))
            store[cid] = choice = new SandChoice();
        return choice;
    }

    private static int Quantity(SandChoice choice, uint id, int defaultQuantity)
        => Math.Clamp(choice.Quantities.GetValueOrDefault(id, defaultQuantity), MinSandQuantity, MaxSandQuantity);

    private static int SandQuantity(SandChoice choice, uint id) => Quantity(choice, id, DefaultSandQuantity);

    private static int CrystalQuantity(SandChoice choice, uint id) => Quantity(choice, id, DefaultCrystalQuantity);

    /// <summary>見出しの帯の色（要望：見えやすい、違う色）。霊砂は紫、クリスタル・クラスターは青。</summary>
    private static readonly System.Numerics.Vector4 SandHeaderColor = new(0.56f, 0.30f, 0.78f, 1f);
    private static readonly System.Numerics.Vector4 CrystalHeaderColor = new(0.16f, 0.48f, 0.80f, 1f);

    // ------------------------------------------------------------------
    // 画面

    public void DrawRight()
    {
        if (catalog is null && Svc.PlayerState.IsLoaded && DateTime.UtcNow >= nextLoad) Try(Load);
        else if (catalog is { } cat && DateTime.UtcNow >= nextRowsRefresh) Try(() => RefreshRows(cat));

        if (FoldingHeader.Draw("霊砂", ref sandOpen, SandHeaderColor))
            DrawSands();

        // 要望：霊砂の下に「クリスタル・クラスター」。
        if (FoldingHeader.Draw("クリスタル・クラスター", ref crystalOpen, CrystalHeaderColor))
            DrawCrystals();

        // 動いている間の状態・前回の復元・結果は、見出しを閉じていても分かるように見出しの外に出す。
        ImGui.Spacing();
        ImGui.Separator();
        // 霊砂とクリスタルは同時に登録できるので、止めるボタンは登録ごとに出す（どちらを止めるかが分かるように名前を前に置く。2026-10-06）。
        foreach (var run in Runs.Where(r => r.Active))
        {
            using var id = ImRaii.PushId(run.Crystal ? "stopCrystal" : "stopSand");
            ImGui.TextUnformatted($"{run.KindName}：");
            ImGui.SameLine();
            if (ImGui.Button("停止して管理リストを削除"))
                RequestUserStop(run);
        }

        if (!AnyActive && config.TimedRecoveryTag.Length != 0)
        {
            ImGui.TextWrapped("前回の管理リスト・GBR の設定の復元が残っています。GBR の自動採集を OFF にしてから押してください。");
            if (ImGui.Button("前回のリストを削除し設定を戻す")) Try(Recover);
        }

        if (status.Length != 0)
            ImGui.TextWrapped(status);

        // 追加したあとの案内（指定の文。「（原料○○品）」の文の下に改行して続ける）。登録しているものの分だけ出す。
        foreach (var run in Runs.Where(r => r.Active))
            ImGui.TextWrapped(run.Crystal ? CrystalSessionGuide : SessionGuide);
    }

    private void DrawSands()
    {
        // 指定の文。
        ImGui.TextUnformatted("欲しい霊砂にチェック＋下部の【Auto-Gatherに追加】ボタンをクリック");
        if (recipeError.Length != 0)
            ImGui.TextColored(ImGuiColors.DalamudYellow, recipeError);
        if (catalog is null)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "霊砂の一覧を読み込んでいます（ログインしていて、GBR が読み込まれていると読めます）");

        var choice = Choice(config.SandChoices);
        var column = ValueColumn(rows.Select(r => r.Name));

        // 縦 2 列。左の列を上から下へ、続きを右の列へ（GBR のリストもこの順＝左の列の上から）。
        var perColumn = (rows.Count + 1) / 2;
        // 霊砂を登録している間だけ止める（クリスタルの登録とは関係なく選べる・追加できる）。
        using (ImRaii.Disabled(sandRun.Active))
        using (var table = ImRaii.Table("##sands", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                for (var r = 0; r < perColumn; r++)
                {
                    ImGui.TableNextRow();
                    for (var c = 0; c < 2; c++)
                    {
                        var index = c * perColumn + r;
                        if (index >= rows.Count)
                            continue;
                        ImGui.TableSetColumnIndex(c);
                        var row = rows[index];
                        DrawChoice($"sand{row.Id}", row.Id, row.Name, row.Gatherable, SourceText(row.Name, "霊砂", row.Sources),
                            choice, pendingSand, column, () => HeldText(row.Id), DefaultSandQuantity);
                    }
                }
            }
        }

        ImGui.Spacing();
        // 説明は要望で 3 文だけにした（登録する文・変える GBR の設定・同時に登録できない）。押せない理由は一番上に黄色で出す。
        DrawAddButton("##addSand", () => { CommitAllPending(choice, pendingSand); Try(StartSand); }, AddBlock(false),
            $"チェックを入れた霊砂の原料（収集品）を、GBR の Auto-Gather にリスト「{SandListName}」として登録します。\n" +
            AddButtonNotes, false);
    }

    private void DrawCrystals()
    {
        ImGui.TextUnformatted("欲しい属性にチェック＋下部の【Auto-Gatherに追加】ボタンをクリック");
        if (crystalError.Length != 0)
            ImGui.TextColored(ImGuiColors.DalamudYellow, crystalError);
        if (catalog is null)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "一覧を読み込んでいます（ログインしていて、GBR が読み込まれていると読めます）");

        var choice = Choice(config.CrystalChoices);
        var column = ValueColumn(elements.Select(e => e.Name));

        // 2 列：左に ファイア・アイス・ウィンド、右に アース・ライトニング・ウォーター（クリスタルの品番の順で 3 つずつ）。
        var perColumn = (elements.Count + 1) / 2;
        // クリスタルを登録している間だけ止める（霊砂の登録とは関係なく選べる・追加できる）。
        using (ImRaii.Disabled(crystalRun.Active))
        using (var table = ImRaii.Table("##crystals", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                for (var r = 0; r < perColumn; r++)
                {
                    ImGui.TableNextRow();
                    for (var c = 0; c < 2; c++)
                    {
                        var index = c * perColumn + r;
                        if (index >= elements.Count)
                            continue;
                        ImGui.TableSetColumnIndex(c);
                        var e = elements[index];
                        DrawChoice($"crystal{e.Key}", e.Key, e.Name, e.Gatherable, SourceText(e.Name, "属性", e.Sources),
                            choice, pendingCrystal, column, () => CrystalHeldText(e), DefaultCrystalQuantity);
                    }
                }
            }
        }

        ImGui.Spacing();
        // 指定：ボタンは「ウィンドとウォーターの間の下辺り」＝ 2 列の真ん中に置く。
        DrawAddButton("##addCrystal", () => { CommitAllPending(choice, pendingCrystal); Try(StartCrystal); }, AddBlock(true),
            $"チェックを入れた属性の原料（精選するとクリスタル・クラスターになる刻限の収集品）を、GBR の Auto-Gather にリスト「{CrystalListName}」として登録します。\n" +
            AddButtonNotes, true);
    }

    /// <summary>
    /// 登録の間だけ変える GBR の設定（名前 → 値）。止めたら元に戻す。
    /// 「詳細→精選を使う（DoReduce）＋いつも全部精選する（AlwaysReduceAllItems）」「詳細→品目の並べ方（SortingMethod）→なし」（説明の文 AddButtonNotes にも同じことを書いている）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GbrSettings = new Dictionary<string, string>
    {
        ["SortingMethod"] = "None", ["DoReduce"] = "True", ["AlwaysReduceAllItems"] = "True",
    };

    /// <summary>
    /// 「Auto-Gatherに追加」の説明の 2 文目（指定の文。ほかの説明は要望で消した）。
    /// 「霊砂とクリスタルは同時には登録できません。…」の一文も、要望で消した。いまは霊砂とクリスタルを同時に登録できる。
    /// </summary>
    public const string AddButtonNotes =
        "GBRの次の設定を変更します「詳細→精選を使う＋いつも全部精選する」「詳細→品目の並べ方→なし」";

    // ---- 「Auto-Gatherに追加」を押せない理由 ----
    // ほかの Auto-Gather リストが有効でも押せる（追加したリストを一番上へ動かす）。

    /// <summary>
    /// 「Auto-Gatherに追加」を押せない理由（要望：説明の一番上に黄色で出す）。押せるなら null。
    /// 押したときにも同じことを確かめ直す（CheckBeforeStart）。ここは押す前に分かるようにするためのもの。
    /// </summary>
    private string? AddBlock(bool crystal)
    {
        // GBR の自動採集中は押せない。理由は指定の文を橙色で出す。
        // 追加すると GBR の設定（品目の並べ方→なし・精選・いつも全部精選する）を変え、一番上に入れたリストの上から順に採らせるため、
        // 自動採集の途中で行うと、動いている採集の並び順や精選が途中で変わる（危ない）。
        // 手で採っている最中（GBR は止まっている）は押せる（要望「自動採取中は出来なくていい、単純に採取中は設定出来ても問題ない」）。
        if (IsAutoGathering())
            return GatheringBlockText;
        // 同じ種類を登録している間だけ押せない（もう片方の登録とは関係なく押せる。2026-10-06）。
        var run = crystal ? crystalRun : sandRun;
        if (run.Active)
            return $"{run.KindName}の管理リストを登録中です。替えるときは「停止して管理リストを削除」を押してください";
        if (config.TimedRecoveryTag.Length != 0 && !AnyActive)
            return "前回の管理リスト・GBR の設定の復元が残っています（下の「前回のリストを削除し設定を戻す」を押してください）";
        if (Svc.PlayerState.ContentId == 0)
            return "ログインしていません";
        if (gbr.IsAutoGatherEnabled() is null)
            return "GatherBuddyReborn の状態を読めません（読み込まれていない可能性があります）";
        if (busy())
            return GatherProfileController.BusyText;
        if (catalog is null)
            return "一覧を読み込んでいません（「一覧を読み直す」を押してください）";
        if ((crystal ? crystalError : recipeError) is { Length: > 0 } error)
            return error;
        if (Choice(crystal ? config.CrystalChoices : config.SandChoices).Selected.Count == 0)
            return crystal ? "欲しい属性にチェックを入れてください" : "欲しい霊砂にチェックを入れてください";
        return null;
    }

    /// <summary>GBR の自動採集中に「Auto-Gatherに追加」に乗せたときの文（指定の文。橙色で出す）。</summary>
    public const string GatheringBlockText = GatherProfileController.AutoGatheringText;

    /// <summary>GBR の自動採集中か（手で採っているだけなら false）。</summary>
    private bool IsAutoGathering() => gbr.IsAutoGatherEnabled() == true;

    /// <summary>「Auto-Gatherに追加」ボタン。centered なら 2 列の真ん中に置く。押せない理由（block）があれば押せず、説明の一番上に出す（採集中は橙色・ほかは黄色）。</summary>
    private void DrawAddButton(string id, Action onClick, string? block, string tooltip, bool centered)
    {
        const string label = "Auto-Gatherに追加";
        if (centered)
        {
            var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
            var avail = ImGui.GetContentRegionAvail().X;
            if (avail > width)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - width) / 2);
        }

        using (ImRaii.Disabled(block is not null))
        {
            if (ImGui.Button(label + id))
                onClick();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UnvisitedFeature.ButtonTooltip(block, tooltip, block == GatheringBlockText ? ImGuiColors.DalamudOrange : null);

        ImGui.SameLine();
        using (ImRaii.Disabled(AnyActive))
        {
            if (ImGui.Button("一覧を読み直す" + id + "reload"))
                Try(Load);
        }
    }

    /// <summary>数の欄の位置（そのセルの左端から）。どの行でも数の欄が縦にそろうよう、一番長い名前に合わせる。</summary>
    private static float ValueColumn(IEnumerable<string> names)
    {
        var width = names.Select(n => ImGui.CalcTextSize(n).X).DefaultIfEmpty(0f).Max();
        return ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + width + 12 * ImGuiHelpers.GlobalScale;
    }

    /// <summary>1 行：チェック（名前）＋欲しい数の欄＋「個」。数の欄に乗せると鞄とリテイナーの数。</summary>
    private void DrawChoice(string scopeId, uint id, string name, bool gatherable, string tooltip,
        SandChoice choice, Dictionary<uint, int> pending, float column, Func<string> heldText, int defaultQuantity)
    {
        using var scope = ImRaii.PushId(scopeId);
        var check = choice.Selected.Contains(id);

        // 採れる原料が無いものは選べない（選んだまま採れなくなった物は、外せるように押せるままにする）。
        using (ImRaii.Disabled(!gatherable && !check))
        {
            if (ImGui.Checkbox(name, ref check))
            {
                CommitPending(id, choice, pending);
                if (check) choice.Selected.Add(id);
                else choice.Selected.Remove(id);
                config.Save();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);

        ImGui.SameLine(column);
        ImGui.SetNextItemWidth(88 * ImGuiHelpers.GlobalScale);
        var value = pending.TryGetValue(id, out var p) ? p : Quantity(choice, id, defaultQuantity);
        if (ImGui.InputInt("##quantity", ref value))
            pending[id] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            CommitPending(id, choice, pending);

        // 数の欄に乗せると「鞄：○○個／リテイナー：○○個」（「（いま○○個）」の表示は外した）。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(heldText());

        ImGui.SameLine();
        ImGui.TextUnformatted("個");
    }

    /// <summary>「鞄：○○個／リテイナー：○○個」。鞄は Tick で 1 秒ごと、リテイナーは乗せた品だけ Allagan Tools から読む。</summary>
    private string HeldText(uint itemId) => $"鞄：{BagCount(itemId)}／リテイナー：{RetainerCount(itemId)}";

    /// <summary>属性の「鞄：クリスタル○○個・クラスター○○個／リテイナー：…」。</summary>
    private string CrystalHeldText(ElementRow e)
        => $"鞄：クリスタル {BagCount(e.CrystalId)}・クラスター {BagCount(e.ClusterId)}\n" +
           $"リテイナー：クリスタル {RetainerCount(e.CrystalId)}・クラスター {RetainerCount(e.ClusterId)}";

    private string BagCount(uint itemId) => heldShown.TryGetValue(itemId, out var b) ? $"{b:N0}個" : "読み込み中";

    private string RetainerCount(uint itemId)
        => retainers.TryGet(itemId, out var r) ? r is { } n ? $"{n:N0}個" : "不明（Allagan Tools が要ります）" : "読み込み中";

    private static string SourceText(string name, string what, IReadOnlyList<SourceRow> sources)
    {
        var lines = sources.Select(s => $"・{JobName(s.Job)} Lv{s.Level} {s.Name}（{Windows(s.UptimeHours)}）" + (s.Blocked is null ? "" : $"［{s.Blocked}］"));
        return $"「{name}」の原料（精選するとこの{what}の品が出る刻限の収集品）：\n" + string.Join("\n", lines);
    }

    /// <summary>出る時刻（ET）を「4時・8時」の形で。不明なら「時刻不明」。</summary>
    private static string Windows(uint hours)
    {
        var slots = CrystalPlan.Slots(hours);
        if (slots == 0)
            return "時刻不明";
        return string.Join("・", Enumerable.Range(0, CrystalPlan.SlotCount).Where(s => (slots & (1 << s)) != 0).Select(s => $"ET{s * 4}時"));
    }

    /// <summary>打ち込んでいる途中の数を、範囲に収めて保存する（入力欄から離れたとき・チェックや追加を押す直前）。</summary>
    private void CommitPending(uint id, SandChoice choice, Dictionary<uint, int> pending)
    {
        if (!pending.Remove(id, out var value))
            return;
        choice.Quantities[id] = Math.Clamp(value, MinSandQuantity, MaxSandQuantity);
        config.Save();
    }

    private void CommitAllPending(SandChoice choice, Dictionary<uint, int> pending)
    {
        foreach (var id in pending.Keys.ToArray())
            CommitPending(id, choice, pending);
    }

    // ------------------------------------------------------------------
    // 登録

    /// <summary>登録の前に確かめること（両方共通）。もう片方が登録中でも始められる（2026-10-06）。</summary>
    private void CheckBeforeStart(Run run)
    {
        if (run.Active) throw new InvalidOperationException($"{run.KindName}の管理リストを登録中です");
        if (config.TimedRecoveryTag.Length != 0 && !AnyActive) throw new InvalidOperationException("前回の設定を復元してから開始してください");
        if (gbr.IsAutoGatherEnabled() != false || busy() || Svc.PlayerState.ContentId == 0)
            throw new InvalidOperationException("GBR の自動採集とほかの自動処理を止めてから追加してください");
        Load(); access.CheckContract();
        // GBR が止まっていれば、手で採っている最中などでも追加してよい。GBR の設定とリストを書くだけで、GBR は動いていないため。
        if (!access.CanWriteListsWhileStopped) throw new InvalidOperationException("キャラクターを読めません。少し待ってから押してください");
        _ = lists.ListAll() ?? throw new InvalidOperationException(lists.LastError);
        eligible = catalog!.Entries.Where(CanGather).DistinctBy(e => e.ItemId).ToArray();
    }

    private void StartSand()
    {
        CheckBeforeStart(sandRun);
        if (recipeError.Length != 0) throw new InvalidOperationException(recipeError);

        var choice = Choice(config.SandChoices);
        // 画面の並び（左の列の上から）の順に、選んだ霊砂を並べる。
        var picked = rows.Where(r => choice.Selected.Contains(r.Id)).ToArray();
        if (picked.Length == 0) throw new InvalidOperationException("欲しい霊砂にチェックを入れてください");
        // いま採れる原料が無い霊砂（レベル・伝承録）は飛ばして、採れる物だけ登録する。チェックは残すので、採れるようになれば次に押したときに入る
        // （2026-10-06。前は「チェックを外してください」と止めていた。要望「人間にアナログ的な指示を強いるな」）。
        var chosen = picked.Where(r => r.Gatherable).ToArray();
        if (chosen.Length == 0) throw new InvalidOperationException("選んだ霊砂は、どれも今は採れる原料がありません（名前に乗せると理由が出ます）");

        var sandGoals = chosen.Select(r => new TimedPlan.Goal(r.Id, (uint)SandQuantity(choice, r.Id))).ToArray();
        var sandRecipes = recipes.Where(r => chosen.Any(c => c.Id == r.OutputId) && eligible.Any(e => e.ItemId == r.SourceId)).ToArray();
        var counts = sandGoals.ToDictionary(g => g.ItemId, g => new[] { g.ItemId });
        var sourceIds = sandRecipes.Select(r => r.SourceId).ToHashSet();
        var held = ReadHeld(sandGoals, counts);
        var entries = TimedPlan.Build(eligible, sandGoals, sandRecipes, held, ReadSources(sourceIds));
        if (entries.Count == 0) throw new InvalidOperationException("選んだ霊砂はすべて目標数を持っています");

        Begin(sandRun, entries, sandGoals, sandRecipes, counts, sourceIds,
            $"Auto-Gather の一番上にリスト「{SandListName}」を追加しました（原料 {entries.Count} 品）。" + SkippedNote(picked.Where(r => !r.Gatherable).Select(r => r.Name)));
    }

    private void StartCrystal()
    {
        CheckBeforeStart(crystalRun);
        if (crystalError.Length != 0) throw new InvalidOperationException(crystalError);

        var choice = Choice(config.CrystalChoices);
        var picked = elements.Where(e => choice.Selected.Contains(e.Key)).ToArray();
        if (picked.Length == 0) throw new InvalidOperationException("欲しい属性にチェックを入れてください");
        // いま採れる原料が無い属性は飛ばして、採れる物だけ登録する（霊砂と同じ。2026-10-06）。
        var chosen = picked.Where(e => e.Gatherable).ToArray();
        if (chosen.Length == 0) throw new InvalidOperationException("選んだ属性は、どれも今は採れる原料がありません（名前に乗せると理由が出ます）");

        // クリスタルとクラスターが両方とも欲しい数に達したら達成＝少ない方で判定する。
        var allGoals = chosen.Select(e => new TimedPlan.Goal(e.Key, (uint)CrystalQuantity(choice, e.Key))).ToArray();
        var counts = chosen.ToDictionary(e => e.Key, e => new[] { e.CrystalId, e.ClusterId });
        var held = ReadHeld(allGoals, counts);
        var short_ = chosen.Where(e => held[e.Key] < CrystalQuantity(choice, e.Key)).ToArray();
        if (short_.Length == 0) throw new InvalidOperationException("選んだ属性はすべて目標数を持っています");

        // 原料を自動で選んで並べる（CrystalPlan）。候補はキャラが採れて、出る時刻が分かる刻限の収集品。
        var byId = eligible.Where(e => e.UptimeHours != 0).ToDictionary(e => e.ItemId);
        var candidates = crystalRecipes.Where(r => short_.Any(e => e.Key == r.ElementKey) && byId.ContainsKey(r.SourceId))
            .Select(r => new CrystalPlan.Candidate(r.SourceId, r.ElementKey, byId[r.SourceId].Level, CrystalPlan.Slots(byId[r.SourceId].UptimeHours)))
            .ToArray();
        var plan = CrystalPlan.Choose(short_.Select(e => e.Key).ToArray(), candidates);
        if (plan.Order.Count == 0) throw new InvalidOperationException("採れる原料がありません");
        // 1 つの原料が 2 つの属性に当たっても落ちないように（CrystalPlan はそういう組を使わない）。
        var elementOf = candidates.GroupBy(c => c.ItemId).ToDictionary(g => g.Key, g => g.First().ElementKey);
        var crystalRecipesForSession = plan.Order.Select(id => new AethersandRecipe(elementOf[id], chosen.First(e => e.Key == elementOf[id]).Name, id)).ToArray();
        var goalsShort = allGoals.Where(g => short_.Any(e => e.Key == g.ItemId)).ToArray();

        var sourceIds = plan.Order.ToHashSet();
        // 原料の目標数は 200＋鞄の通常品の数（GBR は通常品も数えるため。TimedPlan.SourceTarget）。
        var normal = ReductionInventory.NormalCounts(ReadSources(sourceIds), plan.Order);
        var entries = plan.Order.Select(id => (id, TimedPlan.SourceTarget(normal[id]))).ToList();

        // 並びと、各原料が取る枠（ET）を状態の文に出す（何をどの時間に採るかが分かるように）。
        var layout = string.Join(" → ", plan.Order.Select(id =>
        {
            var slots = string.Join("・", Enumerable.Range(0, CrystalPlan.SlotCount).Where(s => plan.SlotWinners[s] == id).Select(s => $"ET{s * 4}時"));
            var element = chosen.First(e => e.Key == elementOf[id]).Name;
            return $"{byId[id].Name}（{element}・{(slots.Length == 0 ? "取れる枠なし" : slots)}）";
        }));
        Begin(crystalRun, entries, goalsShort, crystalRecipesForSession, counts, sourceIds,
            $"Auto-Gather の一番上にリスト「{CrystalListName}」を追加しました（原料 {entries.Count} 品）。\n並び：{layout}"
            + SkippedNote(picked.Where(e => !e.Gatherable).Select(e => e.Name)));
    }

    /// <summary>採れる原料が無いので登録しなかった物の一言（無ければ空）。</summary>
    private static string SkippedNote(IEnumerable<string> names)
        => names.ToArray() is { Length: > 0 } n ? $"\n「{string.Join("」「", n)}」は今は採れる原料が無いので入れていません。" : "";

    /// <summary>
    /// 登録を始める（霊砂・クリスタル共通）。最初の登録なら、GBR の設定を変える前に、戻すための記録を保存する。
    /// 2 つ目の登録（もう片方が登録中）は、最初の登録の記録と GBR の設定をそのまま使い、リストを足すだけ（2026-10-06）。
    /// </summary>
    private void Begin(Run run, List<(uint ItemId, uint Quantity)> entries, IReadOnlyList<TimedPlan.Goal> sessionGoals,
        IReadOnlyList<AethersandRecipe> sessionRecipeList, Dictionary<uint, uint[]> counts, HashSet<uint> sourceIds, string startedText)
    {
        var first = !AnyActive;
        if (first)
        {
            // 保存してから設定変更。途中失敗・アンロード後にも利用者が復元できる。
            config.TimedRecoveryTag = $"{TagPrefix}[Character:{character}][Session:{Guid.NewGuid():N}]";
            config.TimedOriginalSettings = GbrSettings.Keys.ToDictionary(k => k, access.ReadSetting);
            config.TimedWrittenSettings = new(GbrSettings);
            config.Save();
        }
        try
        {
            run.Goals = sessionGoals; run.Recipes = sessionRecipeList; run.CountIds = counts; run.Sources = sourceIds;
            // 2 つ目の登録でも書き直す（1 つ目の登録中に GBR で設定を変えていても、押したら説明どおりの設定にする。
            // 書き直さないと、GBR を ON にした途端「GBR の設定が変更されたため停止します」で両方止まる）。戻す値は 1 つ目の前の値のまま。
            foreach (var setting in config.TimedWrittenSettings) access.SetSetting(setting.Key, setting.Value);
            Write(run, entries);
            if (first) { autoInstance = access.Auto; reduction.Cancel(); }
            run.Active = true; run.StopRequested = false; run.StopReason = "停止しました。"; run.Short = true;
            run.DisabledByUs.Clear(); run.UserKept.Clear();
            // 指定：この文の下に改行して案内（SessionGuide）を続ける。
            status = startedText;
        }
        catch (Exception startError)
        {
            try
            {
                if (first) Recover();
                // 2 つ目の登録の失敗では、最初の登録のリストと GBR の設定はそのまま残し、このリストだけ消す。
                else if (!lists.RemoveManagedList(run.ListName, config.TimedRecoveryTag)) throw new InvalidOperationException(lists.LastError);
            }
            catch (Exception recoveryError)
            {
                throw new InvalidOperationException($"開始失敗：{startError.GetBaseException().Message} / 復元も未完了：{recoveryError.GetBaseException().Message}");
            }
            throw;
        }
    }

    /// <summary>目標ごとの所持数（本人の鞄）。数える品が 2 つ（クリスタル・クラスター）なら少ない方。</summary>
    private Dictionary<uint, int> ReadHeld(IReadOnlyList<TimedPlan.Goal> goalList, Dictionary<uint, uint[]> counts)
    {
        var raw = inventory.Read(counts.Values.SelectMany(x => x).Distinct(), false)
            ?? throw new InvalidOperationException(inventory.Error);
        return goalList.ToDictionary(g => g.ItemId, g => counts[g.ItemId].Min(id => raw[id]));
    }

    private static Dictionary<uint, ReductionStock> ReadSources(IEnumerable<uint> ids)
        => GbrTimedAccess.CheckReductionInventory(ids.ToHashSet());

    private void Write(Run run, List<(uint ItemId, uint Quantity)> entries)
    {
        var result = lists.WriteManagedList(run.ListName, config.TimedRecoveryTag, entries, true, false);
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        run.Written = entries;
        // GBR は新しいリストを一番下に置くので、一番上へ動かす（ほかの有効なリストより先に原料を採らせる）。
        // もう片方が登録中なら、霊砂のリストを一番上・クリスタルのリストをその下に並べる（TimedRunRules.ListOrder）。
        var order = TimedRunRules.ListOrder(run == sandRun || sandRun.Active, run == crystalRun || crystalRun.Active, SandListName, CrystalListName);
        if (!lists.MoveManagedListsToTop(order, config.TimedRecoveryTag, out _)) throw new InvalidOperationException(lists.LastError);
        topMoveFailedFor = "";
    }

    /// <summary>画面に出す所持数（霊砂・クリスタル・クラスター）を、1 秒ごとに読み直す（鞄はゲームの更新の流れでしか読めないので Tick で読む）。</summary>
    private void RefreshHeldShown()
    {
        if (DateTime.UtcNow < nextHeldRead || rows.Count + elements.Count == 0) return;
        nextHeldRead = DateTime.UtcNow.AddSeconds(1);
        var ids = rows.Select(r => r.Id).Concat(elements.SelectMany(e => new[] { e.CrystalId, e.ClusterId }));
        heldShown = inventory.Read(ids, false) ?? [];
    }

    // ------------------------------------------------------------------
    // 見張り

    /// <summary>OFF は GBR の作業列を破棄する。リストを残したまま精選の待機だけを取り消す。</summary>
    public void OnGatherBuddyEnabledChanged(bool enabled)
    {
        if (!enabled) reduction.Cancel();
    }

    public void ResetCharacter()
    {
        foreach (var run in Runs) { run.Active = false; run.StopRequested = false; }
        reduction.Cancel(); autoInstance = null; character = 0;
        catalog = null; rows = []; elements = []; heldShown = [];
        pendingSand.Clear(); pendingCrystal.Clear(); retainers.Clear(); status = "";
        nextLoad = nextPoll = nextHeldRead = default; recoveryRetry = new();
    }

    public bool? ObserveGathering(bool? on, bool otherBusy)
    {
        if (on is { } known) OnGatherBuddyEnabledChanged(known);
        if (otherBusy) return null;
        return on ?? throw new InvalidOperationException("GBR の状態を読めません");
    }

    public void StartReduction(Func<Action, bool> start, DateTime now)
    {
        var complete = reduction.Begin(now);
        try
        {
            if (start(complete)) status = "精選しています";
            else reduction.Cancel();
        }
        catch { reduction.Cancel(); throw; }
    }

    public void Tick()
    {
        // マウスを乗せた品のリテイナーの数を読む（画面の表示だけ）。
        retainers.Tick();

        if (!AnyActive)
        {
            if (character != Svc.PlayerState.ContentId)
            {
                catalog = null; rows = []; elements = []; heldShown = []; pendingSand.Clear(); pendingCrystal.Clear(); retainers.Clear();
                character = Svc.PlayerState.ContentId;
            }

            RefreshHeldShown();
            return;
        }
        try
        {
            if (character != Svc.PlayerState.ContentId || !ReferenceEquals(autoInstance, access.TryGetAuto()))
            {
                foreach (var run in Runs) { run.Active = false; run.StopRequested = false; }
                reduction.Cancel(); autoInstance = null; status = "キャラクターまたは GBR が変わったため停止しました。GBR の自動採集を OFF にして、前回の復元を押してください。";
                return;
            }
            RefreshHeldShown();
            // 中断の観測は busy 判定より先。通知を受け取れなかった場合も OFF を見て待機を解除する。
            var observed = ObserveGathering(gbr.IsAutoGatherEnabled(), busy());
            if (observed is not { } on) return;

            // 止める登録（「停止して管理リストを削除」・見張りで問題が見つかった）。止めない方の登録は続ける（TimedRunRules.Stop）。
            foreach (var run in Runs.Where(r => r.Active && r.StopRequested).ToArray())
                if (!StopRun(run, on))
                    return;
            if (!AnyActive) return;

            // GBR の自動採集が止まっている間は見張らない。リストは残す（要望：止めるのは「停止して管理リストを削除」）。
            if (!on) return;
            if (DateTime.UtcNow < nextPoll) return;
            nextPoll = DateTime.UtcNow.AddMilliseconds(500);
            if (reduction.Waiting)
            {
                if (!reduction.Ready(DateTime.UtcNow, access.TaskBusy)) return;
                // 精選の前と同じ原料で比べる（途中で片方の登録を止めても、前後で同じ品を数える）。
                var after = ReadSources(beforeReduction!.Keys);
                if (!ReductionInventory.DidReduce(beforeReduction!, after))
                    throw new InvalidOperationException("精選後も原料が減っていません。精選設定・解放状態を確認してください");
                reduction.Cancel();
                status = "精選後の所持数を確かめました。";
            }
            if (!access.SafeBoundary) return;
            var snapshot = lists.ListAll() ?? throw new InvalidOperationException(lists.LastError);
            foreach (var setting in config.TimedWrittenSettings)
                if (access.ReadSetting(setting.Key) != setting.Value) throw new InvalidOperationException("GBR の設定が変更されたため停止します");
            KeepOnTop();

            // 登録ごとに見張る。片方で問題が見つかっても、もう片方は続ける。
            foreach (var run in Runs.Where(r => r.Active && !r.StopRequested))
            {
                try { Watch(run, snapshot); }
                catch (Exception ex) { RequestStop(run, ex.GetBaseException().Message); }
            }

            // 精選：足りない物があり、精選できる原料（収集品）を持っていて、GBR が次に採る品が未知・伝説でないとき（採集を先にする）。
            // 鞄を読めないときは、見張りは続けて精選だけ飛ばす（リストは残す）。通常品や、選んでいない精選できる品があっても止めない（2026-10-06）。
            // GBR の精選は鞄の中を全部精選するので、霊砂とクリスタルの原料をまとめて数える。
            if (Runs.Any(r => r.Active && !r.StopRequested && r.Short))
            {
                Dictionary<uint, ReductionStock> sources;
                try
                {
                    sources = ReadSources(ActiveSources());
                }
                catch (InvalidOperationException ex)
                {
                    status = "精選を止めています：" + ex.Message;
                    return;
                }

                if (ReductionInventory.CanReduce(sources) && !access.NextIsOnce())
                {
                    beforeReduction = sources;
                    StartReduction(access.StartReduction, DateTime.UtcNow);
                }
            }
        }
        catch (Exception ex)
        {
            // 両方に関わる問題（GBR の設定が変わった・鞄を読めない・精選で減らないなど）は、登録を全部止める。
            foreach (var run in Runs.Where(r => r.Active))
                RequestStop(run, ex.GetBaseException().Message);
        }
    }

    /// <summary>登録 1 つの見張り（採集の切れ目で）：リストが変えられていないか、目標に達した原料を無効にする／下回ったら有効に戻す。</summary>
    private void Watch(Run run, IReadOnlyList<GbrAutoGatherListAccess.ListSummary> snapshot)
    {
        var own = snapshot.SingleOrDefault(x => x.Name == run.ListName && x.Description.Contains(config.TimedRecoveryTag, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("管理リストが見つからないため停止します");
        // 品の有効・無効は利用者が変えてよい。品・数・予備リスト・リテイナー在庫の設定が変わったら止める。
        if (own.Fallback || own.UsesRetainerInventory || !own.Entries.SequenceEqual(run.Written))
            throw new InvalidOperationException("管理リストの品・数が変更されたため停止します");

        // こちらが無効にしたあと、利用者が GBR で有効に戻した原料は、もう無効にしない（指定「追加で欲しい場合は手動で有効化」）。
        foreach (var id in run.DisabledByUs.Where(id => !own.DisabledItems.Contains(id)).ToArray())
        {
            run.DisabledByUs.Remove(id);
            run.UserKept.Add(id);
        }

        var held = ReadHeld(run.Goals, run.CountIds);
        var needed = TimedPlan.NeededSources(run.Goals, run.Recipes, held);
        var turnedOff = new List<string>();
        var turnedOn = new List<string>();
        foreach (var (id, _) in run.Written)
        {
            var enabledNow = !own.DisabledItems.Contains(id);
            if (enabledNow && !needed.Contains(id) && !run.UserKept.Contains(id))
            {
                if (!lists.SetManagedItemEnabled(run.ListName, config.TimedRecoveryTag, id, false)) throw new InvalidOperationException(lists.LastError);
                run.DisabledByUs.Add(id);
                turnedOff.Add(SourceName(id));
            }
            else if (!enabledNow && needed.Contains(id) && run.DisabledByUs.Contains(id))
            {
                // 使うなどして目標を下回ったら、こちらが無効にした原料を有効に戻す。
                if (!lists.SetManagedItemEnabled(run.ListName, config.TimedRecoveryTag, id, true)) throw new InvalidOperationException(lists.LastError);
                run.DisabledByUs.Remove(id);
                turnedOn.Add(SourceName(id));
            }
        }

        if (turnedOff.Count > 0)
            status = $"目標数に達した{run.KindName}の原料を無効にしました：{string.Join("・", turnedOff)}";
        if (turnedOn.Count > 0)
            status = $"{run.KindName}が目標数を下回ったので、原料を有効に戻しました：{string.Join("・", turnedOn)}";
        if (run.Goals.All(g => held[g.ItemId] >= g.Target) && turnedOff.Count > 0)
            status += snapshot.Any(x => x.Enabled && !ReferenceEquals(x, own))
                ? $"\n選んだ{run.KindName}がすべて目標数に達しました（ほかの有効なリストの品は続けて採ります）。"
                : $"\n選んだ{run.KindName}がすべて目標数に達しました（GBR は採るものが無くなると自動採集を止めます）。";
        run.Short = run.Goals.Any(g => held[g.ItemId] < g.Target);
    }

    /// <summary>「停止して管理リストを削除」（その登録だけ）。</summary>
    private void RequestUserStop(Run run)
    {
        if (run.StopRequested) return;
        run.StopReason = $"{run.KindName}：利用者の操作で停止しました。";
        run.StopRequested = true;
    }

    /// <summary>見張りで問題が見つかった登録を止める印を付ける（最初の理由を残し、そのあとの失敗は「復元待ち」に足す）。</summary>
    private void RequestStop(Run run, string reason)
    {
        if (!run.StopRequested) run.StopReason = $"{run.KindName}：{reason}";
        status = run.StopRequested ? run.StopReason + " 復元待ち：" + reason : run.StopReason;
        run.StopRequested = true;
    }

    /// <summary>
    /// 登録を 1 つ止める（TimedRunRules.Stop）。止め終えたら true、待つ・失敗したら false（次の Tick で続ける）。
    /// ほかの登録が続くときは、GBR を止めず、設定も戻さず、このリストだけ消す（採集の切れ目で）。最後の登録なら、従来どおり
    /// GBR を止めてから管理リストを全部消し、GBR の設定を戻す。
    /// </summary>
    private bool StopRun(Run run, bool on)
    {
        if (!recoveryRetry.TryBegin(DateTime.UtcNow)) return false;
        var others = Runs.Where(r => r != run && r.Active).ToArray();
        try
        {
            switch (TimedRunRules.Stop(others.Length > 0, on, access.SafeBoundary))
            {
                case TimedRunRules.StopStep.Wait:
                    status = "採集・精選の切れ目で停止します";
                    return false;
                case TimedRunRules.StopStep.RemoveListOnly:
                    if (!lists.RemoveManagedList(run.ListName, config.TimedRecoveryTag)) throw new InvalidOperationException(lists.LastError);
                    run.Active = false; run.StopRequested = false;
                    status = $"{run.StopReason} {run.KindName}の管理リストを削除しました（{others[0].KindName}は続けます）。";
                    return true;
                case TimedRunRules.StopStep.StopGbr:
                    gbr.SetAutoGatherEnabled(false);
                    if (gbr.IsAutoGatherEnabled() != false) return false;
                    break;
            }

            Recover();
            run.Active = false; run.StopRequested = false; reduction.Cancel();
            status = run.StopReason + " 管理リストを削除し、GBR の設定を戻しました。";
            return true;
        }
        catch (Exception ex)
        {
            status = run.StopReason + " 復元待ち：" + ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>一番上へ戻すのに失敗した並び（同じ並びでは試し直さない。保存し直しを繰り返さないため。登録が増減したら試し直す）。</summary>
    private string topMoveFailedFor = "";

    /// <summary>
    /// 登録中、管理リストを Auto-Gather の一番上に保つ（GBR の画面で動かした・ほかのリストを上へ動かしたなど）。
    /// 霊砂とクリスタルの両方を登録しているときは、霊砂を一番上・クリスタルをその下に保つ（TimedRunRules.ListOrder）。
    /// 採集の切れ目でだけ呼ぶ（品の有効・無効を切り替えるのと同じ。GBR は切れ目ごとに採る順を決め直す）。
    /// 動かせなくても採集は続ける（並びがずれるだけで、原料は採れる）。
    /// </summary>
    private void KeepOnTop()
    {
        var order = TimedRunRules.ListOrder(sandRun.Active && !sandRun.StopRequested, crystalRun.Active && !crystalRun.StopRequested,
            SandListName, CrystalListName);
        if (order.Count == 0) return;
        var key = string.Join("|", order);
        if (topMoveFailedFor == key) return;
        if (!lists.MoveManagedListsToTop(order, config.TimedRecoveryTag, out var moved))
        {
            topMoveFailedFor = key;
            status = $"管理リストを Auto-Gather の一番上へ戻せません：{lists.LastError}";
            return;
        }
        if (moved)
            status = $"管理リスト「{string.Join("」「", order)}」を Auto-Gather の一番上へ戻しました";
    }

    /// <summary>原料の名前（状態の文に出す）。</summary>
    private string SourceName(uint id) => eligible.FirstOrDefault(e => e.ItemId == id)?.Name ?? id.ToString();

    private void Recover()
    {
        // GBR が止まっていれば、採集の切れ目は待たない（追加と同じ。手で採っている最中でも戻せる）。
        if (gbr.IsAutoGatherEnabled() != false || busy() || !access.CanWriteListsWhileStopped)
            throw new InvalidOperationException("GBR の自動採集とほかの自動処理を止めてから復元してください");
        if (config.TimedRecoveryTag.Length == 0) return;
        // メモリ上から消えた後の保存失敗も、削除操作を再試行して検証する。どの名前のリストも同じ印のものだけ消す。
        foreach (var name in new[] { SandListName, CrystalListName, OldListName })
            if (!lists.RemoveManagedList(name, config.TimedRecoveryTag)) throw new InvalidOperationException(lists.LastError);
        foreach (var setting in config.TimedOriginalSettings)
            if (config.TimedWrittenSettings.TryGetValue(setting.Key, out var writtenValue) && access.ReadSetting(setting.Key) == writtenValue)
                access.SetSetting(setting.Key, setting.Value); // 利用者による変更を上書きしない。
        config.TimedRecoveryTag = ""; config.TimedOriginalSettings.Clear(); config.TimedWrittenSettings.Clear(); config.Save();
        status = "前回のリストを削除し、GBR の設定を戻しました";
    }

    private void Try(Action action)
    {
        try { action(); } catch (Exception ex) { status = ex.GetBaseException().Message; }
    }
}
