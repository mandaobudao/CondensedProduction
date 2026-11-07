using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CondensedProduction
{
    /// <summary>
    /// 矿机整车出矿：原版 MinerComponent.InternalUpdate 末尾把 productCount 按
    /// min(存货, 1~4) 塞进传送带，而且 productCount 的缓冲上限只有 50，
    /// 所以无论堆叠上限调多高，矿机带上永远是一两个。
    ///
    /// 三处干预：
    ///  1. 三处 productCount ≥ 50 的储量闸门 → 堆叠上限（让存货能攒过 50 而不停摆）；
    ///  2. PlanetFactory.InsertInto 出料调用换成 MinerInsert：每次出料把存货全部吐出；
    ///  3. 返回值仍是实际插入数，原版 productCount -= 返回值 的记账保持不变。
    /// </summary>
    public static class MinerPatch
    {
        [HarmonyPatch(typeof(MinerComponent), "InternalUpdate")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int max = BeltStack.MaxStack;
            var insertInto = AccessTools.Method(typeof(PlanetFactory), "InsertInto",
                new[]
                {
                    typeof(int), typeof(int), typeof(int), typeof(byte), typeof(byte), typeof(byte).MakeByRefType()
                });
            var helper = AccessTools.Method(typeof(MinerPatch), "MinerInsert");

            foreach (var ci in instructions)
            {
                // 缓冲上限 50 → 堆叠上限
                if (ci.opcode == OpCodes.Ldc_I4_S && ci.operand is sbyte sb && sb == 50)
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4, max);
                    continue;
                }
                if (insertInto != null && ci.Calls(insertInto))
                {
                    // 原栈: factory, entityId, offset, itemId, count, inc, ref remainInc
                    // 追加 this（ref MinerComponent）后换成带攒料的版本
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, helper);
                    continue;
                }
                yield return ci;
            }
        }

        /// <summary>
        /// 每次出料机会（原版每 tick 一次）把矿机内全部存货一次吐出来，
        /// 单格上限仍是堆叠上限（255），超出部分留到下一次出料。
        /// </summary>
        private static int MinerInsert(PlanetFactory factory, int entityId, int offset, int itemId,
            byte itemCount, byte itemInc, out byte remainInc, ref MinerComponent comp)
        {
            if (!BeltStack.Enabled)
                return factory.InsertInto(entityId, offset, itemId, itemCount, itemInc, out remainInc);

            int max = BeltStack.MaxStack;
            if (max <= 4)
                return factory.InsertInto(entityId, offset, itemId, itemCount, itemInc, out remainInc);

            int takeCount = comp.productCount;
            if (takeCount <= 0)
            {
                remainInc = 0;
                return 0;
            }
            if (takeCount > max) takeCount = max;
            return factory.InsertInto(entityId, offset, itemId, (byte)takeCount, itemInc, out remainInc);
        }
    }
}
