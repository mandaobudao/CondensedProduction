using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CondensedProduction
{
    /// <summary>
    /// 分馏塔高速出料：堆叠时代下原版输出是瓶颈（流体每口每 tick 只出 2 个、产物每 tick 1 个），
    /// 而输入/处理速度随货物堆叠数放大（progress 公式里乘了平均每格物品数），
    /// 输出缓冲瞬间打满导致分馏停摆，带上的货也只剩 1~2 个/格。
    ///
    /// 做法：转译 InternalUpdate，把 CargoPath.TryUpdateItemAtHeadAndFillBlank（流体输出口）
    /// 与 CargoTraffic.TryInsertItemAtHead（产物输出口）换成批量版本——一次把输出缓冲倒空，
    /// 每格货物直接按堆叠上限（默认 255）出。账本是「我们预扣 moved-1，原版 IL 收尾再扣 1」，
    /// 与原版逐条 -1 的会计结果完全一致。
    /// </summary>
    public static class FractionatorPatch
    {
        [HarmonyPatch(typeof(FractionatorComponent), "InternalUpdate")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var updateMI = AccessTools.Method(typeof(CargoPath), "TryUpdateItemAtHeadAndFillBlank");
            var insertMI = AccessTools.Method(typeof(CargoTraffic), "TryInsertItemAtHead",
                new[] { typeof(int), typeof(int), typeof(byte), typeof(byte) });
            var fluidHelper = AccessTools.Method(typeof(FractionatorPatch), nameof(FluidOutputBatch));
            var productHelper = AccessTools.Method(typeof(FractionatorPatch), nameof(ProductOutputBatch));
            var capHelper = AccessTools.Method(typeof(FractionatorPatch), nameof(ScaleCap));
            var fluidMaxF = AccessTools.Field(typeof(FractionatorComponent), "fluidOutputMax");
            var productMaxF = AccessTools.Field(typeof(FractionatorComponent), "productOutputMax");

            foreach (var ci in instructions)
            {
                if (updateMI != null && ci.Calls(updateMI))
                {
                    // 原栈: path, itemId, maxStack, stack, inc → 追加 this 引用后换批量版
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, fluidHelper);
                    continue;
                }
                if (insertMI != null && ci.Calls(insertMI))
                {
                    // 原栈: traffic, beltId, itemId, stack, inc → 追加 this 引用后换批量版
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, productHelper);
                    continue;
                }
                yield return ci;
                // 输出缓冲上限的闸门比较（fluidOutputMax/productOutputMax）按堆叠上限放大，
                // 否则每 tick 处理量被小缓冲卡死
                if (capHelper != null &&
                    ((fluidMaxF != null && ci.LoadsField(fluidMaxF)) || (productMaxF != null && ci.LoadsField(productMaxF))))
                {
                    yield return new CodeInstruction(OpCodes.Call, capHelper);
                }
            }
        }

        /// <summary>缓冲上限闸门：堆叠开启时按堆叠上限放大（原版缓冲只有几格货物的量）。</summary>
        private static int ScaleCap(int orig)
        {
            if (!BeltStack.Enabled) return orig;
            int max = BeltStack.MaxStack;
            return max > 4 ? orig * max : orig;
        }

        /// <summary>流体输出口（氢返回带）：一次吐空 fluidOutputCount。</summary>
        private static bool FluidOutputBatch(CargoPath path, int itemId, int maxStack, byte stack, byte incAvg, ref FractionatorComponent comp)
        {
            if (!BeltStack.Enabled || BeltStack.MaxStack <= 4)
                return path.TryUpdateItemAtHeadAndFillBlank(itemId, maxStack, stack, incAvg);

            int max = BeltStack.MaxStack;
            int remaining = comp.fluidOutputCount;
            if (remaining <= 0) return false;

            int moved = 0;
            long incMoved = 0;
            while (moved < remaining)
            {
                int take = remaining - moved;
                if (take > max) take = max;
                // 增产剂点数按字节存，防止 incAvg*take 溢出
                if (incAvg > 0 && take > 255 / incAvg) take = 255 / incAvg;
                if (take <= 0) take = 1;
                if (!path.TryUpdateItemAtHeadAndFillBlank(itemId, max, (byte)take, (byte)(incAvg * take))) break;
                moved += take;
                incMoved += incAvg * take;
            }
            if (moved <= 0) return false;

            // 原版 IL 在返回 true 后还会再扣 1 个 count 和 incAvg 个 inc，这里预扣其余部分
            comp.fluidOutputCount -= (moved - 1);
            long incLeft = (long)comp.fluidOutputInc - (incMoved - incAvg);
            comp.fluidOutputInc = (int)(incLeft < incAvg ? incAvg : incLeft);
            return true;
        }

        /// <summary>产物输出口（重氢）：一次吐空 productOutputCount。</summary>
        private static bool ProductOutputBatch(CargoTraffic traffic, int beltId, int itemId, byte stack, byte inc, ref FractionatorComponent comp)
        {
            if (!BeltStack.Enabled || BeltStack.MaxStack <= 4)
                return traffic.TryInsertItemAtHead(beltId, itemId, stack, inc);

            int max = BeltStack.MaxStack;
            int remaining = comp.productOutputCount;
            if (remaining <= 0) return false;

            int moved = 0;
            while (moved < remaining)
            {
                int take = remaining - moved;
                if (take > max) take = max;
                if (!traffic.TryInsertItemAtHead(beltId, itemId, (byte)take, inc)) break;
                moved += take;
            }
            if (moved <= 0) return false;

            // 原版 IL 在返回 true 后还会再扣 1 个 productOutputCount，这里预扣其余部分
            comp.productOutputCount -= (moved - 1);
            return true;
        }
    }
}
