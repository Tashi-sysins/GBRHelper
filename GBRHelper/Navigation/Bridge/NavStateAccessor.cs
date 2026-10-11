using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

public sealed record NavSnapshot(Task<List<Vector3>>? Task, CancellationTokenSource? Cancellation,
    Vector3 Destination, bool Flying, bool Mounting, bool Direct, bool Offset, int Stage, long LastTry, int LandCount)
{
    public bool Combined => !Direct && Stage is 0 or 2;
}

/// <summary>Reads/writes the complete boxed ValueTuple, retaining every member other than Item1.</summary>
public sealed class NavStateAccessor
{
    private readonly FieldInfo state;
    private readonly FieldInfo[] fields;
    private readonly FieldInfo rest;
    private readonly FieldInfo[] tail;
    public NavStateAccessor(Type type)
    {
        state = type.GetField("_navState", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException("_navState");
        fields = new FieldInfo[7];
        for (var i = 0; i < 7; i++) fields[i] = state.FieldType.GetField("Item" + (i + 1)) ?? throw new MissingFieldException("tuple Item");
        rest = state.FieldType.GetField("Rest") ?? throw new MissingFieldException("tuple Rest");
        tail = new FieldInfo[3];
        for (var i = 0; i < 3; i++) tail[i] = rest.FieldType.GetField("Item" + (i + 1)) ?? throw new MissingFieldException("tuple tail");
        if (fields[0].FieldType != typeof(Task<List<Vector3>>) || fields[1].FieldType != typeof(CancellationTokenSource)
            || fields[2].FieldType != typeof(Vector3) || !tail[0].FieldType.IsEnum || tail[1].FieldType != typeof(long)
            || tail[2].FieldType != typeof(int)) throw new NotSupportedException("GBR navigation tuple signature");
        for (var i = 3; i < 7; i++) if (fields[i].FieldType != typeof(bool)) throw new NotSupportedException("tuple bool signature");
    }
    public NavSnapshot Read(object owner)
    {
        var box = state.GetValue(owner)!; var r = rest.GetValue(box)!;
        return new((Task<List<Vector3>>?)fields[0].GetValue(box), (CancellationTokenSource?)fields[1].GetValue(box),
            (Vector3)fields[2].GetValue(box)!, (bool)fields[3].GetValue(box)!, (bool)fields[4].GetValue(box)!,
            (bool)fields[5].GetValue(box)!, (bool)fields[6].GetValue(box)!, Convert.ToInt32(tail[0].GetValue(r)),
            (long)tail[1].GetValue(r)!, (int)tail[2].GetValue(r)!);
    }
    // Called only on the owning framework thread, never by a continuation.
    public bool Replace(object owner, Task<List<Vector3>> expected, CancellationTokenSource cancellation, Task<List<Vector3>> replacement)
    {
        var box = state.GetValue(owner)!;
        if (!ReferenceEquals(fields[0].GetValue(box), expected) || !ReferenceEquals(fields[1].GetValue(box), cancellation)) return false;
        fields[0].SetValue(box, replacement);
        state.SetValue(owner, box);
        return true;
    }
}
