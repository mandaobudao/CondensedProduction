using HarmonyLib;
using System;
using System.Collections.Generic;

namespace CondensedProduction
{
    /// <summary>
    /// 全球供应塔：等价 PlanetwideSupplier + PlanetwideCollector 的组合功能，
    /// 但只作用于「本 mod 设定了倍率（k&gt;1）」的生产建筑。
    ///
    /// 供料：从本行星所有物流站的库存里直接补齐该建筑倍率对应的原料上限
    ///       （上限与 UpdateNeeds 的判据一致：每周期需求 × 倍率 × 缓存系数 × 原版时间项）。
    /// 回收：把建筑产出缓冲里的产物直接并入本行星物流站库存。
    ///
    /// 执行时机：GameLogic.LogicFrame 之后（此时并行工作线程已全部汇合，主线程安全），
    /// 每 GlobalSupplyIntervalTicks 个游戏刻执行一次。
    /// 结构体访问一律用 ref 数组元素，避免拿到副本写回丢失。
    /// </summary>
    public static class GlobalSupply
    {
        private static long lastTick = long.MinValue;
        private static readonly HashSet<int> errLogged = new HashSet<int>();
        private static readonly HashSet<int> diagLogged = new HashSet<int>();

        public static void Tick()
        {
            if (Plugin.GlobalSupplyEnabledEntry != null && !Plugin.GlobalSupplyEnabledEntry.Value) return;
            if (GameMain.data == null) return;
            var factories = GameMain.data.factories;
            if (factories == null) return;

            long now = GameMain.gameTick;
            int interval = Plugin.GlobalSupplyIntervalEntry == null ? 30 : Plugin.GlobalSupplyIntervalEntry.Value;
            if (interval < 1) interval = 1;
            // 读档后 GameMain.gameTick 会从头计数（比上一局的 lastTick 小），
            // 若直接用差值判断会得到负数，导致扫描永久停摆。回退时立即重置节流。
            if (now < lastTick) { lastTick = long.MinValue; diagLogged.Clear(); }
            if (lastTick != long.MinValue && now - lastTick < interval) return;
            lastTick = now;

            bool collect = Plugin.GlobalSupplyCollectEntry == null || Plugin.GlobalSupplyCollectEntry.Value;
            for (int f = 0; f < factories.Length; f++)
            {
                var factory = factories[f];
                if (factory == null || factory.factorySystem == null || factory.transport == null) continue;
                if (!MultStore.PlanetHasWireless(factory.planetId)) continue;
                try { ProcessFactory(factory, collect); }
                catch (Exception e)
                {
                    if (MultStore.Log != null && errLogged.Add(f))
                        MultStore.Log.LogError(string.Format("[CP] wireless supply error planet={0}: {1}", factory.planetId, e));
                }
            }
        }

        private static void ProcessFactory(PlanetFactory factory, bool collect)
        {
            var fs = factory.factorySystem;
            var transport = factory.transport;

            // 物流站池（行星与星际物流站共用同一池）。stationCursor 是已分配数量，
            // 部分版本 stationCount 只统计启用中的站，两者取大更稳。
            StationComponent[] stations = transport.stationPool;
            int stationCount = 0;
            if (stations != null)
                stationCount = Math.Min(Math.Max(transport.stationCount, transport.stationCursor), stations.Length);

            var apool = fs.assemblerPool;
            if (apool != null)
            {
                int cursor = Math.Min(fs.assemblerCursor, apool.Length);
                for (int i = 1; i < cursor; i++)
                {
                    if (apool[i].entityId == 0) continue;
                    ProcessAssembler(factory.planetId, ref apool[i], stations, stationCount, collect);
                }
            }

            var lpool = fs.labPool;
            if (lpool != null)
            {
                int cursor = Math.Min(fs.labCursor, lpool.Length);
                for (int i = 1; i < cursor; i++)
                {
                    if (lpool[i].entityId == 0) continue;
                    if (lpool[i].researchMode)
                        ProcessResearchLab(factory, ref lpool[i], stations, stationCount);
                    else
                        ProcessLab(factory.planetId, ref lpool[i], stations, stationCount, collect);
                }
            }
        }

        /// <summary>
        /// 研究模式：从物流站直供矩阵到 matrixServed（上限 36000×倍率，与 needs 阈值一致）。
        ///
        /// 关键单位问题：matrixServed 存的不是「物品个数」，而是「物品数 × 3600」。
        /// 原版 PlanetFactory.InsertInto 的研究分支是 matrixServed[i] += 3600 * itemCount；
        /// needs 阈值 36000 正好等于 10 个物品。早期版本把原始件数直接写进 matrixServed，
        /// 相当于只记了 1/3600 个物品——从物流站取出的矩阵被静默吞掉，看起来「凭空消失」。
        /// 现在一律走 InsertInto，让游戏自己做单位换算。
        /// </summary>
        private static void ProcessResearchLab(PlanetFactory factory, ref LabComponent c,
            StationComponent[] stations, int stationCount)
        {
            int planetId = factory.planetId;
            if (!MultStore.GetWireless(planetId, c.entityId)) return;
            if (c.matrixServed == null) return;

            int k = MultStore.GetSet(planetId, c.entityId);
            if (k < 1) k = 1;
            long targetUnits = 36000L * k;

            // 原版 needs 数组固定写死六种矩阵 ID（分拣器会同时喂所有 6 种，
            // InternalUpdateResearch 内部再用 matrixPoints 决定哪些参与计算）。
            // 这里直接补全部六种，和分拣器行为对齐，不过滤 matrixPoints。
            int[] ids = LabComponent.matrixIds;
            int n = Math.Min(c.matrixServed.Length, ids == null ? 6 : ids.Length);
            if (n > 6) n = 6;
            for (int i = 0; i < n; i++)
            {
                int itemId = ids != null ? ids[i] : (6001 + i);
                if (itemId <= 0) continue;
                long wantItems = (targetUnits - c.matrixServed[i]) / 3600L;
                if (wantItems <= 0) continue;
                // 防御：k 配置异常时不至于单次扫池循环过多
                if (wantItems > 200000L) wantItems = 200000L;

                int remaining = (int)wantItems;
                while (remaining > 0)
                {
                    int chunk = remaining > 255 ? 255 : remaining;
                    int got, incPts;
                    TakeFromStations(stations, stationCount, itemId, chunk, out got, out incPts);
                    if (got <= 0) break;

                    int incLevel = incPts / got;
                    if (incLevel < 0) incLevel = 0;
                    if (incLevel > 4) incLevel = 4;

                    byte remainInc;
                    int inserted = factory.InsertInto(c.entityId, -1, itemId,
                        (byte)got, (byte)incLevel, out remainInc);
                    if (inserted <= 0)
                    {
                        // 插不进去（不匹配/已满），把取出的原样退回物流站，避免物品消失
                        AddToStations(stations, stationCount, itemId, got, incPts);
                        break;
                    }
                    remaining -= inserted;
                    if (inserted < got)
                        AddToStations(stations, stationCount, itemId, got - inserted,
                            incPts * (got - inserted) / got);
                }
            }

            // 首次诊断：确认研究模式直供路径是否被调用、是否有物流站
            if (MultStore.Log != null && diagLogged.Add(c.entityId))
            {
                int stationWith = 0;
                if (stations != null)
                    for (int s = 0; s < stationCount; s++)
                        if (stations[s] != null && stations[s].storage != null) stationWith++;
                MultStore.Log.LogInfo(string.Format(
                    "[CP] research-lab wireless planet={0} entity={1} kSet={2} matrix0={3} stationCount={4} withStorage={5}",
                    planetId, c.entityId, k, c.matrixServed.Length > 0 ? c.matrixServed[0] : -1,
                    stationCount, stationWith));
            }
        }

        private static void ProcessAssembler(int planetId, ref AssemblerComponent c,
            StationComponent[] stations, int stationCount, bool collect)
        {
            // 只有开启「无线」的建筑才由行星/星际物流站直供直收（与是否设倍率无关）
            if (!MultStore.GetWireless(planetId, c.entityId)) return;
            int kSet = MultStore.GetSet(planetId, c.entityId);
            RecipeExecuteData rec = c.recipeExecuteData;
            if (rec == null || rec.requires == null || rec.requireCounts == null) return;
            if (rec.timeSpend <= 0 || c.served == null || c.incServed == null) return;

            // 与原版 UpdateNeeds 相同的时间项（其判据为 served[i] < requireCounts[i] × num2）
            int num2 = c.speedOverride * 180 / rec.timeSpend + 1;
            if (num2 < 2) num2 = 2;
            long scale = (long)kSet * BufferFactor() * num2;

            int n = Math.Min(rec.requires.Length,
                Math.Min(rec.requireCounts.Length, c.served.Length));
            for (int i = 0; i < n; i++)
            {
                int itemId = rec.requires[i];
                int per = rec.requireCounts[i];
                if (itemId <= 0 || per <= 0) continue;
                long t = (long)per * scale;
                int target = t > int.MaxValue ? int.MaxValue : (int)t;
                int want = target - c.served[i];
                if (want <= 0) continue;

                int got, incPts;
                TakeFromStations(stations, stationCount, itemId, want, out got, out incPts);
                if (got <= 0) continue;
                c.served[i] += got;
                c.incServed[i] += incPts;
            }

            if (collect) CollectProducts(stations, stationCount, ref c);
        }

        private static void ProcessLab(int planetId, ref LabComponent c,
            StationComponent[] stations, int stationCount, bool collect)
        {
            // 只有开启「无线」的建筑才由行星/星际物流站直供直收（与是否设倍率无关）
            if (!MultStore.GetWireless(planetId, c.entityId)) return;
            int kSet = MultStore.GetSet(planetId, c.entityId);
            RecipeExecuteData rec = c.recipeExecuteData;
            if (rec == null || rec.requires == null || rec.requireCounts == null) return;
            if (rec.timeSpend <= 0 || c.served == null || c.incServed == null) return;

            // 科研站制造模式的要料判据是「每格物品数」而非周期数，因此只按倍率×缓存系数补料
            long scale = (long)kSet * BufferFactor();

            int n = Math.Min(rec.requires.Length,
                Math.Min(rec.requireCounts.Length, c.served.Length));
            for (int i = 0; i < n; i++)
            {
                int itemId = rec.requires[i];
                int per = rec.requireCounts[i];
                if (itemId <= 0 || per <= 0) continue;
                long t = (long)per * scale;
                int target = t > int.MaxValue ? int.MaxValue : (int)t;
                int want = target - c.served[i];
                if (want <= 0) continue;

                int got, incPts;
                TakeFromStations(stations, stationCount, itemId, want, out got, out incPts);
                if (got <= 0) continue;
                c.served[i] += got;
                c.incServed[i] += incPts;
            }

            if (collect) CollectLabProducts(stations, stationCount, ref c);
        }

        private static void CollectProducts(StationComponent[] stations, int stationCount, ref AssemblerComponent c)
        {
            RecipeExecuteData rec = c.recipeExecuteData;
            if (rec == null || rec.products == null || rec.productCounts == null || c.produced == null) return;
            int n = Math.Min(rec.products.Length, c.produced.Length);
            if (n == 0) return;

            int incLevel = AvgIncLevel(c.incUsed, c.served, c.incServed);
            for (int j = 0; j < n; j++)
            {
                int itemId = rec.products[j];
                int count = c.produced[j];
                if (itemId <= 0 || count <= 0) continue;
                int added = AddToStations(stations, stationCount, itemId, count, incLevel * count);
                if (added > 0) c.produced[j] -= added;
            }
        }

        private static void CollectLabProducts(StationComponent[] stations, int stationCount, ref LabComponent c)
        {
            RecipeExecuteData rec = c.recipeExecuteData;
            if (rec == null || rec.products == null || rec.productCounts == null || c.produced == null) return;
            int n = Math.Min(rec.products.Length, c.produced.Length);
            if (n == 0) return;

            int incLevel = AvgIncLevel(c.incUsed, c.served, c.incServed);
            for (int j = 0; j < n; j++)
            {
                int itemId = rec.products[j];
                int count = c.produced[j];
                if (itemId <= 0 || count <= 0) continue;
                int added = AddToStations(stations, stationCount, itemId, count, incLevel * count);
                if (added > 0) c.produced[j] -= added;
            }
        }

        /// <summary>按原料缓存里 incServed/served 的比例推算产物的增产剂等级（0~4）。</summary>
        private static int AvgIncLevel(bool incUsed, int[] served, int[] incServed)
        {
            if (!incUsed || served == null || incServed == null) return 0;
            int total = 0, inc = 0;
            int n = Math.Min(served.Length, incServed.Length);
            for (int i = 0; i < n; i++)
            {
                if (served[i] <= 0 || incServed[i] <= 0) continue;
                total += served[i];
                inc += incServed[i];
            }
            if (total <= 0) return 0;
            int level = inc / total;
            return level < 0 ? 0 : (level > 4 ? 4 : level);
        }

        /// <summary>从本行星所有物流站里取指定物品，返回取到数量与对应的增产剂点数。</summary>
        private static void TakeFromStations(StationComponent[] stations, int stationCount,
            int itemId, int want, out int taken, out int incPts)
        {
            taken = 0;
            incPts = 0;
            if (stations == null) return;
            for (int s = 0; s < stationCount && taken < want; s++)
            {
                StationComponent st = stations[s];
                if (st == null || st.storage == null) continue;
                // TakeItem 每次只处理一个匹配槽位，同一物品分布在多格时需重复调用
                while (taken < want)
                {
                    int id = itemId;
                    int cnt = want - taken;
                    int inc;
                    st.TakeItem(ref id, ref cnt, out inc);
                    if (cnt <= 0 || id != itemId) break;
                    taken += cnt;
                    incPts += inc * cnt;
                }
            }
            if (taken > want) taken = want;
        }

        /// <summary>把产物并入本行星物流站库存，返回实际接收数量（遵守每格上限）。</summary>
        private static int AddToStations(StationComponent[] stations, int stationCount,
            int itemId, int count, int incPts)
        {
            if (stations == null || count <= 0) return 0;
            int remain = count;
            int addedTotal = 0;
            for (int s = 0; s < stationCount && remain > 0; s++)
            {
                StationComponent st = stations[s];
                if (st == null || st.storage == null) continue;
                StationStore[] storage = st.storage;
                for (int g = 0; g < storage.Length && remain > 0; g++)
                {
                    if (storage[g].itemId != itemId || storage[g].max <= 0) continue;
                    int space = storage[g].max - storage[g].count;
                    if (space <= 0) continue;
                    int add = remain < space ? remain : space;
                    int incShare = (int)((long)incPts * add / count);
                    storage[g].count += add;
                    storage[g].inc += incShare;
                    remain -= add;
                    addedTotal += add;
                }
            }
            return addedTotal;
        }

        private static int BufferFactor()
        {
            int f = Plugin.InputBufferMultiplierEntry == null ? 3 : Plugin.InputBufferMultiplierEntry.Value;
            return f < 1 ? 1 : f;
        }
    }

    /// <summary>全球供应塔的主线程接入点：LogicFrame 结束时并行线程已汇合。</summary>
    public static class GlobalSupplyPatch
    {
        [HarmonyPatch(typeof(GameLogic), "LogicFrame")]
        [HarmonyPostfix]
        private static void PostLogicFrame()
        {
            GlobalSupply.Tick();
            StorageCapPatch.Maintain();
        }
    }
}
