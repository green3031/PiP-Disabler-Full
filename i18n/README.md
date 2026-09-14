# PiP-Disabler 语言文件 / Language files

这个文件夹里的 `.json` 文件决定 F12 设置菜单**显示出来的文字**。
修改它们**不需要编程、不需要重新编译**，改完保存、重开游戏即可生效。

The `.json` files in this folder decide the **text shown** in the F12 settings menu.
Editing them needs **no programming and no recompiling** — save the file, restart the game, done.

---

## 中文说明

### 0. 先搞清楚：改哪里？

一个语言文件长这样（片段）：

```json
{
  "_language": "中文（简体）",
  "_languageCode": "zh-CN",

  "sections": {
    "Per scope settings": "当前瞄具设置"
  },

  "settings": {
    "Per scope settings": {
      "NearPreserveDepth": {
        "name": "近端保留深度(米)",
        "description": "按瞄具自定义的近端保留深度（米）。"
      }
    }
  }
}
```

**规则只有一条：冒号左边的东西一个字都不能动，冒号右边的东西才是给你翻译的。**

| 位置 | 能不能改 | 说明 |
|---|---|---|
| `"_language"` 右边 | ✅ 改 | 这门语言在日志里显示的名字，随便写 |
| `"_languageCode"` 右边 | ✅ 改 | 语言代码，建议和文件名一致 |
| `"sections"` 里冒号**右边** | ✅ 改 | 设置菜单里的**段标题**（大标题） |
| `"sections"` 里冒号**左边** | ❌ 别动 | 这是 cfg 里的段名，改了就会显示回退成英文 |
| `"settings"` 里第一层和第二层的键（如 `"Per scope settings"`、`"NearPreserveDepth"`） | ❌ 别动 | 这是 cfg 里的段名 + 键名，是唯一标识 |
| `"name"` 右边 | ✅ 改 | 选项名（F12 里左侧那一列） |
| `"description"` 右边 | ✅ 改 | 鼠标悬停时弹出的说明 |
| `"_readme"` | ❌ 别动 | 只是提示文字 |

> ⚠️ 段名和键名（cfg 里的名字）**永远保持英文**。这是设计使然：
> 它们要和 `BepInEx\config\com.fiodor.pipdisabler.cfg` 里的键一一对应，
> 一旦跟着语言变，每次换语言你的配置就会全部丢失。所以只有**显示文字**会变。

### 1. 三步新增一门语言

假设你要做**俄语**：

1. **复制** `en.json`，粘贴成 `ru.json`（文件名用语言代码，见下面的表）。
2. **只翻译 value**：把每个 `"name"` 和 `"description"` 右边的英文换成俄语。
   `sections` 里冒号右边的英文也一起换掉。左边的键名一个都别动。
3. 把 `"_languageCode"` 改成 `"ru"`，`"_language"` 改成 `"Русский"`。保存。

完成。游戏里 F12 → PiP-Disabler → **Language** 里填 `ru`（或直接留 `Auto`，
只要游戏语言是俄语就会自动选中）；游戏内的语言设置是俄语时也会自动用它。

半途而废也没关系：**没翻译的条目会自动显示英文**，不会报错、不会空着。

### 2. 文件名和语言代码

文件名必须是 `<语言代码>.json`。游戏自己用的代码（EFT 的内部代码）已经内置了对应关系：

| 游戏内语言 | EFT 内部代码 | 建议文件名 | 备注 |
|---|---|---|---|
| 英语 English | `en` | `en.json` | 内置，随插件发布 |
| 简体中文 | `ch` | `zh-CN.json` | 内置，随插件发布 |
| 繁体中文 | — | `zh-TW.json` | |
| 俄语 Русский | `ru` | `ru.json` | |
| 德语 Deutsch | `ge` | `de.json` | 注意 EFT 用 `ge` 表示德语 |
| 法语 Français | `fr` | `fr.json` | |
| 日语 日本語 | `jp` | `ja.json` | |
| 韩语 한국어 | `kr` | `ko.json` | |
| 西班牙语 | — | `es.json` | |
| 葡萄牙语 | — | `pt.json` | |
| 波兰语 | — | `pl.json` | |
| 土耳其语 | — | `tr.json` | |
| 捷克语 | — | `cs.json` | |
| 泰语 | — | `th.json` | |
| 越南语 | — | `vi.json` | |
| 乌克兰语 | — | `uk.json` | |

代码随便取也行（比如 `xx-mymod.json`），只要在 **Language** 里填同样的字符串就能选中。

### 3. 格式上的几个注意事项

* **编码必须是 UTF-8**（记事本另存为时选 UTF-8；VS Code 默认就是）。
* 引号是**英文双引号** `"`，不是中文引号 `“”`——用中文引号会导致整个文件读不出来（日志里会有一条 warning，然后回退英文）。
* 每个键值对之间要有逗号 `,`，**最后一个不要加逗号**。
* 说明里要换行就写 `\n`（反斜杠 + n），不要真的在引号里敲回车。
* 想在文件里写备注，可以让整行以 `//` 开头（这一行会被忽略）。
* `description` 留空或者删掉 → 该条目回退显示英文说明。

### 4. 怎么确认生效了？

开游戏，看 `BepInEx\LogOutput.log`，应该有一行：

```
[I18n] language folder: ...\BepInEx\plugins\PiP-Disabler\i18n
[I18n] display language 'zh-CN' (中文（简体）) loaded from ...\i18n\zh-CN.json - 102 entries / 7 sections. Selected by: game settings file (...Game.ini)
```

这行会告诉你：用了哪个文件、多少条、以及**是根据什么选中它的**（游戏内设置 / 游戏设置文件 / 系统语言 / 手动指定 / 内置回退）。

### 5. 已知限制（诚实说明）

* **不支持运行中热切换。** ConfigurationManager 只在打开 F12 窗口那一刻读取显示文字，
  所以换了语言（或改了 `Language` 项）之后要**关掉 F12 再打开**才会看到新文字。
  程序会在语言切换后主动请求重建一次列表，多数情况下直接就能看到；
  但如果没变，重开一次窗口即可。
* **游戏内语言是启动时读一次。** 游戏自己的语言设置要等主菜单阶段才存在，
  插件加载时读不到，会先按“游戏设置文件 → 系统语言 → 英文”兜底，
  之后最多 120 秒内再复查一次运行时的真实语言并自动纠正。
  中途在游戏里改语言，要重启游戏才会跟过来。
* **搜索框按翻译后的段标题/选项名匹配。** 界面切成中文后，在搜索框里打英文段名不会再命中。

---

## English

### 0. What may you change?

**Everything to the LEFT of a `:` is an identifier — do not touch it.
Everything to the RIGHT is text for you to translate.**

* Changeable: `_language`, `_languageCode`, the values in `sections`,
  and each entry's `name` / `description`.
* Never change: `settings` keys (they are `"Section" -> "CfgKey"`), the keys in `sections`,
  and `_readme`.

Section names and key names are **always English** on purpose: they are the identifiers in
`BepInEx\config\com.fiodor.pipdisabler.cfg`. If they followed the language, every language
switch would throw your configuration away. Only the displayed text changes.

### 1. Add a language in three steps

1. **Copy** `en.json` to `<language code>.json` (e.g. `ru.json`, `zh-TW.json`).
2. **Translate only the values** — every `"name"`, every `"description"`, and the values in
   `sections`. Leave every key exactly as it is.
3. Set `"_languageCode"` to your code and `"_language"` to the display name. Save.

That's it. `Auto` (the default of the `Language` setting) picks your file automatically when the
game language matches, or type the code into the **Language** entry in F12.
Untranslated entries simply fall back to English — a partial translation is fine.

### 2. Formatting rules

* File must be **UTF-8**. Use straight `"` quotes, never curly `“”` quotes
  (one bad quote makes the whole file unreadable; the log will say so and English is used).
* Commas between members, **no** trailing comma.
* Newlines inside a description are written `\n`.
* A line starting with `//` is ignored, so you can annotate the file.
* Empty/missing `description` falls back to the English one.

### 3. Known limits

* **No live switching.** ConfigurationManager reads the display text when the F12 window is
  opened. After changing the language, close and reopen F12 (the plugin also asks it to rebuild,
  so most of the time it updates immediately).
* The in-game language is read once at startup; the plugin re-checks it for up to 120 seconds
  and corrects itself if the runtime value differs. Changing the game language mid-session
  needs a game restart.
* The F12 search box matches the *translated* section/entry names.
