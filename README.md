# Rider VCX Filters Fix

**Rider VCX Filters Fix** 是面向 **JetBrains Rider 2026.3 (`branch 263`) 及以后版本** 的离线双端（JVM 前端 + .NET 后端）插件，用于在大型 Unreal Engine C++ `.sln` 工程中启用并修复 VCX Project Filters 视图。

- 插件名称：**Rider VCX Filters Fix**
- 插件 ID：`local.rider.vcx-filter-view-fix`
- 当前版本：**2.1.1**（上一发布基线 tag：`v2.0.2`）
- 维护者：**ChenXin (L66 Tools)**
- 兼容范围：`<idea-version since-build="263" />`（已在 `RD-263.7307.258` / `RD-263.7307.325` / `RD-263.7307.355` 构建核验）

## 解决的问题

### 1. 解除 Unreal 解决方案对 VCX Filters 的强制禁用（2.0.x）
- Rider 2026.3 的 `UnrealEngineVcxFilterOverride.get_IsForceDisabled` 会强制禁用 UE 解决方案的 **Use VCX project filters** 设置，导致跨目录文件（如项目根之外的 `Shaders`、`lua`）在项目根下被拆散平铺。
- .NET 后端组件通过 Harmony 补丁使 `IsForceDisabled` 稳定返回 `false`，保留用户自主开关 `UseFilters` 的能力，且不修改 `ICppUE4SolutionDetector.IsUnrealSolution`。

### 2. 将对应真实磁盘目录的 VCX Filter 还原为普通文件夹视图与交互（2.1.1 新增）
Rider 原生 VCX Filter 模式会把所有目录替换为虚拟节点 `VcxFilterMark`（前端为 `RdVcxFilterDescriptor`），带来以下问题：
- 所有文件夹都显示为带漏斗图标的 filter；
- `VcxFilterMark.GetProjectFolder()` 直接返回 `.vcxproj` 所在目录（`Intermediate\ProjectFiles\`），导致文件树中复制粘贴、新建文件、从资源管理器拖入的文件都掉进 `Intermediate\ProjectFiles\` 并显示为黄色“项目外文件”；
- 前端 `VcxFilterUiDataRule` / `VcxFilterMoveProvider` 禁用选中 filter 节点按 Ctrl+V 粘贴；
- 前端 `RiderNewDirectoryAction` 在启用 VCX Filters 时直接隐藏/禁用了右键 **New Directory**。

**2.1.1 的统一修复（同时覆盖 `UE5` 与游戏工程）**：
- **目录映射解析器（`VcxFilterFolderResolver`）**：基于 `PreloadedFilterData`（直接文件、自底向上子 filter 反推、自顶向下空目录探测），将每个对应真实磁盘目录的 filter 精确映射到物理目录；极少数混杂多目录或无磁盘目录的纯虚拟分组仍保持为 `RdVcxFilterDescriptor`（继续由前端 `VcxFilterViewFix` 过滤 Show All Files 重复项）。
- **普通文件夹呈递（`VcxFilterFolderViewPatch`）**：将解析出真实目录的 `VcxFilterMark` 呈递为带真实绝对路径的 `RdProjectFolderDescriptor`，一次性恢复：
  - 普通文件夹图标（无漏斗符号）；
  - 真实的 Show All Files 目录扫描；
  - 选中文件夹按 Ctrl+V 粘贴；
  - 右键 **New File** 与从资源管理器拖入文件直接落盘到目标目录。
- **后端操作闭环（`VcxFilterFolderViewPatch`）**：
  - 补丁 `VcxFilterMark.GetProjectFolder()` 返回真实 `IProjectFolder`；
  - 补丁 `ProjectModelTaskHandler.CopyHandler`：复制文件/目录直接落盘到目标真实目录，在 `BeginAddFileScope()` 下加入当前 `.vcxproj` 与 `.vcxproj.filters`（不再落到 `Intermediate\ProjectFiles\`，不再显示为黄色项目外文件）；
  - 补丁 `ProjectModelTaskHandler.AddItemsHandler`：新建空目录或拖入文件夹时，自动将对应 `<Filter>` 层级写入 `.vcxproj.filters` 并归属其中的文件；
  - 补丁 `MoveHandler` / `RemoveHandler` / `HandleRenameCommand`：兼容对文件夹节点执行移动、F2 重命名与删除操作。
- **前端恢复 New Directory（`VcxNewDirectoryAction`）**：覆盖 `NewDir` Action，在对应真实物理目录的文件夹节点上恢复右键 **New → Directory**，并由后端同步写入 `.vcxproj.filters`。

## 仓库结构

```text
RiderVcxFix/
├── backend/              # .NET 10 后端插件源码、csproj、build.ps1、固定版本 0Harmony.dll
├── frontend/             # Java 21 JVM 前端插件源码、plugin.xml、fixture 测试、build.ps1
├── scripts/
│   └── install.ps1       # 离线安装脚本（自动备份旧版本、校验 3 个运行文件）
├── docs/                 # 历史验证报告与 Rider 内部 API 调研证据
├── .gitignore
├── LICENSE
└── README.md
```

## 构建与安装

### 构建完整插件包

```powershell
& '.\backend\build.ps1'
& '.\frontend\build.ps1' -RunFixture
```

构建完成后生成：
- 生产目录：`bundle\rider-vcx-filters-fix\`（仅包含 `lib\vcx-filter-view-fix.jar`、`dotnet\RiderVcxFix.Backend.dll`、`dotnet\0Harmony.dll`）
- 压缩包：`artifacts\rider-vcx-filters-fix-2.1.1.zip`

### 安装到 Rider 2026.3

1. **完全退出所有 Rider 窗口**，并确认后台 `Rider.BackendHost.exe` 等进程均已退出。
2. 运行安装脚本（或手动将 `artifacts\rider-vcx-filters-fix-2.1.1.zip` 解压到 `%APPDATA%\JetBrains\Rider2026.3\plugins\`，确保同一插件 ID 只保留一份）：

```powershell
& '.\scripts\install.ps1'
```

3. 正常启动 Rider 打开 `.sln`，开启 **Use VCX project filters** 即可（**Show All Files** 与 **Show Unreal Project Files** 均可保持开启）。

## 诊断日志

- **前端日志**：Rider `idea.log` 中搜索 `[VcxFilterViewFix]`
- **后端日志**：Rider backend 日志中搜索 `[RiderVcxFix.Backend]`，或查看 `%TEMP%\rider-vcxfix-backend-<PID>.log`

## 回退

- 若需回退到纯 2.0.2 版本：在仓库中 `git checkout v2.0.2` 重新构建，或从 `.local-archive\backups\` 恢复旧插件目录后重启 Rider。
- 若需彻底停用：在 Rider Plugins 中禁用或卸载 **Rider VCX Filters Fix** 并完整重启。
