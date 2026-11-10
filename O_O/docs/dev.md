# 开发说明

## 构建与运行

```bash
dotnet build O_O.slnx
dotnet test tests/Island.Core.Tests
dotnet run --project src/Island.App
```

SDK 由 `global.json` 固定为 .NET 10。

## 调试用环境变量

| 变量 | 作用 |
| --- | --- |
| `O_O_DEBUG=1` | 在 stderr 输出屏幕、刘海、布局与原生窗口状态 |
| `O_O_FAKE_NOTCH=185x32` | 假装当前屏幕有居中的刘海（宽×高，pt），用于在无刘海的机器上检查刘海布局 |
| `O_O_PLACEMENT=Attached` | 强制放置模式（覆盖"岛体"设置）：`Auto` / `Attached` / `Floating` / `BelowMenuBar` / `Capsule` |
| `O_O_STATE=Expanded` | 启动后保持展开 |
| `O_O_DEMO=1` | 每 2 秒在 展开 → 紧凑 → 空闲 → 紧凑 间循环，用于截图与检查动画 |
| `O_O_INSTANCE=名称` | 单实例锁用的名字；开发时另起一个互不影响的实例（否则第二次启动会交给已运行的 O_O 并退出） |
| `O_O_DEBUG_WINDOW_PNG=路径` | 启动后把岛窗口（含模板）离屏渲染成 PNG，检查透明与形状 |
| `O_O_DEBUG_WINDOW_PNG_DELAY=秒` | 上一项的延迟（默认 2 秒），用来截取动画结束后的画面 |
| `O_O_DEBUG_TRAY_PNG=路径` | 把菜单栏 / 托盘图标渲染成 PNG |
| `O_O_FAKE_FULLSCREEN=1` | 假装前台有全屏应用，检查"全屏时隐藏" |
| `O_O_DEBUG_WALLPAPER=1` | 启动时只把"从壁纸取色"会选的颜色打印到 stderr（不应用、不保存） |

命令行：`--probe-fullscreen x y w h` 用当前平台的全屏检测判断给定矩形（调试检测逻辑）；`--pack-plugin <目录> [输出.zip]` 校验并打包插件（见下文"插件"）。

## 启动性能

Debug（JIT）启动约 10 秒，主要是 Arc 的 `UseArcSpecial()` 和 Jaunt 的 JIT；这不代表发布版本。`O_O_DEBUG=1` 会打印 `[trace]` 启动各阶段耗时（从进程启动算起）。测发布性能请用 NativeAOT：

```bash
dotnet publish src/Island.App -c Release -r osx-arm64 -p:PublishAot=true -o out/osx-arm64
```

## 离屏渲染控制台

屏幕截图不可用时（无显示器、远程会话），可把控制台渲染成 PNG 检查界面。它会用临时数据目录启动内置插件，并把 `samples/plugins` 当作用户插件安装，以便看到授权流程：

```bash
src/Island.App/bin/Debug/net10.0/O_O --render-console out.png plugins light   # 页面：plugins / appearance / about；light / dark
```

岛本身也可以离屏渲染（运行内置插件和 `samples/plugins` 里的示例插件，展开或紧凑状态，可加 `attached` 看贴顶外形）：

```bash
src/Island.App/bin/Debug/net10.0/O_O --render-island out.png expanded [attached]   # 或 compact
```

`samples/plugins/com.example.demo-usage` 提供固定的用量数据，便于不登录任何账户就预览"AI 用量"。

`O_O_RENDER_GLASS=1` 配合 `--render-console` 可在彩色背景上预览控制台的玻璃层次（离屏渲染无法捕获真实的系统材质）。

`O_O_RENDER_PLUGIN=<插件 id>` 配合 `--render-console … plugins` 打开该插件的详情抽屉；`O_O_RENDER_PLUGIN=+add` 打开"添加插件"对话框。`--render-island` 时 `O_O_RENDER_ACTIVITY=<插件 id>` 切到该插件的页面，`O_O_RENDER_DROP=路径1:路径2` 先把文件拖进文件暂存。

默认按 1 倍渲染；`O_O_RENDER_SCALE=2` 更清晰，但部分 Arc 组件的合成层在离屏 2 倍渲染时会被重复缩放（错位、放大），这是渲染工具的限制，不代表界面有问题。

## 生成应用图标

图标由应用自身按 `src/Island.App/AppLogo.cs` 的矢量图形绘制（`src/Island.App/Tools/IconRenderer.cs`），修改 Logo 后重新生成：

```bash
dotnet build src/Island.App && src/Island.App/bin/Debug/net10.0/O_O --render-icon assets/icon
```

输出各尺寸 PNG、`O_O.ico`（Windows 可执行文件图标）与 `O_O.icns`（macOS 打包时使用）。

## 插件

第一方插件在 `plugins/`，构建时复制到输出目录。用户插件放在 `~/Library/Application Support/O_O/plugins/<id>/`（Windows `%APPDATA%\O_O\plugins`），同 id 时覆盖内置插件。


插件以 `.zip` 安装（控制台 → 插件 → 添加插件）。打包：`O_O --pack-plugin <插件目录> [输出.zip]`（开发时：`dotnet run --project src/Island.App -- --pack-plugin <目录>`），会按安装时的规则校验清单、权限、设置与入口文件，并排除 `.git`、`node_modules` 等。编写插件的完整指南见 Claude Code 技能 `.claude/skills/o_o-plugin/SKILL.md`（附最小模板）。

## 工程结构

见 `docs/design/01-architecture.md` §2。

## macOS 发布（签名、公证、安装包）

`build/macos/package.sh --arch arm64|x64 --version X.Y.Z [--identity "Developer ID Application: …"] [--notarize]`
用 NativeAOT 发布、组装 `O_O.app`（`Info.plist`：`com.aidotnet.o-o`，无 Dock 图标；内置插件在
`Contents/Resources/plugins`）、逐个签名（hardened runtime，权限见 `build/macos/O_O.entitlements`）、打包
`build/out/O_O-<版本>-macos-<架构>.dmg`，带 `--notarize` 时用 `APPLE_ID` / `APPLE_APP_SPECIFIC_PASSWORD` /
`APPLE_TEAM_ID` 公证并装订。不带 `--identity` 时为临时签名，只能本机试用（hardened runtime 下临时签名的库无法
通过库校验，本机试运行时需去掉 `--options runtime`）。Intel 版在 Apple 芯片的机器上交叉编译即可。

CI：仓库根目录 `.github/workflows/o_o-macos-release.yml`。推送标签 `o_o-v1.2.3` 会构建两个架构、签名、公证，
并创建 GitHub Release 附上 `.dmg`；也可在 Actions 页手动运行（只产出构建产物）。需要的仓库 Secrets：
`MACOS_CERTIFICATE`（Developer ID Application 证书 .p12 的 base64）、`MACOS_CERTIFICATE_PASSWORD`、`APPLE_ID`、
`APPLE_APP_SPECIFIC_PASSWORD`；变量 `APPLE_TEAM_ID`。

