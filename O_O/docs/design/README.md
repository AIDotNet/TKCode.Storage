# O_O 桌面灵动岛 — 设计总览

> 状态：Draft v0.1（2026-11-09）
> 目标平台：Windows 10/11、macOS 14 Sonoma+（含刘海屏 MacBook）、Linux（X11；Wayland 会话通过 XWayland 运行）

## 1. 一句话定位

一个**极薄的跨平台"壳"**：常驻屏幕顶部中央的胶囊窗口 + 控制台；所有业务能力（音乐、时间、待办、专注、日程、剪贴板、文件暂存、邮箱、AI 余额……）都以**插件**形式提供，第一方插件和第三方插件走同一套 API。

## 2. 技术栈（全部锁定精确版本）

| 层 | 选型 | 版本 | 说明 |
| --- | --- | --- | --- |
| 运行时 | .NET 10 (LTS) | 10.0.x | 以 NativeAOT 发布，单文件、秒启动、低内存 |
| UI 框架 | Avalonia | 12.1.4 | 与 TKCode.Arc 绑定的版本 |
| 组件库 | TKCode.Arc (+ Blocks / Special / Icons.Lucide / Fonts) | 1.0.0 | **唯一允许的 UI 组件来源**，见 [§5](#5-组件库约束) |
| JS 引擎 | Jaunt | 1.0.1 | 纯托管、NativeAOT 兼容、可设预算、隔离 Realm |
| 存储 | SQLite (Microsoft.Data.Sqlite) + JSON | — | 插件 KV / 剪贴板历史 / 待办 |
| 凭据 | Keychain / Windows Credential Manager / libsecret | — | Token、API Key 永不明文落盘 |
| Linux 系统接口 | Tmds.DBus.Protocol | — | MPRIS、通知、Secret Service（AOT 友好） |
| Windows 系统接口 | CsWin32 源生成 + CsWinRT | — | 窗口样式、剪贴板监听、GSMTC 媒体 |
| macOS 系统接口 | `LibraryImport` 直调 `objc_msgSend` | — | NSWindow / NSScreen / NSPasteboard |

## 3. 文档索引

| 文档 | 内容 |
| --- | --- |
| [01-architecture.md](01-architecture.md) | 分层、工程结构、进程/线程模型、数据与安全 |
| [02-island-shell.md](02-island-shell.md) | 灵动岛窗口、状态机、**刘海适配**、多屏、点击穿透、动画 |
| [03-plugin-system.md](03-plugin-system.md) | 插件清单、生命周期、Jaunt 集成、JS API、声明式 UI、权限、分发 |
| [04-console.md](04-console.md) | 控制台面板的信息架构与每页使用的 Arc 组件 |
| [05-builtin-plugins.md](05-builtin-plugins.md) | 第一方插件逐个设计（音乐/时间/主题/待办/专注/日程/文件暂存/剪贴板/邮箱/AI 余额）+ 扩展候选 |
| [07-home-and-layout.md](07-home-and-layout.md) | 首页与页面、收起时的小项、胶囊 / 刘海形态与三边停靠、"岛体"设置页（已定稿，待实现） |
| [06-roadmap.md](06-roadmap.md) | 里程碑、风险清单、待决问题 |

## 4. 设计原则

1. **壳只做"岛"本身**：窗口、布局、动画、调度、插件宿主、系统能力桥接。凡是"展示什么"都交给插件。
2. **能力在宿主，逻辑在插件**：涉及平台 API 的能力（媒体会话、剪贴板、文件拖放、钥匙串）由 C# 宿主实现为 *Capability*，插件通过权限申请使用；插件本身是纯 JS，跨平台零差异。
3. **插件 UI 是声明式的**：JS 输出虚拟节点树，宿主映射到 Arc 控件。插件无法直接触达 Avalonia 对象 → 安全、AOT 友好、主题统一。
4. **默认低打扰**：空闲时 CPU < 0.5%、无动画帧；只在状态变化时渲染。
5. **隐私默认开启**：剪贴板忽略密码管理器内容；所有网络访问按域名授权；凭据只进系统钥匙串。

## 5. 组件库约束

所有界面元素必须来自 TKCode.Arc（`xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"`），遵守其 agent 规则：

- 必须写 `arc:` 前缀，否则 `Button`/`Slider` 等会解析到 Avalonia 原生控件。C# 中用别名 `using ArcButton = TKCode.Arc.Button;`。
- 只使用组件 API 参考中存在的属性/事件/枚举；实现前逐个查 `https://docs.tkcode.si/components/<name>.api.md`。
- 颜色、圆角、间距、字体只用设计 Token（`{arc:ArcDesignTokenResource Surface}` / `ArcTokens.Bind(...)`），**禁止硬编码 hex**（唯一例外见 02 文档"刘海区纯黑"，也通过自定义 Token 实现）。
- 图标：`ArcIcons.*` 或 `lucide:LucideIcon`。插件通过数据传入图标名 → 必须在工程里开启 `<LucideIconsIncludeAll>true</LucideIconsIncludeAll>`（代价是体积，见 06 风险）。
- 自定义控件（如岛本体 `IslandSurface`）的过渡写在 ControlTheme 的 `^:motion-full` / `^:motion-reduced` 下，并设 `arc:ArcMotion.Tracks="True"`，尊重系统"减少动态效果"。
- 文案句式大小写；按钮说清做什么（"保存更改"而非"确定"）；每个界面最多一个 Primary 按钮。

## 6. 默认视觉：苹果风格

所有窗口（岛与控制台）默认苹果风格，三平台一致。实现方式是 Arc 的主题机制，而不是另写一套控件：

| 方面 | 做法 |
| --- | --- |
| 强调色 | Apple 系统蓝 `#007AFF` 作为 Arc 的 accent 种子，暗色由 Arc 自动推导 |
| 圆角 | `TKCode.Arc.Themes.Corners` 的 `Scale(0.6)`：控件≈11、面板≈16、大面≈20，接近 macOS 比例 |
| 字体 | macOS 用系统字体（SF）；Windows / Linux 用 Inter（SF 授权不允许随应用分发） |
| 数字 | 时钟等数字使用等宽数字（`tnum`） |
| 岛 | 纯黑、与刘海融合、弹簧形变（Arc `Morph`）。外形是**连续曲率圆角**（"超椭圆"，平滑系数 0.6，与 Apple 的圆角一致，`ContinuousShape`），贴顶或包裹刘海时两侧有**内凹的肩部**过渡到屏幕上沿，像 MacBook 的刘海。展开后的卡片高度随内容自适应（120–400pt） |
| 玻璃质感 | 控制台窗口向系统申请半透明材质（macOS 磨砂、Windows 11 Mica、Windows 10/11 Acrylic）：侧边栏完全透出材质，内容区按 Arc 背景色 55% 着色，卡片按 Surface 色 70% 着色；系统不支持或开启"减少透明度"时自动回到不透明的 token 颜色。灵动岛不使用玻璃：它要与刘海同色 |
| 岛内控件 | 岛始终是深色：用 Arc 的 `ArcThemeScope.Appearance="Dark"` 让岛内所有 Arc 控件固定为深色，强调色和动效仍跟随应用 |
| 控制台窗口 | macOS 用系统红绿灯；Windows / Linux 在 `ArcWindow` 标题栏左侧绘制红绿灯样式的窗口按钮（`CaptionButtons="None"` + 自绘），可在设置里切回系统样式（M3 实现） |

代码：`src/Island.UI/Themes/AppleTheme.cs`（命名主题预设 `O_O.Apple`）。用户在控制台选的强调色会叠加在其上。
