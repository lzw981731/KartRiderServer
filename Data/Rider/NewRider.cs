using System;
using System.Collections.Generic;
using System.IO;
using KartRider.IO.Packet;
using KartRider;
using ExcData;
using System.Linq;
using Profile;

namespace RiderData
{
    public static class NewRider
    {
        public static Dictionary<ushort, Dictionary<ushort, string>> items = new Dictionary<ushort, Dictionary<ushort, string>>();
        private static readonly HashSet<ushort> excludedKeys = new HashSet<ushort>{ 3, 6, 10, 15, 19, 24, 25, 29, 33, 34, 35, 40, 41, 47, 48, 50, 51, 56, 57, 58, 60, 62, 63, 64, 65, 66, 72, 73, 74, 75 };
        private static readonly HashSet<ushort> ValidItemCatIds = new HashSet<ushort> { 1, 2, 4, 8, 11, 12, 13, 14, 16, 18, 20, 21, 26, 27, 28, 31, 52, 61, 70, 71 };

        public static void LoadItemData(SessionGroup Parent, string Nickname)
        {
            KartExcData.Tune_ExcData(Parent, Nickname);
            KartExcData.Plant_ExcData(Parent, Nickname);
            KartExcData.Level_ExcData(Parent, Nickname);
            KartExcData.Parts_ExcData(Parent, Nickname);
            KartExcData.Level12_ExcData(Parent, Nickname);
            KartExcData.Parts12_ExcData(Parent, Nickname);
            NewRider.XUniquePartsData(Parent, Nickname);
            NewRider.XLegendPartsData(Parent, Nickname);
            NewRider.XRarePartsData(Parent, Nickname);
            NewRider.XNormalPartsData(Parent, Nickname);
            NewRider.V1UniquePartsData(Parent, Nickname);
            NewRider.V1LegendPartsData(Parent, Nickname);
            NewRider.V1RarePartsData(Parent, Nickname);
            NewRider.V1NormalPartsData(Parent, Nickname);
            NewRider.partsEngine12(Parent, Nickname);
            NewRider.partsHandle12(Parent, Nickname);
            NewRider.partsWheel12(Parent, Nickname);
            NewRider.partsBooster12(Parent, Nickname);
            NewRider.Items(Parent, Nickname);
            NewRider.NewKart1(Parent);
            NewRider.NewKart2(Parent);
            NewRider.NewItem(Parent);
            NewRider.NewRiderData(Parent);//라이더 인식
        }

        public static void NewRiderData(SessionGroup Parent)
        {
            string Nickname = Parent.Client.Nickname;
            var riderDataConfig = ProfileService.GetProfileConfig(Nickname);
            if (riderDataConfig?.Rider == null)
            {
                Console.WriteLine("[NewRiderData] Warning: ProfileConfig or Rider is null for {0}", Nickname);
                return;
            }
            using (OutPacket oPacket = new OutPacket("PrGetRider"))
            {
                oPacket.WriteByte(1);
                oPacket.WriteByte(0);
                oPacket.WriteString(Nickname);
                oPacket.WriteShort(0);
                oPacket.WriteShort(0);
                oPacket.WriteShort(riderDataConfig.Rider.Emblem1);
                oPacket.WriteShort(riderDataConfig.Rider.Emblem2);
                oPacket.WriteShort(0);
                GameSupport.GetRider(Nickname, oPacket);
                oPacket.WriteString(riderDataConfig.Rider.Card);
                oPacket.WriteUInt(riderDataConfig.Rider.Lucci);
                oPacket.WriteUInt(riderDataConfig.Rider.RP);
                oPacket.WriteBytes(new byte[94]);
                oPacket.WriteBytes(new byte[215]);
                Parent.Client.Send(oPacket);
            }
        }

        public static void NewKart1(SessionGroup Parent)
        {
            ushort sn = 1;
            int range = 100;//分批次数
            // 购买模式：车辆列表改为"已购"（NewKart.json 永久车 + NewItem.json 限时车 itemCatId=3），
            // 包结构保持 PrRequestKartInfoPacket 不变，客户端不崩，且不再显示未拥有车辆。
            string Nickname = Parent.Client.Nickname;
            if (!FileName.FileNames.ContainsKey(Nickname))
            {
                FileName.Load(Nickname);
            }
            var filename = FileName.FileNames[Nickname];
            var ownedKartIds = new List<ushort>();
            var ownedKartSNs = new Dictionary<ushort, ushort>();
            if (File.Exists(filename.NewKart_LoadFile))
            {
                var newkart = JsonHelper.DeserializeNoBom<List<NewKart>>(filename.NewKart_LoadFile) ?? new List<NewKart>();
                foreach (var k in newkart)
                {
                    ownedKartIds.Add(k.KartID);
                    ownedKartSNs[k.KartID] = k.KartSN;
                }
            }
            if (File.Exists(filename.NewItem_LoadFile))
            {
                var newitem = Stock.LoadNewItem(filename);
                foreach (var item in newitem)
                {
                    if (item.itemCatId == 3 && !ownedKartIds.Contains(item.itemId))
                    {
                        ownedKartIds.Add(item.itemId);
                        ownedKartSNs[item.itemId] = item.itemSn;
                    }
                }
            }
            List<ushort> kart = ownedKartIds;
            int times = kart.Count / range + (kart.Count % range > 0 ? 1 : 0);
            if (times == 0) times = 1; // 空车库也发一个空包，避免客户端等待车辆列表超时
            for (int i = 0; i < times; i++)
            {
                var tempList = kart.GetRange(i * range, (i + 1) * range > kart.Count ? (kart.Count - i * range) : range);
                int Count = tempList.Count;
                using (OutPacket outPacket = new OutPacket("PrRequestKartInfoPacket"))
                {
                    outPacket.WriteByte(1);
                    outPacket.WriteInt(Count);
                    foreach (var Kart in tempList)
                    {
                        outPacket.WriteShort(3);
                        outPacket.WriteUShort(Kart);
                        outPacket.WriteUShort(ownedKartSNs.TryGetValue(Kart, out var ksn) ? ksn : sn);
                        outPacket.WriteUShort(1);//数量
                        outPacket.WriteShort(0);
                        outPacket.WriteShort(-1);
                        outPacket.WriteShort(0);
                        outPacket.WriteShort(0);
                        outPacket.WriteShort(0);
                    }
                    Parent.Client.Send(outPacket);
                }
            }
        }

        public static void NewKart2(SessionGroup Parent)
        {
            string Nickname = Parent.Client.Nickname;
            if (!FileName.FileNames.ContainsKey(Nickname))
            {
                FileName.Load(Nickname);
            }
            var filename = FileName.FileNames[Nickname];
            var newkart = new List<NewKart>();
            if (File.Exists(filename.NewKart_LoadFile))
            {
                newkart = JsonHelper.DeserializeNoBom<List<NewKart>>(filename.NewKart_LoadFile) ?? new List<NewKart>();
            }

            int range = 100;//分批次数
            int times = newkart.Count / range + (newkart.Count % range > 0 ? 1 : 0);
            for (int i = 0; i < times; i++)
            {
                var tempList = newkart.GetRange(i * range, (i + 1) * range > newkart.Count ? (newkart.Count - i * range) : range);
                int Count = tempList.Count;
                using (OutPacket outPacket = new OutPacket("PrRequestKartInfoPacket"))
                {
                    outPacket.WriteByte(1);
                    outPacket.WriteInt(Count);
                    foreach (var Kart in tempList)
                    {
                        outPacket.WriteShort(3);
                        outPacket.WriteUShort(Kart.KartID);
                        outPacket.WriteUShort(Kart.KartSN);
                        outPacket.WriteUShort(1);//数量
                        outPacket.WriteShort(0);
                        outPacket.WriteShort(-1);
                        outPacket.WriteShort(0);
                        outPacket.WriteShort(0);
                        outPacket.WriteShort(0);
                    }
                    Parent.Client.Send(outPacket);
                }
            }
        }

        public static void NewItem(SessionGroup Parent)
        {
            string Nickname = Parent.Client.Nickname;
            if (!FileName.FileNames.ContainsKey(Nickname))
            {
                FileName.Load(Nickname);
            }
            var newitem = Stock.DelExpiredNewItem(Nickname);//加载并清除已到期的道具

            int range = 100;//分批次数
            int times = newitem.Count / range + (newitem.Count % range > 0 ? 1 : 0);
            for (int i = 0; i < times; i++)
            {
                var tempList = newitem.GetRange(i * range, (i + 1) * range > newitem.Count ? (newitem.Count - i * range) : range);
                int Count = tempList.Count;
                using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
                {
                    oPacket.WriteInt(times);
                    oPacket.WriteInt(i + 1);
                    oPacket.WriteInt(Count);
                    foreach (var item in tempList)
                    {
                        oPacket.WriteUShort(item.itemCatId);
                        oPacket.WriteUShort(item.itemId);
                        oPacket.WriteUShort(item.itemSn);
                        oPacket.WriteUShort(item.itemCount);
                        // 购买模式下已购道具始终可用（PreventItem=0），非购买模式沿用全局开关
                        oPacket.WriteByte((byte)(ServerConf.Current.PurchaseOnlyMode ? 0 : (Program.PreventItem ? 1 : 0)));
                        oPacket.WriteByte(0);
                        oPacket.WriteTime(item.endTime);
                        oPacket.WriteByte(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(0);
                    }
                    Parent.Client.Send(oPacket);
                }
            }
        }

        public static void Items(SessionGroup Parent, string Nickname)
        {
            // 购买模式：构建已拥有道具集合，按 (itemCatId, itemId) 精确匹配。
            // 实测目录分类：catId=1角色、3车辆、9气球等；NewItem.json 与目录的 catId 语义一致，
            // 必须按二元组匹配，否则拥有某类别itemId=N会误把另一类别itemId=N也判成已拥有（跨类别串味）。
            // 默认道具白名单（始终视为已拥有）：角色catId=1的黑妞(3)、皮蛋(2)、葱头(4)
            HashSet<(ushort cat, ushort id)> ownedItems = null;
            if (ServerConf.Current.PurchaseOnlyMode)
            {
                ownedItems = new HashSet<(ushort, ushort)>
                {
                    (1, 2), // 皮蛋
                    (1, 3), // 黑妞
                    (1, 4), // 葱头
                };
                if (!FileName.FileNames.ContainsKey(Nickname))
                {
                    FileName.Load(Nickname);
                }
                var filename = FileName.FileNames[Nickname];
                var newitemList = Stock.LoadNewItem(filename);
                foreach (var item in newitemList)
                {
                    ownedItems.Add((item.itemCatId, item.itemId));
                }
                Console.WriteLine($"[PurchaseOnlyMode] {Nickname} 已拥有道具: {ownedItems.Count} 件（含默认角色白名单）");
            }
            foreach (var category in items)
            {
                ushort itemCatId = category.Key;
                if (!excludedKeys.Contains(itemCatId))
                {
                    List<List<ushort>> items = new List<List<ushort>>();
                    foreach (var item in category.Value)
                    {
                        ushort sn = 0;
                        ushort num = ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0;
                        ushort id = item.Key;
                        if (ValidItemCatIds.Contains(itemCatId))
                        {
                            num = 1;
                        }
                        // 购买模式：未拥有的道具数量置 0（客户端据此不显示），
                        // 注意仍然下发该条目（不跳过），否则客户端因缺失目录条目闪退
                        bool isOwned = ownedItems == null || ownedItems.Contains((itemCatId, id));
                        if (ServerConf.Current.PurchaseOnlyMode && !isOwned)
                        {
                            num = 0;
                        }
                        if (itemCatId == 7)
                        {
                            if (id == 3 || id == 4)
                            {
                                List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, (ushort)(isOwned ? 1 : 0) };
                                items.Add(add);
                            }
                            else
                            {
                                List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, num };
                                items.Add(add);
                            }
                        }
                        else if (itemCatId == 14)
                        {
                            if (id == 22 || id == 23 || id == 31 || id == 37 || id == 53 || id == 57 || id == 99)
                            {
                                List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, num };
                                items.Add(add);
                            }
                        }
                        else if (itemCatId == 23)
                        {
                            if (id == 1)
                            {
                                List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, num };
                                items.Add(add);
                            }
                            else if (id == 3 || id == 89 || id == 97 || id == 98 || id == 99 || id == 100 || id == 106)
                            {
                                List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, (ushort)(isOwned ? 1 : 0) };
                                items.Add(add);
                            }
                        }
                        else if (itemCatId == 28)
                        {
                            if (id != 50 && id != 37)
                            {
                                List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, num };
                                items.Add(add);
                            }
                        }
                        else
                        {
                            List<ushort> add = new List<ushort> { (ushort)id, (ushort)sn, num };
                            items.Add(add);
                        }
                    }
                    LoRpGetRiderItemPacket(Parent, itemCatId, items, ownedItems);
                }
            }
        }

        public static void partsEngine12(SessionGroup Parent, string Nickname)
        {
            if (items.TryGetValue(72, out Dictionary<ushort, string> resultDict))
            {
                using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
                {
                    int count = resultDict.Count;
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(count);
                    foreach (var kvp in resultDict)
                    {
                        short id = (short)kvp.Key;
                        oPacket.WriteShort(72);
                        oPacket.WriteShort(id);
                        oPacket.WriteShort(0);
                        oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                        oPacket.WriteByte(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(-1);
                        oPacket.WriteShort(-1);
                        oPacket.WriteByte(1);
                        if (id < 11)
                        {
                            oPacket.WriteByte(4);
                        }
                        else if (id < 21)
                        {
                            oPacket.WriteByte(3);
                        }
                        else if (id < 31)
                        {
                            oPacket.WriteByte(2);
                        }
                        else if (id < 41)
                        {
                            oPacket.WriteByte(1);
                        }
                        oPacket.WriteShort(V2Specs.Get12Parts(id));
                    }
                    Parent.Client.Send(oPacket);
                }
            }
        }

        public static void partsHandle12(SessionGroup Parent, string Nickname)
        {
            if (items.TryGetValue(73, out Dictionary<ushort, string> resultDict))
            {
                using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
                {
                    int count = resultDict.Count;
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(count);
                    foreach (var kvp in resultDict)
                    {
                        short id = (short)kvp.Key;
                        oPacket.WriteShort(73);
                        oPacket.WriteShort(id);
                        oPacket.WriteShort(0);
                        oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                        oPacket.WriteByte(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(-1);
                        oPacket.WriteShort(-1);
                        oPacket.WriteByte(1);
                        if (id < 11)
                        {
                            oPacket.WriteByte(4);
                        }
                        else if (id < 21)
                        {
                            oPacket.WriteByte(3);
                        }
                        else if (id < 31)
                        {
                            oPacket.WriteByte(2);
                        }
                        else if (id < 41)
                        {
                            oPacket.WriteByte(1);
                        }
                        oPacket.WriteShort(V2Specs.Get12Parts(id));
                    }
                    Parent.Client.Send(oPacket);
                }
            }
        }

        public static void partsWheel12(SessionGroup Parent, string Nickname)
        {
            if (items.TryGetValue(74, out Dictionary<ushort, string> resultDict))
            {
                using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
                {
                    int count = resultDict.Count;
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(count);
                    foreach (var kvp in resultDict)
                    {
                        short id = (short)kvp.Key;
                        oPacket.WriteShort(74);
                        oPacket.WriteShort(id);
                        oPacket.WriteShort(0);
                        oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                        oPacket.WriteByte(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(-1);
                        oPacket.WriteShort(-1);
                        oPacket.WriteByte(1);
                        if (id < 11)
                        {
                            oPacket.WriteByte(4);
                        }
                        else if (id < 21)
                        {
                            oPacket.WriteByte(3);
                        }
                        else if (id < 31)
                        {
                            oPacket.WriteByte(2);
                        }
                        else if (id < 41)
                        {
                            oPacket.WriteByte(1);
                        }
                        oPacket.WriteShort(V2Specs.Get12Parts(id));
                    }
                    Parent.Client.Send(oPacket);
                }
            }
        }

        public static void partsBooster12(SessionGroup Parent, string Nickname)
        {
            if (items.TryGetValue(75, out Dictionary<ushort, string> resultDict))
            {
                using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
                {
                    int count = resultDict.Count;
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(1);
                    oPacket.WriteInt(count);
                    foreach (var kvp in resultDict)
                    {
                        short id = (short)kvp.Key;
                        oPacket.WriteShort(75);
                        oPacket.WriteShort(id);
                        oPacket.WriteShort(0);
                        oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                        oPacket.WriteByte(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(-1);
                        oPacket.WriteShort(-1);
                        oPacket.WriteByte(1);
                        if (id < 11)
                        {
                            oPacket.WriteByte(4);
                        }
                        else if (id < 21)
                        {
                            oPacket.WriteByte(3);
                        }
                        else if (id < 31)
                        {
                            oPacket.WriteByte(2);
                        }
                        else if (id < 41)
                        {
                            oPacket.WriteByte(1);
                        }
                        oPacket.WriteShort(V2Specs.Get12Parts(id));
                    }
                    Parent.Client.Send(oPacket);
                }
            }
        }

        public static void XUniquePartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 1;
                //-----------------------------------------------------------------X 유니크 파츠
                for (short i = 1053; i <= 1080; i += 3)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1053; i <= 1080; i += 3)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1053; i <= 1080; i += 3)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1053; i <= 1080; i += 3)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void XLegendPartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 2;
                //-----------------------------------------------------------------X 레전드 파츠
                for (short i = 1005; i <= 1050; i += 5)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1005; i <= 1050; i += 5)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1005; i <= 1050; i += 5)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1005; i <= 1050; i += 5)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void XRarePartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 3;
                //-----------------------------------------------------------------X 레어 파츠
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void XNormalPartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 4;
                //-----------------------------------------------------------------X 일반 파츠
                for (short i = 810; i <= 900; i += 10)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 810; i <= 900; i += 10)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 810; i <= 900; i += 10)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 810; i <= 900; i += 10)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(1);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        //-----------------------------------------------------------------------------------------------V1 파츠 관련
        public static void V1UniquePartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 1;
                //-----------------------------------------------------------------V1 유니크 파츠
                for (short i = 1153; i <= 1180; i += 3)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1053; i <= 1080; i += 3)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1153; i <= 1180; i += 3)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1053; i <= 1080; i += 3)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void V1LegendPartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 2;
                //-----------------------------------------------------------------V1 레전드 파츠
                for (short i = 1105; i <= 1150; i += 5)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1005; i <= 1050; i += 5)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1105; i <= 1150; i += 5)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1005; i <= 1050; i += 5)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void V1RarePartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 3;
                //-----------------------------------------------------------------V1 레어 파츠
                for (short i = 1010; i <= 1100; i += 10)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 1010; i <= 1100; i += 10)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void V1NormalPartsData(SessionGroup Parent, string Nickname)
        {
            using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
            {
                oPacket.WriteInt(1);
                oPacket.WriteInt(1);
                oPacket.WriteInt(40);
                byte Grade = 4;
                //-----------------------------------------------------------------V1 일반 파츠
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(63);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 810; i <= 900; i += 10)
                {
                    oPacket.WriteShort(64);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 910; i <= 1000; i += 10)
                {
                    oPacket.WriteShort(65);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                for (short i = 810; i <= 900; i += 10)
                {
                    oPacket.WriteShort(66);
                    oPacket.WriteShort(2);
                    oPacket.WriteShort(0);
                    oPacket.WriteUShort(ProfileService.GetProfileConfig(Nickname)?.Rider?.SlotChanger ?? 0);
                    oPacket.WriteByte(0);
                    oPacket.WriteByte(0);
                    oPacket.WriteShort(-1);
                    oPacket.WriteShort(-1);
                    oPacket.WriteByte(1);
                    oPacket.WriteByte(Grade);
                    oPacket.WriteShort(i);
                }
                Parent.Client.Send(oPacket);
            }
        }

        public static void LoRpGetRiderItemPacket(SessionGroup Parent, ushort itemCat, List<List<ushort>> item, HashSet<(ushort cat, ushort id)> ownedItems = null)
        {
            int range = 100;//分批次数
            int times = item.Count / range + (item.Count % range > 0 ? 1 : 0);
            for (int i = 0; i < times; i++)
            {
                var tempList = item.GetRange(i * range, (i + 1) * range > item.Count ? (item.Count - i * range) : range);
                using (OutPacket oPacket = new OutPacket("LoRpGetRiderItemPacket"))
                {
                    oPacket.WriteInt(times);
                    oPacket.WriteInt(i + 1);
                    oPacket.WriteInt(tempList.Count);
                    for (int f = 0; f < tempList.Count; f++)
                    {
                        oPacket.WriteUShort(itemCat);
                        oPacket.WriteUShort(tempList[f][0]);
                        oPacket.WriteUShort(tempList[f][1]);
                        oPacket.WriteUShort(tempList[f][2]);
                        // 购买模式：按 (itemCatId, itemId) 二元组匹配（防止跨类别 itemId 相同导致误判）
                        byte preventItem;
                        if (ServerConf.Current.PurchaseOnlyMode && ownedItems != null)
                        {
                            ushort itemId = tempList[f][0];
                            preventItem = ownedItems.Contains((itemCat, itemId)) ? (byte)0 : (byte)1;
                        }
                        else
                        {
                            preventItem = (byte)(Program.PreventItem ? 1 : 0);
                        }
                        oPacket.WriteByte(preventItem);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(-1);
                        oPacket.WriteShort(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteByte(0);
                        oPacket.WriteShort(0);
                    }
                    Parent.Client.Send(oPacket);
                }
            }
        }
    }
}