# 01 · 架构

## 1. 分层

```text
┌──────────────────────────────────────────────────────────────────────┐
│  插件层 (JS, Jaunt)                                                    │
│  media · clock · todo · focus · calendar+cal.* · shelf · clipboard ·  │
│  ai-usage(聚合) · ai.claude/codex/deepseek/... · theme-* · 第三方插件    │
├───────────────────────────── @island/api ────────────────────────────┤
│  插件宿主 Island.Plugins.Host                                          │
│  Manifest/权限 · 模块解析 · 每插件 JsEngine + 事件循环 · 预算/熔断        │
│  VNode Diff → Arc 控件映射 · Capability 绑定 · Sidecar(JSON-RPC)        │
├──────────────────────────────────────────────────────────────────────┤
│  核心 Island.Core                                                      │
│  岛状态机 · 槽位注册表 · Activity 调度(优先级队列) · 设置 · 事件总线       │
│  存储(SQLite/KV) · 凭据 · 热键 · 日志                                    │
├──────────────────────────────────────────────────────────────────────┤
│  UI Island.UI (Avalonia 12.1.4 + TKCode.Arc 1.0.0)                     │
│  IslandWindow / IslandSurface · ConsoleWindow(ArcWindow) · 托盘          │
├──────────────────────────────────────────────────────────────────────┤
│  平台层 Island.Platform.{Windows,MacOS,Linux} : IPlatformServices      │
│  窗口层级/穿透 · 屏幕安全区(刘海) · 全屏检测 · 媒体会话 · 剪贴板监听      │
│  文件拖放 · 凭据库 · 开机自启 · 勿扰状态                                 │
└──────────────────────────────────────────────────────────────────────┘
```

依赖方向严格自上而下；`Island.Core` 不引用 Avalonia，便于单测。

## 2. 解决方案结构

```text
O_O.sln
├─ src/
│  ├─ Island.App/                    # 入口 Program.cs、UseArc、DI 组装、托盘、单实例
│  ├─ Island.Core/                   # 状态机、调度、设置、存储、事件总线（无 UI 依赖）
│  ├─ Island.UI/                     # 岛视图、VNode 渲染器、控制台页面
│  ├─ Island.Platform.Abstractions/  # IPlatformServices 及各能力接口
│  ├─ Island.Platform.Windows/
│  ├─ Island.Platform.MacOS/
│  ├─ Island.Platform.Linux/
│  ├─ Island.Plugins.Host/           # Jaunt 集成、Manifest、权限、Capability 桥
│  └─ Island.Plugins.Abstractions/   # Capability 接口、DTO（[JsSerializable] 绑定上下文）
├─ sdk/
│  ├─ island-api/                    # @island/api 的 .d.ts + 文档（npm 包，仅类型）
│  └─ create-island-plugin/          # 脚手架 CLI
├─ plugins/                          # 第一方插件（与第三方完全同构）
│  ├─ com.o_o.clock/ media/ todo/ focus/ calendar/
│  ├─ cal.feishu/ cal.dingtalk/ cal.wecom/ cal.google/
│  ├─ file-shelf/ clipboard/ mail/ ai-usage/
│  ├─ ai.claude/ ai.codex/ ai.deepseek/ ai.kimi/ ai.newapi/ ai.manual/
│  └─ theme.default/ theme.aurora/ ...
└─ tests/
```

平台工程按 RID 条件引用（`win-*` / `osx-*` / `linux-*`），NativeAOT 每平台单独发布，避免把别的平台互操作代码带进产物。

## 3. 进程与线程模型

| 线程 | 职责 |
| --- | --- |
| UI 线程 (Avalonia Dispatcher) | 岛与控制台渲染、动画、命中区域更新 |
| 插件线程 × N | 每个插件一个长驻 `JsEngine`，独占一个工作线程跑其事件循环（Jaunt 的执行是单线程的；执行期间其它线程的读 API 会被拒绝） |
| 能力 I/O | `Task` 线程池：HTTP、IMAP、文件、SQLite |
| 平台监听 | 剪贴板/媒体会话等原生回调，统一投递到事件总线 |

- 插件 → UI：渲染结果（VNode patch）先在插件线程算好，按帧合并后 `Dispatcher.UIThread.Post` 一次性应用。
- UI → 插件：用户事件序列化为 `{handlerId, payload}` 投递到插件事件循环队列。
- 插件数 > 阈值（默认 12）时切换为共享线程池 + 每引擎互斥调度，避免线程过多。

**为什么不是多进程？** 首版单进程更简单、内存更低；Jaunt 自身提供预算、取消与故障隔离（引擎 faulted 后可丢弃重建），足以防止单插件拖垮宿主。真正需要原生代码的第三方能力走 **Sidecar 子进程**（见 03 §8）。

## 4. 启动流程

1. 单实例检查（命名互斥体 / Unix socket）；已有实例则把参数（如 `--console`）转发后退出。
2. `AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithArcFonts().UseArc(arc => arc.UseArcControls().UseArcBlocks().UseArcSpecial().WithTheme(...))`。
3. 读取设置 → 应用主题（`ArcTheme.Apply`）。
4. 创建 `IslandWindow`（先隐藏）→ 平台层设置窗口层级/穿透 → 计算屏幕几何（含刘海）。
5. 扫描插件目录 → 校验签名与 Manifest → 按依赖拓扑顺序 `activate`（并行，单个超时 2s 不阻塞启动）。
6. 显示岛（Idle 态淡入）。目标：冷启动到可见 < 400ms。

## 5. 数据与存储

| 数据 | 位置 | 格式 |
| --- | --- | --- |
| 全局设置 | `<AppData>/O_O/settings.json` | JSON，带 `schemaVersion` 迁移 |
| 插件设置 | `<AppData>/O_O/plugins/<id>/settings.json` | 由插件 Manifest 的 JSON Schema 校验 |
| 插件 KV | `<AppData>/O_O/data.db` 表 `kv(plugin_id, key, value, updated)` | SQLite，每插件配额（默认 20MB） |
| 剪贴板历史 / 待办 / 文件暂存索引 | 同一个 `data.db` 下各自表（第一方插件通过 `storage.sql` 能力，第三方仅 KV） | SQLite |
| 文件暂存实体 | `<Cache>/O_O/shelf/` | 原文件或副本 |
| 凭据 | 系统钥匙串，服务名 `O_O/<pluginId>` | — |

`<AppData>`：Windows `%APPDATA%`、macOS `~/Library/Application Support`、Linux `$XDG_CONFIG_HOME`。

## 6. 横切关注点

- **日志**：结构化日志按插件分流，控制台"开发者"页可实时查看；插件 `console.*` 由宿主注入（Jaunt 裸引擎没有 `console`）。
- **国际化**：壳与第一方插件 zh-CN / zh-TW / en；Arc 自带三语文档与本地化。插件 Manifest 支持 `i18n/*.json`。
- **可访问性**：岛的所有交互元素有键盘焦点顺序（全局热键聚焦岛 → Tab 切换）；尊重系统减少动态效果（`ArcMotion.Level`）。
- **更新**：壳自更新（检查 → 下载 → 下次启动替换）；插件独立更新。
- **遥测**：默认关闭。
