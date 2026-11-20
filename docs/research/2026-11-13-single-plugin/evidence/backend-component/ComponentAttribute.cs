using System;
using JetBrains.Annotations;

namespace JetBrains.Application.Parts;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
[MeansImplicitUse(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
[ComponentInstantiation((Instantiation)(-32758))]
public abstract class ComponentAttribute : PartAttribute
{
	protected const Instantiation ComponentDefaultUnspecifiedInstantiation = (Instantiation)(-32758);

	public readonly ComponentProperties Properties;

	public bool Pluggable;

	protected ComponentAttribute()
	{
		Properties = new ComponentProperties((Instantiation)(-32758));
	}

	/// <inheritdoc cref="T:JetBrains.Application.Parts.Instantiation" />
	protected ComponentAttribute(Instantiation instantiation)
	{
		Properties = new ComponentProperties(instantiation);
	}
}
