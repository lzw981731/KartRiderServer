using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KartRider
{
    /// <summary>
    /// 账号数据
    /// </summary>
    public class AccountData
    {
        /// <summary>账号名（小写存储，登录时不区分大小写）</summary>
        public string Username { get; set; }

        /// <summary>密码哈希（SHA-256）</summary>
        public string PasswordHash { get; set; }

        /// <summary>随机盐</summary>
        public string Salt { get; set; }

        /// <summary>绑定的游戏昵称（空=未创角）</summary>
        public string Nickname { get; set; }

        /// <summary>注册时间</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>最后登录时间</summary>
        public DateTime LastLoginAt { get; set; }
    }

    /// <summary>
    /// 账号存储（accounts.json）
    /// </summary>
    public class AccountStore
    {
        /// <summary>key = 小写账号名</summary>
        public Dictionary<string, AccountData> Accounts { get; set; } = new Dictionary<string, AccountData>();
    }

    /// <summary>
    /// 账号服务：注册、登录、创角、昵称校验、HTTP API
    /// </summary>
    public static class AccountService
    {
        private static readonly object _lock = new object();
        private static AccountStore _store = new AccountStore();
        private static string _accountsPath;
        private static HttpListener _httpListener;
        private static CancellationTokenSource _cts;
        private static FileSystemWatcher _watcher;
        private static volatile bool _skipWatcherReload;
        private static System.Threading.Timer _reloadDebounce;

        /// <summary>已登录 token → 小写账号名（内存缓存，重启失效）</summary>
        private static readonly ConcurrentDictionary<string, string> _tokens = new ConcurrentDictionary<string, string>();

        /// <summary>token 有效期</summary>
        private static readonly TimeSpan TokenExpiry = TimeSpan.FromHours(24);

        /// <summary>token 创建时间</summary>
        private static readonly ConcurrentDictionary<string, DateTime> _tokenCreatedAt = new ConcurrentDictionary<string, DateTime>();

        /// <summary>是否启用账号认证（false = 任何人可直接登录，兼容旧模式）</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// 初始化账号服务，加载 accounts.json
        /// </summary>
        public static void Init(string profileDir)
        {
            _accountsPath = Path.GetFullPath(Path.Combine(profileDir, "accounts.json"));
            Load();
            StartWatcher();
            Console.WriteLine($"[AccountService] 已加载 {_store.Accounts.Count} 个账号，认证={(Enabled ? "开启" : "关闭")}");
        }

        /// <summary>
        /// 启动 HTTP API 服务
        /// </summary>
        public static void StartHttpApi(int port)
        {
            _cts = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://*:{port}/");
            _httpListener.Start();
            _ = ListenAsync(_cts.Token);
            Console.WriteLine($"[AccountService] HTTP API 监听端口 {port}");
        }

        /// <summary>
        /// 停止 HTTP API 服务
        /// </summary>
        public static void StopHttpApi()
        {
            _cts?.Cancel();
            _httpListener?.Stop();
            _watcher?.Dispose();
            _reloadDebounce?.Dispose();
        }

        // ---- 公开查询接口 ----

        /// <summary>
        /// 昵称是否已注册（服务端 PqCnAuthenLogin 校验用）
        /// </summary>
        public static bool IsNicknameRegistered(string nickname)
        {
            if (!Enabled) return true; // 认证关闭时全部放行
            if (string.IsNullOrEmpty(nickname)) return false;
            lock (_lock)
            {
                return _store.Accounts.Values.Any(a => !string.IsNullOrEmpty(a.Nickname) && a.Nickname == nickname);
            }
        }

        /// <summary>
        /// 验证 token 是否有效，返回绑定的昵称（null 表示无效）
        /// </summary>
        public static string ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (_tokens.TryGetValue(token, out string username))
            {
                if (_tokenCreatedAt.TryGetValue(token, out DateTime createdAt))
                {
                    if (DateTime.UtcNow - createdAt > TokenExpiry)
                    {
                        _tokens.TryRemove(token, out _);
                        _tokenCreatedAt.TryRemove(token, out _);
                        return null;
                    }
                }
                lock (_lock)
                {
                    if (_store.Accounts.TryGetValue(username, out var acc))
                    {
                        return acc.Nickname;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 验证 token 并返回用户名（用于创角等操作）
        /// </summary>
        public static string ValidateTokenGetUsername(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (_tokens.TryGetValue(token, out string username))
            {
                if (_tokenCreatedAt.TryGetValue(token, out DateTime createdAt))
                {
                    if (DateTime.UtcNow - createdAt > TokenExpiry)
                    {
                        _tokens.TryRemove(token, out _);
                        _tokenCreatedAt.TryRemove(token, out _);
                        return null;
                    }
                }
                return username;
            }
            return null;
        }

        // ---- 注册（昵称可选，支持先注册后创角）----

        public static (bool ok, string msg, AccountData account) Register(string username, string password, string nickname = null)
        {
            if (string.IsNullOrWhiteSpace(username))
                return (false, "账号名不能为空", null);
            if (string.IsNullOrWhiteSpace(password))
                return (false, "密码不能为空", null);
            if (username.Length > 32)
                return (false, "账号名最长32字符", null);
            if (password.Length < 4)
                return (false, "密码至少4位", null);

            // 昵称可选：提供时校验，不提供时留空（后续通过 /create-character 绑定）
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                if (nickname.Length > 16)
                    return (false, "昵称最长16字符", null);
            }

            string key = username.ToLowerInvariant();

            lock (_lock)
            {
                if (_store.Accounts.ContainsKey(key))
                    return (false, "账号已存在", null);

                // 昵称唯一性检查（仅当提供了昵称时）
                if (!string.IsNullOrWhiteSpace(nickname))
                {
                    if (_store.Accounts.Values.Any(a => !string.IsNullOrEmpty(a.Nickname) && a.Nickname == nickname))
                        return (false, "该昵称已被其他账号绑定", null);
                }

                string salt = GenerateSalt();
                string hash = HashPassword(password, salt);

                var account = new AccountData
                {
                    Username = key,
                    PasswordHash = hash,
                    Salt = salt,
                    Nickname = string.IsNullOrWhiteSpace(nickname) ? "" : nickname,
                    CreatedAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };

                _store.Accounts[key] = account;
                Save();
                return (true, "注册成功", account);
            }
        }

        // ---- 登录 ----

        public static (bool ok, string msg, string token, string nickname, bool hasNickname) Login(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return (false, "账号和密码不能为空", null, null, false);

            string key = username.ToLowerInvariant();

            lock (_lock)
            {
                if (!_store.Accounts.TryGetValue(key, out var account))
                    return (false, "账号不存在", null, null, false);

                string hash = HashPassword(password, account.Salt);
                if (hash != account.PasswordHash)
                    return (false, "密码错误", null, null, false);

                // 生成 token
                string token = GenerateToken();
                _tokens[token] = key;
                _tokenCreatedAt[token] = DateTime.UtcNow;

                // 清理过期 token
                CleanupExpiredTokens();

                account.LastLoginAt = DateTime.UtcNow;
                Save();

                bool hasNick = !string.IsNullOrEmpty(account.Nickname);
                return (true, "登录成功", token, account.Nickname ?? "", hasNick);
            }
        }

        // ---- 创建角色（绑定昵称到已有账号）----

        public static (bool ok, string msg) CreateCharacter(string token, string nickname)
        {
            if (string.IsNullOrWhiteSpace(nickname))
                return (false, "角色名不能为空");
            if (nickname.Length > 16)
                return (false, "角色名最长16字符");

            string username = ValidateTokenGetUsername(token);
            if (string.IsNullOrEmpty(username))
                return (false, "登录已过期，请重新登录");

            lock (_lock)
            {
                if (!_store.Accounts.TryGetValue(username, out var account))
                    return (false, "账号不存在");

                if (!string.IsNullOrEmpty(account.Nickname))
                    return (false, "该账号已有角色");

                // 昵称唯一性检查
                if (_store.Accounts.Values.Any(a => !string.IsNullOrEmpty(a.Nickname) && a.Nickname == nickname))
                    return (false, "该角色名已被使用");

                account.Nickname = nickname;
                Save();
                Console.WriteLine($"[AccountService] 账号 {username} 创建角色: {nickname}");
                return (true, "角色创建成功");
            }
        }

        // ---- HTTP API ----

        private static async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = HandleRequestAsync(context, ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AccountService] HTTP 监听异常: {ex.Message}");
                }
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            // CORS
            resp.Headers.Add("Access-Control-Allow-Origin", "*");
            resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            string path = req.Url.AbsolutePath.ToLowerInvariant();
            string body = null;
            if (req.HasEntityBody)
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                body = await reader.ReadToEndAsync();
            }

            try
            {
                string result;
                switch (path)
                {
                    case "/register":
                        result = HandleRegister(body);
                        break;

                    case "/login":
                        result = HandleLogin(body);
                        break;

                    case "/create-character":
                        result = HandleCreateCharacter(body);
                        break;

                    case "/check":
                        result = HandleCheck(req);
                        break;

                    case "/status":
                        result = HandleStatus();
                        break;

                    default:
                        resp.StatusCode = 404;
                        result = JsonSerializer.Serialize(new { ok = false, msg = "未知接口" });
                        break;
                }

                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";
                byte[] buf = Encoding.UTF8.GetBytes(result);
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AccountService] API 处理异常: {ex.Message}");
                resp.StatusCode = 500;
                byte[] errBuf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, msg = "服务器内部错误" }));
                resp.ContentLength64 = errBuf.Length;
                await resp.OutputStream.WriteAsync(errBuf, ct);
            }
            finally
            {
                resp.Close();
            }
        }

        private static string HandleRegister(string body)
        {
            var data = JsonSerializer.Deserialize<RegisterRequest>(body);
            if (data == null)
                return JsonSerializer.Serialize(new { ok = false, msg = "请求格式错误" });

            var (ok, msg, account) = Register(data.Username, data.Password, data.Nickname);
            if (ok)
            {
                bool hasNick = !string.IsNullOrEmpty(account.Nickname);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    msg,
                    username = account.Username,
                    nickname = account.Nickname ?? "",
                    hasNickname = hasNick
                });
            }
            return JsonSerializer.Serialize(new { ok = false, msg });
        }

        private static string HandleLogin(string body)
        {
            var data = JsonSerializer.Deserialize<LoginRequest>(body);
            if (data == null)
                return JsonSerializer.Serialize(new { ok = false, msg = "请求格式错误" });

            var (ok, msg, token, nickname, hasNickname) = Login(data.Username, data.Password);
            if (ok)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    msg,
                    token,
                    nickname,
                    hasNickname
                });
            }
            return JsonSerializer.Serialize(new { ok = false, msg });
        }

        private static string HandleCreateCharacter(string body)
        {
            var data = JsonSerializer.Deserialize<CreateCharacterRequest>(body);
            if (data == null)
                return JsonSerializer.Serialize(new { ok = false, msg = "请求格式错误" });

            var (ok, msg) = CreateCharacter(data.Token, data.Nickname);
            return JsonSerializer.Serialize(new { ok, msg });
        }

        private static string HandleCheck(HttpListenerRequest req)
        {
            string nickname = req.QueryString["nickname"];
            if (string.IsNullOrEmpty(nickname))
                return JsonSerializer.Serialize(new { ok = false, msg = "缺少 nickname 参数" });

            bool registered = IsNicknameRegistered(nickname);
            return JsonSerializer.Serialize(new { ok = true, registered, nickname });
        }

        private static string HandleStatus()
        {
            int accountCount;
            int onlineCount;
            lock (_lock)
            {
                accountCount = _store.Accounts.Count;
            }
            onlineCount = ClientManager.GetOnlinePlayers().Count;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                enabled = Enabled,
                accountCount,
                onlineCount
            });
        }

        // ---- 持久化 ----

        private static void Load()
        {
            if (!File.Exists(_accountsPath))
            {
                _store = new AccountStore();
                Save();
                return;
            }

            try
            {
                string json = File.ReadAllText(_accountsPath, Encoding.UTF8);
                _store = JsonSerializer.Deserialize<AccountStore>(json) ?? new AccountStore();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AccountService] 加载 accounts.json 失败: {ex.Message}，使用空数据");
                _store = new AccountStore();
            }
        }

        /// <summary>
        /// 启动文件监视器，accounts.json 被外部修改时自动重新加载
        /// </summary>
        private static void StartWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(_accountsPath);
                string filename = Path.GetFileName(_accountsPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                _reloadDebounce = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        Load();
                        Console.WriteLine($"[AccountService] accounts.json 已重新加载（{_store.Accounts.Count} 个账号）");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AccountService] 重新加载 accounts.json 失败: {ex.Message}");
                    }
                }, null, Timeout.Infinite, Timeout.Infinite);

                _watcher = new FileSystemWatcher(dir, filename)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += (s, e) =>
                {
                    if (_skipWatcherReload)
                    {
                        _skipWatcherReload = false;
                        return;
                    }
                    // 防抖：500ms 内多次变更只触发一次重载
                    _reloadDebounce.Change(500, Timeout.Infinite);
                };
                _watcher.EnableRaisingEvents = true;
                Console.WriteLine($"[AccountService] 文件监视已启动（{filename}）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AccountService] 文件监视启动失败: {ex.Message}，外部修改需重启生效");
            }
        }

        private static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_accountsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _skipWatcherReload = true;
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_store, options);
                File.WriteAllText(_accountsPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AccountService] 保存 accounts.json 失败: {ex.Message}");
            }
        }

        // ---- 工具方法 ----

        private static string GenerateSalt()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(16);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string HashPassword(string password, string salt)
        {
            byte[] input = Encoding.UTF8.GetBytes(password + salt);
            byte[] hash = SHA256.HashData(input);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string GenerateToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static void CleanupExpiredTokens()
        {
            foreach (var kv in _tokenCreatedAt)
            {
                if (DateTime.UtcNow - kv.Value > TokenExpiry)
                {
                    _tokens.TryRemove(kv.Key, out _);
                    _tokenCreatedAt.TryRemove(kv.Key, out _);
                }
            }
        }

        // ---- 请求 DTO ----

        private class RegisterRequest
        {
            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("password")]
            public string Password { get; set; }

            [JsonPropertyName("nickname")]
            public string Nickname { get; set; }
        }

        private class LoginRequest
        {
            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("password")]
            public string Password { get; set; }
        }

        private class CreateCharacterRequest
        {
            [JsonPropertyName("token")]
            public string Token { get; set; }

            [JsonPropertyName("nickname")]
            public string Nickname { get; set; }
        }
    }
}