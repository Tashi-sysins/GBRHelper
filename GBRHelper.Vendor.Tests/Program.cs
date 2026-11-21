using System.Reflection;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using GBRHelper.Ipc;
using GBRHelper.Navigation;
using GatherBuddy.Vulcan.Vendors;
using Env = VendorTests.Environment;
using VendorTests;

var passed = 0;
var failed = 0;
void Check(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Assert(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
var now = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
var id = new VendorApproachIdentity(129, "local:upper", 100, 1, 2, Vector3.Zero);

Check("11.05m height-adjusted gap is assisted", () => Assert(VendorApproachPolicy.IsRecognitionGap(10.84f, 11.05f)));
Check("10.5m boundary resumes original", () => Assert(!VendorApproachPolicy.IsRecognitionGap(10.5f, 10.5f)));
Check("outside original 11m horizontal range is original", () => Assert(!VendorApproachPolicy.IsRecognitionGap(11.01f, 11.1f)));
Check("outside original 15m 3D range is original", () => Assert(!VendorApproachPolicy.IsRecognitionGap(10, 15.01f)));
Check("invalid numeric distances are original", () =>
{
    foreach (var v in new[] {float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f})
        Assert(!VendorApproachPolicy.IsRecognitionGap(v, 11.05f));
    Assert(!VendorApproachPolicy.IsRecognitionGap(10, 9));
});
Check("moving source approach is preserved without another movement request", () =>
{
    var p = new VendorApproachPolicy(); Assert(p.Update(now, id, true) == VendorApproachAction.Wait); Assert(p.Attempts == 0);
});
Check("retry interval also applies to failed requests", () =>
{
    var p = new VendorApproachPolicy(); Assert(p.Update(now, id, false) == VendorApproachAction.Move);
    Assert(p.Update(now.AddSeconds(1.99), id, false) == VendorApproachAction.Wait);
    Assert(p.Update(now.AddSeconds(2), id, false) == VendorApproachAction.Move); Assert(p.Attempts == 2);
});
Check("deadline is not restarted by active movement", () =>
{
    var p = new VendorApproachPolicy(); p.Update(now, id, true);
    Assert(p.Update(now.AddSeconds(30), id, true) == VendorApproachAction.Original && p.Exhausted);
});
Check("paused time does not consume deadline", () =>
{
    var p = new VendorApproachPolicy(); p.Update(now, id, true); p.Update(now.AddSeconds(10), id, true); p.Pause();
    Assert(p.Update(now.AddSeconds(100), id, true) == VendorApproachAction.Wait);
    Assert(p.Update(now.AddSeconds(120), id, true) == VendorApproachAction.Original);
});
Check("attempt limit is finite and remains exhausted", () =>
{
    var p = new VendorApproachPolicy();
    for (int i=0;i<VendorApproachPolicy.MaximumAttempts;i++) Assert(p.Update(now.AddSeconds(i*2), id, false) == VendorApproachAction.Move);
    Assert(p.Update(now.AddSeconds(24), id, false) == VendorApproachAction.Original && p.Exhausted);
    Assert(p.Update(now.AddSeconds(25), id, false) == VendorApproachAction.Original);
});
Check("last accepted movement remains active until it finishes", () =>
{
    var p = new VendorApproachPolicy();
    for (int i=0;i<VendorApproachPolicy.MaximumAttempts;i++) p.Update(now.AddSeconds(i*2), id, false);
    Assert(p.Update(now.AddSeconds(23),id,true)==VendorApproachAction.Wait);
    Assert(p.Update(now.AddSeconds(24),id,false)==VendorApproachAction.Original);
});
Check("source and area changes start independent attempts", () =>
{
    var p = new VendorApproachPolicy(); p.Update(now,id,false);
    Assert(p.Update(now,id with {Territory=130},false)==VendorApproachAction.Move && p.Attempts==1);
    Assert(p.Update(now,id with {ObjectId=101},false)==VendorApproachAction.Move && p.Attempts==1);
});

var hooks = new Dictionary<string,Delegate>();
VendorAethernetBridge Fake(Env env, Func<MethodInfo,Delegate,IDisposable>? factory=null)
    => new(typeof(VendorNavigator).Assembly,env,factory ?? ((m,d)=>{hooks[m.Name]=d; return new Cleanup(()=>hooks.Remove(m.Name));}));
bool Call(VendorNavigator owner, bool housing = false)
{
    var hook=hooks[housing?"HandlePendingHousingShardTeleport":"HandlePendingLocalAethernetTeleport"];
    var originalType=hook.Method.GetParameters()[0].ParameterType;
    var original=Delegate.CreateDelegate(originalType, typeof(OriginalCall).GetMethod(nameof(OriginalCall.Run))!);
    try {return (bool)hook.DynamicInvoke(original, owner)!;}
    catch(TargetInvocationException ex) when(ex.InnerException!=null) {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
}
Check("idle 11.05m gap issues one approach and bypasses GBR stop", () =>
{
    var env=new Env(); var n=new VendorNavigator{Env=env}; using var b=Fake(env);
    Assert(b.HookCount==2 && Call(n)); Assert(env.Moves==1&&n.OriginalCalls==0&&n.Stops==0);
    Call(n); Assert(env.Moves==1);
});
Check("existing movement keeps running in gap", () =>
{
    var env=new Env{Moving=true}; var n=new VendorNavigator{Env=env}; using var b=Fake(env);
    Assert(Call(n)&&env.Moving&&n.Stops==0&&n.OriginalCalls==0&&env.Moves==0);
});
Check("strict inner range delegates once to GBR", () =>
{
    var env=new Env{Moving=true}; var n=new VendorNavigator{Env=env,Horizontal=10,Distance=10.5f}; using var b=Fake(env);
    Assert(Call(n)&&n.OriginalCalls==1&&n.Stops==1&&env.Moves==0);
});
Check("housing handler shares bounded approach", () =>
{
    var env=new Env(); var n=new VendorNavigator{Env=env}; using var b=Fake(env);
    Assert(Call(n,true)&&env.Moves==1&&n.OriginalCalls==0);
});
Check("GBR vendor exit readiness blocks both routes", () =>
{
    var env=new Env();var n=new VendorNavigator{Env=env};using var b=Fake(env);
    VendorNavigator.VendorReady=false;
    try {Call(n);Call(n,true);Assert(n.OriginalCalls==2&&env.Moves==0);}
    finally {VendorNavigator.VendorReady=true;}
});
Check("housing action throttle remains respected", () =>
{
    var env=new Env();var n=new VendorNavigator{Env=env,HousingReady=false};using var b=Fake(env);
    Call(n,true);Assert(n.OriginalCalls==1&&env.Moves==0);
});
foreach (var kind in new[]{"small","unsafe","busy","motion-unreadable","busy-unreadable","unknown-source","not-targetable","wrong-owner","inactive","no-route","wrong-area","invalid-position"})
Check(kind+" uses original once without helper movement", () =>
{
    var env=new Env(); var n=new VendorNavigator{Env=env};
    switch(kind)
    {
        case "small": ((Source)n.Source!).ObjectKind=ObjectKind.EventObj;break;
        case "unsafe":env.MayAssist=false;break;
        case "busy":env.Busy=true;break;
        case "motion-unreadable":env.MotionReadable=false;break;
        case "busy-unreadable":env.BusyReadable=false;break;
        case "unknown-source":n.SourceKnown=false;break;
        case "not-targetable":((Source)n.Source!).IsTargetable=false;break;
        case "wrong-owner":env.Current=false;break;
        case "inactive":n.IsActive=false;break;
        case "no-route":n.SetRoute(null,null,129);break;
        case "wrong-area":env.Territory=130;break;
        case "invalid-position":((Source)n.Source!).Position=new(float.NaN,0,0);break;
    }
    using var b=Fake(env); Assert(Call(n)&&n.OriginalCalls==1&&env.Moves==0);
});
Check("original exceptions are propagated without a second original call", () =>
{
    var env=new Env{MayAssist=false};var n=new VendorNavigator{Env=env,OriginalThrows=true};using var b=Fake(env);
    try {Call(n);throw new Exception("did not throw");}catch(InvalidOperationException ex){Assert(ex.Message=="original error");}
    Assert(n.OriginalCalls==1);
});
Check("failed request remains rate-limited", () =>
{
    var env=new Env{MoveAccepted=false};var n=new VendorNavigator{Env=env};using var b=Fake(env);
    Call(n);Call(n);Assert(env.Moves==1&&n.OriginalCalls==0);
    env.UtcNow=env.UtcNow.AddSeconds(2);Call(n);Assert(env.Moves==2);
});
Check("bounded exhaustion resumes GBR once and logs once", () =>
{
    var env=new Env{Moving=true};var n=new VendorNavigator{Env=env};using var b=Fake(env);
    Call(n);env.UtcNow=env.UtcNow.AddSeconds(30);Call(n);Call(n);
    Assert(n.OriginalCalls==2&&env.Logs.Count==1&&env.Moves==0);
});
Check("partial registration failure disposes earlier hook", () =>
{
    int calls=0,cleaned=0;
    try {using var b=Fake(new Env(),(m,d)=>{if(++calls==2)throw new InvalidOperationException("register");return new Cleanup(()=>cleaned++);});throw new Exception("did not throw");}
    catch(InvalidOperationException ex){Assert(ex.Message=="register");}
    Assert(cleaned==1);
});
Check("dispose attempts all hooks and retains failed handles for retry", () =>
{
    int cleaned=0;bool fail=true;var b=Fake(new Env(),(m,d)=>new Cleanup(()=>{cleaned++;if(fail)throw new InvalidOperationException("cleanup");}));
    try{b.Dispose();throw new Exception("did not throw");}catch(AggregateException ex){Assert(ex.InnerExceptions.Count==2);}
    Assert(cleaned==2&&b.HookCount==2);fail=false;b.Dispose();Assert(cleaned==4&&b.HookCount==0);b.Dispose();Assert(cleaned==4);
});
Check("failed registration and rollback transfer retained handles for safe retry", () =>
{
    int registrations=0,cleanups=0;bool failCleanup=true;
    var env=new Env();var n=new VendorNavigator{Env=env};
    try
    {
        using var b=Fake(env,(m,d)=>
        {
            if(++registrations==2)throw new InvalidOperationException("register");
            hooks[m.Name]=d;
            return new Cleanup(()=>
            {
                cleanups++;
                if(failCleanup)throw new InvalidOperationException("cleanup");
                hooks.Remove(m.Name);
            });
        });
        throw new Exception("did not throw");
    }
    catch(VendorAethernetRegistrationException ex)
    {
        Assert(ex.PendingBridge.HookCount==1&&ex.InnerExceptions.Count==2&&cleanups==1);
        Call(n);Assert(n.OriginalCalls==1&&env.Moves==0); // Retained callbacks are already disabled.
        failCleanup=false;ex.PendingBridge.Dispose();Assert(cleanups==2&&ex.PendingBridge.HookCount==0);
    }
});
Check("per-frame observation pauses 90 busy seconds while GBR skips handler", () =>
{
    var env=new Env{Moving=true};var n=new VendorNavigator{Env=env};using var b=Fake(env);
    Call(n);env.UtcNow=env.UtcNow.AddSeconds(10);Call(n);
    env.Busy=true;b.Observe();env.UtcNow=env.UtcNow.AddSeconds(90);b.Observe();env.Busy=false;b.Observe();
    Call(n);Assert(n.OriginalCalls==0);
    env.UtcNow=env.UtcNow.AddSeconds(20);Call(n);Assert(n.OriginalCalls==1);
});
Check("observed stop resets exhausted budget for same-route restart", () =>
{
    var env=new Env{Moving=true};var n=new VendorNavigator{Env=env};using var b=Fake(env);
    Call(n);env.UtcNow=env.UtcNow.AddSeconds(30);Call(n);Assert(n.OriginalCalls==1);
    n.IsActive=false;b.Observe();n.IsActive=true;Call(n);Assert(n.OriginalCalls==1&&env.Moves==1);
});
Check("same-frame route restart gets new budget from route generation", () =>
{
    var env=new Env{Moving=true};var n=new VendorNavigator{Env=env};using var b=Fake(env);
    Call(n);env.UtcNow=env.UtcNow.AddSeconds(30);Call(n);Assert(n.OriginalCalls==1);
    n.RestartSameRoute();Call(n);Assert(n.OriginalCalls==1&&env.Moves==1);
});
Check("real MonoMod hooks work after handlers have already run", () =>
{
    var env=new Env();var n=new VendorNavigator{Env=env};n.RunLocal();n.RunHousing();Assert(n.OriginalCalls==2);
    ManagedHookRuntime.Initialize(AppContext.BaseDirectory);
    using(var b=new VendorAethernetBridge(typeof(VendorNavigator).Assembly,env,ManagedHookRuntime.CreateHook))
    {
        Assert(b.HookCount==2);n.RunLocal();Assert(env.Moves==1&&n.OriginalCalls==2);
        env.Moving=true;n.RunHousing();Assert(env.Moving&&n.Stops==0&&n.OriginalCalls==2);
        n.Horizontal=10;n.Distance=10.5f;n.RunLocal();Assert(n.OriginalCalls==3&&n.Stops==1);
    }
    n.RunLocal();Assert(n.OriginalCalls==4);
});
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed==0 ? 0 : 1;

public static class OriginalCall { public static bool Run(object self)=>((VendorNavigator)self).Original(); }
