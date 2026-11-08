# AFEmulator

English | [简体中文](docs/README.zh-CN.md) | [繁體中文](docs/README.zh-TW.md)

A Windows C# server emulator and connection client for the Philippine 2014
Assault Fire client. The solution includes Server and Connect console programs,
Avalonia desktop interfaces, and a shared transport library.

Matches run on the original game's native dedicated server (DS) engine through
the `Afdev` backend, using a separately supplied game installation. Server
handles launcher protocols, accounts, player state, lobby operations, commerce,
and match allocation. Connect authenticates the connection, forwards traffic
through mutual TLS, prepares local resources, and launches the player client.

Development has ended. This repository is released as-is, and no further
development or updates are planned.

## Implemented features

- Expanded the shop with additional items.
- Fixed the one-hour duration display issue in storage and incorrect item unit labels.
- Fixed slow loadout switching and related stutter.
- Added support for achievements, levels, and player name cards.
- Enabled revive coins to restart rounds in all PVE modes.
- Fixed issues that prevented compatible items from being equipped together.
- Fixed match settlement issues; some maps still have settlement problems.

| Mode | Supported content |
| --- | --- |
| PVP | Team Deathmatch, Demolition, and Free-for-All across all maps, with bot support. Demolition bots still have known issues. |
| PVE | All maps and difficulty levels. |
| XPVP | Standard Mutation across all maps, with bot support. |

## Requirements

- Windows x64 and **Visual Studio 2026**.
- **.NET 10 SDK**, the **.NET desktop development** workload, and the
  **Desktop development with C++** workload with a Windows SDK for Native AOT.
- NuGet access during restore.
- A compatible AF2014 client and two verified AFDEV executables, supplied
  separately. Game files and TCLS are not included.
- MongoDB and deployment-specific authentication keys and TLS certificates
  for an operational server.

## Build in Visual Studio

1. Open **`AFEmulator.slnx`** in Visual Studio 2026.
2. Restore NuGet packages, select **Release / Any CPU**, and build the solution.
3. Right-click each executable project, choose **Publish**, and use the supplied
   **Windows-x64** folder profile. Publish all four executable projects.

| Project | Published executable | Destination |
| --- | --- | --- |
| `AFServer/AFServer` | `AFServer.exe` | `Running/Server` |
| `AFServer/AFServer.Avalonia` | `AFServer.Avalonia.exe` | `Running/Server` |
| `AFConnect/AFConnect.Edge` | `AFConnect.exe` | `Running/Connect` |
| `AFConnect/AFConnect.Launcher` | `AFConnect.GUI.exe` | `Running/Connect` |

The profiles publish self-contained Windows x64 Native AOT applications. Launch
the resulting EXEs directly; no Python, batch, or PowerShell runtime scripts
are required. The desktop and console hosts on each side share one deployment
configuration and must not run concurrently against the same resources.

Alternatively, open **Developer PowerShell for VS 2026** in the repository
directory and run these commands directly:

```powershell
MSBuild.exe AFEmulator.slnx -restore -t:Build -p:Configuration=Release
MSBuild.exe AFServer/AFServer/AFServer.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFServer/AFServer.Avalonia/AFServer.Avalonia.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFConnect/AFConnect.Edge/AFConnect.Edge.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
MSBuild.exe AFConnect/AFConnect.Launcher/AFConnect.Launcher.csproj -restore -t:Publish -p:PublishProfile=Windows-x64 -p:Configuration=Release
```

Publishing preserves existing `appsettings.json` and `config.json`. Stop the
target application before replacing its EXE or native libraries. Server Native
AOT publishing retains the existing MongoDB trimming and AOT analysis warnings.

## Configure and run

Keep one shared client installation in the following local layout:

```text
Running/
  Server/
    AFServer.exe
    AFServer.Avalonia.exe
    appsettings.json
    data/
    secrets/                 # local keys and certificates
    database/                # optional local MongoDB executable and data
  Connect/
    AFConnect.exe
    AFConnect.GUI.exe
    config.json
    secrets/                 # local public key and client certificate
  AF_2014/
    Binaries/Win32/
      TGame.exe
      TGame_AFDEV.exe
      TGame_AFDEV2.exe
    TGame/
```

The two AFDEV EXEs must use the same verified native build. Configure both DS
slots and Connect to use this shared tree directly. Do not create client copies,
symbolic links, junctions, or hard links for separate roles.

Edit `Running/Server/appsettings.json` and `Running/Connect/config.json` before
connecting. The checked-in settings are templates: `YOUR_SERVER_IP`, certificate
fingerprints, authentication keys, and account state require local setup. The
default pair uses `MatchBackend: Afdev`, `TunnelVersion: 2`, and
`MatchTransport: Afdev`. File paths are relative to each EXE's directory.

### Database and account

Use a running MongoDB service by setting `MongoConnectionString` and
`MongoDatabase` in Server settings and disabling `MongoLocalProcess.AutoStart`.
Alternatively, supply `database/bin/mongod.exe` beside the Server deployment,
leave automatic startup enabled, and keep its data in `database/data`.
The MongoDB executable and account database are not included in the source.

To create an account, start MongoDB and run:

```powershell
& './Running/Server/AFServer.exe' --create-account player1
```

Replace `player1` with the account name. The command reads the password
interactively and returns a UIN. Set Server's `ActiveUin` to the intended existing
account. The launcher authentication flow selects that account; entering a name
in the interface is not a separate password login.

### Connection and authentication

| Settings | Setup |
| --- | --- |
| Server `RemoteConnect.Port` and Connect `ServerPort` | Use the same TLS port; the default is 9443. |
| Connect `ServerHost` | Replace `YOUR_SERVER_IP` with the reachable Server host. |
| Server `RemoteConnect.ListenAddress` | The default is loopback. For another machine, bind the intended network interface and allow the TLS port through its firewall. |
| ROLE and ZONE ports | Keep Connect's `LocalRolePort` / `LocalZonePort` equal to Server's `RemoteConnect` values; defaults are 65005 / 65006. |
| Server `MatchBackend` | Use `Afdev` for native DS matches. |
| Connect `TunnelVersion` / `MatchTransport` | Use `2` / `Afdev` with the default Server backend. |

The Connect GUI's Tools window can generate paired TLS certificates and update
both configurations. Stop Server and disconnect the game first; use `../Server`
for the Server directory and `.` for Connect. The tool backs up prior settings
and certificates. Generated PFX files contain private keys and use an empty
password. Existing password-protected PFX files use `AF_CONNECT_CERT_PASSWORD`
on Server and `AF_EDGE_CERT_PASSWORD` on Connect unless configured otherwise.

For manual TLS setup, set Server's `RemoteConnect.ServerCertificatePath` and
`AllowedClientCertificateSha256`, then configure Connect's
`ClientCertificatePath` and `ServerCertificateSha256` with the paired client PFX
and Server certificate fingerprint.

Game-authentication keys are separate from TLS certificates. Supply the matching
1024-bit RSA private key at Server's `PrivateKeyPath` (default
`secrets/PRIVATE.PEM`) and its PEM public key at Connect's
`LauncherPublicKeyPath` (default `secrets/APClient.dat`). The player executable
must carry the corresponding supported public key. Set Server's
`RemoteConnect.ExpectedApClientSha256` and Connect's `GamePublicKeySha256` to
that game public-key fingerprint. It is calculated from SubjectPublicKeyInfo
PEM with LF newlines and one trailing newline. TLS pairing does not generate or
replace these game keys.

Connect also uses local launcher ports 8000, 9010, and 9060. Its Tools window can
map `tversion`, `tauthproxy`, and `tdir.levelupgames.ph` to loopback, back up the
hosts file, and refresh DNS. The native match port ranges configured in Server
must also remain available.

### Start the applications

Start `AFServer.Avalonia.exe`; its window attempts to start Server automatically.
Once Server is ready, open `AFConnect.GUI.exe`. Console users can run
`AFServer.exe` and `AFConnect.exe`. Choose one host per side.

Connect and Server GUI request administrator rights through their existing
manifests. Normal Windows launches may show a UAC prompt. The Server console
uses the caller's privileges; native process access requires compatible
privileges on both sides.

### Diagnostics

Run these commands directly from the repository directory:

```powershell
& './Running/Server/AFServer.exe' --self-test --no-pause
& './Running/Server/AFServer.exe' --check --no-pause
& './Running/Connect/AFConnect.exe' --check
```

The protocol self-test needs no client or database. Server `--check` requires
the configured environment and an already running MongoDB instance. Connect
`--check` requires the deployed client and Server and checks connection,
authentication, and the server directory. Client shop preparation is available
through `AFConnect.exe --prepare-shop`; stop processes using the shared resources
before an actual update.

If startup fails, check MongoDB availability, missing client files, paired keys
and certificate fingerprints, and occupied ports. Logs are written beside the
EXEs under `logs/`; Server GUI also keeps local state under `state/`.

## Repository layout

| Path | Purpose |
| --- | --- |
| `AFEmulator.slnx` | Complete six-project Visual Studio solution |
| `AFServer/` | Server host, desktop interface, protocols, persistence, and matches |
| `AFConnect/` | Connection engine, desktop launcher, and shared proxy library |
| `develop/` | Ignored local research, diagnostics, and C# tests under `develop/test/` |
| `Running/` | Ignored local executable deployments and the shared game installation |

## License

The project source is licensed under the [MIT License](LICENSE). Third-party
dependencies retain their own licenses. This license does not grant rights to
Assault Fire, TCLS, proprietary game executables, or client assets; obtain and
use those separately under their applicable terms.
