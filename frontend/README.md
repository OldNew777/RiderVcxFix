# Rider VCX Filters Fix 2.0.2 frontend

Plugin ID: local.rider.vcx-filter-view-fix.
Compatibility: Rider 2026.3 (branch 263) and later; until-build intentionally omitted for open-ended compatibility.
Verified build: RD-263.7307.258 only.
Name: Rider VCX Filters Fix.
Author/Maintainer: ChenXin (L66 Tools).
Description: Rider 2026.3 compatibility plugin that preserves virtual VCX filter nodes while preventing duplicate Show All Files entries, with a companion .NET backend fix for Use VCX project filters.
Non-dynamic registration: require-restart=true; install/update requires a full Rider restart.

VcxFilterViewFix.java is copied from the previously verified 1.0.0 frontend. Its algorithm and fixture are unchanged; the active log version is updated to 2.0.2. The front end removes only SolutionExplorerFileNode additions beneath RdVcxFilterDescriptor nodes, retaining model node order and project-root additions.

Build the production JAR without waiting for backend artifacts:

    & 'E:\Softwares\RiderVcxFix\unified-plugin\frontend\build.ps1' -FrontendOnly

Build the complete plugin after the backend artifacts are available:

    & 'E:\Softwares\RiderVcxFix\unified-plugin\frontend\build.ps1'

Default backend artifact input:
E:\Softwares\RiderVcxFix\unified-plugin\backend\artifacts

Complete ZIP output:
E:\Softwares\RiderVcxFix\unified-plugin\artifacts\rider-vcx-filters-fix-2.0.2.zip

The script packages only the production JAR and explicit backend dependency DLLs. It does not package fixtures, diagnostic runners, Rider API JARs, startup-hook DLLs or hostEnvProvider code. It does not install a plugin, launch Rider or write user configuration.

- source-copy-check.json records unchanged algorithm/source-copy checks.
- frontend-package-check.json records actual JAR entries and Java class major version.
- build-manifest.json records the Rider version and artifact/dependency hashes.

The build supports -RiderHome, -JavaHome and -BackendArtifacts. Optional -RunFixture is retained for future algorithm changes; this unchanged algorithm already passed the previous fixture and real-IDE validation. Unified backend behavior still requires fresh isolated runtime validation.