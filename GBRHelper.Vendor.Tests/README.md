# Vendor aethernet approach regression tests

From the repository root, run:

```powershell
dotnet run --project .\GBRHelper.Vendor.Tests\GBRHelper.Vendor.Tests.csproj -c Release
```

Requires .NET 10 and the same MonoMod.RuntimeDetour 25.3.6 package used by GBRHelper. The tests link the production `VendorAethernetBridge`, `VendorApproachPolicy`, and `ManagedHookRuntime` files directly. Lightweight object and vendor fixtures replace the game and GBR, so the tests never start FF14 or change an installed plugin.

The suite covers distance boundaries, original-call forwarding, source eligibility, vendor UI readiness, retry and deadline limits, per-frame busy/stop observation, repeated-route generations, registration rollback, retryable cleanup, and actual MonoMod interception after the fixture handlers have already executed. The runtime test uses the production dependency validation and default-context hook factory.

The fixture can verify interception and lifecycle behavior; it cannot substitute for a game run with official GBR and Lifestream.
