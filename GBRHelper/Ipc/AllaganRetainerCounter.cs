using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GBRHelper.Ipc;

/// <summary>
/// Allagan Tools から、今のキャラクターのリテイナー全員が持つ数を読む（2026-10-05 霊砂・クリスタルの数の欄の「鞄／リテイナー」の表示）。
/// 画面に出すだけで、採る数の判定には使わない。読めないとき（Allagan Tools が無い・リテイナーを開いていない）は「不明」。
///
/// 【IPC】実機で確かめた Allagan Tools の IPC をそのまま使う：
///   ・AllaganTools.IsInitialized() → bool
///   ・AllaganTools.GetCharactersOwnedByActive(false) → 今のキャラクターが持つキャラクター（リテイナーなど）の番号
///   ・AllaganTools.ItemCount(品, リテイナーの番号, 持ち物の種類) → uint（品質を問わない数）
///   持ち物の種類はリテイナーの 7 ページとクリスタル欄。リテイナー以外の番号が混じっても、リテイナーの種類で数えるので 0 になる。
/// 【重さ】1 品につきリテイナーの数×8 回の呼び出しになるので、画面でマウスを乗せた品だけ、10 秒に 1 回まで読む（Tick で読む）。
/// </summary>
public sealed class AllaganRetainerCounter
{
    private static readonly int[] RetainerTypes =
    [
        (int)InventoryType.RetainerPage1, (int)InventoryType.RetainerPage2, (int)InventoryType.RetainerPage3, (int)InventoryType.RetainerPage4,
        (int)InventoryType.RetainerPage5, (int)InventoryType.RetainerPage6, (int)InventoryType.RetainerPage7, (int)InventoryType.RetainerCrystals,
    ];

    private readonly Dictionary<uint, (int? Count, DateTime At)> cache = new();
    private readonly HashSet<uint> wanted = new();

    /// <summary>最後に読めなかった理由。</summary>
    public string Error { get; private set; } = "";

    /// <summary>
    /// 覚えている数（読めなかったら null）を返す。まだ読んでいなければ false。
    /// 呼ぶと「読みたい品」に入り、次の Tick で読む（画面で乗せている間、毎フレーム呼ばれる）。
    /// </summary>
    public bool TryGet(uint itemId, out int? count)
    {
        this.wanted.Add(itemId);
        if (this.cache.TryGetValue(itemId, out var c))
        {
            count = c.Count;
            return true;
        }

        count = null;
        return false;
    }

    /// <summary>キャラクターが変わったときに覚えを捨てる。</summary>
    public void Clear()
    {
        this.cache.Clear();
        this.wanted.Clear();
    }

    /// <summary>ゲームの更新の流れで呼ぶ。画面で乗せている品の数が古ければ読み直す。</summary>
    public void Tick()
    {
        foreach (var id in this.wanted)
        {
            if (this.cache.TryGetValue(id, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(10))
                continue;
            this.cache[id] = (this.Read(id), DateTime.UtcNow);
        }

        this.wanted.Clear();
    }

    /// <summary>
    /// いくつかの品のリテイナーの数をまとめて読む（全素材の補充の「希望所持数」。ゲームの更新の流れで呼ぶ）。
    /// 1 品でも読めなければ例外（推測で 0 にすると、リテイナーにある分まで採ってしまうため）。
    /// </summary>
    public static Dictionary<uint, int> ReadNow(IEnumerable<uint> itemIds)
    {
        var counter = new AllaganRetainerCounter();
        var result = new Dictionary<uint, int>();
        foreach (var id in itemIds)
            result[id] = counter.Read(id) ?? throw new InvalidOperationException("希望所持数にはリテイナーの数が要ります：" + counter.Error);
        return result;
    }

    private int? Read(uint itemId)
    {
        try
        {
            var pi = Svc.PluginInterface;
            if (!pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized").InvokeFunc())
            {
                this.Error = "Allagan Tools が準備できていません";
                return null;
            }

            var owned = pi.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive").InvokeFunc(false);
            var count = pi.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount");
            long total = 0;
            foreach (var retainer in owned)
                foreach (var type in RetainerTypes)
                    total += count.InvokeFunc(itemId, retainer, type);

            this.Error = "";
            return (int)Math.Min(total, int.MaxValue);
        }
        catch (Exception ex)
        {
            this.Error = "Allagan Tools から読めません：" + ex.GetBaseException().Message;
            return null;
        }
    }
}
