using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CondensedProduction
{
    /// <summary>
    /// 储液罐整车出料。TankComponent.GameTick 里 4 条出料带（belt0~belt3）各有一处
    /// 硬编码：每次只往传送带插 1 个，插成功后 fluidCount -= 1。
    /// 这里把「插 1 个」换成「把罐里有的全插进去（单格上限 255）」，
    /// 并预先补扣 take-1，使原版收尾的 -=1 记账仍然正确。
    /// </summary>
    public static class TankPatch
    {
        [HarmonyPatch(typeof(TankComponent), "GameTick")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var insert = AccessTools.Method(typeof(CargoTraffic), "TryInsertItemAtHead",
                new[] { typeof(int), typeof(int), typeof(byte), typeof(byte) });
            var helper = AccessTools.Method(typeof(TankPatch), "TankInsert");
            var list = new List<CodeInstruction>(instructions);

            for (int i = 0; i < list.Count; i++)
            {
                var ci = list[i];
                // 原版：ldc.i4.1（数量 1）; ldloc incPerItem; conv.u1; callvirt TryInsertItemAtHead
                if (ci.opcode == OpCodes.Ldc_I4_1 && i + 3 < list.Count
                    && (list[i + 1].opcode == OpCodes.Ldloc_S || list[i + 1].opcode == OpCodes.Ldloc)
                    && list[i + 2].opcode == OpCodes.Conv_U1
                    && insert != null && list[i + 3].Calls(insert))
                {
                    // 保留 incPerItem（int），丢掉 ldc.i4.1 与 conv.u1，追加 this
                    yield return list[i + 1];
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, helper);
                    i += 3;
                    continue;
                }
                yield return ci;
            }
        }

        private static bool TankInsert(CargoTraffic traffic, int beltId, int itemId, int incPerItem,
            ref TankComponent comp)
        {
            int max = BeltStack.MaxStack;
            if (!BeltStack.Enabled || max <= 4)
                return traffic.TryInsertItemAtHead(beltId, itemId, 1, (byte)incPerItem);

            int take = comp.fluidCount;
            if (take > max) take = max;
            // inc 参数是 byte，一次插太多会溢出
            if (incPerItem > 0 && take > 255 / incPerItem) take = 255 / incPerItem;
            if (take < 1) take = 1;

            if (!traffic.TryInsertItemAtHead(beltId, itemId, (byte)take, (byte)(incPerItem * take)))
                return false;

            // 原版成功后还会再扣 1 个和 1 份 inc，这里只补扣 take-1
            comp.fluidCount -= (take - 1);
            comp.fluidInc -= incPerItem * (take - 1);
            return true;
        }
    }
}
