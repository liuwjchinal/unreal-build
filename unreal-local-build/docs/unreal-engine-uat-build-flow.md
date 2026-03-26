# Unreal Engine 打包构建源码流程说明

本文基于本机安装的 Unreal Engine 5.7.3 源码，对 Windows 下常见的 `RunUAT.bat BuildCookRun` 打包链路做源码级说明。目标是回答三类问题：

- Web 打包工具底层真正调用了什么
- UAT/UBT 在源码里是怎样串起 `Build -> Cook -> Stage -> Package -> Archive`
- 为什么同一套 Engine 默认不能真正并行执行多个不同项目的完整打包

## 1. 整体入口关系

从业务层看，当前本地打包工具最终拼出的命令是 `RunUAT.bat BuildCookRun ...`，相关逻辑在 [BuildCommandFactory.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildCommandFactory.cs#L25)。

从 Unreal 源码看，实际调用链大致是：

```text
RunUAT.bat
  -> AutomationTool/Program.cs
    -> 解析命令行、建立 CommandEnvironment、初始化日志
    -> 执行 BuildCookRun 命令
      -> BuildCookRun.Automation.cs
        -> Project.Build()
        -> Project.Cook()
        -> Project.CopyBuildToStagingDirectory()
        -> Project.Package()
        -> Project.Archive()
```

关键入口文件：

- UAT 主入口：[Program.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Program.cs#L450)
- BuildCookRun 主命令：[BuildCookRun.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Scripts/BuildCookRun.Automation.cs#L250)
- UBT 入口：[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L612)

## 2. UAT 启动阶段做了什么

### 2.1 单实例检查

UAT 在真正执行命令前，会先走单实例保护：

- 主入口读取 `-WaitForUATMutex`：[Program.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Program.cs#L450)
- 然后调用单实例包装器：[Program.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Program.cs#L453)
- 具体互斥实现位于 [ProcessSingleton.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/Shared/EpicGames.Build/Automation/ProcessSingleton.cs#L35)

这里的关键点是：

- mutex 名是根据 `EntryAssemblyLocation` 生成的：[ProcessSingleton.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/Shared/EpicGames.Build/Automation/ProcessSingleton.cs#L53)
- 也就是说，**同一套 Engine 安装路径下的 AutomationTool 默认共享同一把全局锁**
- 如果已有一个 UAT 在跑，新的 UAT 默认会直接抛异常：  
  [ProcessSingleton.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/Shared/EpicGames.Build/Automation/ProcessSingleton.cs#L72)

这就是我们之前在实际打包里看到 `A conflicting instance of AutomationTool is already running` 的源码来源。

### 2.2 日志目录和命令环境初始化

UAT 启动后会创建 `CommandEnvironment`，其中会初始化：

- `EngineSavedFolder`
- `LogFolder`
- `FinalLogFolder`
- `CmdExe`
- `LocalRoot`

相关逻辑在 [CommandEnvironment.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/CommandEnvironment.cs#L90)。

几个需要特别注意的点：

- `EngineSavedFolder` 默认是 `Engine/Programs/AutomationTool/Saved`：[CommandEnvironment.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/CommandEnvironment.cs#L90)
- `LogFolder` 默认也是从这里展开出来的：[CommandEnvironment.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/CommandEnvironment.cs#L104)
- 如果当前进程被认定为“唯一实例”，还会清空整个日志目录：[CommandEnvironment.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/CommandEnvironment.cs#L119)

这说明 UAT 的默认日志根目录本身就是按 Engine 共享的，而不是按项目或任务隔离的。

### 2.3 文件日志创建

UAT 真正开始写文件日志的逻辑在 [LogUtils.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/LogUtils.cs#L40)：

- 默认日志文件名是 `Log.txt`
- 如果占用则递增尝试 `Log_2.txt`、`Log_3.txt`

这能解决“单个文件同名”问题，但不能解决“共享日志目录被清空”或“多任务写同一个根目录”的问题。

## 3. BuildCookRun 主流程

`BuildCookRun` 的核心流程非常直接，源码就在 [BuildCookRun.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Scripts/BuildCookRun.Automation.cs#L250)：

1. `Project.Build(...)`
2. `Project.Cook(...)`
3. `Project.CopyBuildToStagingDirectory(...)`
4. `Project.Package(...)`
5. `Project.Archive(...)`
6. `Project.Deploy(...)`
7. `Project.Run(...)`
8. `Project.GetFile(...)`

也就是说，`BuildCookRun` 本身是一个**串行管线命令**。如果调用者没有显式用参数跳过其中某一段，那么它会从 Build 一路跑到 Archive。

## 4. Build 阶段怎样进入 UBT

Build 阶段并不是 UAT 自己编译，而是 UAT 再去调用 UBT。

几个关键点：

- `ProjectParams` 支持 `-ubtargs=` 透传参数给 UBT：[ProjectParams.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/ProjectParams.cs#L24)
- `ubtargs` 的解析在 [ProjectParams.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/ProjectParams.cs#L1041)
- 最终保存在 `ProjectParams.UbtArgs`：[ProjectParams.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/ProjectParams.cs#L2470)
- UAT 在构造实际 UBT 命令时会追加这些参数：[UnrealBuild.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/UnrealBuild.cs#L230)

这意味着：

- 业务层并不是只能“调用 BuildCookRun”
- 如果需要调整 UBT 的 mutex 行为、编译参数、临时目录或其它底层构建行为，可以通过 `-UbtArgs=` 往下传

## 5. UBT 的单实例与临时目录机制

### 5.1 UBT 单实例保护

UBT 也有独立的单实例控制：

- `-NoMutex`：[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L181)
- `-WaitMutex`：[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L187)
- 实际获取全局锁的逻辑：[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L612)

和 UAT 一样，UBT 默认也是按执行程序集路径生成 mutex 名，所以**同一套 Engine 下默认只能有一个 UBT 真正执行**。

### 5.2 UBT 默认临时目录

UBT 会在启动时覆盖 `TMP/TEMP`，相关逻辑在 [UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L574)。

默认行为是：

- temp 根目录来自系统临时目录下的 `UnrealBuildTool`
- 再拼一个基于 `UnrealBuildTool.dll` 路径 hash 的子目录：[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L591)

这意味着不同项目只要共用同一套 Engine，默认就会共享同一套 UBT 临时目录。

## 6. Build 相关中间文件落盘位置

### 6.1 Manifest

UAT/UBT 的 manifest 目录逻辑在 [UnrealBuild.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/UnrealBuild.cs#L735)：

- installed engine 且指定了项目文件时，manifest 落到 `Project/Intermediate/Build`
- 否则可能落到 `Engine/Intermediate/Build`

这说明 installed build 场景下，不同项目的 manifest 大多数能天然分开；这也是“不同项目并行”比“同项目并行”更可控的原因之一。

### 6.2 ActionHistory

UBT 的 action history 在 [ActionHistory.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/System/ActionHistory.cs#L171) 和 [ActionHistory.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/System/ActionHistory.cs#L181)：

- Engine 目标可能写到 `Engine/.../ActionHistory.bin`
- 项目目标则写到 `Project/.../ActionHistory.dat`

所以如果是 installed engine、纯项目目标编译，不同项目在 action history 维度通常可以拆开；但只要涉及 Engine 目标或 Program 目标，就仍然会回到 Engine 级共享状态。

## 7. Cook / Stage / Package / Archive 阶段

### 7.1 Cook

Cook 逻辑主入口在 [CookCommand.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Scripts/CookCommand.Automation.cs#L255)。

Cook 阶段会：

- 组织命令行参数
- 启动 Editor/Commandlet 进行 Cook
- 生成 CookServer 日志、CookerStats 等

注意：

- Cook 相关日志默认也落在 `CmdEnv.LogFolder`
- 如果这个目录不按任务隔离，就会出现多任务日志混杂、文件覆盖或清理互相影响

### 7.2 Stage

Stage 由 `CopyBuildToStagingDirectory` 负责，其实现文件是 [CopyBuildToStagingDirectory.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Scripts/CopyBuildToStagingDirectory.Automation.cs#L1)。

这一段会做：

- 生成待拷贝 manifest
- 收集 UFS / NonUFS / DebugFiles
- 输出 Pak/IoStore 命令文件

这些辅助文件同样默认写在 `CmdEnv.LogFolder`，比如：

- `PakCommands.txt`
- `IoStoreCommands.txt`
- `PrePak_*`
- `FinalCopy*`

如果多个任务共享日志根目录，就会在这里产生明显的文件竞争面。

### 7.3 Package

Package 主入口在 [PackageCommand.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Scripts/PackageCommand.Automation.cs#L15)。

Package 进一步调用平台相关实现，例如 Windows 平台在 [WinPlatform.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Win/WinPlatform.Automation.cs#L332) 构造 Pak 参数。

这一阶段通常会调用：

- `UnrealPak`
- `IoStore`

它们本身未必通过 UAT 顶层 mutex 保护，因此真正要做多项目并行时，还要关注这些外部工具是否共享输出目录、签名文件、响应文件和平台缓存。

### 7.4 Archive

Archive 主入口在 [ArchiveCommand.Automation.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/Scripts/ArchiveCommand.Automation.cs#L53)。

这一阶段的主要职责是把 staging/package 后的内容整理到最终归档目录。  
从工程实践上看，Archive 的并发风险通常低于 Build 和 Cook，但前提仍然是不同任务的 `ArchiveDirectory` 已经按任务隔离。

## 8. 为什么同一套 Engine 默认不能真正并行

结合上面的源码，可以把原因总结为四层：

### 8.1 UAT 顶层互斥是 Engine 级

[ProcessSingleton.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/Shared/EpicGames.Build/Automation/ProcessSingleton.cs#L53) 的 mutex key 取决于入口程序集路径，同一套 Engine 自然是同一把锁。

### 8.2 UBT 互斥也是 Engine 级

[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L617) 也是按程序集路径生成 mutex。

### 8.3 UAT 默认日志目录是共享的

[CommandEnvironment.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/AutomationTool/AutomationUtils/CommandEnvironment.cs#L104) 初始化 `LogFolder` 时默认走 Engine 共享目录，且唯一实例还会清空目录。

### 8.4 UBT 默认临时目录也是共享的

[UnrealBuildTool.cs](/D:/UnrealEngine-5.7.3-release/Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs#L591) 只按 Engine DLL 路径 hash temp 目录，不区分项目。

所以“把前端并发数配成 2”并不等于底层真正并行。只要不改源码或至少不改底层传参和目录隔离逻辑，最终仍会被 UAT/UBT 串行化。

## 9. 实现不同项目并行时，推荐改哪些点

基于源码结构，推荐的改造顺序是：

### 9.1 优先改 UAT/UBT 的单实例粒度

目标不是彻底取消单实例，而是从“Engine 级单实例”改成“项目级单实例”：

- 同一项目继续互斥
- 不同项目允许并发

建议做法：

- 给 UAT 增加 `Project` scope 的 mutex key
- 给 UBT 增加同样的 `Project` scope
- 同时保留 `Engine` scope 作为默认保守模式

### 9.2 必须补任务级日志目录

如果只改 mutex，不改日志目录和清理策略，最终还是会在：

- `Log.txt`
- `CookServer.log`
- `PakCommands.txt`
- `IoStoreCommands.txt`

这些文件上互相影响。

### 9.3 必须补 UBT temp 目录隔离

否则不同项目并行 UBT 时，`TMP/TEMP` 和中间响应文件仍可能竞争。

### 9.4 建议对 Engine 目标保守处理

虽然“不同项目并行”是目标，但一旦本次构建需要重编：

- Engine 模块
- Program 目标
- `UnrealPak`
- `ShaderCompileWorker`

最好仍然退回到 Engine 级互斥，而不是硬并行。

## 10. 对当前 Web 打包工具的直接启发

对当前仓库里的本地打包工具来说，源码层面的结论是：

1. 当前把 `UatConcurrency` 提高，并不会自动变成真正并行  
   证据在 [BuildOrchestrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildOrchestrator.cs#L41) 和 [appsettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.json#L5)

2. 业务层如果要尝试并行，不能只在服务端调度器层绕过，需要有 Unreal 源码或底层命令行参数支持

3. 真正稳妥的实现方式不是“直接去掉锁”，而是：
   - 项目级互斥
   - 任务级日志目录
   - 任务级临时目录
   - Engine 目标保守串行

## 11. 小结

`RunUAT BuildCookRun` 在 Unreal 源码里本质上是一个串行流程命令。默认情况下，同一套 Engine 之所以不能真正并行打多个项目，不是单一原因，而是以下几层叠加：

- UAT 顶层单实例
- UBT 单实例
- UAT 共享日志目录
- UBT 共享临时目录
- 部分 Engine/Program 目标仍然天然共享中间产物和 action history

因此，如果后续要在同一套 Engine 下实现“不同项目真正并行”，必须把方案设计成“**项目级互斥 + 任务级目录隔离 + Engine 目标保守串行**”，而不是简单地关闭 mutex。
