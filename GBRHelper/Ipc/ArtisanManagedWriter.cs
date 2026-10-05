using System.Collections.Generic;

namespace GBRHelper.Ipc;

/// <summary>Artisanの同じリストの前回名と現在名だけを置換する。</summary>
public static class ArtisanManagedWriter
{
    public static GbrAutoGatherListAccess.WriteResult Write(GbrAutoGatherListAccess lists, string name,
        string tag, IReadOnlyList<(uint ItemId, uint Quantity)> entries, string previous)
        => lists.WriteManagedList(name, tag, entries, enabled: true, previousListName: previous);
}
