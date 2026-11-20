using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.DataFlow;
using JetBrains.Lifetimes;
using JetBrains.Util;
using JetBrains.Util.Logging;

namespace RiderVcxFix.Backend;

// Shell creation precedes SolutionInstance construction for this instantiation mode.
[ShellComponent(Instantiation.ContainerAsyncPrimaryThread)]
public sealed class VcxFilterOverridePatch
{
    public const string HarmonyOwner = "local.rider.vcx-filter-view-fix.backend";
    private static PatchSession? ActiveSession;

    public VcxFilterOverridePatch(Lifetime lifetime)
    {
        var session = new PatchSession();
        if (Interlocked.CompareExchange(ref ActiveSession, session, null) != null)
        {
            Diagnostics.Write("component-duplicate active-session-already-present");
            return;
        }

        Diagnostics.Write($"component-initialized instantiation=ContainerAsyncPrimaryThread property={session.PropertyIdentity} value={session.FalseProperty.Value} readyToRun={Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "<unset>"} comPlusReadyToRun={Environment.GetEnvironmentVariable("COMPlus_ReadyToRun") ?? "<unset>"} startupHookConfigured={!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS"))}");
        lifetime.OnTermination(() =>
        {
            session.Dispose();
            Interlocked.CompareExchange(ref ActiveSession, null, session);
        });
        session.Initialize();
    }

    // This never changes the detector's IsUnrealSolution property. The same false property
    // backs both the helper's subscriptions and every subsequent enablement check.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Prefix(object __instance, ref IProperty<bool> __result)
    {
        var session = Volatile.Read(ref ActiveSession);
        if (session == null || !session.CanReplace)
            return true;

        __result = session.FalseProperty;
        session.ObserveGetter(__instance);
        return false;
    }

    private sealed class PatchSession : IDisposable
    {
        private const string TargetAssembly = "JetBrains.ReSharperCpp.RiderPlugin";
        private const string TargetType = "JetBrains.ReSharperCpp.RiderPlugin.Unreal.UnrealEngineVcxFilterOverride";
        private readonly object Gate = new();
        private readonly Harmony Harmony = new(HarmonyOwner);
        private readonly ConditionalWeakTable<object, DetectorProbe> Probes = new();
        private MethodInfo? Target;
        private FieldInfo? DetectorField;
        private MethodInfo? DetectorGetter;
        private bool Attempted;
        private bool HandlerRegistered;
        private int Disposed;
        private int Enabled;
        private long GetterCalls;
        private int ProbeFailureReported;

        // Current Rider's Lifetime constructors for Property are obsolete and ignore the
        // lifetime. Own this single instance through the Shell's PatchSession instead.
        public IProperty<bool> FalseProperty { get; } = new Property<bool>("RiderVcxFix.UnrealFilterVeto", false);
        public int PropertyIdentity => RuntimeHelpers.GetHashCode(FalseProperty);
        public bool IsDisposed => Volatile.Read(ref Disposed) != 0;
        public bool CanReplace => !IsDisposed && Volatile.Read(ref Enabled) != 0;

        public void Initialize()
        {
            Assembly? loaded;
            lock (Gate)
            {
                // Lifetime cancellation may run Dispose before initialization reaches
                // this point. Register and unregister under the same gate so a stopped
                // session can never leave an AssemblyLoad subscription behind.
                if (IsDisposed || HandlerRegistered)
                    return;
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                HandlerRegistered = true;
                loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == TargetAssembly);
            }
            if (loaded != null)
            {
                TryPatch(loaded, "already-loaded");
                return;
            }

            if (IsDisposed)
                return;

            // Normal component discovery can load this assembly already. If it has not,
            // ask the host's established resolver; never invent an installation path.
            try
            {
                TryPatch(Assembly.Load(new AssemblyName(TargetAssembly)), "host-resolver");
            }
            catch (Exception exception)
            {
                Diagnostics.Write($"target-load-deferred assembly={TargetAssembly} error={exception.GetType().FullName}: {exception.Message}");
            }
        }

        private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name == TargetAssembly)
                TryPatch(args.LoadedAssembly, "assembly-load-event");
        }

        private void TryPatch(Assembly assembly, string source)
        {
            lock (Gate)
            {
                if (Attempted || IsDisposed)
                    return;
                Attempted = true;
                try
                {
                    var type = assembly.GetType(TargetType, throwOnError: true)!;
                    var getter = type.GetProperty("IsForceDisabled", BindingFlags.Public | BindingFlags.Instance)?.GetMethod;
                    if (getter == null || getter.ReturnType != typeof(IProperty<bool>) || getter.GetParameters().Length != 0)
                        throw new MissingMethodException(TargetType, "get_IsForceDisabled returning IProperty<bool>");

                    DetectorField = type.GetField("myDetector", BindingFlags.Instance | BindingFlags.NonPublic);
                    DetectorGetter = DetectorField?.FieldType.GetProperty("IsUnrealSolution")?.GetMethod;
                    Diagnostics.Write($"target-loaded source={source} assembly={assembly.GetName().Name} version={assembly.GetName().Version} mvid={assembly.ManifestModule.ModuleVersionId} getter={type.FullName}.{getter.Name} implFlags={getter.GetMethodImplementationFlags()} originalDetectorProbeAvailable={DetectorField != null && DetectorGetter != null}");
                    Target = getter;
                    var existing = HarmonyLib.Harmony.GetPatchInfo(getter);
                    Diagnostics.Write($"patch-before owners={FormatOwners(existing)} owner={HarmonyOwner}");
                    if (existing?.Owners.Contains(HarmonyOwner) != true)
                    {
                        var prefix = typeof(VcxFilterOverridePatch).GetMethod(nameof(Prefix), BindingFlags.Public | BindingFlags.Static)!;
                        Harmony.Patch(getter, prefix: new HarmonyMethod(prefix));
                    }
                    Target = getter;
                    var installed = HarmonyLib.Harmony.GetPatchInfo(getter);
                    if (installed?.Prefixes.Any(p => p.owner == HarmonyOwner) != true)
                        throw new InvalidOperationException("Harmony did not report this owner's prefix after Patch");
                    Volatile.Write(ref Enabled, 1);
                    Diagnostics.Write($"patch-installed owner={HarmonyOwner} owners={FormatOwners(installed)} stableProperty={PropertyIdentity} value={FalseProperty.Value} awaiting-real-getter-call=true");
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref Enabled, 0);
                    try
                    {
                        if (Target != null)
                            Harmony.Unpatch(Target, HarmonyPatchType.Prefix, HarmonyOwner);
                    }
                    catch (Exception cleanupException)
                    {
                        Diagnostics.Write($"failed-patch-cleanup-error replacement-disabled=true error={cleanupException}");
                    }
                    Diagnostics.Write($"patch-failed prefix-replacement-disabled=true original-behavior-retained=true error={exception}");
                }
            }
        }

        public void ObserveGetter(object instance)
        {
            try
            {
                var call = Interlocked.Increment(ref GetterCalls);
                var probe = Probes.GetValue(instance, CreateProbe);
                var original = probe.OriginalProperty;
                var originalValue = original?.Value;
                var encoded = originalValue.HasValue ? (originalValue.Value ? 1 : 0) : -1;
                var previous = Interlocked.Exchange(ref probe.LastOriginalValue, encoded);
                if (previous != encoded || call <= 8 || (call & (call - 1)) == 0)
                {
                    Diagnostics.Write($"getter-observed call={call} instance={RuntimeHelpers.GetHashCode(instance)} returnedProperty={PropertyIdentity} stable=true returnedValue={FalseProperty.Value} originalDetectorProperty={probe.OriginalIdentity} originalDetectorValue={(originalValue.HasValue ? originalValue.Value.ToString() : "<unavailable>")} distinctFromOriginal={!ReferenceEquals(original, FalseProperty)} originalDetectorWritten=false");
                }
            }
            catch (Exception exception)
            {
                // Diagnostics must never change the patched getter's result or abort load.
                if (Interlocked.Exchange(ref ProbeFailureReported, 1) == 0)
                    Diagnostics.Write($"detector-probe-failed getter-still-returns-false error={exception.GetType().FullName}: {exception.Message}");
            }
        }

        private DetectorProbe CreateProbe(object instance)
        {
            IProperty<bool>? original = null;
            if (DetectorField != null && DetectorGetter != null)
            {
                var detector = DetectorField.GetValue(instance);
                if (detector != null)
                    original = DetectorGetter.Invoke(detector, null) as IProperty<bool>;
            }
            var callers = new StackTrace(1, false).GetFrames()
                .Select(f => f.GetMethod()).Where(m => m != null)
                .Select(m => $"{m!.DeclaringType?.FullName}.{m.Name}")
                .Where(n => !n.StartsWith("RiderVcxFix.Backend.", StringComparison.Ordinal))
                .Take(10);
            Diagnostics.Write($"getter-first-instance instance={RuntimeHelpers.GetHashCode(instance)} callerChain={string.Join(" <- ", callers)}");
            return new DetectorProbe(original);
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (Interlocked.Exchange(ref Disposed, 1) != 0)
                    return;
                if (HandlerRegistered)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    HandlerRegistered = false;
                }
                try
                {
                    if (Target != null)
                        Harmony.Unpatch(Target, HarmonyPatchType.Prefix, HarmonyOwner);
                    Diagnostics.Write($"shell-terminated owner={HarmonyOwner} totalGetterCalls={GetterCalls} own-prefix-removed=true");
                }
                catch (Exception exception)
                {
                    Diagnostics.Write($"shell-cleanup-failed getter-falls-back-to-original=true error={exception}");
                }
            }
        }

        private static string FormatOwners(Patches? patches) => patches == null
            ? "<none>" : string.Join(",", patches.Owners.OrderBy(x => x, StringComparer.Ordinal));
    }

    private sealed class DetectorProbe(IProperty<bool>? originalProperty)
    {
        public IProperty<bool>? OriginalProperty { get; } = originalProperty;
        public int OriginalIdentity => OriginalProperty == null ? 0 : RuntimeHelpers.GetHashCode(OriginalProperty);
        public int LastOriginalValue = -2;
    }
}

internal static class Diagnostics
{
    private static readonly object FileGate = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), $"rider-vcxfix-backend-{Environment.ProcessId}.log");

    public static void Write(string message)
    {
        var line = $"[RiderVcxFix.Backend] pid={Environment.ProcessId} {message}";
        try
        {
            Logger.GetLogger<VcxFilterOverridePatch>().Info(line);
        }
        catch
        {
            // Keep the separate process-specific trace available if the host logger is
            // still starting up, disabled, or shutting down.
        }
        try
        {
            lock (FileGate)
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
        }
        catch
        {
            // A missing log destination is not a reason to break Rider startup.
        }
    }
}



