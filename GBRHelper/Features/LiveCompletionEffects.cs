using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>
/// 実際のゲームに繋いだ ICompletionEffects。
///
/// 【方針】
/// - 呼び出しは必ず Framework スレッドで行う（指示書：APIを呼ぶのはFrameworkスレッド上だけ）
/// - 呼び出せない状況（ログイン前・AddonLifecycle 外・例外）はすべて null
/// - GetLocalContentId は IClientState を見る（ログイン直後は 0 のことがある）
/// </summary>
public sealed class LiveCompletionEffects : ICompletionEffects
{
    public ulong? GetLocalContentId()
    {
        try
        {
            var cid = Svc.PlayerState.ContentId;
            return cid == 0 ? null : cid;
        }
        catch
        {
            return null;
        }
    }

    public bool? CallIsGatheringItemGathered(ushort gatheringItemId)
    {
        try
        {
            if (!Svc.Framework.IsInFrameworkUpdateThread)
                return null;

            if (gatheringItemId == 0)
                return null;

            if (Svc.PlayerState.ContentId == 0)
                return null;

            return QuestManager.IsGatheringItemGathered(gatheringItemId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 伝承録を読んだか。Dalamud の IUnlockState.IsItemUnlocked に本のアイテムを渡す。
    /// 中身は PlayerState.IsFolkloreBookUnlocked(本の ItemAction.Data[0])（Dalamud UnlockState.cs:400-401）。
    /// GatheringSubCategory.Division を渡すのではない（Division は採掘・園芸・釣りの本で同じ値になる）。
    /// IsItemUnlocked はプレイヤー情報の読込み前も false を返すので、その場合は先に null にする
    /// （「読んでいない」と「まだ分からない」を混ぜない）。
    /// </summary>
    public bool? IsFolkloreBookRead(uint bookItemId)
    {
        try
        {
            if (!Svc.Framework.IsInFrameworkUpdateThread)
                return null;

            if (Svc.PlayerState.ContentId == 0 || !Svc.PlayerState.IsLoaded)
                return null;

            if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(bookItemId, out var book))
                return null;

            if (!Svc.Unlocks.IsItemUnlockable(book))
                return null; // 解放できる品ではない＝本の番号の取り違え。判断しない

            return Svc.Unlocks.IsItemUnlocked(book);
        }
        catch
        {
            return null;
        }
    }
}
