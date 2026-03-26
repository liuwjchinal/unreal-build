# Unreal Local Build

轻量级 Unreal Windows 本地打包 Web 工具。适用于单台 Windows 打包机，本机和局域网其它电脑都可以通过浏览器访问；底层通过 `svn update` 和 Unreal `RunUAT.bat BuildCookRun` 完成出包。

## 目录结构

- `backend/`
  - ASP.NET Core Minimal API
  - SQLite 数据库存储项目配置与构建记录
  - 后台调度器、SSE、日志读取、产物下载
- `frontend/`
  - Vite + React + TypeScript 前端
  - 项目配置页、构建中心页、构建详情页
- `UnrealBuildWeb.sln`
  - Visual Studio 解决方案

## 功能概览

- 多项目配置管理
- 多项目并发构建，同项目默认串行
- 支持 `Game / Client / Server`
- 支持 `Development / Shipping`
- 支持 `Clean / Pak / IoStore / 额外 UAT 参数`
- 构建队列、构建列表、构建详情
- 实时日志、构建阶段、估算进度、构建时长
- 错误摘要、zip 下载链接
- 项目配置 JSON 导入导出

## 运行环境

建议环境：

- Windows 10 / 11
- .NET SDK 10
- Node.js 20 或更高版本
- SVN 命令行工具可用，`svn` 已加入 `PATH`
- 已安装 Unreal Engine，且目标引擎目录下存在 `Engine\\Build\\BatchFiles\\RunUAT.bat`
- 打包机上已能正常使用当前 SVN 工作副本和 Unreal 命令行打包

可选但建议：

- Visual Studio 2022 或 Rider，用于调试后端
- TortoiseSVN 或已缓存 SVN 凭据的命令行环境

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

### 3. 检查运行目录

后端第一次启动会自动创建：

- `backend/AppData/`
- `backend/AppData/unreal-build.db`
- `backend/AppData/builds/`

这些目录默认用于保存数据库、日志和临时构建产物。

## 配置说明

主配置文件：

- [backend/appsettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.json#L1)
- [backend/appsettings.Development.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.Development.json#L1)

常用配置项：

- `App:ServerUrl`
  - 后端监听地址
  - 默认 `http://0.0.0.0:5080`
- `App:StorageRoot`
  - 数据库、日志和构建中间文件根目录
  - 默认 `AppData`
- `App:GlobalConcurrency`
  - 全局并发构建上限
  - 默认 `2`
- `App:UatConcurrency`
  - 真正进入 `RunUAT` 阶段的并发上限
  - 默认 `1`
- `App:AutomationToolCleanupEnabled`
  - 是否启用空闲态 AutomationTool 清理
  - 默认 `true`
- `App:AutomationToolCleanupMode`
  - `TrackedOnly`：只清理由本系统启动并追踪过的 UAT 根进程，默认值，较安全
  - `AnyWhenIdle`：当系统确认没有运行中构建时，清理整机所有 `RunUAT.bat` / `AutomationTool.dll` 进程，风险更高
- `App:DefaultLogTailLines`
  - 构建详情页首次加载的日志尾部行数
- `App:MaxLiveLogLines`
  - 前端实时日志最大保留行数
- `App:EventChannelCapacity`
  - 每个 SSE 订阅者的事件缓冲容量
- `App:EventHeartbeatSeconds`
  - SSE 心跳间隔
- `App:BuildRetentionDays`
  - 日志和构建产物保留天数
- `App:CleanupIntervalMinutes`
  - 清理任务执行间隔
- `App:FrontendDevOrigin`
  - 前端开发模式允许访问后端的来源地址

## AutomationTool 清理策略

系统现在会在以下时机检查并清理遗留 UAT 进程：

- 服务启动恢复后
- 新建构建任务前
- 构建完成后且系统空闲时

默认模式是 `TrackedOnly`：

- 后端在启动 `RunUAT.bat` 时，会把该构建的根进程 PID 写入 [backend/AppData/builds](/D:/UnrealGit/unreal-build/unreal-local-build/backend/AppData/builds) 下对应构建目录的 `uat-process.json`
- 当数据库里没有运行中的构建时，只会尝试清理这些由本系统启动并仍然残留的 UAT 根进程
- 这样可以避免误伤你手动开的其它 Unreal/AutomationTool 任务

如果你确认这台机器只给本系统使用，也可以改成：

```json
"AutomationToolCleanupMode": "AnyWhenIdle"
```

这个模式会在系统空闲时直接清理整机所有 `RunUAT.bat` / `AutomationTool.dll` 进程。

## Unreal 和 SVN 配置要求

在网页里新增项目时，需要填写：

- 项目名称
- SVN 工作副本路径
- `.uproject` 路径
- Unreal Engine 根目录
- 归档根目录
- `GameTarget / ClientTarget / ServerTarget`
- 允许的构建配置
- 默认额外 UAT 参数

系统会在保存和提交构建时检查：

- 路径是否存在
- `RunUAT.bat` 是否存在
- Target 名称是否合法
- 对应 `.Target.cs` 是否存在
- SVN 工作副本能否通过 `svn info`
- 提交构建时是否存在 SVN 冲突、锁定或损坏状态
- 归档目录是否可写

## 启动方式

### 开发模式

1. 启动后端

```powershell
cd .\unreal-local-build
dotnet run --project .\backend\Backend.csproj
```

2. 启动前端

```powershell
cd .\unreal-local-build\frontend
npm run dev
```

默认访问地址：

- 前端开发页：[http://localhost:5173](http://localhost:5173)
- 后端 API：[http://localhost:5080](http://localhost:5080)

### 生产模式

1. 构建前端静态资源

```powershell
cd .\unreal-local-build\frontend
npm run build
```

产物输出到 [backend/wwwroot](/D:/UnrealGit/unreal-build/unreal-local-build/backend/wwwroot)。

2. 启动后端

```powershell
cd .\unreal-local-build
dotnet run --project .\backend\Backend.csproj --no-build
```

浏览器访问：

- 本机：[http://localhost:5080](http://localhost:5080)
- 局域网其它电脑：`http://<打包机IP>:5080`

## 局域网访问设置

如果局域网其它电脑无法访问，优先检查：

1. [backend/appsettings.json](/D:/UnrealGit/unreal-build/unreal-local-build/backend/appsettings.json#L1) 中 `App:ServerUrl` 是否仍为 `http://0.0.0.0:5080`
2. Windows 防火墙是否放行 `5080` 端口
3. 打包机和访问端是否在同一网段
4. 目标浏览器是否能访问 [http://<打包机IP>:5080/api/health](http://localhost:5080/api/health)

手动放行端口：

```powershell
netsh advfirewall firewall add rule name="Unreal Local Build 5080" dir=in action=allow protocol=TCP localport=5080
```

## 使用流程

### 1. 新增项目

打开项目配置页，填写：

- SVN 工作副本
- `.uproject`
- Engine 根目录
- 归档根目录
- Target 名称

### 2. 发起构建

在构建中心选择：

- 项目
- Revision，例如 `HEAD` 或指定版本号
- Target 类型
- 构建配置
- 是否 `Clean`
- 是否 `Pak`
- 是否 `IoStore`
- 额外 UAT 参数

点击“加入构建队列”。

### 3. 查看构建进度

构建详情页可以看到：

- 当前阶段
- 估算进度百分比
- 实时日志
- 退出码
- 错误摘要
- 下载链接

### 4. 下载产物

构建成功后会自动压缩为 zip，通过构建详情页或构建列表中的下载按钮下载。

## 数据与日志位置

默认路径：

- 数据库：`unreal-local-build/backend/AppData/unreal-build.db`
- 构建目录：`unreal-local-build/backend/AppData/builds/`
- 前端发布目录：`unreal-local-build/backend/wwwroot/`

## 已知边界

- 仅支持 Windows 打包机
- 仅支持 SVN，不支持 Git / Perforce
- 不做分布式 worker
- 不做自动重试
- 默认不做登录鉴权
- 当前迁移可运行，但 EF Core 的 `ModelSnapshot` 仍待补齐

## 故障排查

### 页面能打开但无法构建

优先检查：

- 项目路径是否真实存在
- `RunUAT.bat` 是否存在
- SVN 工作副本是否有冲突或锁
- Target 名称是否对应正确的 `.Target.cs`

### 构建卡在等待 UAT 槽位

先看 [http://localhost:5080/api/health](http://localhost:5080/api/health) 返回的 `uatConcurrency` 和清理配置。

如果机器上之前残留过异常退出的 `RunUAT` / `AutomationTool` 进程：

- 默认 `TrackedOnly` 模式会在系统空闲时自动清理由本系统启动过的残留 UAT 根进程
- 如果这台机器只给本系统用，也可以切到 `AnyWhenIdle`

### 点击下载产物返回 404

通常表示该构建并没有成功生成 zip。现在后端只有在产物文件真实存在时才会返回 `downloadUrl`。
