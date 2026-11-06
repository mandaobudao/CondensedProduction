using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace CondensedProduction
{
    /// <summary>
    /// 浓缩核心：周期时间不变，单周期消耗与产出 ×k（等效 k 台建筑合一）。
    /// 实现：转译 AssemblerComponent.InternalUpdate / LabComponent.InternalUpdateAssemble，
    /// 把 recipeExecuteData.requireCounts / productCounts 数组的元素读取统一乘以 k。
    /// k 按 (行星, entityId) 精确解析：工作线程的行星上下文由
    /// GameLogic._assembler_parallel / _lab_produce_parallel 的转译注入维护（ThreadStatic）。
    /// 能耗：电力组件 requiredEnergy × k（同帧去重，工作才乘）。
    /// </summary>
    public static class MultEngine
    {
        /// <summary>
        /// 消耗端批量（仅 requireCounts 读取点使用）：min(设定倍率, 当前缓存可支撑的周期数)。
        /// 原版流程里 requireCounts 只在一个周期开始时（检查并扣除 served）读取，
        /// 因此这里每次调用都是一个新周期的起点：按当前缓存重算 k 并锁存，
        /// 本周期内产出端一律读取该锁存值，保证「吃多少料、出多少货」严格一致。
        /// （原版在周期完成时先 replicating=false 再产出，随后同 tick 内立即检查下一周期，
        ///  消耗与产出时 replicating 都是 false，无法用该标志区分两端，必须按读取的字段分流。）
        /// </summary>
        private static int GetAsmKConsume(ref AssemblerComponent c)
        {
            int planet = MultStore.CurrentPlanetId;
            int kSet = planet >= 0 ? MultStore.GetSet(planet, c.entityId) : MultStore.GetCapK(c.entityId);
            if (kSet <= 1) return 1;
            return ResolveDyn(planet, c.entityId, kSet,
                c.recipeExecuteData.requireCounts, c.served);
        }

        private static int GetLabKConsume(ref LabComponent c)
        {
            int planet = MultStore.CurrentPlanetId;
            int kSet = planet >= 0 ? MultStore.GetSet(planet, c.entityId) : MultStore.GetCapK(c.entityId);
            if (kSet <= 1) return 1;
            return ResolveDyn(planet, c.entityId, kSet,
                c.recipeExecuteData.requireCounts, c.served);
        }

        /// <summary>
        /// 产出端批量（仅 productCounts 读取点使用）：只读消耗端锁存的 k，绝不重算。
        /// 产出时原料已被扣除，若按当前缓存重算会把 k 压低，导致「吃 1000 份料只出 1 份货」。
        /// </summary>
        private static int GetAsmKProduce(ref AssemblerComponent c)
        {
            int planet = MultStore.CurrentPlanetId;
            int kSet = planet >= 0 ? MultStore.GetSet(planet, c.entityId) : MultStore.GetCapK(c.entityId);
            if (kSet <= 1) return 1;
            return ProduceK(planet, c.entityId, kSet);
        }

        private static int GetLabKProduce(ref LabComponent c)
        {
            int planet = MultStore.CurrentPlanetId;
            int kSet = planet >= 0 ? MultStore.GetSet(planet, c.entityId) : MultStore.GetCapK(c.entityId);
            if (kSet <= 1) return 1;
            return ProduceK(planet, c.entityId, kSet);
        }

        private static int ProduceK(int planet, int entityId, int kSet)
        {
            int k = 0;
            if (planet >= 0) k = MultStore.GetEff(planet, entityId);
            else fallbackEff.TryGetValue(entityId, out k);
            // 理论上产出前必定有过一次消耗（已锁存）。没有锁存记录说明是异常路径，
            // 退回设定值比退回 1 更接近消耗端当时的选择。
            if (k < 1) k = kSet;
            if (k < 1) k = 1;
            return k;
        }

        // 无行星上下文（非并行路径）时的锁存兜底，按 entityId 记录
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> fallbackEff =
            new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        // 每线程每帧缓存：同一 tick 内同一建筑的 k 必须稳定，
        // 否则消耗过程中缓存下降会让同一次结算前后用的 k 不一致。
        [ThreadStatic] private static long tlsFrame = long.MinValue;
        [ThreadStatic] private static Dictionary<long, int> tlsK;

        private static int ResolveDyn(int planet, int entityId, int kSet, int[] require, int[] served)
        {
            long frame = GameMain.gameTick;
            if (frame != tlsFrame || tlsK == null)
            {
                tlsFrame = frame;
                if (tlsK == null) tlsK = new Dictionary<long, int>();
                else tlsK.Clear();
            }
            long key = (long)(planet + 2) * 1000000L + entityId;
            int cached;
            if (tlsK.TryGetValue(key, out cached)) return cached;

            int avail = kSet;
            if (require != null && served != null)
            {
                int n = require.Length < served.Length ? require.Length : served.Length;
                for (int i = 0; i < n; i++)
                {
                    int need = require[i];
                    if (need <= 0) continue;
                    int can = served[i] / need;
                    if (can < avail) avail = can;
                }
            }
            // 电网不足时限电：k 不超过「充电速率 + 储能均摊」可支撑的水平（电网健康时不限）
            int energyCap = MultStore.GetEnergyCapK(planet, entityId);
            if (avail > energyCap) avail = energyCap;
            // 料不足一个周期时用 1：原版检查 served >= require 自然不满足，机器正常待料
            if (avail < 1) avail = 1;
            if (avail > kSet) avail = kSet;

            // 关键：k 变化加 delta clamp。直接按缓存算的 avail 可能在相邻周期之间大幅跳变
            // （缓存被分拣器/无线供料瞬时刷新），让能耗扫池跟着阶跃跳——这是电网抖动
            // 的主要根源。每次只允许 k 变最多 kSet/20（至少 1），缓存变化被缓慢过滤。
            int prev = planet >= 0 ? MultStore.GetEff(planet, entityId) : 0;
            if (prev > 0)
            {
                int step = kSet / 20;
                if (step < 1) step = 1;
                int diff = avail - prev;
                if (diff > step) avail = prev + step;
                else if (diff < -step) avail = prev - step;
                if (avail < 1) avail = 1;
                if (avail > kSet) avail = kSet;
            }

            tlsK[key] = avail;
            MultStore.SetEff(planet, entityId, avail);
            fallbackEff[entityId] = avail;
            return avail;
        }

        // ---------- 原料缓存上限（只改要料阈值，needs[i] 是物品ID不能动） ----------

        /// <summary>
        /// 要料阈值倍数 = 设定倍率 × 缓存系数。
        /// 原版 UpdateNeeds 的判据是 served[i] &lt; requireCounts[i] × (speed×180/timeSpend+1)，
        /// 只有这个阈值变大，缓存才会继续囤料；needs[] 里存的是「需要的物品ID」，不能写数量。
        /// </summary>
        private static int GetAsmKNeeds(ref AssemblerComponent c)
        {
            int k = KFor(c.entityId);
            if (k <= 1) return 1;
            return (int)Math.Min(int.MaxValue, (long)k * BufferFactor());
        }

        private static int GetLabKNeeds(ref LabComponent c)
        {
            int k = KFor(c.entityId);
            if (k <= 1) return 1;
            return (int)Math.Min(int.MaxValue, (long)k * BufferFactor());
        }

        /// <summary>研究模式倍率：直接用设定值（研究没有批量缓存概念，吞吐由可用矩阵套数自适应）。</summary>
        private static int GetLabKSet(ref LabComponent c)
        {
            int planet = MultStore.CurrentPlanetId;
            int k = planet >= 0 ? MultStore.GetSet(planet, c.entityId) : 1;
            return k < 1 ? 1 : k;
        }

        private static int BufferFactor()
        {
            int f = Plugin.InputBufferMultiplierEntry == null ? 3 : Plugin.InputBufferMultiplierEntry.Value;
            return f < 1 ? 1 : f;
        }

        /// <summary>查询设定倍率：优先用当前行星上下文，取不到时回退全局表。</summary>
        internal static int KFor(int entityId)
        {
            int planet = MultStore.CurrentPlanetId;
            return planet >= 0 ? MultStore.GetSet(planet, entityId) : MultStore.GetCapK(entityId);
        }

        // ---------- 能耗扫池（主线程，帧末） ----------

        internal static void EnergyPost(FactorySystem fs)
        {
            if (fs == null || fs.factory == null) return;
            int planet = fs.factory.planetId;
            if (!MultStore.PlanetHasSets(planet)) return;
            var consumers = fs.factory.powerSystem != null ? fs.factory.powerSystem.consumerPool : null;
            if (consumers == null) return;
            long frame = GameMain.gameTick;

            var apool = fs.assemblerPool;
            if (apool != null)
            {
                int cursor = Math.Min(fs.assemblerCursor, apool.Length);
                for (int i = 1; i < cursor; i++)
                {
                    if (apool[i].entityId == 0 || !apool[i].replicating) continue;
                    // 能耗用平滑倍率：动态 k 随缓存每周期跳变，直接乘会让电网需求锯齿波动
                    int k = MultStore.GetEnergyK(planet, apool[i].entityId);
                    if (k <= 1) continue;
                    int pcId = apool[i].pcId;
                    if (pcId <= 0 || pcId >= consumers.Length) continue;
                    if (!MultStore.TryMarkEnergy(planet, apool[i].entityId, frame)) continue;
                    AccountEnergy(planet, apool[i].entityId, consumers, pcId, k);
                    consumers[pcId].requiredEnergy *= k;
                }
            }

            var lpool = fs.labPool;
            if (lpool != null)
            {
                int cursor = Math.Min(fs.labCursor, lpool.Length);
                for (int i = 1; i < cursor; i++)
                {
                    if (lpool[i].entityId == 0 || !lpool[i].replicating) continue;
                    // 制造模式用平滑后的动态值；研究模式没有批量概念，直接用设定倍率
                    int k = lpool[i].researchMode
                        ? MultStore.GetSet(planet, lpool[i].entityId)
                        : MultStore.GetEnergyK(planet, lpool[i].entityId);
                    if (k <= 1) continue;
                    int pcId = lpool[i].pcId;
                    if (pcId <= 0 || pcId >= consumers.Length) continue;
                    if (!MultStore.TryMarkEnergy(planet, lpool[i].entityId, frame)) continue;
                    if (!lpool[i].researchMode)
                        AccountEnergy(planet, lpool[i].entityId, consumers, pcId, k);
                    consumers[pcId].requiredEnergy *= k;
                }
            }
        }

        internal static void EnergyPostAll()
        {
            if (GameMain.data == null) return;
            var factories = GameMain.data.factories;
            if (factories == null) return;
            for (int f = 0; f < factories.Length; f++)
            {
                var factory = factories[f];
                if (factory == null || factory.factorySystem == null) continue;
                try { EnergyPost(factory.factorySystem); }
                catch (Exception e)
                {
                    if (MultStore.Log != null) MultStore.Log.LogError("[CP] energy sweep error: " + e);
                }
            }
        }

        private static int BufferSeconds()
        {
            int s = Plugin.EnergyBufferSecondsEntry == null ? 10 : Plugin.EnergyBufferSecondsEntry.Value;
            return s < 0 ? 0 : s;
        }

        /// <summary>
        /// 建筑内置电容账务（每帧一次，跟随能耗扫池）：
        /// 到账 = 平滑充电功率 × 上帧电网满足率；支出 = 本帧实际批量 × 基准功耗；
        /// 差额存入/扣除储能。电网不足（满足率 &lt; 98%）时给出限电倍率上限
        /// （充电速率 + 储能在储能时长内的均摊），下個周期边界生效；电网健康时不限。
        /// </summary>
        private static void AccountEnergy(int planet, int entityId,
            PowerConsumerComponent[] consumers, int pcId, int kSmooth)
        {
            long baseE = consumers[pcId].requiredEnergy;
            if (baseE <= 0) baseE = consumers[pcId].workEnergyPerTick;
            if (baseE <= 0) return;

            int bufTicks = BufferSeconds() * 60;
            float sat = MultStore.GetSat(planet, entityId);
            if (sat > 1f) sat = 1f;
            long served = (long)(baseE * (double)kSmooth * sat);
            long consumed = (long)MultStore.GetEff(planet, entityId) * baseE;
            int kSet = MultStore.GetSet(planet, entityId);
            long bufCap = (long)kSet * baseE * bufTicks;
            long buf = MultStore.ChargeBuffer(planet, entityId, served - consumed, bufCap);
            MultStore.SetBufferCap(planet, entityId, bufCap);

            int capK;
            if (sat < 0.95f && bufTicks > 0)
            {
                // 电网不足：立即限电（充电速率 + 储能均摊），快速下压
                long spread = buf / ((long)bufTicks * baseE);
                capK = kSmooth + (int)spread;
                if (capK < 1) capK = 1;
                if (capK > kSet) capK = kSet;
                MultStore.ResetHealthyCount(planet, entityId);
            }
            else
            {
                // 电网恢复：需要连续满足率 ≥98% 且储能 >20% 才开始爬坡，
                // 且每帧只升 kSet/360（比之前慢三倍），彻底阻尼阈值附近的抖动
                MultStore.TickHealthyCount(planet, entityId);
                int prev = MultStore.GetEnergyCapK(planet, entityId);
                if (prev == int.MaxValue) capK = int.MaxValue;
                else if (MultStore.GetHealthyCount(planet, entityId) < 30
                         || (bufTicks > 0 && bufCap > 0 && (double)buf / bufCap < 0.20))
                    capK = prev; // 还没满足恢复条件，维持当前上限
                else
                {
                    int stepUp = kSet / 360 + 1;
                    capK = prev + stepUp;
                    if (capK >= kSet) capK = int.MaxValue;
                }
            }
            MultStore.SetEnergyCapK(planet, entityId, capK);
        }
    }

    /// <summary>
    /// 电网满足率采样：机器 tick 的 power 参数即该建筑实际拿到的功率比，
    /// 写入样本供电容账务与限电判断使用（只采样设了倍率的建筑，开销可忽略）。
    /// </summary>
    public static class PowerSamplePatch
    {
        [HarmonyPatch(typeof(AssemblerComponent), "InternalUpdate")]
        [HarmonyPrefix]
        private static void PreAsm(ref AssemblerComponent __instance, float power)
        {
            int planet = MultStore.CurrentPlanetId;
            if (planet < 0 || MultStore.GetSet(planet, __instance.entityId) <= 1) return;
            MultStore.SamplePower(planet, __instance.entityId, power);
        }

        [HarmonyPatch(typeof(LabComponent), "InternalUpdateAssemble")]
        [HarmonyPrefix]
        private static void PreLab(ref LabComponent __instance, float power)
        {
            int planet = MultStore.CurrentPlanetId;
            if (planet < 0 || MultStore.GetSet(planet, __instance.entityId) <= 1) return;
            MultStore.SamplePower(planet, __instance.entityId, power);
        }
    }

    /// <summary>
    /// 通用转译器：把方法内 recipeExecuteData.requireCounts / productCounts
    /// 数组的元素读取（ldelem.i4）统一乘以 k。
    /// 原理：数组引用先被存入局部变量，后续所有 ldelem.i4 都是按周期计数的读取点
    /// （缓存检查、消耗、增产剂分摊、产出、产物上限、生产统计），统一 ×k 即为批量模型。
    /// </summary>
    public static class CountMultiplyTranspiler
    {
        internal static IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> instructions,
            MethodInfo getKConsume, MethodInfo getKProduce)
        {
            var list = new List<CodeInstruction>(instructions);
            var tracked = new Dictionary<int, MethodInfo>(); // 持有计数数组的局部变量索引 → 该用的 k 获取器

            // 第一遍：找出存计数数组的局部变量，并按来源字段分流：
            // requireCounts（周期开始时的检查与扣除）→ 消耗端获取器（允许重算并锁存）
            // productCounts（周期完成时的产出与上限判断）→ 产出端获取器（只读锁存值）
            for (int i = 0; i + 2 < list.Count; i++)
            {
                if (IsLdfld(list[i], "recipeExecuteData"))
                {
                    MethodInfo getter = CountFieldGetter(list[i + 1], getKConsume, getKProduce);
                    if (getter != null)
                    {
                        int idx = GetStoreLocalIndex(list[i + 2]);
                        if (idx >= 0) tracked[idx] = getter;
                    }
                }
            }

            // 第二遍：对来自这些局部变量的 ldelem.i4 注入 ×k
            for (int i = 0; i < list.Count; i++)
            {
                int loadIdx = GetLoadLocalIndex(list[i]);
                MethodInfo getK;
                if (loadIdx >= 0 && tracked.TryGetValue(loadIdx, out getK))
                {
                    // 向前找配套的 ldelem.i4（中间只允许索引压栈指令）
                    int j = i + 1;
                    bool ok = false;
                    while (j < list.Count && j <= i + 4)
                    {
                        if (list[j].opcode == OpCodes.Ldelem_I4) { ok = true; break; }
                        if (!IsIndexPush(list[j])) break;
                        j++;
                    }
                    if (ok)
                    {
                        // 输出 ldloc..ldelem，然后 ×k
                        for (int t = i; t <= j; t++) yield return list[t];
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, getK);
                        yield return new CodeInstruction(OpCodes.Mul);
                        i = j;
                        continue;
                    }
                }
                yield return list[i];
            }
        }

        private static bool IsLdfld(CodeInstruction ci, string name)
        {
            return (ci.opcode == OpCodes.Ldfld) && ci.operand is FieldInfo f && f.Name == name;
        }

        private static MethodInfo CountFieldGetter(CodeInstruction ci, MethodInfo consume, MethodInfo produce)
        {
            if (ci.opcode == OpCodes.Ldfld && ci.operand is FieldInfo f &&
                f.DeclaringType != null && f.DeclaringType.Name == "RecipeExecuteData")
            {
                if (f.Name == "requireCounts") return consume;
                if (f.Name == "productCounts") return produce;
            }
            return null;
        }

        private static bool IsIndexPush(CodeInstruction ci)
        {
            return ci.opcode == OpCodes.Ldc_I4 || ci.opcode == OpCodes.Ldc_I4_S
                || ci.opcode == OpCodes.Ldc_I4_0 || ci.opcode == OpCodes.Ldc_I4_1
                || ci.opcode == OpCodes.Ldc_I4_2 || ci.opcode == OpCodes.Ldc_I4_3
                || GetLoadLocalIndex(ci) >= 0;
        }

        internal static int GetStoreLocalIndex(CodeInstruction ci)
        {
            if (ci.opcode == OpCodes.Stloc_0) return 0;
            if (ci.opcode == OpCodes.Stloc_1) return 1;
            if (ci.opcode == OpCodes.Stloc_2) return 2;
            if (ci.opcode == OpCodes.Stloc_3) return 3;
            if (ci.opcode == OpCodes.Stloc_S || ci.opcode == OpCodes.Stloc)
                return LocalIndex(ci.operand);
            return -1;
        }

        internal static int GetLoadLocalIndex(CodeInstruction ci)
        {
            if (ci.opcode == OpCodes.Ldloc_0) return 0;
            if (ci.opcode == OpCodes.Ldloc_1) return 1;
            if (ci.opcode == OpCodes.Ldloc_2) return 2;
            if (ci.opcode == OpCodes.Ldloc_3) return 3;
            if (ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloc)
                return LocalIndex(ci.operand);
            return -1;
        }

        private static int LocalIndex(object operand)
        {
            var lb = operand as LocalBuilder;
            if (lb != null) return lb.LocalIndex;
            if (operand is int) return (int)operand;
            if (operand is short) return (short)operand;
            if (operand is byte) return (byte)operand;
            return -1;
        }

        internal static CodeInstruction LoadLocal(int index)
        {
            switch (index)
            {
                case 0: return new CodeInstruction(OpCodes.Ldloc_0);
                case 1: return new CodeInstruction(OpCodes.Ldloc_1);
                case 2: return new CodeInstruction(OpCodes.Ldloc_2);
                case 3: return new CodeInstruction(OpCodes.Ldloc_3);
                default: return new CodeInstruction(OpCodes.Ldloc_S, index);
            }
        }
    }

    /// <summary>组装机消耗/产出批量转译（消耗端自适应，产出端读锁存值）。</summary>
    public static class AssemblerBatchPatch
    {
        private static readonly MethodInfo GetKConsume =
            typeof(MultEngine).GetMethod("GetAsmKConsume", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo GetKProduce =
            typeof(MultEngine).GetMethod("GetAsmKProduce", BindingFlags.Static | BindingFlags.NonPublic);

        [HarmonyPatch(typeof(AssemblerComponent), "InternalUpdate")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ci in CountMultiplyTranspiler.Apply(instructions, GetKConsume, GetKProduce))
            {
                // 原版产物缓存上限是硬编码 100（熔炉类单产物与多产物分支各一处：
                // produced[i] + productCounts[i] <= 100 否则停机）。
                // 该常数不乘 k 的话，productCounts×k > 100（约 k>100）就永远无法再生产，
                // 这就是「100 倍以上不运行」的根因。这里把它放大为 100×k（产出端锁存值）。
                if (ci.opcode == OpCodes.Ldc_I4_S && ci.operand is sbyte b && b == 100)
                {
                    yield return ci;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, GetKProduce);
                    yield return new CodeInstruction(OpCodes.Mul);
                    continue;
                }
                yield return ci;
            }
        }
    }

    /// <summary>
    /// 原料缓存上限转译：把 UpdateNeeds 里的 requireCounts 读取放大，
    /// 使「继续要料」的阈值 = 每周期需求 × 设定倍率 × 缓存系数，
    /// 缓存因此能囤到对应倍率的料（仅影响阈值，needs[] 的物品ID语义不变）。
    /// </summary>
    public static class NeedsBufferPatch
    {
        private static readonly MethodInfo GetAsmK =
            typeof(MultEngine).GetMethod("GetAsmKNeeds", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo GetLabK =
            typeof(MultEngine).GetMethod("GetLabKNeeds", BindingFlags.Static | BindingFlags.NonPublic);

        [HarmonyPatch(typeof(AssemblerComponent), "UpdateNeeds")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> AsmNeeds(IEnumerable<CodeInstruction> instructions)
        {
            return CountMultiplyTranspiler.Apply(instructions, GetAsmK, GetAsmK);
        }

        [HarmonyPatch(typeof(LabComponent), "UpdateNeedsAssemble")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> LabNeeds(IEnumerable<CodeInstruction> instructions)
        {
            return CountMultiplyTranspiler.Apply(instructions, GetLabK, GetLabK);
        }
    }

    /// <summary>科研站（制造模式）消耗/产出批量转译（消耗端自适应，产出端读锁存值）。</summary>
    public static class LabBatchPatch
    {
        private static readonly MethodInfo GetKConsume =
            typeof(MultEngine).GetMethod("GetLabKConsume", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo GetKProduce =
            typeof(MultEngine).GetMethod("GetLabKProduce", BindingFlags.Static | BindingFlags.NonPublic);

        [HarmonyPatch(typeof(LabComponent), "InternalUpdateAssemble")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(CountMultiplyTranspiler.Apply(instructions, GetKConsume, GetKProduce));
            for (int i = 0; i < list.Count; i++)
            {
                yield return list[i];
                // 科研站产物缓存上限：produced[i] + productCounts[i] <= 10 × (speedOverride+9999)/10000。
                // 常数 10 不乘 k 时，productCounts×k 超过该上限就永久停机。
                // 仅匹配「上限表达式」里的 ldc.i4.s 10（其后紧跟 ldarg.0 + ldfld speedOverride），
                // 方法内另有一处无关的 ldc.i4.s 10 不能动。
                if (list[i].opcode == OpCodes.Ldc_I4_S && list[i].operand is sbyte b && b == 10
                    && i + 2 < list.Count
                    && list[i + 1].opcode == OpCodes.Ldarg_0
                    && list[i + 2].opcode == OpCodes.Ldfld
                    && list[i + 2].operand is FieldInfo f && f.Name == "speedOverride")
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, GetKProduce);
                    yield return new CodeInstruction(OpCodes.Mul);
                }
            }
        }
    }

    /// <summary>
    /// 研究模式（矩阵）倍率：每 tick 把原版研究流程连续执行 k 次，
    /// 等价于 k 座科研站同时研究——矩阵消耗、哈希产出、增产剂效果全部 ×k。
    /// 原版每次调用前会把 research_speed 钳制到当前可用矩阵套数，
    /// 因此循环调用不会透支矩阵（不够时自动停下），天然自适应。
    /// </summary>
    public static class LabResearchPatch
    {
        // 循环体内对原方法的调用会再次触发本前缀，用该标志放行，避免无限递归
        [ThreadStatic] private static bool inLoop;

        [HarmonyPatch(typeof(LabComponent), "InternalUpdateResearch")]
        [HarmonyPrefix]
        private static bool Prefix(ref LabComponent __instance, float power, float research_speed,
            int[] consumeRegister, ref TechState ts, ref int techHashedThisFrame,
            ref long uMatrixPoint, ref long hashRegister, ref uint __result)
        {
            if (inLoop) return true;
            int planet = MultStore.CurrentPlanetId;
            int k = planet >= 0 ? MultStore.GetSet(planet, __instance.entityId) : 1;
            if (k <= 1) return true;

            inLoop = true;
            try
            {
                __result = __instance.InternalUpdateResearch(power, research_speed, consumeRegister,
                    ref ts, ref techHashedThisFrame, ref uMatrixPoint, ref hashRegister);
                for (int i = 1; i < k && __instance.replicating; i++)
                {
                    __instance.InternalUpdateResearch(power, research_speed, consumeRegister,
                        ref ts, ref techHashedThisFrame, ref uMatrixPoint, ref hashRegister);
                }
            }
            finally { inLoop = false; }
            return false;
        }
    }

    /// <summary>
    /// 研究模式矩阵缓存上限：原版 needs 阈值硬编码 36000（matrixServed[i] &lt; 36000 才继续要料）。
    /// 研究 ×k 后矩阵消耗 ×k，缓存同步放大为 36000×k，否则高速研究一直处在断料边缘。
    /// </summary>
    public static class ResearchNeedsPatch
    {
        private static readonly MethodInfo GetK =
            typeof(MultEngine).GetMethod("GetLabKSet", BindingFlags.Static | BindingFlags.NonPublic);

        [HarmonyPatch(typeof(LabComponent), "UpdateNeedsResearch")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ci in instructions)
            {
                yield return ci;
                if (ci.opcode == OpCodes.Ldc_I4 && ci.operand is int v && v == 36000)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, GetK);
                    yield return new CodeInstruction(OpCodes.Mul);
                }
            }
        }
    }

    /// <summary>
    /// 并行 tick 上下文注入：在 _assembler_parallel / _lab_produce_parallel
    /// 切换到新行星工厂时（factories[..] 取出存入局部变量处），
    /// 把该工厂行星写入 ThreadStatic，供批量转译器精确查 k。
    /// </summary>
    public static class ParallelContextTranspiler
    {
        private static readonly MethodInfo SetCtx =
            typeof(ParallelContextTranspiler).GetMethod(nameof(SetContext),
                BindingFlags.Static | BindingFlags.NonPublic);

        private static void SetContext(PlanetFactory factory)
        {
            MultStore.CurrentPlanetId = factory != null ? factory.planetId : -1;
        }

        internal static IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 2 < list.Count; i++)
            {
                // 匹配：ldfld GameLogic::factories ... ldelem.ref ... stloc
                if (list[i].opcode == OpCodes.Ldfld && list[i].operand is FieldInfo f
                    && f.Name == "factories" && f.DeclaringType.Name == "GameLogic")
                {
                    for (int j = i + 1; j < list.Count && j <= i + 12; j++)
                    {
                        if (list[j].opcode == OpCodes.Ldelem_Ref
                            && CountMultiplyTranspiler.GetStoreLocalIndex(list[j + 1]) >= 0)
                        {
                            // 在 stloc 前 dup 一份工厂引用设置上下文，避免重新加载局部变量
                            var inject = new List<CodeInstruction>
                            {
                                new CodeInstruction(OpCodes.Dup),
                                new CodeInstruction(OpCodes.Call, SetCtx)
                            };
                            list.InsertRange(j + 1, inject);
                            return list; // 每方法只注入一处
                        }
                    }
                }
            }
            return list;
        }
    }

    /// <summary>并行路径：上下文注入 + 帧末能耗扫池。</summary>
    public static class ParallelTickPatch
    {
        [HarmonyPatch(typeof(GameLogic), "_assembler_parallel")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> AsmTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ParallelContextTranspiler.Apply(instructions);
        }

        [HarmonyPatch(typeof(GameLogic), "_lab_produce_parallel")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> LabTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ParallelContextTranspiler.Apply(instructions);
        }

        [HarmonyPatch(typeof(GameLogic), "_assembler_parallel")]
        [HarmonyPostfix]
        private static void PostAsm(int threadOrdinal)
        {
            MultStore.CurrentPlanetId = -1;
            if (threadOrdinal == 0) MultEngine.EnergyPostAll();
        }

        [HarmonyPatch(typeof(GameLogic), "_lab_produce_parallel")]
        [HarmonyPostfix]
        private static void PostLab(int threadOrdinal)
        {
            MultStore.CurrentPlanetId = -1;
        }
    }

    /// <summary>串行回退路径（关闭多线程时）：行星上下文 + 帧末能耗。</summary>
    public static class FactoryContextPatch
    {
        [HarmonyPrefix, HarmonyPatch(typeof(FactorySystem), "GameTick")]
        private static void PreGameTick(FactorySystem __instance)
        {
            MultStore.CurrentPlanetId = __instance.factory != null ? __instance.factory.planetId : -1;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(FactorySystem), "GameTick")]
        private static void PostGameTick(FactorySystem __instance)
        {
            MultEngine.EnergyPost(__instance);
            MultStore.CurrentPlanetId = -1;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(FactorySystem), "GameTickLabProduceMode")]
        private static void PreLabProduce(FactorySystem __instance)
        {
            MultStore.CurrentPlanetId = __instance.factory != null ? __instance.factory.planetId : -1;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(FactorySystem), "GameTickLabProduceMode")]
        private static void PostLabProduce()
        {
            MultStore.CurrentPlanetId = -1;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(FactorySystem), "GameTickLabResearchMode")]
        private static void PreLabResearch(FactorySystem __instance)
        {
            MultStore.CurrentPlanetId = __instance.factory != null ? __instance.factory.planetId : -1;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(FactorySystem), "GameTickLabResearchMode")]
        private static void PostLabResearch()
        {
            MultStore.CurrentPlanetId = -1;
        }
    }

    /// <summary>
    /// 建筑设置复制/粘贴联动：游戏内复制建筑设置（窗口里的复制/粘贴按钮）时，
    /// 倍率跟随复制到目标建筑。
    /// </summary>
    public static class CopySettingPatch
    {
        // 原版整栋复制（框选/复制建筑连同设置）：走 BuildingParameters 剪贴板，
        // 复制时记录该建筑的倍率/无线，落地粘贴时写到新建筑上。
        [HarmonyPatch(typeof(BuildingParameters), "CopyFromFactoryObject")]
        [HarmonyPostfix]
        private static void BpCopy(int objectId, PlanetFactory factory)
        {
            if (factory == null || objectId <= 0) return;
            MultStore.ClipboardK = MultStore.GetSet(factory.planetId, objectId);
            MultStore.ClipboardWireless = MultStore.GetWireless(factory.planetId, objectId);
        }

        [HarmonyPatch(typeof(BuildingParameters), "ApplyPrebuildParametersToEntity")]
        [HarmonyPostfix]
        private static void BpPaste(int entityId, PlanetFactory factory)
        {
            if (factory == null || entityId <= 0) return;
            MultStore.SetMult(factory.planetId, entityId, MultStore.ClipboardK);
            MultStore.SetWireless(factory.planetId, entityId, MultStore.ClipboardWireless);
        }

        [HarmonyPatch(typeof(PlanetFactory), "OnCopyBuildingSetting")]
        [HarmonyPostfix]
        private static void OnCopy(PlanetFactory __instance, int entityId)
        {
            MultStore.ClipboardK = MultStore.GetSet(__instance.planetId, entityId);
            MultStore.ClipboardWireless = MultStore.GetWireless(__instance.planetId, entityId);
        }

        [HarmonyPatch(typeof(PlanetFactory), "OnPasteBuildingSetting")]
        [HarmonyPostfix]
        private static void OnPaste(PlanetFactory __instance, int entityId)
        {
            MultStore.SetMult(__instance.planetId, entityId, MultStore.ClipboardK);
            MultStore.SetWireless(__instance.planetId, entityId, MultStore.ClipboardWireless);
        }
    }
}
