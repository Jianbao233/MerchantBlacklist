# MerchantBlacklist / 商店黑名单

> Slay the Spire 2 mod — local-only merchant inventory blacklist for relics, potions & cards.
> 本机商店遗物 / 药水 / 卡牌黑名单过滤器。

**Compatible with STS2 0.107.1 and newer**（已在 0.107.1 与 0.111.0 上核实 API 兼容）· **MIT License** · `affects_gameplay = false`

---

## English

### What it does

Filters blacklisted relics, potions and cards out of **the local player's** merchant
inventory (normal merchant + ancient merchant).

- View-only, local-only filter — never modifies world state.
- Never touches elite / boss / chest / event rewards, nor **card rewards**.
- Sends no network messages, the other player sees nothing.
- Multiplayer-safe: purchase/obtain messages carry the **full model**, not an inventory index,
  so filtering locally cannot desync the other players.

### Features

- **Pause menu entry** — adds a "Shop Blacklist" button to the pause menu, right
  under "Settings". Clones the native pause-menu button so the visuals match
  the rest of the menu.
- **Native compendium-style panel** — relics and potions are rendered via the
  game's own `NRelic` / `NPotion` widgets, so hover tooltips and shaders work
  exactly like the in-game compendium. Each rarity group has *Allow all* /
  *Ban all* shortcuts.
- **Cards tab** — bans cards sold by the merchant. Cards are listed as text rows
  (name / cost / type / rarity / id); hovering a row shows a card preview.
- **Two layers: global + per-character** — a global ban list applies to every
  character, and each character has its own list on top of it.
- **Right-click to ban inside the shop** — at any merchant, hold the modifier
  (default `Shift`) and right-click a relic, potion or card in stock to ban it.
  The slot is hidden immediately.
- **Default hotkey: `F10`** — toggles the panel.
- **Localized UI** — follows the client language: Chinese (any `zh*` variant; the
  panel text is Simplified) or English. The settings entries additionally ship a
  Traditional Chinese variant.

### Optional integrations

Neither is required — without them the `F10` hotkey still works through a built-in
fallback listener.

- **[STS2-RitsuLib](https://github.com/CursedStudio/STS2-RitsuLib)** — adds a settings
  page and routes the hotkey through its runtime router (blocks the key while typing
  in text fields or using the dev console).
- **[ModConfig](https://github.com/CursedStudio/ModConfig)** — exposes the hotkey for
  rebinding in its own panel.

### Install

1. No hard dependencies. Optionally install STS2-RitsuLib and/or ModConfig (see above).
2. Unpack the release zip into:
   ```
   <Steam>/steamapps/common/Slay the Spire 2/mods/MerchantBlacklist/
   ```
   The folder should directly contain `MerchantBlacklist.dll` and `mod_manifest.json`.
3. Launch the game and enable the mod from the mod list.

### Usage

| Action | Where |
| --- | --- |
| Open / close the blacklist panel | Pause menu → "Shop Blacklist", or press `F10` |
| Ban a relic / potion | Click its icon in the panel, **or** hold the modifier and right-click it in any merchant |
| Ban a card | Open the **Cards** tab and click a row, **or** hold the modifier and right-click it in the merchant |
| Unban | Click it again in the panel |
| Allow / ban a whole rarity group | Use the *Allow all* / *Ban all* buttons in each group |
| Switch between the global and a character list | The buttons at the top of the panel |
| Change the right-click modifier | RitsuLib settings page (or the ModConfig panel) |

### Build from source

```powershell
# Debug build + deploy to your local mods folder
.\build.ps1 -Configuration Debug

# Release build (writes torelease/ snapshot)
.\build.ps1 -Configuration Release
```

`torelease/` and `release/` are local-only and never committed.

---

## 简体中文

### 功能简述

仅在 **本机玩家自己的商店**（普通商人 + 先古商人）库存生成时，把已拉黑的遗物、
药水和卡牌筛掉。

- 纯视图过滤，不改世界状态。
- 不影响精英 / Boss / 宝箱 / 事件奖励，也**不影响卡牌奖励**。
- 不发任何网络消息，对端零感知，联机安全：购买 / 获得消息携带的是**完整 model**
  而不是库存索引，所以本地过滤不会让对端错位。

### 特性

- **暂停菜单入口**：在「设置」按钮正下方注入「商店黑名单」按钮，复用原生
  Material，外观与暂停菜单一致。
- **原生百科风面板**：遗物 / 药水使用游戏自己的 `NRelic` / `NPotion` 组件渲染，
  悬浮 tooltip、shader 高亮与百科一致。每个稀有度组带「全启用 / 全 ban」快捷按钮。
- **卡牌页签**：可拉黑商人出售的卡牌。卡牌以文字列表呈现（名称 / 费用 / 类型 /
  稀有度 / ID），鼠标悬停显示卡牌预览。
- **全局 + 角色两层**：全局黑名单对所有角色生效，每个角色另有自己的一层叠加在上面。
- **商店内右键即时拉黑**：在商人界面按住修饰键（默认 `Shift`）右键点击遗物 /
  药水 / 卡牌即可拉黑，对应槽位立即隐藏。
- **默认热键 `F10`**：切换面板。
- **跟随客户端语言**：中文（任意 `zh*` 变体，面板文案为简体）或 English；
  设置项另有繁体变体。

### 可选集成

两者都不是必需的 —— 不装也能用，`F10` 会走内置的保底轮询通道。

- **[STS2-RitsuLib](https://github.com/CursedStudio/STS2-RitsuLib)**：提供设置页，并把热键接入
  它的运行时路由器（在文本框输入或开发控制台时自动屏蔽该键）。
- **[ModConfig](https://github.com/CursedStudio/ModConfig)**：在它自己的面板里暴露热键供改键。

### 安装

1. 无硬依赖。可选安装 STS2-RitsuLib 和 / 或 ModConfig（见上）。
2. 把发布包解压到：
   ```
   <Steam>/steamapps/common/Slay the Spire 2/mods/MerchantBlacklist/
   ```
   该目录下应直接包含 `MerchantBlacklist.dll` 和 `mod_manifest.json`。
3. 启动游戏，在 mod 列表里启用即可。

### 使用方式

| 操作 | 位置 |
| --- | --- |
| 打开 / 关闭黑名单面板 | 暂停菜单 →「商店黑名单」，或按 `F10` |
| 拉黑遗物 / 药水 | 在面板里点击它，**或**在商人界面按住修饰键右键点击 |
| 拉黑卡牌 | 打开**卡牌**页签点击对应行，**或**在商人界面按住修饰键右键点击 |
| 取消拉黑 | 在面板里再次点击它 |
| 整组启用 / 拉黑 | 每个稀有度组里有「全启用 / 全 ban」按钮 |
| 切换全局 / 角色列表 | 面板顶部的按钮与下拉框 |
| 改右键修饰键 | RitsuLib 设置页（或 ModConfig 面板） |

### 从源码构建

```powershell
# Debug 构建并部署到本机 mods 目录
.\build.ps1 -Configuration Debug

# Release 构建（同时写入 torelease/ 快照）
.\build.ps1 -Configuration Release
```

`torelease/` 和 `release/` 仅本地保留，不会进 Git。

---

## License

MIT — see [LICENSE](./LICENSE).
