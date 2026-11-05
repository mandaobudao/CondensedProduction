using HarmonyLib;
using System;

namespace CondensedProduction
{
    /// <summary>
    /// 全容器每格上限扩大：物流站（行星/星际/轨道采集）、储物箱、罐体、玩家背包。
    /// 做法是放大「上限来源」而不是逐个容器赋值，天然只放大一次、读档也不会叠加。
    /// 另外提供 Maintain() 周期校正，负责补上「读档前就存在」的旧存档数据。
    /// </summary>
    public static class StorageCapPatch
    {
        private static int Factor
        {
            get
            {
                int f = Plugin.ContainerMultiplierEntry == null ? 100 : Plugin.ContainerMultiplierEntry.Value;
                return f < 1 ? 1 : f;
            }
        }

        private static bool tableScaled;
        private static bool prefabScaled;
        private static int minScaledStationMax;
        private static long lastMaintain = long.MinValue;

        /// <summary>周期校正入口（主线程，每 30 游戏刻一次，全部操作幂等）。</summary>
        public static void Maintain()
        {
            if (Factor <= 1) return;
            long now = GameMain.gameTick;
            // 读档后 gameTick 会重新计数（小于上一局记录），差值变负会导致校正永久停摆
            if (now < lastMaintain) lastMaintain = long.MinValue;
            if (lastMaintain != long.MinValue && now - lastMaintain < 30) return;
            lastMaintain = now;
            EnsureStackTable();
            EnsureStationPrefabs();
            NormalizeStations();
        }

        /// <summary>
        /// 物流站每格容量：放大预制体基础值 stationMaxItemCount。
        /// StationComponent.Init 与科技重算都基于它，因此只改这里即可全站生效。
        /// </summary>
        private static void EnsureStationPrefabs()
        {
            if (prefabScaled) return;
            if (LDB.items == null || LDB.items.dataArray == null) return;
            int f = Factor;
            var items = LDB.items.dataArray;
            int min = int.MaxValue;
            for (int i = 0; i < items.Length; i++)
            {
                PrefabDesc pd = items[i].prefabDesc;
                if (pd == null || !pd.isStation) continue;
                long v = (long)pd.stationMaxItemCount * f;
                pd.stationMaxItemCount = (int)Math.Min(int.MaxValue, v);
                if (pd.stationMaxItemCount < min) min = pd.stationMaxItemCount;
            }
            prefabScaled = true;
            minScaledStationMax = min == int.MaxValue ? 0 : min;
        }

        /// <summary>
        /// 储物箱/罐体/背包每格上限来源：静态堆叠表（由 ItemProto.StackSize 填充）。
        /// 放大一次后，新建与筛选设置都会自动按新上限。
        /// </summary>
        private static void EnsureStackTable()
        {
            if (tableScaled) return;
            if (!StorageComponent.staticLoaded) return;
            int[] table = StorageComponent.itemStackCount;
            if (table == null) return;
            int f = Factor;
            for (int i = 0; i < table.Length; i++)
            {
                long v = (long)table[i] * f;
                table[i] = (int)Math.Min(int.MaxValue, v);
            }
            tableScaled = true;
        }

        [HarmonyPatch(typeof(StationComponent), "Init")]
        [HarmonyPrefix]
        private static void StationInitPrefix()
        {
            EnsureStationPrefabs();
            NormalizeStations();
        }

        [HarmonyPatch(typeof(StorageComponent), "LoadStatic")]
        [HarmonyPostfix]
        private static void LoadStaticPostfix()
        {
            EnsureStackTable();
        }

        /// <summary>
        /// 读档后按新的堆叠表恢复每格上限（旧存档里存的仍是原版上限）。
        /// 用「取表值」而不是「再乘 100」，重复读档不会叠加。
        /// </summary>
        [HarmonyPatch(typeof(StorageComponent), "Import")]
        [HarmonyPostfix]
        private static void ImportPostfix(StorageComponent __instance)
        {
            if (Factor <= 1) return;
            EnsureStackTable();
            var grids = __instance.grids;
            int[] table = StorageComponent.itemStackCount;
            if (grids == null || table == null) return;
            for (int i = 0; i < grids.Length; i++)
            {
                int fid = grids[i].filter;
                if (fid > 0 && fid < table.Length) grids[i].stackSize = table[fid];
            }
        }

        /// <summary>
        /// 旧存档里的物流站每格上限是按原版容量存盘的，读档后不会自动变大。
        /// 这里用「上限低于最小预制体放大值」判定未放大，并补乘系数（幂等）。
        /// </summary>
        private static void NormalizeStations()
        {
            if (minScaledStationMax <= 0) return;
            int f = Factor;
            if (f <= 1) return;
            if (GameMain.data == null || GameMain.data.factories == null) return;
            var factories = GameMain.data.factories;
            for (int i = 0; i < factories.Length; i++)
            {
                var factory = factories[i];
                if (factory == null || factory.transport == null || factory.transport.stationPool == null) continue;
                var stations = factory.transport.stationPool;
                int count = Math.Min(factory.transport.stationCount, stations.Length);
                for (int s = 0; s < count; s++)
                {
                    StationComponent st = stations[s];
                    if (st == null || st.storage == null) continue;
                    StationStore[] storage = st.storage;
                    for (int g = 0; g < storage.Length; g++)
                    {
                        if (storage[g].itemId == 0 || storage[g].max <= 0) continue;
                        if (storage[g].max >= minScaledStationMax) continue;
                        long v = (long)storage[g].max * f;
                        storage[g].max = (int)Math.Min(int.MaxValue, v);
                    }
                }
            }
        }
    }
}
