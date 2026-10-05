using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>
/// Crafting Lists の素材（品番）が、いまのキャラクターで伝承録の点から採れるかを決める。
///
/// 確認「伝承録を読んでいない品は、自動的にプリセットから外れるか」：
/// 解放採取・全素材の補充・霊砂・クリスタルは、品の一覧（LiveCatalogBuilder）と GatheringCompletionReader.FolkloreOk で外していたが、
/// Crafting Lists（GBR の「Artisan から読み込む」と同じ中身）は外していなかった。GBR の自動採集は伝承録を見ないので
/// （伝説の採集点の前で待ち続ける。GatheringCompletionReader.FolkloreOk の説明）、ここで外す。
/// レベルは GBR が自分で外す（GBR AutoGather/Lists/ActiveItemList.cs 340-344：採集点のレベルが本人のレベルを超えると対象から外す）。
///
/// 採集品は品の一覧の項目の伝承録（採集点の GatheringSubCategory の本）、魚は FishParameter の GatheringSubCategory の本で見る
/// （GBR も同じ所から本を引いている：GatherBuddy.GameData Node.Base.cs・Fish.cs）。
/// </summary>
public sealed class ItemFolklore(LiveCatalogBuilder builder, GatheringCompletionReader reader)
{
    private Dictionary<uint, GatherableCatalog.Entry[]> gatherables = new();
    private Dictionary<uint, uint[]>? fishBooks;

    /// <summary>Prepare で一覧を作れなかった理由。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 押すたびに呼ぶ。伝承録の覚えを捨て（あとで読んだのを反映する）、品の一覧を作り直す。作れなければ false（理由は LastError）。
    /// 伝承録を読んだかはゲームの更新の流れでしか読めないので、Tick から呼ぶ。
    /// </summary>
    public bool Prepare()
    {
        reader.InvalidateFolklore();
        var catalog = builder.Build();
        if (catalog is null)
        {
            this.LastError = "採取品の一覧を作れません：" + builder.LastError;
            return false;
        }

        this.gatherables = catalog.Entries.GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.ToArray());
        try
        {
            this.fishBooks ??= LoadFishBooks();
        }
        catch (Exception ex)
        {
            this.LastError = "魚の伝承録をゲームデータから読めません：" + ex.GetBaseException().Message;
            return false;
        }

        return true;
    }

    /// <summary>true＝採れる（伝承録が要らない・読んでいる）、false＝読んでいない伝承録がある、null＝確かめられない。</summary>
    public bool? Ok(uint itemId)
    {
        if (this.gatherables.TryGetValue(itemId, out var entries))
            return Combine(entries.Select(reader.FolkloreOk));
        if (this.fishBooks is not null && this.fishBooks.TryGetValue(itemId, out var books))
            return reader.BooksRead(books);
        return true; // 伝承録の要る採集品・魚ではない
    }

    /// <summary>
    /// 同じ品の項目（採掘と園芸など）ごとの結果をまとめる。1 つでも読んでいなければ false、分からないものがあれば null。
    /// GBR がどちらの採集点を選ぶかは分からないので、全部が採れるときだけ true にする。
    /// </summary>
    public static bool? Combine(IEnumerable<bool?> results)
    {
        var unknown = false;
        foreach (var r in results)
        {
            if (r == false)
                return false;
            if (r is null)
                unknown = true;
        }

        return unknown ? null : true;
    }

    /// <summary>素材を、入れる品・伝承録を読んでいない品・確かめられない品に分ける（並びは保つ）。</summary>
    public static (List<(uint ItemId, uint Quantity)> Keep, List<uint> NotRead, List<uint> Unknown) Filter(
        IReadOnlyList<(uint ItemId, uint Quantity)> entries, Func<uint, bool?> ok)
    {
        var keep = new List<(uint ItemId, uint Quantity)>();
        var notRead = new List<uint>();
        var unknown = new List<uint>();
        foreach (var e in entries)
        {
            switch (ok(e.ItemId))
            {
                case true: keep.Add(e); break;
                case false: notRead.Add(e.ItemId); break;
                default: unknown.Add(e.ItemId); break;
            }
        }

        return (keep, notRead, unknown);
    }

    /// <summary>魚の品番 → 要る伝承録の本の品番（FishParameter の GatheringSubCategory の Item）。本の要らない魚は入れない。</summary>
    public static Dictionary<uint, uint[]> LoadFishBooks()
    {
        var map = new Dictionary<uint, HashSet<uint>>();
        foreach (var fish in Svc.Data.GetExcelSheet<FishParameter>())
        {
            var item = fish.Item.RowId;
            var book = fish.GatheringSubCategory.ValueNullable?.Item.RowId ?? 0;
            if (item == 0 || book == 0)
                continue;
            if (!map.TryGetValue(item, out var books))
                map[item] = books = new HashSet<uint>();
            books.Add(book);
        }

        return map.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }
}
