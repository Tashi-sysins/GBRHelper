using System;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using GBRHelper.Ipc;
using GBRHelper.Navigation;
using GBRHelper.Ui;

namespace GBRHelper.Features;

/// <summary>Owns a temporary, version-checked purchase approach adapter. Never writes GBR files or fields.</summary>
public sealed class VendorAethernetFeature(Configuration config, GbrConfigAccess access, LifestreamIpc lifestream,
    RunLog log, Func<bool> characterReady, Func<string?> blocked) : IFeature, IVendorAethernetEnvironment, IDisposable
{
    private LoadedPluginBinding? binding;
    private VendorAethernetBridge? bridge;
    private PropertyInfo? navigatorProperty;
    private DateTime nextConnect;
    private string status = "接続待ちです";
    private string lastError = "";
    private bool disposed;
    private bool cleanupPending;

    public string Name => "購入時のエーテライト接近";
    public string Description => "NPCへの購入移動でエーテライトの手前に止まる場合、都市内転送に必要な距離まで接近します。";
    public int SortOrder => 25;
    public bool Enabled { get => true; set { } }
    public bool HideEnableToggle => true;

    public bool MayAssist => !disposed && config.VendorAethernetAssistOn && characterReady()
        && Svc.Framework.IsInFrameworkUpdateThread && Me.Available && binding?.IsCurrent == true
        && blocked() == null
        && !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51]
        && !Svc.Condition[ConditionFlag.Occupied] && !Svc.Condition[ConditionFlag.OccupiedInEvent]
        && !Svc.Condition[ConditionFlag.OccupiedInQuestEvent] && !Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !Svc.Condition[ConditionFlag.WatchingCutscene] && !Svc.Condition[ConditionFlag.WatchingCutscene78]
        && !Svc.Condition[ConditionFlag.Casting] && !Svc.Condition[ConditionFlag.InCombat]
        && !Svc.Condition[ConditionFlag.Unconscious] && !Svc.Condition[ConditionFlag.Gathering]
        && !Svc.Condition[ConditionFlag.ExecutingGatheringAction] && !Svc.Condition[ConditionFlag.BoundByDuty]
        && !Svc.Condition[ConditionFlag.Occupied33] && !Svc.Condition[ConditionFlag.Occupied38]
        && !Svc.Condition[ConditionFlag.Occupied39] && !Svc.Condition[ConditionFlag.Mounting]
        && !Svc.Condition[ConditionFlag.Mounting71] && !Svc.Condition[ConditionFlag.InFlight]
        && !Svc.Condition[ConditionFlag.Diving] && !Svc.Condition[ConditionFlag.Fishing];
    public DateTime UtcNow => DateTime.UtcNow;
    public uint Territory => Svc.ClientState.TerritoryType;

    public bool IsCurrent(object owner)
        => binding?.IsCurrent == true && navigatorProperty != null
            && ReferenceEquals(navigatorProperty.GetValue(null), owner);

    public bool TryGetLifestreamBusy(out bool busy) => lifestream.TryIsBusy(out busy);

    public bool TryGetMotion(out bool moving)
    {
        moving = false;
        try
        {
            // All three reads must succeed. Missing IPC must not look like an idle navigation system.
            var running = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc();
            var query = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc();
            var simple = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc();
            moving = running || query || simple;
            return true;
        }
        catch { return false; }
    }

    public bool TryMoveTo(System.Numerics.Vector3 position)
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<System.Numerics.Vector3, bool, bool>(
                "vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc(position, false);
        }
        catch { return false; }
    }

    public void Record(string message)
    {
        status = message;
        log.Write("エーテライト接近", message);
    }

    public void Tick()
    {
        if (disposed) return;
        try
        {
            if (!config.VendorAethernetAssistOn || !characterReady())
            {
                Disconnect();
                status = "補助は無効です";
                return;
            }
            if (cleanupPending) Disconnect();
            if (binding != null && !binding.IsCurrent) Disconnect();
            if (bridge != null) { bridge.Observe(); return; }
            if (DateTime.UtcNow < nextConnect) return;
            nextConnect = DateTime.UtcNow.AddSeconds(5);
            var current = access.ResolveBinding();
            var report = new PluginValidationReport { InternalName = current.InternalName };
            GbrCompatibilityProfile.ValidateFileAndLoadedModule(current.Assembly, current.DllPath,
                GbrCompatibilityProfile.GbrHash, report);
            var navigator = current.Instance.GetType().GetProperty("VendorNavigator", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMemberException("GBRの購入移動処理を取得できません");
            if (navigator.PropertyType.FullName != "GatherBuddy.Vulcan.Vendors.VendorNavigator")
                throw new NotSupportedException("GBRの購入移動処理の型が対応版と異なります");
            current.EnsureCurrent();
            ManagedHookRuntime.Initialize(Svc.PluginInterface.AssemblyLocation.DirectoryName!);
            binding = current;
            navigatorProperty = navigator;
            var created = new VendorAethernetBridge(current.Assembly, this, ManagedHookRuntime.CreateHook);
            bridge = created; // retain ownership even if the post-connection liveness check fails
            current.EnsureCurrent();
            lastError = "";
            Record("購入時の接近補助を開始しました（大エーテライトは高さを含め10.5m以内まで接近）");
        }
        catch (Exception ex)
        {
            if (ex is VendorAethernetRegistrationException registration) bridge = registration.PendingBridge;
            try { Disconnect(); }
            catch (Exception cleanup) { ex = new AggregateException(ex, cleanup); }
            nextConnect = DateTime.UtcNow.AddSeconds(5);
            status = "接近補助は利用できません：" + ex.Message;
            if (lastError != status) { lastError = status; log.Write("エーテライト接近", status); }
        }
    }

    public void DrawRight()
    {
        var on = config.VendorAethernetAssistOn;
        if (ImGui.Checkbox("有効##vendorAethernet", ref on))
        {
            config.VendorAethernetAssistOn = on;
            config.Save();
            if (!on) Disconnect();
            else nextConnect = default;
        }
        ImGui.TextWrapped(status);
        ImGui.TextWrapped("GBRの購入処理中だけ動作します。接近後のアクセスと転送はGBRとLifestreamが行います。");
    }

    private void Disconnect()
    {
        // The hooks can remain in the process until disposal finishes; the environment guard goes false first.
        binding = null;
        navigatorProperty = null;
        cleanupPending = bridge != null;
        bridge?.Dispose();
        bridge = null;
        cleanupPending = false;
    }

    public void ResetCharacter() { Disconnect(); nextConnect = default; status = "接続待ちです"; }
    public void Dispose() { disposed = true; Disconnect(); }
}
