using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using GBRHelper.Navigation;

namespace GBRHelper.Ipc;

/// <summary>Transfers ownership of failed rollback handles to the feature for cleanup retries.</summary>
public sealed class VendorAethernetRegistrationException : AggregateException
{
    public VendorAethernetBridge PendingBridge { get; }
    public VendorAethernetRegistrationException(VendorAethernetBridge pendingBridge,
        Exception registrationFailure, Exception cleanupFailure)
        : base("都市内転送補助の登録と解除に失敗しました", registrationFailure, cleanupFailure)
        => PendingBridge = pendingBridge;
}

public interface IVendorAethernetEnvironment
{
    bool MayAssist { get; }
    bool IsCurrent(object owner);
    bool TryGetMotion(out bool moving);
    bool TryGetLifestreamBusy(out bool busy);
    bool TryMoveTo(Vector3 position);
    DateTime UtcNow { get; }
    uint Territory { get; }
    void Record(string message);
}

/// <summary>
/// Intercepts only the two vendor source-approach handlers of a separately validated GBR.
/// Neither files nor GBR fields are written. Existing GBR movement continues through the
/// narrow recognition gap; the original handler resumes after reaching the inner radius.
/// </summary>
public sealed class VendorAethernetBridge : IDisposable
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string NavigatorType = "GatherBuddy.Vulcan.Vendors.VendorNavigator";
    private readonly IVendorAethernetEnvironment env;
    private readonly MethodInfo nearest, canLeaveVendor, housingActionReady;
    private readonly PropertyInfo active;
    private readonly FieldInfo localName, housingName, parentTerritory, routeStarted;
    private readonly List<IDisposable> hooks = new();
    private readonly VendorApproachPolicy policy = new();
    private bool disposed, exhaustionRecorded, errorRecorded;
    private object? observedOwner;
    private string? observedRoute;
    private bool observedHousing;
    private uint observedTerritory;
    private DateTime observedStarted;
    public int HookCount => hooks.Count;
    private delegate bool Original(object self);

    public VendorAethernetBridge(Assembly gbr, IVendorAethernetEnvironment environment,
        Func<MethodInfo, Delegate, IDisposable> makeHook)
    {
        env = environment;
        var type = gbr.GetType(NavigatorType, throwOnError: true)!;
        active = type.GetProperty("IsActive", Instance)
            ?? throw new MissingMemberException(NavigatorType, "IsActive");
        if (active.PropertyType != typeof(bool)) throw new NotSupportedException("VendorNavigator.IsActive signature");
        localName = Field(type, "_pendingAethernetName", typeof(string));
        housingName = Field(type, "_pendingHousingShardTeleportName", typeof(string));
        parentTerritory = Field(type, "_pendingAethernetParentTerritoryId", typeof(uint));
        routeStarted = Field(type, "_teleportWaitStartTime", typeof(DateTime));
        nearest = type.GetMethod("TryGetNearestAethernetSource", Instance, null,
            [typeof(IGameObject).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType()], null)
            ?? throw new MissingMethodException(NavigatorType, "TryGetNearestAethernetSource");
        if (nearest.ReturnType != typeof(bool)) throw new NotSupportedException("Aethernet source getter signature");
        var local = Handler(type, "HandlePendingLocalAethernetTeleport");
        var housing = Handler(type, "HandlePendingHousingShardTeleport");
        canLeaveVendor = type.GetMethod("CanIssueTeleportRequest", BindingFlags.NonPublic | BindingFlags.Static,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(NavigatorType, "CanIssueTeleportRequest");
        if (canLeaveVendor.ReturnType != typeof(bool)) throw new NotSupportedException("Vendor exit readiness signature");
        housingActionReady = Handler(type, "CanIssueHousingEntryAction");
        try
        {
            hooks.Add(makeHook(local, (Func<Original, object, bool>)Local));
            hooks.Add(makeHook(housing, (Func<Original, object, bool>)Housing));
        }
        catch (Exception failure)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new VendorAethernetRegistrationException(this, failure, cleanup); }
            throw;
        }
    }

    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = type.GetField(name, Instance) ?? throw new MissingFieldException(type.FullName, name);
        if (field.FieldType != expected) throw new NotSupportedException(name + " signature");
        return field;
    }

    private static MethodInfo Handler(Type type, string name)
    {
        var method = type.GetMethod(name, Instance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(type.FullName, name);
        if (method.ReturnType != typeof(bool)) throw new NotSupportedException(name + " signature");
        return method;
    }

    private bool Local(Original original, object self) => Dispatch(original, self, false);
    private bool Housing(Original original, object self) => Dispatch(original, self, true);

    /// <summary>Called each framework update, including frames when GBR skips its handlers.</summary>
    public void Observe()
    {
        if (disposed || observedOwner is not { } owner) return;
        try
        {
            if (!env.IsCurrent(owner) || active.GetValue(owner) is not true
                || (observedHousing ? housingName : localName).GetValue(owner) as string != observedRoute
                || env.Territory != observedTerritory
                || (DateTime)routeStarted.GetValue(owner)! != observedStarted)
            {
                Reset();
                return;
            }
            if (!env.MayAssist || !env.TryGetLifestreamBusy(out var busy) || busy
                || canLeaveVendor.Invoke(null, null) is not true
                || (observedHousing && housingActionReady.Invoke(owner, null) is not true))
                policy.Pause();
        }
        catch (Exception ex)
        {
            policy.Pause();
            if (!errorRecorded)
            {
                errorRecorded = true;
                Record("都市内転送補助の状態確認を見送りました: " + ex.Message);
            }
        }
    }

    private bool Dispatch(Original original, object self, bool housing)
    {
        // Only the inspection/intervention is caught. Never catch and retry an original call.
        var intercepted = false;
        try { intercepted = TryAssist(self, housing); }
        catch (Exception ex)
        {
            policy.Pause();
            if (!errorRecorded)
            {
                errorRecorded = true;
                Record("都市内転送の接近補助を見送りました: " + ex.Message);
            }
        }
        return intercepted || original(self);
    }

    private bool TryAssist(object self, bool housing)
    {
        if (disposed || !env.IsCurrent(self) || active.GetValue(self) is not true)
        {
            Reset();
            return false;
        }
        var route = (housing ? housingName : localName).GetValue(self) as string;
        if (string.IsNullOrWhiteSpace(route)) { Reset(); return false; }
        var expectedTerritory = (uint)parentTerritory.GetValue(self)!;
        if (expectedTerritory != 0 && expectedTerritory != env.Territory) { Reset(); return false; }
        if (!env.MayAssist || !env.TryGetLifestreamBusy(out var busy) || busy)
        {
            policy.Pause();
            return false;
        }
        // Preserve GBR's shop/dialog/animation-lock checks before assisting either route.
        if (canLeaveVendor.Invoke(null, null) is not true
            || (housing && housingActionReady.Invoke(self, null) is not true))
        {
            policy.Pause();
            return false;
        }

        object?[] args = [null, 0f, 0f];
        if (nearest.Invoke(self, args) is not true || args[0] is not IGameObject source
            || source.ObjectKind != ObjectKind.Aetheryte || !source.IsTargetable
            || args[1] is not float horizontal || args[2] is not float distance)
        {
            Reset();
            return false;
        }
        var position = source.Position;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || source.GameObjectId == 0 || !VendorApproachPolicy.IsRecognitionGap(horizontal, distance))
        {
            Reset();
            return false;
        }
        if (!env.TryGetMotion(out var moving)) { policy.Pause(); return false; }
        var startedAt = (DateTime)routeStarted.GetValue(self)!;
        observedOwner = self;
        observedRoute = route;
        observedHousing = housing;
        observedTerritory = env.Territory;
        observedStarted = startedAt;
        var identity = new VendorApproachIdentity(env.Territory, (housing ? "housing:" : "local:") + route + ":" + startedAt.Ticks,
            source.GameObjectId, source.DataId, source.BaseId, position);
        var previouslyExhausted = policy.Exhausted;
        var action = policy.Update(env.UtcNow, identity, moving);
        if (action == VendorApproachAction.Original)
        {
            if (policy.Exhausted && (!previouslyExhausted || !exhaustionRecorded))
            {
                exhaustionRecorded = true;
                Record("都市内転送の接近補助は上限に達したため停止しました。GBRの従来処理へ戻します。位置とログを確認してください。");
            }
            return false;
        }
        exhaustionRecorded = false;
        if (action == VendorApproachAction.Move)
        {
            var started = env.TryMoveTo(position);
            Record($"都市内転送の接近補助: {(started ? "接近開始" : "移動要求失敗")}, エリア={identity.Territory}, source={identity.ObjectId}, 水平={horizontal:F2}m, 距離={distance:F2}m, 目標={VendorApproachPolicy.RecognitionDistance:F1}m以内, 試行={policy.Attempts}");
        }
        // Keep the original handler from stopping the source approach again at 11 metres.
        return true;
    }

    private void Record(string message)
    {
        try { env.Record(message); } catch { /* Logging cannot affect another plugin. */ }
    }
    private void Reset()
    {
        policy.Reset();
        exhaustionRecorded = false;
        observedOwner = null;
        observedRoute = null;
    }

    public void Dispose()
    {
        if (disposed && hooks.Count == 0) return;
        disposed = true;
        var failures = new List<Exception>();
        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            try { hooks[i].Dispose(); hooks.RemoveAt(i); }
            catch (Exception ex) { failures.Add(ex); }
        }
        Reset();
        if (failures.Count != 0) throw new AggregateException("都市内転送補助の解除に失敗しました", failures);
    }
}
