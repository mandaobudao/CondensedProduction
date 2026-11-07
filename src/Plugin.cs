using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.IO;
using crecheng.DSPModSave;

namespace CondensedProduction
{
    /// <summary>
    /// 浓缩生产：任意配方生产建筑可设定倍率 N，等效于 N 台相同建筑合为一体。
    /// 生产时间不变，原料消耗、产物产出与能耗均按实际运行倍率缩放；
    /// 实际倍率根据当前输入物品量自适应，不会超过设定上限。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("crecheng.DSPModSave", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin, IModCanSave
    {
        public const string GUID = "com.mandao.CondensedProduction";
        public const string NAME = "CondensedProduction";
        public const string VERSION = "1.5.1";

        public static ConfigEntry<int> MaxMultiplierEntry;
        public static ConfigEntry<bool> ScaleOutputStorageEntry;
        public static ConfigEntry<int> InputBufferMultiplierEntry;
        public static ConfigEntry<int> EnergyBufferSecondsEntry;
        public static ConfigEntry<int> ContainerMultiplierEntry;
        public static ConfigEntry<bool> GlobalSupplyEnabledEntry;
        public static ConfigEntry<int> GlobalSupplyIntervalEntry;
        public static ConfigEntry<bool> GlobalSupplyCollectEntry;
        public static ConfigEntry<bool> BeltStackEnabledEntry;
        public static ConfigEntry<int> BeltStackMaxEntry;
        public static ConfigEntry<bool> KeepVisualStackEntry;

        private void Awake()
        {
            MaxMultiplierEntry = Config.Bind("通用", "MaxMultiplier", 1000,
                "单个建筑可设定的倍率上限（1~100000，0 表示不限制）。实际运行倍率始终受输入物品量与引擎速度上限自适应。");
            ScaleOutputStorageEntry = Config.Bind("通用", "ScaleOutputStorage", true,
                "是否按设定倍率放大熔炉/装配机的产物缓存上限（熔炉100→100×N等）。");
            EnergyBufferSecondsEntry = Config.Bind("电力", "EnergyBufferSeconds", 10,
                new ConfigDescription("每栋倍率建筑的内部储能时长（秒，按满倍率功耗计）。电网按平均功率平稳充电，" +
                    "瞬时消耗从储能扣除；电网不足时靠储能维持运行并逐步降速，而不是功率瞬间跳变。0 = 关闭储能",
                    new AcceptableValueRange<int>(0, 120)));
            InputBufferMultiplierEntry = Config.Bind("通用", "InputBufferMultiplier", 3,
                "原料缓存系数：缓存上限 = 原版上限 × 设定倍率 × 该值。越大越能囤料，供料波动时不易停机。");
            ContainerMultiplierEntry = Config.Bind("通用", "ContainerMultiplier", 100,
                "容器每格上限倍数：物流站、储物箱、罐体、玩家背包的每格容量 = 原版 × 该值。");
            GlobalSupplyEnabledEntry = Config.Bind("无线供料", "Enabled", true,
                "启用无线供料：开启了「无线」的生产建筑，由本行星的原版行星物流站/星际物流站直接供料，无需分拣器。");
            GlobalSupplyIntervalEntry = Config.Bind("无线供料", "IntervalTicks", 30,
                "无线供料执行间隔（游戏刻）。越小补料越及时，CPU 开销越大。");
            GlobalSupplyCollectEntry = Config.Bind("无线供料", "Collect", true,
                "是否把开启了「无线」的建筑的产出直接回收到本行星物流站库存。");
            BeltStackEnabledEntry = Config.Bind("传送带堆叠", "Enabled", true,
                "启用传送带堆叠扩容（堆叠器、白爪、物流站出口）。");
            BeltStackMaxEntry = Config.Bind("传送带堆叠", "MaxStack", 255,
                "堆叠上限。游戏内堆叠数存为单字节，最大 255（原版 4 层）。");
            KeepVisualStackEntry = Config.Bind("传送带堆叠", "KeepVisualStack", true,
                "保持原版渲染：绘制时把堆叠临时压回 4 层，画面与原版一致（仅影响显示）。");

            // 旧版本默认上限是 100，配置文件会保留旧值导致设 1000 被悄悄钳到 100。
            // 只迁移一次：把 ≤100 的旧值抬到 1000，之后玩家自己的修改不再被改动。
            var migrated = Config.Bind("兼容", "MultiplierMigrated", false,
                "内部使用：记录已将旧的 100 倍上限迁移为 1000。");
            if (!migrated.Value)
            {
                if (MaxMultiplierEntry.Value > 0 && MaxMultiplierEntry.Value <= 100)
                    MaxMultiplierEntry.Value = 1000;
                migrated.Value = true;
                Config.Save();
            }

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(ParallelTickPatch));
            harmony.PatchAll(typeof(FactoryContextPatch));
            harmony.PatchAll(typeof(AssemblerBatchPatch));
            harmony.PatchAll(typeof(LabBatchPatch));
            harmony.PatchAll(typeof(LabResearchPatch));
            harmony.PatchAll(typeof(ResearchNeedsPatch));
            harmony.PatchAll(typeof(NeedsBufferPatch));
            harmony.PatchAll(typeof(PowerSamplePatch));
            harmony.PatchAll(typeof(CopySettingPatch));
            harmony.PatchAll(typeof(VFInputGuardPatch));
            harmony.PatchAll(typeof(StorageCapPatch));
            harmony.PatchAll(typeof(StationLimitPatch));
            harmony.PatchAll(typeof(StationCapTechPatch));
            harmony.PatchAll(typeof(BuildingGrabPatch));
            harmony.PatchAll(typeof(GlobalSupplyPatch));
            harmony.PatchAll(typeof(PilerStackPatch));
            harmony.PatchAll(typeof(InserterStackPatch));
            harmony.PatchAll(typeof(StationOutputPatch));
            harmony.PatchAll(typeof(CargoVisualClampPatch));
            harmony.PatchAll(typeof(FractionatorPatch));
            harmony.PatchAll(typeof(MinerPatch));
            harmony.PatchAll(typeof(ContainerOutputPatch));
            harmony.PatchAll(typeof(TankPatch));

            MultiplierUI.Init();
            MultStore.Log = Logger;
            Logger.LogInfo("CondensedProduction " + VERSION + " loaded.");
        }

        private void OnGUI()
        {
            MultiplierUI.Draw();
        }

        public void Export(BinaryWriter w)
        {
            MultStore.Export(w);
        }

        public void Import(BinaryReader r)
        {
            MultStore.Import(r);
        }

        public void IntoOtherSave()
        {
            MultStore.ClearAll();
        }
    }
}
