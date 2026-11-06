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
        public const int SaveVersion = 3; // v3: wirelessMap 改成星球级 HashSet

        private static readonly Dictionary<int, Dictionary<int, int>> setMap =
            new Dictionary<int, Dictionary<int, int>>();

        // 无线供料开关：**星球级**（任一建筑开即全站生效）。
        // 堆叠在一起的科研站/组装机属同一逻辑单元，不该每栋独立开——
        // 玩家只要给其中一台开无线，整颗星球的倍率建筑都自动参与直供直收。
        private static readonly HashSet<int> wirelessMap =
            new HashSet<int>();

        // 实际批量（可被工作线程写入，必须线程安全）：key = (planetId + 2) * 1000000 + entityId
        private static readonly ConcurrentDictionary<long, int> effMap =
            new ConcurrentDictionary<long, int>();

        // 能耗倍率（平滑值）：能耗不直接乘动态批量 k——k 每个周期边界都随缓存重算，
        // 直接乘会让电网需求随补料节奏锯齿形大幅波动。这里保存向实际 k 缓慢收敛
        // 的移动平均，电力曲线因此平缓爬坡/下坡，而不是阶跃跳变。
        private static readonly ConcurrentDictionary<long, int> energyMap =
            new ConcurrentDictionary<long, int>();

        // ---------- 建筑内置电容（电网波动隔离） ----------

        // 电网满足率采样（0~1）：由机器 tick 前缀按帧写入，值即机器实际拿到的功率比
        private static readonly ConcurrentDictionary<long, float> satMap =
            new ConcurrentDictionary<long, float>();

        // 建筑内部储能（能量值）：平时按平滑功率充电富余存入，瞬时消耗/电网不足时扣除
        private static readonly ConcurrentDictionary<long, long> bufferMap =
            new ConcurrentDictionary<long, long>();

        // 限电倍率上限：电网不足时由能耗扫池计算（充电速率 + 储能均摊），int.MaxValue = 不限
        private static readonly ConcurrentDictionary<long, int> energyCapMap =
            new ConcurrentDictionary<long, int>();

        // 储能容量（随能耗扫池更新，供 UI 能量条显示）
        private static readonly ConcurrentDictionary<long, long> bufCapMap =
            new ConcurrentDictionary<long, long>();

        public static void SamplePower(int planetId, int entityId, float power)
        {
            if (planetId < 0) return;
            if (power < 0f) power = 0f;
            if (power > 1f) power = 1f;
            satMap[EffKey(planetId, entityId)] = power;
        }

        /// <summary>最近一次采样到的电网满足率，无采样时按 1（满功率）处理。</summary>
        public static float GetSat(int planetId, int entityId)
        {
            float s;
            return satMap.TryGetValue(EffKey(planetId, entityId), out s) ? s : 1f;
        }

        /// <summary>对建筑储能充放 delta（可为负），钳制在 [0, cap]，返回充放后的储能值。</summary>
        public static long ChargeBuffer(int planetId, int entityId, long delta, long cap)
        {
            long key = EffKey(planetId, entityId);
            long cur;
            bufferMap.TryGetValue(key, out cur);
            cur += delta;
            if (cur < 0) cur = 0;
            if (cur > cap) cur = cap;
            bufferMap[key] = cur;
            return cur;
        }

        // 限电恢复迟滞计数器：连续满足率 ≥98% 的 tick 数，需 ≥30 才开始恢复
        private static readonly ConcurrentDictionary<long, int> healthyCountMap =
            new ConcurrentDictionary<long, int>();

        public static void TickHealthyCount(int planetId, int entityId)
        {
            if (planetId < 0) return;
            long key = EffKey(planetId, entityId);
            healthyCountMap.AddOrUpdate(key, 1, (k, v) => v < 9999 ? v + 1 : v);
        }

        public static void ResetHealthyCount(int planetId, int entityId)
        {
            if (planetId < 0) return;
            healthyCountMap[EffKey(planetId, entityId)] = 0;
        }

        public static int GetHealthyCount(int planetId, int entityId)
        {
            int c;
            return healthyCountMap.TryGetValue(EffKey(planetId, entityId), out c) ? c : 0;
        }

        public static void SetBufferCap(int planetId, int entityId, long cap)
        {
            if (planetId < 0) return;
            bufCapMap[EffKey(planetId, entityId)] = cap;
        }

        /// <summary>读取建筑储能状态（当前值/容量），供 UI 能量条。无数据时返回 false。</summary>
        public static bool GetBufferInfo(int planetId, int entityId, out long cur, out long cap)
        {
            long key = EffKey(planetId, entityId);
            bufferMap.TryGetValue(key, out cur);
            return bufCapMap.TryGetValue(key, out cap);
        }

        public static int GetEnergyCapK(int planetId, int entityId)
        {
            int k;
            return energyCapMap.TryGetValue(EffKey(planetId, entityId), out k) ? k : int.MaxValue;
        }

        public static void SetEnergyCapK(int planetId, int entityId, int k)
        {
            if (planetId < 0) return;
            energyCapMap[EffKey(planetId, entityId)] = k;
        }

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
            return wirelessMap.Contains(planetId);
        }

        /// <summary>开/关星球级无线供料（任一建筑的按钮操作都会同步到整颗星球）。</summary>
        public static void SetWireless(int planetId, int entityId, bool on)
        {
            if (planetId < 0) return;
            if (on) wirelessMap.Add(planetId);
            else wirelessMap.Remove(planetId);
        }

        public static bool PlanetHasWireless(int planetId)
        {
            return wirelessMap.Contains(planetId);
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
            long key = EffKey(planetId, entityId);
            effMap[key] = k;

            // 能耗平滑：每次周期边界（SetEff 只在此时被调用）向实际 k 收敛 1/4，
            // 最少走 1，首次直接取实际值。约十几个周期收敛到目标，波动被压掉大半。
            int old;
            if (!energyMap.TryGetValue(key, out old)) { energyMap[key] = k; return; }
            int diff = k - old;
            if (diff == 0) return;
            int step = diff / 4;
            if (step == 0) step = diff > 0 ? 1 : -1;
            energyMap[key] = old + step;
        }

        /// <summary>能耗用的平滑倍率（仅能耗扫池使用，生产结算仍用 GetEff 的瞬时值）。</summary>
        public static int GetEnergyK(int planetId, int entityId)
        {
            int k;
            return energyMap.TryGetValue(EffKey(planetId, entityId), out k) ? k : 1;
        }

        public static void ClearAll()
        {
            setMap.Clear();
            effMap.Clear();
            energyMap.Clear();
            satMap.Clear();
            bufferMap.Clear();
            bufCapMap.Clear();
            energyCapMap.Clear();
            healthyCountMap.Clear();
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
            // 星球级无线开关（v3+：HashSet<planetId>；v2 格式旧存档仍可读取并自动合并）
            w.Write(wirelessMap.Count);
            foreach (int planetId in wirelessMap) w.Write(planetId);
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
            if (version >= 3)
            {
                // 新格式：直接 planetId 列表
                int wCount = r.ReadInt32();
                for (int i = 0; i < wCount; i++) wirelessMap.Add(r.ReadInt32());
            }
            else if (version >= 2)
            {
                // 旧格式：planetId -> entityId 集合。合并成星球级：
                // 该星球任一建筑开过无线就视为整星球开启
                int wCount = r.ReadInt32();
                for (int p = 0; p < wCount; p++)
                {
                    int planetId = r.ReadInt32();
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++) r.ReadInt32(); // 跳过 entityId
                    wirelessMap.Add(planetId);
                }
            }
        }
    }
}
