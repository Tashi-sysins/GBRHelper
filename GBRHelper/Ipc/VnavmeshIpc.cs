using System.Numerics;

namespace GBRHelper.Ipc;

/// <summary>
/// vnavmesh への窓口。呼び鈴まで歩くのに使う。
///
/// 使っている IPC 名は vnavmesh のソースで確認済み
/// （ffxiv_navmesh/vnavmesh/IPCProvider.cs、前置詞は "vnavmesh."）。
///
/// 注意（資料と実装から確かめた事実）:
///  ・経路探索に失敗しても例外は飛ばず、経路が空のまま終わる。
///    「移動失敗」と「移動完了」が IPC 上は区別できない。
///    → 距離と制限時間を必ず自分で確かめる。
///  ・走行状態が3種類ある。1つでも進行中なら移動中とみなす。
/// </summary>
public sealed class VnavmeshIpc : IpcGate
{
    public override string InternalName => "vnavmesh";

    /// <summary>
    /// ナビメッシュが使える状態か。
    ///
    /// 中身は navmeshManager.Navmesh != null（IPCProvider.cs:17）。
    /// **エリアを移動した直後は読み込み中で null になる。**
    /// 着いた瞬間にここを見て false だからと諦めると、
    /// 構築が終われば歩けたはずの場面で失敗する（実機でそうなった）。
    /// 使う側は <see cref="BuildProgress"/> と併せて待つこと。
    /// </summary>
    public bool IsReady()
        => this.TryInvoke("Nav.IsReady",
               () => this.Func<bool>("vnavmesh.Nav.IsReady").InvokeFunc(), out var ready)
           && ready;

    /// <summary>
    /// ナビメッシュの構築の進み具合。
    ///
    /// 中身は navmeshManager.LoadTaskProgress（IPCProvider.cs:18）。
    ///   0 以上 1 未満 … 構築中（待てば使えるようになる）
    ///   負の値        … 構築していない
    /// 読めなければ null。
    ///
    /// 「まだ使えない」と「待っても無駄」を区別するために見る。
    /// </summary>
    public float? BuildProgress()
        => this.TryInvoke("Nav.BuildProgress",
            () => this.Func<float>("vnavmesh.Nav.BuildProgress").InvokeFunc(), out var p)
            ? p
            : null;

    /// <summary>
    /// いまナビメッシュを構築している最中か。
    /// 構築中なら、使えるようになるまで待つ価値がある。
    /// </summary>
    public bool IsBuilding()
        => this.BuildProgress() is { } progress && progress is >= 0 and < 1;

    /// <summary>
    /// エリアを移ったときに自動でナビメッシュを読み込む設定になっているか。
    ///
    /// 【これが OFF だと、待っても永久に使えるようにならない】
    /// vnavmesh 側はエリアが変わったとき、この設定が false なら
    /// 何も読み込まずに戻る（NavmeshManager.cs:68-71）:
    ///
    ///     if (!Service.Config.AutoLoadNavmesh)
    ///     {
    ///         if (CurrentKey.Length == 0)
    ///             return; // nothing is loaded, and auto-load is forbidden
    ///
    /// そのため BuildProgress は負のまま（構築すら始まらない）になる。
    /// 「構築中だから待つ」では解決しないので、こちらから読み込みを頼む。
    ///
    /// 読めなければ null。
    /// </summary>
    public bool? IsAutoLoad()
        => this.TryInvoke("Nav.IsAutoLoad",
            () => this.Func<bool>("vnavmesh.Nav.IsAutoLoad").InvokeFunc(), out var on)
            ? on
            : null;

    /// <summary>
    /// いまのエリアのナビメッシュを読み込ませる。
    ///
    /// 中身は navmeshManager.Reload(true)（IPCProvider.cs:19）。
    /// 引数の true は「出来合いのものがあれば使う」の意味で、
    /// 無ければその場で構築が始まる。
    ///
    /// 自動読み込みが OFF の環境でも、これを呼べば読み込まれる。
    /// **相手の設定は書き換えない。** 設定を変えるのは利用者の領分で、
    /// こちらの用が済んだあとも影響が残ってしまう。
    /// </summary>
    public bool Reload()
        => this.TryInvoke("Nav.Reload",
            () => this.Func<bool>("vnavmesh.Nav.Reload").InvokeFunc(), out _);

    /// <summary>
    /// 指定地点の近くまで歩く。
    /// range を対話距離より内側に取ること。ちょうどを狙うと到着判定の揺れで届かない。
    /// </summary>
    public bool MoveCloseTo(Vector3 destination, float range)
        => this.TryInvoke("SimpleMove.PathfindAndMoveCloseTo",
               () => this.Func<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo")
                   .InvokeFunc(destination, false, range), out var ok)
           && ok;

    /// <summary>
    /// いま移動中か。
    /// 走行状態は3種類あるので、1つでも進行中なら移動中とみなす。
    /// </summary>
    public bool IsMoving()
    {
        if (this.TryInvoke("Path.IsRunning",
                () => this.Func<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out var running) && running)
            return true;

        if (this.TryInvoke("Nav.PathfindInProgress",
                () => this.Func<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc(), out var navBusy) && navBusy)
            return true;

        if (this.TryInvoke("SimpleMove.PathfindInProgress",
                () => this.Func<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(), out var moveBusy) && moveBusy)
            return true;

        return false;
    }

    /// <summary>
    /// いま歩いている（飛んでいる）経路の残りの点（最後の点が行き先）。経路が無ければ空、読めなければ null。
    /// IPC は vnavmesh.Path.ListWaypoints（IPCProvider.cs:45）。GBR は vnavmesh に経路を求めさせ、その点の並びを
    /// Path.MoveTo で渡して動かす（GBR の AutoGather.Movement.cs 271・381）ので、これを読めば GBR の行き先が分かる。
    /// ⚠ 誰が積んだ経路かは区別しない。GBR の自動採集が ON で、こちらが動かしていない場面でだけ使う（ヤンサの山越え）。
    /// </summary>
    public System.Collections.Generic.List<Vector3>? ListWaypoints()
        => this.TryInvoke("Path.ListWaypoints",
            () => this.Func<System.Collections.Generic.List<Vector3>>("vnavmesh.Path.ListWaypoints").InvokeFunc(), out var points)
            ? points ?? []
            : null;

    /// <summary>移動を止める。自分が始めた移動のときだけ呼ぶこと。</summary>
    public bool Stop()
        => this.TryAction("Path.Stop",
            () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
