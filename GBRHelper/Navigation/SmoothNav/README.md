# SmoothNav の写し

移動の部品 SmoothNav の Core の写し（無改変。先頭に出典の行と標準の using を足しただけ）。原本のコミットは `4548808`。

- GBRHelper が使うのは、角を曲線にする `CurveBuilder` と地上の歩ける面の窓口 `MeshPointGround`。
  つなぎ口は `Navigation/Core/SmoothNavCurve.cs`（GBRHelper の照会を SmoothNav の窓口の形にする）。
- ここのファイルは直接直さず、原本で直してから写し直す。名前空間は原本のまま `SmoothNav.Core`。

| 写し | 原本 | 原本の SHA256（先頭16文字・改行は LF） |
|---|---|---|
| Collision.cs | SmoothNav.Core/Collision.cs | ba74bcab375d3c06 |
| CurveBuilder.cs | SmoothNav.Core/CurveBuilder.cs | b181d1687eea0d64 |
| FollowerSimulation.cs | SmoothNav.Core/FollowerSimulation.cs | d756c4e9b097282d |
| FrameworkQueue.cs | SmoothNav.Core/FrameworkQueue.cs | 5fdf878e100d02f5 |
| GroundSurface.cs | SmoothNav.Core/GroundSurface.cs | 66fcc1fc93c139a4 |
| LoadedCoverage.cs | SmoothNav.Core/LoadedCoverage.cs | f41d171b54f2c99f |
| MeshPointGround.cs | SmoothNav.Core/MeshPointGround.cs | f5ccdee396a65e16 |
| Metrics.cs | SmoothNav.Core/Metrics.cs | fe3ae64035b2c68d |
| MovementSession.cs | SmoothNav.Core/MovementSession.cs | 559b0cac1830ee5f |
| PathMarker.cs | SmoothNav.Core/PathMarker.cs | 1bd7a394bf5b9e3b |
| PathRefiner.cs | SmoothNav.Core/PathRefiner.cs | 95f869e30daa0f82 |
| RoutePlanner.cs | SmoothNav.Core/RoutePlanner.cs | 846d51f073c633cf |
| WindowMetrics.cs | SmoothNav.Core/WindowMetrics.cs | d9d4fd5dedf2d547 |
