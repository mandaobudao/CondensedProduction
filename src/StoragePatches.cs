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
            // ItemProto 与 ModelProto 的 prefabDesc 可能是两个不同实例，必须两边都放大，
            // 否则会出现「界面里一拖上限就从 ×100 掉回原版 5000」：
            // 滑条显示用的是 ItemProto.prefabDesc，
            // 而点击滑条时 PlanetTransport.SetStationStorage 的钳制用的是 ModelProto.prefabDesc。
            var seen = new System.Collections.Generic.HashSet<PrefabDesc>();

            var items = LDB.items.dataArray;
            for (int i = 0; i < items.Length; i++)
                ScaleStationMax(items[i].prefabDesc, f, seen);

            if (LDB.models != null && LDB.models.dataArray != null)
            {
                var models = LDB.models.dataArray;
                for (int i = 0; i < models.Length; i++)
                    ScaleStationMax(models[i].prefabDesc, f, seen);
            }

            prefabScaled = true;
        }

        private static void ScaleStationMax(PrefabDesc pd, int f,
            System.Collections.Generic.HashSet<PrefabDesc> seen)
        {
            // 不依赖 isStation：物流站模型侧的 isStation 可能并未置位，
            // 用「有每格容量上限」判定最稳。
            if (pd == null || pd.stationMaxItemCount <= 0) return;
            if (!seen.Add(pd)) return; // 若是同一实例则只放大一次
            long v = (long)pd.stationMaxItemCount * f;
            pd.stationMaxItemCount = (int)Math.Min(int.MaxValue, v);
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

        // 注意：不再对物流站已有的 storage[i].max 做任何「放大」或「校正」。
        // 放大只发生在容量来源（PrefabDesc.stationMaxItemCount 与静态堆叠表），
        // 玩家在界面里设置的物品上限完全由玩家决定，mod 不会再改写它。
    }

    /// <summary>
    /// 修复物流站「物品限制」被原版钳回基础值的问题。
    ///
    /// 原版 PlanetTransport.SetStationStorage 会用 PrefabDesc.stationMaxItemCount
    /// 对滑条请求值做上限钳制。站容量被本 mod 放大后，如果钳制侧读到的仍是
    /// 未放大的那份预制体，玩家一拖滑条上限就会掉回原版值（例如 5000）。
    /// 这里在调用后把玩家实际拖到的值写回：滑条范围本身已按放大后的容量给出，
    /// 因此请求值就是合法上限，不需要再钳一次。
    /// </summary>
    [HarmonyPatch]
    public static class StationLimitPatch
    {
        private static int requested;
        private static int requestedItem;

        [HarmonyPatch(typeof(PlanetTransport), "SetStationStorage")]
        [HarmonyPrefix]
        private static void PreSet(int itemId, int itemCountMax)
        {
            requested = itemCountMax;
            requestedItem = itemId;
        }

        [HarmonyPatch(typeof(PlanetTransport), "SetStationStorage")]
        [HarmonyPostfix]
        private static void PostSet(PlanetTransport __instance, int stationId, int storageIdx, int itemId)
        {
            if (itemId <= 0 || itemId != requestedItem) return;
            var pool = __instance.stationPool;
            if (pool == null) return;
            for (int i = 0; i < pool.Length; i++)
            {
                var st = pool[i];
                if (st == null || st.id != stationId || st.storage == null) continue;
                if (storageIdx < 0 || storageIdx >= st.storage.Length) return;
                if (st.storage[storageIdx].itemId == itemId && requested > st.storage[storageIdx].max)
                    st.storage[storageIdx].max = requested;
                return;
            }
        }
    }
}
