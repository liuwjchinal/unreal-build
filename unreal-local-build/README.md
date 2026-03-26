# Unreal Local Build

轻量级 Unreal Engine Windows 本地打包 Web 工具。

适用场景：
- 单台 Windows 打包机
- SVN 工作副本更新
- Unreal `RunUAT.bat BuildCookRun` 命令行打包
- 本机和局域网内其它电脑通过浏览器访问

## 目录结构

- `backend/`
  - ASP.NET Core Minimal API
  - SQLite 数据库存储项目配置和构建记录
  - 后台调度器、日志读取、SSE、下载接口
- `frontend/`
  - Vite + React + TypeScript
  - 项目配置页、构建列表页、构建详情页
- `UnrealBuildWeb.sln`
  - Visual Studio 解决方案

## 主要功能

- 多项目配置管理
- 跨项目并发构建，同项目默认串行
- 支持 `Game / Client / Server`
- 支持 `Development / Shipping`
- 支持 `Clean / Pak / IoStore / 额外 UAT 参数`
- 构建列表、构建详情、实时日志、阶段进度
- 下载打包产物 zip
- 项目配置 JSON 导入导出
- 自动清理遗留 `AutomationTool` 进程
- 自动清理本地构建缓存

## 运行环境

建议环境：
- Windows 10 / 11
- .NET SDK 10
- Node.js 20 或更高版本
- `svn` 已加入 `PATH`
- 已安装 Unreal Engine，且存在 `Engine\Build\BatchFiles\RunUAT.bat`

## 首次安装

### 1. 安装前端依赖

在 [frontend/package.json](/D:/UnrealGit/unreal-build/unreal-local-build/frontend/package.json#L1) 所在目录执行：

```powershell
cd .\unreal-local-build\frontend
npm install
```

### 2. 恢复并编译后端

```powershell
cd ..\backend
dotnet restore
dotnet build
```

### 3. 首次启动后自动生成的数据目录

后端第一次启动后会自动创建：
- `backend/AppData/`
- `backend/AppData/unreal-build.db`
- `backend/AppData/builds/`

## 关键配置

主配置文件：
- [backend/appsettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.json#L1)
- [backend/appsettings.Development.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.Development.json#L1)

常用配置项：

- `App:ServerUrl`
  - 服务监听地址
  - 默认值：`http://0.0.0.0:5080`
- `App:StorageRoot`
  - 本地数据库、日志、临时构建缓存目录
  - 默认值：`AppData`
- `App:GlobalConcurrency`
  - 全局可同时运行的构建任务上限
- `App:UatConcurrency`
  - 真正进入 `RunUAT` 阶段的并发上限
  - 默认值：`1`
- `App:BuildRetentionDays`
  - 超过该天数的本地构建缓存会被清理
  - 默认值：`14`
- `App:CleanupIntervalMinutes`
  - 清理任务执行间隔
  - 默认值：`60`
- `App:KeepRecentSuccessfulBuildsPerProject`
  - 每个项目默认保留最近多少个成功构建的本地缓存
  - 默认值：`3`
  - 设置为 `-1` 表示禁用这条规则
- `App:MaxBuildCacheSizeGb`
  - `backend/AppData/builds` 总缓存体积上限，超过后会按最旧构建先删
  - 默认值：`20`
  - 设置为 `0` 表示禁用这条规则
- `App:CleanupArchiveDirectories`
  - 是否连外部归档目录一起删除
  - 默认值：`false`
  - 默认只清理 `backend/AppData/builds` 里的 zip、日志和中间文件，不动项目配置里的归档目录
- `App:AutomationToolCleanupEnabled`
  - 是否启用遗留 `AutomationTool` 清理
- `App:AutomationToolCleanupMode`
  - `TrackedOnly`：只清理本系统启动并追踪过的 UAT 进程
  - `AnyWhenIdle`：系统空闲时清理整机所有 `RunUAT.bat` / `AutomationTool.dll`

## 本地构建缓存清理策略

默认策略分三层：

1. 超过 `14` 天的已完成构建缓存会被清理
2. 每个项目只保留最近 `3` 个成功构建的本地缓存
3. `backend/AppData/builds` 总缓存超过 `20 GB` 后，会按最旧构建开始回收

默认清理范围：
- 构建日志
- 下载用 zip
- 构建临时目录
- `uat-process.json`

默认不会清理：
- 项目配置中的外部归档目录

如果你希望外部归档目录也一起回收，可以把：

```json
"CleanupArchiveDirectories": true
```

## 项目配置要求

网页里新增项目时需要填写：

- 项目名称
- SVN 工作副本路径
- `.uproject` 路径
- Unreal Engine 根目录
- 归档根目录
- `GameTarget / ClientTarget / ServerTarget`
- 允许的构建配置
- 默认额外 UAT 参数

系统会在保存项目和提交构建时检查：
- 路径是否存在
- `RunUAT.bat` 是否存在
- Target 是否存在对应 `.Target.cs`
- `svn info` 是否可执行
- 工作副本是否有冲突、锁定或损坏
- 归档目录是否可写

## 启动方式

### 开发模式

先启动后端：

```powershell
cd .\unreal-local-build
dotnet run --project .\backend\Backend.csproj
```

再启动前端：

```powershell
cd .\unreal-local-build\frontend
npm run dev
```

### 生产模式

先构建前端：

```powershell
cd .\unreal-local-build\frontend
npm run build
```

再启动后端：

```powershell
cd .\unreal-local-build
dotnet run --project .\backend\Backend.csproj --no-build --no-launch-profile
```

访问地址：
- 本机：[http://localhost:5080](http://localhost:5080)
- 局域网其它机器：`http://<打包机IP>:5080`
- 健康检查：[http://localhost:5080/api/health](http://localhost:5080/api/health)

说明：
- 如果你使用 `dotnet run` 启动服务，建议带上 `--no-launch-profile`
- 否则 [backend/Properties/launchSettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/Properties/launchSettings.json#L1) 里的开发配置可能覆盖掉实际监听地址

## 局域网访问排查

如果局域网其它机器无法访问，按下面顺序检查：

1. 确认服务进程真的已启动
2. 确认 [backend/appsettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.json#L1) 里的 `App:ServerUrl` 是 `http://0.0.0.0:5080`
3. 在打包机本机执行：

```powershell
netstat -ano | findstr :5080
```

正常情况应看到 `0.0.0.0:5080` 或对应网卡 IP 的 `LISTENING`

4. 检查本机健康接口：

```powershell
curl http://localhost:5080/api/health
curl http://<打包机IP>:5080/api/health
```

5. 放行 Windows 防火墙端口：

```powershell
netsh advfirewall firewall add rule name="Unreal Local Build 5080" dir=in action=allow protocol=TCP localport=5080
```

6. 如果仍不通，再检查：
- 打包机和访问机器是否在同一网段
- 目标端口是否被安全软件拦截
- 服务是否被注册成只在当前用户会话内启动，重连后已退出

## 使用流程

### 1. 配置项目

打开项目配置页，填写：
- SVN 工作副本
- `.uproject`
- Engine 根目录
- 归档根目录
- Target 名称

### 2. 发起构建

在构建中心选择：
- 项目
- Revision
- Target 类型
- 构建配置
- 是否 `Clean`
- 是否 `Pak`
- 是否 `IoStore`
- 额外 UAT 参数

然后点击“加入构建队列”。

### 3. 查看构建进度

构建详情页会显示：
- 当前阶段
- 估算进度
- 实时日志
- 退出码
- 错误摘要
- 下载链接

### 4. 下载产物

只有在：
- 构建状态为成功
- zip 已经真正生成完成
- 文件可读

这三个条件都满足后，前端才会显示下载按钮。

## 数据位置

默认路径：
- 数据库：`unreal-local-build/backend/AppData/unreal-build.db`
- 构建缓存：`unreal-local-build/backend/AppData/builds/`
- 前端发布目录：`unreal-local-build/backend/wwwroot/`

## 已知边界

- 仅支持 Windows 打包机
- 仅支持 SVN
- 不支持分布式 worker
- 不做自动重试
- 默认不做登录鉴权

## 故障排查

### 页面能打开但不能构建

优先检查：
- 项目路径是否真实存在
- `RunUAT.bat` 是否存在
- SVN 工作副本是否健康
- Target 名是否对应正确的 `.Target.cs`

### 构建卡在等待 UAT 槽位

看健康检查接口里的：
- `uatConcurrency`
- `automationToolCleanupEnabled`
- `automationToolCleanupMode`

默认 `TrackedOnly` 模式会在系统空闲时清理本系统残留的 UAT 进程。

### 下载按钮出现但下载失败

现在后端只有在 zip 文件真实存在且可读时才返回下载链接；如果仍失败，优先检查：
- 构建是否真正成功
- zip 是否仍在生成
- 本地磁盘空间是否不足
