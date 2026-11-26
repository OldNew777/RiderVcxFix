package local.rider.vcxfix;

import com.intellij.ide.util.treeView.AbstractTreeNode;
import com.intellij.openapi.actionSystem.DataContext;
import com.intellij.openapi.project.Project;
import com.jetbrains.rd.ide.model.RdPath;
import com.jetbrains.rider.model.RdCustomLocation;
import com.jetbrains.rider.model.RdProjectDescriptor;
import com.jetbrains.rider.model.RdProjectFolderDescriptor;
import com.jetbrains.rider.model.RdProjectModelItemDescriptor;
import com.jetbrains.rider.model.RdSolutionFolderDescriptor;
import com.jetbrains.rider.model.RdVcxFilterDescriptor;
import com.jetbrains.rider.projectView.ProjectModelDataKeys;
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerViewSettings;
import com.jetbrains.rider.projectView.views.solutionExplorer.nodes.SolutionExplorerFileNode;
import com.jetbrains.rider.projectView.views.solutionExplorer.nodes.SolutionExplorerModelNode;
import com.jetbrains.rider.projectView.workspace.ProjectModelEntity;
import sun.misc.Unsafe;

import java.lang.reflect.Field;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/** Tests the actual extension and action classes with real Rider descriptors, without an IDE. */
public final class VcxFilterViewFixFixture {
    public static void main(String[] args) throws Exception {
        Project project = proxy(Project.class, null);
        SolutionExplorerViewSettings settings = proxy(SolutionExplorerViewSettings.class, null);
        VcxFilterViewFix extension = new VcxFilterViewFix(project);

        // Real node constructors require IDE services. These inert instances are only used as type markers.
        Unsafe unsafe = unsafe();
        AbstractTreeNode<?> filterNode = (AbstractTreeNode<?>) unsafe.allocateInstance(SolutionExplorerModelNode.class);
        AbstractTreeNode<?> filterFileNode = (AbstractTreeNode<?>) unsafe.allocateInstance(SolutionExplorerModelNode.class);
        AbstractTreeNode<?> physicalDirectory = (AbstractTreeNode<?>) unsafe.allocateInstance(SolutionExplorerFileNode.class);
        AbstractTreeNode<?> physicalFile = (AbstractTreeNode<?>) unsafe.allocateInstance(SolutionExplorerFileNode.class);

        RdVcxFilterDescriptor filter = new RdVcxFilterDescriptor(
            "Shaders\\ShaderLab\\UI", "Shaders\\ShaderLab", new UUID(0, 1), false, null, "UI",
            new RdCustomLocation(new RdPath("A:/fixture/l66.vcxproj", "A:/fixture/l66.vcxproj")), false);
        ProjectModelEntity filterEntity = proxy(ProjectModelEntity.class, filter);

        List<AbstractTreeNode<?>> children = new ArrayList<>(List.of(filterNode, physicalDirectory, filterFileNode, physicalFile));
        extension.modifyChildren(filterEntity, settings, children);
        require(children.size() == 2 && children.get(0) == filterNode && children.get(1) == filterFileNode,
                "A virtual filter must lose physical additions while keeping model children and their order.");
        extension.modifyChildren(filterEntity, settings, children);
        require(children.equals(List.of(filterNode, filterFileNode)), "Filtering twice must be idempotent.");

        // Folder-presented VCX filters (RdProjectFolderDescriptor) must keep real Show All Files additions.
        RdProjectFolderDescriptor folderDescriptor = new RdProjectFolderDescriptor(
            false, false, false, false, null, null, "ShaderLab",
            new RdCustomLocation(new RdPath("A:/fixture/Shaders/Private/ShaderLab", "A:/fixture/Shaders/Private/ShaderLab")), false);
        ProjectModelEntity folderEntity = proxy(ProjectModelEntity.class, folderDescriptor);
        List<AbstractTreeNode<?>> folderChildren = new ArrayList<>(List.of(filterNode, physicalDirectory, physicalFile));
        extension.modifyChildren(folderEntity, settings, folderChildren);
        require(folderChildren.equals(List.of(filterNode, physicalDirectory, physicalFile)),
                "A resolved folder descriptor must retain real Show All Files additions.");

        RdProjectModelItemDescriptor projectDescriptor = (RdProjectDescriptor) unsafe.allocateInstance(RdProjectDescriptor.class);
        ProjectModelEntity projectEntity = proxy(ProjectModelEntity.class, projectDescriptor);
        List<AbstractTreeNode<?>> projectChildren = new ArrayList<>(List.of(filterNode, physicalDirectory, physicalFile));
        extension.modifyChildren(projectEntity, settings, projectChildren);
        require(projectChildren.equals(List.of(filterNode, physicalDirectory, physicalFile)),
                "The top-level project must retain Show All Files additions.");

        List<AbstractTreeNode<?>> emptyChildren = new ArrayList<>();
        extension.modifyChildren(filterEntity, settings, emptyChildren);
        require(emptyChildren.isEmpty(), "An empty filter must stay empty.");

        // Verify VcxNewDirectoryAction enablement rules.
        VcxNewDirectoryAction newDirAction = (VcxNewDirectoryAction) unsafe.allocateInstance(VcxNewDirectoryAction.class);
        require("NewDirectory".equals(newDirAction.defaultName()), "defaultName must be NewDirectory.");
        require("".equals(newDirAction.defaultExtension()), "defaultExtension must be empty.");

        DataContext virtualFilterCtx = dataContextWithEntity(filterEntity);
        require(!newDirAction.isEnabled(virtualFilterCtx),
                "New Directory must remain disabled on pure virtual RdVcxFilterDescriptor nodes.");

        RdSolutionFolderDescriptor slnFolderDesc = (RdSolutionFolderDescriptor) unsafe.allocateInstance(RdSolutionFolderDescriptor.class);
        DataContext slnFolderCtx = dataContextWithEntity(proxy(ProjectModelEntity.class, slnFolderDesc));
        require(!newDirAction.isEnabled(slnFolderCtx),
                "New Directory must remain disabled on solution folders.");

        DataContext folderCtx = dataContextWithEntity(folderEntity);
        require(newDirAction.isEnabled(folderCtx),
                "New Directory must be enabled on folder-presented VCX filter nodes.");

        System.out.println("PASS: VcxFilterViewFix + VcxNewDirectoryAction fixture checks all passed.");
        System.out.println("LIMIT: inert actual node instances and interface proxies; no live IDE services, rendering, or UI exercised.");
    }

    @SuppressWarnings("removal")
    private static DataContext dataContextWithEntity(ProjectModelEntity entity) {
        return dataId -> {
            if (ProjectModelDataKeys.INSTANCE.getPROJECT_MODEL_ENTITY_ARRAY().getName().equals(dataId)) {
                return new ProjectModelEntity[] { entity };
            }
            return null;
        };
    }

    private static Unsafe unsafe() throws Exception {
        Field field = Unsafe.class.getDeclaredField("theUnsafe");
        field.setAccessible(true);
        return (Unsafe) field.get(null);
    }

    @SuppressWarnings("unchecked")
    private static <T> T proxy(Class<T> type, RdProjectModelItemDescriptor descriptor) {
        return (T) Proxy.newProxyInstance(type.getClassLoader(), new Class<?>[] {type}, (instance, method, args) -> {
            return switch (method.getName()) {
                case "getDescriptor" -> descriptor;
                case "getName" -> "VCX filter view fixture";
                case "toString" -> "fixture:" + type.getSimpleName();
                case "hashCode" -> System.identityHashCode(instance);
                case "equals" -> instance == args[0];
                default -> defaultValue(method.getReturnType());
            };
        });
    }

    private static Object defaultValue(Class<?> type) {
        if (!type.isPrimitive()) { return List.class.isAssignableFrom(type) ? List.of() : null; }
        if (type == boolean.class) { return false; }
        if (type == char.class) { return '\0'; }
        if (type == long.class) { return 0L; }
        if (type == double.class) { return 0.0; }
        if (type == float.class) { return 0.0f; }
        if (type == byte.class) { return (byte) 0; }
        if (type == short.class) { return (short) 0; }
        return 0;
    }

    private static void require(boolean condition, String message) {
        if (!condition) { throw new AssertionError(message); }
    }
}
