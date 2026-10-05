namespace GBRHelper.Features;

/// <summary>
/// いまの場所でリテイナーを呼べるか（ワールドの判定）。
///
/// 公式の案内（The Lodestone「ワールド間テレポ」の制限事項）に
/// 「Retainers cannot be summoned, hired, or released from service.」とあり、
/// 他のワールドに滞在している間はリテイナーを呼べない。
/// そのまま宿屋へ向かうと、呼び鈴を開いても回収できずに失敗し、
/// 採集と往復を繰り返すことになる。ホームワールドへ戻るまで回収に向かわない。
///
/// ホームワールドへ自分で戻ることはしない。
/// ワールドを移ると、採集の続きもそのワールドで行うことになり、利用者の意図から外れるため。
/// </summary>
public static class RetainerAccess
{
    public enum State
    {
        /// <summary>ホームワールドに居る。</summary>
        Allowed,

        /// <summary>他のワールドに滞在している。</summary>
        Visiting,

        /// <summary>
        /// 判断できない（ワールドが読めない）。
        /// 止める側に倒すと、読めないだけで回収が永久に止まるので、これまでどおり向かう側に倒す。
        /// 向かった先で本当に呼べなければ、呼び鈴の段で失敗として拾われる。
        /// </summary>
        Unknown,
    }

    /// <summary>ワールドの行番号から判断する。0 は「読めない」。</summary>
    public static State Judge(uint currentWorld, uint homeWorld)
    {
        if (currentWorld == 0 || homeWorld == 0)
            return State.Unknown;

        return currentWorld == homeWorld ? State.Allowed : State.Visiting;
    }

    /// <summary>いまの状態。Dalamud の IPlayerState から読む（読み込み前は 0 が返る）。</summary>
    public static State Current()
    {
        if (!Svc.PlayerState.IsLoaded)
            return State.Unknown;

        return Judge(Svc.PlayerState.CurrentWorld.RowId, Svc.PlayerState.HomeWorld.RowId);
    }
}
