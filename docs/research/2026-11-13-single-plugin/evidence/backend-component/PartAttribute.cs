using System;
using JetBrains.Annotations;

namespace JetBrains.Application.Parts;

[Serializable]
[AttributeUsage(AttributeTargets.All, Inherited = true, AllowMultiple = false)]
[MeansImplicitUse]
public abstract class PartAttribute : Attribute
{
}
