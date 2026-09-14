# PiP-Disabler — SPT 4.1.5 移植版

把 **PiP-Disabler**（原作者 [Fiodorwellfme](https://github.com/Fiodorwellfme)）移植到 **SPT 4.1.5 / EFT 0.16.9.5**。

> **本仓库内容 = 结案版（定版）**，不是实验分支。
> 版本 `1.5.0`　DLL `215 040` 字节　SHA256 `85AC781CDADF9EFA10F5D52862665D3F7C4B9E0AE670C7CFF1EFE3D4C5249A69`

---

## 这个模组做什么

原版 PiP-Disabler 用于关掉瞄具的「画中画」渲染（独立光学相机 + 画中画纹理）。本移植版在 4.1.5 上的效果：

- **放大倍镜内可见热成像** —— 镜片网格被清空、光学相机被抑制、主相机 FOV 被压到瞄具倍率，于是**镜内画面就是主相机画面**，自然继承热成像后处理（准星由 `ReticleRenderer` 在 `AfterEverything` 于热成像之后绘制）；
- 高倍镜「画面糊」消失（镜内不再是经画中画纹理的低分辨率画面）；
- 复合瞄具（如 EOTech HHS-1）可用；
- 配置菜单**显示文本外置**为语言文件，默认带英语与简体中文，并**自动跟随游戏内语言设置**。

## 安装

把以下文件**放在同一个目录**：`<游戏>\BepInEx\plugins\PiP-Disabler\`

```
PiP-Disabler.dll
pipdisabler_effect_shaders.bundle
pipdisabler_reticle_shaders.bundle
custom_mesh_surgery_settings.json      ← 按瞄具参数（82 条）
i18n\en.json
i18n\zh-CN.json
i18n\README.md
```

`release\` 目录里就是这一整套，直接整个复制过去即可。

需要 **BepInEx 5.4.23.5**（含 HarmonyX）。

> ⚠️ **两个必须知道的坑**
>
> 1. **运行时附属文件必须与 DLL 同目录。** 源码是**按 pluginDir 直接找文件名**的，把那两个 `.bundle` 放进 `Resources\Shaders\` 子目录会**静默失效**（不报错，只是功能没了）。
> 2. **不要改动 `[BepInPlugin]` 的版本号格式。** 它必须是合法 `System.Version`（纯数字，如 `1.5.0`）。写成 `"1.5.0-mvp-4.1.5"` 这类**带后缀**的字符串会让 BepInEx **静默丢弃**整个插件 —— 不加载、不报错、不生成配置；而且 `TypeLoader` 的类型缓存按「DLL 路径 + 最后写入时间」判定命中后会**一直沿用这个错误结论**，极难排查。（本项目真的踩过这个坑，白测两轮。）

## 语言文件

配置菜单的显示文本全部来自 `i18n\*.json`，**cfg 的段名与键名保持英文稳定标识符** —— 所以换语言不会改变配置文件结构、不需要迁移配置。

- 默认 `Language = Auto`，自动读取游戏内语言设置（`SPT_Runtime\user\sptSettings\Game.ini`）；
- 也可以在配置菜单里直接指定语言代码（`en`、`zh-CN`、`ru`…）；
- **新增一门语言**：复制 `i18n\en.json` 改名成目标语言代码，翻译其中的文本值即可 —— 三步教程见 `i18n\README.md`。**不需要重新编译。**

## 已知问题（结案决定：保留，不再修）

按瞄具个案问题——**这是取舍，不是可解方程**：挡住视线的那块几何本来就是瞄具自身外壳的一部分。「视线被挡」与「模型被删」是同一取舍的两端。

| 瞄具 | 现象 |
|---|---|
| SIG TANGO6T 1-6x24 | 模型缺失 ＋ 黑边厚（**可用**） |
| Marksman 3x30 | 全黑 |
| TA02 4x32 | 放大过头 |
| Monstrum 2x32 | 画面比镜框小 |
| ADO P4 | 边缘空隙 |

定版按瞄具参数 = `custom_mesh_surgery_settings.json`（82 条），TANGO6T 关键值 `NearPreserveDepth=0.005`、`Plane1Radius=0.047253523`。

## 构建

使用 Roslyn `csc` 直接编译（无 `.csproj`），需要 .NET SDK 与游戏目录下的 BepInEx/Managed 程序集作为参考：

```powershell
& <构建脚本> -ProjectDir <本仓库根> -OutDll <输出路径>\PiP-Disabler.dll
```

编译后务必回归校验：

- `Tools\verify_patch_targets.ps1` —— Harmony 目标必须 **72 EXISTS / 0 PROBLEM**，且 4.0.13 遗留名扫描 0 命中（会在 `Tools\` 下写出 `_verify_targets.md` / `_verify_transpiler.md` 两份临时清单）
- `Tools\verify_defaults_unchanged.ps1` —— 既有默认值不得变动（应报 **102 compared / 0 mismatched**）

> **可复现性已实测**：全新 `git clone` 本仓库后直接编译，得到与 `release\PiP-Disabler.dll` **逐字节相同**的文件
> （215,040 字节 / SHA256 `85AC781CDADF9EFA10F5D52862665D3F7C4B9E0AE670C7CFF1EFE3D4C5249A69`），
> 换个目录编译结果也一样。构建开关含 `-deterministic`；`.gitattributes` 锁定文本文件为 LF，
> 避免行尾被平台或 `core.autocrlf` 改写而影响结果。

> `Tools\` 下还留有更早期做法（把 cfg 键名汉化）的脚本 —— `verify_zh_rewrite.ps1`（其文件头已标 `OBSOLETE / DO NOT RUN`）、`apply_settings_zh.ps1`、`migrate_cfg_names.ps1`。**现行方案是外部语言文件，不需要它们**，保留仅为记录演进过程。
>
> `Tools\` 下的脚本保留了移植时使用的**作者本地绝对路径**（指向作者机器的游戏目录与工作区），在其他机器上需要先改这些路径。

## 目录

```
*.cs                    插件主体（PiPDisablerPlugin / Settings / I18n / ConfigManagerBridge / ConfigDefaultSync）
Patches\                全部 Harmony 补丁与渲染逻辑
Resources\Shaders\      着色器源码与编译好的 .bundle
i18n\                   语言文件 + 新增语言教程
Tools\                  构建后校验脚本
docs\                   移植说明、配置汉化对照表、结案说明
release\                可直接安装的一整套运行时文件
Settings.cs.en.bak      i18n 改造前的英文版 Settings.cs（历史对照）
Settings.cs.zh.bak      i18n 改造前的键名汉化版（历史对照）
```

## 许可

MIT。本项目是 **Fiodorwellfme 的 PiP-Disabler 的衍生作品**，原版权声明已在 `LICENSE` 中保留。
