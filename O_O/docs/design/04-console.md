# 04 · 控制台面板

## 1. 定位与入口

控制台是配置灵动岛的独立窗口（`ArcWindow`，自定义标题栏），入口：托盘菜单 / 岛右键 / 全局热键 `Ctrl/Cmd+,` / 命令行 `O_O --console`。关闭控制台不影响岛运行。

整体骨架用 Arc Blocks：左侧 `Sidebar`，右侧每页以 `PageHeader` 开头；全局搜索用 `SettingsCommand`（在一个搜索框里找到并直接修改任意设置）。窗口内 `ToastStack` 统一反馈"已保存"。

## 2. 信息架构

```text
控制台
├─ 概览          实时预览 + 状态
├─ 外观          主题 / 颜色 / 材质 / 动效
├─ 位置与布局    屏幕 / 刘海 / 放置模式 / 尺寸
├─ 活动与面板    槽位分配 / 优先级 / 顺序
├─ 插件          已安装 / 权限 / 设置 / 日志
├─ 账户与连接    日程（飞书/钉钉/企业微信/Google）/ 邮箱 / AI 提供商
├─ 快捷键
├─ 隐私
├─ 开发者
└─ 关于与更新
```

## 3. 各页设计（只使用 TKCode.Arc 组件）

### 3.1 概览

- 顶部：**岛实时预览**——在控制台里嵌入一个缩小的 `IslandSurface`，可点按钮切换 Idle / Compact / Expanded / Alert 预览（`SegmentedControl`）。
- 指标：`MetricCard` × 4（运行插件数、今日专注时长、剪贴板条目数、待办剩余）。
- 插件健康：`ProviderStatus` 风格列表复用为插件运行状态（正常 / 重启中 / 已禁用）。
- 空状态：首次启动显示 `OnboardingIllustration` 引导三步（选位置 → 选主题 → 启用插件）。

### 3.2 外观

| 设置 | 组件 |
| --- | --- |
| 明 / 暗 / 跟随系统 | `SegmentedControl` + `ThemeSwitcher`（预览过渡效果） |
| 强调色（8 个 Arc 预设） | `RadioCards`（色块卡片） |
| 自定义强调色 | `ColorPicker`（映射到最近预设 + 自定义 Token） |
| 主题包 | `Card` 网格（`WaterfallPanel`），每卡带预览图；`Badge` 标"当前" |
| 岛材质 | `Select`：纯色 / 亚克力 / Mica |
| 圆角、弹簧阻尼 | `Slider` / `ScrubInput` |
| 动效等级 | `SegmentedControl`：完整 / 减弱 / 关闭（对应 `ArcMotion.Level`） |
| 字体 | `Select`：Inter / Geist / 系统 |

### 3.3 位置与布局

| 设置 | 组件 |
| --- | --- |
| 显示在哪块屏幕 | `RadioGroup`：主屏 / 跟随鼠标 / 内建屏 / 全部 |
| 放置模式 | `RadioCards`：贴顶 / 悬浮 / 菜单栏下（每卡有示意图） |
| 刘海检测结果 | `Alert`（info）："检测到刘海：宽 186pt，高 37pt，已启用包裹模式" |
| 水平/垂直偏移 | `ScrubInput`（拖动标签微调，实时作用于岛） |
| 展开最大尺寸 | `NumberField` × 2 |
| 悬停展开延迟 | `Slider`（0~1000ms，带"仅点击"选项 `Switch`） |
| 全屏时隐藏 / 录屏时隐藏 | `Switch` |

### 3.4 活动与面板

- 面板顺序：`ReorderableList`（拖拽调整 Expanded 顶部标签顺序，`Switch` 控制显隐）。
- Compact 槽位策略：`Table`（插件 × 优先级 `Select` × 是否允许抢占 `Switch`）。
- Idle 显示内容：`Select`（空白 / 时钟 / 指定插件）。

### 3.5 插件

- 列表：`SearchField` + `FilterToolbar`（全部 / 已启用 / 有更新 / 第三方）+ `Table`（图标、名称、版本、发布者 `Badge`、启用 `Switch`）。
- 安装：`FileDropzone` 接收 `.island` 包（v1 不做插件市场）。
- 详情：`SheetStack` 逐层下钻 → 
  - 概要（描述 `Markdown`、版本、权限）
  - **权限**：每项一行 `Switch` + 说明；新增权限首次确认用 `Dialog`
  - **设置**：由 Manifest 的 JSON Schema 自动生成（见 §4）
  - **日志**：`CodeBlock`（只读、等宽、可复制）
  - **资源**：`Sparkline`（CPU 时间）、`UsageMeter`（存储配额）
- 卸载：`ConfirmMorph`（按钮原地变为确认 → 结果 + 撤销）。

### 3.6 账户与连接

- 总览用 `ConnectorSetup` Block（"连接你的工具"卡片：飞书、钉钉、企业微信、Google Calendar、邮箱…，每个应用一行连接状态）。
- AI 提供商密钥用 `ProviderKeys` Block（每个提供商一行：Key 输入/掩码、验证、删除），配合 `ProviderStatus` 显示连通性。
- 邮箱账号：`Input` + `PasswordField`（授权码），高级区 `Accordion` 收起 IMAP 主机/端口。

### 3.7 快捷键

`Table` 列出所有命令（壳 + 插件贡献），每行 `ShortcutRecorder`（自带冲突警告）。

### 3.8 隐私

| 设置 | 组件 |
| --- | --- |
| 剪贴板历史条数（默认 30） | `NumberField`（1~500） |
| 剪贴板忽略的应用 | `TagInput` |
| 选中后直接粘贴到前一个应用 | `Switch`；macOS 未授权辅助功能时旁边显示 `Alert` 与"打开系统设置"按钮 |
| 历史保留天数 | `Select` |
| 清除所有历史 | `HoldToConfirm`（按住确认，防误触） |
| 文件暂存自动清理 | `Select`：1 天 / 7 天 / 从不 |
| 网络访问审计 | `Timeline`（按插件、域名列出近期请求） |

### 3.9 开发者

- 加载未打包插件（选择目录）+ 热重载 `Switch`。
- 插件运行时检查器：`TreeView` 列出插件 → 选中后右侧 `JsonViewer` 显示当前 VNode 树 / 设置 / KV。
- 实时日志：`CodeBlock` + 级别 `ChipGroup` 过滤。
- 模拟器：手动触发 Alert / 活动 / 拖放事件，便于调试。

### 3.10 关于与更新

版本、更新通道 `SegmentedControl`（稳定 / 预览）、`ActionButton`"检查更新"（自带进行中状态）、开源许可列表。

## 3.12 Claude Code 连接（已实现，位于插件卡片内）

不设单独页面：凡声明了 `localAuth:claude-code` 的插件，其卡片里在权限下方显示"Claude Code"一节（`ClaudeConnectionPanel`）——"Claude Code 命令"（占位符就是默认值 `claude`，命令名或完整路径都可以、留空即用默认；找不到时在字段下提示），以及本机所有已登录的配置目录（邮箱、套餐、是否已接入状态栏），每行"接入 / 还原"，多个未接入时有"全部接入"。命令只保存在宿主（`<数据目录>/connections.json`），插件无法设置宿主要运行的程序。

## 3.13 统一窗口（已实现）

控制台不显示 Arc 的标题栏带（`IsTitleBarVisible=False`，内容仍延伸进系统装饰区）：侧边栏玻璃一直延伸到顶边，macOS 的红绿灯浮在侧边栏上方，Windows / Linux 在同一位置绘制红绿灯样式按钮；侧边栏顶部和内容区顶部各有一条不可见的拖动区（拖动移动窗口，双击缩放），与 macOS"系统设置"一类应用一致。

## 3.11 实现状态（M3 第一部分）

已实现 `src/Island.UI/Console`：`ArcWindow` + `Sidebar`，页面有**插件**（状态徽章、授权提示、逐项权限开关、自动设置表单）、**外观**（浅色 / 深色 / 跟随系统、强调色）、**关于**（版本、数据目录——可更改，数据在重启时迁移，见 `Island.Core.Storage.DataLocation`）。macOS 使用系统红绿灯；Windows / Linux 在标题栏左侧绘制红绿灯样式的窗口按钮（`TrafficLights`）。从岛展开后的"设置"按钮打开，开发时也可用 `O_O --console` 启动即打开。

自动表单的实际映射：枚举 → `Select`，密码 → `PasswordField`，其余字符串 → `Input`，数字 → `NumberField`，布尔 → `Switch`。（下表中的 `RadioGroup`、`ColorPicker`、`ShortcutRecorder` 等映射尚未实现。）

待决：Arc 的 Primary 按钮是前景色（浅色模式为黑色），不是强调色；如要更像 macOS 的蓝色默认按钮，需要为 Button 写一个基于 Arc 主题的 ControlTheme。

## 4. 插件设置的自动表单

插件在 Manifest 里给 JSON Schema，宿主按规则生成表单（字段以 `Label` 命名、`Description` 解释、`ErrorMessage` 报错——遵循 Arc 规则）：

| Schema | Arc 组件 |
| --- | --- |
| `string` | `Input` |
| `string` + `format: password` / `x-secret: true` | `PasswordField`（值存入钥匙串而非 settings.json） |
| `string` + `enum`（≤ 5 项） | `RadioGroup`；> 5 项 → `Select` |
| `string` + `format: color` | `ColorPicker` |
| `string` + `format: hotkey` | `ShortcutRecorder` |
| `string` + `format: date` / `time` | `DatePicker` / `TimePicker` |
| `string` + `x-multiline` | `Textarea` |
| `number` / `integer` | `NumberField`；带 `x-slider` → `Slider` |
| `boolean` | `Switch` |
| `array<string>` | `TagInput`；带 `enum` → `MultiSelect` |
| `object` | `Accordion` 分组 |

修改即时生效（`Switch` 语义），无需"保存"按钮；无法即时生效的项（如屏幕切换）以 `Toast` 提示。
