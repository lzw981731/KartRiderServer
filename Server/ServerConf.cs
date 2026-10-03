using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KartRider
{
    /// <summary>
    /// 服务器配置（server.conf），启动时加载
    /// </summary>
    public class ServerConf
    {
        // ---- 俱乐部信息（覆盖玩家档案中的俱乐部数据）----

        /// <summary>俱乐部公告（显示在游戏内俱乐部页面，支持\n换行）</summary>
        [JsonPropertyName("ClubIntro")]
        public string ClubIntro { get; set; } = "跑跑卡丁车交流群：84338611\n单机启动器下载地址：https://yanygm.github.io/Launcher_V2/";

        /// <summary>俱乐部名称</summary>
        [JsonPropertyName("ClubName")]
        public string ClubName { get; set; } = "TCCstar";

        /// <summary>俱乐部标志 LOGO（0=无标志）</summary>
        [JsonPropertyName("ClubMarkLogo")]
        public int ClubMarkLogo { get; set; } = 0;

        /// <summary>俱乐部标志 LINE（0=无标志）</summary>
        [JsonPropertyName("ClubMarkLine")]
        public int ClubMarkLine { get; set; } = 0;

        // ---- 登录赠送（仅新玩家首次登录生效，Profile目录不存在=新玩家）----

        /// <summary>登录赠送车辆ID列表，如 [167, 168]（空列表=不赠送）</summary>
        [JsonPropertyName("LoginGiftKarts")]
        public List<ushort> LoginGiftKarts { get; set; } = new List<ushort>();

        /// <summary>登录赠送角色ID列表（空列表=不赠送）</summary>
        [JsonPropertyName("LoginGiftCharacters")]
        public List<ushort> LoginGiftCharacters { get; set; } = new List<ushort>();

        /// <summary>登录赠送 Lucci（游戏金币）数量，在初始值基础上累加（0=不赠送）</summary>
        [JsonPropertyName("LoginGiftLucci")]
        public uint LoginGiftLucci { get; set; } = 0;

        /// <summary>登录赠送 Koin 数量，在初始值基础上累加（0=不赠送）</summary>
        [JsonPropertyName("LoginGiftKoin")]
        public uint LoginGiftKoin { get; set; } = 0;

        // ---- 新玩家初始值（首次登录时覆盖硬编码默认值）----

        /// <summary>新玩家初始 Lucci（游戏金币，默认1000000）</summary>
        [JsonPropertyName("InitialLucci")]
        public uint InitialLucci { get; set; } = 1000000;

        /// <summary>新玩家初始 RP（排位积分=角色等级依据，默认2000000000=满级，0=1级新手）</summary>
        [JsonPropertyName("InitialRP")]
        public uint InitialRP { get; set; } = 2000000000;

        /// <summary>新玩家初始 Koin（默认1000000）</summary>
        [JsonPropertyName("InitialKoin")]
        public uint InitialKoin { get; set; } = 1000000;

        /// <summary>新玩家初始 Cash（点券，默认1000000）</summary>
        [JsonPropertyName("InitialCash")]
        public uint InitialCash { get; set; } = 1000000;

        /// <summary>新玩家初始 TcCash（限时点券，默认1000000）</summary>
        [JsonPropertyName("InitialTcCash")]
        public uint InitialTcCash { get; set; } = 1000000;

        /// <summary>新玩家初始 VIP 等级（0=无VIP，5=满级VIP，默认5）</summary>
        [JsonPropertyName("InitialPremium")]
        public ushort InitialPremium { get; set; } = 5;

        /// <summary>新玩家初始卡槽切换器数量（0=无，32767=满，默认32767）</summary>
        [JsonPropertyName("InitialSlotChanger")]
        public ushort InitialSlotChanger { get; set; } = 32767;

        // ---- 内部字段 ----

        private static readonly object _lock = new object();      // 配置读写锁
        private static ServerConf _instance = new ServerConf();   // 单例实例
        private static string _confPath;                          // 配置文件路径

        /// <summary>获取当前配置（只读单例）</summary>
        public static ServerConf Current
        {
            get { return _instance; }
        }

        /// <summary>
        /// 从 server.conf 加载配置，文件不存在则创建默认配置
        /// </summary>
        public static void Load(string rootDir)
        {
            lock (_lock)
            {
                _confPath = Path.GetFullPath(Path.Combine(rootDir, "server.conf"));

                // 配置文件不存在时，创建带中文注释的默认配置
                if (!File.Exists(_confPath))
                {
                    SaveDefault();
                    Console.WriteLine($"[ServerConf] 已创建默认配置: {_confPath}");
                    return;
                }

                // 读取配置文件（允许包含 // 注释行，读取时自动跳过）
                try
                {
                    string json = File.ReadAllText(_confPath, Encoding.UTF8);
                    // 去除 // 单行注释（不影响字符串内的 //）
                    json = RemoveComments(json);
                    _instance = JsonSerializer.Deserialize<ServerConf>(json) ?? new ServerConf();

                    // 打印加载信息
                    Console.WriteLine($"[ServerConf] 已加载配置: {_confPath}");
                    Console.WriteLine($"  ClubIntro: {_instance.ClubIntro.Substring(0, Math.Min(40, _instance.ClubIntro.Length))}...");
                    Console.WriteLine($"  ClubName: {_instance.ClubName}");
                    Console.WriteLine($"  LoginGiftKarts: [{string.Join(", ", _instance.LoginGiftKarts)}]");
                    if (_instance.LoginGiftCharacters.Count > 0)
                        Console.WriteLine($"  LoginGiftCharacters: [{string.Join(", ", _instance.LoginGiftCharacters)}]");
                    if (_instance.LoginGiftLucci > 0)
                        Console.WriteLine($"  LoginGiftLucci: {_instance.LoginGiftLucci}");
                    if (_instance.LoginGiftKoin > 0)
                        Console.WriteLine($"  LoginGiftKoin: {_instance.LoginGiftKoin}");
                }
                catch (Exception ex)
                {
                    // 加载失败时使用默认配置，不中断启动
                    Console.WriteLine($"[ServerConf] 加载配置失败: {ex.Message}，使用默认配置");
                    _instance = new ServerConf();
                }
            }
        }

        /// <summary>
        /// 去除 JSON 中的 // 单行注释
        /// </summary>
        private static string RemoveComments(string json)
        {
            var sb = new StringBuilder();
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                    sb.Append(c);
                }
                else if (!inString && c == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    // 跳过注释直到行尾
                    while (i < json.Length && json[i] != '\n') i++;
                    // 保留换行符
                    if (i < json.Length) sb.Append('\n');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 保存默认配置文件（带中文注释，方便手动编辑）
        /// </summary>
        private static void SaveDefault()
        {
            try
            {
                string content = @"{
  // ==== 俱乐部信息（覆盖玩家档案中的俱乐部数据）====
  ""ClubIntro"": ""跑跑卡丁车交流群：84338611\n单机启动器下载地址：https://yanygm.github.io/Launcher_V2/"",  // 俱乐部公告（支持\n换行）
  ""ClubName"": ""TCCstar"",       // 俱乐部名称
  ""ClubMarkLogo"": 0,             // 俱乐部标志 LOGO（0=无标志）
  ""ClubMarkLine"": 0,             // 俱乐部标志 LINE（0=无标志）

  // ==== 登录赠送（仅新玩家首次登录生效，在初始值基础上累加）====
  ""LoginGiftKarts"": [],           // 登录赠送车辆ID列表，如 [167, 168]（空=不赠送）
  ""LoginGiftCharacters"": [],      // 登录赠送角色ID列表（空=不赠送）
  ""LoginGiftLucci"": 0,            // 登录赠送 Lucci（游戏金币），0=不赠送
  ""LoginGiftKoin"": 0,             // 登录赠送 Koin，0=不赠送

  // ==== 新玩家初始值（首次登录时覆盖硬编码默认值）====
  ""InitialLucci"": 1000000,        // 新玩家初始 Lucci（游戏金币）
  ""InitialRP"": 2000000000,        // 新玩家初始 RP（排位积分=角色等级，0=1级新手，20亿=满级）
  ""InitialKoin"": 1000000,         // 新玩家初始 Koin
  ""InitialCash"": 1000000,         // 新玩家初始 Cash（点券）
  ""InitialTcCash"": 1000000,       // 新玩家初始 TcCash（限时点券）
  ""InitialPremium"": 5,            // 新玩家初始 VIP 等级（0=无，5=满级）
  ""InitialSlotChanger"": 32767     // 新玩家初始卡槽切换器（0=无，32767=满）
}
";
                File.WriteAllText(_confPath, content, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerConf] 创建默认配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 判断是否有登录赠送配置（任一字段非零/非空即返回true）
        /// </summary>
        public bool HasLoginGifts()
        {
            return (LoginGiftKarts != null && LoginGiftKarts.Count > 0)   // 有赠送车辆
                || (LoginGiftCharacters != null && LoginGiftCharacters.Count > 0)  // 有赠送角色
                || LoginGiftLucci > 0   // 有赠送Lucci
                || LoginGiftKoin > 0;   // 有赠送Koin
        }
    }
}