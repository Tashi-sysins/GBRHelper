using System.Collections.Generic;
namespace GBRHelper.Features;

/// <summary>ベンチャー依頼品の解放採取の画面の中身（帯ごと）。</summary>
public sealed class UnlockPanel
{
    /// <summary>件数を出せない理由（まだ解析していない・依頼品を読めない など）。出せれば null。</summary>
    public string? Problem { get; set; }

    /// <summary>「完了分を整理」「再解析」の結果など、下の 1 行。</summary>
    public string ResultLine { get; set; } = "";

    public List<UnlockBand> Bands { get; set; } = new();
}

public sealed class UnlockBand
{
    public int Key { get; set; }

    /// <summary>件数を出せたか（false なら「－」）。</summary>
    public bool Available { get; set; }

    public int Ungathered { get; set; }
    public int Unknown { get; set; }
    public int FolkloreLocked { get; set; }
    public int ExcludedNotVenture { get; set; }

    /// <summary>GBR のリスト：0 無い・1 有効・2 無効。</summary>
    public int Registered { get; set; }

    /// <summary>帯のチェックに乗せたときの吹き出しの行（見出しの次から）。</summary>
    public List<TipLine> Tip { get; set; } = new();

    /// <summary>対象品がなくなった帯（チェックが付いていても暗くする）。</summary>
    public bool Completed => this.Available && this.Ungathered == 0 && this.Unknown == 0;
}

/// <summary>吹き出しの 1 行。Tone：0 ふつう・1 灰・2 黄。</summary>
public sealed record TipLine(string Text, int Tone = 0);

/// <summary>全素材の補充の画面の中身（帯ごと）。</summary>
public sealed class StockPanel
{
    /// <summary>件数を出せない理由（鞄を読めない など）。出せれば null。</summary>
    public string? Problem { get; set; }

    public List<StockBand> Bands { get; set; } = new();
}

public sealed class StockBand
{
    public int Key { get; set; }

    /// <summary>採る候補の品（MaterialPlan.Candidates）。目標は、画面の数で決める（MaterialPlan.FromCandidates）。</summary>
    public List<MaterialPlan.Candidate> Items { get; set; } = new();

    /// <summary>GBR のリスト：0 無い・1 有効・2 無効。</summary>
    public int Registered { get; set; }
}

