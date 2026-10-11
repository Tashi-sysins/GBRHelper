// Adapted from SmoothNav.Dalamud/GameCollisionProbe.cs at 4548808fc485bc597865384d1175f38d5f37846f.
// Ground body checks are new GBRHelper code.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace GBRHelper.Navigation;

public sealed unsafe class NativeCollisionProbe
{
    private readonly List<(float MinX, float MinZ, float MaxX, float MaxZ, bool Loaded)> pieces = new();
    private readonly List<(Vector3 Min, Vector3 Max)> pending = new();
    private bool coverageValid;
    private long frame = -1;
    private SceneWrapper* scene;
    private static readonly Vector3[] CoverageOffsets = [Vector3.Zero, new(1.5f, 0, 1.5f), new(-1.5f, 0, 1.5f), new(1.5f, 0, -1.5f), new(-1.5f, 0, -1.5f)];
    private double prepareMs, coverageMs, rayMs;
    private int rayCount;
    private long epoch;
    private int lastFingerprint;
    private int coverageFingerprint;
    public ProbeCode CoverageCode { get; private set; } = ProbeCode.Unloaded;
    public long? CoverageStamp => Prepare() ? epoch : null;
    public object Metrics => new { Frame = frame, PrepareMilliseconds = prepareMs, CoverageMilliseconds = coverageMs,
        RayMilliseconds = rayMs, Rays = rayCount, Pieces = pieces.Count, Pending = pending.Count, CoverageCode, Epoch = epoch };

    private bool Prepare()
    {
        if (!Svc.Framework.IsInFrameworkUpdateThread) return false;
        var now = Svc.Framework.LastUpdate.Ticks;
        if (frame == now) return coverageValid;
        prepareMs = coverageMs = rayMs = 0; rayCount = 0; CoverageCode = ProbeCode.Unloaded;
        var started = Stopwatch.GetTimestamp();
        var valid = PrepareFrame(now);
        prepareMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!valid) { epoch++; return false; }
        CoverageCode = ProbeCode.None;
        if (coverageFingerprint != lastFingerprint) { epoch++; lastFingerprint = coverageFingerprint; }
        return true;
    }
    private bool PrepareFrame(long now)
    {
        frame = now; coverageValid = false; scene = null;
        pieces.Clear(); pending.Clear();
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null || framework->BGCollisionModule->ShuttingDown) return false;
        var manager = framework->BGCollisionModule->SceneManager;
        if (manager == null || manager->NumScenes <= 0 || manager->FirstScene == null) return false;
        scene = manager->FirstScene;
        if (scene->Scene == null) return false;
        var identity = new HashCode(); identity.Add((nint)scene); identity.Add((nint)scene->Scene);
        var started = Stopwatch.GetTimestamp(); var visited = 0;
        foreach (var collider in scene->Scene->Colliders)
        {
            if (++visited > 20000) { CoverageCode = ProbeCode.Limit; return false; }
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 1) { CoverageCode = ProbeCode.TimeBudget; return false; }
            if (collider == null) continue;
            identity.Add((nint)collider);
            if (collider->GetColliderType() == ColliderType.Streamed)
            {
                var s = (ColliderStreamed*)collider;
                if (!s->Loaded || s->Header == null || s->Elements == null || s->Header->NumMeshes is < 0 or > 100000)
                { var piece = (s->StreamedMinX, s->StreamedMinZ, s->StreamedMaxX, s->StreamedMaxZ, false); pieces.Add(piece); identity.Add(piece); continue; }
                for (var i = 0; i < s->Header->NumMeshes; i++)
                {
                    if (pieces.Count >= 100000) { CoverageCode = ProbeCode.Limit; return false; }
                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 1) { CoverageCode = ProbeCode.TimeBudget; return false; }
                    var e = s->Elements + i;
                    var piece = (e->MinX, e->MinZ, e->MaxX, e->MaxZ, e->Mesh != null && e->Mesh->Loaded);
                    pieces.Add(piece); identity.Add(piece); identity.Add((nint)e->Mesh);
                }
            }
            else if (collider->GetColliderType() == ColliderType.Mesh)
            {
                var m = (ColliderMesh*)collider;
                identity.Add(m->Loaded); identity.Add(collider->LoadInProgress());
                identity.Add(m->WorldBoundingBox.Min); identity.Add(m->WorldBoundingBox.Max);
                if (m->Loaded && !collider->LoadInProgress()) continue;
                var bb = m->WorldBoundingBox;
                if (!Geometry.Finite(bb.Min) || !Geometry.Finite(bb.Max) || bb.Max.X <= bb.Min.X || bb.Max.Z <= bb.Min.Z) return false;
                pending.Add((bb.Min, bb.Max));
            }
        }
        // Missing coverage data cannot prove that an empty ray is clear.
        coverageValid = pieces.Count > 0;
        coverageFingerprint = identity.ToHashCode();
        return coverageValid;
    }
    private bool Loaded(Vector3 a, Vector3 b)
    {
        if (!Prepare()) return false;
        CoverageCode = ProbeCode.Unloaded;
        var started = Stopwatch.GetTimestamp();
        try { var loaded = LoadedCore(a, b); if (loaded) CoverageCode = ProbeCode.None; return loaded; }
        finally { coverageMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }
    private bool LoadedCore(Vector3 a, Vector3 b)
    {
        var started = Stopwatch.GetTimestamp();
        var min = Vector3.Min(a, b) - new Vector3(1.5f); var max = Vector3.Max(a, b) + new Vector3(1.5f);
        foreach (var p in pieces)
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 1) { CoverageCode = ProbeCode.TimeBudget; return false; }
            if (!p.Loaded && p.MaxX >= min.X && p.MinX <= max.X && p.MaxZ >= min.Z && p.MinZ <= max.Z) return false;
        }
        foreach (var p in pending)
            if (p.Max.X >= min.X && p.Min.X <= max.X && p.Max.Y >= min.Y && p.Min.Y <= max.Y && p.Max.Z >= min.Z && p.Min.Z <= max.Z) return false;
        // Positive coverage at both ends and along the line, not only the absence of pending geometry.
        var steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(a, b) / 4));
        for (var i = 0; i <= steps; i++)
        {
            var center = Vector3.Lerp(a, b, i / (float)steps);
            foreach (var offset in CoverageOffsets)
            {
                var point = center + offset; var covered = false;
                foreach (var p in pieces)
                {
                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 1) { CoverageCode = ProbeCode.TimeBudget; return false; }
                    if (p.Loaded && point.X >= p.MinX && point.X <= p.MaxX && point.Z >= p.MinZ && point.Z <= p.MaxZ) { covered = true; break; }
                }
                if (!covered) return false;
            }
        }
        return true;
    }
    public ProbeResult Flight(Vector3 a, Vector3 b)
    {
        if (!Geometry.Finite(a) || !Geometry.Finite(b) || Vector3.Distance(a, b) > 256) return ProbeResult.Unknown("飛行照会範囲外", ProbeCode.InvalidInput);
        if (!Loaded(a + Vector3.UnitY, b + Vector3.UnitY)) return ProbeResult.Unknown("当たり判定の読み込み範囲を確認できません", CoverageCode);
        var delta = b - a; var length = delta.Length();
        if (length < .001f) return ProbeResult.Clear;
        var direction = delta / length;
        var axis = Math.Abs(direction.Y) > .95f ? Vector3.UnitX : Vector3.UnitY;
        var side = Vector3.Normalize(Vector3.Cross(direction, axis)) * .5f;
        var up = Vector3.Normalize(Vector3.Cross(side, direction)) * .5f;
        ProbeResult? uncertain = null;
        ReadOnlySpan<Vector3> offsets = stackalloc Vector3[] { Vector3.UnitY, Vector3.UnitY + side, Vector3.UnitY - side, Vector3.UnitY + up, Vector3.UnitY - up };
        foreach (var offset in offsets)
        {
            var result = Ray(a + offset, direction, length, true);
            if (result.ReliableBlock) return result;
            if (result.Verdict == Verdict.Unknown) uncertain = result;
        }
        return uncertain ?? ProbeResult.Clear;
    }
    public ProbeResult GroundBody(Vector3 a, Vector3 b)
    {
        if (!Loaded(a, b)) return ProbeResult.Unknown("地上の当たり判定を確認できません", CoverageCode);
        var delta = b - a; var length = delta.Length();
        if (length < .001f) return ProbeResult.Clear;
        var direction = delta / length;
        var side = Vector3.Cross(direction, Vector3.UnitY);
        if (side.LengthSquared() < .001f) return ProbeResult.Unknown("急な地面の変化", ProbeCode.AmbiguousSurface);
        side = Vector3.Normalize(side) * .35f;
        // Ground-only rules: torso rays follow mesh height and never apply fly-through materials.
        ProbeResult? uncertain = null;
        ReadOnlySpan<Vector3> offsets = stackalloc Vector3[] { Vector3.UnitY * .75f, Vector3.UnitY * 1.25f, Vector3.UnitY + side, Vector3.UnitY - side };
        foreach (var offset in offsets)
        {
            var result = Ray(a + offset, direction, length, false);
            if (result.ReliableBlock) return result;
            if (result.Verdict == Verdict.Unknown) uncertain = result;
        }
        return uncertain ?? ProbeResult.Clear;
    }
    private ProbeResult Ray(Vector3 start, Vector3 direction, float length, bool fly)
    {
        var started = Stopwatch.GetTimestamp();
        try { return RayCore(start, direction, length, fly); }
        finally { rayMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }
    private ProbeResult RayCore(Vector3 start, Vector3 direction, float length, bool fly)
    {
        var traveled = 0f;
        for (var i = 0; i <= 4; i++)
        {
            RaycastHit hit = default;
            var origin = new Vector4(start, 0); var d = direction; var distance = length - traveled;
            var filter = new RaycastMaterialFilter();
            var args = new RaycastParams { Algorithm = 0, Origin = &origin, Direction = &d, MaxDistance = &distance, MaterialFilter = &filter };
            rayCount++;
            if (!scene->Raycast(&hit, 1, &args)) return ProbeResult.Clear;
            if (!Geometry.Finite(hit.Point) || !float.IsFinite(hit.Distance) || hit.Distance < 0 || hit.Distance > distance + .01f)
                return ProbeResult.Unknown("不正な当たり判定結果", ProbeCode.InvalidInput);
            if (fly && FlyThrough(hit.Material))
            {
                var advance = hit.Distance + .05f; traveled += advance; start += direction * advance;
                if (traveled >= length) return ProbeResult.Clear;
                continue;
            }
            var normal = hit.Normal;
            if (!Geometry.Finite(normal) || normal.LengthSquared() < .25f) normal = Vector3.Cross(hit.V2 - hit.V1, hit.V3 - hit.V1);
            if (!Geometry.Finite(normal) || normal.LengthSquared() < .000001f) return ProbeResult.Unknown("衝突面の法線不明", ProbeCode.AmbiguousSurface);
            normal = Vector3.Normalize(normal);
            if (!fly && Math.Abs(normal.Y) > .45f) return ProbeResult.Unknown("床・段差と壁を区別できません", ProbeCode.AmbiguousSurface);
            return new(Verdict.Blocked, fly ? "飛行身体線が衝突面と交差" : "地面に沿った身体線が壁と交差", EvidenceKind.Collision, hit.Point, normal, ProbeCode.Collision);
        }
        return ProbeResult.Unknown("通過材質の検査回数上限", ProbeCode.Limit);
    }
    private static bool FlyThrough(ulong material)
        => (material & 0x100000) == 0x100000 || (material & 0x1000000) == 0x1000000
            || (material & 0x800000) == 0x800000 || (material & 0xB400) == 0xB400;
}
