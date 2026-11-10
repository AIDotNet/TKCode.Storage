# 05 · 第一方插件设计

所有第一方插件位于 `plugins/`，与第三方插件同构（JS + `@island/api`），能力由宿主 Capability 提供。下表中"宿主能力"列是需要在 C# 平台层实现的部分。

## 总览

| 插件 ID | 形态 | Compact 显示 | Expanded / Panel | 宿主能力 |
| --- | --- | --- | --- | --- |
| `com.o_o.media` | 活动 | 封面 + 律动条 | `NowPlaying` | `media` |
| `com.o_o.clock` | 活动(ambient) | 时间 | 世界时钟 / 计时器 | — |
| `com.o_o.todo` | 面板 | 剩余数 | 待办列表 | `storage.sql` |
| `com.o_o.focus` | 活动 + 面板 | 倒计时 | 专注环 + 统计 | `system`（勿扰） |
| `com.o_o.calendar` | 活动 + 面板（聚合） | 下个会议倒计时 | 今日日程 | 跨插件调用 |
| `com.o_o.cal.*` | 日程平台插件（飞书/钉钉/企业微信/Google） | — | — | `oauth`、`network`、`secrets` |
| `com.o_o.shelf` | 面板 + drop | 文件数 | 暂存区 | `files.shelf` |
| `com.o_o.clipboard` | 面板 | — / 复制提示 | 历史列表 | `clipboard.*` |
| `com.o_o.mail` | 活动 + 面板 | 未读数 / 新邮件 | 收件摘要 | `mail`、`secrets` |
| `com.o_o.ai-usage` | 面板 + 活动（聚合） | 告警时显示 | 各账户余额/额度 | 跨插件调用 |
| `com.o_o.ai.*` | 提供商插件 | — | — | `network`、`secrets`、`localAuth` |
| `theme.*` | 主题包 | — | — | — |

---

## 1. 音乐播放 `com.o_o.media`

**宿主能力 `media`**（各平台差异最大的一个）：

| 平台 | 方案 | 备注 |
| --- | --- | --- |
| Windows | `GlobalSystemMediaTransportControlsSessionManager`（GSMTC，WinRT） | 覆盖 Spotify、网易云、QQ 音乐、浏览器等；可拿封面缩略图、进度、控制 |
| macOS | MediaRemote 私有框架 | **风险**：macOS 15.4 起对非 Apple 进程限制 MediaRemote 读取，需要适配方案（社区方案是借助系统自带可信二进制加载框架的 adapter，作为 Sidecar）；回落：AppleScript/ScriptingBridge 直接控制 Music、Spotify |
| Linux | MPRIS over D-Bus（`org.mpris.MediaPlayer2.*`） | 标准、稳定 |

统一模型：`{ app, title, artist, album, artwork(png bytes), duration, position, isPlaying, canSeek }` + 命令 `play/pause/next/prev/seek`。

**UI**

- Compact：leading = 封面（圆角 `image`），trailing = 音频律动条（宿主提供的轻量自绘可视化，仅播放时 30fps；无法拿到真实频谱时用伪随机律动）。
- Expanded：`NowPlaying`（迷你播放器展开为完整播放器的连续动画，正契合灵动岛）；`IconMorph` 播放/暂停；`Slider` 进度；来源应用图标。
- 切歌时 `Alert` 态短暂显示新歌名（`TextMorph`）。
- 设置：律动条样式、是否显示歌词（预留，需歌词源插件）、忽略的播放器（`TagInput`）。

> **实现状态（2026-11-10）**：宿主能力 `media`（`media.nowPlaying()` / `command('play'|'pause'|'toggle'|'next'|'previous')` / `onChange`；`IPluginMedia` + `PlatformMedia`，平台层 `IMediaPlatform`；只在有插件监听时轮询，进度单纯前进不算变化）。
> - macOS：Music 与 Spotify 每次变化时发出的分布式通知（`com.apple.Music.playerInfo`、`com.spotify.client.PlaybackStateChanged`，无需权限）；启动时若它们已在运行，用一次 AppleScript 读当前状态（首次会出现"自动化"授权提示；不会启动未运行的应用）；命令走 `MRMediaRemoteSendCommand`，任何接入系统"正在播放"的播放器都能收到。其他播放器（浏览器、网易云等）只能控制、看不到曲目。本机仅验证了启动与订阅不出错；通知、AppleScript 与命令未在真实播放中验证（避免改动用户的播放）。
> - Windows：仅媒体键（播放/暂停、上一首、下一首）；`nowPlaying` 为空。GSMTC 需要 CsWinRT，待评估 AOT 代价（R5）。未实测。
> - Linux：通过 `playerctl`（MPRIS）读取与控制，未安装则不可用。未实测。
> - 插件 `com.o_o.media`：页面"音乐"（曲名、艺人 · 专辑、进度条与时间、上一首 / 播放暂停 / 下一首），播放时左侧"正在播放"小项与 `active` 活动（点开岛直达音乐页），暂停即撤下。**未做**：封面、律动条、切歌提醒、忽略的播放器设置、拖动进度。

## 2. 时间 `com.o_o.clock`

- Idle/Compact：`HH:mm`（`TextMorph` 逐字变形），可选日期、农历、星期。
- Expanded：世界时钟列表（`list` + `text`）、秒表、倒计时（`Countdown`，结束触发 `critical` Alert）。
- 设置：12/24 小时、显示秒、附加时区（`MultiSelect`）。

## 3. 主题切换

- 主题本身是**主题包插件**（见 03 §9）；内置 `theme.default`（Arc 默认 + 系统跟随）、`theme.aurora`、`theme.mono`、`theme.glass`。
- 快速切换：岛右键菜单"外观"子菜单 + 命令面板命令；可按时间自动切换（日出/日落，用 `schedule`）。
- 壁纸取色：Windows `IDesktopWallpaper`、macOS `NSWorkspace.desktopImageURL`、Linux GNOME `org.gnome.desktop.background` → 主色映射到最近的 `ArcAccentPreset`。

> **实现状态（2026-11-10）**：主题包是插件清单里的 `contributes.themes`（`id`、`title`、`description`、`accent` #RRGGBB、`appearance` system/light/dark、`corners` 0.2–2），加载时校验；纯主题包带一个空的 `main.js`。内置 `com.o_o.theme.default`（苹果，默认启用）、`aurora`（极光，深色 · 紫罗兰）、`mono`（黑白）、`glass`（玻璃，浅色 · 湖蓝），后三个在"添加插件"里。选择主题 = 通过 Arc 设定外观（主题有指定时）、强调色和圆角比例；之后仍可单独调整外观和强调色，直到再选一次主题。控制台"外观"页：主题卡片（Arc RadioCards）、外观与强调色、按时间自动切换浅色 / 深色（半小时为单位；手动选择会关闭自动切换）、"从桌面壁纸取强调色"（每 10 分钟看一次；壁纸没有明显颜色时不变）。岛的 ⋯ / 右键菜单新增"外观"子菜单（跟随系统 / 浅色 / 深色 + 已安装的主题）。壁纸：macOS 用 `desktopImageURLForScreen:` 由 AppKit 解码并缩小（支持 HEIC 系统壁纸，本机实测取到 #377EB3）；Windows `SPI_GETDESKWALLPAPER`；Linux GNOME `gsettings`（后两者未实测）。设置存于 `theme.json`。**未做**：主题包覆盖岛自定义 Token / 材质 / 弹簧参数（03 §9 的 `tokens`）、字体、按日出日落切换、命令面板。

## 4. 待办清单 `com.o_o.todo`

- 数据：`todos(id, title, notes, due, priority, done, order, list_id, created, updated)`。
- Panel：顶部 `Input`（回车添加，支持自然语言 "明天 3 点 交周报" → 解析 due），列表 `ReorderableList` + `Checkbox`，左滑 `SwipeActions`（删除/延期）。
- Compact：有临期任务时显示 "3 项待办 · 1 项今天到期"；到期前 10 分钟 `passive` 活动。
- 后续：同步飞书任务（复用飞书插件的 OAuth）、与专注联动（"开始专注此任务"）。

> **实现状态（2026-11-10）**：插件 `com.o_o.todo`。岛内没有文字输入（在岛上打字会抢走正在使用的应用的键盘焦点，也与剪贴板"直接粘贴"冲突），所以添加有两条路：① 控制台插件设置里的多行"添加待办"框——每次保存时只把**新出现的行**加入（按行做多重集差，框可以保留也可以清空，删行不会删任务）；② 页面右上角"从剪贴板添加"（`clipboard.read`），复制的每一行成为一项（最多 20 行）。两者都识别简单的日期时间（今天 / 明天 / 后天 / 周X / 下周X / 11月12日 / 11/12 / 15:30 / 下午3点半），行首 `!` 表示重要。岛上：待办 / 已完成 文字标签、分页；点击行勾选、星标重要、删除、"专注此任务"（调用专注插件的 `focus` 提供者）；过期标红（强调色）；收起时"n 项待办"小项；有时间的待办到期前（默认 10 分钟，可设）以 `passive` 活动出现在收起的岛上，到点提醒一次并在之后 10 分钟升为 `active`（点开岛直接到待办页）；已完成默认保留 7 天。提供者 `todo`：`list` / `complete` / `addPomodoro`。**未做**：岛内输入框、自然语言更多写法、拖动排序、左滑操作、飞书任务同步、系统通知。

## 5. 专注 `com.o_o.focus`

- 番茄钟：工作 25 / 短休 5 / 长休 15（可配），可绑定待办任务。
- Compact：leading = 进度环（`gauge` 迷你态），trailing = 剩余 `mm:ss`（`counter`）。
- Expanded：大号 `Gauge` + 开始/暂停/跳过；今日统计 `BarChart`；一年专注 `ActivityHeatmap`。
- 专注期间：可选开启系统勿扰（Windows Focus Assist / macOS Focus 只能通过快捷指令触发 → 提供"运行快捷指令"选项 / Linux GNOME `show-banners=false`），岛只放行 `critical`。
- 结束：`Alert` + 系统通知 + 可选提示音。

> **实现状态（2026-11-10）**：插件 `com.o_o.focus`。番茄钟 专注 25 / 短休 5 / 长休 15，每 4 个番茄长休（均可设），可选"自动开始下一段"；页面显示阶段、大号剩余时间、进度条、开始 / 暂停 / 继续、跳过、重新开始这一段；可从待办中选择关联的任务（`todo` 提供者，选择器）。进行中：收起时左侧"剩余时间"小项、`active` 活动（点开岛直接到专注页）；一段结束时提醒；完成的番茄按天计数（保留 31 天）并记到关联的待办上（待办插件未运行时排队，之后补记）。状态持久化：O_O 关闭期间结束的阶段在启动时补完。提供者 `focus`：`start(task)` / `state`。**未做**：系统勿扰联动、统计图表（`BarChart` / `ActivityHeatmap`，岛上暂无对应节点）、提示音、系统通知、专注期间只放行 critical。

## 6. 日程：聚合插件 + 平台插件

和 AI 用量一样拆成两层：

```text
com.o_o.calendar             聚合插件：合并多来源日程、会前提醒活动、今日面板
   ▲ 贡献点 calendarProviders
   ├─ com.o_o.cal.feishu     飞书
   ├─ com.o_o.cal.dingtalk   钉钉
   ├─ com.o_o.cal.wecom      企业微信
   └─ com.o_o.cal.google     Google Calendar
```

### 6.1 提供商插件协议

```ts
export interface CalendarProvider {
  listCalendars(account): Promise<{ id: string; name: string; color?: string; primary?: boolean }[]>;
  listEvents(account, range: { from: string; to: string }): Promise<CalendarEvent[]>;
}
type CalendarEvent = {
  id: string; calendarId: string; title: string;
  start: string; end: string; allDay: boolean;       // ISO 8601，带时区
  location?: string; meetingUrl?: string;            // 飞书会议 / 钉钉会议 / 腾讯会议 / Google Meet
  attendees?: { name: string; avatar?: string; status?: 'accepted' | 'declined' | 'tentative' | 'none' }[];
  myStatus?: 'accepted' | 'declined' | 'tentative' | 'none';
};
```

- 认证统一走宿主 `oauth` 能力（授权码 + PKCE，系统浏览器 + 本地回环回调），token 存钥匙串并由各插件自行刷新（这些是我们自己的 OAuth 会话，与 `localAuth` 的只读规则不同）。
- 聚合插件负责：去重（同一会议同时出现在多个日历时按标题 + 时间 + 会议链接合并）、缓存、轮询节奏（5 分钟）、会前精确定时。

### 6.2 各平台插件

| 插件 | 认证 | 数据 | 实现前需核实 |
| --- | --- | --- | --- |
| `cal.feishu` | 飞书开放平台用户身份 OAuth，获取 `user_access_token` | 日历 v4：主日历及订阅日历的事件；会议链接取飞书视频会议 | 应用发布流程；主路径为引导用户创建企业自建应用（填 App ID / Secret） |
| `cal.dingtalk` | **第三方个人应用**的用户 token：授权码 → `POST /v1.0/oauth2/userAccessToken`（有效 7200 秒）→ `GET /v1.0/contact/users/me` 取 unionId；需申请个人权限 `Calendar.Event.Read` | `GET https://api.dingtalk.com/v1.0/calendar/users/{unionId}/calendars/primary/events`（`timeMin` / `timeMax` / `nextToken` / `syncToken`），请求头 `x-acs-dingtalk-access-token`；提醒场景更适合"日程视图"接口（展开重复日程） | 可行（有约束）。**未确认**：桌面端能否走浏览器回跳式 OAuth（文档主要描述 JS SDK）；无组织的个人用户能否使用；日程视图接口的确切路径 |
| `cal.wecom` | 企业微信 API 只有企业级 access_token（自建应用由管理员在"协作 → 日程 → 可调用接口的应用"中添加），没有按用户的 OAuth | — | **API 路线不可行**：日程接口"仅可获取应用自己创建的日历下的日程"，读不到用户在客户端里自己的日程。**改用 CalDAV**：用户在企业微信"日历设置 → 同步至其他日历"取得用户名与密码，服务器 `caldav.wecom.work`，插件作为 CalDAV 客户端读取。约束：密码需用户手动复制、可能被企业策略关闭 |
| `cal.google` | Google OAuth（桌面应用），作用域 **`calendar.events.readonly`**（比 `calendar.readonly` 窄） | `GET /calendar/v3/calendars/primary/events?timeMin=…&singleEvents=true&orderBy=startTime`，会议链接取 `hangoutLink` / `conferenceData` | 可行（有约束）：日历读取属于**敏感**（非受限）作用域，未验证应用显示警告页且项目终身最多 100 名新用户；验证约 10 个工作日，无需 CASA 安全评估。验证前可让用户填自己的 OAuth 客户端 ID |

> 以上为 M0 ⑥ 调研结论（2026-11-09）。资料来自官方文档的搜索摘录（调研时无法直接打开页面），标注"未确认"的项实现前需再核对。来源：open.dingtalk.com（query-an-event-list、obtain-user-token、权限点映射）、developer.work.weixin.qq.com/document/path/97723、open.work.weixin.qq.com/help2/pc/20223、developers.google.com/identity/protocols/oauth2/scopes、support.google.com/cloud/answer/7454865。

后续可按同一协议补充：Outlook（Microsoft Graph）、iCloud（CalDAV）、通用 ICS 订阅链接。

> **实现状态（2026-11-10，不含 OAuth 的部分）**：
> - `com.o_o.calendar` 聚合插件：调用所有 `calendar` 提供方，取今天和明天的日程，按"标题 + 开始时间 + 会议链接"合并重复，结果缓存在 storage（重启后先显示缓存），每 5 分钟和提供方 / 设置变化时刷新。开始前 N 分钟（默认 10）提醒一次，并出现 `page: 'today'` 的活动：开始前为 passive，开始后 5 分钟内为 active；收起时右侧小项显示 N 小时（默认 3）内的下一个日程（时间，进行中时显示标题）。定时器精确到下一个变化时刻（提醒、开始、结束、跨天），至少每分钟一次。"日程"页：今天 / 明天文字标签、分页、全天 / 时间段 / 进行中、会议与地点图标、各来源的错误。点击有会议链接的日程会**复制链接**（还没有"在浏览器中打开"能力）。
> - `com.o_o.cal.caldav`：通用 CalDAV（企业微信 caldav.wecom.work、iCloud、Nextcloud、飞书 CalDAV 等）。设置为服务器地址、用户名、密码（钥匙串），权限 `network:@serverUrl`。发现流程为 current-user-principal（含 `/.well-known/caldav`）→ calendar-home-set → 日历（跳过只含 VTODO 的列表和收件箱），然后用带时间范围的 calendar-query REPORT 取事件，Basic 认证。
> - `com.o_o.cal.ics`：最多 3 个 .ics 订阅链接，权限为 `network:@url1/2/3`，15 分钟内不重复下载。
> - 两个提供方共用 `ical.js`（插件之间不能互相 import，所以各带一份）。它处理折行、转义、UTC / 浮动时间、TZID（文件自带的 VTIMEZONE 含夏令时规则，或常见无夏令时时区表），以及 RRULE DAILY / WEEKLY / MONTHLY / YEARLY，含 INTERVAL、COUNT、UNTIL、BYDAY（含序数）、BYMONTHDAY、BYMONTH。EXDATE、RECURRENCE-ID 改期和 CANCELLED 也会处理；会议链接从描述、地点、URL 中识别（腾讯会议、飞书、钉钉、Meet、Zoom、Teams、Webex）。
> - **未做 / 限制**：
>   - 打开链接要等宿主的 `shell.openUrl` 能力。
>   - webcal:// 需用户改成 https://，因为 `network:@` 只接受 http(s)。
>   - 服务器把请求重定向到其他主机（如 iCloud 的 pXX-caldav.icloud.com）时会被拒绝，需用户把那个地址填进设置。
>   - 不认识且没有 VTIMEZONE 的时区按本机时间处理。
>   - 不支持 RRULE 的 BYSETPOS、BYWEEKNO、HOURLY 等。
>   - 不显示参会人。
>   - 飞书、钉钉、Google 的 OAuth 插件等宿主 `oauth` 能力就绪后再做。

> **实现状态（2026-11-10，OAuth 来源）**：三个插件都是可选内置插件，登录走宿主 `oauth`（系统浏览器 + 回环回调；令牌由宿主保存在钥匙串、自动刷新，只发给各自的 API 主机，插件只拿到账户句柄）。用户在插件设置里填自己应用的 ID / 密钥；"日程"页对还没有账户的来源显示"登录 …"一行（提供方的 `status()` / `signIn()`），点击后打开浏览器，并在 5 分钟内每 3 秒检查一次，登录完成后日程自动出现。
> - `com.o_o.cal.google`：作用域 `calendar.events.readonly`；`GET /calendar/v3/calendars/primary/events?timeMin&timeMax&singleEvents=true&orderBy=startTime`（展开重复日程、分页）；会议链接取 `hangoutLink` 或 `conferenceData.entryPoints` 中的 video；账户名取返回里主日历的 `summary`（邮箱），因此不需要额外作用域。只读主日历。
> - `com.o_o.cal.feishu`：企业自建应用（App ID / Secret），需在应用里登记重定向 URL `http://127.0.0.1:47821/oauth/callback`；作用域 `calendar:calendar:readonly offline_access`（无 offline_access 就拿不到 refresh_token）。`GET /open-apis/calendar/v4/calendars`（取 primary 和 shared，跳过只看忙闲和会议室），`GET …/calendars/{id}/events?start_time&end_time`（Unix 秒）；返回 `{ code, msg, data }`。重复日程按 `recurrence`（RRULE）用 `ical.js` 展开。账户名取 `GET /open-apis/authen/v1/user_info`。**未能核实**：`instance_view`（官方展开接口）的路径——当前文档里查不到，所以自行展开；例外实例（改期的单次日程）与系列可能重复显示。
> - `com.o_o.cal.dingtalk`：第三方个人应用 / 内部应用的用户令牌（宿主按钉钉格式换取，令牌放在 `x-acs-dingtalk-access-token` 头里）；`GET /v1.0/contact/users/me` 取 unionId，`GET /v1.0/calendar/users/{unionId}/calendars/primary/eventsview?timeMin&timeMax&maxResults&nextToken`（"日程视图"，由服务端展开重复日程；已对照官方文档核实）。**未核实**：桌面端走浏览器回跳式登录（login.dingtalk.com/oauth2/auth）对个人用户是否可用。
> - 修正：`ical.js`（三份）里重复日程的 UTC 起始时间（`DTSTART:…Z`）展开后被当成本机时间，导致在非 UTC 时区偏移；现在每次重复都保留 UTC。

### 6.3 UI

- Compact：会前 15 分钟起显示 `passive` 活动（来源平台图标 + 会议名 + `Countdown`），会前 1 分钟升级为 `critical`，带"加入会议"按钮（打开会议链接）。
- Panel：今日 `Timeline`，事件左侧色条区分来源日历；点击展开详情（参会人 `AvatarGroup`、地点、会议链接 `CopyButton`）。
- 设置：各账户要显示的日历（`MultiSelect`）、提醒提前量、忽略已拒绝的会议、合并重复会议 `Switch`。
- 控制台"账户与连接"页用 `ConnectorSetup` 列出四个平台的连接状态。

## 7. 文件临时暂存 `com.o_o.shelf`（"文件列"）

- 入口：把文件拖到岛上 → DropTarget → 落入暂存区；也可从剪贴板粘贴文件。
- 存储策略（可配）：**引用**原路径（默认，零拷贝；原文件被移动则标记失效）或**复制**到 `<Cache>/O_O/shelf/`。
- Panel：横向文件卡片（缩略图/图标、名称、大小），支持多选后**整体拖出**到任意应用（Avalonia `DragDrop.DoDragDrop` 携带文件列表）、`ContextMenu`（在文件夹中显示、复制路径 `CopyButton`、AirDrop/分享（mac `NSSharingService`）、移除）。
- Compact：有文件时 trailing 显示数量 `Badge`。
- 自动清理：按保留天数；清空用 `ConfirmMorph`。

> **实现状态（2026-11-10）**：`files.shelf` 能力（`files.onDrop` / `info` / `reveal` / `open`；岛面接受拖入的文件，只有在有插件接收时才显示可放下）；可拖出的列表行 `ui.item({ dragFiles })`。插件 `com.o_o.shelf`：只保存原路径（引用模式），页面"文件"分页列出（图标按类型、大小、已移动或删除的标记），点击打开、"在文件夹中显示"、移出、全部移出；收起时"暂存的文件数"小项；拖入时提醒；保留天数可设（默认 7）。**未做**：复制模式、缩略图、多选整体拖出、AirDrop/分享、`ConfirmMorph` 清空。

## 8. 剪贴板历史 `com.o_o.clipboard`

**宿主能力 `clipboard.watch`**

| 平台 | 监听方式 | 敏感内容排除 |
| --- | --- | --- |
| Windows | `AddClipboardFormatListener` | 跳过带 `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory=0` 格式的内容 |
| macOS | `NSPasteboard.changeCount` 轮询（500ms，前台活跃时 250ms） | 跳过 `org.nspasteboard.ConcealedType` / `TransientType` |
| Linux X11 | XFixes `SelectionNotify` | 跳过 `x-kde-passwordManagerHint=secret` |
| Linux Wayland（XWayland） | 优先 XFixes；Wayland 原生应用复制的内容需由 XWayland 同步过来，同步不全时回落 `wl-paste --watch`（Sidecar） | 同上 |

- **默认记录最近 30 条，用户可在控制台修改（1~500）**；超出按 FIFO 淘汰，**置顶项不计入淘汰**。
- 支持类型：纯文本、富文本（存纯文本 + HTML）、图片（缩略图 + 原图 ≤ 10MB）、文件列表、URL（识别后展示 `LinkUnfurl` 式预览，仅在用户开启联网预览时）。
- 去重：内容哈希相同则提升到顶部而不新增。
- Panel：`SearchField` 过滤 + `ChipGroup`（全部/文本/图片/文件/链接）+ 列表。
- **直接粘贴**（v1 实现）：选中一条后回车 → 写回剪贴板 → 收起岛 → 把焦点还给之前的应用 → 模拟一次粘贴快捷键。`Shift+回车` 只复制不粘贴。

  | 平台 | 实现 | 所需权限 |
  | --- | --- | --- |
  | Windows | 记住展开前的前台窗口，`SetForegroundWindow` 还原后 `SendInput` 发送 `Ctrl+V` | 无；目标是管理员权限进程时会被 UIPI 拦截 → 降级为"已复制，请手动粘贴"的 `Alert` |
  | macOS | 记住前台应用，`NSRunningApplication.activate` 还原后 `CGEventPost` 发送 `Cmd+V` | 辅助功能权限（`AXIsProcessTrustedWithOptions` 检测并引导到系统设置）；未授权时同样降级 |
  | Linux（XWayland） | XTest 扩展发送 `Ctrl+V` | 无；Wayland 原生窗口收不到 XTest 事件 → 检测到时降级 |

  岛窗口本身设为不抢焦点（Windows `WS_EX_NOACTIVATE`，macOS 非激活面板），减少焦点还原失败；还原后延迟约 50ms 再发按键。
  首次使用时在控制台"隐私"页用 `Alert` 说明所需权限；"直接粘贴"可用 `Switch` 关闭，关闭后回车只复制。
- 全局热键（默认 `Ctrl/Cmd+Shift+V`）直接展开到剪贴板面板。
- 忽略应用列表（`TagInput`）、一键清空（`HoldToConfirm`）、可选本地加密存储。

> **实现状态（2026-11-10）**：宿主能力 `clipboard.read/write/watch/paste`（`IPluginClipboard`；内容经 Avalonia 剪贴板，变化检测 / 敏感内容 / 直接粘贴经平台层 `IClipboardPlatform`）。macOS：`changeCount` 500ms 轮询、跳过 `org.nspasteboard.Concealed/Transient/AutoGeneratedType`、记住最近的前台应用，粘贴时重新激活它并用 `CGEventPost` 发送 ⌘V，未授权"辅助功能"时弹出系统引导并降级为"已复制"（已在本机验证记录）。Windows：`GetClipboardSequenceNumber`、`ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory`、`SetForegroundWindow` + `SendInput`（未在 Windows 上实测）。Linux：比较内容、XTest 发送 Ctrl+V（未实测；KDE 密码提示尚未识别）。插件 `com.o_o.clipboard`：文本 / 链接 / 文件，去重提到最前，保留条数默认 30（1–500）且置顶不计，"全部 / 文本 / 链接 / 文件"文字标签筛选、分页，点击直接粘贴（可在设置中改为只复制）。**未做**：图片与富文本、搜索框（岛内输入需要抢焦点，与直接粘贴冲突，待定）、全局热键、忽略应用列表、加密存储。

## 9. 邮箱 `com.o_o.mail`

- **宿主能力 `mail`**：基于 MailKit 的 IMAP 客户端（需验证 NativeAOT 兼容性，见 06），IDLE 长连接实时收信，断线指数退避。
- 账号类型：
  - Gmail / Outlook：OAuth2（XOAUTH2）。
  - QQ / 163 / 飞书邮箱 / 企业邮：IMAP + 授权码（`PasswordField`，存钥匙串）。
- 只读最小化：只拉取 `ENVELOPE` + 前 2KB 正文用于摘要；不存储正文。
- Compact：未读数 `Badge`；新邮件到达 → `Alert`（发件人 `Avatar` + 主题，3s）。
- Panel：多账户 `SegmentedControl`，最近 20 封列表，悬停预览，点击在默认邮件客户端/网页中打开；可选"AI 摘要"（借助 AI 插件，需用户启用）。
- 规则：VIP 发件人才弹 Alert、免打扰时段。

> **实现状态（2026-11-10）**：
> - **宿主能力 `mail`**（`mail.check / status / recent`）：没有用 MailKit（NativeAOT 风险 R5），而是自写的精简只读 IMAP 客户端（`Services/Imap.cs`）：隐式 TLS（993，系统证书校验）、LOGIN（引号转义，不能加引号的字符串走 literal）、SASL XOAUTH2（SASL-IR）、STATUS、EXAMINE（从不 SELECT / STORE，不会把邮件标成已读）、UID SEARCH、UID FETCH（ENVELOPE / BODYSTRUCTURE，再取第一个 text/plain（否则 text/html）部分的前 1KB 做摘要，`BODY.PEEK`）；解码 RFC 2047、quoted-printable、base64，字符集经 `CodePagesEncodingProvider`（GBK / GB2312 按 GB18030 解码）。响应大小与每次操作（45 秒）都有上限。轮询，不用 IDLE。
> - **账户与密钥**：插件只描述账户，宿主决定连哪里、自己取密钥。授权码账户：`{ host | hostSetting, port?, user | userSetting, passwordSetting }`——服务器必须是已知邮箱服务商（QQ、163、126、腾讯企业邮、阿里、iCloud、Gmail、Outlook 等）或用户在设置里填写的地址；密码由宿主从插件自己的密码设置读取，JS 拿不到。OAuth 账户：`{ oauth: handle }`——服务器由登录提供商固定（Google → `imap.gmail.com`，Microsoft → `outlook.office365.com`），令牌经 XOAUTH2 发送，插件给的 host 被忽略；其他提供商（飞书、钉钉）的登录不能用来收邮件。
> - **OAuth**：新增 `microsoft` 提供商（`login.microsoftonline.com/common` v2.0，PKCE，公共客户端，范围 `offline_access https://outlook.office.com/IMAP.AccessAsUser.All`）；登录返回的 `id_token` 里的邮箱用作账户名（XOAUTH2 的用户名）。Gmail 用 `https://mail.google.com/` 范围——这是 Google 的**受限**范围，公开发布前需要通过 Google 的应用验证（含安全评估）；验证前只能由用户自己创建客户端 ID。
> - **插件 `com.o_o.mail`**（可选）：最多 3 个授权码邮箱（预设 QQ / 163 / 126 / 腾讯企业邮 / 阿里 / iCloud / Gmail / 自定义服务器）+ Google / Microsoft 登录（填好客户端 ID 后在"邮件"页点"用 … 登录"）。收起时右侧未读数小项；每次检查到新邮件提醒一次（"n 封新邮件"，首次检查不算新）；"邮件"页按账户分标签，最近邮件分页，未读圆点，点击打开对应的网页邮箱（`shell.openUrl`）；登录账户可退出。
> - **未做 / 未验证**：IDLE 实时推送、发信、正文全文、VIP 规则与免打扰；没有用真实的 Gmail / Outlook / QQ 账户验证过（XOAUTH2 与 IMAP 主机名按各家文档）；Outlook 个人邮箱已停用应用密码，只能走 Microsoft 登录。

## 10. AI 订阅 / 余额：聚合插件 + 提供商插件

### 10.1 拆分原则

**余额查询不内置在一个大插件里，每个提供商（尤其是第三方客户端）都是独立插件。** 好处：

- 某个提供商接口变动，只需更新那一个插件，不用发壳或聚合插件的新版本。
- 权限最小化：DeepSeek 插件只拿得到 `network:api.deepseek.com`，碰不到 Claude 的登录态。
- 社区可以自己给任意新客户端写提供商插件。

```text
com.o_o.ai-usage            聚合插件：面板、告警、Compact 活动、刷新调度、历史趋势
   ▲ 贡献点 aiUsageProviders（插件间协议）
   ├─ com.o_o.ai.claude      本地登录态导入
   ├─ com.o_o.ai.codex       本地登录态导入
   ├─ com.o_o.ai.deepseek    API Key
   ├─ com.o_o.ai.kimi        API Key
   ├─ com.o_o.ai.newapi      Base URL + 令牌，多站点
   └─ 任意社区插件
```

### 10.2 提供商插件协议

提供商插件在 Manifest 里声明 `contributes.aiUsageProviders`，并导出实现：

```jsonc
// com.o_o.ai.deepseek/plugin.json（节选）
{
  "permissions": ["network:api.deepseek.com", "secrets"],
  "contributes": {
    "aiUsageProviders": [{
      "id": "deepseek", "title": "DeepSeek", "icon": "assets/deepseek.svg",
      "kind": "official",                  // official | unofficial | manual
      "accountSetup": "apiKey"             // apiKey | localAuth | custom（自定义设置视图）
    }]
  }
}
```

```ts
// 提供商插件导出（由聚合插件通过宿主跨插件调用）
export interface UsageProvider {
  discoverAccounts?(): Promise<AccountRef[]>;          // 自动发现（localAuth 类）
  fetch(account: AccountRef): Promise<UsageSnapshot>;
}
type UsageSnapshot = {
  account:   { label: string; email?: string; plan?: string };
  balance?:  { amount: number; currency: 'CNY' | 'USD' | 'credits' };
  quota?:    { name: string; usedPercent: number; resetsAt?: string; window?: '5h' | 'weekly' | 'monthly' }[];
  fetchedAt: string;
  status:    'ok' | 'expired' | 'error';
  message?:  string;                                    // 如 "请在 Claude Code 中重新登录"
};
```

- **跨插件调用**：宿主新增 `island.providers.call(providerId, method, args)`。调用在**提供商插件自己的引擎**里执行，只返回 `UsageSnapshot` 数据；聚合插件永远拿不到对方的凭据。
- 返回值由宿主按 schema 校验，非法数据直接丢弃。

### 10.3 Claude / Codex：解析本机已登录配置自动添加

用户只要在本机登录过 Claude Code / Codex CLI，插件就能**自动发现账户**，无需再输入任何东西。

**宿主新能力 `localAuth`**（只读、白名单、逐项授权）——插件没有通用文件系统权限，只能通过它读取预先登记的几个来源：

| 来源 ID | 位置（按顺序查找） | 读取字段 |
| --- | --- | --- |
| `claude-code` | **账户信息（无密钥）**：`<配置目录>/.claude.json` 的 `oauthAccount`（`emailAddress`、`displayName`、`billingType`、`organizationUuid` 等）。**凭据**：macOS 钥匙串条目，服务名 `Claude Code-credentials-<sha256(配置目录)前 8 位十六进制>`；Windows / Linux：`<配置目录>/.credentials.json`。配置目录 = `$CLAUDE_CONFIG_DIR`，未设置时为 `~/.claude` | 显示用账户信息；OAuth access token、过期时间 |
| `codex-cli` | `$CODEX_HOME/auth.json`，默认 `~/.codex/auth.json`：`auth_mode`、`OPENAI_API_KEY`（ChatGPT 登录时为 null）、`tokens{id_token, access_token, refresh_token, account_id}`、`last_refresh` | **套餐无需联网**：`id_token`（JWT）声明里有 `email`、`chatgpt_plan_type`、`chatgpt_subscription_active_until`；额度仍需联网 |

> M0 ⑤ 核实结果（2026-11-09，本机 macOS，只读取结构、未读取任何值）：
> - Codex 的位置与字段与设计一致。
> - Claude Code 的钥匙串服务名**带后缀**：设置了 `CLAUDE_CONFIG_DIR` 时为 `Claude Code-credentials-` + 配置目录路径 SHA-256 的前 8 位（本机实测吻合）。因此发现逻辑按前缀枚举条目，再用各候选配置目录的哈希反查对应关系。默认目录（未设置环境变量）下的无后缀名称沿用公开资料，本机未能验证。
> - 显示账户、征求同意时只需读 `.claude.json`，不碰钥匙串；真正调用用量接口时才读取凭据。该钥匙串条目属于 Claude Code，O_O 第一次读取时 macOS 会弹出系统授权框（"允许 / 始终允许"），这是预期行为，同意流程需提前说明。
> - Windows / Linux 上的 `.credentials.json` 尚未实机核实。

规则：

1. **只读，绝不刷新、绝不写回。** OAuth 刷新会让 refresh token 轮换，导致 CLI 本身被登出。token 过期时显示 `status: 'expired'`，提示"请运行一次 Claude Code / Codex 以刷新登录"，并监听文件变化，CLI 刷新后自动恢复。
2. **逐项授权**：首次发现时控制台弹 `Dialog`："检测到 Claude Code 登录（xxx@example.com，Max 订阅），是否允许 Claude 用量插件读取？"。授权按来源 ID 记录，可随时撤销。
3. **token 绑定域名**：宿主只在请求发往该来源登记的域名时注入 token（插件拿到的是不透明的 `authHandle`，不是 token 明文），防止插件把 token 发去别处。
4. **多账户**：通过环境变量或多个配置目录登录的多份配置都会被发现；用户也可以在控制台手动添加额外的配置目录。
5. **用量数据**：用登录态调用各自客户端显示用量用的接口（非公开）拿 5 小时窗口 / 每周额度；接口失败时，Codex 回落为解析本地会话日志里最近一次的速率限制信息。UI 上标注"非官方"。

### 10.4 首批提供商插件

| 插件 | 账户来源 | 数据 | 类型 |
| --- | --- | --- | --- |
| `ai.claude` | `localAuth: claude-code` 自动发现 | 订阅类型、5h / 每周额度百分比、重置时间 | 非官方 |
| `ai.codex` | `localAuth: codex-cli` 自动发现 | ChatGPT 套餐、5h / 每周额度、重置时间 | 非官方 |
| `ai.deepseek` ✅ | API Key（最多 3 个） | 余额与充值 / 赠送明细（`GET https://api.deepseek.com/user/balance`） | 官方 |
| `ai.kimi` ✅ | API Key（最多 3 个），站点：中国站 / 国际站 | 可用余额与现金 / 代金券明细（`GET https://api.moonshot.cn` 或 `.ai` `/v1/users/me/balance`） | 官方 |
| `ai.newapi` ✅（仅本机站点） | 站点地址 + sk 令牌（最多 3 个），额度单位 USD / CNY | 剩余额度、总额度、已用（`/v1/dashboard/billing/subscription` 与 `/usage`） | 开源项目的 OpenAI 兼容接口 |
| `ai.manual` | 用户自填额度、续费日期 | 到期提醒 | 兜底 |

> **暂不实现**：Grok Bot、ZCode、WorkBuddy 等第三方客户端的提供商插件。协议已为它们预留（`aiUsageProviders` 贡献点 + `localAuth` 来源登记），以后由第一方或社区按需补充，不影响壳与聚合插件。

### 10.4.1 实现状态（2026-11-09）

已实现并随程序发布：`plugins/com.o_o.ai-usage`（聚合）、`plugins/com.o_o.ai.codex`、`plugins/com.o_o.ai.claude`。

**API Key 类提供方（2026-11-09）**：`plugins/com.o_o.ai.deepseek`、`com.o_o.ai.kimi`、`com.o_o.ai.newapi`。
- Key 填在各插件的设置里（密码类型，存系统钥匙串）：`key1`–`key3` 加可选名称，每个 Key 是一个账户；账户的 `handle` 是槽位名（`key1`），Key 本身不离开提供方插件。名称留空时，第一个 Key 显示服务名，其余显示打码的 Key（`sk-…abcd`）。
- 返回 `balance`（聚合插件显示滚动余额与明细）。DeepSeek 金额是字符串，优先取 CNY；Kimi 中国站为人民币、国际站为美元；NewAPI 的"总额度"是令牌的剩余 + 已用，用量接口的 `total_usage` 是已用 × 100，不限额度的令牌（总额度 1 亿）显示"已用额度"。错误（Key 无效、接口返回非 2xx、格式变化）都显示中文说明。
- 接口依据：DeepSeek 与 Kimi 为官方文档（api-docs.deepseek.com、platform.kimi.com/docs/api/balance）；NewAPI 的两个计费接口没有官方参考页，依据其 `controller/billing.go` 源码（OneAPI 兼容路由），可信度中等。
- **限制**：插件的网络权限是清单里写死的主机名，自建的 NewAPI 站点主机事先未知，目前只能访问本机（`network:localhost`）上的站点；其他地址会提示"暂时只能访问本机的 NewAPI 站点"。需要宿主支持"用户在设置里填写、经确认后授权的主机"才能用于远程站点。
- 品牌图标：岛上的 `ui.logo` 目前只有 claude / codex，这三个提供方暂不显示图标。
- 测试：`AiProviderPluginsTests` 用本机假服务器模拟三个接口（把服务地址指向 127.0.0.1），覆盖多 Key、Key 无效、站点切换、剩余额度计算和远程站点被拒，全程不联网。

- 提供方协议：`accounts() → [{ handle, label, plan }]`，`usage(account) → { plan?, windows: [{ name, usedPercent, resetsAt? }] }`。
- 聚合插件没有任何网络或凭据权限；提供方未获授权时，它在岛上提示"请在控制台允许"，并每 30 秒重新检查。
- **未经实测的部分**：两个提供方调用的用量接口（Codex：`chatgpt.com/backend-api/wham/usage`；Claude：`api.anthropic.com/api/oauth/usage`）是按客户端显示用量的方式写的非公开接口，没有用真实登录验证过。解析做了防御：格式不符时显示"接口格式变化"，不会显示猜测的数字。首次在真机上授权后需核对。
- 端到端测试覆盖了聚合、提供方错误、等待授权，以及真实的 Codex 提供方在凭据只允许本机时被拒绝访问 `chatgpt.com`（全程不联网）。

**Claude 改走官方状态栏数据（2026-11-09 决定）**：
- 经查 Claude Code 文档：`/usage` 只能交互使用（`-p` 模式支持的命令里没有它），Hooks 与 Agent SDK 都不提供额度数据；**唯一有文档的本地来源是状态栏**——`statusLine` 命令的 stdin JSON 里有 `rate_limits.five_hour` / `rate_limits.seven_day`（`used_percentage`、`resets_at`），仅 Pro / Max 订阅、会话首次响应之后才有。
- 文档同时提到：未经批准，Anthropic 不允许第三方产品提供 claude.ai 登录或额度功能。读取 Claude Code 的令牌并调用非公开接口不在任何文档约定之内，因此**已停用**：`ClaudeCodeSource.ReadCredential` 永远返回 null，Claude 用量插件不再申请任何网络权限。
- 现在的做法（`ClaudeStatusLineBridge`）：用户在控制台"连接"页点"接入"，O_O 把一个桥接脚本设为该配置目录的状态栏命令；脚本把 stdin 的 JSON 存给 O_O，再运行用户原来的状态栏命令（显示不变）。原 `statusLine` 与整份 `settings.json` 都有备份，"还原"会原样放回；无法解析的 `settings.json` 一律不动。数据只在有 Claude Code 会话时更新，岛上显示"数据更新于 X 前"。
- 套餐（如 Max 20x）由 `claude auth status --json` 得到（以该配置目录为 `CLAUDE_CONFIG_DIR` 运行），Claude Code 自己读取自己的登录，O_O 不碰。运行 Claude Code 的命令可在"连接"页自定义，留空时使用自动找到的默认路径，并在界面上写明这个默认值。
- 本机实测：发现 rec 下已登录的账户并通过 CLI 拿到 "Max 20x"，0.6 秒；未读取任何凭据。

**Claude 改走官方状态栏数据（2026-11-09 决定）**：
- 经查 Claude Code 文档：`/usage` 只能交互使用（`-p` 模式支持的命令里没有它），Hooks 与 Agent SDK 都不提供额度数据；**唯一有文档的本地来源是状态栏**——`statusLine` 命令的 stdin JSON 里有 `rate_limits.five_hour` / `rate_limits.seven_day`（`used_percentage`、`resets_at`），仅 Pro / Max 订阅、会话首次响应之后才有。
- 文档同时提到：未经批准，Anthropic 不允许第三方产品提供 claude.ai 登录或额度功能。读取 Claude Code 的令牌并调用非公开接口不在任何文档约定之内，因此**已停用**：`ClaudeCodeSource.ReadCredential` 永远返回 null，Claude 用量插件不再申请任何网络权限。
- 现在的做法（`ClaudeStatusLineBridge`）：用户在控制台"连接"页点"接入"，O_O 把一个桥接脚本设为该配置目录的状态栏命令；脚本把 stdin 的 JSON 存给 O_O，再运行用户原来的状态栏命令（显示不变）。原 `statusLine` 与整份 `settings.json` 都有备份，"还原"会原样放回；无法解析的 `settings.json` 一律不动。数据只在有 Claude Code 会话时更新，岛上显示"数据更新于 X 前"。
- 套餐（如 Max 20x）由 `claude auth status --json` 得到（以该配置目录为 `CLAUDE_CONFIG_DIR` 运行），Claude Code 自己读取自己的登录，O_O 不碰。
- 运行 Claude Code 的命令可在"连接"页自定义：填命令名（默认 `claude`）或完整路径都可以，后面还可以带参数。命令名按"本进程 PATH → 交互式登录 shell（`$SHELL -ilc 'command -v …'`，读取 ~/.zshrc，与在终端里输入该命令时一致）→ 常见安装位置"的顺序查找；从访达启动的应用 PATH 很短，所以必须问 shell。本机实测：机器上装了三份 Claude Code，只有交互式 shell 给出的 `~/.npm-global/bin/claude`（2.1.322）是用户实际在用的，并返回 "Max 20x"。

**真机首测（2026-11-09，用户本机）**：
- Codex 接口可用：Free 套餐返回 30 天窗口，现显示为"每月"。
- Claude 首测显示"已退出登录"：应用启动时没有 `CLAUDE_CONFIG_DIR`，只找到 `~/.claude.json` 里的旧账户。已修复：只列出本机确实有凭据的配置目录（macOS 上只查钥匙串条目是否存在、不读取内容，因此不会弹授权框）；Claude 用量插件新增"其他配置目录"设置，每行一个路径。
- **自动发现包装工具的配置目录**：rec 这类账户管理工具给每个账户单独的 `CLAUDE_CONFIG_DIR`（如 `~/.rec/claude/accounts/<id>/config`），O_O 自己启动时拿不到这个环境变量。现在发现账户时会扫描家目录下的隐藏文件夹（深度 5、跳过 `.npm` / `.cache` / `node_modules` 等、不跟随符号链接、1.5 秒与 2 万项上限、结果缓存 5 分钟），找到含 `.claude.json` 的配置目录，再只保留确有登录凭据的。本机实测 0.9 秒找到 rec 下唯一已登录的账户、跳过其余 3 个已退出的。"其他配置目录"设置仅作兜底。
- **布局（2026-11-09 定稿，参考用户提供的设计）**：一次只显示一个提供商。顶部是"‹ 提供商文字标签 ›"，该提供商有多个账户时右侧是"当前账户 ⌄"的文字选择器；主体按额度类型变化——额度型每个窗口一个圆环（Arc `Gauge`，最多 3 个，按 5 小时 → 每日 → 每周 → 每月的固定顺序，位置不随用量变化），左侧是套餐与更新时间，超过 3 个窗口的部分才用细进度条；余额型（DeepSeek、NewAPI 这类）显示滚动数字的余额（Arc `AnimatedCounter`）和明细；未授权的提供商也有标签页，内容说明去哪里授权；底部是页码点。所选提供商记在插件存储里。全部按 02 §7 的岛内设计要求：没有框和药丸，层级靠字重与颜色。提供方协议新增可选的 `balance: { amount, currency, label?, details? }`。
- （旧）布局：每个账户一行——左侧固定宽度的一栏（来源名与套餐徽章、邮箱、"更新于…"或错误信息），右侧并排该账户的各个圆环（88px），多个账户时卡片不会超高。
- 用量改为 Arc `Gauge` 圆环：每个额度窗口一个环，环下显示窗口名和重置时间；低于 75% 用强调色，75% 起黄色，达到提醒阈值后红色。

- 2026-11-10：宿主新增 `network:@<设置项>` 权限——插件只能访问用户在该文本设置中填写的那个站点（协议、主机、端口都相同；用户填 http 才允许 http）。插件不能修改自己的设置，所以地址总是来自用户；声明时该设置必须是普通文本设置，否则清单报错。NewAPI 改用 `network:@baseUrl`，远程自建站点现在可以访问。没有品牌标志的提供商（DeepSeek、Kimi、NewAPI）在用量页显示中性的钱包图标。

### 10.5 聚合插件 UI

- Panel：每个账户一张卡——提供商图标、套餐名 `Badge`、余额 `AnimatedCounter` 或额度 `UsageMeter`（5h / 每周 / 每月各一条）、重置时间、近 7 天趋势 `Sparkline`；`status: expired` 时卡片显示 `Alert`（warning）和恢复说明。
- 告警：余额低于阈值或额度 > 80% → `passive` 活动；> 95% → `Alert`。
- Compact（可选常驻）：用户指定的一项，如 "Claude 5h 62%"。
- 控制台：API Key 类用 `ProviderKeys`；本机登录类用 `ConnectorSetup` 列出"已发现的本机登录"及授权状态；`ProviderStatus` 显示连通性。
- 刷新：默认 10 分钟；手动刷新；每个提供商全局限速，避免触发对方风控。

---

## 11. 扩展插件候选（v1 之后）

| 插件 | 说明 | 用到的 Arc 组件 |
| --- | --- | --- |
| 系统监控 | CPU / 内存 / 网速 / GPU | `Sparkline`、`Gauge`、`RealtimeStream` |
| 电池与充电 | 插电时展示充电动画与剩余时间 | `ChargeSession` |
| 天气 | 当前天气 + 降雨提醒 | `MetricCard`、`Sparkline` |
| AI 快问 | 岛上直接提问，流式回答 | `AiComposer`、`Markdown`、`AgentStatus` |
| Claude Code / Codex 任务状态 | 本地 Agent 正在运行/等待确认/完成时在岛上提示（读 hook 或本地日志） | `AgentStatus`、`ToolCall` |
| 下载与构建进度 | 浏览器下载、CI/GitHub Actions 状态 | `Progress`、`Badge` |
| 麦克风/摄像头占用指示 | 隐私提示 | `Badge` |
| 截图 + OCR + 翻译 | 截图后在岛上显示识别结果 | `CopyButton`、`AiTranslate` |
| 通知镜像 | 把选定应用的系统通知汇聚到岛 | `Alert`、`NotificationCenter` |
| 快速笔记 | 一句话速记，同步飞书文档 | `Textarea` |
| 股票 / 加密货币行情 | 自选列表 | `Sparkline`、`AnimatedCounter` |
| 语音输入 | 按住热键说话转文字 | `VoiceOrb` |
