using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CondensedProduction
{
    /// <summary>
    /// 容器整车出料：储物仓 / 储液罐（以及分流器上挂的容器）往传送带倒货时，
    /// 原版 PlanetFactory.BeltFastFillIn 每格每次只取 1 个（循环里局部变量初始化为 1：
    /// ldc.i4.1 → stloc.s V_14，再传给 StorageComponent.TakeItemFromGrid），
    /// 所以箱子里堆再多，带上永远是一个一个出来。
    ///
    /// 把该初始值改成堆叠上限（255），每格每 tick 一次倒满一格。
    /// 后续 conv.u1 强转成 byte，255 正好不溢出；TakeItemFromGrid 本身按格内实际存量封顶。
    /// </summary>
    public static class ContainerOutputPatch
    {
        [HarmonyPatch(typeof(PlanetFactory), "BeltFastFillIn")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int max = BeltStack.MaxStack;
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 1 < list.Count; i++)
            {
                // 只认「ldc.i4.1 紧跟 stloc」这一处（取货数量初始化为 1）
                if (list[i].opcode == OpCodes.Ldc_I4_1
                    && (list[i + 1].opcode == OpCodes.Stloc_S || list[i + 1].opcode == OpCodes.Stloc))
                {
                    list[i] = new CodeInstruction(OpCodes.Ldc_I4, max);
                }
            }
            return list;
        }
    }
}
