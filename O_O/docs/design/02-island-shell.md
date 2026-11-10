# 02 · 灵动岛壳（窗口、状态、刘海适配）

## 1. 关键决策：固定大窗口 + 动态命中区域

**不随动画改窗口尺寸。** 在三个平台上频繁 `Resize` 顶层窗口都会闪烁、掉帧（尤其 macOS 与 Windows DWM）。做法是：

- `IslandWindow` 一次性创建为**最大可能尺寸**（默认 760×460 逻辑像素，可配），透明背景，贴在目标屏幕顶部中央。
- 岛的可见形状 `IslandSurface` 在窗口内部用 Avalonia 动画伸缩（宽、高、圆角、内容交叉淡化）。
- 每当形状变化，平台层把**可点击区域**更新为当前胶囊的矩形（加 4px 容差），其余透明区域**点击穿透**到下方应用。

| 平台 | 窗口层级 | 点击穿透实现 |
| --- | --- | --- |
| Windows | `Topmost`、`WS_EX_TOOLWINDOW`（不进 Alt-Tab/任务栏）、`WS_EX_NOACTIVATE` | 处理 `WM_NCHITTEST`：胶囊外返回 `HTTRANSPARENT`；或在鼠标离开胶囊时切换 `WS_EX_TRANSPARENT`（更稳，作为默认） |
| macOS | `NSWindow.level = NSStatusWindowLevel`（高于菜单栏）、`collectionBehavior = canJoinAllSpaces \| stationary \| fullScreenAuxiliary \| ignoresCycle` | 全局鼠标位置轮询（仅在 60px 邻域内高频）切换 `ignoresMouseEvents` |
| Linux X11 | `_NET_WM_WINDOW_TYPE_DOCK`、`_NET_WM_STATE_ABOVE / STICKY / SKIP_TASKBAR` | XShape 扩展设置 input shape = 胶囊矩形 |
| Linux Wayland | 合成器不允许客户端自定位/置顶 | **已定：通过 XWayland 运行**，启动时检测到 Wayland 会话则以 X11 后端启动（设置 `DISPLAY`、不启用 Avalonia 的 Wayland 后端），窗口行为与 X11 行一致；原生 layer-shell 不在 v1 范围 |

Avalonia 侧窗口属性：`WindowDecorations="None"`、`TransparencyLevelHint="Transparent"`、`Background="Transparent"`、`CanResize="False"`、`ShowInTaskbar="False"`、`Topmost="True"`（macOS 除外，由平台层设置层级）。所有窗口都使用 Arc 的 `ArcWindow`：岛窗口关闭标题栏（`IsTitleBarVisible=False`、`TitleBar=null`）且不延伸进系统装饰（`ExtendClientAreaToDecorationsHint=False`），实测窗口在岛外完全透明、内容从顶边开始。

## 2. 岛的状态机

```text
               hover 停留 ≥ 250ms / 点击 / 热键
   ┌────────┐  ─────────────────────────────▶  ┌──────────┐
   │Compact │                                  │ Expanded │
   │(含Idle)│  ◀─────────────────────────────  │          │
   └───┬────┘   鼠标离开 600ms / Esc / 失焦      └────┬─────┘
       │ ▲                                         │
 push  │ │ 超时/处理完                              │ 拖入文件
 alert ▼ │                                         ▼
   ┌────────┐                               ┌────────────┐
   │ Alert  │  (瞬时通知：复制成功、专注结束)  │ DropTarget │ → 文件暂存插件
   └────────┘                               └────────────┘
   Hidden：全屏应用 / 用户手动隐藏 / 屏幕共享隐私模式
```

| 状态 | 尺寸（逻辑 px，默认） | 内容来源 |
| --- | --- | --- |
| Idle | 刘海宽 或 180×32 | 无活动时，可选显示时钟插件的极简内容或完全空白 |
| Compact | (Leading + 中央 + Trailing)×32~37 | 当前主活动的 `compact.leading` / `compact.trailing` 两个槽 |
| Expanded | 最大 720×420，按内容自适应高度 | 当前主活动的 `expanded` 视图；顶部 `SegmentedControl` 切换各插件 |
| Alert | Compact 宽 +80，停留 1.5~4s | `island.alert()` 推送 |
| DropTarget | Expanded 尺寸 | 拖拽悬停时展开为放置区 |

### 2.1 活动（Live Activity）调度

插件通过 `island.activity.start({ priority, compact, expanded, ttl })` 声明"我现在有东西要展示"。宿主维护优先级队列：

- 优先级：`critical`(来电/会议即将开始) > `active`(正在播放/专注中) > `passive`(下一个日程) > `ambient`(时钟)。
- 同时有 2 个及以上活动时：主活动占据岛；第二活动以**分离小气泡**显示在岛右侧（类似 iPhone 的双活动），点击可交换。
- 滚轮在 Compact 上滚动 = 在活动间轮换；用户手动 Pin 某活动则不被自动抢占。
- 每个插件最多同时持有 2 个活动，防刷屏。

> 实现状态（外形）：岛的轮廓由 `ContinuousBorder` 绘制并裁剪内容——连续曲率圆角（Apple 风格的超椭圆，不是圆弧），贴顶 / 包裹刘海时带 8pt 内凹肩部；展开圆角 40pt；展开高度按内容测量后在 120–400pt 之间取值，内容变化时弹簧过渡到新高度。
>
> 实现状态（菜单）：展开后的右上角只有一个"⋯"按钮，打开 Arc `DropdownMenu`（打开控制台、保持展开、退出 O_O）；在岛上任意位置右键打开同样的菜单（Arc `ContextMenu`）。没有"收起"按钮：指针离开即收起，"保持展开"可以固定。菜单打开期间岛保持当前状态。
>
> 实现状态：展开状态下，有多个活动时顶部会出现分段切换（`SegmentedControl`），可以查看非当前的活动；紧凑状态仍显示优先级最高的活动。双活动气泡尚未实现。

### 2.2 交互约定

| 手势 | 行为 |
| --- | --- |
| 悬停 | 250ms 后展开（可配 0~1000ms，或关闭仅点击展开） |
| 单击 | 展开 / 在 Expanded 内交给插件 |
| 右键 | Arc `ContextMenu`：固定此活动、隐藏 30 分钟、打开控制台、插件设置 |
| 滚轮 | 活动轮换；Expanded 内为滚动 |
| 拖入文件/文本 | 进入 DropTarget，交给声明了 `drop` 贡献点的插件（默认文件暂存） |
| 全局热键 | 默认 `Ctrl/Cmd+Shift+Space` 展开并聚焦，`Esc` 收起 |

## 3. 刘海（Notch）适配 —— macOS 重点

### 3.1 检测

`NSScreen` 提供（macOS 12 起可用，项目最低支持 14，可直接调用无需版本判断）：

- `safeAreaInsets.top > 0` → 该屏有刘海，值即菜单栏/刘海高度（常见 32~38pt）。
- `auxiliaryTopLeftArea` / `auxiliaryTopRightArea` → 刘海左右两侧可用矩形。
- 刘海矩形 = 屏幕顶部，x 从 `left.maxX` 到 `right.minX`。

平台层输出统一模型：

```csharp
public sealed record ScreenTopology(
    string ScreenId, PixelRect Bounds, double Scale,
    Rect? Notch,          // null 表示无刘海
    double MenuBarHeight, // mac 菜单栏高度；Windows/Linux 为 0
    bool IsPrimary);
```

外接显示器、合盖、分辨率变化时（`NSApplicationDidChangeScreenParametersNotification` / `WM_DISPLAYCHANGE` / XRandR 事件）重新计算并重新布局。

### 3.2 布局策略："包住刘海"

```text
   ┌────────── 屏幕顶边 ──────────────────────────────────────────┐
   │ 菜单栏…     ┌──────┬═══════════════════┬──────┐     …菜单栏 │
   │            │ Lead │   物理刘海 (不可绘制)│ Trail│             │
   │            └──────┴═══════════════════┴──────┘             │
   │                 ▲ Compact：内容只放在左右"翼"                 │
   │            ┌────────────────────────────────────┐          │
   │            │  Expanded：从刘海下沿整体向下展开       │          │
   │            │  （顶部 Notch 高度区域仍只用左右翼）      │          │
   │            └────────────────────────────────────┘          │
```

- **Compact 宽度** = `notch.Width + leadingWidth + trailingWidth`；高度 = `MenuBarHeight`，顶边对齐屏幕顶边。
- 中央刘海区不放任何内容（会被物理遮挡）；`compact.center` 槽在有刘海的屏幕上**自动禁用**，插件内容回落到左右翼。
- 岛背景在刘海屏上**必须为纯黑**，才能与物理刘海融为一体。实现为自定义 Token `IslandNotchSurface`（在主题包中定义、且主题无法覆盖为非黑），而不是硬编码颜色。
- 外圈两个上角用"反向圆角"（凹角）与菜单栏衔接，视觉上像刘海长出来的。
- Expanded 状态：顶部 `MenuBarHeight` 高度内仍只用左右翼（放标题/关闭按钮），正文从刘海下沿开始。
- 鼠标热区：刘海本身也算热区（用户习惯把鼠标甩到刘海触发）。

### 3.3 无刘海屏的三种放置模式（控制台可选）

| 模式 | 说明 | 默认用于 |
| --- | --- | --- |
| `Attached` 贴顶 | 胶囊顶边贴屏幕顶边，上方直角、下方圆角，像"刘海" | Windows、Linux |
| `Floating` 悬浮 | 距顶 8px 的完整胶囊 | — |
| `BelowMenuBar` 菜单栏下 | macOS 无刘海屏：放在菜单栏下沿，避免遮挡菜单项 | macOS 外接屏 |

额外可调：水平偏移（`ScrubInput`）、垂直偏移、Idle 时宽度、是否在 Idle 时完全隐藏只留 2px 指示条。

### 3.4 Windows / Linux 注意点

- 顶部若有自动隐藏的任务栏（任务栏置顶），用 `SHAppBarMessage(ABM_GETTASKBARPOS)` 避让。
- 高 DPI 与混合 DPI 多屏：所有几何用逻辑像素计算，切屏时按目标屏 `Scale` 重新定位（Per-Monitor V2）。
- Linux GNOME 顶栏（32px）同 macOS 菜单栏处理：检测 `_NET_WORKAREA` 顶部偏移，默认 `BelowMenuBar`。

## 4. 多显示器

设置项 `displayMode`：

- `Primary`：只在主屏（默认）。
- `FollowCursor`：岛跟随鼠标所在屏，切屏时淡出淡入。
- `BuiltIn`：Mac 优先内建刘海屏。
- `All`：每屏一个岛，共享同一活动状态（只有一个可展开）。

## 5. 自动隐藏

- 前台应用全屏（Windows：前台窗口覆盖整个显示器且非桌面；macOS：当前 Space 为全屏；X11：`_NET_WM_STATE_FULLSCREEN`）→ 隐藏，`critical` 活动仍可短暂弹出（可关）。
- 屏幕共享/录屏隐私：Windows `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`、macOS `NSWindow.sharingType = .none`，作为开关。
- 勿扰/专注模式联动：系统勿扰开启时仅显示 `critical`。

## 6. 视觉与动效

- 岛形状：`IslandSurface` 自定义控件（继承 Avalonia `ContentControl`），圆角、尺寸由弹簧动画驱动；过渡定义在 ControlTheme 的 `^:motion-full`（弹簧，阻尼 0.82、响应 0.35s）与 `^:motion-reduced`（120ms 线性淡入淡出），`arc:ArcMotion.Tracks="True"`。
- 内容切换：旧内容缩放 0.96 + 模糊淡出，新内容从 1.04 淡入，与形状动画同时进行。
- 文字变化优先用 Arc `TextMorph`（逐字变形），数字用 `AnimatedCounter`。
- 图标状态切换用 `IconMorph`（播放↔暂停、复制↔对勾）。
- 空闲时不运行任何动画计时器；可见内容每秒最多刷新一次（时钟），音频波形仅在播放且可见时 30fps。

## 7. 岛内的设计要求（必须遵守）

岛是一个很小的黑色表面，桌面应用的那一套"框"放进来会显得突兀。凡是在岛内渲染的内容（宿主界面与插件视图）都遵守：

1. **岛是唯一的容器。** 岛内不出现药丸、带底色的分段控件、带边框的输入框或按钮框、卡片套卡片。需要分组就用间距。
2. **层级靠字重和颜色，不靠框。** 当前 / 主要信息：`IslandForegroundBrush` + 半粗；其余：`IslandSecondaryBrush`。不使用徽章（Badge）表示套餐、状态之类的元信息，改用灰色文字。
3. **选择器是文字。** 标签页是一排文字（`TextTabs`：Arc 幽灵按钮，悬停才出现底色；当前项白色半粗）；翻页的 ‹ › 放在卡片左右两侧、与内容垂直居中（像轮播），是裸图标（`GlyphButton`：灰色，悬停变白，任何时候都没有底框）；页码点是圆点（当前页稍大、强调色）；不常用的选择（如账户）是"当前值 ⌄"的文字触发器，选项放进菜单（Arc `DropdownMenu`）。
4. **颜色留给数据。** 强调色与成功 / 警告 / 危险色只用于数据本身（圆环、进度、告警图标），不用于控件装饰。
5. **不重复。** 已经在标签页里出现的名字，内容区不再重复。
6. **从岛弹出的菜单是独立表面**，可以使用 Arc 的菜单外观。
7. **岛内一律深色**（`ArcThemeScope.Appearance="Dark"`），与系统外观无关。

对插件开放的视图类型中，`segmented`（带底色的分段控件）保留给控制台等岛外场景；岛内请用 `tabs`、`picker`、`iconButton`、`dots`。

> 例外（2026-11-10）：`ui.card` 卡片——深灰填充（#1C1C1E，悬停 #2C2C2E）的圆角块，只用于用户可以"拿起"的对象（文件暂存里的文件、图片），可点击、可拖出；其他内容仍然不加框。
