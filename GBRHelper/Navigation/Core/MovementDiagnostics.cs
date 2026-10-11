using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GBRHelper.Navigation;

public sealed record MovementSnapshot(DateTimeOffset Time, string Phase, string Code, string Reason, RouteRequest? Request,
    Vector3 Position, Vector3? ConnectionTo, bool Flying, bool Mounting, int Stage, int? RemainingCount, int LandCount,
    ProbeResult? Probe, RouteDisposition? Disposition, bool OriginalBlocked, int Searches,
    double? ElapsedMilliseconds = null, double? BudgetMilliseconds = null, string? StopResult = null,
    AcquisitionSample? Acquisition = null, object? NativeMetrics = null);

/// <summary>Memory evidence survives disk failure, stop/reset and reconnect; character changes clear it explicitly.</summary>
public sealed class MovementDiagnostics
{
    public MovementSnapshot? Current { get; private set; }
    public MovementSnapshot? LastFailure { get; private set; }
    public void Record(MovementSnapshot snapshot, bool failure)
    { Current = snapshot; if (failure) LastFailure = snapshot; }
    public void CompleteStop(RouteRequest request, string result)
    {
        if (LastFailure?.Request == request)
        {
            LastFailure = LastFailure with { StopResult = result };
            if (Current?.Request == request) Current = LastFailure;
        }
    }
    public void ResetCurrent() => Current = null;
    public void Clear() { Current = LastFailure = null; }
    public string ToText(string? path, long dropped, string? storageError, AcquisitionSample? acquisition)
        => JsonSerializer.Serialize(new { Current, LastFailure, Acquisition = acquisition, TracePath = path, Dropped = dropped, StorageError = storageError }, Options);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, IncludeFields = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
