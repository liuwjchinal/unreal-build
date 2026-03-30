# Unreal Local Build

轻量级 Unreal 本地打包 Web 工具，基于 `ASP.NET Core Minimal API + BackgroundService + EF Core + SQLite + React + Vite + TypeScript`。

当前版本支持：
- Windows 打包
- Android 打包第一版：`ASTC APK`、测试包、`Game` only
- 手动构建
- 定时构建
- 构建队列、实时日志、下载产物、缓存清理

相关专题文档：
- [定时构建说明](./docs/build-schedules.md)
- [Unreal UAT 源码流程说明](./docs/unreal-engine-uat-build-flow.md)

## 目录结构

- [backend](./backend)
  - ASP.NET Core Minimal API
  - SQLite 数据库存储项目配置、构建记录、定时任务
  - 后台调度、日志读取、SSE、下载接口、缓存清理
- [frontend](./frontend)
  - Vite + React + TypeScript
  - 构建中心、项目配置、定时任务页面
- [UnrealBuildWeb.sln](./UnrealBuildWeb.sln)
  - Visual Studio 解决方案

## 当前能力边界

### Windows

- 支持 `Game / Client / Server`
- 支持 `Development / Shipping`
- 支持 `Clean / Pak / IoStore / 额外 UAT 参数`

### Android 第一版

- 只支持 `Game`
- 只支持 `ASTC`
- 只做测试包，不做正式签名发布
- 继续复用现有构建队列、日志、下载、定时任务和缓存清理

### 同一项目跨平台策略

- 同一项目的 Windows / Android 构建继续串行
- 不拆独立工作副本
- 不允许同一项目 Windows 和 Android 并发构建
- 来回切平台时构建时间通常会增加，这是正常现象

## 运行环境

- Windows 10 / 11
- .NET SDK 10
- Node.js 20 或更高版本
- `svn` 已加入 `PATH`
- 已安装 Unreal Engine，且存在 `Engine\Build\BatchFiles\RunUAT.bat`

Android 第一版额外要求：
- 已安装 Android SDK
- 已安装 Android NDK
- 已配置 `JAVA_HOME`
- 已接受 Android SDK License
- 推荐环境变量：
  - `ANDROID_SDK_ROOT`
  - `ANDROID_HOME`
  - `ANDROID_NDK_ROOT` 或 `NDKROOT`
  - `JAVA_HOME`

## 重要升级说明

这次 Android 平台改造把“平台”升级成了系统的一等字段。

按当前设计，**旧数据不做兼容**。升级新版本前，请先手动删除旧 SQLite 数据库，再重新录入项目配置和定时任务。

默认数据库路径：
- [backend/AppData/unreal-build.db](./backend/AppData/unreal-build.db)

升级步骤：
1. 停止当前服务。
2. 删除旧数据库文件 [backend/AppData/unreal-build.db](./backend/AppData/unreal-build.db)。
3. 启动新版本后端服务。
4. 重新录入项目配置。
5. 重新录入定时任务。

## 首次安装

### 1. 安装前端依赖

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

### 3. 构建前端静态资源

```powershell
cd ..\frontend
npm run build
```

### 4. 首次启动后自动创建的数据目录

后端首次启动后会自动创建：
- [backend/AppData](./backend/AppData)
- [backend/AppData/builds](./backend/AppData/builds)

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

也可以直接执行：

```powershell
cd .\unreal-local-build
.\start-dev.bat
```

相关脚本：
- [start-dev.bat](./start-dev.bat)
- [start-prod.bat](./start-prod.bat)

### 生产模式

```powershell
cd .\unreal-local-build
.\start-prod.bat
```

或手动启动：

```powershell
cd .\unreal-local-build\frontend
npm run build

cd ..\
dotnet run --project .\backend\Backend.csproj --no-build --no-launch-profile
```

默认访问地址：
- 本机：[http://localhost:5080](http://localhost:5080)
- 局域网：[http://<打包机IP>:5080](http://localhost:5080)
- 健康检查：[http://localhost:5080/api/health](http://localhost:5080/api/health)

说明：
- `5173` 是前端开发服务器，仅用于开发调试
- 正式局域网访问统一走 `5080`

## 关键配置

主配置文件：
- [backend/appsettings.json](./backend/appsettings.json)
- [backend/appsettings.Development.json](./backend/appsettings.Development.json)

常用字段：
- `App:ServerUrl`
  - 后端监听地址
  - 默认：`http://0.0.0.0:5080`
- `App:StorageRoot`
  - 本地数据库、日志、构建缓存根目录
  - 默认：`AppData`
- `App:GlobalConcurrency`
  - 全局可同时运行的构建任务上限
- `App:UatConcurrency`
  - 真正进入 `RunUAT` 的并发上限
  - 默认：`1`
- `App:ScheduleServiceEnabled`
  - 是否启用定时构建服务
- `App:ScheduleScanIntervalSeconds`
  - 定时构建轮询间隔
  - 默认：`30`
- `App:BuildRetentionDays`
  - 本地构建缓存保留天数
- `App:KeepRecentSuccessfulBuildsPerProject`
  - 每个项目默认保留最近多少个成功构建缓存
- `App:MaxBuildCacheSizeGb`
  - [backend/AppData/builds](./backend/AppData/builds) 总缓存体积上限

## 项目配置字段说明

项目配置页中主要字段含义如下：

- `项目名称`
  - 页面展示名、日志标识、产物命名的一部分
- `SVN 工作副本路径`
  - 本地 SVN checkout 根目录
  - 构建前会执行 `svn update`
- `.uproject 路径`
  - 需要打包的 Unreal 项目文件
- `Engine 根目录`
  - Unreal Engine 根目录
  - 系统会自动定位 `RunUAT.bat`
- `归档根目录`
  - Unreal `-archive` 的输出根目录
- `GameTarget / ClientTarget / ServerTarget`
  - 页面选择不同 Target 类型时最终传给 UAT 的 `-target=`
- `允许的构建配置`
  - 例如 `Development`、`Shipping`
- `默认额外 UAT 参数`
  - 当前项目默认附加的 UAT 参数
- `启用 Android 构建`
  - 是否允许该项目构建 Android
- `Android Texture Flavor`
  - Android 第一版固定为 `ASTC`

## 构建任务字段说明

发起构建时主要字段：

- `项目`
  - 选中的项目配置
- `Revision`
  - `HEAD` 或指定 SVN 版本号
- `平台`
  - `Windows` 或 `Android`
- `Target 类型`
  - Windows：`Game / Client / Server`
  - Android：只允许 `Game`
- `构建配置`
  - 例如 `Development / Shipping`
- `Clean`
  - 是否附加 `-clean`
- `Pak`
  - 是否附加 `-pak`
- `IoStore`
  - 是否附加 `-iostore`
- `额外 UAT 参数`
  - 本次构建额外附加的参数

构建历史记录会额外保存：
- `平台`
- `触发来源`
  - `Manual` 或 `Schedule`
- `定时任务 ID`
  - 若为定时触发则记录来源任务

## 平台支持规则

### Windows

- `Target 类型`：`Game / Client / Server`
- `TargetPlatform`：`Win64`

### Android

- `Target 类型`：`Game`
- `TargetPlatform`：`Android`
- `CookFlavor`：`ASTC`

## 下载命名规则

产物 zip 名称包含平台字段，命名格式为：

`构建日期-项目名称-平台-构建配置-Target类型-svn版本号.zip`

示例：
- `20260327-153000-LyraStarterGame-Windows-Development-Game-r12554.zip`
- `20260327-153500-LyraStarterGame-Android-Development-Game-r12554.zip`

Windows 和 Android 的归档目录名也会包含平台字段，避免人工查看时混淆。

## 定时构建

当前支持：
- 每日固定时间
- 单项目定时构建
- 全项目定时构建
- Windows / Android 都可配置定时任务

时间语义：
- 使用部署机本地时间

更多说明见：
- [docs/build-schedules.md](./docs/build-schedules.md)

## 数据位置

默认路径：
- 数据库：[backend/AppData/unreal-build.db](./backend/AppData/unreal-build.db)
- 构建缓存：[backend/AppData/builds](./backend/AppData/builds)
- 前端静态资源：[backend/wwwroot](./backend/wwwroot)

## 本地构建缓存清理策略

默认策略：
1. 超过 `14` 天的构建缓存清理
2. 每个项目保留最近 `3` 个成功构建缓存
3. [backend/AppData/builds](./backend/AppData/builds) 总大小超过 `20 GB` 时按最旧记录回收

默认只清理：
- 构建日志
- 下载 zip
- 构建临时目录

默认不清理：
- 项目配置中的外部归档根目录

## 常见问题

### 1. 局域网访问提示“不安全”

如果访问的是 `http://<IP>:5173`，这是 Vite 开发服务器，浏览器会标记为不安全。

正式局域网访问统一使用：
- [http://localhost:5080](http://localhost:5080)

### 2. 同一项目 Windows 和 Android 来回切换会不会变慢？

会。

原因：
- 平台二进制输出不同
- Cook 数据按平台分开
- Android 还会增加 SDK / Gradle / 打包链路

但当前系统对同一项目仍是串行执行，所以不会因为并发互踩导致结果错误，只会让总体构建时间变长。

### 3. Android 构建前会检查什么？

Android 第一版会在入队前检查：
- 项目是否启用 Android 构建
- 是否只选择了 `Game`
- 是否存在 AndroidRuntimeSettings
- Android SDK / NDK / JDK 是否可用
- Android SDK License 是否存在

### 4. 下载产物是否支持断点续传？

支持。

后端下载接口已开启 `HTTP Range`，正式下载请走 `5080`，不要走 `5173` 的开发代理。

## GitHub 协作

仓库已包含：
- CI：[.github/workflows/ci.yml](../.github/workflows/ci.yml)
- PR 模板：[.github/PULL_REQUEST_TEMPLATE.md](../.github/PULL_REQUEST_TEMPLATE.md)
- Issue 模板：[.github/ISSUE_TEMPLATE](../.github/ISSUE_TEMPLATE)

如果希望在 PR 中触发代码审查，可在 PR 描述或评论中写：

`@codex review`

## 旧版项目配置 JSON 导入兼容

- 旧版导出的项目配置 JSON 即使没有 `androidEnabled` 和 `androidTextureFlavor` 字段，当前版本也可以直接导入。
- 导入时会自动补默认值：
  - `androidEnabled = true`
  - `androidTextureFlavor = ASTC`
- 这项兼容只针对“项目配置导入文件”。
- 数据库升级策略仍然保持不变：升级 Android 版本时，继续按本文前面的说明手动删库重建。

相关字段说明文档：

- [构建字段说明](./docs/build-field-reference.md)
