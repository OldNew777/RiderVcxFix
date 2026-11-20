# Rider VCX Filters 合并插件可行性调研

调研日期：2026-11-13。目标版本：Rider RD-263.7307.258。

本报告区分现有已验证修复、对本机安装产物的静态核实和尚未实施的候选方案。本轮没有重写、安装插件，也没有重启或修改正在运行的 Rider。现有工具仍位于 `E:\Softwares\RiderVcxFix`，现有启动方式见主目录 README。

## 结论与推荐顺序

可以把前后端修复交付为一个 Rider 插件包，取消外部 CMD 启动器。Rider 官方支持同一 ZIP 内同时包含 JVM JAR 和 .NET DLL，两端仍分别在自己的运行时执行。

优先验证正式双端插件：前端保留现有视图扩展，后端用 ShellComponent 安装更窄的 getter 补丁。先在默认运行环境验证；若能证实失败来自 ReadyToRun，再由同包前端扩展仅设置 `DOTNET_ReadyToRun=0`。这两种设计都不需要 startup hook，修复代码全部由普通插件承载。

如果正式后端插件不能满足真实运行中的加载时序或补丁行为，再考虑同包携带现有 startup hook，并通过后端环境扩展设置其启动变量。它仍可实现一次安装和普通快捷方式启动，但需要处理卸载后 Early Backend Starter（EBS）的旧 DLL 路径缓存。

纯 .NET hook 目前没有找到与前端修复等价、同时保持项目树语义的修复点，不推荐作为首选 fallback。以上候选方案均未实现或进行隔离安装验证。

## 两项修复为什么分属不同运行时

后端的 `VcxFiltersHelper.IsFeatureEnabled` 使用 `useFilters && !overrides.Any(o => o.IsForceDisabled.Value)`。`UnrealEngineVcxFilterOverride.IsForceDisabled` 直接返回 Unreal detector 的属性。检测到 Unreal 方案后会否决用户的 Use VCX project filters 设置。现有 hook 将 helper 结果改成传入的 `useFilters`，解除禁用及反复 Restart Needed；现实现实测依赖进程启动前的 `DOTNET_ReadyToRun=0`。

前端的 `ProjectModelEntityExtensionsKt.getVirtualFileAsContentRoot` 把 `RdVcxFilterDescriptor` 明确映射到所属项目的内容根。`SolutionExplorerModelNode.calculateChildren` 在 Show All Files 开启时自行扫描目录，补入 `SolutionExplorerFileNode`。这些补项不在后端 RD 树里，后端事后删除 RD 节点无法阻止它们生成。

现有 JVM 插件通过 `solutionExplorerCustomization.modifyChildren`，仅移除虚拟 filter 下的物理补项，保留真实模型节点及项目根的物理文件。这部分已在真实 Rider 中验证，应继续保留。

CMD 只负责在启动前设置 .NET 环境，并不实现另一项修复。它是现实现的引导入口，不是 Rider 架构强制要求的永久组成部分。

## 候选 A：正式 JVM＋.NET 插件

后端使用 `[ShellComponent(Instantiation.ContainerAsyncPrimaryThread)]`。本机 SDK 的 Instantiation 文档明确说明该类 Shell component 必须在构建 SolutionInstance 容器前创建。目标 Unreal override 是 DemandAnyThreadSafe 的 SolutionComponent，因此这为先安装补丁、后创建并订阅 VCX 状态提供了具体顺序依据。

补丁目标改为 `UnrealEngineVcxFilterOverride.get_IsForceDisabled`，返回一个在 Shell 生命周期内存活、值稳定为 false 的 `IProperty<bool>`。首次订阅 Change 和后续读取必须使用同一实例，不能每次 getter 调用创建新属性。只撤销 Unreal 的这一项否决，可以保留用户开关、其他 override 的否决及正常 Unreal 检测；不能直接改 detector 的原属性。

新增一个返回 false 的 override 无法抵消已有 true，因为接口是任一 true 即禁用的规则。本轮未找到已确认可用的组件替换或排除 API；这不等于证明此类 API 完全不存在。

getter 的本机静态核实结果：12 字节 CIL，读取 detector 并调用 IsUnrealSolution getter；sealed 类型，public final virtual newslot specialname，impl_flags=0，没有 NoInlining。判定和订阅循环通过接口 callvirt 访问它。相关 RiderPlugin DLL 没有自身 R2R/native header。

这些事实不能保证 Harmony 一定成功。小 getter 可能被内联或去虚拟化；预编译调用方和普通插件的实际加载顺序仍须实测。旧 helper hook 的成功不能替代新 getter 补丁的验证。

若默认环境失败且能证实与 R2R 有关，可尝试由同包前端 `RiderBackendEnvProvider` 仅设置 `DOTNET_ReadyToRun=0`，继续通过普通 .NET 插件执行补丁。该设计没有 startup hook DLL 路径引用，避开这一具体卸载风险；但 R2R=0 不保证不存在内联问题，并会影响整个后端进程的预编译代码使用。冷启动、CPU 和内存代价尚未测量。

## 候选 B：插件内部携带 startup hook

本机及官方扩展点列表都确认以下接口存在：

- EP：`com.intellij.rider.protocol.hostEnvProvider`
- 接口：`com.jetbrains.rider.protocol.RiderBackendEnvProvider`
- `getEnvironment()`：返回 `Iterable<kotlin.Pair<String, String>>`
- `isDynamic()`：默认 false；本场景需要非 dynamic provider，让变量在 CLR 创建前进入进程环境。
- 使用 `defaultExtensionNs="com.intellij"` 时可注册 `rider.protocol.hostEnvProvider`。

同一插件包保留前端修复并携带 hook/Harmony。provider 从实际插件安装目录解析 DLL 绝对路径，提供 `DOTNET_STARTUP_HOOKS` 和 `DOTNET_ReadyToRun=0`。用户只安装插件，随后正常从快捷方式或 .sln 启动。实施时需合并已有其他 startup hooks、去除本工具重复路径，并核查其他启动环境来源的优先级。

本机 EBS 字节码已确认：普通应用启动前，它会从 system/caches 的 rider_ebs_plugins.txt、rider_ebs_flags.txt、rider_ebs_vars.txt 读取缓存，并使用缓存环境启动 EarlyReSharperProcess。它不会在这一阶段直接调用当前 provider。

正常应用启动后，ReSharperProcess.Companion.getAdditionalInfo() 计算当前非 dynamic provider 环境。ProtocolManager 比较当前环境 Map 和 early 缓存 Map 的内容；环境、插件集合、flags 或 locale 不一致时，会终止旧 early lifetime 并创建使用当前环境的新 ReSharperProcess。正常退出时 RiderEBSPluginCacheHandler.appWillBeClosed 保存环境供下次 EBS 使用。但非 dynamic 环境在前端生命周期内是懒加载后缓存的，禁用或卸载时退出缓存仍可能留下旧 hook 变量。这为正常安装后一次完整重启生效提供了依据，不能保证卸载时旧缓存已经移除 hook；两者仍需安装实测。

卸载边界不能由上述 mismatch 逻辑单独保证安全：EBS 创建 future 的异常或 null 会通过 getOrLogException 回退到 normal backend，但如果旧缓存指向已删除的 startup hook DLL，CLR 可在进程已经 spawn 后失败。这不一定是 future 创建异常。

限定核查已确认 EarlyBackendStarter 创建独立 LifetimeDefinition，normal backend 使用嵌套 lifetime；EBS 被接受后才挂接 normal lifetime 的退出事件。EarlyReSharperProcess 传入 isLightweight=false，Base.processTerminated 没有 early 专用豁免。若进程未主动 shutdown 且父 lifetime 仍存活，可进入 CriticalErrorApplicationTerminator；其排队回调也没有 later lifetime/requestShutdown 重查。因此不能静态保证缺失 DLL 的 stale EBS 不会弹 critical dialog。实施 B 前必须运行时验证或设计缓存与 DLL 生命周期处理，未解决前不能声称具备可靠卸载行为。

## 纯 hook fallback 的限制

修改 filter.Location 无法解决当前前端补项，因为前端对 VCX filter 明确使用所属项目根。已检查的 descriptor 字段包括 FilterPath、ParentFilterPath、Guid、IsEmpty、Extensions、Name、Location、LoadChildrenLazily，未发现已确认可用于禁止此补项的协议标记。

修改项目根、伪造 descriptor 或调整树父级，会影响项目根 Show All Files 及文件新增、移动、导航，不能视为现有视图修复的等价替代。若让 .NET hook 跨运行时改 JVM，则还需要 Java agent、JVMTI、Attach 或部署前端代码，实际仍是两运行时组件，复杂度高于标准双端插件。

这是对当前已检查路径的结论，不是证明纯 hook 在任何设计下都不可能。

## 实施前必须通过的隔离验证

1. 使用匹配 263.7307.258 的 SDK，验证插件真实注册、程序集加载及补丁完成顺序。默认环境冷启动、无方案启动后打开方案，都须早于 VcxFiltersHost 初始化。
2. 验证 getter 的接口调用和订阅使用同一个 false 属性，filters 能加载；UseFilters 关闭仍禁用，其他 override 保留否决权，Unreal 功能正常。
3. 保留 Show All Files、Show Unreal Project Files；虚拟 filters 没有重复物理补项，项目根物理项、真实模型项及正常导航仍正常。
4. 后端复用、关闭重开方案及 EBS 重启没有补丁叠加或失效。每次环境测试必须确认启动了新后端进程。
5. 安装、禁用、卸载、更新后的完整重启都没有启动故障；符号不匹配时记录诊断并保留原行为。B 额外覆盖旧 env 指向已删除 hook DLL 的情况。
6. 如需 R2R=0，测量相对于默认环境的冷启动及资源代价。普通入口使用默认配置路径，插件不强制外观或 compact mode，并确认设置在重启后保留。

## 公开来源与证据边界

- [Rider SDK](https://www.jetbrains.com/help/resharper/sdk/Rider.html)：官方双端 ZIP 的 lib/JAR 与 dotnet/DLL 结构。文档更新时间较早，仅用于证明打包能力。
- [Rider 扩展点列表](https://plugins.jetbrains.com/docs/intellij/rider-extension-point-list.html)：列出 hostEnvProvider 与 RiderBackendEnvProvider。
- [官方模板 build.gradle.kts](https://raw.githubusercontent.com/JetBrains/resharper-rider-plugin/master/content/build.gradle.kts)：prepareSandbox 将 .NET DLL 复制到插件 dotnet 目录。查询时模板对应 2025.3，不能直接当作本机 263 的 SDK。
- [官方模板 Rider.csproj](https://raw.githubusercontent.com/JetBrains/resharper-rider-plugin/master/content/src/dotnet/ReSharperPlugin.SamplePlugin/ReSharperPlugin.SamplePlugin.Rider.csproj)：引用 JetBrains.Rider.SDK；插件库目标框架不能用来推断当前后端运行时。
- [Component Model](https://www.jetbrains.com/help/resharper/sdk/Platform_ComponentModel.html)：Shell 与 Solution 生命周期概念；263 的具体顺序以本机文档、元数据与代码为依据。

DeepSeek Search 本轮返回 HTTP401 后转用 ai-search。SearXNG 的搜索与 egress 已验证可用；公开搜索未找到可用的 provider 实现示例，关键签名与调用顺序来自本机安装产物。未以公开搜索的负结果证明某 API 不存在。

本轮小型证据将与本报告一同保存在 research 子目录，按函数和类型名引用，不依赖不稳定的代码行号。证据不包含安装 JAR、用户配置、缓存或完整 JVM 参数。

## 证据索引

| 结论 | 证据 | 状态与仍待确认的部分 |
| --- | --- | --- |
| 双端插件打包可行 | 上述官方 SDK 与模板 | 官方支持；需匹配本机 SDK。 |
| ShellComponent 可在 SolutionInstance 前创建 | [Instantiation.cs](evidence/backend-component/Instantiation.cs) | 本机文档静态核实；实际插件注册和补丁完成时机仍须测。 |
| 新增 false override 不能抵消 Unreal 禁用 | [VcxFiltersHelper.cs](evidence/backend-component/VcxFiltersHelper.cs)、[UnrealEngineVcxFilterOverride.cs](evidence/backend-component/UnrealEngineVcxFilterOverride.cs) | 本机实现静态核实。 |
| 前端物理补项位于 JVM，需要保留视图修复 | [修复点字节码](evidence/frontend-view/repair-point-bytecode.txt) | 静态路径已核实；现有修复另有前轮真实运行验证。 |
| provider 环境与 EBS 缓存的调用顺序 | [接口与生命周期核查](evidence/frontend-environment/frontend-env-api-notes.md)、[critical 回调摘录](evidence/frontend-environment/critical-callback.javap-excerpts.txt) | 本机字节码静态核实；候选插件安装、卸载尚未实测。 |
| 无 startup hook 的 getter 补丁能否在默认环境可靠生效 | 前述类型、元数据与生命周期依据 | 尚未实施；内联、R2R 调用方及运行时结果待验证。 |

文件迁移与复制时逐一核对 SHA256，清单见 [evidence-manifest.json](evidence-manifest.json)。仅本轮临时证据目录和报告被迁移，前轮研究摘录保留原件。

