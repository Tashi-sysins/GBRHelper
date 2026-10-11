using System;
using System.Collections.Generic;
using System.Numerics;

namespace GBRHelper.Navigation;

public enum ProgressDecision { None, Replan, Exhausted }
/// <summary>Finite, per-target recovery accounting across navigation generations. Normal waiting resets the clock.</summary>
public sealed class ProgressWatch
{
    private string current = "";
    private Vector3 anchor;
    private long since;
    private readonly Dictionary<string, int> retries = new();
    public ProgressDecision Observe(RouteRequest request, Vector3 position, long now, double thresholdSeconds, bool eligible)
    {
        // A two-metre cell avoids treating sub-centimetre changes as a new recovery budget.
        var key = $"{request.Territory}/{request.ItemId}/{MathF.Round(request.Destination.X / 2)}/{MathF.Round(request.Destination.Z / 2)}";
        if (!eligible || !Geometry.Finite(position)) { current = ""; return ProgressDecision.None; }
        if (key != current || Geometry.Horizontal(position, anchor) >= 2)
        { current = key; anchor = position; since = now; return ProgressDecision.None; }
        if (!double.IsFinite(thresholdSeconds) || thresholdSeconds <= 0 || now - since < Math.Max(1000, thresholdSeconds * 750))
            return ProgressDecision.None;
        since = now;
        if (retries.GetValueOrDefault(key) >= 1) return ProgressDecision.Exhausted;
        // If accounting fills up, terminate instead of evicting budgets to permit an unbounded loop.
        if (retries.Count >= 32 && !retries.ContainsKey(key)) return ProgressDecision.Exhausted;
        retries[key] = retries.GetValueOrDefault(key) + 1;
        return ProgressDecision.Replan;
    }
    public void ResetTracking() => current = "";
}
