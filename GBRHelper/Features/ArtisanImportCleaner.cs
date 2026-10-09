using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>
/// GBR 自身の「Artisan から読み込む」で作られたリストから、シャード・クリスタル・クラスターを外す
/// （要望「Crafting Lists から末端素材を GBR のプリセットに抽出する機能で、シャード・クリスタル・クラスターは抽出しない」）。
///
/// 【なぜ要るか】GBRHelper の「Crafting Listsから末端素材抽出」は 0.3.0.12 からクリスタル類を入れないが、GBR の自動採集タブの
///   「Artisan から読み込む」（GBR 本体の機能。GBRHelper の画面でも「こちらからでも取り込める」と案内している）は、Artisan の ListMaterials が返す
///   クリスタル類（レシピの材料のクリスタルの枠。Artisan HelperExtensions.cs:142-155）を、採れる品としてそのまま入れる（GBR Reflection.cs:89-104）。
///   例：材料に 6 属性のクリスタルがある Crafting List を GBR で取り込むと、6 品ともリストに入る（2026-10-09 にゲームデータで材料を数えて確かめた）。
/// 【決まり】GBR の取り込みのリスト（説明が "Imported from Artisan"）が新しくできたら、GBR が保存し終えたのを確かめてから（保存ファイルに、
///   同じ名前の取り込みのリストがメモリと同じ数ある）、クリスタル類を GBR の RemoveItem で消す。1 つのリストは 1 回だけ見る。
///   GBRHelper を読み込んだとき（GBR を読み直したときも）にもうあったリストは触らない（前からのリストや、あとから手で足したクリスタル類は消さない）。
/// ゲームの更新の流れ（ArtisanFeature.Tick）から呼ぶ。GBR の画面で品を消すのと同じスレッド。
/// </summary>
public sealed class ArtisanImportCleaner(IGbrArtisanImports lists, Func<uint, bool> isCrystal)
{
    /// <summary>リストを見に行く間隔（毎フレームは読まない）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>見たリスト（参照で見分ける）。</summary>
    private readonly HashSet<object> seen = new(ReferenceEqualityComparer.Instance);

    /// <summary>リストを見た GBR の管理の部品（替わったら GBR を読み直した＝いまあるリストを見たことにし直す）。</summary>
    private object? manager;

    private DateTime next;

    /// <summary>直近の結果（画面に出す）。何もしていなければ空。</summary>
    public string LastResult { get; private set; } = string.Empty;

    /// <summary>
    /// 新しくできた取り込みのリストを見て、クリスタル類があれば外す。外した・外せなかったときはその文（記録に書く）を返す。何もなければ null。
    /// </summary>
    public string? Tick(DateTime now)
    {
        if (now < this.next)
            return null;
        this.next = now + Interval;

        var found = lists.ArtisanImportedLists(out var mgr);
        if (found is null || mgr is null)
            return null;

        if (!ReferenceEquals(mgr, this.manager))
        {
            // 初めて読めたとき・GBR を読み直したとき：いまあるリストは見たことにする（触らない）。
            this.manager = mgr;
            this.seen.Clear();
            foreach (var l in found)
                this.seen.Add(l.Handle);
            return null;
        }

        string? message = null;
        foreach (var l in found)
        {
            if (this.seen.Contains(l.Handle))
                continue;

            // GBR の取り込みは別のスレッドでリストを足して保存する。保存し終えてから触る（同時に保存しないため）。
            var inMemory = found.Count(x => x.Name == l.Name);
            if (lists.SavedArtisanImportCount(l.Name) is not { } saved || saved < inMemory)
                continue;

            this.seen.Add(l.Handle);
            if (!l.Items.Any(isCrystal))
                continue;

            var removed = lists.RemoveItems(l.Handle, isCrystal);
            message = removed is null
                ? $"GBR の「Artisan から読み込む」で作った「{l.Name}」から、シャード・クリスタル・クラスターを外せませんでした：{lists.LastError}"
                : $"GBR の「Artisan から読み込む」で作った「{l.Name}」から、シャード・クリスタル・クラスター {removed.Count} 品を外しました";
            this.LastResult = $"{DateTime.Now:HH:mm} {message}";
        }

        return message;
    }
}
