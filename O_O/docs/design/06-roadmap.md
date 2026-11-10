# 06 · 路线图、风险与待决问题

## 1. 里程碑

| 阶段 | 目标 | 交付 |
| --- | --- | --- |
| **M0 技术验证**（1~2 周） | 先把最高风险点打穿 | ① 三平台透明置顶窗口 + 点击穿透 ② macOS 刘海检测与包裹布局 ③ Jaunt 长驻引擎 + 宿主事件循环 + 一个 VNode 渲染到 Arc 控件 ④ NativeAOT 三平台发布跑通 ⑤ 核实 Claude Code / Codex 本机登录配置的位置与格式 ⑥ 核实钉钉 / 企业微信用户级日程读取权限 |
| **M1 壳** | 可用的岛（单实例、托盘 / 菜单栏图标、全屏隐藏、显示器"主屏 / 跟随鼠标"已完成，2026-11-10） | 状态机、Activity 调度、动画、多屏、全屏隐藏、托盘、单实例、设置存储 |
| **M2 插件宿主** | 完整插件 API v1 | Manifest/权限/贡献点、`@island/api` 首批能力、VNode 白名单全集、预算熔断、开发模式热重载、SDK 脚手架 |
| **M3 控制台** | 配置全流程（第一部分已完成：插件 / 外观 / 关于页、授权、自动设置表单） | 外观、位置、插件管理、权限、快捷键、隐私、自动设置表单 |
| **M4 第一方插件 I** | 日常可用 | 时钟、音乐、主题包、待办、专注、剪贴板历史、文件暂存 |
| **M5 第一方插件 II** | 连接外部服务 | 日程聚合插件 + 飞书 / 钉钉 / 企业微信 / Google 平台插件、邮箱、AI 用量聚合插件 + 提供商插件（Claude、Codex、DeepSeek、Kimi、NewAPI 先行） |
| **M6 发布** | 公测 | 签名/公证（Windows Authenticode、macOS notarization）、自动更新、插件包签名、文档站 |

## 2. 风险清单

| # | 风险 | 影响 | 应对 |
| --- | --- | --- | --- |
| R1 | **Linux Wayland** 不允许客户端自定位、置顶、全局输入区域 | Linux 上岛位置错乱 | **已定：XWayland**。剩余风险：未安装 XWayland 的发行版（启动时检测并提示安装）、分数缩放下 XWayland 窗口模糊 |
| R2 | **macOS 15.4+ 限制 MediaRemote** | Mac 上拿不到通用"正在播放" | 2026-11-10 本机（macOS 26.6）：框架可加载，`MRMediaRemoteGetNowPlayingInfo` 返回空——当时无播放器运行，不能据此判断是否受限。已实现的回落：Music / Spotify 的分布式通知 + 一次 AppleScript 读初始状态；命令仍用 `MRMediaRemoteSendCommand`（发送命令据报不受限，未实测）。通用读取（浏览器、第三方播放器）仍需 adapter 方案（系统自带可信二进制加载框架的 Sidecar），待做 |
| R3 | 点击穿透切换的边缘抖动（快速移动鼠标时） | 体验 | 命中区加容差 + 防抖；Windows 优先 `WM_NCHITTEST` 方案对比 |
| R4 | `LucideIconsIncludeAll` 增大体积 | 包体 | 已实测：`Island.UI.dll` 从 57KB 增至 663KB，对启动时间无可测影响；暂保留全集 |
| R5 | MailKit、CsWinRT 在 NativeAOT 下的裁剪警告 | 构建/运行时异常 | M0 做 AOT 冒烟测试；必要时 rd.xml / `DynamicDependency` 或改用更薄的 IMAP 实现 |
| R6 | 非官方 AI 额度接口变动 | 功能失效 | 每个提供商是独立插件，可单独更新；标注"非官方"；失败时降级为手动模式 |
| R7 | 飞书 / 钉钉应用发布需企业审核 | 普通用户难以接入 | 提供"自建应用"引导（填 App ID/Secret）作为主路径 |
| R13 | 企业微信日程接口读不到用户自己的日程（已确认） | 企业微信只能走 CalDAV | `cal.wecom` 改为 CalDAV 客户端；需用户手动复制同步密码，企业可能关闭同步 |
| R14 | Google `calendar.readonly` 为敏感作用域，公开发布需通过应用验证 | 未验证前用户数受限 | 先支持用户填自己的 OAuth 客户端 ID；正式发布前提交验证 |
| R15 | 直接粘贴时焦点还原失败或按键被拦截（管理员进程、macOS 未授权、Wayland 原生窗口） | 粘贴到错误位置或无反应 | 岛不抢焦点 + 检测失败条件 + 降级为"已复制，请手动粘贴" |
| R8 | 插件线程过多 | 资源 | 超阈值切共享调度；空闲插件引擎可休眠（序列化状态由插件自行负责） |
| R9 | 常驻置顶窗口影响游戏/全屏性能 | 用户反感 | 全屏自动隐藏 + 空闲零动画 + 性能面板可见 |
| R10 | TKCode.Arc / Jaunt 为 1.0 早期版本 | API 变动 | 锁定精确版本；VNode 映射层隔离组件 API，升级只改映射 |
| R11 | Claude Code / Codex 本地登录配置格式或位置变化 | 自动发现失效 | 来源登记表由宿主维护、可热更新；读取失败降级为"请手动指定配置目录" |
| R16 | macOS 上岛贴在屏幕最顶端（y=0，贴顶放置）时，启动后偶发整块不绘制（实测贴顶 3/8 次、刘海包裹 0/10 次） | 启动后看不到岛 | 已加"从 Idle 长出"的启动动画缓解；根因待查（疑似 Avalonia macOS 透明窗口首帧呈现）。复现：反复启动后对岛所在行取像素 |
| R17 | JIT（Debug）模式启动慢：`UseArcSpecial()` 约占 4 秒，Jaunt 冷启动约 3.5 秒 | 仅影响开发调试 | NativeAOT 下整体 0.5 秒；发布一律 AOT。若需缩短开发循环，可考虑 ReadyToRun |
| R18 | `TKCode.Arc.Blocks` 的 `Sidebar` 在 NativeAOT 下有 3 条裁剪警告（其 `ItemsSource` + `LabelMember` 数据绑定模式使用反射绑定） | 用该模式时可能失效 | 只用 XAML 声明 `SidebarItem`，不用数据绑定模式；已用 AOT 版本离屏渲染验证侧边栏正常。可向 Arc 反馈 |
| R12 | 误刷新 OAuth token 导致 CLI 被登出 | 用户的 CLI 被登出 | `localAuth` 只读，宿主层面禁止对这些来源的 token 调用刷新端点 |

## 3. 已定决策

| 决策 | 结论 |
| --- | --- |
| Linux 范围 | X11 + XWayland，不做原生 Wayland |
| AI 余额 | 聚合插件 + 每个提供商一个独立插件；第三方客户端一律以插件实现 |
| Claude / Codex | 解析本机已登录配置自动添加账户（`localAuth`，只读、不刷新） |
| 第三方客户端（Grok Bot、ZCode、WorkBuddy） | v1 不实现，只保留插件协议 |
| macOS 最低版本 | 14 Sonoma |
| Claude 用量来源 | 只用 Claude Code 文档中的状态栏数据（O_O 自动接入、可还原）；不读取 Claude Code 令牌、不调用非公开接口 |
| Claude 用量来源 | 只用 Claude Code 文档中的状态栏数据（O_O 自动接入、可还原）；不读取 Claude Code 令牌、不调用非公开接口 |
| 产品名 / 插件包后缀 | `O_O` / `.island` |
| 插件市场 | v1 不做，只支持本地安装 |
| 剪贴板直接粘贴 | v1 实现（需系统权限的平台做引导与降级） |
| 日程平台 | 飞书、钉钉、企业微信、Google Calendar，聚合插件 + 平台插件 |
| 默认视觉风格 | 所有窗口默认苹果风格（见 README §6），三平台一致 |

## 4. 待决问题

暂无。

## 5. M0 进度（2026-11-09）

| 验证点 | 状态 |
| --- | --- |
| ① 透明置顶窗口 + 点击穿透 | macOS 已在真机验证（窗口层级 27 位于菜单栏之上、全空间可见、指针采样切换 `ignoresMouseEvents`）；Windows（窗口区域 `SetWindowRgn`）与 Linux（XShape 输入区域）已实现、可编译，**未在真机运行** |
| ② 刘海检测与包裹布局 | **已在真实刘海屏上验证**（MacBook 内建 Liquid Retina，1470×956pt）：检测到刘海 179×32pt、x=646；Idle 恰好覆盖刘海（窗口 x 355 + 表面 x 291 = 646），Compact 两侧各延伸 86pt；导出窗口像素核对：岛内不透明黑、岛外与肩部外侧完全透明。屏幕截图仍不可用，未做目视检查 |
| ③ Jaunt + VNode 渲染 | **完成**：每插件一个长驻 Jaunt 引擎（Hardened）跑在独立线程；宿主实现事件循环、定时器、`console`、`@island/api`（`island.activity` + `ui`）；VNode 经 JSON 传给 UI，按 key 增量更新到 Arc 控件；回调预算、出错重启（退避 3 次后禁用）、导入沙箱均有测试（12 项）。时钟已改为 JS 插件 |
| ④ NativeAOT 三平台发布 | macOS arm64 **完成**：0 个裁剪 / AOT 警告；可执行文件 53MB + 原生库约 20MB；热启动 0.54s 显示岛，插件激活 15ms，内存约 122MB。Windows / Linux 未发布 |
| ⑤ Claude Code / Codex 登录配置核实 | macOS **完成**，见 05 §10.3：Codex 与设计一致；Claude Code 钥匙串服务名带配置目录哈希后缀，已修正设计。Windows / Linux 未核实 |
| ⑥ 钉钉 / 企业微信日程权限核实 | **完成**（文档调研，见 05 §6.2）：Google 可行（敏感作用域需验证）；钉钉可行（个人应用 + `Calendar.Event.Read`，桌面 OAuth 待确认）；企业微信 API 不可行，改用 CalDAV |

## 6. 下一步

1. 在刘海 MacBook、Windows、Linux（X11 与 XWayland）上实机跑 ①②，发布 Windows / Linux 的 NativeAOT 版本，并核实两平台的 Claude Code 凭据文件。
2. M2 余下部分：权限授予界面（首次安装确认、逐项撤销）、`settings` / `notifications` / `oauth` / `localAuth` 能力。
3. M1：双活动气泡、右键菜单、全局热键。
