# CondensedProduction — 浓缩生产

《戴森球计划 / Dyson Sphere Program》BepInEx 5 插件。让生产建筑以「倍数」运行（等效 N 台合一），并附带容器扩容、传送带堆叠扩容与无线供料。

面向玩家的完整功能说明、安装与配置表见 [dist/README.md](dist/README.md)（该文件同时作为 Thunderstore 商店页）。

## 功能一览

| 功能 | 说明 |
|------|------|
| 浓缩生产 | 生产建筑可设 1~1000 倍率；周期不变，原料/产物/能耗按实际倍率缩放，按当前缓存自适应 |
| 容器扩容 | 物流站、储物箱、储液罐、玩家背包每格上限 ×100 |
| 传送带堆叠 | 堆叠器 / 白爪 / 物流站出口上限 255 层（`Cargo.stack` 为 byte），渲染保持原版 |
| 无线供料 | 单个建筑开关：由本行星物流站直接供料与回收产物，跨行星生效，可随建筑复制 |

## 目录结构

```
src/        插件源码（C#）
tools/      构建用工具（图标生成）
dist/       manifest.json / README.md / icon.png / 打好的 zip
build.ps1   一键编译 + 打包 + 本地部署
```

源码文件：

- `Plugin.cs` — 插件入口、配置项、Harmony 补丁注册、存档读写
- `ProductionPatches.cs` — 倍率核心（配方计数转译、动态批量、能耗、建筑设置复制）
- `GlobalSupply.cs` — 无线供料（补料 / 回收）
- `StoragePatches.cs` — 容器每格上限
- `BeltStack.cs` — 传送带堆叠扩容
- `MultiplierUI.cs` — 建筑窗口旁的倍率面板
- `MultStore.cs` — 倍率与无线开关的数据层

## 构建

依赖本机环境（路径写在 `build.ps1` 顶部，按自己的环境修改）：

1. 游戏安装目录（`DSPGAME_Data\Managed` 下的 `Assembly-CSharp.dll`）
2. BepInEx 5 核心库（`BepInEx.dll`、`0Harmony.dll`）
3. `DSPModSave.dll`（来自 CommonAPI-DSPModSave）
4. Roslyn `csc.exe` 与 netstandard 2.1 引用程序集

```powershell
.\build.ps1              # 编译 + 生成图标 + 打包 dist\CondensedProduction.zip + 部署到本地测试环境
.\build.ps1 -SkipDeploy  # 只编译打包，不部署
```

产物：

- `dist\CondensedProduction.zip` — 可直接上传 Thunderstore（根目录为 `manifest.json` / `README.md` / `icon.png` / `CondensedProduction.dll`）

## 发布到 Thunderstore

1. 修改 `dist/manifest.json` 里的 `website_url`（填本仓库地址）
2. 确认 `version_number` 与 `Plugin.cs` 的 `VERSION` 一致
3. 用 `build.ps1` 重新打包，把 `dist\CondensedProduction.zip` 上传到 Thunderstore

## 许可证

未指定。发布前请自行补充 LICENSE。
