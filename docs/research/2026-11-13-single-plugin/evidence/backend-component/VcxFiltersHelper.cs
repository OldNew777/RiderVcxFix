using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using JetBrains.Application.Settings;
using JetBrains.DataFlow;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel.DataContext;
using JetBrains.Util;

namespace JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter;

/// <summary>
///   Common helper methods for VCXProj filters functionality.
///   Centralizes common checks and path operations to avoid code duplication.
/// </summary>
public static class VcxFiltersHelper
{
	private sealed class VirtualFileSystemPathComparer : IEqualityComparer<VirtualFileSystemPath>
	{
		public bool Equals(VirtualFileSystemPath x, VirtualFileSystemPath y)
		{
			return AreSamePath(x, y);
		}

		public int GetHashCode(VirtualFileSystemPath obj)
		{
			if (!obj.IsEmpty)
			{
				return FileSystemDefinition.PathStringEquality.GetHashCode(GetPathKey(obj));
			}
			return 0;
		}
	}

	public static IEqualityComparer<VirtualFileSystemPath> PathComparer { get; } = new VirtualFileSystemPathComparer();

	/// <summary>
	///   Checks if a project mark represents a VCXProj project.
	/// </summary>
	public static bool IsVcxProject(IProjectMark projectMark)
	{
		return projectMark.Location.ExtensionNoDot.Equals("vcxproj", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	///   Checks if a project represents a VCXProj project.
	/// </summary>
	public static bool IsVcxProject(IProject project)
	{
		return project.ProjectFileLocation.ExtensionNoDot.Equals("vcxproj", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	///   Gets the path to the .vcxproj.filters file for a given project path.
	/// </summary>
	public static VirtualFileSystemPath GetFilterFilePath(VirtualFileSystemPath projectPath)
	{
		return projectPath.Parent.Combine(projectPath.Name + ".filters");
	}

	/// <summary>
	///   Checks if filters file exists for a given project path.
	/// </summary>
	public static bool FiltersFileExists(VirtualFileSystemPath projectPath)
	{
		return GetFilterFilePath(projectPath).ExistsFile;
	}

	/// <summary>
	///   Resolves the loaded <see cref="T:JetBrains.ProjectModel.IProject" /> for a <c>.vcxproj</c> path via the solution's
	///   location index, disambiguating same-location project items by project file location.
	///   Caller must hold a read lock.
	///   Returns <c>null</c> when no matching project is loaded.
	/// </summary>
	[CanBeNull]
	public static IProject FindProjectByPath([NotNull] ISolution solution, [NotNull] VirtualFileSystemPath projectPath)
	{
		IProject project = solution.FindProjectItemsByLocation(projectPath).OfType<IProject>().FirstOrDefault((IProject project2) => AreSamePath(project2.ProjectFileLocation, projectPath));
		if (project != null)
		{
			return project;
		}
		return solution.GetAllProjects().FirstOrDefault((IProject project2) => AreSamePath(project2.ProjectFileLocation, projectPath));
	}

	public static bool AreSamePath(VirtualFileSystemPath x, VirtualFileSystemPath y)
	{
		if (x.IsEmpty || y.IsEmpty)
		{
			if (x.IsEmpty)
			{
				return y.IsEmpty;
			}
			return false;
		}
		if (x == y)
		{
			return true;
		}
		return FileSystemDefinition.PathStringEquality.Equals(GetPathKey(x), GetPathKey(y));
	}

	private static string GetPathKey(VirtualFileSystemPath path)
	{
		return path.FullPath.Replace('/', '\\');
	}

	/// <summary>
	///   Combined check: Is this a VCX project with filters feature enabled and filters file exists?
	/// </summary>
	public static bool IsVcxFilterApplicable(IProjectMark projectMark, IContextBoundSettingsStoreLive settingsStore, ISolution solution)
	{
		if (IsVcxFilterFeatureEnabled(projectMark, settingsStore, solution))
		{
			return FiltersFileExists(projectMark.Location);
		}
		return false;
	}

	public static bool IsVcxFilterFeatureEnabled(IProjectMark projectMark, IContextBoundSettingsStoreLive settingsStore, ISolution solution)
	{
		if (IsFeatureEnabled(GetUseFiltersSetting(settingsStore), solution))
		{
			return IsVcxProject(projectMark);
		}
		return false;
	}

	/// <summary>
	///   Checks if a VCX project has the filters feature enabled (without requiring filters file to exist).
	///   Accepts a pre-bound settings store for efficient repeated calls — avoids creating a new
	///   settings-context subscription on each invocation.
	/// </summary>
	public static bool IsVcxFilterFeatureEnabled(IProject project, IContextBoundSettingsStoreLive settingsStore)
	{
		if (IsFeatureEnabled(GetUseFiltersSetting(settingsStore), project.GetSolution()))
		{
			return IsVcxProject(project);
		}
		return false;
	}

	/// <summary>
	///   Checks if a VCX project has the filters feature enabled (without requiring the filters file
	///   to exist). Settings-only — unlike <see cref="M:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxFiltersHelper.IsVcxFilterApplicable(JetBrains.ProjectModel.ProjectsHost.IProjectMark,JetBrains.Application.Settings.IContextBoundSettingsStoreLive,JetBrains.ProjectModel.ISolution)" /> it performs no disk
	///   I/O, so it is safe inside a dispatcher-side Changed callback under a read lock;
	///   the .filters existence probe belongs on a background thread.
	/// </summary>
	public static bool IsVcxFilterFeatureEnabled(IProject project, ISettingsStore settingsStore, ISolution solution)
	{
		if (IsFeatureEnabled(GetUseFiltersSetting(settingsStore, solution), solution))
		{
			return IsVcxProject(project);
		}
		return false;
	}

	/// <summary>
	///   Converts an on-disk file location into the MSBuild <c>Include</c> spelling the .filters
	///   document is keyed by: relative to <paramref name="projectDirectory" /> and backslash-separated
	///   on every host OS. Returns <c>null</c> when no relative form exists — the file is on a
	///   different drive or UNC host than the project.
	/// </summary>
	/// <remarks>
	///   The single authority for the on-disk → include-spelling direction; every caller that needs to
	///   look a file up in the .filters keys must route through here, so all of them agree on the
	///   spelling on every host. Separator normalization goes through the platform API rather than a
	///   hand-rolled <c>Replace</c> because the input is a real path, where the platform knows the
	///   host's separator; the reverse direction — parsing an include spelling — must NOT use the path
	///   parser at all, see <see cref="T:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxIncludePathUtil" />.
	///   <para>
	///     Returning <c>null</c> rather than throwing keeps the failure policy with the caller: the
	///     view providers skip such a file and let the default provider place it, while
	///     <c>VcxFilterService</c> falls back to treating its input as an include spelling already.
	///   </para>
	/// </remarks>
	[CanBeNull]
	public static string TryGetProjectRelativeInclude(VirtualFileSystemPath filePath, VirtualFileSystemPath projectDirectory)
	{
		try
		{
			return filePath.MakeRelativeTo(projectDirectory).NormalizeSeparators(FileSystemPathEx.SeparatorStyle.Windows);
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>
	///   <see cref="M:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxFiltersHelper.TryGetProjectRelativeInclude(JetBrains.Util.VirtualFileSystemPath,JetBrains.Util.VirtualFileSystemPath)" /> for a project identified by its mark.
	/// </summary>
	[CanBeNull]
	public static string GetRelativeFilePath(IProjectMark projectMark, VirtualFileSystemPath filePath)
	{
		return TryGetProjectRelativeInclude(filePath, projectMark.Location.Directory);
	}

	/// <summary>
	///   Gets the lower-cased file extension (without dot) from an MSBuild <c>Include</c> spelling.
	/// </summary>
	/// <remarks>
	///   Delegates to <see cref="M:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxIncludePathUtil.GetExtensionNoDot(System.String)" />. Every caller passes an
	///   include spelling rather than a filesystem path, so segmentation must not go through the
	///   OS-aware parser — see the remarks on <see cref="T:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxIncludePathUtil" /> for why that used to
	///   produce a host-dependent answer here.
	/// </remarks>
	public static string GetExtension(string filePath)
	{
		return VcxIncludePathUtil.GetExtensionNoDot(filePath);
	}

	/// <summary>
	///   The single source of truth for whether the VCX filters feature is enabled at the solution
	///   level: the user's <see cref="P:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxFilterSettings.UseFilters" /> setting is on AND no registered
	///   <see cref="T:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.IVcxFilterFeatureOverride" /> currently force-disables it. Every "is the feature
	///   on?" check must route through this so the frontend and backend never disagree (RIDER-40004).
	/// </summary>
	public static bool IsFeatureEnabled(bool useFilters, [NotNull] IEnumerable<IVcxFilterFeatureOverride> overrides)
	{
		if (useFilters)
		{
			return !overrides.Any((IVcxFilterFeatureOverride o) => o.IsForceDisabled.Value);
		}
		return false;
	}

	/// <summary>
	///   Wires <paramref name="push" /> to the reactive feature-enabled state: exactly one initial push,
	///   then a recompute whenever <paramref name="useFilters" /> or any override's
	///   <see cref="P:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.IVcxFilterFeatureOverride.IsForceDisabled" /> actually changes. Advising on the
	///   overrides — not just the setting — is what keeps the pushed value from going stale when an
	///   override flips after solution load (e.g. Unreal detection completing). See RIDER-40004.
	/// </summary>
	public static void AdviseFeatureEnabled(Lifetime lifetime, [NotNull] IProperty<bool> useFilters, [NotNull] IReadOnlyCollection<IVcxFilterFeatureOverride> overrides, [NotNull] Action<bool> push)
	{
		useFilters.Change.Advise_NoAcknowledgement(lifetime, Recompute);
		foreach (IVcxFilterFeatureOverride @override in overrides)
		{
			@override.IsForceDisabled.Change.Advise_NoAcknowledgement(lifetime, Recompute);
		}
		Recompute();
		void Recompute()
		{
			push(IsFeatureEnabled(useFilters.Value, overrides));
		}
	}

	private static bool IsFeatureEnabled(bool useFilters, ISolution solution)
	{
		return IsFeatureEnabled(useFilters, solution.GetComponents<IVcxFilterFeatureOverride>());
	}

	private static bool GetUseFiltersSetting(IContextBoundSettingsStoreLive settingsStore)
	{
		return settingsStore.GetValue((VcxFilterSettings x) => x.UseFilters);
	}

	private static bool GetUseFiltersSetting(ISettingsStore settingsStore, ISolution solution)
	{
		return settingsStore.BindToContextTransient(ContextRange.Smart(solution.ToDataContext2())).GetValue((VcxFilterSettings x) => x.UseFilters);
	}

	/// <summary>
	///   Returns <c>true</c> if moving <paramref name="filterPath" /> under
	///   <paramref name="newParentFilterPath" /> would create a cycle (moving a filter
	///   under itself or one of its own descendants).
	/// </summary>
	public static bool IsCyclicMove(string filterPath, string newParentFilterPath)
	{
		if (!string.IsNullOrEmpty(newParentFilterPath))
		{
			return VcxFilterPathUtil.IsSelfOrDescendantOf(newParentFilterPath, filterPath);
		}
		return false;
	}

	/// <summary>
	///   Re-homes every file currently assigned to any of <paramref name="deletedFilterPaths" /> to its
	///   default extension-based filter, in place on <paramref name="doc" />. Files whose extension has no
	///   matching defined filter are left unassigned and fall to the project root. Mirrors Visual Studio,
	///   which re-categorizes a deleted filter's files into their default filters on the next save.
	///   Must run after the filter definitions are removed so the resolver never re-selects a deleted
	///   filter, and inside the caller's project-lock/document scope — this performs no I/O or locking and
	///   does not save the document.
	/// </summary>
	internal static void ReassignFilesToDefaults([NotNull] VcxFiltersDocument doc, [NotNull] IReadOnlyCollection<string> deletedFilterPaths)
	{
		IReadOnlyList<(string, string)> filesInFilters = doc.GetFilesInFilters(deletedFilterPaths);
		if (filesInFilters.Count == 0)
		{
			return;
		}
		HashSet<string> allFilterPaths = doc.GetAllFilterPaths();
		Dictionary<string, string> extensionMappings = doc.GetExtensionMappings();
		foreach (var item3 in filesInFilters)
		{
			string item = item3.Item1;
			string item2 = item3.Item2;
			string targetFilter = VcxFilterExtensionMap.ResolveFilterForFile(item, allFilterPaths, extensionMappings);
			doc.SetFileMapping(item, item2, targetFilter);
		}
	}
}
