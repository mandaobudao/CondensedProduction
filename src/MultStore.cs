using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace CondensedProduction
{
    /// <summary>
    /// 倍率数据层。
    /// setMap: planetId -> (entityId -> 设定倍率)，随存档持久化。
    /// effMap: planetId -> (entityId -> 最近一个tick实际运行倍率)，仅运行时用于能耗与UI显示。
    /// CurrentPlanetId: 工厂tick期间的行星上下文（FactorySystem各GameTick入口设置），
    /// 使用ThreadStatic以兼容游戏的多线程行星更新。
    /// </summary>
    public static class MultStore
    {
        public const int SaveVersion = 2;

        private static readonly Dictionary<int, Dictionary<int, int>> setMap =
            new Dictionary<int, Dictionary<int, int>>();

        // 无线供料开关：planetId -> 开启无线的 entityId 集合
        private static readonly Dictionary<int, HashSet<int>> wirelessMap =
            new Dictionary<int, HashSet<int>>();

        // 实际批量（可被工作线程写入，必须线程安全）：key = (planetId + 2) * 1000000 + entityId
        private static readonly ConcurrentDictionary<long, int> effMap =
            new ConcurrentDictionary<long, int>();

        private static long EffKey(int planetId, int entityId)
        {
            return (long)(planetId + 2) * 1000000L + entityId;
        }

        [ThreadStatic] public static int CurrentPlanetId = -1;

        public static BepInEx.Logging.ManualLogSource Log;

        // 建筑设置复制/粘贴用的倍率剪贴板
        public static int ClipboardK = 1;
        public static bool ClipboardWireless;

        // 兜底环境行星：Project Eden 等 mod 会把组装机 tick 移到工作线程，
        // ThreadStatic 在工作线程取不到，此时回退到主线程设置的环境值
        // （原版按行星依次 tick，同一时刻只有一个行星在结算，因此安全）
        public static int AmbientPlanetId = -1;

        public static int EffectivePlanetId
        {
            get { return CurrentPlanetId >= 0 ? CurrentPlanetId : AmbientPlanetId; }
        }

        public static int MaxMultiplier
        {
            get
            {
                int v = Plugin.MaxMultiplierEntry == null ? 100 : Plugin.MaxMultiplierEntry.Value;
                return v <= 0 ? int.MaxValue : v;
            }
        }

        public static int GetSet(int planetId, int entityId)
        {
            Dictionary<int, int> planet;
            int k;
            if (setMap.TryGetValue(planetId, out planet) && planet.TryGetValue(entityId, out k))
                return k;
            return 1;
        }

        public static void SetMult(int planetId, int entityId, int k)
        {
            int max = MaxMultiplier;
            if (k > max) k = max;
            Dictionary<int, int> planet;
            if (k <= 1)
            {
                if (setMap.TryGetValue(planetId, out planet))
                {
                    planet.Remove(entityId);
                    if (planet.Count == 0) setMap.Remove(planetId);
                }
                SetEff(planetId, entityId, 1);
                capMap.Remove(entityId);
                return;
            }
            if (!setMap.TryGetValue(planetId, out planet))
            {
                planet = new Dictionary<int, int>();
                setMap[planetId] = planet;
            }
            planet[entityId] = k;
            capMap[entityId] = k;
        }

        public static bool PlanetHasSets(int planetId)
        {
            return setMap.ContainsKey(planetId);
        }

        public static bool GetWireless(int planetId, int entityId)
        {
            HashSet<int> planet;
            return wirelessMap.TryGetValue(planetId, out planet) && planet.Contains(entityId);
        }

        public static void SetWireless(int planetId, int entityId, bool on)
        {
            HashSet<int> planet;
            if (on)
            {
                if (!wirelessMap.TryGetValue(planetId, out planet))
                {
                    planet = new HashSet<int>();
                    wirelessMap[planetId] = planet;
                }
                planet.Add(entityId);
            }
            else if (wirelessMap.TryGetValue(planetId, out planet))
            {
                planet.Remove(entityId);
                if (planet.Count == 0) wirelessMap.Remove(planetId);
            }
        }

        public static bool PlanetHasWireless(int planetId)
        {
            return wirelessMap.ContainsKey(planetId);
        }

        // 能耗按帧去重：key = planetId * 1000000 + entityId，value = 最后应用的游戏帧
        private static readonly Dictionary<long, long> energyFrame = new Dictionary<long, long>();

        public static bool TryMarkEnergy(int planetId, int entityId, long frame)
        {
            long key = (long)planetId * 1000000 + entityId;
            long last;
            if (energyFrame.TryGetValue(key, out last) && last == frame) return false;
            energyFrame[key] = frame;
            return true;
        }

        // 产物缓存上限用的全局 entityId→倍率 表（工作线程无法得知行星时的近似表，
        // 由扫池在主线程刷新；跨行星同 entityId 时取其一，仅影响缓存上限显示，无害）
        private static readonly Dictionary<int, int> capMap = new Dictionary<int, int>();

        public static int GetCapK(int entityId)
        {
            int k;
            return capMap.TryGetValue(entityId, out k) ? k : 1;
        }

        public static int GetEff(int planetId, int entityId)
        {
            int k;
            return effMap.TryGetValue(EffKey(planetId, entityId), out k) ? k : 1;
        }

        public static void SetEff(int planetId, int entityId, int k)
        {
            if (planetId < 0) return;
            effMap[EffKey(planetId, entityId)] = k;
        }

        public static void ClearAll()
        {
            setMap.Clear();
            effMap.Clear();
            capMap.Clear();
            energyFrame.Clear();
            wirelessMap.Clear();
        }

        public static void Export(BinaryWriter w)
        {
            w.Write(SaveVersion);
            w.Write(setMap.Count);
            foreach (var pk in setMap)
            {
                w.Write(pk.Key);
                w.Write(pk.Value.Count);
                foreach (var e in pk.Value)
                {
                    w.Write(e.Key);
                    w.Write(e.Value);
                }
            }
            w.Write(wirelessMap.Count);
            foreach (var pk in wirelessMap)
            {
                w.Write(pk.Key);
                w.Write(pk.Value.Count);
                foreach (var e in pk.Value) w.Write(e);
            }
        }

        public static void Import(BinaryReader r)
        {
            ClearAll();
            int version = r.ReadInt32();
            int planetCount = r.ReadInt32();
            for (int p = 0; p < planetCount; p++)
            {
                int planetId = r.ReadInt32();
                int count = r.ReadInt32();
                var planet = new Dictionary<int, int>(count);
                for (int i = 0; i < count; i++)
                {
                    int entityId = r.ReadInt32();
                    int k = r.ReadInt32();
                    if (k > 1)
                    {
                        planet[entityId] = k;
                        capMap[entityId] = k;
                    }
                }
                if (planet.Count > 0) setMap[planetId] = planet;
            }
            if (version >= 2)
            {
                int wCount = r.ReadInt32();
                for (int p = 0; p < wCount; p++)
                {
                    int planetId = r.ReadInt32();
                    int count = r.ReadInt32();
                    var set = new HashSet<int>();
                    for (int i = 0; i < count; i++) set.Add(r.ReadInt32());
                    if (set.Count > 0) wirelessMap[planetId] = set;
                }
            }
        }
    }
}
