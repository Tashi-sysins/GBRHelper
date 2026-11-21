using System.Numerics;
using GBRHelper.Navigation;

// Only the external game/IPC boundary is stubbed. Tests execute the production queue and slice scheduler.
namespace GBRHelper
{
    public static class Svc
    {
        public static FrameworkStub Framework = new();
        public static PluginInterfaceStub PluginInterface = new();
    }
    public sealed class FrameworkStub { public bool IsInFrameworkUpdateThread = true; }
    public sealed class PluginInterfaceStub
    {
        public bool Ready = true;
        public Func<Vector3, Vector3?> Nearest = p => p;
        public IpcStub<bool> GetIpcSubscriber<T>(string name) => new(() => Ready);
        public IpcNearestStub GetIpcSubscriber<T1,T2,T3,T4>(string name) => new(Nearest);
        public IpcFindStub GetIpcSubscriber<T1,T2,T3,T4,T5>(string name) => new();
    }
    public sealed class IpcStub<T>(Func<T> query) { public T InvokeFunc() => query(); }
    public sealed class IpcNearestStub(Func<Vector3, Vector3?> query)
    { public Vector3? InvokeFunc(Vector3 p, float xz, float y) => query(p); }
    public sealed class IpcFindStub
    { public Task<List<Vector3>> InvokeFunc(Vector3 a, Vector3 b, bool fly, CancellationToken cancel) => Task.FromResult(new List<Vector3> { a,b }); }
}

namespace GBRHelper.Navigation
{
    public sealed class NativeCollisionProbe
    {
        public static long? Stamp = 1;
        public static int BodyCalls;
        public static Func<Vector3, Vector3, ProbeResult> Body = (_, _) => ProbeResult.Clear;
        public long? CoverageStamp => Stamp;
        public ProbeCode CoverageCode => ProbeCode.Unloaded;
        public object Metrics => new { Stub = true, BodyCalls };
        public ProbeResult Flight(Vector3 a, Vector3 b) => Body(a,b);
        public ProbeResult GroundBody(Vector3 a, Vector3 b) { BodyCalls++; return Body(a,b); }
    }
}
