using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using GBRHelper.Ipc;

namespace Dalamud.Game.ClientState.Objects.Enums { public enum ObjectKind { None, Aetheryte, EventObj } }
namespace Dalamud.Game.ClientState.Objects.Types
{
    public interface IGameObject
    {
        ObjectKind ObjectKind { get; }
        bool IsTargetable { get; }
        Vector3 Position { get; }
        ulong GameObjectId { get; }
        uint DataId { get; }
        uint BaseId { get; }
    }
}
namespace VendorTests
{
    public sealed class Source : IGameObject
    {
        public ObjectKind ObjectKind { get; set; } = ObjectKind.Aetheryte;
        public bool IsTargetable { get; set; } = true;
        public Vector3 Position { get; set; } = new(-84.031f, 20.767f, .015f);
        public ulong GameObjectId { get; set; } = 100;
        public uint DataId { get; set; } = 1;
        public uint BaseId { get; set; } = 2;
    }
    public sealed class Environment : IVendorAethernetEnvironment
    {
        public bool MayAssist { get; set; } = true;
        public bool Current { get; set; } = true;
        public bool Moving, Busy, MotionReadable = true, BusyReadable = true, MoveAccepted = true, ThrowMove;
        public int Moves;
        public Vector3 LastMove;
        public DateTime UtcNow { get; set; } = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        public uint Territory { get; set; } = 129;
        public List<string> Logs { get; } = [];
        public bool IsCurrent(object owner) => Current;
        public bool TryGetMotion(out bool moving) { moving = Moving; return MotionReadable; }
        public bool TryGetLifestreamBusy(out bool busy) { busy = Busy; return BusyReadable; }
        public bool TryMoveTo(Vector3 position)
        {
            Moves++; LastMove = position;
            if (ThrowMove) throw new InvalidOperationException("move error");
            return MoveAccepted;
        }
        public void Record(string message) => Logs.Add(message);
    }
    public sealed class Cleanup(Action cleanup) : IDisposable { public void Dispose() => cleanup(); }
}
namespace GatherBuddy.Vulcan.Vendors
{
    public sealed class VendorNavigator
    {
        private string? _pendingAethernetName = "upper decks";
        private string? _pendingHousingShardTeleportName = "housing shard";
        private uint _pendingAethernetParentTerritoryId = 129;
        private DateTime _teleportWaitStartTime = new(2026,11,21,0,0,0,DateTimeKind.Utc);
        public bool IsActive { get; set; } = true;
        public IGameObject? Source { get; set; } = new VendorTests.Source();
        public float Horizontal { get; set; } = 10.84f;
        public float Distance { get; set; } = 11.05f;
        public bool SourceKnown { get; set; } = true;
        public int OriginalCalls, Stops;
        public bool OriginalThrows;
        public static bool VendorReady = true;
        public bool HousingReady = true;
        public VendorTests.Environment? Env;
        public void SetRoute(string? local, string? housing, uint territory)
        { _pendingAethernetName = local; _pendingHousingShardTeleportName = housing; _pendingAethernetParentTerritoryId = territory; }
        public void RestartSameRoute() => _teleportWaitStartTime = _teleportWaitStartTime.AddSeconds(1);
        private bool TryGetNearestAethernetSource(out IGameObject? source, out float horizontal, out float distance)
        { source = Source; horizontal = Horizontal; distance = Distance; return SourceKnown; }
        private static bool CanIssueTeleportRequest() => VendorReady;
        private bool CanIssueHousingEntryAction() => HousingReady;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool HandlePendingLocalAethernetTeleport() => Original();
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool HandlePendingHousingShardTeleport() => Original();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool RunLocal() => HandlePendingLocalAethernetTeleport();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool RunHousing() => HandlePendingHousingShardTeleport();
        public bool Original()
        {
            OriginalCalls++;
            if (Env?.Moving == true) { Stops++; Env.Moving = false; }
            if (OriginalThrows) throw new InvalidOperationException("original error");
            return true;
        }
    }
}
