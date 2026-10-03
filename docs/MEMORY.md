# MerchantBlacklist 项目记忆（公版）

> 本文件**只记录公版（开源）内容**，服务于公开仓 `Jianbao233/MerchantBlacklist`。
> 本仓是公开仓库：非公版功能、内部沟通信息、商业信息一律**不得**写入本仓任何文件。
> 建档：2026-10-03（重启开发时的状态清点）。

---

## 1. 项目定位

本机商店**遗物 / 药水 / 卡牌**黑名单过滤器。只过滤本地玩家自己的商店库存（普通商人 + 先古商人），纯视图过滤、不发网络消息、联机安全。

- mod id：`MerchantBlacklist`
- 工坊条目：`3747498557`，workspace `_workshop_workspaces/ShopBlacklist`（workspace 名是历史抢注遗留）
- 独立 Git 仓库：`https://github.com/Jianbao233/MerchantBlacklist`（公开）

## 2. 版本与发布状态（2026-10-03）

| 项 | 值 |
|---|---|
| 源码版本 | **0.3.0**（含卡牌 ban；`mod_manifest.json` / csproj 一致） |
| git | `main` 本地领先 `origin/main` **9 个 commit**（2026-10-03 阶段一 + 阶段二，**尚未 push**） |
| 工坊已上传版本 | **0.2.0**（2026-06-21），条目 visibility `private` |
| `release/` 归档 | 最新 `MerchantBlacklist_v0.2.0.zip`；**0.3.0 从未打包** |
| `torelease/` | 仍是 0.2.0 时期产物，需在下次发布前重建 |
| 部署区 | 未部署（开发机上跑的是另一条交付线，见根 `AGENTS.md`） |

## 3. 架构与拦截点

全部游戏类型经**反射**获取，编译期不引用 `sts2.dll`（csproj 只引 Godot SDK 与 `Lib.Harmony`），因此**编译通过不代表游戏 API 兼容**——必须核对反编译。

| 文件 | 作用 |
|---|---|
| `src/MerchantBlacklistMod.cs` | 入口；`BlacklistStore.LoadFromDisk` + `Harmony.PatchAll` |
| `src/Patches/ModManagerInitPatch.cs` | 启动时机兜底：`ModManager.Initialize` 后跳两帧初始化 |
| `src/Patches/MerchantInventoryCreatePatch.cs` | **核心拦截点**：`MerchantInventory.CreateForNormalMerchant` Postfix |
| `src/Patches/PauseMenuBlacklistButtonPatch.cs` | 暂停菜单注入按钮（duplicate `_settingsButton`，插到其下方） |
| `src/Patches/ShopRightClickBanPatch.cs` | 商店内右键拉黑（`NMerchantRelic` / `NMerchantPotion` / `NMerchantCard` 的 `_Ready`） |
| `src/Core/BlacklistStore.cs` | JSON 持久化 + 全局/角色两层集合（schema v4） |
| `src/Core/InventoryFilter.cs` | 过滤与重抽：遗物走 `ModelDb.AllRelics` 候选（**不碰 grab bag**，避免遗物池耗尽）、药水走 `PotionFactory`、卡牌走 `MerchantCardEntry.Populate` |
| `src/Core/CharacterDetector.cs` | 反射取当前角色 id（`RunManager` → `LocalContext.GetMe` → `Player.Character.Id.Entry`） |
| `src/UI/ModelCatalog.cs` | 反射枚举 `ModelDb.AllRelics / AllPotions / AllCards`，缓存成 UI 条目 |
| `src/UI/NativeNodeFactory.cs` | 复用原生 `NRelic.Create` / `NPotion.Create` / `NHoverTipSet.CreateAndShow` |
| `src/UI/BlacklistPanel.cs` | 面板（`CanvasLayer`，Layer=100），遗物/药水/卡牌三个页签 |

## 4. 游戏版本兼容性核实结论（2026-10-03）

> 本机 mod 生态同时服务两个分支：**正式版 `v0.107.1`** 与 **public-beta `v0.111.0`**。
> 2026-10-03 当天游戏曾停在 beta，随后切回正式版（`release_info.json` 实测）。

**方式一 · 编译期门禁（推荐，可重复）**

`main/sts2/tools/MerchantBlacklistApiGate/`（工作区本地工具，不进任何 git 仓）把 mod 反射访问的**全部**游戏成员写成 C# 表达式；能编译通过即证明这些成员在该版本存在且签名不变：

```powershell
dotnet build -c Debug -p:Sts2SdkDir="D:\A-Developing\tools\sts2_sdk_by_version\v0.107.1"
dotnet build -c Debug -p:Sts2SdkDir="D:\A-Developing\tools\sts2_sdk_by_version\v0.111.0"
```

**结果：两个分支均 0 错误 0 警告。** 覆盖 `CardPoolModel.Title/IsColorless` 与全部池子子类、
`CardModel.Title/Description/EnergyCost/Pool/Rarity/Type/HoverTips`、`CardEnergyCost.Canonical`、
`MerchantCardEntry.Populate` / `CardCreationResult.Card`、`NRelic.Create(IconSize)` / `NPotion.Create` /
`NCard.Create`（2 参）/ `NHoverTipSet.CreateAndShow+Remove`、`ModelDb.AllRelics/AllPotions/AllCards/AllCharacters`、
`MerchantInventory.CreateForNormalMerchant` + `Player`、`RunManager.Instance` / `DebugOnlyGetState` /
`IRunState.Players` / `LocalContext.GetMe` / `Character.Id.Entry`、`LocManager.Instance.Language`、
`SaveManager.Instance.MarkPotionAsSeen`、`PotionFactory.CreateRandomPotionOutOfCombat`、
`ModManifest.{id,version,affectsGameplay,minGameVersion,dependencies}`。

**方式二 · 交叉核对反编译文本**

`tools/Tools/sts.dll历史存档/sts2_decompiled_v0.111.0_20260814/`（beta）与
`docs/archive/历代版本源码/SL2_v0.107.1/src/`（正式版，GDRE 快照）。已确认两个分支的卡池类名、`Title` 取值
（`"ironclad"`/`"colorless"`/`"curse"`…）与 `IsColorless` 取值**完全一致**（0.111.0 仅多一个 `DeprivedCardPool`）。

**运行时证据**：部署侧日志显示 mod 在 v0.111.0 下加载成功、暂停菜单按钮注入成功、`ModelCatalog ready`。

## 5. 数据与持久化

- 路径：`%APPDATA%\SlayTheSpire2\mods_settings\MerchantBlacklist.json`
- 当前 `schema_version: 4`：

```jsonc
{
  "schema_version": 4,
  "active_category": "GLOBAL",
  "global": { "relics": [], "potions": [], "cards": [] },
  "perCharacter": { "IRONCLAD": { "relics": [], "potions": [], "cards": [] } }
}
```

- 写入用「先写 `.tmp` 再 Move」的原子替换；UI 改动 debounce 落盘。
- **改动 schema 前务必先确认没有其它交付线在复用同一文件**（本机存在其它构建共享此路径的风险，见根 `AGENTS.md` 双线红线）。

## 6. 构建与发布

```powershell
.\build.ps1 -Configuration Debug     # 构建并部署到本机 mods 目录
.\build.ps1 -Configuration Release   # Release 构建 + 写 torelease/ 快照
```

- `torelease/`（staging）与 `release/`（zip 归档）**不进 git**。
- 工坊上传必须从 `_workshop_workspaces/ShopBlacklist/content/` 发布，内容与 `torelease/` 完全同步；上传流程见 `main/sts2/创意工坊/01-上传流程.md`，发布前必跑 `03-发布审核.md`。
- 行尾：本仓已加 `.gitattributes`（`* text=auto eol=lf`）。**不要**依赖 `core.autocrlf`；历史上工作区被整体改写成 CRLF，曾造成 18 个文件的假「已修改」。

## 7. 已知待办

**A. 卡牌 ban 收尾** —— 1–4 已于 2026-10-03 完成（双分支编译期门禁验证），剩 5。

已完成：

1. **删死代码**：`NativeNodeFactory.TryCreateCard` / `ShowHoverTipForCard`（无调用方；且 `NCard.Create` 是 2 参，单参 `MethodInfo.Invoke` 必抛 `TargetParameterCountException`，即**从未生效过**）；`InventoryFilter` 的 `_relicFactoryType` / `_pullNextRelicFromBackFiltered` / `_cardModelType` / `_cardModelIdProp` / `BuildTypedPredicate*`；`ModelCatalog._locStringType` 与失效的 `using`。用 Roslyn 分析器（`EnforceCodeStyleInBuild` + `AnalysisMode=All`）复扫，无 IDE0051/IDE0052 残留。
2. **卡牌费用改走公开链**：`CardModel.EnergyCost`（public）→ `CardEnergyCost.Canonical`（public int），不再用 `NonPublic` 反射读 protected 的 `CanonicalEnergyCost`。
3. **无色判定改用 `CardPoolModel.IsColorless`** —— 顺带**修掉一个真 bug**：`CardPoolModel.Title` 是**小写**（`"colorless"`），旧代码用 `PoolName.Contains("Colorless")`（大小写敏感）判定，**恒为 false**，导致「无色卡」分组永远为空、所有卡都被归进「角色卡」；排序里的无色后置同样失效。两个分支同样中招。
4. **角色过滤从 `ModelCatalog` 移到面板渲染期**（`BlacklistPanel._RefreshCardContent`）：用 `IsColorless` + `PoolName` 与角色 id 忽略大小写比较。原先过滤被烘焙进 ModelCatalog 的**全量缓存**，而 `Invalidate()` 无调用方 → 切换全局/角色后卡牌列表不会变。

待办：

5. **卡牌奖励入口：经查证「不应实现」**（2026-10-03 结论，附证据）。原先 `docs/TODO_internal.md` 计划把 ban 范围扩到「战斗外卡牌奖励生成」，但那条路走不通：

   - `CardReward.OnSelect()`（0.107.1 `Core/Rewards/CardReward.cs:183-236`）的奖励选择走
     `PlayerChoiceSynchronizer` 的**索引**同步：本端 `SyncLocalChoice(player, choiceId, PlayerChoiceResult.FromIndex(num))`；
     其它端 `num = (await WaitForRemoteChoice(...)).AsIndexOrNull()` 后 `obtainedCard = _cards[num.Value].Card`
     —— **索引是在各端自己的本地 `_cards` 列表里解析的**。本地过滤会让各端列表错位，结果是错牌，或日志报
     `Received bad player choice index N for a card reward with M cards`。备选方案用 `_cards.Count + alternativeIndex`
     编码，同样依赖列表长度一致。
   - 结论：卡牌奖励**不能**做本地过滤（除非放弃联机，与 mod 定位冲突）。这也与 `docs/DESIGN.md` 既有的
     「不动战斗后奖励 / 精英 / Boss / 宝箱 / 事件」边界一致 —— **建议维持现状**。

   **对照：商店路径的联机安全已复核（补上 v0.3.0 卡牌 ban 缺失的验证）** ——
   `MerchantCardEntry.OnTryPurchase` 走 `RewardSynchronizer.SyncLocalObtainedCard(CreationResult.Card)`，
   而 `RewardObtainedMessage` 只携带完整 `cardModel` / `potionModel` / `relicModel` / `goldAmount`、
   **没有任何索引字段**（0.107.1 与 0.111.0 逐字段一致），且全库不存在 `MerchantPurchase` 类消息。
   所以商店里的卡牌 ban 与遗物/药水一样：**客户端本地过滤联机安全**。

**B. 元数据与文档对齐**
1. ✅ 已补 `min_game_version: "0.107.1"`（最低支持版本，与 LoadOrderManager / RitsuLib 同约定），消除每次启动的 `does not declare min game version` WARN。依据：双分支编译期 API 门禁在 v0.107.1 与 v0.111.0 均通过。描述里的「适配 STS2 0.107」当前是准确的（正式版即 0.107.1），等 v0.111.0 也跑过再考虑改成「0.107.1+ / 0.111.x」口径。
2. `docs/DESIGN.md` 与实现脱节：仍写「不做卡牌黑名单」、`affects_gameplay: true`。**实现是对的**——`affects_gameplay` 必须保持 `false`（v0.111.0 用它决定是否参与联机内容哈希与 `ContentSorter` 排序；本 mod 不注册 model、不发网络消息）。
3. 0.3.0 从未打包；下次发布要重建 `torelease/` 并归档 `release/MerchantBlacklist_v0.3.0.zip`。

**C. 面板 UI 重构（前置调研已完成，等方向确认）**

现状诊断（实测）：`BlacklistPanel.cs` 1276 行，含 **61 处硬编码 `new Color(...)`**、5 个自绘 `StyleBoxFlat`、22 处字号覆写、28 处颜色覆写，并且是一整套**自制拖拽 / 八向缩放浮动窗**（`_DetectEdges`/`_ApplyResize`/`ResizeEdge`…）——**游戏原生 UI 里没有这种窗口**；三个页签里遗物/药水是图标网格、卡牌是**文字列表**。原始设计（`docs/DESIGN.md` §6.1）锁定的是「不写自有 Theme、不画自有底板、全部 clone 原生节点」，实现已偏离 —— 这就是「不像游戏」的根因，不是功能缺失。

游戏原生可复用零件（**已核实 0.107.1 与 0.111.0 两分支都存在**）：

| 用途 | 原生类型 | 入口 / 关键点 |
|---|---|---|
| 遗物收藏册整屏 | `NRelicCollection : NSubmenu` | `Create()`；`res://scenes/screens/relic_collection/relic_collection.tscn` |
| 遗物格子 | `NRelicCollectionEntry : NButton` | `Create(relic, visibility)`；**public 字段 `relic`** → 可直接 hook `Pressed` |
| 药水实验室 | `NPotionLab` / `NLabPotionHolder : Control` | `.../potion_lab/potion_lab.tscn`；holder 的模型是 private，需反射 |
| 卡牌库整屏 | `NCardLibrary : NSubmenu` | `Create()`；`.../card_library/card_library.tscn` |
| 卡牌网格 | `NCardGrid` / `NCardLibraryGrid` | **原生 `HolderPressed` / `HolderAltPressed` 委托**（天然区分左右键） |
| 子菜单基类 | `NSubmenu : Control, IScreenContext` | `SetStack()` 可选（不进原生栈就自己 `AddChild`）；`HideBackButtonImmediately()` |
| 场景路径构造 | `SceneHelper.GetScenePath(inner)` | → `res://scenes/<inner>.tscn`；也可直接 `ResourceLoader.Load<PackedScene>` |

三个候选方向（按根 `AGENTS.md` 约定**须先与用户确认**再动手）：**A** 直接实例化原生图鉴做「ban 叠加层」（视觉 100% 原生，但要把只读屏改造成编辑器，依赖内景结构、跨版本最脆）；**B** 保留现有布局只换视觉层（用主题读取替代硬编码配色，卡牌页签改图标网格，风险最低）；**C** 去掉浮动窗、改原生模态形态。

方案 A 的可行性已核实（结论是可行，比预想乐观）：

- 三个图鉴屏都在**预载集**里 —— `Core/Assets/AssetSets.cs:175-177` 把 `NRelicCollection.AssetPaths` / `NPotionLab.AssetPaths` / `NCardLibrary.AssetPaths` 收进 `MainMenuSet`；且 `AssetCache.GetAsset` 未命中时会**回退到 `ResourceLoader.Load`** → `Create()` 不依赖"玩家是否进过图鉴"。
- 场景**自包含**：`NRelicCollection._Ready()` 用唯一名取 `%ScreenContents` 与 7 个稀有度分类节点，无需外部宿主；`NSubmenu.SetStack()` 可选，`HideBackButtonImmediately()` 可隐藏返回键。
- 内容填充走**公开虚方法** `OnSubmenuOpened()`（可直接调用触发加载，无需反射）；格子可点（`NRelicCollectionEntry : NButton` + public `relic`；`NCardGrid` 的 `HolderPressed`/`HolderAltPressed`）。
- **唯一硬摩擦**：`NRelicCollection.LoadRelics()` 是 private 且按玩家收集进度决定可见性（读 `SaveManager.Instance.Progress.DiscoveredRelics`），图鉴会把没见过的遗物显示成 locked —— 黑名单编辑器需要"全部可见"，得反射传「全见过 + 全解锁」状态或自建分类节点。
