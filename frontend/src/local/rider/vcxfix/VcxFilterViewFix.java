package local.rider.vcxfix;

import com.intellij.ide.util.treeView.AbstractTreeNode;
import com.intellij.openapi.diagnostic.Logger;
import com.intellij.openapi.project.Project;
import com.jetbrains.rider.model.RdVcxFilterDescriptor;
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerCustomization;
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerViewSettings;
import com.jetbrains.rider.projectView.views.solutionExplorer.nodes.SolutionExplorerFileNode;
import com.jetbrains.rider.projectView.workspace.ProjectModelEntity;

import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;

/** Keeps the front-end's Show All Files additions outside virtual VCX filters. */
public final class VcxFilterViewFix extends SolutionExplorerCustomization {
    private static final Logger LOG = Logger.getInstance(VcxFilterViewFix.class);
    private final AtomicInteger removalEvents = new AtomicInteger();
    private final AtomicInteger removedNodes = new AtomicInteger();

    public VcxFilterViewFix(Project project) {
        super(project);
        LOG.info("[VcxFilterViewFix] active plugin=local.rider.vcx-filter-view-fix version=2.1.1");
    }

    @Override
    public void modifyChildren(ProjectModelEntity entity,
                               SolutionExplorerViewSettings settings,
                               List<AbstractTreeNode<?>> children) {
        if (!(entity.getDescriptor() instanceof RdVcxFilterDescriptor filter)) {
            return;
        }

        int before = children.size();
        children.removeIf(child -> child instanceof SolutionExplorerFileNode);
        int removed = before - children.size();
        if (removed == 0) {
            return;
        }

        int total = removedNodes.addAndGet(removed);
        int event = removalEvents.incrementAndGet();
        if (event <= 20 || event % 1000 == 0) {
            LOG.info("[VcxFilterViewFix] removed physical additions filter=" + filter.getFilterPath()
                     + " removed=" + removed + " remaining=" + children.size()
                     + " events=" + event + " totalRemoved=" + total);
        }
    }
}
