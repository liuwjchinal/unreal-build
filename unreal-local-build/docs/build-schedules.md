# 定时构建任务说明

## 概述

本地 Unreal 打包工具支持“每日固定时间”的定时构建任务。定时任务不会直接绕过现有构建系统执行，而是在到点后自动生成标准构建请求，并复用现有的：

- 项目校验
- SVN 自愈与更新
- 构建排队
- 日志采集
- 构建产物下载
- 构建缓存清理

相关实现：

- 调度模型：[BuildSchedule.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildSchedule.cs#L1)
- 调度服务：[BuildScheduleService.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleService.cs#L1)
- 调度执行器：[BuildScheduleRunner.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleRunner.cs#L1)
- 调度校验器：[BuildScheduleValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleValidator.cs#L1)
- 接口入口：[Program.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Program.cs#L355)
- 前端页面：[SchedulesPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/SchedulesPage.tsx#L1)

## 功能边界

- 第一版只支持“每日固定时间”，例如 `12:00`、`13:00`、`00:00`
- 定时任务触发的 Revision 固定为 `HEAD`
- 支持两种范围：
  - 单项目定时任务
  - 所有项目定时任务
- 时间解释统一按部署机本地时间
- 不补跑已经错过的任务
- 不支持 Cron 表达式、每周规则、工作日规则、节假日规则

## 字段说明

### 基础字段

- `任务名称`
  - 仅用于页面展示、日志定位和后续排查
- `启用任务`
  - 关闭后不会在到点时自动触发
- `每日触发时间`
  - 使用 `HH:mm` 的 24 小时制格式
- `触发范围`
  - `SingleProject`：只触发一个项目
  - `AllProjects`：触发时读取当前全部项目并逐个入队

### 构建参数

- `项目`
  - 仅 `SingleProject` 任务需要指定
- `Target 类型`
  - `Game / Client / Server`
- `构建配置`
  - 例如 `Development / Shipping`
- `Clean`
  - 是否追加 `-clean`
- `Pak`
  - 是否追加 `-pak`
- `IoStore`
  - 是否追加 `-iostore`
- `额外 UAT 参数`
  - 直接附加到本次 UAT 命令末尾

### 运行状态字段

- `最近触发时间`
  - 上一次成功触发调度的 UTC 时间
- `最近入队数量`
  - 上一次触发时成功加入队列的构建数
- `最近触发信息`
  - 上一次触发的摘要，例如“请求 2 个项目，成功入队 2 个，失败 0 个”

## 执行规则

### 单项目任务

到点后生成 1 条标准构建请求：

- `Revision = HEAD`
- `ProjectId = 当前任务绑定的项目`
- `TargetType = 当前任务配置`
- `BuildConfiguration = 当前任务配置`
- `Clean / Pak / IoStore / ExtraUatArgs` 也沿用任务配置

### 所有项目任务

到点时读取当前全部已配置项目，然后逐个入队：

- 每个项目各生成 1 条标准构建请求
- 某个项目入队失败，不阻断其他项目
- 后续仍受现有并发和项目互斥策略约束

## 与现有构建调度器的关系

定时任务不是第二套执行器，而是复用现有构建调度器：

- 入队入口：[BuildOrchestrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildOrchestrator.cs#L49)
- 调度触发时会补充触发元数据：
  - `TriggerSource = Schedule`
  - `ScheduleId = 当前定时任务 ID`

因此构建历史里可以区分：

- 手动触发
- 定时触发

## 健康检查

接口：[http://localhost:5080/api/health](http://localhost:5080/api/health)

新增字段：

- `scheduleServiceEnabled`
  - 是否启用定时构建后台服务
- `scheduleScanIntervalSeconds`
  - 调度扫描间隔
- `enabledScheduleCount`
  - 当前启用中的定时任务数量
- `lastScheduleTickUtc`
  - 最近一次调度扫描时间

## 配置项

配置文件：[appsettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.json#L1)

- `App:ScheduleServiceEnabled`
  - 是否启用定时构建后台服务
- `App:ScheduleScanIntervalSeconds`
  - 调度扫描间隔，默认 `30`

## 失败行为

定时触发成功只表示“已经尝试入队”，不代表一定成功构建。后续仍可能因为这些原因失败：

- 项目配置缺失或 Target 填错
- SVN 工作副本异常
- Unreal 插件缺失
- UAT / UBT 编译失败
- 打包机资源不足

这些失败都会继续落到现有的：

- 构建记录
- 构建日志
- 错误摘要

## 真实验收记录

### 单项目定时任务验收

在本机常驻服务上，已对 `LyraStarterGame` 做过真实自动触发验证：

- 定时任务：`LyraStarterGame Daily 13:00 Development`
- 任务 ID：`69089ebc-2e00-43d6-bf35-d0b717054d9b`
- 真实自动触发构建：[6b83aec5-71f0-4dd0-bde3-b1c13d8f17b4](http://127.0.0.1:5080/api/builds/6b83aec5-71f0-4dd0-bde3-b1c13d8f17b4)
- 触发来源：`Schedule`
- 构建结果：成功

### 所有项目定时任务验收

在本机常驻服务上，已对“所有项目定时任务”做过真实自动触发验证：

- 触发时间：`2026-03-27 15:30:25` 本地时间
- 触发结果：请求 `2` 个项目，成功入队 `2` 个，失败 `0` 个
- 实际生成的构建记录：
  - [78edfd0c-de9d-41bc-adab-0743e04e341e](http://127.0.0.1:5080/api/builds/78edfd0c-de9d-41bc-adab-0743e04e341e)
  - [3697a2f7-5c39-42a6-8b7b-cc1d09d5cacd](http://127.0.0.1:5080/api/builds/3697a2f7-5c39-42a6-8b7b-cc1d09d5cacd)

测试完成后，临时全项目测试任务和临时测试项目均已从当前服务中清理。

## 已修复的边界问题

### 当前分钟漏触发

之前如果在“当前这一分钟”内新建、修改或重新启用一个定时任务，而又刚好错过最近一次 30 秒扫描，就可能漏掉当天这次触发。

现在已经修复：

- 新建任务时，如果它在当前分钟已到点，会立即补做一次 due 判断
- 修改任务时，如果它在当前分钟已到点，会立即补做一次 due 判断
- 重新启用任务时，如果它在当前分钟已到点，会立即补做一次 due 判断

相关代码：

- [BuildScheduleRunner.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleRunner.cs#L11)
- [Program.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Program.cs#L377)
