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
| `src/SelfTest.cs` | **环境变量守卫的启动自检**（默认完全惰性）：设 `MERCHANTBLACKLIST_SELFTEST=1` 时，启动约 2 秒后（120 帧）自动建立一次 `ModelCatalog` 并把「面板应该显示什么」打进日志。**只读**，不写设置、不改商店。用途：真机验证不必再依赖"有人按 F10"。 |

> **为什么需要自检**：`ModelCatalog` 只在打开过一次面板时才建立，因此真机验证原本必须有人进游戏操作；
> 自检把这条路径变成「设环境变量 → 启动 → 读日志」。它输出的 `ModelCatalog ready. relics=…, potions=…, cards=…`
> 与面板那行同形，`main/sts2/tools/MerchantBlacklistVerify/verify-in-game.ps1` 用同一个正则解析两种来源。

## 4. 游戏版本兼容性核实结论（2026-10-03）

> 本机 mod 生态同时服务两个分支：**正式版 `v0.107.1`** 与 **public-beta `v0.111.0`**。
> 2026-10-03 当天游戏曾停在 beta，随后切回正式版（`release_info.json` 实测）。

**方式一 · 编译期门禁（推荐，可重复）**

`main/sts2/tools/MerchantBlacklistApiGate/`（工作区本地工具，不进任何 git 仓）把 mod 反射访问的**全部**游戏成员写成 C# 表达式；能编译通过即证明这些成员在该版本存在且签名不变：

```powershell
dotnet build -c Debug -p:Sts2SdkDir="D:\A-Developing\tools\sts2_sdk_by_version\v0.107.1"
dotnet build -c Debug -p:Sts2SdkDir="D:\A-Developing\tools\sts2_sdk_by_version\v0.111.0+build24724944"
```

> **同版本号重发（2026-10-04 实证）**：Steam 会在**不改 `release_info.json` 版本号**的情况下替换游戏二进制 ——
> 当天 01:32 的 v0.111.0 重发把 `sts2.dll` 从 10,655,744 字节换成 9,757,184 字节（buildid 24724944）。
> 所以「当前安装版本是否兼容」**不能只看版本号**：比对 `data_sts2_windows_x86_64\sts2.dll` 的 SHA256，
> 或把门禁的 `Sts2SdkDir` 直接指向安装目录跑一遍。**本 mod 已对新构建实跑：0 错误 0 警告。**

**结果：两个分支均 0 错误 0 警告。** 覆盖 `CardPoolModel.Title/IsColorless` 与全部池子子类、
`CardModel.Title/Description/EnergyCost/Pool/Rarity/Type/HoverTips`、`CardEnergyCost.Canonical`、
`MerchantCardEntry.Populate` / `CardCreationResult.Card`、`NRelic.Create(IconSize)` / `NPotion.Create` /
`NCard.Create`（2 参）/ `NHoverTipSet.CreateAndShow+Remove`、`ModelDb.AllRelics/AllPotions/AllCards/AllCharacters`、
`MerchantInventory.CreateForNormalMerchant` + `Player`、`RunManager.Instance` / `DebugOnlyGetState` /
`IRunState.Players` / `LocalContext.GetMe` / `Character.Id.Entry`、`LocManager.Instance.Language`、
`SaveManager.Instance.MarkPotionAsSeen`、`PotionFactory.CreateRandomPotionOutOfCombat`、
`ModManifest.{id,version,affectsGameplay,minGameVersion,dependencies}`。

**方式二 · 离线算「面板应该显示多少项」**

`main/sts2/tools/MerchantBlacklistVerify/PoolExtractor/`：不启动游戏，直接从 `sts2.dll` 的 IL 读出
各卡池 `GenerateAllCards()` 的内容与各模型的稀有度，按 `CardModel.Pool` 的解析规则（`AllCardPools` 里
**第一个**包含该卡的池，顺序敏感）算出面板期望值：

| 构建 | 遗物 | 药水 | 卡牌页签 | 角色卡 | 无色卡 |
|---|---|---|---|---|---|
| `v0.111.0+build24724944`（当前安装） | 145 | 60 | 519 | 454 | **65** |

**方法可信度**：在重发前的旧构建上算出 `relics=145 / potions=60`，与 mod 真实运行日志实测值**逐个吻合**；
卡牌类总数 596 也与 GDRE 快照 `Cards/` 目录的 596 个 `.cs` 一致。
**无色卡 65 是关键判据** —— 修复前 `PoolName.Contains("Colorless")` 大小写敏感恒为 false，
「无色卡」分组永远是空的，那 65 张会被错误归进「角色卡」。

> 为什么必须离线算：运行时探针（独立进程里加载游戏程序集直接问 `ModelDb`）**走不通** ——
> `ModelDb` 的内容注册在游戏启动流程里（游戏外是空的，抛 `KeyNotFoundException`），
> 而 `LocManager.Initialize()` / `SaveManager` 会走到 Godot 原生调用，**0xC0000005 硬崩且 try/catch 拦不住**。
> 游戏更新后重跑 PoolExtractor 即可刷新期望值。

**方式三 · 交叉核对反编译文本**

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
2. ✅ `docs/DESIGN.md` 已与实现对齐：文首加了「与本稿写作时的差异」小节（卡牌 ban 已实现、`affects_gameplay` 为 `false`、两层黑名单、UI 待重做），修正了 `affects_gameplay: true` 与「不做卡牌黑名单」两处，并补上两条已核实的联机事实（`RewardObtainedMessage` 只带完整 model 无索引；卡牌奖励是索引型同步故明确不做）。**实现是对的** —— `affects_gameplay` 必须保持 `false`（v0.111.0 用它决定是否参与联机内容哈希与 `ContentSorter` 排序）。
3. ✅ `README.md` 已与实现对齐（2026-10-04）：
   - 补上 v0.3.0 的**卡牌 ban** 与卡牌页签 —— 此前 README 完全没提这个已上线的功能；
   - **修正依赖口径**：RitsuLib / ModConfig 都只是**可选**（`BlacklistInputNode` 有保底轮询，两样都不装时 `F10` 照常工作），原文把 RitsuLib 写成了必需安装步骤；
   - 安装说明不再钉死过期的 `MerchantBlacklist_v0.1.0.zip`；改键路径补上 RitsuLib 设置页；
   - 版本口径改为 `0.107.1 and newer`（与 `min_game_version: 0.107.1` 一致），并注明已在 0.107.1 / 0.111.0 上核实 API 兼容；
   - 语言支持改为准确表述：面板支持中文（任意 `zh*`，文案为简体）/ English，繁体只存在于设置项（`_IsChinese()` 对任意 `zh*` 都返回 true，面板只有一套简体文案 —— 原「支持繁体」是过度声明）。
4. ✅ 停止跟踪 `mod-uploader.log`（工坊上传器写的临时日志，不属于源码）：加进 `.gitignore` 并 `git rm --cached`。
5. ⏳ 0.3.0 从未打包；下次发布要重建 `torelease/` 并归档 `release/MerchantBlacklist_v0.3.0.zip`。**发布前记得**：`mod_manifest.json` 的 description 与 `_workshop_workspaces/ShopBlacklist/workshop.json` 的文案要同步（description 里「适配 STS2 0.107」可与 `min_game_version: 0.107.1` 对齐成「0.107.1+」）。

**C. 面板 UI 重构 —— 已完成（2026-10-04，提交 `1338aed`）**

用户授权「按你的原型设计开发，自己无头测试，最后交付验收」。`BlacklistPanel.cs` 已按浏览器原型重写
（80,905 字节），**保留了用户确认的三个形态**：可拖拽 + 八向缩放浮动窗、卡牌用文字列表、悬停卡牌预览。
渲染架构换成原型那套：

| 旧实现 | 新实现 |
|---|---|
| 切页签 / 每敲一个搜索字符 / 每点一次 ban 都清空 `_content` 重建整表 | 节点**只建一次**，之后只改 `Visible` / stylebox / modulate |
| 首次打开一次性建完几百个节点 | **分帧构建**（Timer，每帧 40 个节点） |
| 卡牌列表全量建行 | **虚拟滚动**（实测 519 行只实例化 7 个行节点） |
| 搜索按键即重绘 | **防抖 120ms**（`Timer` 节点） |
| —— | `id → 节点 / id → 数据` 两张字典；本层/全局继承两种 ban 视觉区分 + 图例；批量与清空**可撤销**；清空二次确认；搜索命中在卡牌行高亮 |

**无头 + 禁音实测证据**（`--headless --audio-driver Dummy --quit-after 0`，退出码 0，15.8 秒）：

```
[SelfTest] ModelCatalog ready. relics=145, potions=60, cards=519.
[SelfTest] cards split. character=454, colorless=65.
[SelfTest] panel/populated: relics=145(cells=145,visible=145) potions=60(cells=60,visible=60)
           cards=519 cardRows=519(pooled=7) colorless=65 nativeIcons=205 fallbackIcons=0
[SelfTest] panel/search: relics=145(cells=145,visible=2) potions=60(cells=60,visible=0)
[SelfTest] async pass OK: 分帧构建已插入全部 205 个格子
```

- 145/60/519/**65** 与 `PoolExtractor` 从 IL 独立算出的期望值**逐个吻合**（该期望值已用旧构建真实日志 145/60 对撞验证）→ 阶段二那个「无色卡分组恒为空」的修复在真机上确认生效
- `nativeIcons=205 fallbackIcons=0`：205 个遗物/药水格子全部拿到**原生** `NRelic`/`NPotion` 节点（图标与 tooltip 会真的渲染，没有退化成占位）
- `pooled=7`：虚拟滚动生效
- 同步与**分帧**两条构建路径都跑通（分帧是用户实际走的那条）
- 无自检标记时启动**完全惰性**：普通启动日志里没有任何 `[SelfTest]` 行

> **未验证项（需人眼）**：headless 无渲染，所以**视觉外观没有被确认过** —— 配色、间距、悬停预览位置、缩放后的布局自适应都要在真实窗口里看。

<details>
<summary>阶段三的前置调研（历史记录，已完成其使命）</summary>

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

</details>

---

## 8. 视觉迭代：截图驱动的自我审查（2026-10-04）

用户授权「用前端可见的形式开发，但别抢占到最前端，自己后台截图改」。做法：

```powershell
# 等游戏退出 → 部署 → 可见窗口启动（压到 z 序底部且不激活、Dummy 音频）→ 逐姿势截图 → 自动退出
main\sts2\tools\MerchantBlacklistVerify\wait-and-shoot.ps1
main\sts2\tools\MerchantBlacklistVerify\run-visual-shots.ps1
```

- 截图用 **Godot 视口截图**（`GetViewport().GetTexture().GetImage().SavePng()`），窗口被遮挡也能截，不依赖桌面截图工具
- 姿势（`MERCHANTBLACKLIST_SELFTEST=shots`）：0=面板隐藏的干净参照 → 遗物 / 卡牌 / 卡牌+预览 / 搜索 / 已 ban / 角色层继承 / 清空确认 / 最小尺寸
- **先等主菜单就绪再截**（开场 logo 期间只能拍到 MegaCrit 标志，实测踩过）
- 每帧同时输出 `layout/poseN:` 几何自检（窗口尺寸、是否在树、页脚是否在窗内、面板/滚动区尺寸、格子尺寸、列数、虚拟行数）

**只有看图才能发现的问题**（几何自检全绿但画面是坏的）：

| 问题 | 根因 |
|---|---|
| 面板一打开就是最小尺寸 620×460 | `_Ready` 时视口还没尺寸，按视口夹紧把窗口压到下限。改为**首次显示时**再定尺寸/居中 |
| 卡牌列表整片空白、首行文字糊成一团 | 虚拟滚动的行是 `_cardSizer` 的普通子节点，**必须自己设 `Position`**，我漏了 → 所有行堆在 (0,0) |
| 筛选块只看得见色点、文字不渲染 | 往 `Button` 里塞 `HBox+Label` —— Button 不是容器，子控件尺寸为 0。改用 `Button.Text` + 字色 |
| 窄窗口下内容比窗口还宽（620 窗口里 pane=649） | 工具条 `HBoxContainer` 的最小宽度是子项之和。改 `HFlowContainer` 让筛选块自动换行 |
| 预览框显示 `{Summon:diff()}` 内部标记 | 游戏格式化器缺运行期上下文时的占位符。游戏标记 → Godot BBCode 颜色 + 占位符换 `?` |
| 暂停菜单每次打开都喷一条 ERROR + C# 回溯 | `BindReleased` 里多余的"先 Disconnect 再 Connect"（连接本来就没被复制） |

**两个我差点被自己骗过去的坑**：

1. **用"像素变了没有"判断面板透不透明是错的** —— 半透明同样会让每个像素都变。正确做法是看**同一条内容均匀的横带上，背后的东西变了面板值变不变**（实测 4 个采样点背后亮度 57→255，面板值恒为 39，差 0 → 确实不透明）。我一度按"看图"认定有鬼影，还"量出"9% 透射率，其实是拿两个**内容不同**的点在比较。
2. **看缩略图会看错** —— 我"看见"主菜单文字穿透面板，像素数据证明没有。结论以像素/几何为准，眼睛只用来判断"好不好看"。

**顺带确认的两件事**：

- 面板底色仍从 0.97 改成 **1.0**（设置面板不该透出游戏画面）
- `CanvasLayer.Layer` 从 100 抬到 **1000**：确保压在最上层。原生悬停 tooltip 不受影响 —— `NativeNodeFactory.ReparentTipToOwnerLayer` 会把 tip 重挂到 owner 所在的 CanvasLayer 同层渲染

---

## 8.1 设计迭代（2026-10-04，第二轮：好看 / 能用 / 交互合理）

用户授权「全权接管、自己开发、迭代到好看能用美观且交互合理」后做的一批设计改进：

| 改进 | 为什么 |
|---|---|
| 筛选块**开/关两态各一套 stylebox** | 原来开关都长一样，玩家看不出筛选到底生效没有 |
| 页签**激活态**（金色底 + 2px 下边框 + 亮字） | 原来只有 Godot 默认的微弱按下态，分不清当前在哪个页签 |
| 卡牌行**斑马纹** | 500+ 行纯色列表很难扫读 |
| 卡牌行**列宽重排**（名称固定 300px、ID 用 Spacer 推右） | 原来名称 `ExpandFill` 把中段空出约 600px，列表看着很散 |
| **滚动条跟着配色**（金色 grabber） | Godot 默认滚动条与黑金配色不搭 |
| 搜索框 **`ClearButtonEnabled`** | 一键清空搜索，原生能力不用自己画按钮 |
| **Esc 关面板**（并吃掉事件） | 不吃掉的话游戏会同时打开暂停菜单 |
| **记住每个页签的滚动位置** | 翻到一半切页签再回来跳回顶部很难受 |
| 浮动窗**投影**（`StyleBoxFlat.ShadowSize`） | 让它从游戏画面上"浮"起来 |
| **「只看已 ban」筛选** | 黑名单是长期累积的，回看时不该在 519 行里翻；该筛选同时驱动批量按钮的计数，语义一致 |

> 又一次的教训：我两次从缩略图里"看出"问题（列表左侧有 250px 缩进、分组标题前有个金三角），
> **像素扫描证明两者都不存在**（内容起点 x≈470 紧跟边框；x=460/461 是面板边框本身，亮像素从 x=477 才是文字笔画）。
> 缩略图只能用来判断"好不好看"，**定位问题一律用像素/几何数据**。

---

## 9. 无头自检怎么跑（真机验证不再需要人按 F10）

面板的 `ModelCatalog` 只在"打开过一次面板"时才建立，所以真机验证原本必须有人进游戏操作。
现在有了内建自检（`src/SelfTest.cs`），一条命令即可：

```powershell
# 触发方式二选一（**优先用文件**：实测从外部启动游戏时环境变量不一定能可靠传到游戏进程）
Set-Content "$env:TEMP\MerchantBlacklist.selftest" -Value "full" -NoNewline -Encoding ASCII
# 或：$env:MERCHANTBLACKLIST_SELFTEST = "full"

& "F:\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" `
    --headless --quit-after 0 --audio-driver Dummy `
    --log-file "D:\A-Developing\tmp\mb-selftest\run.log"
```

- `catalog`（或文件内容留空）：只建立目录并打印期望值，游戏继续运行
- `full`：额外同步构建面板 + 走一遍三个页签 + 搜索 + ban 切换，再用**分帧路径**建第二个面板，
  最后自己退出游戏（退出码 0，约 16 秒）
- **跑完记得删掉标记文件**，否则每次启动都会自检

三条踩过的坑（都写进代码注释了）：

1. **驱动必须用 `SceneTreeTimer` 链**，不能挂节点 `_Process` 或 `SceneTree.ProcessFrame` —— 后两者在游戏启动过程中会被暂停，自检静默卡死（LoadOrderManager 会话实测，其 README 有记录）
2. **环境变量不可靠**：第一次无头测试时 `$env:...="full"` 设了、游戏进程里却是空的（日志里连一行自检都没有），所以改成以 `%TEMP%` 标记文件为主
3. **收尾别用 `finally`**：full 模式还要等分帧构建跑完，无条件 `Finish()` 会把游戏过早退掉（第一次跑分帧路径就踩了，报告没打出来就退出了）
