using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace GBRHelper.Ui;

/// <summary>
/// GBRHelper が使うほかのプラグインの一覧と、いま使えるかの判定（左の一覧の一番上「必要なプラグイン」）。
/// 要望「必要なプラグインが分かるように、分かりやすい場所へ表示」。
///
/// 画面にもゲームにも触らないので、ゲーム無しで試せる。導入済みの一覧（Dalamud の InstalledPlugins）は呼ぶ側が渡す。
/// 見分けは InternalName（表示名は変わることがあるため使わない）。
/// </summary>
public static class RequiredPlugins
{
    /// <summary>どのくらい要るか。</summary>
    public enum Need
    {
        /// <summary>無いと GBRHelper のどの機能も動かない。</summary>
        Required,

        /// <summary>その機能を使うときだけ要る。</summary>
        ForFeature,
    }

    /// <summary>1 つのプラグイン。</summary>
    /// <param name="InternalName">Dalamud の InternalName（見分けに使う）。</param>
    /// <param name="DisplayName">画面に出す名前。</param>
    /// <param name="Need">どのくらい要るか。</param>
    /// <param name="UsedBy">使う機能。</param>
    /// <param name="IfMissing">無いとどうなるか。</param>
    /// <param name="RepoUrls">
    /// 配布元（Dalamud の設定の「カスタムプラグインリポジトリ」に足す URL）。先頭が案内する URL、ほかは同じ中身の別の書き方。
    /// null は Dalamud の公式の配布（足さなくてよい）。
    /// </param>
    public sealed record Entry(string InternalName, string DisplayName, Need Need, string UsedBy, string IfMissing,
        IReadOnlyList<string>? RepoUrls = null);

    /// <summary>
    /// 使うプラグインの全部（画面の並び）。InternalName は各プラグインの manifest の値（2026-10-05 導入済みの一覧で確認）。
    /// DisplayName は配布元に載っている名前と同じ（インストールの画面をこの名前で検索する）。
    /// 配布元（RepoUrls）は 2026-10-08 に各配布元の一覧を読んで、その InternalName が載っていることを確かめた
    /// （Allagan Tools は Dalamud の公式の一覧 kamori.goats.dev に載っている）。配布元が変わったら直す。
    /// </summary>
    public static readonly IReadOnlyList<Entry> All =
    [
        new("GatherBuddyReborn", "GatherBuddy Reborn", Need.Required,
            "すべての機能",
            "何も動きません（GBRHelper は GBR の自動採集を補助するプラグインです）",
            ["https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"]),
        new("AutoRetainer", "AutoRetainer", Need.ForFeature,
            "ベンチャー回収",
            "ベンチャーが回収できるかを判断できず、回収もできません",
            ["https://love.puni.sh/ment.json", "https://puni.sh/api/plugins"]),
        new("Lifestream", "Lifestream", Need.ForFeature,
            "ベンチャー回収・ヤンサの山越え",
            "回収のときに宿屋の部屋へ帰れません・ヤンサの山越えでテレポできません",
            ["https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json",
             "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json"]),
        new("vnavmesh", "vnavmesh", Need.ForFeature,
            "ベンチャー回収・ヤンサの山越え",
            "宿屋の部屋の中で呼び鈴まで歩けません・ヤンサの山越えで経路を読めません",
            ["https://puni.sh/api/repository/veyn"]),
        new("InventoryTools", "Allagan Tools", Need.ForFeature,
            "全素材の補充（希望所持数）・霊砂・クリスタル（リテイナーの数の表示）",
            "リテイナーの持ち数を数えられません（希望所持数の帯は作られず、数は「不明」と出ます）"),
        new("Artisan", "Artisan", Need.ForFeature,
            "Crafting Listsから末端素材抽出",
            "Crafting Lists を読めないので、この機能だけ使えません",
            ["https://love.puni.sh/ment.json", "https://puni.sh/api/plugins"]),
    ];

    /// <summary>配布元の状態（足りないプラグインの「配布元を追加」を出すかの判断）。</summary>
    public enum RepoStatus
    {
        /// <summary>Dalamud の公式の配布（足さなくてよい）。</summary>
        Official,

        /// <summary>カスタムプラグインリポジトリに登録済みで有効。</summary>
        Added,

        /// <summary>登録済みだが無効。</summary>
        Disabled,

        /// <summary>登録されていない。</summary>
        Missing,

        /// <summary>Dalamud の設定を読めない（登録されているか分からない）。</summary>
        Unknown,
    }

    /// <summary>
    /// そのプラグインの配布元が、Dalamud の設定のカスタムプラグインリポジトリに入っているか。
    /// repos は登録済みの配布元（ReadRepos）。null は読めなかった。同じ中身の別の URL のどれかが有効なら登録済み。
    /// </summary>
    public static RepoStatus RepoState(Entry entry, IReadOnlyList<(string Url, bool Enabled)>? repos)
    {
        if (entry.RepoUrls is not { Count: > 0 } urls)
            return RepoStatus.Official;
        if (repos is null)
            return RepoStatus.Unknown;
        var found = repos.Where(r => urls.Any(u => SameUrl(u, r.Url))).ToList();
        if (found.Any(r => r.Enabled))
            return RepoStatus.Added;
        return found.Count > 0 ? RepoStatus.Disabled : RepoStatus.Missing;
    }

    /// <summary>URL が同じか（前後の空白・末尾の「/」・大文字小文字を見ない）。どちらかが null なら違う。</summary>
    public static bool SameUrl(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // ワンクリックのインストール（Dalamud の公式の配布（Allagan Tools）も扱う。Dalamud の内部に触る部分は Ipc\PluginInstaller.cs）

    /// <summary>Dalamud が配布元を読み込んだ状態。</summary>
    public enum RepoLoad
    {
        /// <summary>Dalamud の配布元の一覧に入っていない（設定を読み直していない）。</summary>
        NotListed,

        /// <summary>読み込み中・まだ読んでいない。</summary>
        Loading,

        /// <summary>読み込めた。</summary>
        Loaded,

        /// <summary>読み込めなかった（回線・配布元の停止など）。</summary>
        Failed,
    }

    /// <summary>
    /// Dalamud の配布元の一覧（PluginManager.Repos の PluginMasterUrl・State・IsThirdParty）から、そのプラグインの配布元の読み込み状態を決める。
    /// State は Dalamud の PluginRepositoryState の名前（Unknown・InProgress・Success・Fail。Dalamud の PluginRepositoryState.cs）。
    /// 公式の配布（RepoUrls が null）は、第三者の配布元でないもの（Official）を見る。同じ中身の別の URL が 2 つあれば、どちらかが読めていれば読めた。
    /// </summary>
    public static RepoLoad RepoLoadState(Entry entry, IEnumerable<(string? Url, string? State, bool Official)> repos)
    {
        var states = repos.Where(r => entry.RepoUrls is { Count: > 0 } urls ? urls.Any(u => SameUrl(u, r.Url)) : r.Official)
            .Select(r => r.State).ToList();
        if (states.Count == 0)
            return RepoLoad.NotListed;
        if (states.Any(s => s == "Success"))
            return RepoLoad.Loaded;
        return states.All(s => s == "Fail") ? RepoLoad.Failed : RepoLoad.Loading;
    }

    /// <summary>配布一覧の 1 件（Dalamud の AvailablePlugins の manifest から読んだもの）。Official は公式の配布から来たか。</summary>
    public sealed record Candidate(string? InternalName, string? RepoUrl, int ApiLevel, bool Official = false);

    /// <summary>
    /// 配布一覧から、入れる版を選ぶ。選べた位置（無ければ -1）と、選べなかった理由を返す。
    /// 【決めた配布元から来た版だけを選ぶ】配布元を多く登録していると、同じ InternalName の別の人の再配布
    /// （言語版の改造など）も載っていることがある。名前が最初に合ったものを入れると、意図しない版を入れるおそれがある。公式の配布の品は公式から来た版だけ。
    /// 【今の Dalamud で読み込める版だけを選ぶ】Dalamud の配布一覧には、API が 1 つ古い版も「古い」と表示するために残っている
    /// （PluginManager.IsManifestEligible）。入れても読み込めないので選ばない。
    /// </summary>
    public static (int Index, string? Why) Pick(Entry entry, IReadOnlyList<Candidate> candidates, int apiLevel)
    {
        var sameName = Enumerable.Range(0, candidates.Count)
            .Where(i => string.Equals(candidates[i].InternalName, entry.InternalName, StringComparison.Ordinal))
            .ToList();
        var fromRepo = sameName.Where(i => entry.RepoUrls is { Count: > 0 } urls
            ? urls.Any(u => SameUrl(u, candidates[i].RepoUrl))
            : candidates[i].Official).ToList();
        var usable = fromRepo.Where(i => candidates[i].ApiLevel >= apiLevel).ToList();
        if (usable.Count > 0)
            return (usable[0], null);

        var where = RepoName(entry);
        if (fromRepo.Count > 0)
            return (-1, $"{where}の {entry.DisplayName} は API {fromRepo.Max(i => candidates[i].ApiLevel)} 向けで、今の Dalamud（API {apiLevel}）では読み込めません。配布元の更新を待ってください");
        if (sameName.Count > 0)
            return (-1, $"{entry.DisplayName} が{where}の一覧にありません。ほかの配布元にある同じ名前の版は、別の人の再配布のおそれがあるので入れません");
        return (-1, $"{entry.DisplayName} が{where}の一覧にありません");
    }

    /// <summary>配布元の呼び方（文に入れる）。</summary>
    public static string RepoName(Entry entry)
        => entry.RepoUrls is { Count: > 0 } urls ? $"配布元（{urls[0]}）" : "Dalamud の公式の配布";

    /// <summary>いまの状態。</summary>
    public enum Status
    {
        /// <summary>導入されていて、読み込まれている（使える）。</summary>
        Loaded,

        /// <summary>導入されているが、読み込まれていない（Dalamud の一覧で無効・読み込みに失敗など）。</summary>
        NotLoaded,

        /// <summary>導入されていない。</summary>
        Missing,
    }

    /// <summary>1 つのプラグインの状態。Version は導入されていれば版（無ければ null）。</summary>
    public sealed record State(Entry Entry, Status Status, string? Version);

    /// <summary>
    /// 導入済みの一覧から、全部のプラグインの状態を作る。
    /// 同じ InternalName が 2 つある（開発版と配布版を両方入れている）ときは、読み込まれている方を使う。
    /// </summary>
    /// <param name="installed">Dalamud の導入済みの一覧（InternalName・読み込まれているか・版）。</param>
    public static List<State> Check(IEnumerable<(string? InternalName, bool IsLoaded, string? Version)> installed)
    {
        var list = installed.Where(p => !string.IsNullOrEmpty(p.InternalName)).ToList();
        return All.Select(entry =>
        {
            var same = list.Where(p => string.Equals(p.InternalName, entry.InternalName, StringComparison.Ordinal)).ToList();
            if (same.Count == 0)
                return new State(entry, Status.Missing, null);

            var loaded = same.Where(p => p.IsLoaded).ToList();
            return loaded.Count > 0
                ? new State(entry, Status.Loaded, loaded[0].Version)
                : new State(entry, Status.NotLoaded, same[0].Version);
        }).ToList();
    }

    /// <summary>
    /// Dalamud の導入済みの一覧から、内部名・読み込まれているか・版を読む。
    ///
    /// 【1 つ読めなくても、ほかは読む】
    /// Dalamud は版（Version）に manifest の AssemblyVersion をそのまま返すので、manifest に版の無いプラグイン
    /// （開発用のプラグインなど）があると null になる（Dalamud Plugin/Internal/Types/LocalPlugin.cs の EffectiveVersion）。
    /// 以前はこれで一覧を読む処理全体が落ち、「Object reference not set to an instance of an object.」と出ていた。
    /// 版は null のまま扱い、ほかの値を読むときに例外が出たプラグインは飛ばす。
    /// </summary>
    public static List<(string? InternalName, bool IsLoaded, string? Version)> Read(IEnumerable<IExposedPlugin> installed)
    {
        var list = new List<(string? InternalName, bool IsLoaded, string? Version)>();
        foreach (var p in installed)
        {
            try
            {
                if (p is null)
                    continue;
                string? version = null;
                try { version = p.Version?.ToString(); }
                catch (Exception) { /* 版だけ読めない：版なしとして扱う */ }
                list.Add((p.InternalName, p.IsLoaded, version));
            }
            catch (Exception)
            {
                // このプラグインは読めない。ほかのプラグインは読み続ける。
            }
        }

        return list;
    }

    /// <summary>
    /// そのプラグインのメイン画面を開く（開いていれば閉じる。GBR は開閉の切り替えを登録している：GatherBuddy.cs の UiBuilder.OpenMainUi）。
    /// Dalamud の公式の口（IExposedPlugin.OpenMainUi）を使うので、相手のコマンドの文字を決め打ちしない。
    /// 開けたら null、開けなければ理由。
    /// </summary>
    public static string? OpenMainUi(IEnumerable<IExposedPlugin> installed, string internalName, string displayName)
    {
        IExposedPlugin? target = null;
        try
        {
            target = installed.FirstOrDefault(p => p is not null && p.InternalName == internalName && p.IsLoaded);
        }
        catch (Exception ex)
        {
            return $"導入済みのプラグインの一覧を読めません：{ex.GetBaseException().Message}";
        }

        if (target is null)
            return $"{displayName} が読み込まれていません";
        if (!target.HasMainUi)
            return $"{displayName} の画面を開く口がありません";

        try
        {
            target.OpenMainUi();
            return null;
        }
        catch (Exception ex)
        {
            return $"{displayName} の画面を開けません：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>使えないプラグインの数（導入されていない・読み込まれていない）。</summary>
    public static int Unavailable(IEnumerable<State> states) => states.Count(s => s.Status != Status.Loaded);

    /// <summary>使えない「必須」のプラグインがあるか（あれば左の一覧で赤く出す）。</summary>
    public static bool RequiredUnavailable(IEnumerable<State> states)
        => states.Any(s => s.Entry.Need == Need.Required && s.Status != Status.Loaded);

    /// <summary>左の一覧の項目名（足りなければ数を添える）。</summary>
    public static string LeftLabel(int unavailable)
        => unavailable == 0 ? "必要なプラグイン" : $"必要なプラグイン（不足 {unavailable}）";

    /// <summary>状態の文。</summary>
    public static string StatusText(State state) => state.Status switch
    {
        Status.Loaded => state.Version is { Length: > 0 } v ? $"導入済み（{v}）" : "導入済み",
        Status.NotLoaded => "導入済み・停止中",
        _ => "見つかりません",
    };
}
