# 定时构建任务说明

## 概述

本地 Unreal 打包工具支持“每日固定时间”的定时构建任务。定时任务不会直接执行构建流程，而是到点后自动生成标准构建请求，并复用现有的构建校验、排队、日志、下载和清理机制。

相关实现：

- 调度模型：[BuildSchedule.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Models/BuildSchedule.cs#L1)
- 调度服务：[BuildScheduleService.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleService.cs#L1)
- 调度执行器：[BuildScheduleRunner.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleRunner.cs#L1)
- 调度校验器：[BuildScheduleValidator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildScheduleValidator.cs#L1)
- 接口入口：[Program.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Program.cs#L1)
- 前端页面：[SchedulesPage.tsx](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/src/pages/SchedulesPage.tsx#L1)

## 功能边界

- 第一版只支持每日固定时间，例如 `12:00`、`00:00`
- 定时任务的 SVN 版本固定为 `HEAD`
- 支持两种任务范围：
  - 单项目任务
  - 所有项目任务
- 调度时间按**部署机本地时间**解释
- 不补跑已经错过的任务
- 不支持 Cron 表达式、每周规则、工作日规则、节假日规则

## 字段含义

### 基础字段

- `任务名称`
  - 用于页面展示、日志识别和后续筛选
- `启用任务`
  - 开启后才会在定时时间自动触发
- `每日触发时间`
  - 使用 `HH:mm` 24 小时制格式
- `触发范围`
  - `单项目`：只触发一个项目
  - `所有项目`：触发时读取当前数据库中的全部项目

### 构建参数

- `项目`
  - 仅单项目任务需要填写
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
  - 按行或逗号分隔，会直接附加到本次 UAT 命令末尾

### 运行状态字段

- `最近触发时间`
  - 上次成功触发调度的 UTC 时间
- `最近入队数量`
  - 上次触发时成功加入队列的构建数
- `最近触发信息`
  - 上次触发的摘要，例如请求数、成功数和失败数

## 执行规则

### 单项目任务

到点后生成 1 条构建请求：

- `Revision = HEAD`
- `ProjectId = 当前选中的项目`
- `TargetType = 当前任务配置`
- `BuildConfiguration = 当前任务配置`
- 其余 `Clean / Pak / IoStore / ExtraUatArgs` 也沿用任务配置

### 所有项目任务

到点时读取**当前所有项目**，然后逐个入队：

- 每个项目都生成一条标准构建请求
- 某个项目入队失败，不会阻断其他项目
- 后续仍受现有并发控制约束

## 与现有调度器的关系

定时任务不是第二套执行器，而是复用现有构建调度器：

- 入队接口：[BuildOrchestrator.cs](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Services/BuildOrchestrator.cs#L41)
- 定时触发时会补充触发元数据：
  - `TriggerSource = Schedule`
  - `ScheduleId = 当前任务 ID`

这样构建历史里可以区分：

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
  - 调度扫描间隔，建议保持默认 `30`

## 失败行为

定时触发成功仅表示“已经尝试入队”，不代表一定成功构建。后续仍可能因为以下原因失败：

- 项目配置缺失或 Target 填错
- SVN 工作副本状态异常
- Unreal 插件缺失
- UAT / UBT 编译失败
- 打包机资源不足

这些失败都会继续落到现有的构建记录、构建日志和错误摘要里。
