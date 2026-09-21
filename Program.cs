using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KartRider;
using KartRider.Common.Data;
using Profile;
using RiderData;

namespace KartRider
{
    /// <summary>
    /// KartRider 独立服务器入口（headless，无 GUI）。
    /// 部署在 Linux VPS / Windows 服务器上，玩家通过 Launcher_V2 连接。
    /// </summary>
    internal static class Program
    {
        // ---- 被服务器各模块引用的全局开关（原 Launcher GUI 的静态状态）----

        /// <summary>禁用道具（原 Launcher 设置的 per-profile 开关）</summary>
        public static bool PreventItem = false;

        /// <summary>速度补丁（原 Launcher 设置的 per-profile 开关）</summary>
        public static bool SpeedPatch = false;

        /// <summary>游戏根目录（用于定位客户端数据文件，例如 Data/aaa.pk、KartRider.pin）</summary>
        public static string GameRoot = ".";

        /// <summary>服务器监听端口（默认 39311）</summary>
        public static ushort ListenPort = 39311;

        /// <summary>是否已配置好所有参数（校验失败时退出）</summary>
        private static bool _configured;

        /// <summary>
        /// 打印用法帮助
        /// </summary>
        private static void PrintHelp()
        {
            Console.WriteLine("KartRider Headless Server");
            Console.WriteLine();
            Console.WriteLine("用法: KartRiderServer [选项]");
            Console.WriteLine();
            Console.WriteLine("  --root <path>       游戏(客户端)数据根目录，用于读取 Data/aaa.pk 赛道表和 KartRider.pin 版本");
            Console.WriteLine("  --port <port>       监听端口，默认 39311（客户端填写 ServerPort 即此值）");
            Console.WriteLine("  --profile <path>    玩家 Profile 数据存储目录，默认 <root>/Profile");
            Console.WriteLine("  --client-version <v>客户端版本号(ushort)，默认从 KartRider.pin 读取");
            Console.WriteLine("  --locale <id>       LocaleID，默认从 KartRider.pin 读取");
            Console.WriteLine("  --country <id>      nClientLoc，默认从 KartRider.pin 读取");
            Console.WriteLine("  --no-public-ip      跳过公网 IP 探测（内网部署/无外网时更快启动）");
            Console.WriteLine("  -h, --help          显示帮助");
            Console.WriteLine();
            Console.WriteLine("示例: KartRiderServer --root /srv/kart --port 39311");
        }

        private static void Main(string[] args)
        {
            Console.WriteLine("=== KartRider Headless Server ===");
            Console.WriteLine($"启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // ---- 参数解析 ----
            string root = ".";
            string profileDir = null;
            ushort? portOverride = null;
            ushort? clientVersion = null;
            ushort? localeId = null;
            ushort? nClientLoc = null;
            bool noPublicIp = false;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--root":
                            root = args[++i];
                            break;
                        case "--port":
                            portOverride = ushort.Parse(args[++i]);
                            break;
                        case "--profile":
                            profileDir = args[++i];
                            break;
                        case "--client-version":
                            clientVersion = ushort.Parse(args[++i]);
                            break;
                        case "--locale":
                            localeId = ushort.Parse(args[++i]);
                            break;
                        case "--country":
                            nClientLoc = ushort.Parse(args[++i]);
                            break;
                        case "--no-public-ip":
                            noPublicIp = true;
                            break;
                        case "-h":
                        case "--help":
                            PrintHelp();
                            return;
                        default:
                            Console.WriteLine($"未知参数: {args[i]}");
                            PrintHelp();
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"参数解析失败: {ex.Message}");
                PrintHelp();
                return;
            }

            // ---- 环境校验 ----
            if (!Directory.Exists(root))
            {
                Console.WriteLine($"[FATAL] 游戏根目录不存在: {root}");
                Console.WriteLine("请使用 --root 指定客户端数据根目录（例如 /srv/kart，需包含 Data 子目录）。");
                return;
            }

            GameRoot = Path.GetFullPath(root);
            ListenPort = portOverride ?? ListenPort;

            // ---- Profile 目录 ----
            if (profileDir == null)
            {
                profileDir = Path.Combine(GameRoot, "Profile");
            }
            profileDir = Path.GetFullPath(profileDir);
            Directory.CreateDirectory(profileDir);

            // 配置 Profile 文件路径基准目录
            ConfigureFileNamePaths(profileDir);

            // 加载/保存设置
            ProfileService.LoadSettings();
            ProfileService.SettingConfig.ServerPort = ListenPort;

            // 版本参数：优先命令行，其次从 KartRider.pin 读取
            if (clientVersion.HasValue) ProfileService.SettingConfig.ClientVersion = clientVersion.Value;
            if (localeId.HasValue) ProfileService.SettingConfig.LocaleID = localeId.Value;
            if (nClientLoc.HasValue) ProfileService.SettingConfig.nClientLoc = nClientLoc.Value;

            if (ProfileService.SettingConfig.ClientVersion == 0)
            {
                TryReadPinVersion(GameRoot);
            }

            Console.WriteLine($"监听端口: {ListenPort}");
            Console.WriteLine($"客户端版本: {ProfileService.SettingConfig.ClientVersion} (LocaleID={ProfileService.SettingConfig.LocaleID}, nClientLoc={ProfileService.SettingConfig.nClientLoc})");
            Console.WriteLine($"Profile 目录: {profileDir}");
            Console.WriteLine($"游戏根目录: {GameRoot}");

            // ---- 加载赛道数据（RhoDump：读取 Data/aaa.pk 填充 RandomTrack.TrackList）----
            if (!LoadTrackData(GameRoot))
            {
                Console.WriteLine("[FATAL] 赛道数据加载失败，服务器无法启动。");
                return;
            }

            // ---- 诊断：输出 items 表结构（类别号→条目数），并定位宠物/飞宠/角色的类别 ----
            try
            {
                Console.WriteLine($"[DIAG] items 表共 {NewRider.items.Count} 个类别:");
                foreach (var kv in NewRider.items.OrderBy(x => x.Key))
                {
                    Console.WriteLine($"[DIAG]   itemCatId={kv.Key,-4} 条目={kv.Value.Count,-5} (范围 {kv.Value.Keys.Min()}-{kv.Value.Keys.Max()})");
                }
                foreach (ushort probe in new ushort[] { 135, 30008, 362, 501, 1522, 1426 })
                {
                    var hit = NewRider.items.FirstOrDefault(kv => kv.Value.ContainsKey(probe));
                    Console.WriteLine($"[DIAG]   ID {probe} -> itemCatId={hit.Key} ({(hit.Value == null ? "不存在" : hit.Value[probe])})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DIAG] 输出 items 结构失败: {ex.Message}");
            }

            // ---- 生成必备的 Profile 配置文件 ----
            // 原版 Launcher 在 Load_Data() 中自动生成，服务器若不生成会导致
            // GrSessionDataPacket(开赛数据)读取 SpecialKartConfig.json 时抛异常，
            // 客户端表现为点开始游戏后卡死。
            EnsureProfileFiles();

            // ---- 启动服务器 ----
            Console.WriteLine("正在启动服务器...");
            try
            {
                if (noPublicIp)
                {
                    RouterListener.RouterIPList = LanIpGetter.GetAllLocalLanIps();
                    RouterListener.RouterIPList.Add("127.0.0.1");
                }
                RouterListener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] 服务器启动失败: {ex.Message}");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("=== 服务器已启动 ===");
            Console.WriteLine($"TCP 监听: 0.0.0.0:{ListenPort} (+{ListenPort + 2} 消息)");
            Console.WriteLine($"UDP 监听: 0.0.0.0:{ListenPort} (+{ListenPort + 1} P2P)");
            Console.WriteLine("按 Ctrl+C 停止服务器。");

            // ---- 阻塞等待 ----
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\n正在停止服务器...");
                    RouterListener.Stop();
                    cts.Cancel();
                };
                try
                {
                    Task.Delay(Timeout.Infinite, cts.Token).Wait();
                }
                catch (AggregateException)
                {
                    // 取消令牌触发，正常退出流程
                }
            }

            Console.WriteLine("服务器已停止。");
        }

        /// <summary>
        /// 配置 FileName 的路径基准。原 Launcher 以 appDir(程序所在目录)为基准，
        /// 服务器改为以玩家 Profile 目录为基准。
        /// </summary>
        private static void ConfigureFileNamePaths(string profileDir)
        {
            FileName.ProfileDir = profileDir;
            FileName.Load_Settings = Path.Combine(profileDir, "Settings.json");
            FileName.ModelMax_LoadFile = Path.Combine(profileDir, "ModelMax.xml");
            FileName.SpecialKartConfig = Path.Combine(profileDir, "SpecialKartConfig.json");
            FileName.Coupon = Path.Combine(profileDir, "Coupon.json");
            FileName.Load_TrackRank = Path.Combine(profileDir, "TrackRank");
        }

        /// <summary>
        /// 尝试从客户端 KartRider.pin 读取版本信息（仅当命令行未指定版本时）。
        /// </summary>
        private static void TryReadPinVersion(string root)
        {
            try
            {
                string pinPath = Path.Combine(root, "KartRider.pin");
                if (!File.Exists(pinPath))
                {
                    Console.WriteLine("[WARN] 未找到 KartRider.pin，请用 --client-version/--locale/--country 指定版本信息");
                    return;
                }

                PINFile pin = new PINFile(pinPath);
                ProfileService.SettingConfig.ClientVersion = pin.Header.MinorVersion;
                ProfileService.SettingConfig.LocaleID = pin.Header.LocaleID;
                ProfileService.SettingConfig.nClientLoc = pin.Header.Unk2;
                Console.WriteLine($"[PIN] 已从 KartRider.pin 读取版本: {pin.Header.MinorVersion}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] 读取 KartRider.pin 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载赛道数据：读 Data/aaa.pk 填充 RandomTrack.TrackList。
        /// 数据加载失败时返回 false 阻止服务器启动（避免玩家进房后无赛道列表）。
        /// </summary>
        private static bool LoadTrackData(string root)
        {
            try
            {
                string aaaPath = Path.Combine(root, "Data", "aaa.pk");
                if (!File.Exists(aaaPath))
                {
                    Console.WriteLine($"[WARN] 未找到 {aaaPath}，尝试其他数据路径...");
                    // 允许从 Launcher 根目录直接指定（aaa.pk 可能在不同位置）
                    string altPath = Path.Combine(root, "aaa.pk");
                    if (File.Exists(altPath))
                    {
                        aaaPath = altPath;
                    }
                    else
                    {
                        Console.WriteLine($"[FATAL] 无法定位 aaa.pk（需要客户端 Data 目录数据）");
                        return false;
                    }
                }

                Console.WriteLine($"正在读取赛道数据: {aaaPath}");
                var packFolderManager = KartRhoFile.Dump(aaaPath);
                if (packFolderManager == null)
                {
                    Console.WriteLine("[FATAL] aaa.pk 解析失败");
                    return false;
                }
                packFolderManager.Reset();
                Console.WriteLine($"赛道数据加载完成，共 {RandomTrack.TrackList.Count} 条赛道记录");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] 赛道数据加载异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 生成服务器运行必需的 Profile 配置文件。
        /// 缺失 SpecialKartConfig.json 会导致 GrSessionDataPacket(开赛数据)抛异常，
        /// 客户端表现为点开始游戏后卡死无响应。
        /// </summary>
        private static void EnsureProfileFiles()
        {
            try
            {
                SpecialKartConfig.SaveConfigToFile(FileName.SpecialKartConfig);
                Console.WriteLine($"[配置] SpecialKartConfig.json 已就绪");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[配置] 生成 SpecialKartConfig.json 失败: {ex.Message}");
            }

            try
            {
                if (!File.Exists(FileName.ModelMax_LoadFile))
                {
                    File.WriteAllText(FileName.ModelMax_LoadFile, ModelMax.XmlContent);
                    Console.WriteLine($"[配置] ModelMax.xml 已创建");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[配置] 生成 ModelMax.xml 失败: {ex.Message}");
            }
        }
    }
}