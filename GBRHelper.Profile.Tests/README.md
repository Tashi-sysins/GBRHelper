# Profile conflict regression checks

Run from the repository root:

```powershell
dotnet run --project .\GBRHelper.Profile.Tests\GBRHelper.Profile.Tests.csproj
```

This executable links the production conflict, ownership, catalog, completion,
stock and unlock planning sources. It checks both disabled-item regressions,
disabled lists, write scopes, ownership, mixed enabled/disabled occurrences and
the resulting stock/unlock plans. A failed assertion returns exit code 1.

`Fixtures.cs` replaces only the IPC `ListSummary` data contract, UI naming
constants and game completion effects. Reflection, native calls, actual inventory,
loaded plugins and configuration writes are outside this test's scope. It needs
the .NET 10 SDK and no NuGet packages or running game.
