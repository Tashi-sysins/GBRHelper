using System;
using System.Collections.Generic;
using System.Linq;

namespace GBRHelper.Ui;

/// <summary>
/// GBRHelper のすべての機能を 1 箇所にまとめた目録。
/// Plugin.cs が機能を 1 個ずつ Add し、MainWindow が左ペインを描くときに Items を列挙する。
///
/// 【有効/無効の保存】
/// - 各機能の Enabled は、機能側（通常は Configuration の bool）で保存する。
/// - FeatureCatalog は保存しない。並び順もコードで決める（SortOrder）。
/// </summary>
public sealed class FeatureCatalog
{
    private readonly List<IFeature> items = new();

    public IReadOnlyList<IFeature> Items => this.items;

    public void Add(IFeature feature)
    {
        this.items.Add(feature);
        this.items.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
    }

    /// <summary>
    /// 選ばれている機能。見つからなければ最初の機能。何も登録されていなければ null。
    /// </summary>
    public IFeature? FindSelected(string? name)
    {
        if (this.items.Count == 0)
            return null;
        if (string.IsNullOrEmpty(name))
            return this.items[0];
        return this.items.FirstOrDefault(f => f.Name == name) ?? this.items[0];
    }

    /// <summary>Enabled になっている機能だけに Tick を伝える。</summary>
    public void TickEnabled()
    {
        for (var i = 0; i < this.items.Count; i++)
        {
            var f = this.items[i];
            if (!f.Enabled)
                continue;

            try
            {
                f.Tick();
            }
            catch (Exception ex)
            {
                // 1 個の機能の例外で他の機能が止まらないように囲う。
                // ログは機能側で出す前提のため、ここでは静かに握りつぶす。
                Svc.Log.Error($"[GBRHelper] 機能「{f.Name}」の Tick で例外: {ex}");
            }
        }
    }
}
