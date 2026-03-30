# 构建系统字段说明

本文档说明 [unreal-local-build](/D:/UnrealGit/unreal-build/unreal-local-build) 当前构建系统中的关键业务字段，重点覆盖新增的平台和 Android 相关字段。

## 项目配置字段

### `androidEnabled`

- 所属模型：[`ProjectConfig.AndroidEnabled`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/ProjectConfig.cs#L27)
- 前端表单：[`ProjectsPage.tsx`](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/ProjectsPage.tsx#L35)
- 默认值：`true`

含义：

- 表示这个项目是否允许被本系统当成 Android 可构建项目使用。
- 这是本系统自己的业务开关，不是 Unreal 引擎原生字段。

会影响的行为：

- 构建页是否允许为该项目选择 `Android` 平台，见 [`BuildsPage.tsx`](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/BuildsPage.tsx#L33)
- 后端是否允许 Android 构建请求通过预检，见 [`ProjectValidator.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L184)
- “所有项目”的 Android 定时任务是否会把该项目纳入候选，见 [`BuildScheduleRunner.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleRunner.cs#L186)
- Android 定时任务校验是否通过，见 [`BuildScheduleValidator.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleValidator.cs#L69)

不会影响的行为：

- 不会自动修改 Unreal 项目的 Android 配置
- 不会自动安装 Android SDK / NDK / JDK
- 不会影响 Windows 构建

说明：

- 现在新建项目时默认就是 `true`，目的是让每个项目默认都具备“可开启 Android 构建”的准入状态。
- 但这不代表项目一定能成功打 Android 包。真正入队时仍然需要通过 Android 配置和环境预检。

### `androidTextureFlavor`

- 所属模型：[`ProjectConfig.AndroidTextureFlavor`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/ProjectConfig.cs#L29)
- 当前默认值：`ASTC`

含义：

- Android 构建使用的纹理压缩 Flavor。
- 当前第一版固定只支持 `ASTC`。

后端约束：

- 预检只允许 `ASTC`，见 [`ProjectValidator.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/ProjectValidator.cs#L105)
- 命令工厂会把它转成 Android UAT 参数，见 [`BuildCommandFactory.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L180)

### `gameTarget / clientTarget / serverTarget`

- 所属模型：[`ProjectConfig`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/ProjectConfig.cs#L19)

含义：

- 这些字段决定不同平台和 Target 类型下最终传给 UAT 的 `-target=...` 值。

当前规则：

- Windows 支持 `Game / Client / Server`
- Android 第一版只支持 `Game`
- Android 构建时会直接复用 `gameTarget`，见 [`BuildCommandFactory.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L42)

## 构建请求与构建记录字段

### `platform`

- 请求字段：[`QueueBuildRequest.Platform`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Contracts/BuildContracts.cs#L9)
- 记录字段：[`BuildRecord.Platform`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildRecord.cs#L19)
- 定时任务字段：[`BuildSchedule.Platform`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildSchedule.cs#L19)

含义：

- 构建任务的平台类型，是当前系统里的一等字段。
- 当前只支持：
  - `Windows`
  - `Android`

会影响的行为：

- UAT 命令拼装平台分支，见 [`BuildCommandFactory.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L135)
- 构建历史和详情页的平台展示
- 定时任务的执行平台
- 产物命名和归档目录命名

### `triggerSource`

- 记录字段：[`BuildRecord.TriggerSource`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildRecord.cs#L15)

含义：

- 标识构建是手动触发还是定时触发。
- 当前取值：
  - `Manual`
  - `Schedule`

用途：

- 方便在构建历史里区分来源
- 便于排查定时任务是否按预期自动入队

### `scheduleId`

- 记录字段：[`BuildRecord.ScheduleId`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildRecord.cs#L17)

含义：

- 如果这条构建是由定时任务触发，会记录来源任务 ID。
- 手动构建时为空。

## 平台对执行链路的影响

### Windows

- 平台参数固定为 `Win64`
- 支持 `Game / Client / Server`

### Android

- 当前第一版只支持 `Game`
- 命令固定包含：
  - `-targetplatform=Android`
  - `-cookflavor=ASTC`
- 见 [`BuildCommandFactory.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L173)

## 命名规则

### 下载产物文件名

当前 zip 文件名会包含平台字段，格式为：

- `构建日期-项目名称-平台-构建配置-Target类型-svn版本号.zip`

实现见：

- [`StoragePaths.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/StoragePaths.cs#L75)

### 归档目录名

归档目录也会包含平台字段，避免 Windows 和 Android 构建结果混淆。

实现见：

- [`StoragePaths.cs`](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/StoragePaths.cs#L110)

## 当前默认规则总结

- 新建项目默认 `androidEnabled = true`
- 新建项目默认 `androidTextureFlavor = ASTC`
- Android 第一版只支持 `Game`
- 同一项目的 Windows / Android 构建继续串行，不允许并发互踩
