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
| git | `main` 本地已含 2026-10-03 的 3 个 commit（**尚未 push**） |
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

## 4. v0.111.0 兼容性核实结论（2026-10-03）

逐项核对 `tools/Tools/sts.dll历史存档/sts2_decompiled_v0.111.0_20260814/`，**mod 用到的每个依赖点都存在**：

`MerchantInventory.CreateForNormalMerchant` · `ModelDb.AllRelics/AllPotions/AllCards` · `RelicModel.{Id,Icon,Title,Rarity,IsAllowedInShops,HoverTips}` · `PotionModel.{Id,Image,Title,Rarity,HoverTips}` · `CardModel.{Id,Title(string),Description(LocString),CanonicalEnergyCost(protected),Rarity,Type,Pool,HoverTips}` · `CardPoolModel.{Title,IsColorless}` 及其 12 个池子子类 · `CardCreationResult.Card` · `MerchantCardEntry.{CreationResult,Populate}` · `MerchantEntry.Cost` · `NRelic.Create` / `NPotion.Create` / `NCard.Create` · `NHoverTipSet.CreateAndShow/Remove` · `LocManager.Instance/Language` · 角色 id 仍为 5 个大写：`IRONCLAD / SILENT / DEFECT / NECROBINDER / REGENT`。

运行时证据：部署侧日志显示 mod 在 v0.111.0 下加载成功、暂停菜单按钮注入成功、`ModelCatalog ready`。

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

**A. 卡牌 ban 收尾（v0.3.0 已实现主体，以下为遗留）**
1. `NativeNodeFactory.TryCreateCard` / `ShowHoverTipForCard` 已成**死代码**；且 `NCard.Create(CardModel, ModelVisibility = Visible)` 是 2 个参数，`MethodInfo.Invoke` 只传 1 参会抛 `TargetParameterCountException`（即从未生效过）。删除或补齐参数。
2. 卡牌悬浮预览反射取 `CardModel.CanonicalEnergyCost`（**protected**）；官方注释建议改用公开的 `EnergyCost.Canonical`。
3. 卡牌分组判定用 `pool.GetType().Name.Contains("Colorless"/"Curse"/…)` 字符串匹配——实测在 v0.111.0 可用，但应改用 `CardPoolModel.IsColorless` 与 `Title`。注意 `DeprecatedCardPool.Title` 与 `TokenCardPool.Title` **都是 `"token"`**。
4. `ModelCatalog.BuildCardEntries` 的角色过滤写法绕（中文显示名 + 大写 charId 双重 `Contains` 兜底），应改为按 pool `Title`（lowercase，如 `"ironclad"`）判定。
5. 卡牌 ban 目前只拦**商店卡槽**；`docs/TODO_internal.md` 规划的「战斗外卡牌奖励生成」入口尚未做。

**B. 元数据与文档对齐**
1. `mod_manifest.json` 与 `README.md` 仍写「适配 STS2 0.107」；需按实测更新，并补 `min_game_version`（**必须填最低支持版本**，填高会导致低版本分支整包被游戏拒载）。
2. `docs/DESIGN.md` 与实现脱节：仍写「不做卡牌黑名单」、`affects_gameplay: true`。**实现是对的**——`affects_gameplay` 必须保持 `false`（v0.111.0 用它决定是否参与联机内容哈希与 `ContentSorter` 排序；本 mod 不注册 model、不发网络消息）。
3. 0.3.0 从未打包；下次发布要重建 `torelease/` 并归档 `release/MerchantBlacklist_v0.3.0.zip`。

**C. 面板 UI 重构（进行中）**
用户对当前 `BlacklistPanel` 不满意，计划整体重构。注意根 `AGENTS.md` 约定：UI 任务先确认是否继续优先复用游戏原生组件（`tools/SL2_v0.111.0/scenes/`）。
