using JetBrains.Application.Catalogs;
using JetBrains.Application.Catalogs.Filtering;
using JetBrains.Application.Components.NestedContainer;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;

namespace JetBrains.Application.Components;

public class NestedComponentContainerLeafsAndHidesByPartAttribute<TPartAttribute, TPartCatalogSet, TPreCreatedComponentDescriptorFilter> : NestedComponentContainer<NestedComponentContainerLeafsAndHidesByPartAttribute<TPartAttribute, TPartCatalogSet, TPreCreatedComponentDescriptorFilter>.NestedComponentContainerRegisterLeafsAndHidesByPartAttributeStrategy> where TPartAttribute : PartAttribute where TPartCatalogSet : IPartCatalogSet where TPreCreatedComponentDescriptorFilter : INestedComponentContainerComponentDescriptorFilter, new()
{
	public class NestedComponentContainerRegisterLeafsAndHidesByPartAttributeStrategy : NestedComponentContainerRegisterByPartAttributeStrategy<TPartAttribute, TPartCatalogSet, TPreCreatedComponentDescriptorFilter>
	{
		public NestedComponentContainerRegisterLeafsAndHidesByPartAttributeStrategy()
			: base(JetBrains.Application.Catalogs.Filtering.PartSelector.LeafsAndHides)
		{
		}
	}

	internal NestedComponentContainerLeafsAndHidesByPartAttribute(Lifetime lifetime, string id, IInitializationStrategy strategy, IContainerThreading threading)
		: base(lifetime, id, strategy, threading)
	{
	}
}
