using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GBRHelper;

/// <summary>Dalamud のサービスをまとめて受け取る場所。</summary>
public sealed class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static ITargetManager Targets { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IAetheryteList Aetherytes { get; private set; } = null!;

    /// <summary>解放状態（伝承録を読んだか等）。Dalamud 本体の UnlockState が FFXIVClientStructs を正しい番号で呼ぶ。</summary>
    [PluginService] public static IUnlockState Unlocks { get; private set; } = null!;

    /// <summary>関数のフック（機能①の翻訳で ImGui の公開関数を横取りする）。</summary>
    [PluginService] public static IGameInteropProvider Hooks { get; private set; } = null!;
}

/// <summary>自分の位置など、よく使う小物。</summary>
public static class Me
{
    /// <summary>自分の座標。取れなければ原点を返す。</summary>
    public static System.Numerics.Vector3 Position
        => System.Linq.Enumerable.FirstOrDefault(Svc.Objects, o => o.ObjectIndex == 0)?.Position
           ?? System.Numerics.Vector3.Zero;

    /// <summary>ログインして操作できる状態か。</summary>
    public static bool Available
        => Svc.ClientState.IsLoggedIn
           && System.Linq.Enumerable.FirstOrDefault(Svc.Objects, o => o.ObjectIndex == 0) != null;
}
