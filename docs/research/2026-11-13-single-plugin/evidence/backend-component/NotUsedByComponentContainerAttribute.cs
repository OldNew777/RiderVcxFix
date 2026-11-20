using System;

namespace JetBrains.Application.Parts;

/// <summary>
/// Indicates that the marked constructor is not used by the component container to create the component.
/// Parameters of these constructors are not analyzed by safety checks.
/// </summary>
[AttributeUsage(AttributeTargets.Constructor)]
public class NotUsedByComponentContainerAttribute : PartAttribute
{
}
