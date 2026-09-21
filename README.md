# KartRider Headless Server

从 [Launcher_V2](https://github.com/yanygm/Launcher_V2) 提取的独立服务器程序（无 GUI），部署在 Linux VPS / Windows 服务器上，玩家通过 Launcher_V2 连接公网 IP 即可联机。

## 构建

GitHub Actions 云编译（推送 main 自动触发），产物为 `linux-x64` 自包含单文件。

## 部署（Linux VPS）

1. 从 Actions 产物下载 `KartRiderServer-linux-x64`，上传到 VPS。
2. 把客户端数据目录（需要 `Data/aaa.pk`、可选 `KartRider.pin`）放到服务器上，例如 `/srv/kart`。
3. 运行：

```bash
chmod +x KartRiderServer
./KartRiderServer --root /srv/kart --port 39311
```

4. 玩家在 Launcher_V2 设置里填写 `VPS公网IP:39311` 即可连接。

## 参数

| 参数 | 说明 |
|---|---|
| `--root <path>` | 游戏（客户端）数据根目录，用于读取 `Data/aaa.pk` 赛道表和 `KartRider.pin` 版本 |
| `--port <port>` | 监听端口，默认 `39311` |
| `--profile <path>` | 玩家 Profile 存储目录，默认 `<root>/Profile` |
| `--client-version <v>` | 客户端版本号（ushort），默认从 `KartRider.pin` 读取 |
| `--locale <id>` | LocaleID，默认从 `KartRider.pin` 读取 |
| `--country <id>` | nClientLoc，默认从 `KartRider.pin` 读取 |
| `--no-public-ip` | 跳过公网 IP 探测（内网部署时启动更快） |
| `-h, --help` | 帮助 |

## 端口

| 端口 | 协议 | 用途 |
|---|---|---|
| `39311` | TCP+UDP | 游戏主通信 |
| `39312` | UDP | P2P |
| `39313` | TCP | 消息服务器 |

## 实现说明

- 代码来自 Launcher_V2 的 `KartRider.Data/Server`（RouterListener / MsgrServer / UdpServer / ClientSession / MultyPlayer 等），剥离了 WinForms GUI 和客户端启动逻辑。
- 适配点：
  - `ProfileService.Loaded()` 移除 `Program.LauncherDlg` 引用
  - `UdpServer` 的 `SIO_UDP_CONNRESET` IOControl 仅 Windows 执行
  - `Update.GetCountryAsync` 精简为 `NetUtil.GetCountryAsync`
  - 新增 headless 入口 `Program.cs`