using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace CondensedProduction
{
    /// <summary>
    /// 传送带堆叠扩容（原版最高级传送带 / 白爪 / 物流塔出口）。
    ///
    /// 技术上 Cargo.stack 是 byte，最大 255（原版最高 4 层），所以实际生效值按 255 封顶。
    /// 三条扩容路径：
    ///  1. 堆叠器合并上限：转译 PilerComponent.InternalUpdate 里 3 处 ldc.i4.4；
    ///  2. 白爪（本体最高级分拣器，canStack 为真）：周期性把 stackInput/stackOutput 抬到上限，
    ///     库存不足时按实际数量输出（游戏本身的行为）；
    ///  3. 物流站出口：把 StationComponent.pilerCount（出塔堆叠）抬到上限。
    /// 另外 CargoContainer.Draw 绘制前把 stack 临时压回 4 层，画完还原，保证渲染与原版一致。
    /// </summary>
    public static class BeltStack
    {
        /// <summary>实际生效的堆叠上限（1~255）。</summary>
        public static int MaxStack
        {
            get
            {
                int v = Plugin.BeltStackMaxEntry == null ? 255 : Plugin.BeltStackMaxEntry.Value;
                if (v < 1) return 1;
                return v > 255 ? 255 : v;
            }
        }

        public static bool Enabled
        {
            get { return Plugin.BeltStackEnabledEntry == null || Plugin.BeltStackEnabledEntry.Value; }
        }

    }

    /// <summary>
    /// 白爪往传送带放料：原版按 4 层计算「手上物品要占几格」stackCount=(itemCount-1)/4+1，
    /// 抓 255 个会要 64 个连续空位导致放不下去。把该处 /4 改成 /上限。
    /// 方法内其余的 ldc.i4.4 都是数组下标（needs[4] 等），仅匹配「后面紧跟 div」的那处。
    /// </summary>
    public static class InserterStackPatch
    {
        /// <summary>
        /// 使用点前置写入：分拣器每次运行前把 stackInput/stackOutput 设好。
        /// 部分存档的 inserterCursor 与池实际内容不一致，清扫式设置会漏，这里必定生效。
        /// 游戏有三个 tick 变体（普通/双向/无动画），全部挂上。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InserterComponent), "InternalUpdate")]
        [HarmonyPatch(typeof(InserterComponent), "InternalUpdate_Bidirectional")]
        [HarmonyPatch(typeof(InserterComponent), "InternalUpdateNoAnim")]
        private static void Prefix(ref InserterComponent __instance)
        {
            if (!BeltStack.Enabled || !__instance.canStack) return;
            if (__instance.stackInput != 1) __instance.stackInput = 1;
            int max = BeltStack.MaxStack;
            if (__instance.stackOutput != (byte)max) __instance.stackOutput = (byte)max;
        }

        /// <summary>stackCount=(itemCount-1)/4+1 的 /4 → /上限，三个 tick 变体都打。</summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(InserterComponent), "InternalUpdate")]
        [HarmonyPatch(typeof(InserterComponent), "InternalUpdate_Bidirectional")]
        [HarmonyPatch(typeof(InserterComponent), "InternalUpdateNoAnim")]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int max = BeltStack.MaxStack;
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_4
                    && i + 1 < list.Count && list[i + 1].opcode == OpCodes.Div)
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4, max);
                    continue;
                }
                yield return list[i];
            }
        }
    }

    /// <summary>
    /// 物流站出口层数：在 UpdateOutputSlots 使用前直接把 pilerCount 抬到配置层数。
    /// pilerCount 是玩家设置项（0 = 跟随科技上限），清扫式修改不可靠，
    /// 必须在使用点前置写入才能稳定生效。
    /// </summary>
    public static class StationOutputPatch
    {
        [HarmonyPatch(typeof(StationComponent), "UpdateOutputSlots")]
        [HarmonyPrefix]
        private static void Prefix(StationComponent __instance)
        {
            if (!BeltStack.Enabled) return;
            int max = BeltStack.MaxStack;

            // 原来这里是「planetId<=0 就跳过」，但那样会把某些 mod 生成、planetId 为 0 的
            // 真实物流塔也一起跳过（那些塔照样有出料带，结果堆叠一直是原版层数）。
            // 改成按「是否真的接了传送带」判断：没有出料带的虚拟/幽灵站不动它。
            bool hasBelt = false;
            var slotArr = __instance.slots;
            if (slotArr != null)
                for (int i = 0; i < slotArr.Length; i++)
                    if (slotArr[i].beltId > 0) { hasBelt = true; break; }
            if (hasBelt && max > 4 && __instance.pilerCount != max)
                __instance.pilerCount = max;
        }
    }

    /// <summary>堆叠器合并上限：把三处硬编码的 4 层改成配置值。</summary>
    public static class PilerStackPatch
    {
        [HarmonyPatch(typeof(PilerComponent), "InternalUpdate")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int max = BeltStack.MaxStack;
            foreach (var ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldc_I4_4)
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4, max);
                }
                else if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && f == 4f)
                {
                    // 增产剂点数按「一整车」计算，层数提高后同步放大，避免增产剂点数丢失
                    yield return new CodeInstruction(OpCodes.Ldc_R4, (float)max);
                }
                else
                {
                    yield return ci;
                }
            }
        }
    }

    /// <summary>
    /// 渲染保真：绘制前把传送带物品堆叠数临时压回 4 层，绘制后还原，
    /// 这样逻辑上堆了 255 个，画面上仍是原版 4 层的样子。
    /// </summary>
    public static class CargoVisualClampPatch
    {
        private static readonly List<int> stash = new List<int>(2048);

        [HarmonyPatch(typeof(CargoContainer), "Draw")]
        [HarmonyPrefix]
        private static void PreDraw(CargoContainer __instance)
        {
            if (Plugin.KeepVisualStackEntry == null || !Plugin.KeepVisualStackEntry.Value) return;
            Cargo[] pool = __instance.cargoPool;
            if (pool == null) return;
            int cursor = Math.Min(__instance.cursor, pool.Length);
            stash.Clear();
            for (int i = 0; i < cursor; i++)
            {
                if (pool[i].stack > 4)
                {
                    stash.Add(i);
                    stash.Add(pool[i].stack);
                    pool[i].stack = 4;
                }
            }
        }

        [HarmonyPatch(typeof(CargoContainer), "Draw")]
        [HarmonyPostfix]
        private static void PostDraw(CargoContainer __instance)
        {
            if (stash.Count == 0) return;
            Cargo[] pool = __instance.cargoPool;
            if (pool != null)
            {
                int len = pool.Length;
                for (int k = 0; k + 1 < stash.Count; k += 2)
                {
                    int idx = stash[k];
                    if (idx >= 0 && idx < len) pool[idx].stack = (byte)stash[k + 1];
                }
            }
            stash.Clear();
        }
    }

    /// <summary>
    /// 建筑抓取扩容：PlanetFactory.PickFrom（两个重载）原版从任何建筑一次只抓 1 个物品，
    /// 这里把每次抓取量提升到堆叠上限（默认 255），并让分拣器一抓即挥臂。
    /// 配合 stackInput=1，白爪从物流站/生产建筑一次摆臂即可拿走 255 个。
    /// 1) 产物/库存槽的「-=1」改为「-=min(上限, 存量)」，抓出数量写回 stack 出参；
    /// 2) 物流站 TakeItem 的请求数 1→上限，成功判据 count==1 改为 count>0，
    ///    并把实际数量写入 stack 出参（否则数量对不上会凭空吞物品）。
    /// </summary>
    [HarmonyPatch]
    public static class BuildingGrabPatch
    {
        /// <summary>从建筑的整型库存槽抓取：取 min(上限, 存量)，并把实际抓取数写入 stack 出参。</summary>
        public static void Grab(ref int slot, ref byte stackOut)
        {
            int max = BeltStack.MaxStack;
            int take = slot < max ? slot : max;
            slot -= take;
            stackOut = (byte)take;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var byrefByte = typeof(byte).MakeByRefType();
            yield return AccessTools.Method(typeof(PlanetFactory), "PickFrom",
                new[] { typeof(int), typeof(int), typeof(int), typeof(int[]), byrefByte, byrefByte });
            yield return AccessTools.Method(typeof(PlanetFactory), "PickFrom",
                new[] { typeof(uint), typeof(int), typeof(int), typeof(int[]), byrefByte, byrefByte });
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo grab = AccessTools.Method(typeof(BuildingGrabPatch), "Grab");
            MethodInfo takeItem = null;
            ParameterInfo stackParam = null;
            foreach (var p in original.GetParameters())
                if (p.Name == "stack") stackParam = p;

            // Harmony 发射 InlineVar 指令时不接受 ParameterInfo 操作数，
            // 必须用整数参数索引（实例方法 +1，第 0 位是 this）。
            int stackArgIndex = stackParam == null ? -1 : stackParam.Position + (original.IsStatic ? 0 : 1);
            CodeInstruction stackLd = stackArgIndex >= 0 ? new CodeInstruction(OpCodes.Ldarg_S, stackArgIndex) : null;

            // ---- 第一遍：物流站分支 ----
            // TakeItem 调用点：请求数 1→上限；调用后把实际数量写入 stack 出参；成功判据 ==1 → >0
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsTakeItemCall(list[i], ref takeItem)) continue;

                // 找 count 局部变量：调用前最后一个 ldloca.s X（参数为 ref itemId, ref count, ...）
                LocalBuilder countLocal = null;
                bool seenOneRef = false;
                for (int j = i - 1; j >= 0 && j >= i - 8; j--)
                {
                    var lb = GetLocalBuilder(list[j], OpCodes.Ldloca_S, OpCodes.Ldloca);
                    if (lb != null)
                    {
                        if (seenOneRef) { countLocal = lb; break; }
                        seenOneRef = true;
                    }
                }
                if (countLocal == null) continue;

                // 请求数：向前找 ldc.i4.1; stloc.s countLocal
                for (int j = i - 1; j >= 0 && j >= i - 30; j--)
                {
                    if (list[j].opcode == OpCodes.Ldc_I4_1 &&
                        j + 1 < list.Count && IsStoreOf(list[j + 1], countLocal))
                    {
                        list[j] = new CodeInstruction(OpCodes.Ldc_I4, 255);
                        break;
                    }
                }

                // 调用后插入：stack = (byte)count
                var storeStack = new List<CodeInstruction>();
                if (stackLd != null)
                {
                    storeStack.Add(stackLd.Clone());
                    storeStack.Add(new CodeInstruction(OpCodes.Ldloc_S, countLocal));
                    storeStack.Add(new CodeInstruction(OpCodes.Conv_U1));
                    storeStack.Add(new CodeInstruction(OpCodes.Stind_I1));
                }
                list.InsertRange(i + 1, storeStack);
                i += storeStack.Count;

                // 成功判据：向后找 ldloc countLocal; ldc.i4.1; bne.un → ldc.i4.0; beq
                for (int j = i + 1; j < list.Count && j <= i + 10; j++)
                {
                    if (IsLoadOf(list[j], countLocal) &&
                        j + 2 < list.Count &&
                        list[j + 1].opcode == OpCodes.Ldc_I4_1 &&
                        list[j + 2].opcode == OpCodes.Bne_Un)
                    {
                        list[j + 1] = new CodeInstruction(OpCodes.Ldc_I4_0);
                        list[j + 2] = new CodeInstruction(OpCodes.Beq, list[j + 2].operand);
                        break;
                    }
                }
            }

            // ---- 第二遍：产物/库存槽「-=1」模式 ----
            for (int i = 0; i + 5 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldelema &&
                    list[i + 1].opcode == OpCodes.Dup &&
                    list[i + 2].opcode == OpCodes.Ldind_I4 &&
                    list[i + 3].opcode == OpCodes.Ldc_I4_1 &&
                    list[i + 4].opcode == OpCodes.Sub &&
                    list[i + 5].opcode == OpCodes.Stind_I4)
                {
                    if (stackLd == null) continue;
                    list[i + 1] = stackLd.Clone();
                    list[i + 2] = new CodeInstruction(OpCodes.Call, grab);
                    list[i + 3] = new CodeInstruction(OpCodes.Nop);
                    list[i + 4] = new CodeInstruction(OpCodes.Nop);
                    list[i + 5] = new CodeInstruction(OpCodes.Nop);
                    i += 5;
                }
            }

            return list;
        }

        private static LocalBuilder GetLocalBuilder(CodeInstruction ci, OpCode shortForm, OpCode longForm)
        {
            if (ci.opcode != shortForm && ci.opcode != longForm) return null;
            return ci.operand as LocalBuilder;
        }

        private static bool IsStoreOf(CodeInstruction ci, LocalBuilder lb)
        {
            return (ci.opcode == OpCodes.Stloc_S || ci.opcode == OpCodes.Stloc) &&
                ci.operand is LocalBuilder x && x.LocalIndex == lb.LocalIndex;
        }

        private static bool IsLoadOf(CodeInstruction ci, LocalBuilder lb)
        {
            return (ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloc) &&
                ci.operand is LocalBuilder x && x.LocalIndex == lb.LocalIndex;
        }

        private static bool IsTakeItemCall(CodeInstruction ci, ref MethodInfo cache)
        {
            if (ci.opcode != OpCodes.Callvirt) return false;
            var m = ci.operand as MethodInfo;
            if (m == null || m.DeclaringType != typeof(StationComponent)) return false;
            if (m.Name != "TakeItem") return false;
            // 只处理 ref itemId, ref count 版本（无 needs 数组的版本会走同一判定，两个重载都允许）
            cache = m;
            return true;
        }

    }
}
