using System.Reflection;
using System.Runtime.CompilerServices;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter;
using JetBrains.RdBackend.Common.Features.ProjectModel.View;
using JetBrains.ReSharperCpp.RiderPlugin.ProjectModel.Vcxproj.View;
using JetBrains.Util;

namespace RiderVcxFix.Backend;

/// <summary>
/// Resolves VCX virtual filter paths to their corresponding real physical directories on disk.
/// </summary>
public static class VcxFilterFolderResolver
{
    private static readonly FieldInfo? MarkProjectField =
        typeof(VcxFilterMark).GetField("myProject", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConditionalWeakTable<PreloadedFilterData, ResolvedFilterMap> PreloadCache = new();

    public sealed class ResolvedFilterMap
    {
        public ResolvedFilterMap(
            IReadOnlyDictionary<string, string> filterToDirectory,
            IReadOnlySet<string> conflictedFilters,
            string? projectSourceRoot)
        {
            FilterToDirectory = filterToDirectory;
            ConflictedFilters = conflictedFilters;
            ProjectSourceRoot = projectSourceRoot;
        }

        public IReadOnlyDictionary<string, string> FilterToDirectory { get; }
        public IReadOnlySet<string> ConflictedFilters { get; }
        public string? ProjectSourceRoot { get; }

        public bool TryResolve(string filterPath, Func<string, bool> directoryExists, out string resolvedDir)
        {
            resolvedDir = string.Empty;
            var normalized = NormalizeFilterPath(filterPath);
            if (string.IsNullOrEmpty(normalized))
                return false;

            if (FilterToDirectory.TryGetValue(normalized, out var cached))
            {
                resolvedDir = cached;
                return true;
            }

            if (ConflictedFilters.Contains(normalized))
                return false;

            // Dynamic top-down probe for freshly created empty directories not yet in PreloadedFilterData.
            var remainingSegments = new Stack<string>();
            var cursor = normalized;
            string? anchorDir = null;
            while (!string.IsNullOrEmpty(cursor))
            {
                if (FilterToDirectory.TryGetValue(cursor, out var found))
                {
                    anchorDir = found;
                    break;
                }
                if (ConflictedFilters.Contains(cursor))
                    return false;

                remainingSegments.Push(GetLeafName(cursor));
                cursor = GetParentFilterPath(cursor);
            }

            anchorDir ??= ProjectSourceRoot;
            if (string.IsNullOrEmpty(anchorDir) || remainingSegments.Count == 0)
                return false;

            var candidate = anchorDir;
            while (remainingSegments.Count > 0)
                candidate = Path.Combine(candidate, remainingSegments.Pop());

            if (!directoryExists(candidate))
                return false;

            resolvedDir = NormalizeDirPath(candidate);
            return true;
        }
    }

    public static ResolvedFilterMap ComputeResolvedDirectories(
        string projectDirPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filterFiles,
        Func<string, bool> directoryExists)
    {
        var normalizedProjectDir = NormalizeDirPath(projectDirPath);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Register all filters plus any implicit ancestor filter paths.
        foreach (var rawKey in filterFiles.Keys)
        {
            var norm = NormalizeFilterPath(rawKey);
            while (!string.IsNullOrEmpty(norm))
            {
                allFilters.Add(norm);
                norm = GetParentFilterPath(norm);
            }
        }

        // Pass 1: Filters with direct files -> single unique physical directory.
        foreach (var (rawKey, files) in filterFiles)
        {
            var filterPath = NormalizeFilterPath(rawKey);
            if (string.IsNullOrEmpty(filterPath) || files.Count == 0)
                continue;

            string? commonDir = null;
            var hasConflict = false;
            foreach (var relFile in files)
            {
                if (string.IsNullOrWhiteSpace(relFile))
                    continue;

                string? fullDir;
                try
                {
                    var fullFile = Path.GetFullPath(Path.Combine(normalizedProjectDir, relFile));
                    fullDir = Path.GetDirectoryName(fullFile);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(fullDir))
                    continue;

                var normalizedDir = NormalizeDirPath(fullDir);
                if (commonDir == null)
                {
                    commonDir = normalizedDir;
                }
                else if (!string.Equals(commonDir, normalizedDir, StringComparison.OrdinalIgnoreCase))
                {
                    hasConflict = true;
                    break;
                }
            }

            if (hasConflict)
            {
                conflicted.Add(filterPath);
            }
            else if (commonDir != null)
            {
                resolved[filterPath] = commonDir;
            }
        }

        // Build direct parent -> children lookup.
        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var topLevelFilters = new List<string>();
        foreach (var filterPath in allFilters)
        {
            var parent = GetParentFilterPath(filterPath);
            if (parent == null)
            {
                topLevelFilters.Add(filterPath);
            }
            else
            {
                if (!childrenByParent.TryGetValue(parent, out var list))
                {
                    list = new List<string>();
                    childrenByParent[parent] = list;
                }
                list.Add(filterPath);
            }
        }

        // Pass 2: Bottom-up inference for intermediate filters without direct files.
        var byDepthDesc = allFilters.OrderByDescending(GetDepth).ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var filterPath in byDepthDesc)
        {
            if (resolved.ContainsKey(filterPath) || conflicted.Contains(filterPath))
                continue;

            if (!childrenByParent.TryGetValue(filterPath, out var children) || children.Count == 0)
                continue;

            string? inferredParentDir = null;
            var hasConflict = false;
            var matchedLeafCount = 0;

            foreach (var child in children)
            {
                if (!resolved.TryGetValue(child, out var childDir))
                    continue;

                var parentDir = Path.GetDirectoryName(childDir);
                if (string.IsNullOrEmpty(parentDir))
                    continue;

                var normalizedParentDir = NormalizeDirPath(parentDir);
                var childLeafName = GetLeafName(child);
                var dirLeafName = Path.GetFileName(childDir);
                if (string.Equals(childLeafName, dirLeafName, StringComparison.OrdinalIgnoreCase))
                    matchedLeafCount++;

                if (inferredParentDir == null)
                {
                    inferredParentDir = normalizedParentDir;
                }
                else if (!string.Equals(inferredParentDir, normalizedParentDir, StringComparison.OrdinalIgnoreCase))
                {
                    hasConflict = true;
                    break;
                }
            }

            if (hasConflict)
            {
                conflicted.Add(filterPath);
            }
            else if (inferredParentDir != null && matchedLeafCount > 0)
            {
                resolved[filterPath] = inferredParentDir;
            }
        }

        // Infer the project's primary source root directory (e.g. Engine/ or l66/).
        var projectSourceRoot = InferProjectSourceRoot(normalizedProjectDir, topLevelFilters, resolved, directoryExists);

        // Pass 3: Top-down resolution for empty filters / newly created directories on disk.
        var byDepthAsc = allFilters.OrderBy(GetDepth).ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var filterPath in byDepthAsc)
        {
            if (resolved.ContainsKey(filterPath) || conflicted.Contains(filterPath))
                continue;

            var parent = GetParentFilterPath(filterPath);
            string? baseDir = null;
            if (parent != null)
            {
                resolved.TryGetValue(parent, out baseDir);
            }
            else
            {
                baseDir = projectSourceRoot;
            }

            if (string.IsNullOrEmpty(baseDir))
                continue;

            var candidate = Path.Combine(baseDir, GetLeafName(filterPath));
            if (directoryExists(candidate))
            {
                resolved[filterPath] = NormalizeDirPath(candidate);
            }
        }

        return new ResolvedFilterMap(resolved, conflicted, projectSourceRoot);
    }

    public static IProject? GetOwningProject(VcxFilterMark mark)
    {
        return MarkProjectField?.GetValue(mark) as IProject;
    }

    public static bool TryGetMarkById(ProjectModelViewHost viewHost, int id, out VcxFilterMark? mark)
    {
        mark = null;
        try
        {
            var items = viewHost.GetItemsById(id);
            if (items.Count == 1 && items.First() is VcxFilterMark found)
            {
                mark = found;
                return true;
            }
        }
        catch
        {
            // Fall through safely.
        }
        return false;
    }

    public static bool TryResolveDirectory(VcxFilterMark mark, out VirtualFileSystemPath directoryPath)
    {
        directoryPath = VirtualFileSystemPath.GetEmptyPathFor(InteractionContext.SolutionContext);
        var project = GetOwningProject(mark);
        if (project == null || !project.IsValid())
            return false;

        var solution = project.GetSolution();
        if (TryGetPreloadedMap(solution, project, out var map) && map != null)
        {
            if (map.TryResolve(mark.FilterPath, Directory.Exists, out var resolvedStr))
            {
                directoryPath = VirtualFileSystemPath.Parse(resolvedStr, InteractionContext.SolutionContext);
                return !directoryPath.IsEmpty;
            }
            if (map.ConflictedFilters.Contains(NormalizeFilterPath(mark.FilterPath)))
                return false;
        }

        // Fallback 1: inspect direct child IProjectFile nodes already attached under this mark.
        try
        {
            var viewHost = solution.TryGetComponent<ProjectModelViewHost>();
            if (viewHost != null && viewHost.Light.HasItem(mark))
            {
                VirtualFileSystemPath? commonDir = null;
                var conflict = false;
                foreach (var child in viewHost.Light.GetChildrenItems(mark).OfType<IProjectFile>())
                {
                    var dir = child.Location.Directory;
                    if (dir.IsEmpty) continue;
                    if (commonDir == null)
                    {
                        commonDir = dir;
                    }
                    else if (!VcxFiltersHelper.AreSamePath(commonDir, dir))
                    {
                        conflict = true;
                        break;
                    }
                }
                if (!conflict && commonDir != null && !commonDir.IsEmpty)
                {
                    directoryPath = commonDir;
                    return true;
                }
            }
        }
        catch
        {
            // Best-effort fallback only.
        }

        // Fallback 2: Unreal Intermediate\ProjectFiles -> two levels up + filterPath.
        var fallbackRoot = TryInferUnrealSourceRoot(project.ProjectFileLocation.Directory.FullPath, Directory.Exists);
        if (!string.IsNullOrEmpty(fallbackRoot))
        {
            var candidate = Path.Combine(fallbackRoot, NormalizeFilterPath(mark.FilterPath));
            if (Directory.Exists(candidate))
            {
                directoryPath = VirtualFileSystemPath.Parse(NormalizeDirPath(candidate), InteractionContext.SolutionContext);
                return !directoryPath.IsEmpty;
            }
        }

        return false;
    }

    public static bool TryResolveProjectSourceRoot(ISolution solution, IProject project, out VirtualFileSystemPath sourceRoot)
    {
        sourceRoot = VirtualFileSystemPath.GetEmptyPathFor(InteractionContext.SolutionContext);
        if (TryGetPreloadedMap(solution, project, out var map) && !string.IsNullOrEmpty(map?.ProjectSourceRoot))
        {
            sourceRoot = VirtualFileSystemPath.Parse(map.ProjectSourceRoot, InteractionContext.SolutionContext);
            return !sourceRoot.IsEmpty;
        }

        var fallbackRoot = TryInferUnrealSourceRoot(project.ProjectFileLocation.Directory.FullPath, Directory.Exists);
        if (!string.IsNullOrEmpty(fallbackRoot))
        {
            sourceRoot = VirtualFileSystemPath.Parse(fallbackRoot, InteractionContext.SolutionContext);
            return !sourceRoot.IsEmpty;
        }

        return false;
    }

    public static bool TryResolveExistingProjectFolder(VcxFilterMark mark, out IProjectFolder? folder)
    {
        folder = null;
        if (!TryResolveDirectory(mark, out var dirPath) || dirPath.IsEmpty)
            return false;

        var project = GetOwningProject(mark);
        if (project == null || !project.IsValid())
            return false;

        return TryFindExistingProjectFolder(project, dirPath, out folder);
    }

    public static bool TryFindExistingProjectFolder(IProject project, VirtualFileSystemPath dirPath, out IProjectFolder? folder)
    {
        folder = null;
        if (dirPath.IsEmpty || !project.IsValid())
            return false;

        try
        {
            using (project.Locks.UsingReadLock("VcxFilterFolderResolver.cs", nameof(TryFindExistingProjectFolder)))
            {
                foreach (var candidate in project.FindProjectItemsByLocation(dirPath).OfType<IProjectFolder>())
                {
                    if (candidate is not IProject && candidate.IsValid())
                    {
                        folder = candidate;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Do not crash if lock/project state is transitional.
        }
        return false;
    }

    private static bool TryGetPreloadedMap(ISolution solution, IProject project, out ResolvedFilterMap? map)
    {
        map = null;
        try
        {
            var appender = solution.TryGetComponent<VcxFiltersAppender>();
            if (appender == null || !appender.TryGetPreloadedFilterData(project, out var data) || data == null)
                return false;

            map = PreloadCache.GetValue(data, preloaded => BuildMapFromPreloadedData(project.ProjectFileLocation.Directory.FullPath, preloaded));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ResolvedFilterMap BuildMapFromPreloadedData(string projectDirPath, PreloadedFilterData data)
    {
        var filterFiles = new Dictionary<string, IReadOnlyList<string>>(data.FiltersByPath.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, info) in data.FiltersByPath)
        {
            filterFiles[path] = info.Files;
        }
        return ComputeResolvedDirectories(projectDirPath, filterFiles, Directory.Exists);
    }

    private static string? InferProjectSourceRoot(
        string normalizedProjectDir,
        List<string> topLevelFilters,
        Dictionary<string, string> resolved,
        Func<string, bool> directoryExists)
    {
        var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var top in topLevelFilters)
        {
            if (!resolved.TryGetValue(top, out var dir))
                continue;
            if (!string.Equals(Path.GetFileName(dir), top, StringComparison.OrdinalIgnoreCase))
                continue;

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent))
                continue;

            var normParent = NormalizeDirPath(parent);
            candidates[normParent] = candidates.TryGetValue(normParent, out var count) ? count + 1 : 1;
        }

        if (candidates.Count > 0)
        {
            return candidates
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => IsPathAncestorOrEqual(kv.Key, normalizedProjectDir))
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key;
        }

        return TryInferUnrealSourceRoot(normalizedProjectDir, directoryExists);
    }

    private static string? TryInferUnrealSourceRoot(string normalizedProjectDir, Func<string, bool> directoryExists)
    {
        var dirName = Path.GetFileName(normalizedProjectDir);
        if (!string.Equals(dirName, "ProjectFiles", StringComparison.OrdinalIgnoreCase))
            return null;

        var intermediateDir = Path.GetDirectoryName(normalizedProjectDir);
        if (string.IsNullOrEmpty(intermediateDir) ||
            !string.Equals(Path.GetFileName(intermediateDir), "Intermediate", StringComparison.OrdinalIgnoreCase))
            return null;

        var root = Path.GetDirectoryName(intermediateDir);
        if (string.IsNullOrEmpty(root))
            return null;

        var normalizedRoot = NormalizeDirPath(root);
        return directoryExists(normalizedRoot) ? normalizedRoot : null;
    }

    private static bool IsPathAncestorOrEqual(string ancestor, string path)
    {
        if (string.Equals(ancestor, path, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = ancestor.EndsWith('\\') ? ancestor : ancestor + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeFilterPath(string? filterPath)
    {
        if (string.IsNullOrWhiteSpace(filterPath))
            return string.Empty;
        return filterPath.Trim().Replace('/', '\\').Trim('\\');
    }

    public static string? GetParentFilterPath(string normalizedFilterPath)
    {
        var idx = normalizedFilterPath.LastIndexOf('\\');
        return idx > 0 ? normalizedFilterPath[..idx] : null;
    }

    public static string GetLeafName(string normalizedFilterPath)
    {
        var idx = normalizedFilterPath.LastIndexOf('\\');
        return idx >= 0 ? normalizedFilterPath[(idx + 1)..] : normalizedFilterPath;
    }

    private static int GetDepth(string normalizedFilterPath)
    {
        var depth = 0;
        foreach (var ch in normalizedFilterPath)
        {
            if (ch == '\\') depth++;
        }
        return depth;
    }

    private static string NormalizeDirPath(string dirPath)
    {
        var full = Path.GetFullPath(dirPath);
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;
        return full.TrimEnd('\\', '/');
    }
}
