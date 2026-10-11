using System;

namespace GBRHelper.Navigation;

public sealed record AcquisitionSample(long Attempt, uint RequestedItem, uint? ActualItem, int? Before, int? After,
    int OperationsObserved, string Classification);

/// <summary>Observation never drives gathering, list completion, or navigation.</summary>
public sealed class GatherAcquisitionObserver
{
    private long attempt;
    private uint requested;
    private int? before;
    private bool wasExecuting;
    private int operations;
    private bool uncontrolled;
    public AcquisitionSample? Last { get; private set; }
    public void Begin(long attemptId, uint requestedItem, int? count)
    {
        if (attempt == attemptId && requested == requestedItem) return;
        attempt = attemptId; requested = requestedItem; before = count; operations = 0; wasExecuting = false; uncontrolled = false;
        Last = new(attempt, requested, null, before, count, 0, "AcquisitionUnknown");
    }
    public AcquisitionSample? Sample(int? count, bool executing, bool controlledTest)
    {
        if (attempt == 0) return null;
        uncontrolled |= !controlledTest;
        if (executing && !wasExecuting) operations++;
        wasExecuting = executing;
        var increased = before.HasValue && count.HasValue && count > before;
        Last = new(attempt, requested, increased ? requested : null, before, count, operations,
            increased && operations > 0 && controlledTest && !uncontrolled ? "ControlledInventoryDelta" : "AcquisitionUnknown");
        return Last;
    }
    public void Reset() { attempt = 0; requested = 0; before = null; operations = 0; wasExecuting = false; Last = null; }
}
