package local.rider.vcxfix;

import com.intellij.openapi.actionSystem.DataContext;
import com.intellij.openapi.vfs.VirtualFile;
import com.jetbrains.rider.actions.RiderActionsBundle;
import com.jetbrains.rider.model.RdVcxFilterDescriptor;
import com.jetbrains.rider.projectView.actions.newFile.RiderNewActionBase;
import com.jetbrains.rider.projectView.workspace.ProjectModelEntity;
import com.jetbrains.rider.projectView.workspace.ProjectModelEntityExtensionsKt;
import com.jetbrains.rider.usages.RiderProjectViewBundle;
import icons.ReSharperIcons;

import java.io.IOException;
import java.io.UncheckedIOException;

/**
 * Restores the standard New Directory action on VCX projects whose filter nodes have been
 * resolved to real physical directories, while keeping it disabled on solution folders and
 * unresolved pure-virtual VCX filters.
 */
public final class VcxNewDirectoryAction extends RiderNewActionBase {
    public VcxNewDirectoryAction() {
        super(ReSharperIcons.ProjectModel.Folder);
    }

    @Override
    protected String defaultName() {
        return "NewDirectory";
    }

    @Override
    protected String defaultExtension() {
        return "";
    }

    @Override
    protected String actionTitle() {
        return RiderActionsBundle.INSTANCE.message("action.NewDir.text");
    }

    @Override
    protected String getCommandName(String name) {
        return RiderProjectViewBundle.INSTANCE.message("label.create.new.directory", name);
    }

    @Override
    protected boolean isEnabled(DataContext dataContext) {
        ProjectModelEntity entity = ProjectModelEntityExtensionsKt.getProjectModelEntity(dataContext, false);
        if (entity != null) {
            if (ProjectModelEntityExtensionsKt.isSolutionFolder(entity)) {
                return false;
            }
            if (entity.getDescriptor() instanceof RdVcxFilterDescriptor) {
                return false;
            }
        }
        return super.isEnabled(dataContext);
    }

    @Override
    protected VirtualFile createLastComponent(VirtualFile parent, String name) {
        try {
            return parent.createChildDirectory(this, name);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }
}
