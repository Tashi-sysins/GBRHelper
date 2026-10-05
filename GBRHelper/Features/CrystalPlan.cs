using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GBRHelper.Features;

/// <summary>
/// クリスタル・クラスター：精選するとクリスタル・クラスターが出る刻限の原料を、属性ごとに選んで並べる。ゲームに触らない純粋な部品。
/// 決定「ゲームデータから自動で選ぶ」（添付のプリセット＝手で選んだ 6 品の代わりに、キャラが採れる品から選ぶ）。
///
/// 【GBR の採り方】並べ替え（Item Sorting Method）を None にすると、リストの上から「いま出ている品」を採る
///   （GBR の ActiveItemList.cs 389-391）。刻限の採集点はエオルゼア時間の 4 時間ごとの枠（0・4・8・12・16・20 時）に出て、
///   品によって 1〜5 枠に出る。同じ枠に 2 品出ていると上の品だけを採る（下の品はその枠では採らない）。
/// 【選び方】属性ごとに原料を 1 品選び、並びも決める。並べたときに各枠で「一番上の出ている品」が勝つとして、
///   1. 選んだ属性が全部 1 枠以上取れる  2. 空いている枠が少ない  3. 一番少ない属性の枠数が多い（偏らない）
///   4. 一番多い属性の枠数が少ない  5. 原料のレベルの合計が低い  の順に良い組と並びを選ぶ。
///   属性が少なく空き枠が残るときは、空き枠に出る同じ属性群の別の原料をリストの下に足す（上の品から枠を奪わない）。
///   参考（2026-10-02 の調べ・記憶 reference_reduction_crystal_rotation）：紅蓮 Lv70・漆黒 Lv80 には 1 枠 1 属性で重ならない 6 品の組がある。
///   手で選んだ例（蒼天の 6 品）の並びも、この評価で「6 属性が 1 枠ずつ」になる（試験で確かめている）。
/// </summary>
public static class CrystalPlan
{
    /// <summary>4 時間ごとの枠の数（0・4・8・12・16・20 時）。</summary>
    public const int SlotCount = 6;

    /// <summary>原料の候補。Slots は出る枠のビット（Slots(UptimeHours)）。</summary>
    public sealed record Candidate(uint ItemId, uint ElementKey, int Level, byte Slots);

    /// <summary>選んだ並び（上から）と、枠ごとに勝つ原料（無ければ 0）。</summary>
    public sealed record Result(IReadOnlyList<uint> Order, IReadOnlyList<uint> SlotWinners)
    {
        /// <summary>属性ごとに取れた枠の数。</summary>
        public Dictionary<uint, int> Wins(IReadOnlyDictionary<uint, uint> elementOf)
        {
            var wins = new Dictionary<uint, int>();
            foreach (var w in this.SlotWinners.Where(w => w != 0))
                wins[elementOf[w]] = wins.GetValueOrDefault(elementOf[w]) + 1;
            return wins;
        }
    }

    /// <summary>
    /// 品の名前から属性を読む（「ファイアクリスタル」→ ファイア・クリスタル、「ファイアクラスター」→ ファイア・クラスター）。
    /// シャードや属性の無い品は null。英語版の「Fire Crystal」「Fire Cluster」も読む。
    /// </summary>
    public static (string Name, bool Cluster)? Element(string itemName)
    {
        foreach (var (suffix, cluster) in new[] { ("クリスタル", false), ("クラスター", true), (" Crystal", false), (" Cluster", true) })
        {
            if (itemName.Length > suffix.Length && itemName.EndsWith(suffix, StringComparison.Ordinal))
                return (itemName[..^suffix.Length], cluster);
        }

        return null;
    }

    /// <summary>出る時刻のビット（0〜23 時）から、出る枠のビット（6 枠）を作る。枠の 4 時間のうち 1 時間でも出ていればその枠に出る。</summary>
    public static byte Slots(uint hours)
    {
        byte slots = 0;
        for (var slot = 0; slot < SlotCount; slot++)
            if (((hours >> (slot * 4)) & 0xF) != 0)
                slots |= (byte)(1 << slot);
        return slots;
    }

    /// <summary>並べたときに、枠ごとに勝つ原料（一番上の出ている品）。誰も出ていない枠は 0。</summary>
    public static uint[] Winners(IReadOnlyList<Candidate> order)
    {
        var winners = new uint[SlotCount];
        for (var slot = 0; slot < SlotCount; slot++)
            foreach (var c in order)
                if ((c.Slots & (1 << slot)) != 0)
                {
                    winners[slot] = c.ItemId;
                    break;
                }

        return winners;
    }

    /// <summary>
    /// 選んだ属性（elements。クリスタルの品番で表す）について、原料を選んで並べる。
    /// 候補の無い属性は飛ばす（呼ぶ側で選べなくしておく）。候補が 1 つも無ければ空の結果。
    /// </summary>
    public static Result Choose(IReadOnlyList<uint> elements, IEnumerable<Candidate> candidates)
    {
        var all = candidates.Where(c => c.Slots != 0 && elements.Contains(c.ElementKey)).ToArray();

        // 属性ごとに、出る枠の組が同じ候補はまとめ（レベルの低い方・品番の小さい方を代表に）、枠の少ない順に上限まで。
        // 上限は、属性の数に応じて組の数が 4096 を超えないように（6 属性なら 4 つずつ。計算でゲームを止めないため）。
        var count = elements.Distinct().Count();
        var perElement = count >= 6 ? 4 : count == 5 ? 5 : 6;
        var reps = elements.Distinct()
            .Select(e => all.Where(c => c.ElementKey == e)
                .GroupBy(c => c.Slots)
                .Select(g => g.OrderBy(c => c.Level).ThenBy(c => c.ItemId).First())
                .OrderBy(c => BitOperations.PopCount(c.Slots)).ThenBy(c => c.Level).ThenBy(c => c.ItemId)
                .Take(perElement).ToArray())
            .Where(r => r.Length > 0)
            .ToArray();
        if (reps.Length == 0)
            return new Result([], new uint[SlotCount]);

        // 組が多いときは、1 つの組で試す並びの数を減らす（全体で数十万回の評価までに収める）。
        var sets = reps.Aggregate(1L, (n, r) => n * r.Length);
        var orderCap = sets <= 64 ? 720 : sets <= 512 ? 120 : 24;

        Candidate[]? best = null;
        Score bestScore = default;
        foreach (var set in Product(reps))
        {
            foreach (var order in Orders(set, orderCap))
            {
                var score = Evaluate(order);
                if (best is null || score.CompareTo(bestScore) > 0 || (score.CompareTo(bestScore) == 0 && Earlier(order, best)))
                {
                    best = order;
                    bestScore = score;
                }
            }
        }

        if (best is null)
            return new Result([], new uint[SlotCount]);

        // 1 つの原料が 2 つの属性に当たって、ほかに候補が無いと、同じ品が 2 回選ばれる。リストに同じ品を 2 回入れないよう 1 つにまとめる
        // （2026-10-05 の対応表ではそういう原料は無い。評価では同じ品は枠を 1 回しか取れないので、ほかに候補があればそちらが選ばれる）。
        // 空き枠を、選んだ属性の別の原料で埋める（リストの下に足すので、上の品から枠を奪わない）。
        var result = best.DistinctBy(c => c.ItemId).ToList();
        while (true)
        {
            var winners = Winners(result);
            var idle = Enumerable.Range(0, SlotCount).Where(s => winners[s] == 0).ToArray();
            if (idle.Length == 0)
                break;
            var wins = WinsOf(result, winners);
            var add = all.Where(c => result.All(r => r.ItemId != c.ItemId) && idle.Any(s => (c.Slots & (1 << s)) != 0))
                .OrderByDescending(c => idle.Count(s => (c.Slots & (1 << s)) != 0))
                .ThenBy(c => wins.GetValueOrDefault(c.ElementKey))
                .ThenBy(c => c.Level).ThenBy(c => c.ItemId)
                .FirstOrDefault();
            if (add is null)
                break;
            result.Add(add);
        }

        return new Result(result.Select(c => c.ItemId).ToArray(), Winners(result));
    }

    private static Dictionary<uint, int> WinsOf(IReadOnlyList<Candidate> order, uint[] winners)
    {
        var wins = new Dictionary<uint, int>();
        foreach (var w in winners.Where(w => w != 0))
        {
            var e = order.First(c => c.ItemId == w).ElementKey;
            wins[e] = wins.GetValueOrDefault(e) + 1;
        }

        return wins;
    }

    /// <summary>並びの良さ。大きいほど良い（Choose の説明の 1〜5 の順）。</summary>
    private readonly record struct Score(int Covered, int Used, int MinWins, int NegMaxWins, int NegLevel) : IComparable<Score>
    {
        public int CompareTo(Score o)
        {
            var c = this.Covered.CompareTo(o.Covered);
            if (c == 0) c = this.Used.CompareTo(o.Used);
            if (c == 0) c = this.MinWins.CompareTo(o.MinWins);
            if (c == 0) c = this.NegMaxWins.CompareTo(o.NegMaxWins);
            if (c == 0) c = this.NegLevel.CompareTo(o.NegLevel);
            return c;
        }
    }

    /// <summary>並びの良さを数える（何十万回も呼ぶので、配列を作らずに数える）。order は属性ごとに 1 品。</summary>
    private static Score Evaluate(Candidate[] order)
    {
        Span<int> wins = stackalloc int[order.Length];
        var used = 0;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            for (var i = 0; i < order.Length; i++)
            {
                if ((order[i].Slots & (1 << slot)) == 0)
                    continue;
                wins[i]++;
                used++;
                break;
            }
        }

        int covered = 0, min = int.MaxValue, max = 0, level = 0;
        for (var i = 0; i < order.Length; i++)
        {
            if (wins[i] > 0) covered++;
            min = Math.Min(min, wins[i]);
            max = Math.Max(max, wins[i]);
            level += order[i].Level;
        }

        return new Score(covered, used, min, -max, -level);
    }

    /// <summary>同じ良さのとき、品番を上から比べて小さい方を選ぶ（毎回同じ結果にするため）。</summary>
    private static bool Earlier(Candidate[] a, Candidate[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i].ItemId != b[i].ItemId)
                return a[i].ItemId < b[i].ItemId;
        return a.Length < b.Length;
    }

    /// <summary>属性ごとの候補から 1 つずつ選ぶ組（直積）。</summary>
    private static IEnumerable<Candidate[]> Product(Candidate[][] reps)
    {
        var index = new int[reps.Length];
        while (true)
        {
            yield return reps.Select((r, i) => r[index[i]]).ToArray();
            var k = reps.Length - 1;
            while (k >= 0 && ++index[k] == reps[k].Length)
                index[k--] = 0;
            if (k < 0)
                yield break;
        }
    }

    /// <summary>
    /// 試す並び。出る枠の少ない順を基本に（少ない品を上にすると、その品が枠を取れる）、枠の数が同じ品どうしは全部の順を試す
    /// （組み合わせが cap を超えるときは基本の順だけ）。
    /// </summary>
    private static IEnumerable<Candidate[]> Orders(Candidate[] set, int cap)
    {
        var groups = set.OrderBy(c => BitOperations.PopCount(c.Slots)).ThenBy(c => c.Level).ThenBy(c => c.ItemId)
            .GroupBy(c => BitOperations.PopCount(c.Slots)).Select(g => g.ToArray()).ToArray();
        var total = groups.Aggregate(1L, (n, g) => n * Factorial(g.Length));
        if (total > cap)
        {
            yield return groups.SelectMany(g => g).ToArray();
            yield break;
        }

        foreach (var combo in Product(groups.Select(g => Permutations(g).ToArray()).ToArray()))
            yield return combo.SelectMany(p => p).ToArray();
    }

    private static long Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

    private static IEnumerable<Candidate[]> Permutations(Candidate[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var p in Permutations(rest))
                yield return [items[i], .. p];
        }
    }

    // Product は Candidate[][] を受けるので、並びの組（Candidate[][] の配列）用に包む。
    private static IEnumerable<Candidate[][]> Product(Candidate[][][] groups)
    {
        var index = new int[groups.Length];
        while (true)
        {
            yield return groups.Select((g, i) => g[index[i]]).ToArray();
            var k = groups.Length - 1;
            while (k >= 0 && ++index[k] == groups[k].Length)
                index[k--] = 0;
            if (k < 0)
                yield break;
        }
    }
}
