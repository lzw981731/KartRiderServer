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
        /// <summary>俱乐部公告（显示在游戏内俱乐部页面）</summary>
        [JsonPropertyName("ClubIntro")]
        public string ClubIntro { get; set; } = "跑跑卡丁车交流群：84338611\n单机启动器下载地址：https://yanygm.github.io/Launcher_V2/";

        /// <summary>俱乐部名称</summary>
        [JsonPropertyName("ClubName")]
        public string ClubName { get; set; } = "TCCstar";

        /// <summary>俱乐部标志 LOGO</summary>
        [JsonPropertyName("ClubMarkLogo")]
        public int ClubMarkLogo { get; set; } = 0;

        /// <summary>俱乐部标志 LINE</summary>
        [JsonPropertyName("ClubMarkLine")]
        public int ClubMarkLine { get; set; } = 0;

        /// <summary>登录赠送车辆ID列表（新玩家首次登录自动获得）</summary>
        [JsonPropertyName("LoginGiftKarts")]
        public List<ushort> LoginGiftKarts { get; set; } = new List<ushort>();

        /// <summary>登录赠送角色ID列表</summary>
        [JsonPropertyName("LoginGiftCharacters")]
        public List<ushort> LoginGiftCharacters { get; set; } = new List<ushort>();

        /// <summary>登录赠送 Lucci 数量（0=不赠送）</summary>
        [JsonPropertyName("LoginGiftLucci")]
        public uint LoginGiftLucci { get; set; } = 0;

        /// <summary>登录赠送 Koin 数量（0=不赠送）</summary>
        [JsonPropertyName("LoginGiftKoin")]
        public uint LoginGiftKoin { get; set; } = 0;

        /// <summary>新玩家初始 Lucci（默认 1000000）</summary>
        [JsonPropertyName("InitialLucci")]
        public uint InitialLucci { get; set; } = 1000000;

        /// <summary>新玩家初始 RP（默认 2000000000）</summary>
        [JsonPropertyName("InitialRP")]
        public uint InitialRP { get; set; } = 2000000000;

        /// <summary>新玩家初始 Koin（默认 1000000）</summary>
        [JsonPropertyName("InitialKoin")]
        public uint InitialKoin { get; set; } = 1000000;

        /// <summary>新玩家初始 Cash（默认 1000000）</summary>
        [JsonPropertyName("InitialCash")]
        public uint InitialCash { get; set; } = 1000000;

        /// <summary>新玩家初始 TcCash（默认 1000000）</summary>
        [JsonPropertyName("InitialTcCash")]
        public uint InitialTcCash { get; set; } = 1000000;

        /// <summary>新玩家初始 VIP 等级（0=无, 5=满级，默认 5）</summary>
        [JsonPropertyName("InitialPremium")]
        public int InitialPremium { get; set; } = 5;

        /// <summary>新玩家初始卡槽切换器数量（0=无, 32767=满，默认 32767）</summary>
        [JsonPropertyName("InitialSlotChanger")]
        public int InitialSlotChanger { get; set; } = 32767;

        private static readonly object _lock = new object();
        private static ServerConf _instance = new ServerConf();
        private static string _confPath;

        /// <summary>获取当前配置（只读）</summary>
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
                if (!File.Exists(_confPath))
                {
                    // 创建默认配置文件
                    SaveDefault();
                    Console.WriteLine($"[ServerConf] 已创建默认配置: {_confPath}");
                    return;
                }

                try
                {
                    string json = File.ReadAllText(_confPath, Encoding.UTF8);
                    _instance = JsonSerializer.Deserialize<ServerConf>(json) ?? new ServerConf();
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
                    Console.WriteLine($"[ServerConf] 加载配置失败: {ex.Message}，使用默认配置");
                    _instance = new ServerConf();
                }
            }
        }

        /// <summary>
        /// 保存默认配置文件
        /// </summary>
        private static void SaveDefault()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(new ServerConf(), options);
                File.WriteAllText(_confPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerConf] 创建默认配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 判断玩家是否需要登录赠送（Profile 目录不存在 = 新玩家）
        /// </summary>
        public bool HasLoginGifts()
        {
            return (LoginGiftKarts != null && LoginGiftKarts.Count > 0)
                || (LoginGiftCharacters != null && LoginGiftCharacters.Count > 0)
                || LoginGiftLucci > 0
                || LoginGiftKoin > 0;
        }
    }
}