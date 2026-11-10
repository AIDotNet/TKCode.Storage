# 08 · 版本更新与插件目录

> 状态：2026-11-10 定稿并实现（客户端）。服务端是单独的项目（`~/code/O_O.Service`），地址固定为
> `https://O_O.astra-gate.si`，只写在 `src/Island.Core/Updates/ServiceEndpoints.cs` 一处；开发时可用环境变量
> `O_O_SERVICE_URL`（如 `http://localhost:5080`）指向本机服务。

## 1. 版本号

`主.次.构建`，例如 `0.1.1000`。逐段按数字比较（`0.1.1000` > `0.1.999`）；缺少的段按 0；`+提交号`、`-后缀`忽略。
默认版本写在 `Directory.Build.props`（开发构建报告 `0.1.1000`），CI 发布时用 `-p:Version=…` 覆盖。

## 2. 版本检测

`GET {base}/api/version?platform={macos-arm64|macos-x64|windows-x64|windows-arm64|linux-x64|linux-arm64}&current={版本}`

```json
{
  "version": "0.1.1001",
  "publishedAt": "2026-11-10T08:00:00Z",
  "notes": "更新说明",
  "downloads": { "macos-arm64": "https://…/O_O-0.1.1001-macos-arm64.dmg", "windows-x64": "https://…" },
  "download": "https://… 或 null（所问平台的下载地址）",
  "updateAvailable": true
}
```

- 服务不托管安装包，只返回下载地址（目前是 GitHub Release 的附件）。客户端只认 https 下载地址。
- 客户端：启动约 20 秒后检查一次，之后每 24 小时一次（控制台"关于 → 自动检查更新"，默认开）；"检查更新"按钮随时检查。
  是否更新由客户端自己比较版本决定（不只看 `updateAvailable`）。
- 有新版本时：岛上提醒一次（每个版本只提醒一次），"关于"页显示新版本、说明和"下载新版本"（在浏览器中打开下载地址）。
  **从不自动下载或安装。** 网络错误不打扰，只在"关于"页显示"无法连接更新服务"。

## 3. 插件目录

内置插件（随安装包发布，`plugins/`）：AI 用量、Claude、Codex、剪贴板历史、文件暂存、待办、专注、天气、系统监控、
即时消息、岛布局预设、四个主题包。其余在 `catalog/plugins/`，不进安装包，由服务的插件目录提供：DeepSeek、Kimi、
NewAPI、日程及各日历来源（CalDAV、ICS、Google、飞书、钉钉、系统日历）、音乐、GitHub、时钟、邮件。

`catalog/pack.sh [输出目录]` 按安装时的规则校验并打包全部目录插件为 `<id>-<版本>.zip`（默认 `build/out/catalog/`），
交给服务发布。

`GET {base}/api/plugins?platform={macos|windows|linux}`

```json
{
  "plugins": [
    {
      "id": "com.o_o.cal.ics", "name": "日历订阅（ICS）", "version": "0.1.0",
      "description": "订阅 ICS 日历链接", "icon": "Link",
      "permissions": ["network:@url1"], "requires": ["com.o_o.calendar"], "platforms": [],
      "size": 12345, "sha256": "小写十六进制", "download": "https://O_O.astra-gate.si/plugins/com.o_o.cal.ics-0.1.0.zip"
    }
  ]
}
```

- `platforms` 为空表示所有系统；客户端只显示本机系统的插件。
- 安装：下载（https；开发时允许本机 http）→ 大小上限 20 MB → 校验 `sha256` → 按普通插件包安装（和用户自己的插件一样，
  运行前请求权限）→ 先安装 `requires` 里尚未安装的插件。记录"来自 O_O 插件目录"（`catalog-installs.json`）。
- 更新：目录里的版本比已安装的新时，插件详情抽屉里显示"更新到 x.y.z"。

## 4. 清单新字段

- `requires`：本插件依赖的插件 id（如日历来源依赖 `com.o_o.calendar`）。
- `platforms`：`macos` / `windows` / `linux` 的子集，空为全部。不属于本机系统的插件不会加载，也不能安装。
