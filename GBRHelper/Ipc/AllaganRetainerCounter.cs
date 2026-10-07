using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Features;

namespace GBRHelper.Ipc;

/// <summary>
/// Allagan Tools から、今のキャラクターのリテイナー全員が持つ数を読む。
/// 使う所：霊砂・クリスタルの数の欄の「鞄／リテイナー」の表示、全素材の補充の「希望所持数」の吹き出しと GBR のリストの数。
/// 読めないとき（Allagan Tools が無い・まだ準備中・キャラクターが切り替わった直後）は「不明」（推測で 0 にしない。
/// 0 にすると、リテイナーにある分まで採ってしまうため）。
///
/// 【IPC】Allagan Tools 15.0.13 の IPCService.cs で確かめた（2026-10-07）：
///   ・AllaganTools.IsInitialized() → bool
///   ・AllaganTools.CurrentCharacter() → Allagan Tools が「今のキャラクター」と見ている番号（ゲームと違えば読まない）
///   ・AllaganTools.GetCharactersOwnedByActive(false) → 今のキャラクターが持つリテイナーなど（本人を除く）
///   ・AllaganTools.GetCharacterItems(番号) → その持ち主の持ち物の全部の枠（数値の並び。[2]＝品・[3]＝数・[20]＝持ち物の種類・[23]＝持ち主）
///   数えるのはリテイナーの 7 ページとクリスタル欄だけ（MaterialInventory.CountRetainerRows。装備・出品欄は数えない）。
/// 【重さ】リテイナー 1 人につき 1 回の呼び出しで全部の品を数える。
///   前は 1 品につき「リテイナーの数×8」回 ItemCount を呼んでいたが、ItemCount は呼ぶたびに全キャラクターの全所持品をなめる作りで
///   （同 IPCService.cs 102-105）、希望所持数のリストを見張るには重すぎたので、2026-10-07 にこの形へ替えた。数は同じ。
/// </summary>
public sealed class AllaganRetainerCounter
{
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

    /// <summary>ゲームの更新の流れで呼ぶ。画面で乗せている品のどれかが古ければ、リテイナーの持ち物をまとめて読み直す。</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (this.wanted.Any(id => !this.cache.TryGetValue(id, out var c) || now - c.At >= TimeSpan.FromSeconds(10)))
        {
            Dictionary<uint, int>? all = null;
            try
            {
                all = ReadAll();
                this.Error = "";
            }
            catch (Exception ex)
            {
                this.Error = ex.GetBaseException().Message;
            }

            foreach (var id in this.wanted)
                this.cache[id] = (all?.GetValueOrDefault(id), now);
        }

        this.wanted.Clear();
    }

    /// <summary>
    /// 今のキャラクターのリテイナー全員が持つ数を、品ごとにまとめて読む（持っていない品は入らない＝0 個）。
    /// 読めなければ例外（理由の文つき）。ゲームの更新の流れで呼ぶ。
    /// </summary>
    public static Dictionary<uint, int> ReadAll()
    {
        var pi = Svc.PluginInterface;
        return Count(
            () => pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized").InvokeFunc(),
            () => pi.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter").InvokeFunc(),
            () => pi.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive").InvokeFunc(false),
            owner => pi.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems").InvokeFunc(owner),
            Svc.PlayerState.IsLoaded ? Svc.PlayerState.ContentId : 0);
    }

    /// <summary>
    /// ReadAll の中身（IPC を引数で受け取る。試験では偽物を渡す）。
    /// character は今ログインしているキャラクター。Allagan Tools がまだ前のキャラクターを見ていれば読まない。
    /// </summary>
    public static Dictionary<uint, int> Count(Func<bool> initialized, Func<ulong> currentCharacter, Func<HashSet<ulong>> ownedByActive,
        Func<ulong, HashSet<ulong[]>> items, ulong character)
    {
        if (character == 0)
            throw new InvalidOperationException("キャラクター情報の読込みを待っています");
        if (!Call(initialized))
            throw new InvalidOperationException("Allagan Tools が準備できていません");
        if (Call(currentCharacter) != character)
            throw new InvalidOperationException("Allagan Tools がまだ今のキャラクターに切り替わっていません");

        var total = new Dictionary<uint, int>();
        foreach (var owner in Call(ownedByActive) ?? [])
            foreach (var (id, n) in MaterialInventory.CountRetainerRows(Call(() => items(owner)) ?? [], owner))
                total[id] = (int)Math.Min((long)total.GetValueOrDefault(id) + n, int.MaxValue);
        return total;
    }

    /// <summary>IPC を 1 つ呼ぶ（Allagan Tools が無い・古いなどで呼べなければ、理由の文をつけて例外）。</summary>
    private static T Call<T>(Func<T> ipc)
    {
        try
        {
            return ipc();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Allagan Tools から読めません：" + ex.GetBaseException().Message, ex);
        }
    }
}
