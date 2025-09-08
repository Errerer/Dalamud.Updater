# Dalamud.Updater API 更新说明

## 更新概览
已成功将 Dalamud.Updater 项目的更新机制从自定义服务器迁移到 GitHub API 和微软官方源。

## 主要更改

### 1. API 端点变更
- **旧版本信息 API**: `https://aonyx.ffxiv.wang/Dalamud/Release/VersionInfo`
- **新版本信息 API**: GitHub API + gh.atmoomen.top 代理
  - 版本检查: `https://gh.atmoomen.top/https://raw.githubusercontent.com/Dalamud-DailyRoutines/ghapi-json-generator/output/v2/repos/AtmoOmen/Dalamud/releases/latest/data.json`
  - 下载源: `https://api.github.com/repos/AtmoOmen/Dalamud/releases/latest`

### 2. 运行时下载源
- **旧运行时源**: `https://aonyx.ffxiv.wang/Dalamud/Release/Runtime/`
- **新运行时源**: 微软官方 Azure CDN
  - .NET Runtime: `https://dotnetcli.azureedge.net/dotnet/Runtime/{version}/dotnet-runtime-{version}-win-x64.zip`
  - Windows Desktop Runtime: `https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-x64.zip`

### 3. 版本管理
- **运行时版本**: 固定为 9.0.2（之前是动态获取）
- **版本信息结构**: 从 `DalamudVersionInfo` 类迁移到直接使用 `System.Text.Json` 解析 GitHub API 响应

### 4. 新增文件
- `PlatformHelpers.cs`: 提供平台相关的辅助方法（解压、文件操作等）

### 5. 依赖更新
- 添加 `System.Text.Json` v8.0.5 包用于解析 GitHub API 响应

### 6. 功能改进
- 支持 GitHub Token 认证（可选）
- 支持代理模式切换（forceProxy 参数）
- 更稳定的错误处理和重试机制（最多 10 次重试）

### 7. 代码简化
- 移除了 staging/beta 版本的支持逻辑
- 移除了 `OnUpdateEvent` 事件（简化状态管理）
- 移除了 `DalamudVersionInfo` 类的依赖

## 编译状态
✅ 项目已成功编译，仅存在一些非关键性警告。

## 注意事项
1. 新版本使用 GitHub 作为主要源，确保网络能访问 GitHub
2. 运行时版本固定为 9.0.2，如需更新请修改 `RuntimeVersion` 常量
3. 资源文件下载逻辑保持不变，仍使用原有的镜像源