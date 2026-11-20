# Rider VCX Filters Fix 2.0.2

由 **ChenXin (L66 Tools)** 维护的普通 Rider 插件包，同时包含 JVM 前端和 .NET 后端修复。适用版本精确限定为 **Rider RD-263.7307.258**。升级 Rider 后需要重新核对内部 API 和补丁目标。兼容范围声明为 **Rider 2026.3（branch 263）及以后**，实际验证构建为 **RD-263.7307.258**。2.0.2 仅调整兼容声明和构建元数据，过滤算法与后端补丁逻辑不变。

安装包：`E:\Softwares\RiderVcxFix\unified-plugin\artifacts\rider-vcx-filters-fix-2.0.2.zip`，后端程序集版本 2.0.2.0，ZIP SHA256 `221C1829572EA1412C88EE9D7AD7C648BAA88FFD53426A8DC7AD76595211A419`。完整哈希、作者、描述和变更说明见 [artifacts/package-check.json](artifacts/package-check.json)。

## 安装与使用

1. 完全退出 Rider，等待对应后端进程退出。
2. 解压上述 ZIP，将完整的 `rider-vcx-filters-fix` 文件夹离线部署到 `%APPDATA%\JetBrains\Rider2026.3\plugins\`，使 `lib/` 和 `dotnet/` 直接位于插件文件夹中。已有同 ID 的旧前端时，先备份到 plugins 目录之外，再保留这一份 2.0.2。
3. 从普通 Rider 快捷方式或 `.sln` 文件启动，开启 **Use VCX project filters**。**Show All Files** 和 **Show Unreal Project Files** 可以继续开启。

插件名称为 **Rider VCX Filters Fix**，ID 为 `local.rider.vcx-filter-view-fix`，版本为 `2.0.2`，作者/维护者为 `ChenXin (L66 Tools)`。它沿用旧前端的 ID；同一配置中应只保留一份该插件。插件声明 `require-restart="true"`，安装、更新、禁用或卸载均需要完整重启。

2.0.2 从普通 Rider 入口启动，生产包无需 `DOTNET_STARTUP_HOOKS`、`DOTNET_ReadyToRun`、`COMPlus_ReadyToRun` 或旧 CMD 启动器。不要把旧启动器与 2.0.2 同时使用。

本轮只构建并打包 2.0.2，没有自动安装或替换当前 Rider 中的 2.0.0。2.0.0 的默认 Roaming/Local 配置 filter 树、`rider64.exe` + l66.sln 后端验收，以及独立配置中的启用、关闭 filters、禁用和卸载记录仍保留；2.0.2 复用同一过滤算法和后端补丁，并已重新运行前端 fixture、检查 JAR 元数据及包内容。

默认配置的旧严格比较 `comparison.json` 保持 `passed=false`：ShaderLibrary 历史顺序有差异，但 27 个模型成员完全一致。当前源模型采样确认四个 filter 与项目根均保留源顺序；新增的 11 个真实源模型根节点全部保留，历史根节点仍同序保留、11 个物理根节点同序一致。`normal-result.json` 单独记录功能验收 `Passed=true`。用户确认验证期间有并行工程更新；五份工程文件最终三份未变、l66.vcxproj 和 filters 两份改变，具体记录见 [validation-report.md](validation-report.md)。

## 修复行为

| 部分 | 行为 |
| --- | --- |
| JVM 前端 | 在虚拟 VCX filter 下移除 Show All Files 补入的 `SolutionExplorerFileNode`，保留真正的模型节点、顺序和项目根物理文件。 |
| .NET 后端 | 通过普通 Rider 后端组件修正 `UnrealEngineVcxFilterOverride.get_IsForceDisabled`，使 Unreal 项目的 VCX filters 能遵循用户设置。 |

后端由 `ShellComponent(ContainerAsyncPrimaryThread)` 创建，一个 Shell 生命周期内共用一个值为 false 的 `IProperty<bool>`。补丁只替换 Unreal override 的返回属性，不写入 Unreal detector 属性，也不修改用户的 `UseFilters` 或其他 override。Shell 结束时撤销本插件的 Harmony prefix。

前端沿用已验证的 1.0.0 过滤算法。两部分都从同一 ZIP 安装，目录布局为相邻的 `lib/` 和 `dotnet/`：

```text
rider-vcx-filters-fix/
  lib/vcx-filter-view-fix.jar
  dotnet/RiderVcxFix.Backend.dll
  dotnet/0Harmony.dll
```

生产包检查确认只包含上述运行文件；构建记录、测试工具和 Rider API 依赖保存在源码目录。

## 用户设置

用户仍可自行调整 VCX filters、Show All Files、Show Unreal Project Files 和界面密度。插件没有修改配置路径或 UI density 的逻辑。默认配置运行确认 DEFAULT、原 Roaming/Local 路径和外观文件哈希不变；2.0.0 final2 在独立配置中的两次重启也保留 DEFAULT；2.0.1 未修改这部分逻辑。

旧版 Compact Mode 的诊断记录位于 `E:\Softwares\RiderVcxFix\diagnostics\settings\report.md`。该次设置诊断属于独立工具操作，生产插件没有包含诊断工具。

本轮普通启动错误已恢复；恢复期间保留了 Local system 目录中的旧 `.pid`/`.port` 记录，31 项缓存与日志均恢复保留，Roaming 用户配置未修改。旧两项锁记录保存在 `C:\Users\chenxin47\AppData\Local\JetBrains\Rider2026.3-stale-lock-unified-20261113-142601-110`。实际操作见 [validation/normal-final/stale-lock-recovery.json](validation/normal-final/stale-lock-recovery.json)；这不构成启动问题唯一根因的结论。

## 日志与故障判断

- 前端：Rider 的 `idea.log`，前缀 `[VcxFilterViewFix]`。
- 后端：Rider 的 backend 日志，前缀 `[RiderVcxFix.Backend]`；同时写入 `%TEMP%\rider-vcxfix-backend-<PID>.log`。

`component-initialized` 表示 Rider 已创建组件；`target-loaded` 和 `patch-installed` 表示找到目标并登记 prefix。实际生效需要 `getter-first-instance` 和 `getter-observed`：真实 helper 调用了 getter，Unreal detector 为 true 时仍返回稳定的 false 属性。仅出现 `patch-installed` 不足以证明修复生效。

正常退出后应有 `shell-terminated ... own-prefix-removed=true`。`patch-failed` 表示本次补丁未启用；应结合精确 Rider build 和日志分析。

## 构建与源码

在 PowerShell 中依次运行：

```powershell
& 'E:\Softwares\RiderVcxFix\unified-plugin\backend\build.ps1'
& 'E:\Softwares\RiderVcxFix\unified-plugin\frontend\build.ps1'
```

后端使用目标安装中的 .NET 10 API DLL，Harmony 2.4.3 按 SHA256 固定。前端使用目标安装中的 Rider API JAR，以 Java 21 构建。脚本完成编译和打包后，运行验证仍需在 Rider 中执行。

- 后端实现和构建约束：[backend/README.md](backend/README.md)、[backend/VcxFilterOverridePatch.cs](backend/VcxFilterOverridePatch.cs)、[backend/ZoneMarker.cs](backend/ZoneMarker.cs)。
- 前端实现和打包约束：[frontend/README.md](frontend/README.md)。
- 实际包内容与哈希：[artifacts/package-check.json](artifacts/package-check.json)。
- 当前运行证据与覆盖边界：[validation-report.md](validation-report.md)。

## 撤销与旧版回退

撤销 2.0.2：在 Plugins 中禁用或卸载 **Rider VCX Filters Fix**，完整退出并正常重启。普通快捷方式本身不会撤销已启用的 2.0.2 后端修复。

旧 `Start-Rider-VcxFix.cmd`、`source/` 和 `current/` 保留作回退资料。回到旧方案时，先完全退出 Rider，移除或禁用 2.0.2，恢复备份的 1.0.0 前端，确保同一 ID 只有一份，再使用旧启动器。备份和旧版操作说明保留用于需要时回退。
