# Rider 2026.3 VCX Filters 本地修复

**Rider VCX Filters Fix 2.0.2** 是由 **ChenXin (L66 Tools)** 维护的普通 Rider 插件包，将 JVM 前端和 .NET 后端修复放在同一个包中。兼容范围声明为 **Rider 2026.3（branch 263）及以后**，实际验证构建为 **RD-263.7307.258**。完整交付、源码和验证记录位于 `E:\Softwares\RiderVcxFix`。

2.0.2 将兼容声明改为 **Rider 2026.3+**：`since-build="263"` 且不设置 `until-build`，避免 2026.3 小版本更新后因精确构建号而直接失效。插件仍以 **RD-263.7307.258** 为实际验证构建；未来 Rider 大版本若改动内部 API，仍可能需要重新适配。2.0.1 的作者、描述和变更说明元数据继续保留。当前正常 Rider 中已安装的 2.0.0 没有被本轮构建自动替换；需要升级时按下方步骤手动部署 2.0.2。

## 使用

安装 2.0.2 后，从普通 Rider 快捷方式或 `.sln` 文件打开 `A:\L66\trunk\program\l66\l66.sln`。开启 **Use VCX project filters**；**Show All Files** 和 **Show Unreal Project Files** 可以继续开启。

重新安装或迁移时：

1. 完全退出所有 Rider 窗口并等待后端退出。
2. 解压构建产物 `artifacts\rider-vcx-filters-fix-2.0.2.zip`，将完整的 `rider-vcx-filters-fix` 文件夹放入 `%APPDATA%\JetBrains\Rider2026.3\plugins\`。`lib/` 和 `dotnet/` 应直接位于插件文件夹内。
3. 已有同 ID 的 1.0.0 前端时，先将旧目录备份到 plugins 目录之外，同一 ID 只保留一份 2.0.2，再正常启动 Rider。2.0.2 可以从已验证的 2.0.0/2.0.1 文件集升级。

插件名称为 **Rider VCX Filters Fix**，作者/维护者为 **ChenXin (L66 Tools)**，ID 为 `local.rider.vcx-filter-view-fix`，版本为 `2.0.2`，沿用旧前端 ID。安装、更新、禁用或卸载后都需要完整重启。2.0.1 无需旧 CMD 启动器，也无需设置 `DOTNET_STARTUP_HOOKS`、`DOTNET_ReadyToRun` 或 `COMPlus_ReadyToRun`。

## 两部分修复

| 部分 | 修复的行为 |
| --- | --- |
| JVM 前端 | 阻止 Show All Files 将同一批物理文件重复补到每个虚拟 VCX filter 下，保留真正的 filter 模型节点和项目根物理文件。 |
| .NET 后端 | 修正 Unreal 的 `IsForceDisabled` override，使 VCX filters 遵循用户 `UseFilters` 设置；用户关闭 filters 时仍然关闭。 |

后端由 Rider 正常发现的组件加载，与前端一起由插件管理。它不修改 Unreal detector 属性或其他 overrides。生产包只包含一个前端 JAR、`RiderVcxFix.Backend.dll` 和 `0Harmony.dll`。

四个目标 filter 的模型子节点数为 2、8、1、27，补入的物理子节点均为 0；项目根保留 11 个物理子节点。关闭 filters 时没有 VCX filter 描述符；禁用或移出插件后双端均未加载。

普通配置的历史严格比较仍为 `passed=false`：ShaderLibrary 的 27 个模型成员一致，历史顺序有差异。直接读取当前源模型后，四个 filter 和项目根均保留当前源顺序；项目根新增 11 个真实源模型文件，全部历史根节点仍同序保留，11 个物理根节点的顺序一致。单独的 `normal-result.json` 据此记录功能验收 `Passed=true`，没有改写旧比较结果。用户确认同时有其他操作更新工程，最终五份工程文件中三份哈希未变、l66.vcxproj 与 filters 两份改变；该并行更新保留。

具体步骤、构建约束和日志说明见 [docs/unified-plugin-2.0.2.md](docs/unified-plugin-2.0.2.md)，二进制哈希、运行证据及覆盖边界见 [docs/validation-report-2.0.2.md](docs/validation-report-2.0.2.md)，Rider 内部 API 调研见 [docs/research/2026-11-13-single-plugin/report.md](docs/research/2026-11-13-single-plugin/report.md)。

## 用户配置与 Compact Mode

用户仍可自行改变 VCX filters、Show All Files、Show Unreal Project Files 和界面密度。2.0.2 的生产逻辑没有修改配置路径或 UI density 的操作。默认配置运行确认 `UIDensity.DEFAULT`、原 Roaming/Local 路径和原外观文件哈希不变；独立配置中两次重启也保留 DEFAULT。

Rider 的普通密度为 `UIDensity.DEFAULT`，Compact Mode 为 `COMPACT`，保存于 `options/ui.lnf.xml` 的 `UI_DENSITY` 项。DEFAULT 是默认值，保存时该 XML 项可以省略。

此前发现本地和设置同步副本保存了 COMPACT，并在同步 Git 历史中看到远端取消后本地再次回写的记录。诊断工具已通过 Rider 的设置 API 保存普通密度；它没有打包进生产插件，后续仍可自由调整外观。详细快照、同步历史和重启证据见 [diagnostics/settings/report.md](diagnostics/settings/report.md)，旧配置备份为 `diagnostics/settings/ui.lnf.before.xml`。

本轮普通启动错误已恢复。恢复期间保留了 Local system 目录中的旧 `.pid`/`.port` 记录。两项保存在 `C:\Users\chenxin47\AppData\Local\JetBrains\Rider2026.3-stale-lock-unified-20261113-142601-110`；31 项缓存与日志均已恢复保留，Roaming 用户配置未修改。操作日志见 [stale-lock-recovery.json](unified-plugin/validation/normal-final/stale-lock-recovery.json)。这项恢复记录不足以确定此前启动问题的唯一根因。

## 日志、源码与撤销

- 前端日志：正常 Rider `idea.log` 中的 `[VcxFilterViewFix]`。
- 后端日志：正常 Rider backend 日志中的 `[RiderVcxFix.Backend]`，以及 `%TEMP%\rider-vcxfix-backend-<PID>.log`。
- 2.0.2 源码和构建说明：`frontend/`、`backend/`、`scripts/install.ps1`。
- 2.0.2 ZIP SHA256 为 `221C1829572EA1412C88EE9D7AD7C648BAA88FFD53426A8DC7AD76595211A419`。

撤销 2.0.2：在 Plugins 中禁用或卸载 **Rider VCX Filters Fix**，完全退出并正常重启。普通快捷方式本身不会撤销仍然启用的 2.0.2。升级 Rider 后，需要重新核对内部 API 与后端目标方法。

## 旧版回退资料

旧 `Start-Rider-VcxFix.cmd`、`source/`、`current/` 和 `frontend-plugin/` 保留。旧方案由 1.0.0 前端、.NET startup hook 和 CMD 启动器组成，启动器在后端启动前配置 hook 并关闭 ReadyToRun。

不要将旧启动器与 2.0.2 同时使用。需要回退时，先完全退出 Rider，移除或禁用 2.0.2，恢复备份的 1.0.0 前端并确保同一 ID 只有一份，再运行 `Start-Rider-VcxFix.cmd`。旧前端包为 `frontend-plugin\rider-vcx-filter-view-fix-1.0.0.zip`；旧 hook 日志为 `%TEMP%\rider-vcxfix.log`。回退文件与说明保留供需要时使用。
