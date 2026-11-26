using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.ProjectModel;
using JetBrains.RdBackend.Common.Features.ProjectModel;
using JetBrains.RdBackend.Common.Features.ProjectModel.View;
using JetBrains.ReSharperCpp.RiderPlugin.ProjectModel.Vcxproj.View;
using JetBrains.Rider.Model;
using JetBrains.Util;

namespace RiderVcxFix.Backend;

/// <summary>
/// Harmony patches on JetBrains.ReSharperCpp.RiderPlugin that present real-directory VCX filters
/// as normal RdProjectFolderDescriptor nodes and resolve VcxFilterMark.GetProjectFolder() to the
/// real physical IProjectFolder.
///
/// ProjectModelTasks interception (Copy, AddItems, Move, Remove, Rename) is handled without
/// Harmony detours via the managed SolutionComponent <see cref="VcxFilterTaskInterceptor"/>.
/// </summary>
internal static class VcxFilterFolderViewPatch
{
    private static int Enabled;
    private static long FolderPresentedCount;
    private static long VirtualFallbackCount;

    public static bool IsActive => Volatile.Read(ref Enabled) != 0;

    public static List<MethodBase> Install(Harmony harmony, string owner)
    {
        var patched = new List<MethodBase>();
        TryPatchOne("VcxFiltersProjectModelViewPresenter..ctor", () => PatchPresenterCtor(harmony, owner, patched));
        TryPatchOne("VcxFilterMark.GetProjectFolder", () => PatchGetProjectFolder(harmony, owner, patched));

        if (patched.Count > 0)
        {
            Volatile.Write(ref Enabled, 1);
            Diagnostics.Write($"folder-view-patches-installed count={patched.Count}");
        }
        else
        {
            Volatile.Write(ref Enabled, 0);
            Diagnostics.Write("folder-view-patches-none-installed");
        }
        return patched;
    }

    public static void Uninstall(Harmony harmony, string owner, IEnumerable<MethodBase> patchedMethods)
    {
        Volatile.Write(ref Enabled, 0);
        foreach (var method in patchedMethods)
        {
            try
            {
                harmony.Unpatch(method, HarmonyPatchType.All, owner);
            }
            catch (Exception ex)
            {
                Diagnostics.Write($"folder-view-unpatch-error method={method.DeclaringType?.Name}.{method.Name} error={ex.Message}");
            }
        }
    }

    private static void TryPatchOne(string label, Action patchAction)
    {
        try
        {
            patchAction();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"folder-patch-failed target={label} error={ex}");
        }
    }

    private static void PatchPresenterCtor(Harmony harmony, string owner, List<MethodBase> patched)
    {
        var ctor = typeof(VcxFiltersProjectModelViewPresenter).GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(ProjectModelViewHost), typeof(ProjectModelViewPresenter), typeof(ILogger) },
            null);
        if (ctor == null)
            throw new MissingMethodException(nameof(VcxFiltersProjectModelViewPresenter), ".ctor");

        var prefix = typeof(VcxFilterFolderViewPatch).GetMethod(nameof(PresenterCtorPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        PatchSingle(harmony, owner, ctor, prefix: prefix, postfix: null, patched);
    }

    private static void PatchGetProjectFolder(Harmony harmony, string owner, List<MethodBase> patched)
    {
        var method = typeof(VcxFilterMark).GetMethod(nameof(VcxFilterMark.GetProjectFolder), BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
            throw new MissingMethodException(nameof(VcxFilterMark), nameof(VcxFilterMark.GetProjectFolder));

        var prefix = typeof(VcxFilterFolderViewPatch).GetMethod(nameof(GetProjectFolderPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        PatchSingle(harmony, owner, method, prefix: prefix, postfix: null, patched);
    }

    private static void PatchSingle(
        Harmony harmony,
        string owner,
        MethodBase target,
        MethodInfo? prefix,
        MethodInfo? postfix,
        List<MethodBase> patched)
    {
        var existing = HarmonyLib.Harmony.GetPatchInfo(target);
        if (existing?.Owners.Contains(owner) == true)
            return;

        harmony.Patch(
            target,
            prefix: prefix != null ? new HarmonyMethod(prefix) : null,
            postfix: postfix != null ? new HarmonyMethod(postfix) : null);
        patched.Add(target);
        Diagnostics.Write($"folder-patch-installed target={target.DeclaringType?.Name}.{target.Name}");
    }

    // -------------------------------------------------------------------------
    // Patch 1: Present resolved VcxFilterMark nodes as real RdProjectFolderDescriptor
    // -------------------------------------------------------------------------
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PresenterCtorPrefix(
        ProjectModelViewHost viewHost,
        ProjectModelViewPresenter presenter,
        ILogger logger)
    {
        if (!IsActive)
            return true;

        viewHost.Present((VcxFilterMark mark) => PresentVcxFilterMark(mark, logger));
        viewHost.Present((VcxFilterPresenterOverride overrideItem) =>
            presenter.PresentProjectFile(overrideItem.File, overrideItem.File.Location.ToRdLocation()));
        Diagnostics.Write("presenter-registered custom VcxFilterMark folder presenter");
        return false;
    }

    private static RdProjectModelItemDescriptor PresentVcxFilterMark(VcxFilterMark mark, ILogger logger)
    {
        try
        {
            if (IsActive && VcxFilterFolderResolver.TryResolveDirectory(mark, out var dirPath) && !dirPath.IsEmpty)
            {
                var count = Interlocked.Increment(ref FolderPresentedCount);
                if (count <= 10 || (count & (count - 1)) == 0)
                {
                    Diagnostics.Write($"present-as-folder count={count} filter='{mark.FilterPath}' dir='{dirPath.FullPath}'");
                }
                return new RdProjectFolderDescriptor(
                    isInternal: false,
                    isLinked: false,
                    isAppDesigner: false,
                    allowsNonExistence: false,
                    sortKey: null,
                    userData: null,
                    name: mark.Name,
                    location: dirPath.ToRdLocation(),
                    loadChildrenLazily: false);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"present-fallback-error filter='{mark.FilterPath}' error={ex.Message}");
        }

        var fallbackCount = Interlocked.Increment(ref VirtualFallbackCount);
        if (fallbackCount <= 10 || (fallbackCount & (fallbackCount - 1)) == 0)
        {
            Diagnostics.Write($"present-as-virtual-filter count={fallbackCount} filter='{mark.FilterPath}'");
        }

        return new RdVcxFilterDescriptor(
            mark.FilterPath,
            mark.ParentFilterPath,
            mark.Guid,
            mark.IsEmpty,
            mark.Extensions,
            mark.Name,
            mark.Location.ToRdLocation(),
            loadChildrenLazily: false);
    }

    // -------------------------------------------------------------------------
    // Patch 2: VcxFilterMark.GetProjectFolder() -> real IProjectFolder if present
    // -------------------------------------------------------------------------
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool GetProjectFolderPrefix(VcxFilterMark __instance, ref IProjectFolder __result)
    {
        if (!IsActive)
            return true;

        try
        {
            if (VcxFilterFolderResolver.TryResolveExistingProjectFolder(__instance, out var folder) && folder != null)
            {
                __result = folder;
                return false;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"get-project-folder-error filter='{__instance.FilterPath}' error={ex.Message}");
        }
        return true;
    }
}
