# Rider VCX Filters backend

Targets only Rider RD-263.7307.258 and its .NET 10 backend. This library is the .NET half of the unified plugin; it is not a startup hook.

Run `pwsh -File .\build.ps1` to rebuild against the installed Rider host. `-RiderHostDir` accepts another installation of exactly the same build. The project uses the actual installed API assemblies, not a guessed SDK version. The local Harmony 2.4.3 binary is pinned by SHA256. Build output and the exact distributable dependency list are in `artifacts/dependencies.json`.

Place only `RiderVcxFix.Backend.dll` and `0Harmony.dll` in the plugin's `dotnet/` directory. Do not distribute JetBrains API assemblies, PDBs, build manifests, or test files. The CLR library needs no executable runtimeconfig or deps file. Backend discovery uses the plugin's standard dotnet directory and ordinary type-level component/zone attributes, with no invented JVM descriptor node or JetBrains assembly-level flag.

`VcxFilterOverridePatch` is a `ShellComponent(ContainerAsyncPrimaryThread)`. `ZoneMarker` requires Rider's `IRiderFeatureZone` and `IRiderProductEnvironmentZone`. Component lifetime owns one false `IProperty<bool>` instance. The Harmony prefix only replaces `UnrealEngineVcxFilterOverride.get_IsForceDisabled`; it never writes `ICppUE4SolutionDetector.IsUnrealSolution`, changes `UseFilters`, or removes another override's veto. During Shell termination it disables replacement and removes only its own prefix.

Diagnostics are sent to the Rider backend logger with `[RiderVcxFix.Backend]` and also `%TEMP%\rider-vcxfix-backend-<PID>.log`. Process identity, target assembly MVID, Harmony owner, R2R variables and hook presence make runs distinguishable without logging full process arguments or other environment values.

For runtime acceptance, `component-initialized` proves actual DI creation, and `target-loaded` plus `patch-installed` proves registration by owner `local.rider.vcx-filter-view-fix.backend`. Those messages alone do not prove detour effectiveness. Require `getter-first-instance` from an actual helper caller and `getter-observed` where the detector reports true while `returnedValue=False`, `distinctFromOriginal=True`, and the returned property identity remains the same across calls. The weak-key per-instance probe reads the original detector property but neither subscribes to it nor retains a solution through a Shell-lifetime callback. Its value changes are logged when the getter is subsequently called. The feature's RD state and project tree must still be checked in Rider.

Default-environment compatibility, R2R/inlining behavior, real plugin activation, and installation/removal require isolated Rider verification. A build or Harmony owner record is not a runtime pass. An incompatibility disables replacement and logs `patch-failed`; any remaining inactive prefix returns to the original getter rather than inventing a successful repair.
