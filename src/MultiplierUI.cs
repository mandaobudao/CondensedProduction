using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CondensedProduction
{
    /// <summary>
    /// 建筑窗口内嵌倍率面板（IMGUI 实现，跟随原版建筑窗口右上角外侧显示，可拖动偏移）。
    /// 支持组装机类窗口（熔炉/制造台/化工厂/精炼厂/对撞机）、科研站窗口。
    /// 面板显示设定倍率（可编辑）与最近一个tick的实际运行倍率。
    /// </summary>
    public static class MultiplierUI
    {
        private const int WinIdAssembler = 93101;
        private const int WinIdLab = 93102;
        private const float PanelW = 230f;
        private const float PanelH = 178f;

        // 能量条用的 1x1 白纹理（染色绘制）
        private static Texture2D whiteTex;

        private static string inputText = "";
        private static int editingKey = -1;

        // UILabWindow.factory / factorySystem 是私有字段，用反射读取
        private static FieldInfo labFactoryField;
        private static FieldInfo labFactorySystemField;

        // 每个窗口的用户拖动偏移（相对自动停靠位）
        private static readonly Dictionary<int, Vector2> userOffset = new Dictionary<int, Vector2>();
        private static readonly Vector3[] cornersBuf = new Vector3[4];

        // 供 VFInput 屏蔽补丁查询：本帧面板是否显示、鼠标是否悬停在面板上
        public static bool PanelShowing { get; private set; }
        private static Rect lastPanelRect;

        public static void Init()
        {
            labFactoryField = typeof(UILabWindow).GetField("factory",
                BindingFlags.NonPublic | BindingFlags.Instance);
            labFactorySystemField = typeof(UILabWindow).GetField("factorySystem",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        /// <summary>鼠标（Input.mousePosition 坐标系）是否悬停在面板上。</summary>
        public static bool IsMouseOverPanel()
        {
            if (!PanelShowing) return false;
            Vector3 m = Input.mousePosition;
            return m.x >= lastPanelRect.x && m.x <= lastPanelRect.x + lastPanelRect.width
                && m.y >= Screen.height - lastPanelRect.y - lastPanelRect.height
                && m.y <= Screen.height - lastPanelRect.y;
        }

        public static void Draw()
        {
            PanelShowing = false;
            if (GameMain.instance == null || GameMain.isPaused) return;
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame == null) return;

            var asmWin = uiGame.assemblerWindow;
            if (asmWin != null && asmWin.active && asmWin.assemblerId != 0 && asmWin.factorySystem != null)
            {
                AssemblerComponent comp = asmWin.factorySystem.assemblerPool[asmWin.assemblerId];
                DrawPanel(WinIdAssembler, asmWin.windowTrans,
                    asmWin.factorySystem.factory, comp.entityId);
                return;
            }

            var labWin = uiGame.labWindow;
            if (labWin != null && labWin.active && labWin.labId != 0)
            {
                var factory = labFactoryField != null ? labFactoryField.GetValue(labWin) as PlanetFactory : null;
                var fsys = labFactorySystemField != null ? labFactorySystemField.GetValue(labWin) as FactorySystem : null;
                if (factory != null && fsys != null)
                {
                    LabComponent comp = fsys.labPool[labWin.labId];
                    DrawPanel(WinIdLab, labWin.windowTrans, factory, comp.entityId);
                }
                return;
            }
        }

        private static Vector2 ComputeDock(RectTransform anchor)
        {
            // 默认：屏幕右侧中部
            Vector2 fallback = new Vector2(Screen.width - PanelW - 20, Screen.height * 0.3f);
            if (anchor == null) return fallback;
            anchor.GetWorldCorners(cornersBuf);
            // 退化矩形（窗口还没布局好）时回退
            if ((cornersBuf[2] - cornersBuf[0]).sqrMagnitude < 1f) return fallback;
            Camera cam = null;
            var canvas = anchor.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                cam = canvas.worldCamera;
            // cornersBuf[2] = 窗口右上角（屏幕坐标 y 自底向上）
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, cornersBuf[2]);
            float x = Mathf.Clamp(tr.x + 8, 0, Screen.width - PanelW);
            float y = Mathf.Clamp(Screen.height - tr.y, 0, Screen.height - PanelH);
            return new Vector2(x, y);
        }

        private static void DrawPanel(int winId, RectTransform anchor,
            PlanetFactory factory, int entityId)
        {
            if (factory == null || entityId == 0) return;
            int planetId = factory.planetId;
            int key = planetId * 1000000 + entityId;
            if (editingKey != key)
            {
                editingKey = key;
                inputText = MultStore.GetSet(planetId, entityId).ToString();
            }

            Vector2 dock = ComputeDock(anchor);
            Vector2 off;
            if (!userOffset.TryGetValue(winId, out off)) off = Vector2.zero;
            Rect rect = new Rect(dock.x + off.x, dock.y + off.y, PanelW, PanelH);

            Rect newRect = GUI.Window(winId, rect, id =>
            {
                GUILayout.Label("设定倍率上限：");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-10", GUILayout.Width(40))) Apply(factory, entityId, MultStore.GetSet(planetId, entityId) - 10);
                if (GUILayout.Button("-1", GUILayout.Width(35))) Apply(factory, entityId, MultStore.GetSet(planetId, entityId) - 1);
                inputText = GUILayout.TextField(inputText, GUILayout.Width(60));
                if (GUILayout.Button("+1", GUILayout.Width(35))) Apply(factory, entityId, MultStore.GetSet(planetId, entityId) + 1);
                if (GUILayout.Button("+10", GUILayout.Width(40))) Apply(factory, entityId, MultStore.GetSet(planetId, entityId) + 10);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("1")) Apply(factory, entityId, 1);
                if (GUILayout.Button("5")) Apply(factory, entityId, 5);
                if (GUILayout.Button("10")) Apply(factory, entityId, 10);
                if (GUILayout.Button("100")) Apply(factory, entityId, 100);
                if (GUILayout.Button("1000")) Apply(factory, entityId, 1000);
                if (GUILayout.Button("应用输入"))
                {
                    int v;
                    if (int.TryParse(inputText, out v)) Apply(factory, entityId, v);
                }
                GUILayout.EndHorizontal();
                bool wl = MultStore.GetWireless(planetId, entityId);
                if (GUILayout.Button(wl ? "无线：开" : "无线：关"))
                {
                    MultStore.SetWireless(planetId, entityId, !wl);
                }
                int kSet = MultStore.GetSet(planetId, entityId);
                int kEff = MultStore.GetEff(planetId, entityId);
                GUILayout.Label(string.Format("设定 x{0} / 实际 x{1}", kSet, kEff));
                DrawEnergyBar(planetId, entityId);
                // 仅标题栏可拖动，避免遮挡原版窗口标题栏导致其无法拖动
                GUI.DragWindow(new Rect(0, 0, PanelW, 20));
            }, "浓缩生产");

            userOffset[winId] = new Vector2(newRect.x - dock.x, newRect.y - dock.y);
            lastPanelRect = newRect;
            PanelShowing = true;
        }

        /// <summary>建筑内置储能能量条：灰底 + 青色（低电量转橙）填充 + 百分比文字。</summary>
        private static void DrawEnergyBar(int planetId, int entityId)
        {
            long cur, cap;
            if (!MultStore.GetBufferInfo(planetId, entityId, out cur, out cap))
            {
                // 没有扫池数据（新建筑/未开工）时主动用设定倍率估一个 cap，
                // 并写入 MultStore 让后续扫池有数据可更新
                int kSet = MultStore.GetSet(planetId, entityId);
                int secs = Plugin.EnergyBufferSecondsEntry == null ? 10 : Plugin.EnergyBufferSecondsEntry.Value;
                if (kSet <= 1 || secs <= 0) return;
                // 用一个合理的默认功耗估算（组装机典型 ~40MW/tick），让新建筑先有条可看
                cap = (long)kSet * 40000000L * secs * 60;
                if (cap <= 0) return;
                MultStore.SetBufferCap(planetId, entityId, cap);
                MultStore.ChargeBuffer(planetId, entityId, cap, cap); // 初始充满
                cur = cap;
            }
            if (cap <= 0) return;
            if (whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1);
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
            }

            float fill = (float)((double)cur / cap);
            if (fill < 0f) fill = 0f;
            if (fill > 1f) fill = 1f;

            Rect r = GUILayoutUtility.GetRect(PanelW - 24, 16);
            GUI.color = new Color(0.15f, 0.15f, 0.15f);
            GUI.DrawTexture(r, whiteTex);
            GUI.color = fill > 0.3f ? new Color(0.25f, 0.75f, 1f) : new Color(1f, 0.55f, 0.15f);
            GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * fill, r.height - 2), whiteTex);
            GUI.color = Color.white;

            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };
            GUI.Label(r, string.Format("储能 {0:0}%", fill * 100), style);
        }

        private static void Apply(PlanetFactory factory, int entityId, int k)
        {
            if (k < 1) k = 1;
            MultStore.SetMult(factory.planetId, entityId, k);
            // 堆叠建筑是同一逻辑单元：设置一台 = 整堆同步，免去逐台点
            PropagateToStack(factory, entityId, k);
            inputText = MultStore.GetSet(factory.planetId, entityId).ToString();
        }

        /// <summary>
        /// 堆叠科研站共用一个倍率：沿原版 nextLabId 链把设置同步到同一堆的全部科研站。
        /// 原版堆叠链是「下层向上下层传递」，所以既向上遍历 nextLabId，
        /// 也反向扫描池把指向本链的下层 lab 一起纳入。
        /// </summary>
        private static void PropagateToStack(PlanetFactory factory, int entityId, int k)
        {
            var fsys = factory == null ? null : factory.factorySystem;
            if (fsys == null || fsys.labPool == null) return;
            var pool = fsys.labPool;
            int cursor = Math.Min(fsys.labCursor, pool.Length);
            if (cursor <= 1) return;

            int selfId = -1;
            for (int i = 1; i < cursor; i++)
            {
                if (pool[i].entityId == entityId && pool[i].id != 0)
                {
                    selfId = pool[i].id;
                    break;
                }
            }
            if (selfId <= 0) return;

            var chain = new HashSet<int>();
            int cur = selfId, guard = 0;
            while (cur > 0 && cur < cursor && guard++ < 64)
            {
                if (!chain.Add(cur)) break;
                cur = pool[cur].nextLabId;
            }

            // 反向：任何最终指向本链的 lab 都属于同一堆
            for (int i = 1; i < cursor; i++)
            {
                if (pool[i].id == 0 || chain.Contains(pool[i].id)) continue;
                int j = pool[i].nextLabId, g = 0;
                while (j > 0 && j < cursor && g++ < 64)
                {
                    if (chain.Contains(j))
                    {
                        chain.Add(pool[i].id);
                        break;
                    }
                    j = pool[j].nextLabId;
                }
            }

            foreach (int id in chain)
            {
                if (id > 0 && id < cursor && pool[id].entityId != 0)
                    MultStore.SetMult(factory.planetId, pool[id].entityId, k);
            }
        }
    }

    /// <summary>
    /// 鼠标悬停在倍率面板上时，把 VFInput.onGUI 置真，
    /// 阻止游戏把点击穿透到后面的星球/建筑。
    /// </summary>
    public static class VFInputGuardPatch
    {
        [HarmonyLib.HarmonyPatch(typeof(VFInput), "UpdateGameStates")]
        [HarmonyLib.HarmonyPostfix]
        private static void Postfix()
        {
            if (MultiplierUI.IsMouseOverPanel())
            {
                VFInput.onGUI = true;
                VFInput.onGUIOperate = true;
            }
        }
    }
}
