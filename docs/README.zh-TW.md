# AFEmulator

[English](../README.md) | [简体中文](README.zh-CN.md) | 繁體中文

適用於菲律賓 2014 版《Assault Fire》用戶端的 Windows C# 伺服器模擬器與連線用戶端。
方案包含 Server 與 Connect 的主控台程式、Avalonia 桌面介面，以及共用傳輸程式庫。

對局透過 `Afdev` 後端接入原遊戲的原生專用伺服器（DS）引擎，所需遊戲資源須另外提供。
Server 負責啟動器通訊協定、帳號、玩家資料、大廳、商城與對局分配；Connect 負責連線驗證、
透過雙向 TLS 轉送通訊、準備本機資源並啟動玩家用戶端。

專案已結束開發。本儲存庫依現狀公開，後續不再開發或更新。

## 已實作功能

- 商城新增物品。
- 修正倉庫的 1 小時期限顯示問題，以及道具單位標示錯誤。
- 修正背包切換緩慢及相關卡頓問題。
- 支援成就、等級與玩家名片。
- 所有 PVE 模式皆可使用復活幣重新開始回合。
- 修正符合裝備規則的道具無法同時裝備的問題。
- 修正對局結算問題；部分地圖仍有結算異常。

| 模式 | 支援內容 |
| --- | --- |
| PVP | 團隊戰、爆破戰、個人戰的全部地圖，支援人機；爆破人機仍有已知問題。 |
| PVE | 全部地圖、全部難度。 |
| XPVP | 普通變異戰的全部地圖，支援人機。 |

## 環境需求

- Windows x64 與 **Visual Studio 2026**。
- **.NET 10 SDK**、**.NET 桌面開發**工作負載，以及 Native AOT 所需的
  **使用 C++ 的桌面開發**工作負載與 Windows SDK。
- 還原相依套件時能夠存取 NuGet。
- 另外提供相容的 AF2014 用戶端及兩份經過驗證的 AFDEV 執行檔。
  本儲存庫不包含遊戲檔案或 TCLS。
- 實際執行伺服器還需要 MongoDB，以及本機部署所需的驗證金鑰與 TLS 憑證。

## 使用 Visual Studio 編譯

1. 在 Visual Studio 2026 中開啟 **`AFEmulator.slnx`**。
2. 還原 NuGet 套件，選擇 **Release / Any CPU**，再建置方案。
3. 在各執行檔專案上按右鍵，選擇 **Publish（發佈）**，使用現有的
   **Windows-x64** 資料夾發佈設定。四個執行檔專案皆須發佈。

| 專案 | 發佈的執行檔 | 輸出目錄 |
| --- | --- | --- |
| `AFServer/AFServer` | `AFServer.exe` | `Running/Server` |
| `AFServer/AFServer.Avalonia` | `AFServer.Avalonia.exe` | `Running/Server` |
| `AFConnect/AFConnect.Edge` | `AFConnect.exe` | `Running/Connect` |
| `AFConnect/AFConnect.Launcher` | `AFConnect.GUI.exe` | `Running/Connect` |

發佈設定會產生自包含的 Windows x64 Native AOT 程式。直接執行產生的 EXE 即可，
不需要 Python、批次檔或 PowerShell 執行指令碼。同一端的桌面版與主控台版共用一份部署設定，
請勿同時啟動它們來存取同一套資源。

也可以在儲存庫根目錄開啟 **Developer PowerShell for VS 2026**，直接執行以下命令：

```powershell
MSBuild.exe AFEmulator.slnx -restore -t:Build -p:Configuration=Release
MSBuild.exe AFServer/AFServer/AFServer.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFServer/AFServer.Avalonia/AFServer.Avalonia.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFConnect/AFConnect.Edge/AFConnect.Edge.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFConnect/AFConnect.Launcher/AFConnect.Launcher.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
```

發佈時會保留現有的 `appsettings.json` 與 `config.json`。替換 EXE 或原生程式庫前，
請先關閉對應程式。Server 的 Native AOT 發佈仍會顯示既有的 MongoDB 裁剪與 AOT 分析警告。

## 設定與執行

本機僅維護一套共用用戶端資源，目錄結構如下：

```text
Running/
  Server/
    AFServer.exe
    AFServer.Avalonia.exe
    appsettings.json
    data/
    secrets/                 # 本機金鑰與憑證
    database/                # 選用的本機 MongoDB 程式與資料庫資料
  Connect/
    AFConnect.exe
    AFConnect.GUI.exe
    config.json
    secrets/                 # 本機公開金鑰與用戶端憑證
  AF_2014/
    Binaries/Win32/
      TGame.exe
      TGame_AFDEV.exe
      TGame_AFDEV2.exe
    TGame/
```

兩份 AFDEV EXE 必須使用同一份經過驗證的原生組建。兩個 DS 槽位與 Connect 皆直接使用
這套共用目錄。請勿為不同執行角色複製用戶端，也不要建立符號連結、目錄接合或硬連結。

連線前，先編輯 `Running/Server/appsettings.json` 與 `Running/Connect/config.json`。
儲存庫中的設定只是範本：`YOUR_SERVER_IP`、憑證指紋、驗證金鑰與帳號資料都須在本機設定。
預設組合為 `MatchBackend: Afdev`、`TunnelVersion: 2` 與 `MatchTransport: Afdev`。
設定中的檔案路徑皆以各自 EXE 所在目錄為基準解析。

### 資料庫與帳號

若使用已執行的 MongoDB 服務，請在 Server 設定中指定 `MongoConnectionString` 與
`MongoDatabase`，並停用 `MongoLocalProcess.AutoStart`。也可以在 Server 部署目錄內提供
`database/bin/mongod.exe`，保留自動啟動設定，將資料庫資料存放在 `database/data`。
原始碼不包含 MongoDB 執行檔或帳號資料庫。

建立帳號前，先啟動 MongoDB，再執行：

```powershell
& './Running/Server/AFServer.exe' --create-account player1
```

將 `player1` 替換為所需帳號名稱。命令會以互動方式讀取密碼並傳回 UIN。
將 Server 的 `ActiveUin` 設為要使用的既有帳號 UIN。啟動器驗證流程會選擇該帳號；
在介面中輸入名稱並不構成獨立的帳號密碼登入。

### 連線與驗證

| 設定項目 | 設定說明 |
| --- | --- |
| Server 的 `RemoteConnect.Port` 與 Connect 的 `ServerPort` | 使用相同的 TLS 連接埠，預設為 9443。 |
| Connect 的 `ServerHost` | 將 `YOUR_SERVER_IP` 替換為可連線的 Server 主機位址。 |
| Server 的 `RemoteConnect.ListenAddress` | 預設接聽回送位址。跨機器部署時，繫結所需網路介面，並在防火牆中允許 TLS 連接埠。 |
| ROLE 與 ZONE 連接埠 | Connect 的 `LocalRolePort` / `LocalZonePort` 必須與 Server 的 `RemoteConnect` 設定一致，預設值為 65005 / 65006。 |
| Server 的 `MatchBackend` | 原生 DS 對局使用 `Afdev`。 |
| Connect 的 `TunnelVersion` / `MatchTransport` | 搭配預設 Server 後端時，使用 `2` / `Afdev`。 |

Connect GUI 的工具視窗可產生配對的 TLS 憑證，並更新兩端設定。
操作前先停止 Server 並中斷遊戲連線；Server 目錄填入 `../Server`，Connect 目錄填入 `.`。
工具會備份原有設定與憑證。產生的 PFX 檔案包含私密金鑰，密碼為空白。
既有的密碼保護 PFX 預設從環境變數讀取密碼：Server 使用 `AF_CONNECT_CERT_PASSWORD`，
Connect 使用 `AF_EDGE_CERT_PASSWORD`，也可以在設定中指定其他變數。

手動設定 TLS 時，指定 Server 的 `RemoteConnect.ServerCertificatePath` 與
`AllowedClientCertificateSha256`；接著在 Connect 中設定 `ClientCertificatePath` 與
`ServerCertificateSha256`，對應配對的用戶端 PFX 與伺服器憑證指紋。

遊戲驗證金鑰與 TLS 憑證的用途不同。請在 Server 的 `PrivateKeyPath`（預設為
`secrets/PRIVATE.PEM`）提供相符的 1024 位元 RSA 私密金鑰，並在 Connect 的
`LauncherPublicKeyPath`（預設為 `secrets/APClient.dat`）提供對應的 PEM 公開金鑰。
玩家用戶端執行檔也必須包含對應的受支援公開金鑰。將 Server 的
`RemoteConnect.ExpectedApClientSha256` 與 Connect 的 `GamePublicKeySha256`
設為該遊戲公開金鑰的指紋。指紋依 SubjectPublicKeyInfo PEM 文字計算，採用 LF 換行，
並在結尾保留一個換行字元。TLS 憑證配對工具不會產生或替換遊戲驗證金鑰。

Connect 還會使用本機啟動器連接埠 8000、9010 與 9060。工具視窗可以將 `tversion`、
`tauthproxy` 與 `tdir.levelupgames.ph` 對應至回送位址，備份 hosts 檔案並重新整理 DNS。
Server 設定中的原生對局連接埠範圍也必須保持可用。

### 啟動程式

執行 `AFServer.Avalonia.exe` 後，桌面視窗會自動嘗試啟動 Server。
待 Server 就緒後，再開啟 `AFConnect.GUI.exe`。主控台使用者可執行 `AFServer.exe`
與 `AFConnect.exe`。同一端只選擇一種啟動方式。

Connect 與 Server GUI 的既有資訊清單要求系統管理員權限，Windows 正常啟動時可能顯示 UAC 提示。
Server 主控台使用目前使用者的權限；原生處理程序存取需要兩端採用相容的權限。

### 診斷

在儲存庫根目錄直接執行以下命令：

```powershell
& './Running/Server/AFServer.exe' --self-test --no-pause
& './Running/Server/AFServer.exe' --check --no-pause
& './Running/Connect/AFConnect.exe' --check
```

通訊協定自我測試不需要用戶端或資料庫。Server 的 `--check` 需要已設定的執行環境，
以及已啟動的 MongoDB 執行個體。Connect 的 `--check` 需要已部署的用戶端與 Server，
用於檢查連線、驗證及伺服器目錄。可透過 `AFConnect.exe --prepare-shop` 準備用戶端商城資源；
實際更新前，先停止正在使用共用資源的處理程序。

若啟動失敗，請檢查 MongoDB 是否可用、用戶端檔案是否缺漏、金鑰與憑證指紋是否配對，
以及連接埠是否被占用。記錄檔位於 EXE 旁的 `logs/`，Server GUI 也會在 `state/` 儲存本機狀態。

## 儲存庫結構

| 路徑 | 用途 |
| --- | --- |
| `AFEmulator.slnx` | 包含全部六個專案的 Visual Studio 方案 |
| `AFServer/` | 伺服器、桌面介面、通訊協定、持續性儲存與對局管理 |
| `AFConnect/` | 連線引擎、桌面啟動器與共用 Proxy 程式庫 |
| `develop/` | 已忽略的本機研究、診斷與 C# 測試；測試位於 `develop/test/` |
| `Running/` | 已忽略的本機 EXE 部署目錄與共用遊戲資源 |

## 授權條款

本專案原始碼採用 [MIT 授權條款](../LICENSE)。第三方相依套件保留各自的授權條款。
此授權不授予《Assault Fire》、TCLS、專有遊戲執行檔或用戶端資源的使用權；
請另外取得，並遵守各自適用的條款。
