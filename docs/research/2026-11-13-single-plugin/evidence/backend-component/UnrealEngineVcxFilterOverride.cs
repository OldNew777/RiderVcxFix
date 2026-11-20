using JetBrains.Application.Parts;
using JetBrains.DataFlow;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.ProjectsHost.MsBuild.Extensions.VcxFilter;
using JetBrains.ReSharper.Psi.Cpp.UE4;

namespace JetBrains.ReSharperCpp.RiderPlugin.Unreal;

/// <summary>
///   Force-disables the VCX project filters feature for Unreal Engine solutions.
///   UE projects manage their own module layout through UBT/Build.cs and do not benefit
///   from .vcxproj.filters organization; enabling filters there has historically caused
///   duplicate-folder artefacts and inconsistent trees.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class UnrealEngineVcxFilterOverride : IVcxFilterFeatureOverride
{
	private readonly ICppUE4SolutionDetector myDetector;

	public IProperty<bool> IsForceDisabled => myDetector.IsUnrealSolution;

	public UnrealEngineVcxFilterOverride(ICppUE4SolutionDetector detector)
	{
		myDetector = detector;
	}
}
