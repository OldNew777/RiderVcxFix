using System.Reflection;
using JetBrains.Application.Components;
using JetBrains.Application.Parts;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.DocumentManagers.Transactions;
using JetBrains.DocumentManagers.Transactions.ProjectHostActions.Modifications;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter;
using JetBrains.ProjectModel.ProjectsHost.SolutionHost;
using JetBrains.ProjectModel.Tasks;
using JetBrains.RdBackend.Common.Features.ProjectModel;
using JetBrains.RdBackend.Common.Features.ProjectModel.View;
using JetBrains.RdBackend.Common.Features.ProjectModel.View.Ordering;
using JetBrains.RdBackend.Common.Features.Util;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharperCpp.RiderPlugin.ProjectModel.Vcxproj.View;
using JetBrains.Rider.Model;
using JetBrains.Util;

namespace RiderVcxFix.Backend;

/// <summary>
/// Pure-managed SolutionComponent that wraps ProjectModelTasks RD endpoints (Copy, AddItems,
/// Move, Remove, Rename) after ProjectModelTaskHandler binds them — avoiding Harmony detours
/// on JetBrains.RdBackend.Common.dll completely.
/// </summary>
[SolutionComponent(Instantiation.ContainerAsyncPrimaryThread)]
public sealed class VcxFilterTaskInterceptor
{
    private readonly ISolution mySolution;
    private readonly IShellLocks myLocks;
    private readonly ProjectModelViewHost myViewHost;
    private readonly OrderingContextFactory myOrderingFactory;
    private readonly ISolutionHost mySolutionHost;
    private int myAttached;

    private Func<RdCopyItemsCommand, IProgressIndicator, int>? myOrigCopy;
    private Func<RdAddItemsCommand, IProgressIndicator, RdAddItemsResult>? myOrigAddItems;
    private Func<RdMoveItemsCommand, IProgressIndicator, int>? myOrigMove;
    private Action<RdRemoveItemsCommand, IProgressIndicator>? myOrigRemove;
    private Action<RdRenameItemsCommand, IProgressIndicator>? myOrigRename;

    public VcxFilterTaskInterceptor(
        Lifetime lifetime,
        ISolution solution,
        IShellLocks locks,
        ProjectModelViewHost viewHost,
        OrderingContextFactory orderingFactory,
        ISolutionLoadTasksScheduler scheduler)
    {
        mySolution = solution;
        myLocks = locks;
        myViewHost = viewHost;
        myOrderingFactory = orderingFactory;
        mySolutionHost = solution.ProjectsHostContainer().GetComponent<ISolutionHost>();

        Diagnostics.Write($"task-interceptor-created solution='{solution.Name}'");

        scheduler.EnqueueTask(new SolutionLoadTask(
            GetType(),
            "VcxFilterTaskInterceptor.Attach",
            SolutionLoadTaskKinds.SolutionContainerComposed,
            () => TryAttach("SolutionContainerComposed")));

        scheduler.EnqueueTask(new SolutionLoadTask(
            GetType(),
            "VcxFilterTaskInterceptor.AttachFallback",
            SolutionLoadTaskKinds.Done,
            () => TryAttach("Done")));
    }

    private void TryAttach(string phase)
    {
        if (Volatile.Read(ref myAttached) != 0)
            return;

        try
        {
            var taskHandler = mySolution.TryGetComponent<ProjectModelTaskHandler>();
            if (taskHandler == null)
            {
                Diagnostics.Write($"task-interceptor-deferred phase={phase} reason=ProjectModelTaskHandler-null");
                return;
            }

            if (Interlocked.CompareExchange(ref myAttached, 1, 0) != 0)
                return;

            var handlerType = typeof(ProjectModelTaskHandler);
            myOrigCopy = CreateFunc<RdCopyItemsCommand, int>(handlerType, taskHandler, "CopyHandler");
            myOrigAddItems = CreateFunc<RdAddItemsCommand, RdAddItemsResult>(handlerType, taskHandler, "AddItemsHandler");
            myOrigMove = CreateFunc<RdMoveItemsCommand, int>(handlerType, taskHandler, "MoveHandler");
            myOrigRemove = CreateAction<RdRemoveItemsCommand>(handlerType, taskHandler, "RemoveHandler");
            myOrigRename = CreateAction<RdRenameItemsCommand>(handlerType, taskHandler, "RenameHandler");

            var tasks = mySolution.GetProtocolSolution().GetProjectModelTasks();
            tasks.Copy.Set(HandleCopy);
            tasks.AddItems.Set(HandleAddItems);
            tasks.Move.Set(HandleMove);
            tasks.Remove.Set(HandleRemove);
            tasks.Rename.Set(HandleRename);

            Diagnostics.Write($"task-interceptor-attached phase={phase} solution='{mySolution.Name}' endpoints=Copy,AddItems,Move,Remove,Rename");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref myAttached, 0);
            Diagnostics.Write($"task-interceptor-attach-failed phase={phase} error={ex}");
        }
    }

    private static Func<TReq, IProgressIndicator, TRes> CreateFunc<TReq, TRes>(Type type, object instance, string methodName)
    {
        var method = type.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(TReq), typeof(IProgressIndicator) },
            null) ?? throw new MissingMethodException(type.FullName, methodName);
        return (Func<TReq, IProgressIndicator, TRes>)Delegate.CreateDelegate(
            typeof(Func<TReq, IProgressIndicator, TRes>), instance, method);
    }

    private static Action<TReq, IProgressIndicator> CreateAction<TReq>(Type type, object instance, string methodName)
    {
        var method = type.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(TReq), typeof(IProgressIndicator) },
            null) ?? throw new MissingMethodException(type.FullName, methodName);
        return (Action<TReq, IProgressIndicator>)Delegate.CreateDelegate(
            typeof(Action<TReq, IProgressIndicator>), instance, method);
    }

    // -------------------------------------------------------------------------
    // Copy: route to real physical directory + add to .vcxproj & .vcxproj.filters
    // -------------------------------------------------------------------------
    private int HandleCopy(RdCopyItemsCommand command, IProgressIndicator pi)
    {
        if (!VcxFilterFolderViewPatch.IsActive)
            return myOrigCopy!(command, pi);

        var targetIsMark = VcxFilterFolderResolver.TryGetMarkById(myViewHost, command.ParentId, out var targetMark) && targetMark != null;
        var anySourceIsMark = command.Items.Any(x => VcxFilterFolderResolver.TryGetMarkById(myViewHost, x.ItemId, out _));
        if (!targetIsMark && !anySourceIsMark)
            return myOrigCopy!(command, pi);

        mySolution.AssertProjectModelReadyToModifications();

        IProject? targetProject;
        VirtualFileSystemPath targetDirPath;
        string? targetFilterPath = null;
        IProjectFolder? existingTargetFolder = null;

        if (targetIsMark)
        {
            targetProject = VcxFilterFolderResolver.GetOwningProject(targetMark!);
            targetFilterPath = targetMark!.FilterPath;
            if (targetProject == null || !targetProject.IsValid())
                return myOrigCopy!(command, pi);

            if (!VcxFilterFolderResolver.TryResolveDirectory(targetMark!, out targetDirPath) || targetDirPath.IsEmpty)
            {
                if (command.Items.Count > 0 && TryResolveSourcePath(command.Items[0].ItemId, out var firstSrc, out var firstIsDir) && !firstIsDir)
                    targetDirPath = firstSrc.Directory;
                else
                    return myOrigCopy!(command, pi);
            }
            VcxFilterFolderResolver.TryFindExistingProjectFolder(targetProject, targetDirPath, out existingTargetFolder);
        }
        else
        {
            existingTargetFolder = myViewHost.GetProjectFolderAsParent(command.ParentId);
            targetProject = existingTargetFolder.GetProject();
            if (targetProject == null || !targetProject.IsValid())
                return myOrigCopy!(command, pi);
            targetDirPath = existingTargetFolder.Location;
        }

        if (targetDirPath.IsEmpty || !targetDirPath.ExistsDirectory)
            return myOrigCopy!(command, pi);

        var copySpecs = new List<(VirtualFileSystemPath SourcePath, bool IsDirectory, string DestName, VirtualFileSystemPath DestPath)>();
        foreach (var itemData in command.Items)
        {
            if (!TryResolveSourcePath(itemData.ItemId, out var srcPath, out var isDir) || srcPath.IsEmpty)
                throw new ProjectModelEditorException("Unable to resolve source item with id {0}", itemData.ItemId);

            var destName = string.IsNullOrEmpty(itemData.CustomName) ? srcPath.Name : itemData.CustomName;
            var destPath = targetDirPath.Combine(destName);

            if (isDir && (VcxFiltersHelper.AreSamePath(srcPath, destPath) || srcPath.IsPrefixOf(destPath)))
                throw new ProjectModelEditorException("Cannot copy directory '{0}' into itself.", srcPath.Name);

            if (destPath.ExistsFile || destPath.ExistsDirectory)
                throw new ProjectModelEditorException("Item with the name '{0}' already exists in '{1}'.", destName, targetDirPath.FullPath);

            copySpecs.Add((srcPath, isDir, destName, destPath));
        }

        Diagnostics.Write($"copy-intercept targetFilter='{targetFilterPath ?? "<none>"}' targetDir='{targetDirPath.FullPath}' count={copySpecs.Count}");

        var createdItems = new List<IProjectItem>();
        pi.Start(copySpecs.Count);

        Lifetime.Using(lifetime =>
        {
            mySolutionHost.SuspendAsyncProjectsReloading(lifetime);
            IProjectFolder addParent = existingTargetFolder ?? targetProject;

            foreach (var spec in copySpecs)
            {
                if (pi.IsCanceled) break;
                pi.CurrentItemText = spec.DestName;

                if (spec.IsDirectory)
                    CopyDirectoryRecursive(spec.SourcePath.FullPath, spec.DestPath.FullPath);
                else
                    File.Copy(spec.SourcePath.FullPath, spec.DestPath.FullPath, overwrite: false);

                using (targetIsMark ? targetMark!.BeginAddFileScope() : NoOpDisposable.Instance)
                {
                    using (myLocks.UsingReadLock("VcxFilterTaskInterceptor.cs", nameof(HandleCopy)))
                    {
                        var context = myOrderingFactory.Create(targetProject, command.RelativeTo);
                        IProjectItem? created = null;
                        mySolution.InvokeUnderTransaction(cookie =>
                        {
                            AddItemTaskAction action = addParent.Location.IsPrefixOf(spec.DestPath)
                                ? new AddItemPreserveDirectoriesTaskAction(lifetime, cookie, addParent, spec.DestPath, context, pi, includeContent: true)
                                : new AddItemAsLinkTaskAction(lifetime, cookie, addParent, spec.DestPath, context, pi, includeContent: true);
                            created = action.Execute();
                        });
                        if (created != null && created.IsValid())
                            createdItems.Add(created);
                    }
                }

                if (spec.IsDirectory && VcxFiltersHelper.IsVcxProject(targetProject) && VcxFiltersHelper.FiltersFileExists(targetProject.ProjectFileLocation))
                {
                    EnqueueDirectoryFilters(targetProject, spec.DestPath, targetDirPath, targetFilterPath);
                }

                pi.Advance();
            }
        });

        var firstCreated = createdItems.FirstOrDefault();
        var resultId = firstCreated != null ? myViewHost.GetIdByItem(firstCreated) : 0;
        Diagnostics.Write($"copy-intercept-done created={createdItems.Count} firstId={resultId}");
        return resultId;
    }

    // -------------------------------------------------------------------------
    // AddItems: write <Filter> to .vcxproj.filters for new empty dirs & dragged folders
    // -------------------------------------------------------------------------
    private RdAddItemsResult HandleAddItems(RdAddItemsCommand command, IProgressIndicator pi)
    {
        if (!VcxFilterFolderViewPatch.IsActive || command.Items.Count == 0)
            return myOrigAddItems!(command, pi);

        // Fast-path: all items are empty directories inside a VCX project with .vcxproj.filters
        // (right-click -> New Directory). Avoids linked-folder leaf-name collisions at .vcxproj root.
        var emptyDirTargets = TryCollectAllEmptyDirTargets(command);
        if (emptyDirTargets != null)
        {
            var results = new List<RdAddItemDataResult>(emptyDirTargets.Count);
            foreach (var (itemData, location, project, parentDir, parentFilterPath) in emptyDirTargets)
            {
                EnqueueDirectoryFilters(project, location, parentDir, parentFilterPath);
                results.Add(new RdAddItemDataResult(itemData.ItemLocation, null, true));
            }
            Diagnostics.Write($"add-empty-dir-intercepted count={results.Count}");
            return new RdAddItemsResult(results);
        }

        // Standard path for files and non-empty directories (New File, Explorer drag-drop).
        var result = myOrigAddItems!(command, pi);

        try
        {
            for (var i = 0; i < command.Items.Count; i++)
            {
                if (result != null && i < result.Items.Count && !result.Items[i].Success)
                    continue;

                var itemData = command.Items[i];
                var rawPath = itemData.ItemLocation?.Value;
                if (string.IsNullOrEmpty(rawPath)) continue;
                var location = VirtualFileSystemPath.Parse(rawPath, InteractionContext.SolutionContext);
                if (location.IsEmpty || !location.ExistsDirectory) continue;

                if (!TryResolveAddDirectoryTarget(itemData.ParentId, location, out var project, out var parentDirPath, out var parentFilterPath) || project == null)
                    continue;

                EnqueueDirectoryFilters(project, location, parentDirPath, parentFilterPath);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"add-items-post-error error={ex}");
        }

        return result!;
    }

    // -------------------------------------------------------------------------
    // Move: physically move files/folders between folder-presented marks
    // -------------------------------------------------------------------------
    private int HandleMove(RdMoveItemsCommand command, IProgressIndicator pi)
    {
        if (!VcxFilterFolderViewPatch.IsActive)
            return myOrigMove!(command, pi);

        var targetIsMark = VcxFilterFolderResolver.TryGetMarkById(myViewHost, command.ParentId, out var targetMark) && targetMark != null;
        var anySourceIsMark = command.Items.Any(x => VcxFilterFolderResolver.TryGetMarkById(myViewHost, x.ItemId, out _));
        if (!targetIsMark && !anySourceIsMark)
            return myOrigMove!(command, pi);

        IProject? targetProject;
        VirtualFileSystemPath targetDirPath;
        string? targetFilterPath = null;
        IProjectFolder? existingTargetFolder = null;

        if (targetIsMark)
        {
            targetProject = VcxFilterFolderResolver.GetOwningProject(targetMark!);
            targetFilterPath = targetMark!.FilterPath;
            if (targetProject == null || !targetProject.IsValid())
                return myOrigMove!(command, pi);
            if (!VcxFilterFolderResolver.TryResolveDirectory(targetMark!, out targetDirPath) || targetDirPath.IsEmpty || !targetDirPath.ExistsDirectory)
                return myOrigMove!(command, pi);
            VcxFilterFolderResolver.TryFindExistingProjectFolder(targetProject, targetDirPath, out existingTargetFolder);
        }
        else
        {
            existingTargetFolder = myViewHost.TryGetProjectFolderAsParent(command.ParentId);
            if (existingTargetFolder == null)
                return myOrigMove!(command, pi);
            targetProject = existingTargetFolder.GetProject();
            if (targetProject == null || !targetProject.IsValid())
                return myOrigMove!(command, pi);
            targetDirPath = existingTargetFolder.Location;
        }

        var filesToMove = new List<IProjectFile>();
        var marksToMove = new List<(VcxFilterMark Mark, VirtualFileSystemPath Dir)>();

        foreach (var itemData in command.Items)
        {
            if (VcxFilterFolderResolver.TryGetMarkById(myViewHost, itemData.ItemId, out var srcMark) && srcMark != null)
            {
                if (!VcxFilterFolderResolver.TryResolveDirectory(srcMark, out var srcDir) || srcDir.IsEmpty || !srcDir.ExistsDirectory)
                    return myOrigMove!(command, pi);
                marksToMove.Add((srcMark, srcDir));
            }
            else if (myViewHost.GetItemById<IProjectFile>(itemData.ItemId) is { } pf)
            {
                filesToMove.Add(pf);
            }
            else
            {
                return myOrigMove!(command, pi);
            }
        }

        if (marksToMove.Count > 0 && filesToMove.Count == 0 && targetIsMark)
        {
            var vcxService = mySolution.TryGetComponent<VcxFilterService>();
            if (vcxService == null)
                return myOrigMove!(command, pi);

            mySolution.AssertProjectModelReadyToModifications();
            foreach (var (srcMark, srcDir) in marksToMove)
            {
                var srcProject = VcxFilterFolderResolver.GetOwningProject(srcMark);
                if (srcProject == null || !VcxFiltersHelper.AreSamePath(srcProject.ProjectFileLocation, targetProject.ProjectFileLocation))
                    return myOrigMove!(command, pi);
                if (VcxFiltersHelper.IsCyclicMove(srcMark.FilterPath, targetFilterPath))
                    throw new ProjectModelEditorException("Cannot move folder '{0}' into itself or its descendant.", srcMark.Name);

                var destDir = targetDirPath.Combine(srcMark.Name);
                if (destDir.ExistsDirectory || destDir.ExistsFile)
                    throw new ProjectModelEditorException("Directory '{0}' already exists in '{1}'.", srcMark.Name, targetDirPath.FullPath);

                if (VcxFilterFolderResolver.TryFindExistingProjectFolder(srcProject, srcDir, out var srcFolder) && srcFolder != null && existingTargetFolder != null)
                {
                    mySolution.InvokeUnderTransaction(cookie =>
                    {
                        if (!cookie.CanMove(srcFolder, existingTargetFolder, out var reason))
                            throw new ProjectModelEditorException(reason);
                        cookie.Move(srcFolder, existingTargetFolder);
                    });
                }
                else
                {
                    Directory.Move(srcDir.FullPath, destDir.FullPath);
                }

                var projPath = targetProject.ProjectFileLocation;
                var oldFilterPath = srcMark.FilterPath;
                var newParent = targetFilterPath;
                vcxService.EnqueueProjectWork(projPath, () =>
                {
                    vcxService.MoveFilter(projPath, oldFilterPath, newParent);
                });
            }
            return 0;
        }

        if (filesToMove.Count == 0)
            return myOrigMove!(command, pi);

        mySolution.AssertProjectModelReadyToModifications();
        Diagnostics.Write($"move-intercept targetFilter='{targetFilterPath ?? "<none>"}' targetDir='{targetDirPath.FullPath}' files={filesToMove.Count}");

        var movedItems = new List<IProjectItem>();
        var movedPathsForFilter = new List<string>();
        pi.Start(filesToMove.Count);

        Lifetime.Using(lifetime =>
        {
            mySolutionHost.SuspendAsyncProjectsReloading(lifetime);
            IProjectFolder addParent = existingTargetFolder ?? targetProject;

            foreach (var file in filesToMove)
            {
                if (pi.IsCanceled) break;
                pi.CurrentItemText = file.Name;

                var srcPath = file.Location;
                var destPath = targetDirPath.Combine(file.Name);
                if (VcxFiltersHelper.AreSamePath(srcPath, destPath))
                {
                    movedPathsForFilter.Add(destPath.FullPath);
                    movedItems.Add(file);
                    pi.Advance();
                    continue;
                }

                if (destPath.ExistsFile || destPath.ExistsDirectory)
                    throw new ProjectModelEditorException("File '{0}' already exists in '{1}'.", file.Name, targetDirPath.FullPath);

                var movedViaCookie = false;
                if (existingTargetFolder != null)
                {
                    try
                    {
                        mySolution.InvokeUnderTransaction(cookie =>
                        {
                            if (cookie.CanMove(file, existingTargetFolder, out _))
                            {
                                cookie.Move(file, existingTargetFolder);
                                movedViaCookie = true;
                            }
                        });
                    }
                    catch
                    {
                        movedViaCookie = false;
                    }
                }

                if (movedViaCookie)
                {
                    movedItems.Add(file);
                    movedPathsForFilter.Add(destPath.FullPath);
                }
                else
                {
                    File.Move(srcPath.FullPath, destPath.FullPath);
                    using (targetIsMark ? targetMark!.BeginAddFileScope() : NoOpDisposable.Instance)
                    {
                        using (myLocks.UsingReadLock("VcxFilterTaskInterceptor.cs", nameof(HandleMove)))
                        {
                            var context = myOrderingFactory.Create(targetProject, command.RelativeTo);
                            IProjectItem? added = null;
                            mySolution.InvokeUnderTransaction(cookie =>
                            {
                                if (file.IsValid() && cookie.CanRemove(file, out _))
                                    cookie.Remove(file);

                                AddItemTaskAction action = addParent.Location.IsPrefixOf(destPath)
                                    ? new AddItemPreserveDirectoriesTaskAction(lifetime, cookie, addParent, destPath, context, pi, includeContent: true)
                                    : new AddItemAsLinkTaskAction(lifetime, cookie, addParent, destPath, context, pi, includeContent: true);
                                added = action.Execute();
                            });
                            if (added != null && added.IsValid())
                                movedItems.Add(added);
                        }
                    }
                    movedPathsForFilter.Add(destPath.FullPath);
                }

                pi.Advance();
            }
        });

        if (targetIsMark && movedPathsForFilter.Count > 0 && VcxFiltersHelper.FiltersFileExists(targetProject.ProjectFileLocation))
        {
            var vcxService = mySolution.TryGetComponent<VcxFilterService>();
            if (vcxService != null)
            {
                var projPath = targetProject.ProjectFileLocation;
                var filter = targetFilterPath!;
                vcxService.EnqueueProjectWork(projPath, () =>
                {
                    vcxService.BatchMoveFiles(projPath, movedPathsForFilter, filter);
                });
            }
        }

        var firstMoved = movedItems.FirstOrDefault();
        return firstMoved != null ? myViewHost.GetIdByItem(firstMoved) : 0;
    }

    // -------------------------------------------------------------------------
    // Remove: safely handle deleting folder-presented marks
    // -------------------------------------------------------------------------
    private void HandleRemove(RdRemoveItemsCommand command, IProgressIndicator pi)
    {
        if (!VcxFilterFolderViewPatch.IsActive || command.ItemIds.Count == 0)
        {
            myOrigRemove!(command, pi);
            return;
        }

        var markIds = new List<(int Id, VcxFilterMark Mark)>();
        var otherIds = new List<int>();
        foreach (var id in command.ItemIds)
        {
            if (VcxFilterFolderResolver.TryGetMarkById(myViewHost, id, out var mark) && mark != null)
                markIds.Add((id, mark));
            else
                otherIds.Add(id);
        }

        if (markIds.Count == 0)
        {
            myOrigRemove!(command, pi);
            return;
        }

        mySolution.AssertProjectModelReadyToModifications();
        var vcxService = mySolution.TryGetComponent<VcxFilterService>();

        Lifetime.Using(lifetime =>
        {
            mySolutionHost.SuspendAsyncProjectsReloading(lifetime);
            foreach (var (_, mark) in markIds)
            {
                var project = VcxFilterFolderResolver.GetOwningProject(mark);
                if (project == null || !project.IsValid())
                    continue;

                var hasDir = VcxFilterFolderResolver.TryResolveDirectory(mark, out var dirPath) && !dirPath.IsEmpty;
                if (hasDir && VcxFilterFolderResolver.TryFindExistingProjectFolder(project, dirPath, out var existingFolder) && existingFolder != null)
                {
                    mySolution.InvokeUnderTransaction(cookie =>
                    {
                        if (cookie.CanRemove(existingFolder, out var reason))
                            cookie.Remove(existingFolder);
                        else
                            Diagnostics.Write($"remove-mark-folder-skipped filter='{mark.FilterPath}' reason='{reason}'");
                    });
                }

                var childFiles = CollectFilesUnderMarkRecursive(mark);
                if (childFiles.Count > 0)
                {
                    mySolution.InvokeUnderTransaction(cookie =>
                    {
                        foreach (var f in childFiles)
                        {
                            if (f.IsValid() && cookie.CanRemove(f, out _))
                                cookie.Remove(f);
                        }
                    });
                }

                if (hasDir && dirPath.ExistsDirectory)
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dirPath.FullPath).Any())
                            Directory.Delete(dirPath.FullPath, recursive: false);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Write($"remove-empty-dir-failed dir='{dirPath.FullPath}' error={ex.Message}");
                    }
                }

                if (vcxService != null && VcxFiltersHelper.FiltersFileExists(project.ProjectFileLocation))
                {
                    var projPath = project.ProjectFileLocation;
                    var filterPath = mark.FilterPath;
                    vcxService.EnqueueProjectWork(projPath, () =>
                    {
                        vcxService.DeleteFilter(projPath, filterPath, orphanFiles: false);
                    });
                }
            }
        });

        if (otherIds.Count > 0)
        {
            myOrigRemove!(new RdRemoveItemsCommand(otherIds), pi);
        }
    }

    // -------------------------------------------------------------------------
    // Rename: safely handle F2 rename on folder-presented marks
    // -------------------------------------------------------------------------
    private void HandleRename(RdRenameItemsCommand command, IProgressIndicator pi)
    {
        if (!VcxFilterFolderViewPatch.IsActive || command.Items.Count == 0)
        {
            myOrigRename!(command, pi);
            return;
        }

        var markItems = new List<(RdRenameItemData Data, VcxFilterMark Mark)>();
        var otherItems = new List<RdRenameItemData>();
        foreach (var item in command.Items)
        {
            if (VcxFilterFolderResolver.TryGetMarkById(myViewHost, item.Id, out var mark) && mark != null)
                markItems.Add((item, mark));
            else
                otherItems.Add(item);
        }

        if (markItems.Count == 0)
        {
            myOrigRename!(command, pi);
            return;
        }

        mySolution.AssertProjectModelReadyToModifications();
        var vcxService = mySolution.TryGetComponent<VcxFilterService>();

        foreach (var (item, mark) in markItems)
        {
            var project = VcxFilterFolderResolver.GetOwningProject(mark);
            if (project == null || !project.IsValid())
                continue;

            var newFilterPath = VcxFilterPathUtil.Combine(mark.ParentFilterPath, item.Name);
            var hasDir = VcxFilterFolderResolver.TryResolveDirectory(mark, out var dirPath) && !dirPath.IsEmpty;

            if (hasDir && VcxFilterFolderResolver.TryFindExistingProjectFolder(project, dirPath, out var existingFolder) && existingFolder != null)
            {
                mySolution.InvokeUnderTransaction(cookie =>
                {
                    if (!cookie.CanRename(existingFolder, item.Name, out var reason))
                        throw new ProjectModelEditorException(reason);
                    cookie.Rename(existingFolder, item.Name, renameDependant: true);
                });
            }
            else if (hasDir && dirPath.ExistsDirectory)
            {
                var targetDir = dirPath.Parent.Combine(item.Name);
                if (targetDir.ExistsDirectory || targetDir.ExistsFile)
                    throw new ProjectModelEditorException("Directory '{0}' already exists.", targetDir.FullPath);
                Directory.Move(dirPath.FullPath, targetDir.FullPath);
            }

            if (vcxService != null && VcxFiltersHelper.FiltersFileExists(project.ProjectFileLocation))
            {
                var projPath = project.ProjectFileLocation;
                var oldFilterPath = mark.FilterPath;
                vcxService.EnqueueProjectWork(projPath, () =>
                {
                    vcxService.RenameFilter(projPath, oldFilterPath, newFilterPath);
                });
            }
        }

        if (otherItems.Count > 0)
        {
            myOrigRename!(new RdRenameItemsCommand(otherItems), pi);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private List<(RdAddItemData ItemData, VirtualFileSystemPath Location, IProject Project, VirtualFileSystemPath ParentDir, string? ParentFilterPath)>?
        TryCollectAllEmptyDirTargets(RdAddItemsCommand command)
    {
        var list = new List<(RdAddItemData, VirtualFileSystemPath, IProject, VirtualFileSystemPath, string?)>();
        foreach (var itemData in command.Items)
        {
            var rawPath = itemData.ItemLocation?.Value;
            if (string.IsNullOrEmpty(rawPath)) return null;
            var location = VirtualFileSystemPath.Parse(rawPath, InteractionContext.SolutionContext);
            if (location.IsEmpty || !location.ExistsDirectory) return null;
            if (Directory.EnumerateFileSystemEntries(location.FullPath).Any()) return null;

            if (!TryResolveAddDirectoryTarget(itemData.ParentId, location, out var project, out var parentDir, out var parentFilterPath) || project == null)
                return null;

            list.Add((itemData, location, project, parentDir, parentFilterPath));
        }
        return list.Count > 0 ? list : null;
    }

    private bool TryResolveAddDirectoryTarget(
        int parentId,
        VirtualFileSystemPath location,
        out IProject? project,
        out VirtualFileSystemPath parentDirPath,
        out string? parentFilterPath)
    {
        project = null;
        parentFilterPath = null;
        parentDirPath = VirtualFileSystemPath.GetEmptyPathFor(InteractionContext.SolutionContext);

        if (VcxFilterFolderResolver.TryGetMarkById(myViewHost, parentId, out var parentMark) && parentMark != null)
        {
            project = VcxFilterFolderResolver.GetOwningProject(parentMark);
            parentFilterPath = parentMark.FilterPath;
            VcxFilterFolderResolver.TryResolveDirectory(parentMark, out parentDirPath);
        }
        else
        {
            var folder = myViewHost.TryGetProjectFolderAsParent(parentId);
            project = folder?.GetProject();
            if (project != null)
            {
                parentFilterPath = null;
                if (!VcxFilterFolderResolver.TryResolveProjectSourceRoot(mySolution, project, out parentDirPath))
                {
                    parentDirPath = folder?.Location ?? VirtualFileSystemPath.GetEmptyPathFor(InteractionContext.SolutionContext);
                }
            }
        }

        if (project == null || !project.IsValid() || !VcxFiltersHelper.IsVcxProject(project))
            return false;

        if (!VcxFiltersHelper.FiltersFileExists(project.ProjectFileLocation))
            return false;

        if (parentDirPath.IsEmpty || !parentDirPath.IsPrefixOf(location))
            parentDirPath = location.Directory;

        return true;
    }

    private bool TryResolveSourcePath(int itemId, out VirtualFileSystemPath path, out bool isDirectory)
    {
        path = VirtualFileSystemPath.GetEmptyPathFor(InteractionContext.SolutionContext);
        isDirectory = false;

        if (VcxFilterFolderResolver.TryGetMarkById(myViewHost, itemId, out var mark) && mark != null)
        {
            if (VcxFilterFolderResolver.TryResolveDirectory(mark, out path) && !path.IsEmpty)
            {
                isDirectory = true;
                return true;
            }
            return false;
        }

        var projectItem = myViewHost.GetItemById<IProjectItem>(itemId)
            ?? (myViewHost.GetItemById<IProjectElementHolder>(itemId)?.Element as IProjectItem);
        if (projectItem == null)
            return false;

        path = projectItem.Location;
        isDirectory = projectItem is IProjectFolder;
        return !path.IsEmpty;
    }

    private List<IProjectFile> CollectFilesUnderMarkRecursive(VcxFilterMark root)
    {
        var result = new List<IProjectFile>();
        var queue = new Queue<VcxFilterMark>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!myViewHost.Light.HasItem(current))
                continue;

            foreach (var child in myViewHost.Light.GetChildrenItems(current))
            {
                if (child is IProjectFile file)
                    result.Add(file);
                else if (child is VcxFilterMark subMark)
                    queue.Enqueue(subMark);
            }
        }
        return result;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }

    private void EnqueueDirectoryFilters(
        IProject project,
        VirtualFileSystemPath createdDir,
        VirtualFileSystemPath parentDir,
        string? parentFilterPath)
    {
        var vcxService = mySolution.TryGetComponent<VcxFilterService>();
        if (vcxService == null)
            return;

        var projectPath = project.ProjectFileLocation;
        var createdFull = createdDir.FullPath;
        var parentFull = parentDir.FullPath;

        string topRelative;
        try
        {
            topRelative = Path.GetRelativePath(parentFull, createdFull).Replace('/', '\\').Trim('\\');
        }
        catch
        {
            topRelative = createdDir.Name;
        }

        if (string.IsNullOrEmpty(topRelative) || topRelative == "." || topRelative.StartsWith("..", StringComparison.Ordinal))
            topRelative = createdDir.Name;

        var filterPathsToCreate = new List<(string? Parent, string Name)>();
        var filesByTargetFilter = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var currentParent = string.IsNullOrEmpty(parentFilterPath) ? null : parentFilterPath;
        foreach (var segment in topRelative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            filterPathsToCreate.Add((currentParent, segment));
            currentParent = string.IsNullOrEmpty(currentParent) ? segment : currentParent + "\\" + segment;
        }

        var rootFilterForCreatedDir = currentParent!;
        if (Directory.Exists(createdFull))
            CollectSubDirectoriesAndFiles(createdFull, rootFilterForCreatedDir, filterPathsToCreate, filesByTargetFilter);

        Diagnostics.Write($"enqueue-dir-filters project='{project.Name}' rootFilter='{rootFilterForCreatedDir}' filtersToCreate={filterPathsToCreate.Count} fileGroups={filesByTargetFilter.Count}");

        vcxService.EnqueueProjectWork(projectPath, () =>
        {
            foreach (var (parent, name) in filterPathsToCreate)
            {
                try
                {
                    var res = vcxService.CreateFilter(projectPath, name, parent);
                    if (!res.Success)
                        Diagnostics.Write($"create-filter-note parent='{parent ?? "<root>"}' name='{name}' msg='{res.ErrorMessage}'");
                }
                catch (Exception ex)
                {
                    Diagnostics.Write($"create-filter-error parent='{parent ?? "<root>"}' name='{name}' error={ex.Message}");
                }
            }

            foreach (var (targetFilter, filePaths) in filesByTargetFilter)
            {
                if (filePaths.Count == 0) continue;
                try
                {
                    vcxService.BatchMoveFiles(projectPath, filePaths, targetFilter);
                }
                catch (Exception ex)
                {
                    Diagnostics.Write($"assign-dir-files-error filter='{targetFilter}' count={filePaths.Count} error={ex.Message}");
                }
            }
        });
    }

    private static void CollectSubDirectoriesAndFiles(
        string currentDir,
        string currentFilterPath,
        List<(string? Parent, string Name)> filterPathsToCreate,
        Dictionary<string, List<string>> filesByTargetFilter)
    {
        foreach (var file in Directory.GetFiles(currentDir))
        {
            if (!filesByTargetFilter.TryGetValue(currentFilterPath, out var list))
            {
                list = new List<string>();
                filesByTargetFilter[currentFilterPath] = list;
            }
            list.Add(file);
        }

        foreach (var subDir in Directory.GetDirectories(currentDir))
        {
            var subName = Path.GetFileName(subDir);
            if (string.IsNullOrEmpty(subName)) continue;
            filterPathsToCreate.Add((currentFilterPath, subName));
            CollectSubDirectoriesAndFiles(subDir, currentFilterPath + "\\" + subName, filterPathsToCreate, filesByTargetFilter);
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
