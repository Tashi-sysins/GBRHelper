using System;
using System.Numerics;

namespace GBRHelper.Navigation;

public readonly record struct VendorApproachIdentity(
    uint Territory, string Route, ulong ObjectId, uint DataId, uint BaseId, Vector3 Position);

public enum VendorApproachAction { Original, Wait, Move }

/// <summary>Bounds the helper's intervention without changing any GBR navigation state.</summary>
public sealed class VendorApproachPolicy
{
    public const float RecognitionDistance = 10.5f;
    public const int MaximumAttempts = 12;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumActiveWait = TimeSpan.FromSeconds(30);

    private VendorApproachIdentity? identity;
    private DateTime? previous;
    private DateTime nextAttempt;
    private TimeSpan elapsed;
    private bool exhausted;
    public int Attempts { get; private set; }
    public bool Exhausted => exhausted;

    public static bool IsRecognitionGap(float horizontal, float distance)
        => float.IsFinite(horizontal) && float.IsFinite(distance)
        && horizontal >= 0 && distance >= horizontal
        && horizontal <= 11f && distance <= 15f
        && (horizontal > RecognitionDistance || distance > RecognitionDistance);

    public VendorApproachAction Update(DateTime now, VendorApproachIdentity source, bool moving)
    {
        if (identity != source)
        {
            Reset();
            identity = source;
        }
        if (previous is { } last && now > last) elapsed += now - last;
        previous = now;
        if (exhausted) return VendorApproachAction.Original;
        if (elapsed >= MaximumActiveWait || (!moving && Attempts >= MaximumAttempts))
        {
            exhausted = true;
            return VendorApproachAction.Original;
        }
        if (moving || now < nextAttempt) return VendorApproachAction.Wait;
        Attempts++;
        nextAttempt = now + RetryInterval; // Failed issuance also consumes the retry interval.
        return VendorApproachAction.Move;
    }

    public void Pause() => previous = null;

    public void Reset()
    {
        identity = null;
        previous = null;
        nextAttempt = DateTime.MinValue;
        elapsed = TimeSpan.Zero;
        Attempts = 0;
        exhausted = false;
    }
}
