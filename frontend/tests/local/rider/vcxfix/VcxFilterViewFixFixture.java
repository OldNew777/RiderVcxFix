package local.rider.vcxfix;

import com.intellij.ide.util.treeView.AbstractTreeNode;
import com.intellij.openapi.project.Project;
import com.jetbrains.rd.ide.model.RdPath;
import com.jetbrains.rider.model.RdCustomLocation;
import com.jetbrains.rider.model.RdProjectDescriptor;
import com.jetbrains.rider.model.RdProjectModelItemDescriptor;
import com.jetbrains.rider.model.RdVcxFilterDescriptor;
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

/** Tests the actual extension method with actual Rider descriptor and node classes, without an IDE. */
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

        RdProjectModelItemDescriptor projectDescriptor = (RdProjectDescriptor) unsafe.allocateInstance(RdProjectDescriptor.class);
        ProjectModelEntity projectEntity = proxy(ProjectModelEntity.class, projectDescriptor);
        List<AbstractTreeNode<?>> projectChildren = new ArrayList<>(List.of(filterNode, physicalDirectory, physicalFile));
        extension.modifyChildren(projectEntity, settings, projectChildren);
        require(projectChildren.equals(List.of(filterNode, physicalDirectory, physicalFile)),
                "The top-level project must retain Show All Files additions.");

        List<AbstractTreeNode<?>> emptyChildren = new ArrayList<>();
        extension.modifyChildren(filterEntity, settings, emptyChildren);
        require(emptyChildren.isEmpty(), "An empty filter must stay empty.");
        System.out.println("PASS: real Rider API filtering, preserved model children/order, project-root boundary, idempotence, empty filter.");
        System.out.println("LIMIT: inert actual node instances and interface proxies; no live IDE services, rendering, or UI exercised.");
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
