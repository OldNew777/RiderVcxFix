using System;

namespace JetBrains.Application.Parts;

/// <summary>
/// PLEASE, try to use <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> (or <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadUnsafe" />) for new components that are requested by other components,
/// with <see cref="T:JetBrains.Application.Components.IStartupActivity" /> for components that should provide some features themselves (workers) and may be not referenced by others.
/// The legacy default for old components without the specified <see cref="T:JetBrains.Application.Parts.Instantiation" /> is <see cref="F:JetBrains.Application.Parts.Instantiation.ContainerAsyncPrimaryThread" />
/// only because we need to check if they require primary thread or if they can be deferred.
/// These values make sense for async strategies, while the default strategy will create all components at
/// <see cref="M:JetBrains.Application.Components.ComponentContainer.Compose" /> time, but you can still provide correct instantiation value
/// and prepare it to switch to async strategies (e.g. switch from manual container creation to injecting of <see cref="T:JetBrains.Application.Components.NestedContainer.NestedComponentContainer" />).
/// </summary>
[Flags]
public enum Instantiation : short
{
	/// <summary>
	/// Deadlock safe components (<see cref="F:JetBrains.Application.Parts.InstantiationRaw.AnyThreadDependenciesOnly" />) that are created on demand only
	/// and doesn't have any deadlock unsafe dependencies in constructor.
	/// These components can be requested from any thread by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />,
	/// or injected into other <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> components.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	DemandAnyThreadSafe = 0x10,
	/// <summary>
	/// Components that are created on demand only.
	/// This is GOOD OPTION for component-service, but it's always BETTER to use <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> when possible.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	DemandAnyThreadUnsafe = 0,
	/// <summary>
	/// Components that MAY be instantiated asynchronously after <see cref="M:JetBrains.Application.Components.ComponentContainer.Compose" /> call to avoid
	/// long blocking of primary thread, while still MUST be created on primary thread at a specific stage
	/// (for Shell components - before building the SolutionInstance container; for Solution components - prior to transitioning
	/// to SolutionLoadTaskKinds.SolutionContainerComposed and subsequent tasks in ISolutionLoadTasksScheduler).
	/// If such restrictions is not necessary, opt for using <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadUnsafe" /> with <see cref="T:JetBrains.Application.Components.IStartupActivity" /> (preferable)
	/// or <see cref="F:JetBrains.Application.Parts.Instantiation.ContainerAsyncAnyThreadUnsafe" /> to not use the primary thread at all.
	/// </summary>
	/// <remarks>Component WILL be created on primary thread</remarks>
	ContainerAsyncPrimaryThread = 0xA,
	/// <summary>
	/// Components that MAY be instantiated on any thread and asynchronously after <see cref="M:JetBrains.Application.Components.ComponentContainer.Compose" />
	/// call to avoid using of primary thread, while still MUST be created at a specific stage
	/// (for Shell components - before building the SolutionInstance container; for Solution components - prior to transitioning
	/// to SolutionLoadTaskKinds.SolutionContainerComposed and subsequent tasks in ISolutionLoadTasksScheduler).
	/// If such restriction is not necessary, opt for using <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadUnsafe" /> with <see cref="T:JetBrains.Application.Components.IStartupActivity" />.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	ContainerAsyncAnyThreadUnsafe = 2,
	/// <summary>
	/// Deadlock safe components (<see cref="F:JetBrains.Application.Parts.InstantiationRaw.AnyThreadDependenciesOnly" />) that MAY be instantiated on any thread
	/// and asynchronously after <see cref="M:JetBrains.Application.Components.ComponentContainer.Compose" /> call to avoid using of primary thread,
	/// while still MUST be created at a specific stage (for Shell components - before building the SolutionInstance container;
	/// for Solution components - prior to transitioning to SolutionLoadTaskKinds.SolutionContainerComposed and subsequent tasks in ISolutionLoadTasksScheduler).
	/// If such restriction is not necessary, opt for using <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> with <see cref="T:JetBrains.Application.Components.IStartupActivity" />.
	/// These components can't have any deadlock unsafe dependencies in constructor and
	/// can be requested from any thread by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />,
	/// or injected into <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> components.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	ContainerAsyncAnyThreadSafe = DemandAnyThreadSafe | ContainerAsyncAnyThreadUnsafe,
	/// <summary>
	/// Components that are created on demand only.
	/// AsyncOnly components can't be accessed synchronously (ex. by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />), but can be dependent also on other AsyncOnly components.
	/// </summary>
	/// <remarks>Component WILL be created on primary thread</remarks>
	DemandAsyncOnlyPrimaryThread = 0x28,
	/// <summary>
	/// Components that are created on demand only.
	/// AsyncOnly components can't be accessed synchronously (ex. by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />), but can be dependent also on other AsyncOnly components.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	DemandAsyncOnlyAnyThreadUnsafe = 0x20,
	/// <summary>
	/// Deadlock safe components (<see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" />)  with a limited list of allowed dependencies
	/// (<see cref="T:JetBrains.Lifetimes.Lifetime" />, <see cref="T:JetBrains.Util.ILogger" />, <see cref="T:JetBrains.Application.Components.IComponentContainer" />, components wrapped by <see cref="T:JetBrains.Application.Components.ILazy" />),
	/// which itself can be requested from another components only through <see cref="T:JetBrains.Application.Components.ILazy`1" />
	/// This is the BEST OPTION for component-service.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	Service = 0x90,
	[Obsolete("Use DemandAnyThreadUnsafe")]
	DemandAnyThread = 0,
	[Obsolete("Use ContainerAsyncAnyThreadUnsafe")]
	ContainerAsyncAnyThread = ContainerAsyncAnyThreadUnsafe,
	/// <summary>
	/// Components that MUST be created, but MAY be after SolutionLoadTaskKinds.SolutionContainerComposed.
	/// AsyncOnly components can't be accessed synchronously (ex. by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />), but can be dependent also on other AsyncOnly components.
	/// </summary>
	/// <remarks>Component WILL be created on primary thread</remarks>
	[Obsolete("Use DemandAsyncOnlyPrimaryThread with JetBrains.Application.Components.IStartupActivity", true)]
	LaterAsyncOnlyPrimaryThread = 0x2C,
	/// <summary>
	/// Components that MUST be created, but MAY be after SolutionLoadTaskKinds.SolutionContainerComposed.
	/// This is GOOD OPTION for deadlock unsafe component-worker, if it is not needed at the stage of loading the solution,
	/// but it's always BETTER to use <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> with <see cref="T:JetBrains.Application.Components.IStartupActivity" /> when possible.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	[Obsolete("Use DemandAnyThreadUnsafe with JetBrains.Application.Components.IStartupActivity", true)]
	LaterAsyncAnyThreadUnsafe = 4,
	/// <summary>
	/// Deadlock safe components (<see cref="F:JetBrains.Application.Parts.InstantiationRaw.AnyThreadDependenciesOnly" />) that MUST be created,
	/// but MAY be after SolutionLoadTaskKinds.SolutionContainerComposed.
	/// These components can't have any deadlock unsafe dependencies in constructor and
	/// can be requested from any thread by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />,
	/// or injected into <see cref="F:JetBrains.Application.Parts.Instantiation.DemandAnyThreadSafe" /> components.
	/// This is the BEST OPTION for deadlock safe component-worker, if it is not needed at the stage of loading the solution.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	[Obsolete("Use DemandAnyThreadSafe with JetBrains.Application.Components.IStartupActivity", true)]
	LaterAsyncAnyThreadSafe = DemandAnyThreadSafe | LaterAsyncAnyThreadUnsafe,
	/// <summary>
	/// Components that MUST be created, but MAY be after SolutionLoadTaskKinds.SolutionContainerComposed.
	/// AsyncOnly components can't be accessed synchronously (ex. by <see cref="M:JetBrains.Application.Components.ComponentContainerEx.GetComponent``1(JetBrains.Application.Components.IComponentContainer)" />), but can be dependent also on other AsyncOnly components.
	/// </summary>
	/// <remarks>Component WILL be created on any thread</remarks>
	[Obsolete("Use DemandAsyncOnlyAnyThreadUnsafe with JetBrains.Application.Components.IStartupActivity", true)]
	LaterAsyncOnlyAnyThreadUnsafe = 0x24
}
