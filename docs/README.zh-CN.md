# AFEmulator

[English](../README.md) | 简体中文 | [繁體中文](README.zh-TW.md)

面向菲律宾 2014 版《Assault Fire》客户端的 Windows C# 服务端模拟器与连接客户端。
解决方案包含 Server 和 Connect 的控制台程序、Avalonia 桌面界面，以及共享传输库。

对局通过 `Afdev` 后端接入原游戏的原生专用服务器（DS）引擎，所需游戏资源须另行提供。
Server 负责启动器协议、账号、玩家数据、大厅、商城和对局分配；Connect 负责连接认证、
通过双向 TLS 转发通信、准备本地资源并启动玩家客户端。

项目已结束开发。本仓库按现状公开，后续不再开发或更新。

## 已实现功能

- 商城新增物品。
- 修复仓库的 1 小时时长显示问题及道具单位标签显示错误。
- 修复背包切换缓慢及相关卡顿问题。
- 支持成就、等级和玩家名片。
- 所有 PVE 模式均可使用复活币重开回合。
- 修复符合装备规则的道具无法同时装备的问题。
- 修复对局结算问题；部分地图仍有结算异常。

| 模式 | 支持内容 |
| --- | --- |
| PVP | 团队战、爆破战、个人战的全部地图，支持人机；爆破人机仍有已知问题。 |
| PVE | 全部地图、全部难度。 |
| XPVP | 普通变异战的全部地图，支持人机。 |

## 环境要求

- Windows x64 和 **Visual Studio 2026**。
- **.NET 10 SDK**、**.NET 桌面开发**工作负载，以及用于 Native AOT 的
  **使用 C++ 的桌面开发**工作负载和 Windows SDK。
- 还原依赖时能够访问 NuGet。
- 另行提供兼容的 AF2014 客户端和两份经过验证的 AFDEV 可执行文件。
  本仓库不包含游戏文件或 TCLS。
- 实际运行服务端还需要 MongoDB，以及本地部署所需的认证密钥和 TLS 证书。

## 使用 Visual Studio 编译

1. 在 Visual Studio 2026 中打开 **`AFEmulator.slnx`**。
2. 还原 NuGet 包，选择 **Release / Any CPU**，然后生成解决方案。
3. 右键各可执行程序项目，选择 **Publish（发布）**，使用已有的
   **Windows-x64** 文件夹发布配置。四个可执行程序项目均需发布。

| 项目 | 发布的可执行文件 | 输出目录 |
| --- | --- | --- |
| `AFServer/AFServer` | `AFServer.exe` | `Running/Server` |
| `AFServer/AFServer.Avalonia` | `AFServer.Avalonia.exe` | `Running/Server` |
| `AFConnect/AFConnect.Edge` | `AFConnect.exe` | `Running/Connect` |
| `AFConnect/AFConnect.Launcher` | `AFConnect.GUI.exe` | `Running/Connect` |

发布配置会生成自包含的 Windows x64 Native AOT 程序。直接运行生成的 EXE 即可，
无需 Python、批处理或 PowerShell 运行脚本。同一端的桌面版和控制台版共用一份部署配置，
不要同时启动它们来访问同一套资源。

也可以在仓库根目录打开 **Developer PowerShell for VS 2026**，直接运行以下命令：

```powershell
MSBuild.exe AFEmulator.slnx -restore -t:Build -p:Configuration=Release
MSBuild.exe AFServer/AFServer/AFServer.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFServer/AFServer.Avalonia/AFServer.Avalonia.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFConnect/AFConnect.Edge/AFConnect.Edge.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFConnect/AFConnect.Launcher/AFConnect.Launcher.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
```

发布时会保留已有的 `appsettings.json` 和 `config.json`。替换 EXE 或原生库前，
请先关闭对应程序。Server 的 Native AOT 发布仍会显示已有的 MongoDB 裁剪及 AOT 分析警告。

## 配置与运行

本地仅维护一套共享客户端资源，目录结构如下：

```text
Running/
  Server/
    AFServer.exe
    AFServer.Avalonia.exe
    appsettings.json
    data/
    secrets/                 # 本地密钥和证书
    database/                # 可选的本地 MongoDB 程序及数据库数据
  Connect/
    AFConnect.exe
    AFConnect.GUI.exe
    config.json
    secrets/                 # 本地公钥和客户端证书
  AF_2014/
    Binaries/Win32/
      TGame.exe
      TGame_AFDEV.exe
      TGame_AFDEV2.exe
    TGame/
```

两份 AFDEV EXE 必须使用同一份经过验证的原生构建。两个 DS 槽和 Connect 均直接使用这套
共享目录。不要为不同运行角色复制客户端，也不要建立符号链接、目录联接或硬链接。

连接前，先编辑 `Running/Server/appsettings.json` 和 `Running/Connect/config.json`。
仓库中的配置只是模板：`YOUR_SERVER_IP`、证书指纹、认证密钥和账号数据都需要在本地配置。
默认组合为 `MatchBackend: Afdev`、`TunnelVersion: 2` 和 `MatchTransport: Afdev`。
配置中的文件路径均相对于各自 EXE 所在目录解析。

### 数据库与账号

如果使用已运行的 MongoDB 服务，请在 Server 配置中设置 `MongoConnectionString` 和
`MongoDatabase`，并关闭 `MongoLocalProcess.AutoStart`。也可以在 Server 部署目录内提供
`database/bin/mongod.exe`，保留自动启动设置，将数据库数据存放在 `database/data`。
源码不包含 MongoDB 可执行文件或账号数据库。

创建账号前，先启动 MongoDB，再运行：

```powershell
& './Running/Server/AFServer.exe' --create-account player1
```

将 `player1` 替换为所需账号名。命令会交互式读取密码并返回 UIN。
将 Server 的 `ActiveUin` 设置为要使用的已有账号 UIN。启动器认证流程会选择该账号；
在界面中输入名称并不构成单独的账号密码登录。

### 连接与认证

| 配置项 | 设置说明 |
| --- | --- |
| Server 的 `RemoteConnect.Port` 与 Connect 的 `ServerPort` | 使用相同的 TLS 端口，默认为 9443。 |
| Connect 的 `ServerHost` | 将 `YOUR_SERVER_IP` 替换为可访问的 Server 主机地址。 |
| Server 的 `RemoteConnect.ListenAddress` | 默认监听回环地址。异机部署时，绑定所需网络接口，并在防火墙中放行 TLS 端口。 |
| ROLE 和 ZONE 端口 | Connect 的 `LocalRolePort` / `LocalZonePort` 必须与 Server 的 `RemoteConnect` 配置一致，默认值为 65005 / 65006。 |
| Server 的 `MatchBackend` | 原生 DS 对局使用 `Afdev`。 |
| Connect 的 `TunnelVersion` / `MatchTransport` | 配合默认 Server 后端时，使用 `2` / `Afdev`。 |

Connect GUI 的工具窗口可生成配对的 TLS 证书，并更新两端配置。
操作前先停止 Server 并断开游戏连接；Server 目录填写 `../Server`，Connect 目录填写 `.`。
工具会备份原有配置和证书。生成的 PFX 文件包含私钥，密码为空。
已有的密码保护 PFX 默认从环境变量读取密码：Server 使用 `AF_CONNECT_CERT_PASSWORD`，
Connect 使用 `AF_EDGE_CERT_PASSWORD`，也可以在配置中指定其他变量。

手动配置 TLS 时，设置 Server 的 `RemoteConnect.ServerCertificatePath` 和
`AllowedClientCertificateSha256`；然后在 Connect 中设置 `ClientCertificatePath` 和
`ServerCertificateSha256`，对应配对的客户端 PFX 和服务端证书指纹。

游戏认证密钥与 TLS 证书用途不同。请在 Server 的 `PrivateKeyPath`（默认
`secrets/PRIVATE.PEM`）提供匹配的 1024 位 RSA 私钥，并在 Connect 的
`LauncherPublicKeyPath`（默认 `secrets/APClient.dat`）提供对应的 PEM 公钥。
玩家端可执行文件也必须包含对应的受支持公钥。将 Server 的
`RemoteConnect.ExpectedApClientSha256` 和 Connect 的 `GamePublicKeySha256`
设置为该游戏公钥指纹。指纹按 SubjectPublicKeyInfo PEM 文本计算，使用 LF 换行，
并在末尾保留一个换行符。TLS 证书配对工具不会生成或替换游戏认证密钥。

Connect 还会使用本地启动器端口 8000、9010 和 9060。工具窗口可以将 `tversion`、
`tauthproxy` 和 `tdir.levelupgames.ph` 映射至回环地址，备份 hosts 文件并刷新 DNS。
Server 配置中的原生对局端口范围也必须保持可用。

### 启动程序

运行 `AFServer.Avalonia.exe` 后，桌面窗口会自动尝试启动 Server。
待 Server 就绪后，再打开 `AFConnect.GUI.exe`。控制台用户可运行 `AFServer.exe`
和 `AFConnect.exe`。同一端只选择一种启动方式。

Connect 和 Server GUI 的现有清单要求管理员权限，Windows 正常启动时可能显示 UAC 提示。
Server 控制台使用当前用户权限；原生进程访问需要两端采用相容的权限。

### 诊断

在仓库根目录直接运行以下命令：

```powershell
& './Running/Server/AFServer.exe' --self-test --no-pause
& './Running/Server/AFServer.exe' --check --no-pause
& './Running/Connect/AFConnect.exe' --check
```

协议自检不需要客户端或数据库。Server 的 `--check` 需要已配置的运行环境和已启动的
MongoDB 实例。Connect 的 `--check` 需要已部署的客户端和 Server，用于检查连接、
认证及服务器目录。可通过 `AFConnect.exe --prepare-shop` 准备客户端商城资源；
实际更新前，先停止正在使用共享资源的进程。

如果启动失败，请检查 MongoDB 是否可用、客户端文件是否缺失、密钥和证书指纹是否配对，
以及端口是否被占用。日志位于 EXE 旁的 `logs/`，Server GUI 还会在 `state/` 保存本地状态。

## 仓库结构

| 路径 | 用途 |
| --- | --- |
| `AFEmulator.slnx` | 包含全部六个项目的 Visual Studio 解决方案 |
| `AFServer/` | 服务端、桌面界面、协议、持久化和对局管理 |
| `AFConnect/` | 连接引擎、桌面启动器和共享代理库 |
| `develop/` | 已忽略的本地研究、诊断和 C# 测试；测试位于 `develop/test/` |
| `Running/` | 已忽略的本地 EXE 部署目录及共享游戏资源 |

## 许可证

本项目源码采用 [MIT 许可证](../LICENSE)。第三方依赖保留各自的许可证。
该许可证不授予《Assault Fire》、TCLS、专有游戏可执行文件或客户端资源的使用权；
请另行获取，并遵守其适用条款。
