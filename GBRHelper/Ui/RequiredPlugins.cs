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
    public sealed record Entry(string InternalName, string DisplayName, Need Need, string UsedBy, string IfMissing);

    /// <summary>
    /// 使うプラグインの全部（画面の並び）。InternalName は各プラグインの manifest の値（2026-10-05 導入済みの一覧で確認）。
    /// </summary>
    public static readonly IReadOnlyList<Entry> All =
    [
        new("GatherBuddyReborn", "GatherBuddy Reborn", Need.Required,
            "すべての機能",
            "何も動きません（GBRHelper は GBR の自動採集を補助するプラグインです）"),
        new("AutoRetainer", "AutoRetainer", Need.ForFeature,
            "ベンチャー回収",
            "ベンチャーが回収できるかを判断できず、回収もできません"),
        new("Lifestream", "Lifestream", Need.ForFeature,
            "ベンチャー回収",
            "回収のときに宿屋の部屋へ帰れません"),
        new("vnavmesh", "vnavmesh", Need.ForFeature,
            "ベンチャー回収",
            "宿屋の部屋の中で呼び鈴まで歩けません"),
        new("InventoryTools", "Allagan Tools", Need.ForFeature,
            "全素材の補充（希望所持数）・霊砂・クリスタル（リテイナーの数の表示）",
            "リテイナーの持ち数を数えられません（希望所持数の帯は作られず、数は「不明」と出ます）"),
        new("Artisan", "Artisan", Need.ForFeature,
            "Crafting Listsから末端素材抽出",
            "Crafting Lists を読めないので、この機能だけ使えません"),
    ];

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
