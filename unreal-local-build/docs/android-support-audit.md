# Android 打包支持代码审计清单

本文档用于审计 [unreal-local-build](/D:/UnrealGit/unreal-build/unreal-local-build) 中“Android 平台打包支持”相关实现，按功能点列出对应代码位置、当前行为和验证状态，便于后续验收、回归和代码评审。

## 1. 审计结论

- Android 平台已经被提升为系统的一等字段，覆盖手动构建、构建记录、定时任务、下载命名和前端 UI。
- Android 第一版范围已经按设计收口为：
  - `ASTC`
  - `Game only`
  - 测试包，不做正式签名发布
- 旧项目配置 JSON 导入兼容已补齐：
  - 缺失 `androidEnabled` 时默认 `true`
  - 缺失 `androidTextureFlavor` 时默认 `ASTC`
- 当前版本明确要求删库重建，不保留旧数据库兼容。

## 2. 功能点审计表

| 功能点 | 代码位置 | 当前实现 | 状态 |
| --- | --- | --- | --- |
| 平台枚举 | [BuildEnums.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildEnums.cs#L27) | 新增 `BuildPlatform`，包含 `Windows`、`Android` | 已完成 |
| 项目 Android 开关 | [ProjectConfig.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/ProjectConfig.cs#L27) | 新增 `AndroidEnabled`，默认 `true` | 已完成 |
| Android Flavor 字段 | [ProjectConfig.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/ProjectConfig.cs#L29) | 新增 `AndroidTextureFlavor`，默认 `ASTC` | 已完成 |
| 构建记录平台字段 | [BuildRecord.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildRecord.cs#L19) | 每条构建记录都保存 `Platform` | 已完成 |
| 定时任务平台字段 | [BuildSchedule.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildSchedule.cs#L19) | 每条定时任务都保存 `Platform` | 已完成 |
| 构建请求平台字段 | [BuildContracts.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Contracts/BuildContracts.cs#L9) | 手动构建请求带 `Platform` | 已完成 |
| 定时任务请求平台字段 | [ScheduleContracts.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Contracts/ScheduleContracts.cs#L11) | 定时任务请求和返回都带 `Platform` | 已完成 |
| 项目导入 Android 字段兼容 | [ProjectContracts.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Contracts/ProjectContracts.cs#L16) | 请求模型允许旧 JSON 缺失 Android 字段 | 已完成 |
| 旧 JSON 默认值补全 | [ProjectContracts.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Contracts/ProjectContracts.cs#L91) | 缺失时自动补 `androidEnabled = true`、`androidTextureFlavor = ASTC` | 已完成 |
| 数据库建模 | [BuildDbContext.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Data/BuildDbContext.cs#L44) | Android 相关字段和平台列已映射 | 已完成 |
| 数据库迁移 | [202603270002_AddBuildPlatformsAndAndroidProjects.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Migrations/202603270002_AddBuildPlatformsAndAndroidProjects.cs#L11) | 新增 Android / 平台列；按新模型建库 | 已完成 |
| 升级策略 | [DatabaseMigrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Data/DatabaseMigrator.cs#L5) | 迁移模式下要求手动删库重建 | 已完成 |
| Windows / Android Target 解析 | [BuildCommandFactory.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L42) | Windows 按原有 `Game/Client/Server`，Android 复用 `GameTarget` | 已完成 |
| Android 仅支持 Game | [BuildCommandFactory.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L73) | 后端只允许 Android + `Game` | 已完成 |
| Android 命令拼装 | [BuildCommandFactory.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L173) | 固定 `-targetplatform=Android -cookflavor=ASTC` | 已完成 |
| 构建记录保存平台 | [BuildOrchestrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildOrchestrator.cs#L90) | 入队时写入 `Platform` 和平台对应 `TargetName` | 已完成 |
| Android 预检入口 | [ProjectValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L166) | Android 请求会走专项校验 | 已完成 |
| Android 启用开关校验 | [ProjectValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L188) | `androidEnabled = false` 时直接拦截 | 已完成 |
| AndroidRuntimeSettings 校验 | [ProjectValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L193) | 未检测到 Android 配置时直接拦截 | 已完成 |
| SDK / NDK / JDK / License 校验 | [ProjectValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L198) | Android 环境不足时入队前失败 | 已完成 |
| Android Flavor 校验 | [ProjectValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L105) | 第一版只接受 `ASTC` | 已完成 |
| 全项目 Android 定时任务筛选 | [BuildScheduleRunner.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleRunner.cs#L186) | 只挑 `androidEnabled` 的项目 | 已完成 |
| Android 定时任务校验 | [BuildScheduleValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleValidator.cs#L29) | Android 定时任务只支持 `Game` | 已完成 |
| 归档目录命名带平台 | [StoragePaths.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/StoragePaths.cs#L110) | 目录名含 `Windows` / `Android` | 已完成 |
| 下载文件名带平台 | [StoragePaths.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/StoragePaths.cs#L75) | zip 命名含平台字段 | 已完成 |
| 健康检查平台摘要 | [Program.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Program.cs#L95) | 返回 `supportedPlatforms` | 已完成 |
| 构建页平台选择 | [BuildsPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/BuildsPage.tsx#L173) | 新增 `Windows / Android` 下拉 | 已完成 |
| 构建页 Android 限制提示 | [BuildsPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/BuildsPage.tsx#L220) | UI 明确提示 Android 第一版限制 | 已完成 |
| 项目页 Android 字段 | [ProjectsPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/ProjectsPage.tsx#L265) | 新增 Android 开关和 Flavor 选择 | 已完成 |
| 项目页默认启用 Android | [ProjectsPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/ProjectsPage.tsx#L35) | 新建项目默认 `androidEnabled = true` | 已完成 |
| 定时任务页平台选择 | [SchedulesPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/SchedulesPage.tsx#L341) | 定时任务支持选择 Android | 已完成 |
| 构建详情平台展示 | [BuildDetailPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/BuildDetailPage.tsx#L314) | 详情页显示平台 | 已完成 |
| 列表平台格式化 | [formatters.ts](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/components/formatters.ts#L54) | 平台统一显示为 `Windows` / `Android` | 已完成 |
| Android 功能边界文档 | [README.md](/D:/UnrealGit/unreal-build/unreal-local-build/README.md#L36) | 已补 Android 第一版边界和升级说明 | 已完成 |
| 字段说明文档 | [build-field-reference.md](/D:/UnrealGit/unreal-build/unreal-local-build/docs/build-field-reference.md#L1) | 已补平台与 Android 字段解释 | 已完成 |

## 3. 已完成的本地验证

### 3.1 编译验证

- 后端 `dotnet build` 通过
- 前端 `npm run build` 通过

### 3.2 真实 Android 端到端验证

真实验证结果如下：

- 项目：`LyraStarterGame`
- 平台：`Android`
- 类型：`Game`
- 配置：`Development`
- Flavor：`ASTC`
- 构建 ID：`142bad88-0840-4dc3-a6e5-a90ed7dfd20e`
- 结果：`Succeeded`
- 耗时：约 `16m 12s`

相关产物：

- 构建日志：[build.log](/D:/UnrealGit/unreal-build/unreal-local-build/backend/AppData/builds/142bad8808404dc3a6e5a90ed7dfd20e/build.log)
- 产物 zip：[20260330-013207-LyraStarterGame-Android-Development-Game-r12557.zip](/D:/UnrealGit/unreal-build/unreal-local-build/backend/AppData/builds/142bad8808404dc3a6e5a90ed7dfd20e/20260330-013207-LyraStarterGame-Android-Development-Game-r12557.zip)
- 归档目录：[20260330-013207-LyraStarterGame-Android-Development-Game-r12557-142bad88](/D:/UEProjects/AutoBuilds/20260330-013207-LyraStarterGame-Android-Development-Game-r12557-142bad88)

### 3.3 旧项目配置 JSON 导入验证

已用缺失 Android 字段的旧版项目配置 JSON 做过真实导入验证，结果为：

- 可正常导入
- 自动补全：
  - `androidEnabled = true`
  - `androidTextureFlavor = ASTC`

## 4. 当前剩余风险

### 4.1 Android 环境探测方式仍偏保守

当前预检主要依赖环境变量和常见目录探测，见 [ProjectValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L353)。  
如果某些打包机使用的是 Unreal Turnkey / AutoSDK 的特殊布局，可能出现“实际能打包，但预检先拦截”的假阴性。

### 4.2 同一项目跨平台切换时构建时间会变长

当前系统继续按项目串行，不允许同一项目 Windows / Android 并发构建。  
这能保证安全，但会让同一项目频繁切平台时总耗时上升。对应逻辑在 [BuildOrchestrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildOrchestrator.cs#L204)。

### 4.3 Android 进度显示精度仍可继续优化

真实 Android 构建中，后段日志有机会让阶段显示短暂跳回 `Build`。  
这是 UI 精度问题，不影响实际出包。相关解析逻辑在 [BuildOrchestrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildOrchestrator.cs#L676)。

## 5. 建议的后续回归清单

建议每次 Android 相关改动后至少回归以下场景：

1. Windows 手动构建
2. Android 手动构建
3. 同一项目 Windows 后 Android 排队
4. Android 定时任务自动入队
5. 旧项目 JSON 导入
6. 下载 zip 文件名是否带平台
7. 构建历史与详情页的平台展示是否正确

## 6. 对应 GitHub 提交

本轮 Android 支持的主要提交为：

- [`28c505b`](https://github.com/liuwjchinal/unreal-build/commit/28c505b50fa7b5802039855953d83aa7f44844cf) `Add Android build platform support and legacy project import defaults`

该提交已经包含本文档中列出的 Android 支持核心代码。
