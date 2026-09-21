using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using KartLibrary.File;
using Profile;

namespace KartRider;

public class GameRoom
{
    // 房间唯一ID（创建后不可修改）
    public int RoomId { get; }
    public string RoomName { get; set; } = "";
    public uint track { get; set; } = 0;
    public uint trackTemp { get; set; } = 0;
    public uint StartTicks { get; set; } = 0;
    public uint EndTicks { get; set; } = 0;
    public byte SpeedType { get; set; } = 0;
    public byte GameType { get; set; } = 0;
    public int RoomMaster { get; set; } = 0;
    public byte[] RoomData { get; set; } = new byte[32];
    public byte RandomTrackGameType { get; set; } = 0;
    public float redGauge { get; set; } = 0;
    public float blueGauge { get; set; } = 0;
    public bool Lock { get; set; } = false;
    public bool Started { get; set; } = false;
    public int StartedPlayerCount { get; set; } = 0; // 游戏开始时的真实玩家数（不包含AI），用于失效赛道判定
    public string LockPwd { get; set; } = "";
    public List<byte> CloseSlotIds { get; set; } = new List<byte>();
    public Dictionary<int, uint> TimeData { get; set; } = new Dictionary<int, uint>();
    public Dictionary<int, int> Ranking { get; set; } = new Dictionary<int, int>();
    public Dictionary<string, bool> Ready { get; set; } = new Dictionary<string, bool>();

    // 玩家实时赛道跟踪：id -> 坐标/圈数/累计里程（道具赛位置包走TCP逐连接填充）
    public Dictionary<int, TrackPos> Tracks { get; set; } = new Dictionary<int, TrackPos>();

    // 起点区判定（起跑线附近小区域）。
    // 旧条件 y<200 在该赛道全程不满足（y 最小 286），导致过线检测完全失效。
    private static bool IsStartZone(float x, float y) =>
        x >= 400f && x <= 520f && y >= 550f && y <= 650f;

    // 更新玩家位置跟踪：累计里程、检测过线圈数
    public void UpdateTrack(int id, float x, float y, uint ts1)
    {
        if (!Tracks.TryGetValue(id, out TrackPos t))
        {
            // 首次采样：默认在起点区（起跑线附近），圈数从 0 开始
            Tracks[id] = new TrackPos
            {
                X = x,
                Y = y,
                Ts = ts1,
                InStartZone = IsStartZone(x, y)
            };
            return;
        }

        // 起点区判定提前：起点区内（开局倒车/原地微调）不累计里程、不建立方向参考
        bool inStart = IsStartZone(x, y);

        // ts1 不连续（断线/重连/时钟域跳变）：不累计位移，防止坐标跳变污染里程；
        // 无时间戳包(ts1==0)沿用上次时间戳，视为连续采样，避免与有时间戳包交替上报时误判断线
        uint effTs = ts1 == 0 ? t.Ts : ts1;
        bool gap = !t.HasPrev || effTs < t.Ts || effTs - t.Ts > 30000;
        if (!gap)
        {
            float dx = x - t.X;
            float dy = y - t.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist > 0.001f) // 忽略原地不动
            {
                if (!t.HasDir)
                {
                    if (!inStart)
                    {
                        // 已离开起点区的首段位移：累计并建立前进方向参考，
                        // 避免开局倒车把方向基准建反（倒车方向留在起点区内，被 inStart 拦截）
                        t.TotalDist += dist;
                        t.DirX = dx / dist;
                        t.DirY = dy / dist;
                        t.HasDir = true;
                    }
                }
                else
                {
                    // 投影到前进方向：正向位移（proj>=0）按投影计入里程并平滑跟随赛道转向；
                    // 反向位移（proj<0，掉头/发卡弯/倒车）不累计也不扣减，仅让方向跟随新位移。
                    // 旧逻辑反向时保持原方向且按投影扣减，发卡弯（>90° 转向）下方向卡死、
                    // 里程被反复扣减到 0（日志实测：真实路径 3294，旧逻辑只记 115）。
                    float proj = dx * t.DirX + dy * t.DirY;
                    if (proj >= 0f)
                    {
                        t.TotalDist += proj;
                        // 平滑更新前进方向（当前位移与旧方向各半归一），跟随赛道转弯
                        float mixX = t.DirX + dx / dist;
                        float mixY = t.DirY + dy / dist;
                        float ml = (float)Math.Sqrt(mixX * mixX + mixY * mixY);
                        if (ml > 0.001f)
                        {
                            t.DirX = mixX / ml;
                            t.DirY = mixY / ml;
                        }
                    }
                    else
                    {
                        t.DirX = dx / dist;
                        t.DirY = dy / dist;
                    }
                }
            }
        }

        // 圈数：离开起点区后再次进入起点区 = 过线，圈数 +1
        if (t.HasPrev && !t.InStartZone && inStart)
        {
            t.Lap++;
            if (t.DistAtLap > 0)
            {
                float lapLen = t.TotalDist - t.DistAtLap;
                if (lapLen > 200f) // 过滤异常短圈（误检）
                {
                    t.LapLength = lapLen; // 记录最近一圈长度，用于距离分档归一化
                }
            }
            t.DistAtLap = t.TotalDist;
        }

        t.HasPrev = true;
        t.InStartZone = inStart;
        t.X = x;
        t.Y = y;
        if (ts1 != 0)
        {
            t.Ts = ts1; // 无时间戳包不覆盖，保持上次有效时间戳
        }
    }

    // 结束时深拷贝快照，防止结算期间玩家断线
    public RoomMember[] SnapshotMembers { get; set; }

    // 8个格子（0-7）
    public RoomMember[] _slots = new RoomMember[8];
    public RoomMember[] _IDs = new RoomMember[8];

    public RoomMember[] ObIDs = new RoomMember[8];

    // 固定的 ObID 映射：槽位 0-7 对应 ID 8-15
    private static readonly int[] FixedObIds = { 8, 9, 10, 11, 12, 13, 14, 15 };

    // 构造函数：初始化房间ID（由外部传入唯一ID）
    public GameRoom(int roomId)
    {
        RoomId = roomId; // 房间ID创建后固定不变
    }

    public int GetOBCount()
    {
        int count = 0;
        foreach (var member in ObIDs)
        {
            if (member is Player) // 仅统计玩家类型
                count++;
        }
        return count;
    }

    // 统计当前房间内的玩家数量（不包含AI）
    public int GetPlayerCount(byte team = 0)
    {
        int count = 0;
        foreach (var member in _slots)
        {
            if (member is Player && team == 0) // 仅统计玩家类型
                count++;
            else if (member is Player player && player.Team == team)
                count++;
        }
        return count;
    }

    // 统计当前房间内的Ai数量
    public int GetAiCount()
    {
        int count = 0;
        foreach (var member in _slots)
        {
            if (member is Ai)
                count++;
        }
        return count;
    }

    // 统计当前房间内的玩家数量
    public int GetCount()
    {
        int count = 0;
        foreach (var member in _slots)
        {
            if (member is Player || member is Ai)
                count++;
        }
        return count;
    }

    // 清理并重整排名：删除已离开的玩家，排名靠后的往前移
    public void CleanupRankings()
    {
        // 获取房间内所有当前玩家的ID
        var currentPlayerIds = _IDs
            .Where(m => m is Player)
            .Select(m => ((Player)m).ID)
            .ToList();

        // 删除排名中已离开的玩家
        var keysToRemove = Ranking.Keys.Where(k => !currentPlayerIds.Contains(k)).ToList();
        foreach (var key in keysToRemove)
        {
            Ranking.Remove(key);
        }

        // 重整排名，使其连续 (0, 1, 2, ...)
        var sortedRankings = Ranking.OrderBy(kvp => kvp.Value).ToList();
        for (int i = 0; i < sortedRankings.Count; i++)
        {
            Ranking[sortedRankings[i].Key] = i;
        }
    }

    public SlotStatus GetSlotStatus(byte slotId)
    {
        var member = _slots[slotId];
        if (member == null)
            return SlotStatus.Empty;       // 空位置
        if (member is Player)
            return SlotStatus.Player;      // 玩家
        if (member is Ai)
            return SlotStatus.Ai;          // AI
        if (member is Close)
            return SlotStatus.Close;
        return SlotStatus.Empty;           // 理论上不会走到这里（基类不会直接实例化）
    }

    // 辅助方法：获取指定位置的具体成员（可用于获取详细信息）
    public RoomMember GetSlotMember(byte slotId)
    {
        return _slots[slotId];
    }

    public RoomMember GetIdMember(int Id)
    {
        return _IDs[Id];
    }

    // 尝试添加玩家（成功后自动检查是否需要删除房间）
    public byte TryAddPlayer(string nickname, byte team, int playerType, SessionGroup client)
    {
        uint pmap = ProfileService.GetProfileConfig(nickname).Rider.pmap;
        if (pmap == 718 || pmap == 590)
        {
            for (byte i = 0; i < 8; i++)
            {
                if (ObIDs[i] == null)
                {
                    ObIDs[i] = new Player
                    {
                        ID = FixedObIds[i],
                        SlotId = i,
                        Nickname = nickname,
                        PlayerType = 4,
                        Session = client
                    };
                    return i;
                }
            }
            return 255;
        }
        else if (team == 2)
        {
            for (byte i = 0; i < 4; i++)
            {
                if (_slots[i] == null)
                {
                    int id = Array.IndexOf(_IDs, null);
                    _slots[i] = new Player
                    {
                        ID = id,
                        SlotId = i,
                        Nickname = nickname,
                        PlayerType = playerType,
                        Team = team,
                        Session = client
                    };
                    _IDs[id] = _slots[i];
                    return i;
                }
            }
            return 255; // 房间已满
        }
        else if (team == 1)
        {
            for (byte i = 4; i < 8; i++)
            {
                if (_slots[i] == null)
                {
                    int id = Array.IndexOf(_IDs, null);
                    _slots[i] = new Player
                    {
                        ID = id,
                        SlotId = i,
                        Nickname = nickname,
                        PlayerType = playerType,
                        Team = team,
                        Session = client
                    };
                    _IDs[id] = _slots[i];
                    return i;
                }
            }
            return 255; // 房间已满
        }
        else if (team == 0)
        {
            for (byte i = 0; i < 8; i++)
            {
                if (_slots[i] == null)
                {
                    int id = Array.IndexOf(_IDs, null);
                    _slots[i] = new Player
                    {
                        ID = id,
                        SlotId = i,
                        Nickname = nickname,
                        PlayerType = playerType,
                        Team = team,
                        Session = client
                    };
                    _IDs[id] = _slots[i];
                    return i;
                }
            }
            return 255; // 房间已满
        }
        else
        {
            return 255; // 未知队伍
        }
    }

    // 移除指定格子的成员（如果是玩家，需检查是否触发删除）
    public bool RemoveMember(byte slotId, string nickname)
    {
        if (!string.IsNullOrEmpty(nickname))
        {
            uint pmap = ProfileService.GetProfileConfig(nickname).Rider.pmap;
            if (pmap == 718 || pmap == 590)
            {
                if (ObIDs[slotId] is Player p1)
                {
                    if (p1.ID == RoomMaster)
                    {
                        // 房主离开：将房主转移给其他仍在房间的玩家（跳过离开者自身）
                        bool transferred = false;
                        foreach (RoomMember member in _IDs)
                        {
                            if (member is Player p2 && p2.ID != p1.ID)
                            {
                                RoomMaster = p2.ID;
                                p2.PlayerType = 2;
                                transferred = true;
                                break;
                            }
                        }
                        if (!transferred)
                        {
                            // 房间内已无其他玩家，房间随后会被删除
                            RoomMaster = p1.ID;
                        }
                    }
                    ObIDs[slotId] = null;
                    RoomManager.RemoveRoom(this);
                    return true;
                }
                return false;
            }
            else
            {
                RoomMember removedMember = _slots[slotId];
                if (removedMember == null)
                    return false; // 格子已为空

                if (removedMember is Player player)
                {
                    if (player.ID == RoomMaster)
                    {
                        // 房主离开：将房主转移给其他仍在房间的玩家（跳过离开者自身，
                        // 否则 _IDs 中第一个 Player 就是离开的房主自己，导致房间失去房主）
                        bool transferred = false;
                        foreach (RoomMember member in _IDs)
                        {
                            if (member is Player p && p.ID != player.ID)
                            {
                                RoomMaster = p.ID;
                                p.PlayerType = 2;
                                transferred = true;
                                break;
                            }
                        }
                        if (!transferred)
                        {
                            // 房间内已无其他玩家，房间随后会被删除
                            RoomMaster = player.ID;
                        }
                    }
                    _IDs[player.ID] = null;
                    _slots[slotId] = null;
                    RoomManager.RemoveRoom(this);
                    return true;
                }
            }
        }
        else
        {
            // nickname为空时处理AI移除
            RoomMember removedMember = _slots[slotId];
            if (removedMember == null)
                return false;

            if (removedMember is Ai ai)
            {
                _IDs[ai.ID] = null;
                _slots[slotId] = null;
                MultyPlayer.GrSlotDataPacket(RoomId);
                return true;
            }
        }
        return false;
    }

    // 其他方法：设置AI、获取格子信息等（沿用之前的逻辑，略）
    public byte TrySetAi(Ai aiData, byte team)
    {
        if (aiData == null)
            return 255;

        if (team == 2)
        {
            for (byte i = 0; i < 4; i++)
            {
                if (_slots[i] == null)
                {
                    int id = Array.IndexOf(_IDs, null);
                    aiData.ID = id;
                    aiData.Team = team;
                    _slots[i] = aiData;
                    _slots[i].SlotId = i;
                    _IDs[id] = _slots[i];
                    return i;
                }
            }
            return 255; // 房间已满
        }
        else if (team == 1)
        {
            for (byte i = 4; i < 8; i++)
            {
                if (_slots[i] == null)
                {
                    int id = Array.IndexOf(_IDs, null);
                    aiData.ID = id;
                    aiData.Team = team;
                    _slots[i] = aiData;
                    _slots[i].SlotId = i;
                    _IDs[id] = _slots[i];
                    return i;
                }
            }
            return 255; // 房间已满
        }
        else if (team == 0)
        {
            for (byte i = 0; i < 8; i++)
            {
                if (_slots[i] == null)
                {
                    int id = Array.IndexOf(_IDs, null);
                    aiData.ID = id;
                    aiData.Team = team;
                    _slots[i] = aiData;
                    _slots[i].SlotId = i;
                    _IDs[id] = _slots[i];
                    return i;
                }
            }
            return 255; // 房间已满
        }
        else
        {
            return 255; // 未知队伍
        }
    }

    public bool ChangeSlotId(byte slotId, byte newSlotId)
    {
        if (_slots[newSlotId] != null)
            return false;

        _slots[newSlotId] = _slots[slotId];
        _slots[slotId] = null;
        return true;
    }

    public bool AddClose(byte slotId, int ID)
    {
        if (_slots[slotId] != null)
            return false;

        if (_IDs[ID] != null)
            return false;

        Close close = new Close();
        close.ID = ID;
        close.PlayerType = 1;
        _slots[slotId] = close;
        _IDs[ID] = close;
        CloseSlotIds.Add(slotId);
        return true;
    }

    public bool RemoveClose(byte slotId, int ID)
    {
        if (_slots[slotId] == null)
            return false;

        if (_IDs[ID] == null)
            return false;

        if (_slots[slotId] is Close && _IDs[ID] is Close)
        {
            _slots[slotId] = null;
            _IDs[ID] = null;
            CloseSlotIds.Remove(slotId);
            return true;
        }
        return false;
    }
}

// 房间成员基类
public abstract class RoomMember
{
    public byte SlotId { get; set; } // 格子ID（0-7）
}

// 玩家类
public class Player : RoomMember
{
    public int ID { get; set; }
    public string Nickname { get; set; } // 玩家昵称
    public int PlayerType { get; set; } // 玩家类型
    public byte Team { get; set; }
    public SessionGroup Session { get; set; }
    public uint LastPacketReceived { get; set; } = 0;
}

// AI类
public class Ai : RoomMember
{
    public int ID { get; set; }
    public short Character { get; set; }
    public short Rid { get; set; }
    public short Kart { get; set; }
    public short Balloon { get; set; }
    public short HeadBand { get; set; }
    public short Goggle { get; set; }
    public byte Team { get; set; }
}

public class Close : RoomMember
{
    public int ID { get; set; }
    public int PlayerType { get; set; }
}

// 玩家实时赛道跟踪数据：坐标、圈数、累计里程
public class TrackPos
{
    public float X { get; set; }          // 最近一次主向坐标 f1
    public float Y { get; set; }          // 最近一次横向坐标 f2
    public uint Ts { get; set; }          // 最近一次时间戳 ts1
    public bool HasPrev { get; set; }     // 是否有上一采样点
    public bool InStartZone { get; set; } // 上一采样是否在起点区
    public int Lap { get; set; }          // 已完成的圈数
    public float TotalDist { get; set; }  // 累计路径距离（里程）
    public float DistAtLap { get; set; }  // 过线时的累计距离（用于估算单圈长度）
    public float LapLength { get; set; }  // 最近一圈长度
    public bool HasDir { get; set; }      // 是否已建立前进方向参考
    public float DirX { get; set; }       // 前进方向单位向量 X
    public float DirY { get; set; }       // 前进方向单位向量 Y
}

public enum SlotStatus
{
    Empty,    // 空位置
    Player,   // 玩家
    Ai,       // AI
    Close     // 关闭
}
