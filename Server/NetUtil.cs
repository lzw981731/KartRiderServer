using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace KartRider
{
    public class IpInfo
    {
        public string Ip { get; set; }
        public string City { get; set; }
        public string Region { get; set; }
        public string Country { get; set; }
    }

    /// <summary>
    /// 轻量网络工具：获取本机公网 IP（原 Launcher_V2 的 Update.GetCountryAsync 精简版，
    /// 去掉 GUI/更新相关逻辑，供服务器端获取对外公网地址广播给玩家）。
    /// </summary>
    public static class NetUtil
    {
        public static async Task<IpInfo> GetCountryAsync()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    HttpResponseMessage response = await client.GetAsync("https://ipinfo.io/json");
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        IpInfo data = JsonSerializer.Deserialize<IpInfo>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return data;
                    }
                    else
                    {
                        Console.WriteLine($"[NetUtil] ipinfo.io 请求失败，状态码: {response.StatusCode}");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetUtil] 获取公网 IP 异常: {ex.Message}");
                return null;
            }
        }
    }
}