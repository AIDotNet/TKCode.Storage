# 03 · 插件系统

## 1. 目标

- **一切皆插件**：音乐、时钟、主题都与第三方插件走同一 API，第一方插件不享有私有后门（例外：`storage.sql` 等能力仅对签名为第一方的插件开放）。
- **极致可定制**：插件可贡献 *活动视图、展开页、命令、热键、设置页、主题、拖放处理、托盘菜单项*。
- **安全**：最小权限、域名白名单、执行预算、崩溃隔离。
- **NativeAOT 兼容**：不加载任意 .NET 程序集；JS 由 Jaunt 执行，原生扩展走 Sidecar 子进程。

## 2. 插件三种形态

| 形态 | 内容 | 适用 |
| --- | --- | --- |
| **JS 插件**（主力） | `plugin.json` + ES Module | 绝大多数功能 |
| **主题包** | `plugin.json` + `theme.json`（纯数据，无代码） | 主题切换 |
| **Sidecar 插件** | JS 插件 + 一个可执行文件，经 stdio JSON-RPC 通讯 | 需要原生 API 而宿主未提供的能力（如某专有硬件、私有 SDK） |

## 3. 插件包结构与清单

```text
com.example.weather/
├─ plugin.json
├─ dist/main.js           # ES Module 入口（可由 TS 编译，宿主不做转译）
├─ assets/icon.png
├─ i18n/zh-CN.json  en.json
└─ signature.sig          # 可选：发布者签名
```

```jsonc
// plugin.json
{
  "id": "com.example.weather",
  "name": "%name%",                       // i18n 键
  "version": "1.2.0",
  "publisher": "example",
  "engines": { "island": "^1.0.0" },     // 宿主 API 版本范围
  "main": "dist/main.js",
  "activationEvents": ["onStartup"],     // 或 onCommand:xxx / onDrop / onSchedule:*/15m
  "permissions": [
    "network:api.open-meteo.com",
    "location.coarse",
    "notifications"
  ],
  "contributes": {
    "activities": [{ "id": "now", "title": "%activity.now%" }],
    "panels":     [{ "id": "forecast", "title": "%panel.forecast%", "icon": "CloudSun" }],
    "commands":   [{ "id": "refresh", "title": "%cmd.refresh%", "hotkey": null }],
    "settings":   { "$ref": "settings.schema.json" },   // JSON Schema → 控制台自动生成表单
    "drop":       null,
    "trayItems":  []
  },
  "dependencies": { "com.o_o.http-utils": "^1.0.0" }
}
```

### 3.1 贡献点（Contribution Points）

| 贡献点 | 渲染位置 | 说明 |
| --- | --- | --- |
| `activities` | 岛 Compact（leading / trailing / center）+ Expanded | 动态"实时活动" |
| `panels` | Expanded 顶部 `SegmentedControl` 的一个标签页 | 常驻功能页（待办、剪贴板历史…） |
| `commands` | 全局命令面板、右键菜单、热键 | `ShortcutRecorder` 在控制台里绑定 |
| `settings` | 控制台 → 插件 → 设置 | JSON Schema 自动生成表单（见 04 §4） |
| `settingsView` | 控制台自定义页 | 自动表单不够用时，插件可提供自定义 VNode 视图 |
| `drop` | DropTarget 状态 | 声明接收 `files` / `text` / `url` / `image` |
| `themes` | 控制台 → 外观 | 主题包专用 |
| `trayItems` | 系统托盘菜单 | — |
| `aiUsageProviders` | AI 用量聚合插件 | 提供商插件声明自己可被聚合插件调用（见 05 §10.2） |
| `providers`（通用） | 任意声明了消费方的插件 | 插件间协议的通用形式：提供方声明接口 ID，消费方经 `island.providers.call` 调用，执行在提供方引擎内，只传数据 |

## 4. 生命周期

```text
发现 → 校验(签名/清单/引擎版本) → 授权(首次安装弹权限确认) → 加载(编译模块)
  → activate(ctx) → [运行：事件/定时/渲染] → deactivate() → 卸载
                         │ 异常/超预算
                         ▼
                   Faulted → 指数退避重启(1s,5s,30s) → 3 次失败后禁用并在控制台告警
```

```js
// dist/main.js
import { island, ui, storage, http, settings } from '@island/api';

export async function activate(ctx) {
  const act = island.activity.create('now', { priority: 'passive' });
  async function refresh() {
    const res = await http.getJson('https://api.open-meteo.com/v1/forecast?...');
    act.update(render(res));
  }
  ctx.subscriptions.push(island.schedule.every('15m', refresh));
  ctx.subscriptions.push(settings.onChange(refresh));
  await refresh();
}

export function deactivate() {}

function render(w) {
  return {
    compact: {
      leading:  ui.icon('CloudSun'),
      trailing: ui.text(`${w.temp}°`, { variant: 'mono' })
    },
    expanded: ui.stack({ gap: 'md' }, [
      ui.metricCard({ label: '当前温度', value: `${w.temp}°C`, delta: w.delta }),
      ui.sparkline({ values: w.hourly })
    ])
  };
}
```

## 5. Jaunt 集成

### 5.1 每插件一个长驻引擎

Jaunt 的 `JsPlugin.Create(...)` 是"每次调用一个全新 Realm"的无状态模型，适合纯函数；而岛插件需要**常驻状态**（定时器、订阅、缓存），所以：

- **有状态插件**：每插件一个 `JsEngine`（独立 Realm），`JsCompiler.CompileModule` 编译后 `LoadModuleAsync(module, resolver)` 加载，取导出 `activate` / `deactivate`。
- **无状态转换**（可选优化）：如 AI 余额插件里的"响应解析器"，可用 `JsPlugin.Create` 以 FreshRealm 方式并发执行。
- 编译产物 `JsScript`/`JsModule` 是不可变字节码，可缓存到磁盘（按文件哈希）加速冷启动。

### 5.2 事件循环（宿主实现）

Jaunt 裸引擎**没有** `console`、定时器、网络、文件访问——全部由宿主显式注入，这正好是权限控制的切入点：

```text
PluginRunner (每插件一个工作线程)
 loop:
   item = queue.Take()           // 定时器到期 / UI 事件 / 能力回调 / 设置变更
   engine.BeginExecution(limits, ct)
     engine.Invoke(callback, args)  // 或 InvokeAsync，Promise 微任务由 Jaunt job queue 推进
   Complete()
   flush pending VNode patches → UI 线程(按帧合并)
```

- 宿主注入：`setTimeout/setInterval/clearTimeout`（由 `PluginRunner` 的计时器堆实现）、`console`、`queueMicrotask`、`TextEncoder/Decoder`、`URL`，以及 `@island/api` 虚拟模块。
- 异步能力（http、storage、secrets…）用 `SetAsyncFunction` 暴露，返回 Promise。
- DTO 映射使用 Jaunt 源生成器：`[JsSerializable(typeof(HttpResponseDto))] partial class IslandJsContext : JsBindingContext`——零反射，AOT 安全。

### 5.3 预算与隔离

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `JsProfile` | `Hardened` | 冻结内建对象，禁用 `eval` / `Function` 构造 |
| 单次回调 `Timeout` | 50ms（渲染）/ 2s（activate） | 超时 → `JsLimitExceededException` → 引擎 Faulted → 重启 |
| `MaxInstructions` | 按回调类型配置 | 防死循环 |
| `MaxStackDepth` | 256 | — |
| `MaxTrackedBytes` | 64MB / 插件 | 防内存泄漏 |
| `MaxOutputBytes` | 1MB | 限制单次渲染输出 |
| 取消 | `CancellationToken` | 禁用/卸载插件时立即中止 |

- `JsException`（JS 主动 throw）不会让引擎失效，只记录日志；`JsLimitExceededException` / `JsEngineFaultedException` 则丢弃整个引擎重建。
- 控制台"开发者"页显示每个插件的 CPU 时间、内存、重启次数。

### 5.4 模块解析

`IJsModuleResolver` 实现规则：

- `@island/api` → 宿主虚拟模块（按插件已授权的权限**裁剪导出**，未授权的能力根本不存在于对象上）。
- 相对路径 `./x.js` → 仅限插件目录内，禁止 `..` 越界。
- `import data from './d.json' with { type: 'json' }` → JSON 模块（Jaunt 支持 import attributes）。
- 不支持裸包名（插件需自行打包依赖，推荐 esbuild/rolldown 输出单文件 ESM）。

## 6. 声明式 UI：VNode → Arc 控件

插件不接触 Avalonia 对象，而是返回一棵 VNode 树：

```ts
type VNode = { t: string; p?: Record<string, unknown>; c?: VNode[]; k?: string };
```

宿主 `VNodeRenderer`：

1. **白名单映射**：`t` 只能是下表之一，`p` 只允许表中列出的属性（与 Arc API 参考一一对应，实现时以 `.api.md` 为准）。
2. **Keyed diff**：与上次树比较，只对变化节点更新属性，避免重建控件（保持动画连续）。
3. **事件**：`p.onClick` 等是函数，宿主把它注册为 `handlerId`，控件触发时投递回插件线程。
4. **Token 化样式**：插件只能用语义值（`tone: 'accent' | 'muted' | 'danger'`，`gap: 'xs'..'xl'`），不能传颜色/像素，保证主题一致。

| VNode 类型 | 映射到 Arc 控件 | 典型用途 |
| --- | --- | --- |
| `stack` / `row` / `spacer` | Avalonia `StackPanel` / `Grid`（布局容器，非视觉组件） | 布局 |
| `text` | `TextBlock` + Arc 字体 Token；`morph:true` → `TextMorph`；`shimmer:true` → `TextShimmer` | 文本 |
| `counter` | `AnimatedCounter` | 数字滚动 |
| `icon` | `lucide:LucideIcon` / `ArcIcons` | 图标 |
| `image` | `Image`（仅插件目录或已授权域名的图片） | 封面 |
| `avatar` | `Avatar` | 头像 |
| `button` | `arc:Button`（weight: primary/secondary/ghost/danger） | 操作 |
| `iconToggle` | `IconMorph` | 播放/暂停 |
| `copy` | `CopyButton` | 复制 |
| `switch` / `checkbox` | `arc:Switch` / `arc:Checkbox` | 开关、待办勾选 |
| `slider` | `arc:Slider` | 进度/音量 |
| `segmented` | `SegmentedControl` | 子视图切换 |
| `input` / `search` | `Input` / `SearchField` | 输入/过滤 |
| `progress` | `Progress` | 进度 |
| `gauge` | `Gauge` | 专注倒计时环 |
| `usage` | `UsageMeter` | AI 额度 |
| `sparkline` / `bar` | `Sparkline` / `BarChart` | 趋势 |
| `metric` | `MetricCard` | 数值卡 |
| `badge` | `Badge` | 状态 |
| `list` | `ScrollArea` + 虚拟化 ItemsControl；`reorderable:true` → `ReorderableList`；`swipe` → `SwipeActions` | 列表 |
| `nowPlaying` | `NowPlaying` | 音乐 |
| `countdown` | `Countdown` | 会议倒计时 |
| `timeline` | `Timeline` | 日程 |
| `markdown` | `Markdown` | AI 回复、邮件摘要 |
| `empty` | `EmptyState` | 空状态 |
| `skeleton` | `Skeleton` | 加载中 |
| `alert` | `Alert` | 内联提示 |
| `menu` | `DropdownMenu` | 更多操作 |
| `fileDrop` | `FileDropzone` | 文件暂存 |

> 新增映射 = 改宿主 + 升 `@island/api` 次版本号；插件通过 `engines.island` 声明所需版本。

### 6.1 为什么不允许插件加载 XAML？

`AvaloniaRuntimeXamlLoader` 依赖反射，与 NativeAOT 冲突；且 XAML 可以实例化任意类型，等同任意代码执行，破坏权限模型。

## 7. `@island/api` 能力清单与权限

| 模块 | 能力 | 权限字符串 |
| --- | --- | --- |
| `island` | activity / panel / alert / schedule / commands / onDrop | 无需（基础） |
| `ui` | VNode 构造函数 | 无需 |
| `settings` | 读写本插件设置、监听变更 | 无需 |
| `storage` | 本插件 KV | 无需（配额限制） |
| `storage.sql` | 受限 SQLite（本插件私有库） | `storage.sql`（仅第一方） |
| `secrets` | 系统钥匙串读写（作用域为本插件） | `secrets` |
| `http` | fetch 风格 API，自动注入 UA，响应体上限 | `network:<域名>`，支持 `*.feishu.cn` 通配 |
| `oauth` | 启动系统浏览器 + 本地回环回调，PKCE | `oauth:<provider>` |
| `media` | 当前播放会话、控制、封面 | `media` |
| `clipboard` | 读/写/监听/直接粘贴到前一个应用 | `clipboard.read` / `clipboard.write` / `clipboard.watch` / `clipboard.paste`（需系统辅助功能权限，见 05 §8） |
| `files` | 暂存目录、拖出、在文件管理器中显示 | `files.shelf` |
| `notifications` | 系统通知 | `notifications` |
| `system` | 电池、勿扰、空闲时间、前台应用名 | `system.info` |
| `mail` | 只读 IMAP：未读数、最近邮件（宿主自带精简 IMAP 客户端；授权码账户或 OAuth 账户经 XOAUTH2，见 05 §9） | `mail`（OAuth 账户另需 `oauth:google` / `oauth:microsoft`） |
| `shell` | 打开 URL / 打开应用 | `shell.openUrl`（打开前提示一次） |
| `sidecar` | 启动清单声明的 Sidecar 进程并 RPC | `sidecar` |
| `localAuth` | 只读读取宿主登记的本机客户端登录配置（如 Claude Code、Codex CLI），返回不透明 `authHandle`，token 只在发往登记域名时由宿主注入 | `localAuth:<来源ID>`，每个来源首次使用需用户确认 |
| `island.providers` | 调用其它插件贡献的 provider 接口 | 无需（由提供方的贡献点声明开放） |

权限 UI：首次安装或升级新增权限时，控制台弹 `Dialog` 列出权限及用途说明（Manifest 中 `permissionReasons` 字段）；用户可在插件详情中逐项撤销，撤销后对应 API 立即从 `@island/api` 中消失（引擎重建）。

### 7.1 插件间调用

- 消费方：`await island.providers.call('deepseek', 'fetch', account)`。
- 宿主把调用投递到**提供方插件的事件循环**中执行，有独立预算；只返回经 schema 校验的数据。
- 提供方的权限和凭据不会传给消费方；任一方被禁用时调用返回 `unavailable`。

## 8. Sidecar（原生扩展通道）

- Manifest：`"sidecar": { "win-x64": "bin/helper.exe", "osx-arm64": "bin/helper", ... }`。
- 宿主以低权限启动子进程，stdio 上跑 JSON-RPC 2.0；JS 侧 `const h = await sidecar.connect(); await h.call('method', args)`。
- 必须签名（第一方信任根或用户手动信任）；进程随插件停用而终止；资源占用显示在控制台。

## 9. 主题包

```jsonc
// theme.json
{
  "id": "aurora",
  "base": { "appearance": "Dark", "accent": "Violet" },  // 对应 ArcAppearance / ArcAccentPreset
  "tokens": {                                            // 只能覆盖 Arc 已有 Token + 岛自定义 Token
    "IslandRadiusCompact": 18,
    "IslandRadiusExpanded": 28,
    "IslandMaterial": "Acrylic",                          // Solid / Acrylic / Mica（平台不支持时回落 Solid）
    "IslandSpring": { "damping": 0.8, "response": 0.4 }
  },
  "wallpaperAware": true                                 // 从壁纸取主色，映射到最近的 Accent
}
```

切换主题 = `ArcTheme.Apply(t => t.Appearance(...).Accent(...))` + 岛自定义 Token 资源字典替换，无需重启。刘海屏上 `IslandNotchSurface` 始终为黑，主题不可覆盖。

## 10. 分发与开发体验

- **包格式**：`.island`（zip），可选 Ed25519 发布者签名。
- **来源**：v1 只支持本地安装（拖 `.island` 到岛上，或在控制台选择文件），不做插件市场。插件更新也由用户手动安装新版本包；包格式和签名已为以后的市场预留。
- **开发模式**：控制台 → 开发者 → "加载未打包插件"指向目录，文件变动自动热重载；日志、VNode 树（`JsonViewer`）、性能指标实时可见。
- **脚手架**：`npm create island-plugin@latest` 生成 TS 模板 + `@island/api` 类型 + esbuild 配置。
- **版本兼容**：`@island/api` 遵循 SemVer；宿主同时支持当前与上一个主版本。

## 11. 实现状态（M0）

已实现：`src/Island.Plugins.Host`（运行器、模块解析、注入的 `Runtime/prelude.js` 与 `Runtime/api.js`）、`src/Island.Plugins.Abstractions`（清单、VNode 与其 JSON 解析）、`src/Island.UI/Plugins`（`VNodeRenderer`、`ActivityPresenter`）。

- `@island/api` 目前导出 `island.activity.create(id, { title, priority })` → `update(view)` / `end()`，以及 `ui.stack / row / spacer / text / icon / button / toggle / progress / badge / mascot`。
- 线上格式：`{ t, k?, p, c }`，函数属性在 JS 侧替换为 `{ "$h": id }`；每次渲染重新登记处理函数，旧 id 自动失效。
- `mascot` 映射到 Arc `AiMascot`（状态 idle / listening / thinking / writing / done / error），岛与应用图标都用它。
- **权限（M2）**：清单权限严格解析，未知权限或过宽的通配（如 `network:*.com`）会让插件在发现阶段被拒绝。
  - 内置插件（随程序发布的 `plugins/`）按清单授予。
  - 用户安装的插件在用户决定之前处于"等待授权"、不会运行；用户可全部允许、全部拒绝或逐项开关，决定保存在 `<数据目录>/grants.json`，每次变更都会让插件以新的权限集重启。
  - 控制台"插件"页提供授权提示与逐项开关。
- **原生函数私有化**：prelude 把 `__island_*` 收进闭包并从 `globalThis` 删除；宿主在任何插件代码运行前先加载 `@island/api`，由它一次性取走原生函数。插件（包括比 `@island/api` 先被导入的模块）都拿不到原生函数，有测试覆盖。
- **已实现能力**：
  - `storage`：无需权限；每个插件一个 JSON 文件，进程重启和引擎重启后都保留，默认配额 5MB。设计中的 SQLite 存储在插件需要查询时再替换。
  - `http.fetch` / `http.getJson`：需要 `network:` 权限。只允许 https（本机回环地址需 `network:localhost`）；重定向逐跳检查白名单；响应最大 5MB、超时最长 60 秒；禁止设置 `Host` 等协议头。
  - `secrets.get / set / delete`：需要 `secrets` 权限；存入系统凭据库（macOS 钥匙串、Windows 凭据管理器、Linux `secret-tool`），服务名 `O_O/<插件 id>`。
  - 异步能力调用不会阻塞插件的事件循环：宿主在线程池执行，结果经插件队列以 `settle` 派发回来，兑现或拒绝对应的 Promise；单个插件最多 16 个并发调用。
- **`settings`**：清单 `contributes.settings` 用 JSON Schema 子集（`string` / `number` / `integer` / `boolean`，`enum`、`minimum`、`maximum`、`default`、`format`、`x-multiline`）描述，非法的 schema 会让插件被拒绝。控制台据此自动生成表单；值由宿主校验后保存，`format: password` 的值存入系统凭据库、不写配置文件。插件 API：`settings.get / all / getSecret / onChange`。
- 示例第三方插件：`samples/plugins/com.example.weather`（需网络授权、带设置表单、用存储缓存）。
- **`localAuth`**（复用本机客户端的登录，只读）：已登记来源 `claude-code`、`codex-cli`，其他来源名会让清单被拒绝。
  - `localAuth.accounts(source)` 只返回账户信息（邮箱、套餐等）和一个不透明句柄，**插件永远拿不到令牌**。
  - 把账户作为 `auth` 传给 `http.fetch` 时，宿主只在目标主机属于该来源（`chatgpt.com` / `api.anthropic.com`）时附上令牌，**每一跳重定向都重新检查**。
  - 令牌过期只报告、不刷新（刷新会让客户端自身掉线）。
  - `localAuth:*` **永远需要用户确认**，内置插件也不例外；在 macOS 上首次读取 Claude Code 的钥匙串条目会出现系统授权框。
- **插件间调用（§7.1）**：提供方在清单 `contributes.providers` 中声明 `{ interface, id, title }`，并用 `island.provide(iface, id, impl)` 注册实现；调用方用 `island.providers.list / call`。调用在提供方自己的引擎中执行，使用提供方自己的权限，只回传 JSON 数据；提供方出错、未运行、超时（60 秒）都会以错误返回调用方，提供方重启时未完成的调用会失败。
- `localAuth.statusLine(account)`（仅 Claude Code）：返回状态栏桥接拿到的 `{ connected, updatedAt, fiveHour, sevenDay }`，不涉及任何令牌。Claude Code 来源**不再提供凭据**（`ReadCredential` 恒为 null），因此带 `auth` 的请求无法为 Claude 附加令牌。
- `localAuth.statusLine(account)`（仅 Claude Code）：返回状态栏桥接拿到的 `{ connected, updatedAt, fiveHour, sevenDay }`，不涉及任何令牌。Claude Code 来源**不再提供凭据**（`ReadCredential` 恒为 null），因此带 `auth` 的请求无法为 Claude 附加令牌。
- 视图新增 `ui.gauge`（Arc `Gauge`，270° 圆环：value、max、label、detail、warnAt、dangerAt、size）；`localAuth.accounts(source, { configDirs })` 可传入额外的配置目录（宿主校验：必须是绝对路径且包含 `.claude.json`，最多 8 个）。宿主返回给插件的错误信息为中文。
- 活动可用 `activity.setPriority()` 调整抢占强度；`island.providers.onChange(fn)` 在提供方插件启动、停止或获授权时通知，聚合插件据此即时刷新而不必轮询。
- 尚未实现：`notifications` / `oauth` 等能力、Sidecar、包签名。
