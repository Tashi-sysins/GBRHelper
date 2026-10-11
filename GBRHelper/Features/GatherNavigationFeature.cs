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
    private string status = "移動改善は無効です", stopResult = "", diagnostic = "";
    public string Name => "採取の移動改善";
    public string Description => "GBRの採掘・園芸経路を検査し、障害物の迂回と角の曲線化を行います。移動・着地・採取はGBRが実行します。";
    public int SortOrder => 20;
    public bool Enabled { get => true; set { } }
    public bool HideEnableToggle => true;

    public GatherNavigationFeature(Configuration config, GbrConfigAccess access, GatherBuddyIpc gbr, VnavmeshIpc nav,
        Func<string?> busy, Func<string> stop)
    { this.config = config; this.access = access; timedAccess = new(access); this.gbr = gbr; this.nav = nav; this.busy = busy; this.stop = stop; }

    public NavigationMode Mode => Enum.IsDefined(typeof(NavigationMode), config.GatherNavigationMode) ? (NavigationMode)config.GatherNavigationMode : NavigationMode.Off;
    public RouteSettings Settings => new(Ground: config.GatherNavigationGround, Flight: config.GatherNavigationFlight, Detours: config.GatherNavigationDetours);
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
    public void DrawRight()
    {
        var mode = (int)Mode;
        if (ImGui.Combo("動作", ref mode, "無効\0観測のみ\0移動改善（実機検証用）\0"))
        { config.GatherNavigationMode = mode; config.Save(); }
        ImGui.TextWrapped(status);
        if (movement.LastFailure is { } failure)
            ImGui.TextWrapped($"直近の移動中断 {failure.Time.ToLocalTime():MM/dd HH:mm:ss} / {failure.Phase}：{failure.Reason}（取得は観測結果で確認）");
        ImGui.TextWrapped("初期設定は無効です。角の曲線は、地上は歩ける面、飛行は当たり判定で確かめた線だけ使います。動く物の回避は保証しません。");
        var ground = config.GatherNavigationGround;
        if (ImGui.Checkbox("地上の曲線化", ref ground)) { config.GatherNavigationGround = ground; config.Save(); CancelPending(); }
        var detours = config.GatherNavigationDetours;
        if (ImGui.Checkbox("衝突検出時の有限迂回", ref detours)) { config.GatherNavigationDetours = detours; config.Save(); CancelPending(); }
        var flight = config.GatherNavigationFlight;
        if (ImGui.Checkbox("飛行・着地を含む経路（実機検証用）", ref flight)) { config.GatherNavigationFlight = flight; config.Save(); CancelPending(); }
        ImGui.TextWrapped("確かめられない線は使わず、GBRの元の経路で続けます。止めるのは、新しく作った線が確かに当たるときと、同じ所で進めなくなるのを繰り返したときだけです。");
        if (ImGui.Button("Auto-Gatherを停止")) { CancelPending(); stopResult = stop(); }
        if (stopResult.Length != 0) ImGui.TextWrapped(stopResult);
        ImGui.Separator();
        if (ImGui.Checkbox("外部増減を排除した取得確認テスト（次の採取試行から）", ref controlledTest)) acquisition.Reset();
        ImGui.TextWrapped("このチェックは現在の検証中だけ有効です。売買・移動・精選等による同じ品の増減がある場合は外してください。UI表示・到着だけでは取得成功としません。");
        if (acquisition.Last is { } sample)
            ImGui.TextWrapped($"要求品 {sample.RequestedItem} / 本人所持数 {sample.Before?.ToString() ?? "不明"} → {sample.After?.ToString() ?? "不明"} / {sample.Classification}");
        if (ImGui.Button("診断を書き出す"))
        {
            EnsureTrace();
            Record("diagnostic", bridge?.CurrentRequest, new { Status = status, Settings, Mode, Acquisition = acquisition.Last,
                Queue = queries?.Pending, Searches = queries?.Searches, FrameMilliseconds = queries?.LastPumpMilliseconds,
                Dropped = trace?.Dropped, Connection = DiagnosticText() });
            diagnostic = trace == null ? "診断を保存できません。「診断をコピー」で詳細を取得できます。" : "診断の保存先：" + trace.Path;
        }
        ImGui.SameLine();
        if (ImGui.Button("診断をコピー")) { ImGui.SetClipboardText(DiagnosticText()); diagnostic = "診断をコピーしました"; }
        if (ImGui.CollapsingHeader("接続・移動の診断詳細")) ImGui.TextWrapped(DiagnosticText());
        if (ImGui.Button("直近の移動診断を消去")) movement.Clear();
        if (diagnostic.Length > 0) ImGui.TextWrapped(diagnostic);
        if (trace?.Dropped > 0 || trace?.Error.Length > 0) ImGui.TextWrapped($"診断の欠落 {trace?.Dropped} / {trace?.Error}");
    }
    public void Dispose() { Disconnect(); trace?.Dispose(); trace = null; }
}
