# PiP-Disabler-Full — i18n 设计说明（显示层多语言）

> 适用插件：`com.fiodor.pipdisabler`，`[BepInPlugin]` 版本 **1.5.0**（本次改动**未**升版本、**未**改 GUID/Name）
> 相关文件：`I18n.cs`、`ConfigManagerBridge.cs`、`ConfigDefaultSync.cs`、`Settings.cs`、`i18n\en.json`、`i18n\zh-CN.json`、`i18n\README.md`
> 本文替代了上一版的「F12 配置菜单汉化对照表」——那张表的内容现在原样存在于发布格式里，见 `i18n\zh-CN.json`。

---

## 0. 一句话概括

**cfg 的段名/键名恢复为英文稳定标识符，永不随语言变化；所有对外可见文字改由插件目录下
`i18n\<语言代码>.json` 提供，随 DLL 一起发布，翻译者只改 JSON 不用碰代码。**

上一轮的实现是把这个 `Settings.cs` 里的段名、键名、说明**全部替换成中文**，
后果是 BepInEx 写出的 `com.fiodor.pipdisabler.cfg` 键名也变成中文——
只要换一次语言，玩家的全部配置就作废。本轮把这条路彻底改掉：

| | 上一轮（键名汉化） | 本轮（外部语言文件） |
|---|---|---|
| cfg 段名 | `全局网格手术` | `Global Mesh Surgery settings` |
| cfg 键名 | `近端保留深度(米)` | `NearPreserveDepth` |
| 换语言 | 需要迁移 cfg（重新调参） | 只换显示文字，cfg 不动 |
| 加语言 | 改源码 + 重新编译 | 复制一个 JSON 文件 |
| 默认值 | 不变 | **逐字节不变** |

---

## 1. 显示机制（怎么让 F12 显示别的文字）

ConfigurationManager 的每一条设置，在 `Config.Bind(...)` 时都会带一个
`ConfigDescription`，其第三个参数是一组 tag，本项目每条都传一个
`ConfigurationManagerAttributes`（`ConfigurationManagerAttributes.cs`，插件自己的一份副本）。

ConfigurationManager 会把这个 tag 对象上**同名字段**拷进它自己的
`SettingEntryBase` 里，然后照着 `SettingEntryBase` 画界面：

| tag 字段 | 拷到 `SettingEntryBase` | 界面上的效果 |
|---|---|---|
| `DispName` | `DispName` | 选项名（左列文字） |
| `Description` | `Description` | 鼠标悬停的气泡说明 |
| `Category` | `Category` | **段标题**（分组大标题） |
| `DefaultValue` | `DefaultValue` | “Reset”按钮回填的值 |
| `Order` / `IsAdvanced` / `Browsable` / … | 同名属性 | 排序 / 高级项 / 隐藏（既有行为，本轮未动） |

`I18n.ApplyToConfigEntries()` 就在这些 tag 上写译文：

```csharp
tag.Category    = 语言文件里的段标题（没有译文就写回英文段名，保证分组不裂开）
tag.DispName    = 语言文件里的 name（没有译文就写 null → 保持 cfg 键名）
tag.Description = 语言文件里的 description（没有译文就写 null → 保持 ConfigDescription 里的英文）
```

**写 `null` 是刻意的**：ConfigurationManager 的拷贝逻辑是「tag 字段非 null 才覆盖」，
所以 `null` 等于「本条没翻译，用回退值」，也就是 `ConfigDescription` 里保留的**英文原文**
（要求 3）。整个 i18n 层只是显示层，**字段名、类型、默认值、绑定逻辑、`Settings.Custom*` 一律未动**。

---

## 2. 加载链（语言文件怎么被找到）

```
插件目录（PiP-Disabler.dll 所在目录）
└── i18n\
    ├── en.json          ← 内置，必带
    ├── zh-CN.json       ← 内置，必带
    ├── README.md        ← 翻译教程
    └── <你的语言>.json   ← 玩家自己加的
```

`I18n.Load(wantedCode, rawValue)` 的查找顺序（先命中先返回）：

1. 文件名 == **Language 设置里原样填的字符串**（所以 `ch.json` 这种自定义文件名也能用）
2. 文件名 == 归一化后的语言代码（`ch` → `zh-CN.json`）
3. 文件里 `"_languageCode"` 字段 == 上面任一候选
4. `en.json`
5. 都没有 / 目录不存在 / JSON 读不出来 → **返回 null**，界面保持英文（不抛异常）

### 语言代码归一化表（`I18n.Aliases`）

重点是把**游戏自己的代码**映射到常见语言标签：

| 游戏内代码 | 归一化结果 | 说明 |
|---|---|---|
| `en` | `en` | 英语 |
| `ch` | `zh-CN` | EFT 用 `ch` 表示简体中文（见证据 3） |
| `ge` | `de` | EFT 用 `ge` 表示德语 |
| `jp` | `ja` | 日语 |
| `kr` | `ko` | 韩语 |
| `ru` / `fr` | 同名 | 俄语 / 法语 |

同时接受 `zh`、`zh-cn`、`cn`、`chs`、`中文`、`English`、`en-US` 等写法，
以及任何未知字符串（原样透传，所以“任意语言文件码”成立）。

---

## 3. 语言检测链（怎么知道游戏现在是什么语言）

`I18n.DetectCode()`，**逐级兜底，任何一步失败都继续往下走**：

| 步骤 | 来源 | 实测结果 |
|---|---|---|
| 1 | 运行时：`Comfort.Common.Singleton<EFT.Settings.SettingsManager>.Instance.Game.Settings.Language.Value` | 插件 `Awake()` 时该单例**还不存在**（BepInEx 比游戏主菜单早得多），因此启动时通常拿不到；`Update()` 里最多再复查 120 秒，拿到就自动纠正界面语言 |
| 2 | 游戏写盘的设置文件：`<游戏根>\SPT_Runtime\user\sptSettings\Game.ini`（另有 `<根>\user\sptSettings\Game.ini`、`%APPDATA%\Battlestate Games\Escape from Tarkov\Settings\Game.ini` 两个候选） | **本机实际生效的就是这一步**，读到 `"Language": "ch"` → `zh-CN` ✅（实测见第 6 节） |
| 3 | 系统区域：`UnityEngine.Application.systemLanguage` | 与 EFT 自己的 `LocalizationManager.DefaultLanguage` 用同一来源，映射表在 `I18n.TryReadSystemLocale` |
| 4 | 内置回退 `en` | 永不失败 |

每一步都单独 try/catch（`I18n.DetectCode` 内三步各自的 guard + `TryReadRuntimeCodeSafe`），
其中第 1 步标记 `[MethodImpl(NoInlining)]`：万一未来 SPT 挪走了
`EFT.Settings.SettingsManager`，JIT 失败被限制在这一帧内、被 catch 住，
**后面三步照常工作**（这一点在 `verify_patch_targets.ps1` 的 Newtonsoft 隔离检查里也有对应约束）。

### 手动覆盖：`General` → `Language`（新增的唯一配置项）

* 键名 `Language`（英文，稳定），默认值 `Auto`，位于 `General` 段并排在最上面。
* 取值：`Auto`（默认，走检测链）/ `English`（= `en`）/ `中文`（= `zh-CN`）/ 任意语言文件码（`ru`、`zh-TW`、`xx-mymod`…）。
* 语义与用法写进了语言文件本身的说明文字（`i18n\*.json` → `settings.General.Language.description`），
  所以它自己也会被翻译。
* 改动它时会立刻重新解析并刷新 F12 列表。

### 已知限制（如实说明）

* **不支持真正的运行时热切换。** ConfigurationManager 只在**构建设置列表**时读一次 tag
  （打开 F12 窗口时、以及切「Debug info」开关时）。因此换语言后需要**重开 F12**；
  本实现在语言变化时会主动请求 ConfigurationManager 重建列表（反射调用
  `BuildSettingList()`，且仅在窗口已打开时才调用），多数情况下不用重开，但这不是官方保证。
* **游戏内语言只读一次 + 120 秒复查。** 中途在游戏里改语言需要重启游戏。
* **段标题的本地化有代价**：ConfigurationManager 用 `Category` 字符串同时做**分组键**
  和**搜索匹配目标**，所以段名换成中文后，在搜索框里打英文段名不会命中
  （打中文段名可以）。为了不让“部分翻译”把同一段拆成两组，
  `I18n` 对**每一条**都写入 `Category`（没有译文时写回英文段名，即恒等映射）。

---

## 4. 两个必须先查清的问题（结论 + 反编译证据）

### 问题 1：ConfigurationManager 18.4 能不能覆盖“段标题”？

**结论：能，而且是官方字段——用 `ConfigurationManagerAttributes.Category`。**
不需要任何 hack，也不需要为了段名去迁移 cfg。

反编译证据（`D:\game\EFT-SPT 4.1.5\BepInEx\plugins\spt\ConfigurationManager\ConfigurationManager.dll`，ilspycmd 反编译）：

* `SettingEntryBase.cs:60` — `public string Category { get; protected set; }`（公有属性，存在）
* `SettingEntryBase.cs:203-224` — 反射取 tag 类型上的公有实例字段，与 `SettingEntryBase` 的
  公有属性**按名字 join**，`obj2 != null` 时 `item.my.SetValue(this, obj2, null)`；
  即 **tag 字段名 == 属性名** 就会被拷贝（`Category`、`DispName`、`Description`、`DefaultValue` 全在这张名单里）
* `SettingEntryBase.cs:95` — `public string Category;`（tag 侧字段定义，本工程 `ConfigurationManagerAttributes.cs` 第 95 行同款）
* `ConfigurationManager.cs:330-333` — `group x by x.Category into x ... Name = x.Key`（分组键就是 Category）
* `ConfigurationManager.cs:700` — `SettingFieldDrawer.DrawCategoryHeader(category.Name)`（段标题画的就是这个 Name）

反编译得到的 `ConfigurationManagerAttributes` 字段全集（本工程副本与之一致，共 15 个）：

`ShowRangeAsPercent`(bool?)、`CustomDrawer`(Action<ConfigEntryBase>)、`CustomHotkeyDrawer`(delegate)、
`Browsable`(bool?)、**`Category`(string)**、**`DefaultValue`(object)**、`HideDefaultButton`(bool?)、
`HideSettingName`(bool?)、**`Description`(string)**、**`DispName`(string)**、`Order`(int?)、
`ReadOnly`(bool?)、`IsAdvanced`(bool?)、`ObjToStr`(Func<object,string>)、`StrToObj`(Func<string,object>)

> 注：真正决定“哪些字段有效”的不是 tag 类本身，而是 ConfigurationManager 的
> `SettingEntryBase.SetFromAttributes`——它把 tag 的公开字段按名字拷到自己的公开属性上。
> `Category` / `DispName` / `Description` / `DefaultValue` 四者都在对应名单内，**均已实证**。
>
> 另：任务书里提到的
> `...\BepInEx\plugins\ConfigurationManager\ConfigurationManager.dll` 并不存在；该目录下装的是
> **`ConfigurationManager.zh-cn.dll`（汉化补丁，24 KB）**，正主在
> `...\BepInEx\plugins\spt\ConfigurationManager\ConfigurationManager.dll`（61 KB, v18.4）。
> 那个 zh-cn 补丁走的是另一套路线（读 `BepInEx\plugins\zh-cn\<GUID>.jsonc`，
> 再用反射改写 `SettingEntryBase` 的 `DispName`/`Description`/`Category`），
> 与本插件自己的 `i18n\` 完全独立；它只在存在 `com.fiodor.pipdisabler.jsonc` 时才动手，
> 本机没有该文件，所以两者不会打架。

### 问题 2：能不能动态设置 `DefaultValue`，让 F12 的“Reset”回落到**当前瞄具保存的值**？

**结论：能，已实现。**

* tag 的 `DefaultValue` 字段**优先于** cfg 的编译期默认值；
* 每次 `BuildSettingList()`（= 每次打开 F12）都会重新读一次，所以运行中改它是有效的；
* 不需要反射进 `SettingEntryBase`（它的 setter 是 `protected`），因为**改自己的 tag 就够了**。

反编译证据：

* `ConfigSettingEntry.cs:36-38` — 构造函数里先 `base.DefaultValue = entry.DefaultValue;`，
  **随后**才 `SetFromAttributes(entry.Description.Tags, owner)`；
  而 `SetFromAttributes` 只在 tag 值非 null 时覆盖（`SettingEntryBase.cs:210-217`）
  ⇒ **tag 的 `DefaultValue` 覆盖 cfg 默认值；写 null 则回退到 cfg 默认值。**
* `ConfigurationManager.cs:753-768` — `DrawDefaultButton`：`object defaultValue = setting.DefaultValue;`
  然后按钮里 `setting.Set(defaultValue)`
* `ConfigurationManager.cs:189` / `283-292` — `DisplayingWindow` setter 调 `BuildSettingList()`，
  它内部 `SettingSearcher.CollectSettings` 每次**新建** `ConfigSettingEntry`
  （`SettingSearcher.cs:99-102`）⇒ 打开窗口时必然重读 tag
* `I18n` / `ConfigDefaultSync` 拿到 tag 的方式与既有 `Settings.RecalcOrder()` 完全一致
  （`entry.Description.Tags[0]`；`ConfigEntryBase.Description` 与 `ConfigDescription.Tags` 均为公有属性）
* 类型安全：`BepInEx.Configuration.ConfigEntry<T>.BoxedValue` 的 setter 是硬转换 `Value = (T)value;`
  ⇒ 写入的 `DefaultValue` 必须是**精确类型**（float 而不是 double）。`ConfigDefaultSync` 因此
  全部用 `float`/`bool` 装箱，并在第 6 节做了 Reset 实测。

### 实现：`ConfigDefaultSync.cs`

* 在 `PerScopeMeshSurgerySettings.SyncCustomConfigFromOverride()`（按瞄具值载入 `Custom*` 配置项的地方）
  同步调用：
  * 命中按瞄具存档 → `ConfigDefaultSync.ApplyFromScope(entry)`：把该瞄具的 24 个值写成
    `Custom*` 各条目的 `DefaultValue`；
  * 该瞄具没有存档 → `ConfigDefaultSync.Clear()`：把 `DefaultValue` 写回 `null`，
    Reset 于是回到编译期内置默认值。
* 只有真的发生变化时才请求 ConfigurationManager 重建列表（`ConfigManagerBridge.RequestRebuild()`，
  且仅在 F12 窗口已打开时调用，避免无谓开销）。

**效果**：开镜 → F12 → “Per scope settings” 里点 Reset，回填的是**这个瞄具存过的值**，
不再是与你无关的固定默认值。这正是用户提出的诉求。

---

## 5. 加载路径 / 部署

* 语言文件目录按 **DLL 自己所在目录**推导：
  `Path.GetDirectoryName(typeof(I18n).Assembly.Location) + "\i18n"`
  （与既有 `PerScopeMeshSurgerySettings.GetPluginRootDirectory()` 同一套写法），
  所以部署后就是 `BepInEx\plugins\PiP-Disabler\i18n\`。
* 发布内容（与 DLL 同级）：

```
BepInEx\plugins\PiP-Disabler\
├── PiP-Disabler.dll
├── custom_mesh_surgery_settings.json
├── pipdisabler_effect_shaders.bundle
├── pipdisabler_reticle_shaders.bundle
└── i18n\
    ├── en.json
    ├── zh-CN.json
    └── README.md
```

* 文件缺失 / 目录缺失 / JSON 损坏都只是**回退英文 + 一条 warning**，不影响模组功能。
* 中文 JSON 里的内容**不是重新翻译的**：来自 `Tools\settings_zh_map.json`（上一轮的中英映射表，
  101 条）逐条取用；英文侧来自 `Settings.cs.en.bak` 原文。

---

## 6. 实测（不是纸面推断）

### 6.1 语言文件：102/102 全覆盖

`Tools\gen_i18n.ps1` 生成后回读两个 JSON 并逐条比对源码字面量：
`sections 7 / en.json 102 条 / zh-CN.json 102 条 / verify: OK`。
与 `Settings.cs` 里的 102 个 `config.Bind()` 逐条对照：

| 段名 | 条目数 |
|---|---|
| `General` | 16（含新增的 `Language`） |
| `Hacks` | 13 |
| `Graphics` | 3 |
| `Debug` | 4 |
| `Global Mesh Surgery settings` | 24 |
| `Per scope settings` | 26 |
| `Scope Effects` | 16 |
| **合计** | **102** |

**缺失条目：0；回退条目：0。** 未来若上游新增配置项而语言文件没跟上，
该条会回退英文（不会报错、不会空着），`gen_i18n.ps1` 重跑会直接报出行号不一致。

### 6.2 用真实的 ConfigurationManager 18.4 验证显示层

把插件真实的 tag 对象交给 **真的 `ConfigurationManager.ConfigSettingEntry`** 构造，
再读回它自己的属性（脚本 `_tools\test_i18n_cm.ps1`，一次性、不在工程目录里）：

```
entry 'NearPreserveDepth'  (cfg default = 0.02549295)
    DispName    = '近端保留深度(米)'
    Category    = '当前瞄具设置'   <- ConfigurationManager 画的段标题
    Description = '按瞄具自定义的近端保留深度（米）。'
    DefaultValue= 0.0123 (Single)   <- Reset 按钮回填的值
after Reset, entry.Value = 0.0123   (按瞄具存档值)
after Clear:                0.02549295  (回到编译期默认值)
```

⇒ 问题 1、问题 2 都在**真实程序集**上跑通了，不只是反编译推断。

### 6.3 检测链实测

```
TryReadRuntimeCodeSafe()   -> False      （进程外，符合预期；游戏内 Awake 时该单例尚未创建）
BepInEx.Paths.GameRootPath = 'D:\game\EFT-SPT 4.1.5'   （BepInEx 预加载器在游戏内设置的值）
TryReadGameSettingsFile()  -> True
DetectCode()               -> 'zh-CN'   source: game settings file (…\SPT_Runtime\user\sptSettings\Game.ini)
NormalizeCode('ch')        -> 'zh-CN'   -> zh-CN.json found
文件夹不存在                -> null（不抛异常，回退英文）
```

### 6.4 编译与回归

| 项 | 结果 |
|---|---|
| `build.ps1` `csc exit` | **0**，warning/error 行 **0** |
| 参与编译的 `.cs` | 35（原 32 + 新增 3） |
| `Tools\verify_patch_targets.ps1` | Harmony 目标 **72 EXISTS / 0 PROBLEM** |
| 遗留名扫描 | **0 命中** |
| Newtonsoft.Json 隔离 | 5 个使用者 / **0** 个缺 `NoInlining` |
| 产物中的 cfg 段名/键名 | 7 段全英文；101 条既有默认值 token 与 `Settings.cs.en.bak` **逐条一致**（唯一新增 token = `Language` 的 `I18n.AutoValue`） |
| 产物中的中文段名/键名 | **0** |

---

## 7. 改动清单（相对上一轮中文版 `Settings.cs`）

| 文件 | 改动 |
|---|---|
| `Settings.cs` | 从 `Settings.cs.en.bak` 恢复（段名/键名/说明全部回英文）；新增 `Language` 字段与绑定；`Init()` 末尾新增 `I18n.Initialize(Language);` |
| `I18n.cs` | **新增**：语言文件加载/归一化/检测链/应用到 tag/日志 |
| `ConfigManagerBridge.cs` | **新增**：反射访问 ConfigurationManager（取 tag、判断窗口是否打开、请求重建列表） |
| `ConfigDefaultSync.cs` | **新增**：按瞄具值 → 各 `Custom*` 条目的 Reset 回填值 |
| `PiPDisablerPlugin.cs` | `Update()` 首行加 `I18n.Tick();`；`OnDestroy()` 加 `I18n.Shutdown();` |
| `Patches/PerScopeMeshSurgerySettings.cs` | `SyncCustomConfigFromOverride()` 里两处各加一行 `ConfigDefaultSync.ApplyFromScope(entry)` / `Clear()` |
| `Tools/gen_i18n.ps1` | **新增**：从 `.en.bak` + `settings_zh_map.json` 生成并校验两个语言文件 |
| `Tools/verify_patch_targets.ps1` | Newtonsoft 隔离检查的**期望值文案**改为动态（“每个使用者都必须是 NoInlining 包装”），**FAIL 条件未放宽**；补注释 |
| `i18n\en.json` / `i18n\zh-CN.json` / `i18n\README.md` | **新增** |
| `Settings.cs.en.bak` | 保留（英文原文基线，`gen_i18n.ps1` 的输入） |
| `Settings.cs.zh.bak` | 保留（上一轮中文版存档，仅作备份，**不参与编译**） |

### 未动的东西（安全红线）

* `[BepInPlugin("com.fiodor.pipdisabler", "PiP-Disabler", "1.5.0")]` —— GUID / Name / 版本**一字未改**，未升版本。
* 全部 `config.Bind` 的**字段名、类型、默认值、`AcceptableValueRange`、
  `ConfigurationManagerAttributes` 既有参数**（`IsAdvanced` / `ShowRangeAsPercent` / `Order`）逐条未动；
  101 条既有默认值经 token 比对与 `Settings.cs.en.bak` 完全一致。
* `Settings.Custom*` 字段、按瞄具 JSON 结构、Harmony 补丁目标、着色器、Mesh 逻辑均未触碰。
* 未写 `D:\game\`；未动 `PiP-Disabler-MVP\`、4.0.13 原始源码、`PiPDiag\`。

---

## 8. 怎么加一门语言（速查，详见 `i18n\README.md`）

1. 复制 `i18n\en.json` → `i18n\<语言代码>.json`（如 `ru.json`）。
2. 只翻译 `name` / `description` / `sections` 的**冒号右边**；键名一个都别动。
3. 改 `_languageCode`（如 `"ru"`）和 `_language`（如 `"Русский"`），保存。

游戏内 F12 → PiP-Disabler → `Language` 填该代码（或留 `Auto` 让游戏语言自动决定）。
没翻完也安全：缺的条目自动显示英文。
