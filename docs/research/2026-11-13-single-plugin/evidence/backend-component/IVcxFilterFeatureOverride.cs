using JetBrains.DataFlow;

namespace JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter;

/// <summary>
///   Solution-scoped override for the VCX project filters feature. When the returned
///   <see cref="P:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.IVcxFilterFeatureOverride.IsForceDisabled" /> property holds <c>true</c>, the feature is force-disabled
///   regardless of the user's <see cref="P:JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter.VcxFilterSettings.UseFilters" /> setting.
///   The property is <b>reactive</b>: its value may flip <i>after</i> the solution has loaded
///   (e.g. once Unreal-solution detection completes), so consumers must advise on
///   <see cref="P:JetBrains.DataFlow.IProperty`1.Change" /> rather than sampling it once — otherwise the
///   pushed feature state goes permanently stale (RIDER-40004).
///   The property is observe-only for consumers: only the implementing component may set its value
///   (<see cref="T:JetBrains.DataFlow.IProperty`1" /> is used because DataFlow has no read-only property interface
///   exposing both <c>Value</c> and <c>Change</c>).
///   Discovered via <c>solution.GetComponents&lt;IVcxFilterFeatureOverride&gt;()</c>.
/// </summary>
public interface IVcxFilterFeatureOverride
{
	IProperty<bool> IsForceDisabled { get; }
}
