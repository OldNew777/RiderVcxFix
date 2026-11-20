# Rider VCX Filters Fix 2.0.0 验证记录

验证快照：2026-11-13。目标：**Rider RD-263.7307.258**，方案：`A:\L66\trunk\program\l66\l66.sln`。本报告区分不同二进制、运行阶段及历史比较与当前源顺序验收。

## 当前交付包

当前后端修订为 **final2**。包与 bundle 一致，插件 ID 为 `local.rider.vcx-filter-view-fix`，版本 `2.0.0`，精确 build 范围为 `263.7307.258`，要求完整重启。

| 文件 | SHA256 |
| --- | --- |
| `artifacts/rider-vcx-filters-fix-2.0.0.zip` | `0A761253D547EC032D0E2A974D5FCE39669DF9866CB0DB02E2F83CFC6169498A` |
| `RiderVcxFix.Backend.dll`（final2） | `7E9D53A86C4CA8F0491A65898177687BCB8FE7C060909D9CF70069EE9D4D1A61` |
| `0Harmony.dll` | `F7D167B2500808C6C2AD6E38B6EFA2741A2B809F79B0E92C36E4A7079C38D9BB` |
| `vcx-filter-view-fix.jar` | `81901309CA414F2549C83BE729CDC966A487B719BF3256618A06D19700E6A085` |

[artifacts/package-check.json](artifacts/package-check.json) 确认 ZIP 的三个运行文件均与 bundle 相同；没有 startup hook、host environment provider、Rider API 程序集、fixture 或诊断 runner。JAR 类版本为 Java 21，前端算法保持不变。

## 已确认结果

| 检查 | 二进制/运行 | 结果与证据 |
| --- | --- | --- |
| 包内容、精确 build、版本和哈希 | final2 | 通过；`artifacts/package-check.json`、`frontend/build-manifest.json`、`backend/artifacts/dependencies.json`。 |
| 默认用户配置正常离线安装、普通入口启动 | final2；JVM PID 35104、backend PID 50624 | 通过；`validation/normal-final/normal-result.json` 的 `Passed=true`。2.0.0 installed/enabled/loaded/extensionRegistered=true，三个安装文件与 final2 哈希一致。历史严格比较的 false 原样保留，差异见下文。 |
| 默认配置、外观与启动环境 | 同一正常实例 | 通过；`settings.json` 为 DEFAULT 与原 Roaming/Local 路径，hook/R2R 为 null；`settings-file-comparison.json` 的外观文件 SHA256 前后相同。 |
| 首次独立配置冷启动，默认 CLR 环境 | 早期后端 `3CE1C7C80FEF6D750704148F9C150C606BBB509797143AE2BFA17E41E0E0CDD9`；JVM PID 44668、backend PID 37464 | 真实 filter 树通过；`validation/default-cold/cold-comparison.json` 和 `cold-binary-hashes.json`。此结果只归属该早期 DLL。 |
| 完整重启后真实 filter 树 | final2；JVM PID 47132、backend PID 52500 | 通过；`warm-comparison.json` 的 `passed=true`，`warm-binary-hashes.json` 的部署 DLL 与 final2 哈希一致。 |
| 同 final2 文件再次复用 config/system 启动 | final2；JVM PID 22724、backend PID 57324 | 通过；`cached-comparison.json` 的 `passed=true`。本项记录目录复用重启，不据此宣称部署 catalog/EBS 缓存命中。 |
| 普通密度设置持久化 | 独立配置 | 冷启动阶段由诊断操作保存 DEFAULT；final2 的 `warm-settings.json`、`cached-settings.json` 均为 DEFAULT。 |
| 后端真实调用和生命周期清理 | final2 warm，backend PID 52500 | 组件创建、helper 的真实 getter 调用、detector=true 时 stable=false、退出时 own-prefix-removed 均有日志证据。 |
| 关闭 UseFilters 时遵循用户选择 | final2；JVM PID 33832、backend PID 54600 | 通过；`validation/filters-off-final2/off-verified-result.json` 和 `off-verified-tree.json`：插件 enabled/loaded，实体 145438，全局 filter 数 0，真实模型哨兵存在，连续双快照稳定。 |
| 禁用插件后完整重启，UseFilters=false | final2 保留在 plugins 中但已禁用；backend PID 22800 | 通过；`validation/disabled-final2/disabled-off-verified-result.json`：installed=true、enabled/loaded/extensionRegistered=false；实体 145511、filter 数 0、连续双快照稳定；后端补丁/插件探测日志匹配 0，独立后端 trace 不存在，正常退出。 |
| 从 plugins 目录移出插件后复用配置/缓存启动 | JVM PID 58272、backend PID 42424；UseFilters=false | 通过；`validation/disabled-final2/uninstalled-result.json`：installed/loaded/extensionRegistered=false，实体 145438、filter 数 0、连续双快照稳定；后端补丁/插件探测日志匹配 0，独立 trace 不存在，正常退出。 |
| 禁用插件、UseFilters=true 恢复原始行为 | 独立配置；JVM PID 54960 | UI accessibility 确认 `Restart Needed` pane；`validation/disabled-final2/disabled-original-behavior.json` 记录插件未加载。这是恢复原始 bug 的负向证据。 |

上表中的 `warm-*` 和 `cached-*` 文件均位于 `validation/default-cold/`，使用独立的 config/system/plugins/log 路径。默认用户配置的正常运行另记录于 `validation/normal-final/`。

## 真实 filter 树结果

早期冷启动、final2 warm 和 final2 再次复用启动的比较结果一致。测试开启 **Show All Files** 与 **Show Unreal Project Files**，插件 `2.0.0` 均报告 enabled、loaded、extensionRegistered。

| Filter | 真正模型子节点数 | 补入物理子节点数 |
| --- | ---: | ---: |
| `Shaders` | 2 | 0 |
| `Shaders\ShaderLab` | 8 | 0 |
| `Shaders\ShaderLab\UI` | 1 | 0 |
| `Shaders\ShaderLibrary` | 27 | 0 |

上述三次独立配置运行中，每个 filter 的真正模型子节点身份和顺序与参考相同，重复读取稳定；项目根节点身份和顺序也相同，并保留 **11** 个项目根物理节点。`UIUber` 存在。默认配置的当前源顺序和历史差异单独记录如下。

比较参考为 `E:\Softwares\RiderVcxFix\frontend-plugin\verification\fixed-tree.json`。实际快照为 `cold-tree.json`、`warm-tree.json` 和 `cached-tree.json`。验收直接读取 Rider 中的 solution explorer 模型与定制结果。

## final2 后端运行证据

`validation/default-cold/log/backend.2026-11-13_1257.52500.log` 记录：

- Additional Ad-Hoc Application Package 指向 `validation/default-cold/config/plugins/rider-vcx-filters-fix/dotnet`，包含 `RiderVcxFix.Backend.dll` 和 `0Harmony.dll`；ad-hoc catalog 包含 `RiderVcxFix.Backend, Version=2.0.0.0`。
- 新 backend PID 52500 的 `component-initialized`：`readyToRun=<unset>`、`comPlusReadyToRun=<unset>`、`startupHookConfigured=False`。
- `getter-first-instance` 的 caller chain 包含 `VcxFiltersHelper.AdviseFeatureEnabled` 和 `VcxFiltersHost`。
- 从 `getter-observed call=3` 起，`originalDetectorValue=True`，返回属性仍为 `returnedProperty=11781230`、`stable=true`、`returnedValue=False`、`distinctFromOriginal=True`、`originalDetectorWritten=false`。
- 正常退出的 `shell-terminated` 记录 `own-prefix-removed=true`。

部署文件哈希、新进程 PID、部署 catalog 路径和组件初始化共同将该运行关联到 final2。当前日志的 `target-loaded mvid` 属于 JetBrains 目标程序集，不是插件自身 DLL 的 MVID；日志没有提供插件自身已加载 MVID 的直接读数。

再次复用启动的 backend PID 57324 日志还记录 `Solution 'l66' was loaded from caches`，以及 VCX filter 预读 `2 cache hit(s), 0 file read(s)`。这些证明方案/filter 缓存复用，不等同于部署组件 catalog/EBS 缓存命中。

## 关闭 filters 与禁用验证

`filters-off-final2/off-verified-tree.json` 使用诊断 v7 的 `workspace-sentinel` 判据：在 workspace 中找到唯一的 `l66.uproject` 真正模型文件，并验证它归属正确的 l66 项目，同时确认项目根与连续两次快照稳定。完整 workspace 包含 145438 个实体，全局 `RdVcxFilterDescriptor` 数为 0，插件 2.0.0 已启用、已加载。这确认后端修复仍遵循关闭 UseFilters 的用户选择。

此前 `off-tree.json` 的旧诊断把哨兵限定为项目根直接子项，不适用于关闭 filters 后的真实目录层级；该次诊断没有被当作通过。v7 依据真实 workspace 层级修正了就绪条件。backend PID 54600 的 `off-verified-backend-component.log` 记录正常退出时 `own-prefix-removed=true`。

禁用后保留插件文件并重启，`disabled-off-verified-result.json` 确认前端未加载、extension 未注册，backend PID 22800 没有本插件补丁/探测记录或独立 trace；UseFilters=false 时方案完整加载，连续双快照稳定并正常退出。

随后把插件目录移到同 run 的 `uninstalled-plugin-retained/`，复用原 config/system 启动。`uninstalled-result.json` 确认插件未安装、未加载、extension 未注册；145438 个 workspace 实体、filter 数 0、连续双快照稳定。backend PID 42424 的补丁/插件探测日志匹配为 0，独立 trace 不存在，正常退出。插件文件保留便于恢复。独立配置的全部测试进程均已结束。

禁用且 UseFilters=true 的另一次运行恢复 Rider 原始的 `Restart Needed`。UI accessibility 真实识别到该 pane；它不是 2.0.0 引入的新故障。该次正常退出请求被 modal 阻塞，随后只结束该测试自建的 PID 54960/36800/31312。此运行用于证明原始行为恢复，不作为正常退出验收。

## 默认配置普通启动与历史比较差异

2.0.0 已正常离线部署到 `%APPDATA%\JetBrains\Rider2026.3\plugins\rider-vcx-filter-view-fix`，`installed-files.json` 的三个文件均 `Match=true`，与正式 final2 的完整哈希一致。2026-11-13 14:26，普通 `rider64.exe` 直接打开 l66.sln，JVM PID 35104、backend PID 50624；`launch-recovered.json` 确认没有外部启动器、配置目录覆盖、startup hook 或 R2R 环境覆盖。

`settings.json` 确认 `UIDensity.DEFAULT`，config/plugins 为原 Roaming 路径，system/log 为原 Local 路径。`settings-file-comparison.json` 的外观文件前后 SHA256 均为 `EE97199D14B41ED1CFF52B45FC1104DC528FB0B2FDE3E0893D9DAEA2FB30FC34`，`Unchanged=true`。

`backend-component.log` 记录组件创建和真实 helper 调用；已观察到 `getter-observed call=131072`，`originalDetectorValue=True` 时仍返回同一属性 `45300253`，`stable=true`、`returnedValue=False`、`distinctFromOriginal=True`、`originalDetectorWritten=false`。`frontend-component.log` 有 2.0.0 的真实过滤记录。当前普通 Rider 窗口保留供用户继续使用；正常退出清理已由前述独立实例验证。

普通配置的 v7 快照 `comparison.json` **仍为 `passed=false`**。Shaders、ShaderLab、UI 与历史顺序相同；ShaderLibrary 的 27 个模型成员身份多重集完全相同，历史顺序不同。该旧比较结果没有改写为通过。

v8 的 `live-tree-source-order.json` 直接读取 `entity.childrenEntities`，四个 filter 的 `modelSourceOrderPreserved=true`，模型数量为 2、8、1、27，物理补入数均为 0，`UIUber` 存在、重复读取稳定，Show All Files 和 Show Unreal Project Files 均开启。诊断 attach 工具的 Groovy engine ID 修正为 `com.intellij.groovy.scripting` 后成功采样；这项源顺序诊断只读取模型，不改变生产模型或配置。

最终项目根含 **43** 个真正模型节点，与 **43** 个当前源子项同序一致，另有 **11** 个物理根节点。它比历史参考多 **11** 个真正的 `RdProjectFileDescriptor` 源模型文件，因此最终根节点并非与旧参考完全相同；全部历史根节点仍同序保留，物理根节点身份与顺序完全一致。用户确认验证期间有并行工程更新，本文不把新增节点单独归因于后台展开。

`normal-result.json` 分别记录历史成员、历史顺序、当前源顺序、历史根节点保留与物理根节点序列：`LegacyOrderedComparisonPassed=false`、`ProjectRootChildrenIdentical=false`，当前源模型顺序及历史成员/根节点保留检查通过，最终功能验收 **`Passed=true`**。

## 旧锁记录恢复

本轮普通启动错误已恢复。`normal-final/stale-lock-recovery.json` 记录 2026-11-13 14:26 的恢复操作：Local system 路径为 `C:\Users\chenxin47\AppData\Local\JetBrains\Rider2026.3`，旧记录 PID 为 10888；`.pid` 与 `.port` 保留在 `C:\Users\chenxin47\AppData\Local\JetBrains\Rider2026.3-stale-lock-unified-20261113-142601-110`。计划保留的 31 项缓存/日志与实际移回的 31 项一致，状态为 `Recovered`，`CacheEntriesPreserved=true`、`UserConfigModified=false`。此次恢复操作没有修改 Roaming 配置。

journal 确认文件恢复与保留范围，不构成此前启动错误唯一根因或其与 2.0.0 因果关系的结论。

## 工程文件并行更新记录

14:28 的中途核对中，五份工程文件哈希均与 before 一致。最终 `validation/project-hashes-after.json` 记录 **3/5 未变**：l66.sln、UE5.vcxproj、UE5.vcxproj.filters；l66.vcxproj 与 l66.vcxproj.filters 在 **14:31:40** 同时改写，最终哈希变化。

用户确认“有其他操作在更新工程”，记录于 `normal-final/concurrent-project-update.json`。最终两份文件变化与确认的并行更新吻合；before/after 哈希原样保留，没有把最终五份文件写成全部未改，也没有回退其他操作的工程更新。插件生产代码没有工程生成或保存路径。

## 覆盖边界

final2 的正向验证覆盖完整重启和相同 config/system 再次复用启动；全新 config/system 的正向冷启动未单独重做。早期 3CE DLL 的冷启动结果仅归属其自身。旧版 1.0.0 回退保留备份与操作说明，不列入这次 2.0.0 主验收要求。

本轮 2.0.0 功能验收、正常安装、设置保留、关闭 filters、禁用和卸载均已完成。历史严格比较差异及并行工程更新按实际结果记录。

## 2.0.1 元数据增强重建

- 版本：`2.0.1`；作者/维护者：`ChenXin (L66 Tools)`。
- 加强内容：插件名称、作者、详细描述、支持范围、维护者、插件 ID 和 2.0.1 变更说明。
- 前端 fixture：通过；使用实际 Rider API 类，覆盖过滤、顺序、项目根边界、幂等和空 filter。
- 后端构建：`dotnet build` 0 warning / 0 error；程序集版本 `2.0.1.0`。
- 包内容：ZIP 仅含 `lib/vcx-filter-view-fix.jar`、`dotnet/RiderVcxFix.Backend.dll` 和 `dotnet/0Harmony.dll`；JAR 仅含 class 与 `META-INF/plugin.xml`，class major 65。
- ZIP：`E:\Softwares\RiderVcxFix\unified-plugin\artifacts\rider-vcx-filters-fix-2.0.1.zip`；SHA256 `A16372615AAEAA21F1A3E4A123FBDD067B43AA06CDBCE0B8AEC8444CA4B1FD36`。
- 本轮未安装 2.0.1，也未修改当前 Rider 配置；运行时行为沿用 2.0.0 已验证的同一过滤算法和后端补丁逻辑。

## 2.0.2 开放式兼容范围

- 根据 JetBrains 官方 Build Number Ranges，`263` 对应 IntelliJ Platform/Rider `2026.3` 分支；`RD-263.7307.258` 是该分支内的实际构建。
- 插件声明改为 `<idea-version since-build="263" />`，省略 `until-build`。官方语义为 `263` 及以后版本均可安装，不把上限锁死在 `263.7307.258`。
- 实际验证构建仍为 `RD-263.7307.258`。由于插件使用 Rider 内部前端 API 和 .NET 后端 Harmony 补丁，未来 Rider 大版本仍可能发生内部 API 变化，需要在目标版本重新跑兼容性验证。
- 版本：`2.0.2`；后端程序集：`2.0.2.0`；前端 fixture 通过。
- ZIP：`E:\Softwares\RiderVcxFix\unified-plugin\artifacts\rider-vcx-filters-fix-2.0.2.zip`；SHA256 `221C1829572EA1412C88EE9D7AD7C648BAA88FFD53426A8DC7AD76595211A419`。
