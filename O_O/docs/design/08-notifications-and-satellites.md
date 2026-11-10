# 08 · 通知监听与卫星岛（草案）

> **岛 API 2.0（2026-11-10）**：岛上显示什么、插件怎么写，以 [09-island-v2.md](09-island-v2.md) 为准；本文里的
> `island.page` / `island.chip` / `island.satellite` / `island.activity.create` 等 1.x 接口已移除。


目标：让插件能拿到其他应用（微信、QQ 等）的通知，并在主岛旁边的独立小岛里显示，图标为**消息来源应用的图标**。
本文只定义提供给插件的能力；平台实现见 §6。

## 1. 设计原则

1. **不碰第三方客户端**：只读系统通知和 Dock / 托盘角标，不注入、不 Hook、不用第三方协议。
2. **按来源授权**：权限带来源参数，插件只能监听自己声明的应用，和 `network:<域名>` 同一风格。
3. **插件拿不到像素**：来源图标由宿主解析和绘制，插件只拿到一个引用（同 `ui.image` 的约定）。
4. **隐私在宿主层兜底**：用户可在控制台全局关闭消息正文，此时插件收到的 `body` 恒为 `null`，插件无法绕过。
5. **卫星岛是通用能力**：任何插件都可以声明，不绑定消息插件。

## 2. 权限

| 权限 | 含义 |
| --- | --- |
| `notifications.listen:<来源ID>` | 接收指定应用的通知。来源 ID 为 macOS bundle id / Windows AppUserModelId / Linux desktop-entry |
| `notifications.listen:badge:<来源ID>` | 只读该应用的未读角标数（不含内容），要求更低 |

- 与 `localAuth:*` 一致：**永远需要用户确认**，内置插件也不例外。
- 不允许通配（`notifications.listen:*` 会让清单被拒绝）；一个插件最多声明 8 个来源。
- 同时持有 `notifications.listen` 和 `network` 的插件，授权对话框额外提示“该插件可以把消息发往网络”。
- 宿主维护一份已知来源表（`com.tencent.xinWeChat` → 微信，`com.tencent.qq` → QQ 等）用于对话框显示名称和兜底图标。

## 3. `@island/api`：`notifications` 模块

只有持有权限时才导出，撤销权限后立即消失（与现有模块一致）。

```js
import { notifications, island, ui } from '@island/api';

/** 订阅。filter.apps 必须是已授权来源的子集；返回取消订阅函数。 */
const stop = notifications.onReceived((n) => { /* ... */ }, { apps: ['com.tencent.xinWeChat'] });

/** 最近的通知（仅内存，最多 50 条，重启即清空）。 */
const recent = await notifications.list({ app: 'com.tencent.qq', limit: 10 });

/** 未读角标数；来源不提供时为 null。 */
const unread = await notifications.unread('com.tencent.xinWeChat');

/** 把来源应用调到前台（并尽量定位到该会话）。不需要 shell 权限，只能作用于已授权来源。 */
await notifications.activate(n.id);

/** 按应用 id 调到前台：未读数来自 Dock 角标、本次运行还没收到过它的通知时用。 */
await notifications.activateApp('com.tencent.qq');

/** 监听角标变化。 */
notifications.onBadge((appId, count) => { /* ... */ });
```

### 3.1 通知对象

```ts
interface AppNotification {
  id: string;                 // 宿主生成，用于 activate / 去重
  app: { id: string; name: string };   // 来源；图标用 ui.appIcon(app.id) 显示
  title: string;              // 通常是发送人或群名
  subtitle: string | null;
  body: string | null;        // 正文；按用户的“消息显示”档位（§7）裁剪，或来源本身不提供预览时为 null
  threadId: string | null;    // 会话标识（平台提供时），用于合并同一会话
  time: number;               // Unix 毫秒
  source: 'notification' | 'badge';  // badge：只有计数变化，title/body 为空
}
```

### 3.2 `ui.appIcon`（新 VNode）

```js
ui.appIcon(appId, { size: 20 })
```

- 宿主按来源 ID 取真实应用图标（macOS `NSWorkspace`；Windows 应用图标；Linux 图标主题）并缓存。
- 取不到时依次回落：内置品牌图标（微信、QQ）→ 来源名首字母圆标。
- 渲染图标不泄露任何信息（插件看不到像素，也看不到用户装了什么），所以不按来源授权，任何来源 ID 都可以显示。
- 线上格式 `{ t: 'appIcon', p: { appId, size } }`，`VNodeRenderer` 新增映射，次版本号 +1。

## 4. 卫星岛：`island.satellite`

### 4.1 清单

```jsonc
"contributes": {
  "satellites": [
    { "id": "message", "title": "消息提醒", "side": "auto" }   // auto | left | right
  ]
}
```

### 4.2 API（无需额外权限，属于基础能力）

```js
const sat = island.satellite('message');

sat.show(
  ui.row({ gap: 'sm', align: 'center' }, [
    ui.appIcon('com.tencent.xinWeChat', { size: 20 }),
    ui.stack({}, [ui.text(n.title, { weight: 'semibold', lines: 1 }),
                  ui.text(n.body ?? '新消息', { tone: 'secondary', lines: 1 })]),
  ]),
  {
    key: n.threadId ?? n.app.id,   // 同 key 原地更新，不同 key 排队
    seconds: 5,                    // 1–10，默认 4；悬停时暂停
    badge: 3,                      // 图标右上角计数，可省略
    onClick: () => notifications.activate(n.id),
  },
);

sat.hide('key');   // 收回某一条；不传 key 收回全部
```

### 4.3 宿主规则

- **位置**：`auto` 时放在主岛靠屏幕内侧的一边，默认右侧；右侧空间不足（主岛偏右、或会盖住菜单栏图标区以外的内容）时翻到左侧。用户可在控制台为每个卫星岛固定左 / 右。
- **尺寸**：高度等于收起态主岛高度，宽度最大 240pt；超出的文字用 `lines` 截断。内容限于 `row / stack / text / icon / appIcon / image / badge`，不允许输入控件。
- **队列**：同 key 合并更新；不同 key 最多同时显示 2 个（并排或轮播），其余排队。同一来源 5 秒内的多条合并为“+N”角标。
- **动画**：从主岛边缘滑出，到时间后收回，遵循主岛现有的 spring token。
- **抑制**：全屏应用、系统勿扰、用户在控制台关闭该卫星岛时不显示（插件调用不报错，只是被丢弃；`show` 返回 `Promise<boolean>` 表示是否真正显示）。
- **限流**：每个插件每分钟最多 30 次 `show`，超出丢弃最旧的。
- **与主岛关系**：卫星岛不抢占主岛，也不影响主岛展开；主岛展开时卫星岛收回，收起后若仍有未读则恢复。

## 5. 内置插件 `com.o_o.im` 的清单示例

```jsonc
{
  "id": "com.o_o.im",
  "name": "即时消息",
  "permissions": [
    "notifications.listen:com.tencent.xinWeChat",
    "notifications.listen:com.tencent.qq"
  ],
  "permissionReasons": {
    "notifications.listen:com.tencent.xinWeChat": "读取微信的系统通知，在岛旁边显示",
    "notifications.listen:com.tencent.qq": "读取 QQ 的系统通知，在岛旁边显示"
  },
  "contributes": {
    "satellites": [{ "id": "message", "title": "消息提醒", "side": "auto" }],
    "chips": [{ "id": "unread", "title": "未读消息数", "side": "right" }],
    "settings": { "properties": {
      "mute": { "type": "string", "title": "免打扰（会话名，逗号分隔）", "default": "" },
      "seconds": { "type": "integer", "title": "显示秒数", "minimum": 1, "maximum": 10, "default": 4 }
    } }
  }
}
```

## 6. 平台实现

`Island.Platform.Abstractions` 新增：

```csharp
public interface INotificationListener : IDisposable
{
    NotificationListenerCapabilities Capabilities { get; }   // Content / Badge / Icon，缺哪项由宿主降级
    event Action<PlatformNotification> Received;             // AppId, AppName, Title, Subtitle, Body, ThreadId, Time
    event Action<string, int?> BadgeChanged;                 // AppId, count
    Task<PermissionState> RequestAccessAsync();              // 引导用户开启系统权限
    byte[]? GetAppIcon(string appId, int size);              // 供宿主绘制，永不交给插件
    void Activate(PlatformNotification n);
}
```

| 平台 | 通知内容 | 角标 | 所需系统权限 |
| --- | --- | --- | --- |
| macOS | 读通知中心数据库（macOS 15+ 路径与权限收紧，需先实机验证）；备选：辅助功能监听通知横幅 | 辅助功能读 Dock 图标的 `AXStatusLabel` | 完全磁盘访问 / 辅助功能 |
| Windows | 常驻 PowerShell 调 `UserNotificationListener`（不引入 WinRT 投影，见风险 R5）；未打包应用能否获得授权需实机验证，备选读 `wpndatabase.db` | 无统一接口，不提供 | 通知访问（设置 → 隐私 → 通知） |
| Linux | `dbus-monitor` 观察会话总线上的 `Notify` 调用（只观察，需系统装有 dbus-monitor） | 无 | 无 |

**来源 ID 归一**：插件清单里写的是 macOS bundle id（如 `com.tencent.xinWeChat`）。`KnownApps` 把各平台的原生 ID（Linux desktop-entry、Windows AppUserModelId）映射到这些规范 ID，所以同一份清单在三个系统上都能匹配微信、QQ、企业微信、Telegram；不在表里的应用保留原生 ID。

`Capabilities` 缺失时的降级：无 `Content` 只回调 `source: 'badge'`；无 `Icon` 走 §3.2 的回落链。

## 7. 隐私与安全

- 通知只在内存环形缓冲中，不写磁盘、不进日志；控制台的日志视图对 `body` 打码。
- **显示内容由用户配置**（控制台 → 隐私 → “消息显示”），三档：
  - `full`：发送人 + 正文；
  - `senderOnly`：发送人 + “1 条新消息”，`body` 置空；
  - `hidden`：只显示来源图标和计数，`title`、`body` 都置空。

  可设全局默认值，也可按来源（微信、QQ 各自）覆盖，还可单独设“锁屏 / 屏幕共享时自动降为 `hidden`”。宿主在投递给插件**之前**按档位裁剪字段，插件和卫星岛都只能看到裁剪后的内容，插件无法绕过。默认值为 `senderOnly`，首次授权时在授权对话框里让用户选一档。
- 事件按来源过滤发生在宿主，插件拿不到未授权来源的任何通知。
- 无平台权限时 `onReceived` 不会触发，`notifications.status()` 返回 `{ granted: false, reason }`，插件可据此提示用户。

## 8. 待定

1. ~~默认是否显示消息正文~~：已定为用户可配置（§7），默认 `senderOnly`，首次授权时询问。
2. macOS 15+ 实机能读到什么，决定通知内容和角标哪一条为主路径。
3. 微信“折叠 N 条消息”这类聚合通知的 `threadId` 是否可靠。
4. `satellites` 是否要支持 `activity` 式的持续显示（例如倒计时），v1 先只做瞬时提醒。
