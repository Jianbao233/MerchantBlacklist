# MerchantBlacklist 设计文档 v0.1（实现稿）

需求已确认，本稿记录 v0.1 的决策依据。

UI 形态已锁定为**候选 C：独立设置面板**。本稿在第 5、6 节固化决策，第 10 节给出 UI 详细设计与待最终确认的细节。

> 设计取向：黑名单条目数量多，需要可视化批量管理（看图优先于手写配置文件）。

---

## 0. 与本稿写作时的差异（后续更新，读下文时请先看这里）

本稿写于 v0.1 阶段。之后实现演进，以下三处与下文原文不一致，**以这里为准**：

1. **卡牌 ban 已实现**（v0.3.0）：商店卡槽的卡牌会被过滤、面板新增「卡牌」页签、商店内可右键拉黑卡牌。第 1 节非目标里「不做卡牌黑名单」一条已作废。
2. **`affects_gameplay` 最终为 `false`**（不是下文写的 `true`）。理由见第 2 节末尾的更正说明。
3. **黑名单从「全局一层」扩为「全局 + 各角色两层」**（v0.2.0），面板顶部有全局 / 角色切换。
4. 面板**视觉实现**正在重做（当前实现偏离了第 6.1 节「不写自有 Theme、不画自有底板」的原则）。

---

## 1. 目标与非目标

### 目标

- 在商店生成的瞬间，把玩家拉黑的遗物 / 药水 / 卡牌从**他自己**的商店库存里替换或剔除。
- 仅作用于玩家本机的本地商店库存生成路径，不发任何网络消息。
- 联机零侵入：单装可用，主机/客机身份无关，对端是否安装无关。
- 黑名单可持久化、跨局生效、跨角色全局共享（v0.2.0 起另有各角色独立层）。

### 非目标（明确不做）

- 不动战斗后奖励、精英奖励、Boss 奖励、宝箱遗物。
- 不动事件奖励、Neow / Pael / Vakuu / TinkerTime 等任何事件 EventOption。
- 不动战斗后掉落药水。
- ~~不做卡牌黑名单~~ → **已作废**：v0.3.0 实现了卡牌 ban（仅商店卡槽）。
- **不做卡牌奖励过滤**（明确结论，非"暂缓"）：卡牌奖励的选择走**索引型同步**
  （`CardReward.OnSelect` → `PlayerChoiceSynchronizer.SyncLocalChoice(..., PlayerChoiceResult.FromIndex(num))`，
  其它端用 `_cards[num]` 在**各自本地列表**里解析），本地过滤会让各端列表错位、导致错牌或
  `Received bad player choice index` 报错。除非放弃联机，否则不能做。
- 不做开局起手遗物覆盖。
- 不做白名单模式。

> 需求边界确认：只覆盖商店即可；精英 / 宝箱等其它来源即使命中也不处理 → 本 mod 严格只覆盖商店。

---

## 2. 联机安全边界

```
商店遗物 / 商店药水 / 商店卡牌
   生成路径：MerchantInventory.CreateForNormalMerchant(player)
   作用域：  per-player 本地构造，不广播
   结论：    本地过滤即可，单装稳定，对端零感知

购买/获得的同步（已核实，两个游戏分支逐字段一致）
   RewardObtainedMessage 只携带完整 cardModel / potionModel / relicModel / goldAmount
   —— 没有任何索引字段；全库也不存在 MerchantPurchase 之类的消息。
   商店卡牌走的也是 SyncLocalObtainedCard(CreationResult.Card)，即"本地解析出的那张卡"。
   结论：    客户端本地过滤联机安全（含卡牌）。

其它来源（精英 / Boss / 宝箱 / 事件 / Neow / 战斗药水 / 卡牌奖励）
   生成路径：主机权威 + RewardSynchronizer 广播（卡牌奖励还叠加索引型选择同步）
   结论：    严格不动，超出本 mod 范围
```

mod manifest（**实际值**，以仓库根 `mod_manifest.json` 为准）：

```
id                MerchantBlacklist
author            @Bilibili我叫煎包
version           0.3.0
affects_gameplay  false
min_game_version  0.107.1
dependency        无（RitsuLib / ModConfig 均为可选增强，不装也能用）
```

> **更正**：本稿原先主张 `affects_gameplay: true`「诚实标记」，实现时改判为 **`false`**，这是对的。
> v0.111.0 的 `ModelIdSerializationCache` 会在计算**联机内容哈希**时跳过 `affects_gameplay = false` 的 mod
> （`ContentSorter` 也会把它们排到末尾）。本 mod 不注册任何 Model、不发任何网络消息，
> 标 `true` 反而会让装了/没装本 mod 的玩家之间产生内容不一致。**不要照本稿原文改成 true。**

---

## 3. 数据流与拦截点

```
进入商店房间
  └─ MerchantInventory.CreateForNormalMerchant(player)
        └─ [Postfix] MerchantBlacklist 过滤层
              ├─ 遍历 RelicEntries
              │     entry.Model.Id.Entry ∈ blacklist?
              │       命中 → 用 RelicGrabBag(rarity) 重抽，跳过黑名单 id
              │              重抽超过 N 次仍命中 → 接受原品（keep_original）
              ├─ 遍历 PotionEntries（同上，对应 PotionGrabBag）
              └─ 不动 CardEntries / CardRemoval / 其它玩家
  ↓
NMerchantInventory.Initialize 渲染 → 玩家看到的是过滤后的库存
```

唯一拦截点 = **`MerchantInventory.CreateForNormalMerchant` 的 Postfix Patch**。
与 RefreshShop 的「重建本地商店」路径完全兼容：玩家点 RefreshShop 刷新 → 重建走同一个 `CreateForNormalMerchant` → 同一个 Postfix 再过滤一次。

---

## 4. 黑名单数据形态

### 4.1 持久化文件

```
%APPDATA%\SlayTheSpire2\mods_settings\MerchantBlacklist.json
```

```json
{
  "schema_version": 1,
  "blacklisted_relics":  ["circlet", "anchor", "..."],
  "blacklisted_potions": ["block_potion", "..."],
  "settings": {
    "max_reroll_attempts": 12,
    "fallback_when_pool_drained": "keep_original",
    "ui_default_filter_acquired_only": false,
    "enable_quick_ban_in_shop": false
  }
}
```

### 4.2 运行时形态

```
启动加载 → HashSet<string> _relicBlacklist
         → HashSet<string> _potionBlacklist
命中查询 O(1)
UI 写操作 → 改 HashSet → 立刻保存 JSON（debounced 200ms）
```

ID 来源：游戏内 `RelicModel.Id.Entry` / `PotionModel.Id.Entry`，全游戏统一 key（小写下划线）。

---

## 5. UI 形态（已锁定：候选 C 独立设置面板）

已选定 C。理由：黑名单条目多，需要可视化批量管理。

详细 UI 设计见第 10 节。其它候选作为参考保留：

- 候选 A 纯 JSON：被否决，太多条目时手写不现实。
- 候选 B 商店内右键 ban：作为 C 的**可选补充**保留，默认关闭（详见第 10.6 节"游戏内快捷 ban"）。
- 候选 D = A+B：被否决。

---

## 6. 已固化的决策

| 决策项 | 取值 | 备注 |
|---|---|---|
| UI 形态 | 候选 C：独立设置面板 | 已确认 |
| 黑名单粒度 | **全局**，跨角色、跨 run、跨存档 | 已确认 |
| 池子打空 fallback | `keep_original` | 重抽超 12 次仍命中 → 接受原品，不让商店开天窗 |
| 一键 ban 整档稀有度 | **做** | UI 顶部一键按钮（详见 10.4） |
| 白名单模式 | 不做 |  |
| 商人覆盖范围 | 一视同仁，所有走 `MerchantInventory` 的商人都过滤 |  |
| 配置文件位置 | `%APPDATA%\SlayTheSpire2\mods_settings\MerchantBlacklist.json` |  |
| Hover 打 id 调试 | 默认关，UI 设置页可开 |  |
| 游戏内快捷 ban（候选 B） | 默认关，UI 设置页可开 | 保留作为加分项 |

### 6.1 原生 UI 复用清单（已锁定）

已确认直接复用游戏原生 UI。本 mod 不写自有 Theme，不画自有底板，全部 clone 原生节点 + 改文本/绑事件。

| 用途 | 复用的原生节点 | 来源路径（反编译参考 20260417） |
|---|---|---|
| 面板根 / 模态层 | `NModalContainer` | `MegaCrit/sts2/Core/Nodes/CommonUi/NModalContainer.cs` |
| 主菜单入口按钮 | `NMainMenuTextButton` + `NSubmenu` | `MegaCrit/sts2/Core/Nodes/Screens/MainMenu/` |
| 暂停菜单入口按钮 | `NPauseMenuButton` | `MegaCrit/sts2/Core/Nodes/Screens/PauseMenu/` |
| 列表面板视觉 | `NRelicCollection` | `MegaCrit/sts2/Core/Nodes/Screens/RelicCollection/NRelicCollection.cs` |
| 类目分组 | `NRelicCollectionCategory` | 同上 `NRelicCollectionCategory.cs` |
| 单个图标格子 | `NRelicCollectionEntry` | 同上 `NRelicCollectionEntry.cs`，挂"已 ban"灰度 + 红 × 蒙层 |
| 子菜单堆栈 | `NSubmenu` / `NSubmenuStack` / `NMainMenuSubmenuStack` / `NCompendiumSubmenu` | `Core/Nodes/Screens/MainMenu/` |
| Tooltip / Hover | 沿用 `NRelicCollectionEntry` 自带 hover 行为，不另写 |  |

> 说明：
> - 反编译入口走 20260417（UI 类型稳定），逻辑跟 20260616（当前 beta）。
> - 所有节点通过 `PackedScene.Instantiate()` 或反射 `AccessTools.TypeByName(...)` clone；本 mod 不打包 .tscn。
> - 主菜单与暂停菜单两个入口最终调起的是**同一个 `NModalContainer` 实例**（按需懒构造，不做 singleton 预生成）。
> - 遗物 / 药水切页通过在 `NRelicCollection` 顶部塞两个 `NMainMenuTextButton` 风格 Tab 实现；不引入 `TabContainer`。

---

## 7. 项目结构计划

```
STS2_mod/MerchantBlacklist/
├── MerchantBlacklist.csproj
├── project.godot
├── mod_manifest.json
├── build.ps1
├── README.md
├── LICENSE
├── .gitignore
├── docs/
│   └── DESIGN.md                   ← 本文档
├── src/
│   ├── MerchantBlacklistMod.cs     入口 + Harmony 注册
│   ├── MerchantBlacklistLog.cs     统一日志前缀
│   ├── Core/
│   │   ├── BlacklistStore.cs       JSON 加载 / 保存 / HashSet 维护
│   │   ├── InventoryFilter.cs      生成后过滤 + 重抽
│   │   └── ModelCatalog.cs         全量 RelicModel/PotionModel 枚举（按稀有度分组）
│   ├── Patches/
│   │   ├── ModManagerInitPatch.cs              启动时机
│   │   ├── MerchantInventoryCreatePatch.cs     核心拦截
│   │   ├── MainMenuInjectPatch.cs              主菜单按钮入口
│   │   ├── PauseMenuInjectPatch.cs             暂停菜单按钮入口
│   │   └── HoverIdPatch.cs                     可选：hover 打 id
│   └── UI/
│       ├── BlacklistPanel.cs       独立设置面板根节点
│       ├── BlacklistGrid.cs        遗物 / 药水网格
│       ├── BlacklistGridItem.cs    单个图标格子
│       └── BlacklistEntryButton.cs 主菜单 / 暂停菜单入口按钮
├── torelease/                      staging（不进 git）
└── release/                        历次 zip 归档（不进 git）
```

`.gitignore` 同时忽略 `torelease/` 和 `release/`，对齐工作区规则。

---

## 8. 与现有 mod 的关系

- **RefreshShop**：完全互补。RefreshShop 重建商店 → 本 mod 过滤层在 Postfix 自动再跑一次。两者无代码耦合。
- **NoClientCheats**：无关。本 mod 不发网络消息，不会被反作弊拦。
- **ModListHider**：联机协议层无影响，可与之共存。

---

## 9. 下一步行动

1. 最终确认第 10 节 UI 细节（特别是 10.7 待决项）。
2. 仓库骨架初始化（参考 RefreshShop 同款结构）。
3. Core 层先行：JSON 加载 + Postfix 过滤 + 重抽逻辑 + ModelCatalog 全量枚举。
4. UI 层落地：先在主菜单挂入口按钮 → 面板可打开 → 网格能渲染所有遗物 → 点击 toggle ban → 自动持久化。
5. 单机验收 → 联机验收（重点确认对端 0 感知） → 打包 v0.1.0。

---

## 10. UI 详细设计（候选 C：独立设置面板）

### 10.1 入口位置

两处入口，二者只调起同一个面板：

- **主菜单**：在主菜单底部按钮组（设置 / 退出 那一栏附近）添加一个 `黑名单` 按钮。
- **暂停菜单**（局内 Esc）：在暂停菜单按钮列表里添加一个 `黑名单` 按钮。

> 入口节点查找路径在反编译的 `MainMenuScreen` / `PauseMenuScreen` 里确认，建议复用游戏原生 `Button` Theme，让按钮和原生菜单视觉统一。后续实现阶段再确认精确路径。

### 10.2 面板整体布局

```
┌─────────────────────────────────────────────────────────────┐
│ 商店黑名单                                          [×关闭] │
│─────────────────────────────────────────────────────────────│
│ [遗物] [药水]                          搜索: [_____________]│
│  ▲ Tab 切换                                                  │
│─────────────────────────────────────────────────────────────│
│ 稀有度筛选: [全部] [普通] [罕见] [稀有] [Boss] [Shop]        │
│ 一键操作:   [ban全部普通] [ban全部罕见] [清空黑名单]         │
│─────────────────────────────────────────────────────────────│
│ ┌───┬───┬───┬───┬───┬───┬───┬───┬───┬───┐                   │
│ │ R │ R │ R │ R │ R │ R │ R │ R │ R │ R │   ← 遗物图标网格 │
│ ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤                   │
│ │ R │ R*│ R │ R*│ R │ R │ R*│ R │ R │ R │   ← * = 已 ban   │
│ ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤                   │
│ │ ...                                    │   ← 滚动            │
│ └───┴───┴───┴───┴───┴───┴───┴───┴───┴───┘                   │
│─────────────────────────────────────────────────────────────│
│ 已 ban 遗物: 23 / 全部遗物: 187      [设置] [关闭]           │
└─────────────────────────────────────────────────────────────┘
```

- **整体居中**，半透明黑色遮罩盖住后面的菜单。
- 顶部 Tab：`遗物` / `药水` 两栏切换。
- 中部网格：每行 10 个图标，超出滚动。
- 单个格子大小约 80×96（含名字），鼠标 hover 显示原版 tooltip（复用游戏 `RelicHoverTip`）。
- **已 ban 状态视觉**：图标整体变灰 + 红色斜十字覆盖（半透明），一目了然。

### 10.3 单个格子的视觉与交互

```
┌──────────┐
│  [图标]  │   ← 复用 RelicModel.Icon 资源
│          │
│  圆环    │   ← 本地化名（取游戏 i18n）
│  普通    │   ← 稀有度小标签（颜色对应稀有度）
└──────────┘
   ↑
   左键单击：toggle ban / unban
   右键单击：复制 entry id 到剪贴板（方便日志/调试）
   Hover  ：显示原版遗物详情 tooltip
```

未 ban 状态：正常颜色。
已 ban 状态：饱和度降到 30% + 覆盖红色 `×` 图层（透明度 60%）。

### 10.4 一键操作

| 按钮 | 行为 | 是否需要确认弹窗 |
|---|---|---|
| ban 全部普通 | 当前 Tab 内所有 Common 稀有度全 ban | 否 |
| ban 全部罕见 | 同上 Uncommon | 否 |
| ban 全部稀有 | 同上 Rare | 否（仅 UI 提供，建议慎用） |
| ban 全部 Boss | 同上 Boss | 仅药水 Tab 不出现此项 |
| ban 全部 Shop | 同上 Shop（特定商店专属稀有度，如有） | — |
| 清空黑名单 | 当前 Tab 整栏清空 | **需要确认弹窗** |

按钮设计为对当前 Tab 生效（Tab=遗物 时 ban 的是遗物，Tab=药水 时是药水），不做"两栏一起一键"。

### 10.5 筛选与搜索

- 顶部稀有度筛选 chip：多选，默认全选。点击 chip 切换显示/隐藏对应稀有度的格子。
- 顶部搜索框：输入即过滤，匹配本地化名 + entry id（不区分大小写）。
- 右上角小开关「仅显示已获得过的」：默认关，开启后只显示玩家解锁/曾遇到过的遗物（依赖 `UnlockState` / 收集册数据）。
  - 如果该数据接入复杂，**首发版本不做**，留 v0.2。

### 10.6 游戏内快捷 ban（候选 B 的可选补充）

默认关闭。在面板底部「设置」中提供开关：

- 开关：`允许在商店中右键 ban`
- 开启后，玩家在商店遗物/药水图标上**右键**（或 `Shift+左键`，二选一，最终在 10.7.2 决定）即立即 ban 该 id 并记录 JSON。
- 不自动触发刷新（避免误触）；需要时用 RefreshShop 自己刷一下即可。

为什么默认关：主线是候选 C，且商店原生右键有可能被游戏占用。打开是加分项不是主要交付。

### 10.7 待最终确认的 UI 细节

对下列每条给出意向；不答则按「建议」项落地。

#### 10.7.1 入口位置数量
- [A] 仅主菜单（少改一处，开局前设置好就行）
- [B] 主菜单 + 暂停菜单（局内能改，体验最好）
- 建议：**B**。

#### 10.7.2 快捷键
- [A] 在面板顶部加一个全局快捷键 `F8` 任何时候按都能呼出（覆盖战斗 / 地图等）
- [B] 不做全局快捷键，只通过菜单进
- 建议：**B**，避免和其它 mod 的快捷键冲突。

#### 10.7.3 一键"ban 全部普通"是否需要二次确认
- [A] 需要确认弹窗
- [B] 不需要，直接 ban，反正能在面板里再 unban
- 建议：**B**。

#### 10.7.4 已 ban 视觉
- [A] 灰度 + 红 ×
- [B] 灰度 + 半透明
- [C] 红色描边
- 建议：**A**（灰度 + 红 ×），辨识度最高。

#### 10.7.5 排序方式
- [A] 按稀有度分组（Common→Uncommon→Rare→Boss→Shop）
- [B] 按字母 / 拼音
- [C] 按解锁时间
- 建议：**A**（按稀有度），符合"我想 ban 掉某档低质量"的真实使用动机。

#### 10.7.6 是否在面板顶部显示统计条
- 例如：`已 ban 遗物: 23/187 | 已 ban 药水: 5/40`
- 建议：**显示**，让玩家随时知道自己 ban 的规模。

#### 10.7.7 主题色
- 建议：**复用游戏原版 UI 主题**（黑底金边），保持视觉一致性，不自创风格。

---

## 11. 实现注意点（开发期）

- `MerchantInventory.CreateForNormalMerchant(player)` 是 public 静态构造路径，前面在 RefreshShop 已验证可用，本 mod 复用。
- `RelicGrabBag.RollRarity(rarity)` 必须按 `ParameterType` 精确匹配重载（之前 RefreshShop 踩过 `(Player)` 与 `(Rng)` 同名重载坑）。
- 重抽时不要清 `_deques`，仅取队头并跳过黑名单；超 N 次回退到 `keep_original`。
- UI 资源加载用 `RelicModel.Icon` 直接当 `Texture2D`，不要重新打包 png。
- 持久化用 `System.Text.Json` 走 `mods_settings` 目录，写入用 `File.WriteAllText` 原子替换（先写 `.tmp` 再 `Move`），防止崩溃半写坏文件。
- 全量遗物 / 药水枚举：通过反射拿 `Models/Relics` / `Models/Potions` 命名空间下的所有 `RelicModel` / `PotionModel` 单例（参考反编译 `MegaCrit\sts2\Core\Models\Relics`，约 298 个 .cs；`Models\Potions`，约 65 个 .cs）；启动时枚举一次缓存。
- 国际化：标签全部走 `Tr("MerchantBlacklist.…")`，首发提供 `en` + `zhs` 两种。

---

> 草案由煎包根据反编译事实与需求沟通整理。

