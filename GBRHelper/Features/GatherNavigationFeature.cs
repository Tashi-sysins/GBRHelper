using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using GBRHelper.Ipc;
using GBRHelper.Navigation;
using GBRHelper.Ui;

namespace GBRHelper.Features;

public sealed class GatherNavigationFeature : IFeature, IBridgeEnvironment, IDisposable
{
    private readonly Configuration config;
    private readonly GbrConfigAccess access;
    private readonly GbrTimedAccess timedAccess;
    private readonly GbrConfigAccess navigationAccess = new("vnavmesh");
    private readonly GatherBuddyIpc gbr;
    private readonly VnavmeshIpc nav;
    private readonly Func<string?> busy;
    private readonly Func<string> stop;
    private FrameworkRouteQueries? queries;
    private GbrNavigationBridge? bridge;
    private NavStateAccessor? navigationState;
    private NavigationTraceWriter? trace;
    private readonly ConnectionDiagnostics connection = new();
    private readonly MovementDiagnostics movement = new();
    private LoadedPluginBinding? gbrBinding, navBinding;
    private readonly GatherAcquisitionObserver acquisition = new();
    private object? connectedOwner, connectedPlugin;
    private object? connectedNavigation;
    private int cancelPending;
    private Assembly? navigationAssembly;
    private DateTime nextConnect, nextSample;
    private uint territory;
    private NavigationMode connectedMode;
    private bool controlledTest;
    private string status = "移動改善は無効です", stopResult = "";
    public string Name => "採取の移動改善";
    public string Description => "GBRの採掘・園芸経路を検査し、障害物の迂回と角の曲線化を行います。移動・着地・採取はGBRが実行します。";
    public int SortOrder => 20;
    public bool Enabled { get => true; set { } }
    public bool HideEnableToggle => true;

    public GatherNavigationFeature(Configuration config, GbrConfigAccess access, GatherBuddyIpc gbr, VnavmeshIpc nav,
        Func<string?> busy, Func<string> stop)
    { this.config = config; this.access = access; timedAccess = new(access); this.gbr = gbr; this.nav = nav; this.busy = busy; this.stop = stop; }

    public NavigationMode Mode => config.GatherNavigationEnabled ? NavigationMode.Improve : NavigationMode.Off;
    public RouteSettings Settings { get; } = new(Ground: true, Flight: true, Detours: true);
    public uint Territory => Svc.ClientState.TerritoryType;
    public Vector3 Position => Me.Position;
    public bool IsFlying => Svc.Condition[ConditionFlag.InFlight];
    public bool IsMounted => Svc.Condition[ConditionFlag.Mounted];
    public bool IsDiving => Svc.Condition[ConditionFlag.Diving];
    public bool CanMonitorProgress => MayProcess && !timedAccess.TaskBusy
        && !Svc.Condition[ConditionFlag.Gathering] && !Svc.Condition[ConditionFlag.ExecutingGatheringAction]
        && !Svc.Condition[ConditionFlag.Casting] && !Svc.Condition[ConditionFlag.Mounting] && !Svc.Condition[ConditionFlag.Mounting71]
        && !Svc.Condition[ConditionFlag.Occupied] && !Svc.Condition[ConditionFlag.Jumping] && !Svc.Condition[ConditionFlag.Jumping61];
    public double StallThresholdSeconds => double.TryParse(timedAccess.ReadSetting("NavResetThreshold"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    public bool MayProcess => Svc.Framework.IsInFrameworkUpdateThread && Me.Available && busy() == null
        && gbr.IsAutoGatherEnabled() == true && !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51]
        && !Svc.Condition[ConditionFlag.Fishing] && !IsDiving && !Svc.Condition[ConditionFlag.Unconscious]
        && !Svc.Condition[ConditionFlag.Swimming] && !Svc.Condition[ConditionFlag.BoundByDuty]
        && Svc.Objects.LocalPlayer?.ClassJob.RowId is 16 or 17
        && ReferenceEquals(timedAccess.TryGetAuto(), connectedOwner) && ReferenceEquals(access.Plugin(), connectedPlugin)
        && ReferenceEquals(navigationAccess.Plugin(), connectedNavigation)
        && gbrBinding?.IsCurrent == true && navBinding?.IsCurrent == true;

    public bool IsCurrent(RouteRequest request)
    {
        if (!MayProcess || Territory != request.Territory) return false;
        if (navigationState == null || connectedOwner == null) return false;
        var state = navigationState.Read(connectedOwner);
        if (state.Destination != request.Destination) return false;
        // _currentGatherTarget is only populated after opening a gathering node. Navigation uses CurrentRequestedTarget.
        if (!GbrRequestIdentity.MatchesRequestedItem(connectedOwner, request.ItemId)) return false;
        return NodeIdentity(request.Destination, null) == request.Node;
    }
    public string NodeIdentity(Vector3 destination, uint? nodeId)
    {
        var node = Svc.Objects.FirstOrDefault(o => o.Position == destination && (!nodeId.HasValue || o.BaseId == nodeId));
        // Preserve individual identity where loaded; remote node uses position+territory until it becomes visible.
        return node != null ? $"object:{node.GameObjectId}:{node.BaseId}" : $"far:{destination.X:R},{destination.Y:R},{destination.Z:R}";
    }
    public List<Vector3>? RemainingWaypoints() => nav.ListWaypoints()?.ToList();
    public float PathTolerance
    {
        get
        {
            try { return Svc.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Path.GetTolerance").InvokeFunc(); }
            catch { return float.NaN; } // no inferred tolerance when the follower cannot be queried
        }
    }
    public ProbeResult FinalConnection(Vector3 from, Vector3 to, bool fly) => queries?.CheckNow(from, to, fly) ?? ProbeResult.Unknown("照会を解除済み");
    public bool StopGather(RouteRequest request, string reason)
    {
        try
        {
            stopResult = stop();
            movement.CompleteStop(request, stopResult);
            Record("stop", request, new { Reason = reason, Result = stopResult, Movement = movement.LastFailure });
            return gbr.IsAutoGatherEnabled() == false;
        }
        catch (Exception ex)
        {
            movement.CompleteStop(request, "停止処理の例外: " + ex.Message);
            Record("stop-error", request, movement.LastFailure);
            return false; // bridge uses the already-validated Enabled setter as its final stop fallback
        }
    }
    public void ReportMovement(MovementSnapshot snapshot, bool failure)
    {
        snapshot = snapshot with { Acquisition = acquisition.Last, NativeMetrics = queries?.NativeMetrics };
        movement.Record(snapshot, failure);
        Record(failure ? "movement-failure" : "movement", snapshot.Request, snapshot);
    }
    public void RecordStopResult(RouteRequest request, string result)
    {
        movement.CompleteStop(request, result);
        Record("stop-confirmed", request, movement.LastFailure);
    }
    public void Record(string kind, RouteRequest? request, object? detail = null) => trace?.Record(kind, request, detail);
    public void ObserveAttempt(RouteRequest request)
    {
        movement.ResetCurrent();
        acquisition.Begin(request.Attempt, request.ItemId, MaterialInventory.Local(request.ItemId));
    }

    public void Tick()
    {
        try
        {
            if (System.Threading.Interlocked.Exchange(ref cancelPending, 0) != 0) bridge?.ResetPending();
            if (Mode == NavigationMode.Off) { if (bridge != null) Disconnect(); status = "移動改善は無効：従来の採取移動を使用します"; return; }
            if (bridge != null && (connectedMode != Mode || territory != Territory || !ReferenceEquals(timedAccess.TryGetAuto(), connectedOwner)
                || !ReferenceEquals(access.Plugin(), connectedPlugin) || !ReferenceEquals(navigationAccess.Plugin(), connectedNavigation)
                || gbrBinding?.IsCurrent != true || navBinding?.IsCurrent != true)) Disconnect();
            if (bridge == null && DateTime.UtcNow >= nextConnect) Connect();
            if (bridge == null) return;
            if (gbr.IsAutoGatherEnabled() != true || busy() != null) acquisition.Reset();
            if (!MayProcess) { bridge.ResetPending(); status = "従来処理を使用：停止中・他機能・対応外の状態"; }
            else status = bridge.Status;
            queries!.Pump();
            if (DateTime.UtcNow >= nextSample)
            {
                nextSample = DateTime.UtcNow.AddMilliseconds(250);
                if (bridge.CurrentRequest is { } request)
                    Record("position", request, new { Position, IsFlying, IsMounted, Gathering = Svc.Condition[ConditionFlag.Gathering],
                        Queue = queries.Pending, Searches = queries.Searches, FrameMilliseconds = queries.LastPumpMilliseconds });
                if (acquisition.Last is { } last)
                {
                    var current = acquisition.Sample(MaterialInventory.Local(last.RequestedItem), Svc.Condition[ConditionFlag.ExecutingGatheringAction], controlledTest);
                    if (current != last) Record("acquisition", bridge.CurrentRequest, current);
                }
            }
        }
        catch (Exception ex)
        {
            connection.RegisteredHooks = bridge?.HookCount ?? 0;
            var changed = connection.Fail(ex);
            try { Disconnect(); } catch (Exception cleanup) { connection.CleanupError = cleanup.ToString(); connection.ActiveHooks = null; }
            nextConnect = DateTime.UtcNow.AddSeconds(5);
            status = $"移動改善は利用できません（{connection.Stage}）：{connection.Summary}";
            if (changed)
            {
                EnsureTrace();
                Record("connection-error", null, connection.ToText());
                Svc.Log.Error("[GBRHelper Navigation] " + connection.ToText());
            }
        }
    }
    private void Connect()
    {
        nextConnect = DateTime.UtcNow.AddSeconds(5);
        connection.Begin();
        connection.Stage = "GatherBuddyReborn：プラグイン/パス取得";
        var gbrSource = access.ResolveBinding(stage => { connection.Stage = "GatherBuddyReborn：" + stage; connection.Gbr.Stage = stage; });
        DescribeSource(gbrSource, connection.Gbr, GbrCompatibilityProfile.GbrHash);
        connection.Stage = "vnavmesh：プラグイン/パス取得";
        var navSource = navigationAccess.ResolveBinding(stage => { connection.Stage = "vnavmesh：" + stage; connection.Navigation.Stage = stage; });
        DescribeSource(navSource, connection.Navigation, GbrCompatibilityProfile.NavHash);
        ValidateBinding(gbrSource, GbrCompatibilityProfile.GbrHash, connection.Gbr);
        connection.Gbr.Validated = false;
        var autoType = GbrCompatibilityProfile.ValidateAutoGather(gbrSource.Assembly);
        connection.Gbr.Validated = true;
        ValidateBinding(navSource, GbrCompatibilityProfile.NavHash, connection.Navigation);
        connection.Stage = "AutoGather実体取得";
        var owner = gbrSource.Instance.GetType().GetProperty("AutoGather", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException("GBR.AutoGather");
        if (owner.GetType() != autoType || !ReferenceEquals(timedAccess.TryGetAuto(), owner))
            throw new InvalidOperationException("GBRの採取処理が変わりました。接続をやり直します");
        gbrSource.EnsureCurrent(); navSource.EnsureCurrent();
        gbrBinding = gbrSource; navBinding = navSource;
        connectedPlugin = gbrSource.Instance; connectedOwner = owner; connectedNavigation = navSource.Instance; navigationAssembly = navSource.Assembly;
        navigationState = new(owner.GetType());
        queries = new();
        EnsureTrace();
        connection.Stage = "接続ライブラリ準備";
        ManagedHookRuntime.Initialize(Svc.PluginInterface.AssemblyLocation.DirectoryName!);
        connection.HookRuntime = ManagedHookRuntime.Description;
        connection.Stage = "フック登録";
        bridge = LoadedPluginBinding.Connect(gbrSource, navSource, () => new GbrNavigationBridge(owner, this, new RouteProcessor(queries)));
        connectedMode = Mode; territory = Territory;
        connection.Connected(bridge.HookCount);
        Record("connected", null, new { Contract = GbrCompatibilityProfile.Contract, Gbr = GbrCompatibilityProfile.GbrHash,
            Vnavmesh = GbrCompatibilityProfile.NavHash, Helper = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            HelperMvid = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId, ProcessId = Environment.ProcessId,
            Runtime = Environment.Version.ToString(), Mode, Settings, Connection = connection.ToText() });
    }
    private void ValidateBinding(LoadedPluginBinding source, string expectedHash, PluginValidationReport report)
    {
        try { GbrCompatibilityProfile.ValidateFileAndLoadedModule(source.Assembly, source.DllPath, expectedHash, report); }
        finally { connection.Stage = source.InternalName + "：" + report.Stage; }
    }
    private static void DescribeSource(LoadedPluginBinding source, PluginValidationReport report, string expectedHash)
    {
        report.DllPath = source.DllPath; report.ExpectedHash = expectedHash; report.AssemblyName = source.Assembly.FullName;
        report.AssemblyLocation = source.Assembly.Location; report.LoadedMvid = source.Assembly.ManifestModule.ModuleVersionId;
    }
    private void EnsureTrace()
    {
        if (trace != null) { connection.StorageError = trace.Error; return; }
        try
        {
            trace = new(System.IO.Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "navigation-traces"));
            connection.StorageError = null;
        }
        catch (Exception ex) { connection.StorageError = ex.ToString(); }
    }
    private string DiagnosticText()
    {
        if (trace != null) connection.StorageError = trace.Error;
        return $"GBRHelper {typeof(Plugin).Assembly.GetName().Version} / MVID={typeof(Plugin).Assembly.ManifestModule.ModuleVersionId}\n"
            + $"Process={Environment.ProcessId} / Runtime={Environment.Version}\nStatus={status}\n" + connection.ToText()
            + "\n移動診断（接続とは別の状態・発生時刻）:\n"
            + movement.ToText(trace?.Path, trace?.Dropped ?? 0, connection.StorageError, acquisition.Last);
    }
    public void ResetCharacter() { Disconnect(); movement.Clear(); acquisition.Reset(); controlledTest = false; nextConnect = DateTime.MinValue; }
    public void CancelPending() { System.Threading.Interlocked.Exchange(ref cancelPending, 1); }
    private void Disconnect()
    {
        try { bridge?.Dispose(); connection.ActiveHooks = 0; }
        finally { bridge = null; queries?.Dispose(); queries = null; navigationState = null; connectedOwner = connectedPlugin = connectedNavigation = null; navigationAssembly = null; gbrBinding = navBinding = null; acquisition.Reset(); movement.ResetCurrent(); }
    }
    // 画面は「有効」のチェックだけ（利用者の指示・0.3.0.18）。移動の記録は今までどおり navigation-traces に残る。
    public void DrawRight()
    {
        var enabled = config.GatherNavigationEnabled;
        if (ImGui.Checkbox("有効", ref enabled)) { config.GatherNavigationEnabled = enabled; config.Save(); CancelPending(); }
    }
    public void Dispose() { Disconnect(); trace?.Dispose(); trace = null; }
}
