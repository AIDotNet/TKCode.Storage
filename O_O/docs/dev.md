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
| `O_O_PLACEMENT=Attached` | 强制放置模式：`Auto` / `Attached` / `Floating` / `BelowMenuBar`（控制台完成前的临时入口） |
| `O_O_STATE=Expanded` | 启动后保持展开 |
| `O_O_DEMO=1` | 每 2 秒在 展开 → 紧凑 → 空闲 → 紧凑 间循环，用于截图与检查动画 |

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

默认按 1 倍渲染；`O_O_RENDER_SCALE=2` 更清晰，但部分 Arc 组件的合成层在离屏 2 倍渲染时会被重复缩放（错位、放大），这是渲染工具的限制，不代表界面有问题。

## 生成应用图标

图标由应用自身用 Arc `AiMascot` 绘制（`src/Island.App/Tools/IconRenderer.cs`），修改吉祥物或主题后重新生成：

```bash
dotnet build src/Island.App && src/Island.App/bin/Debug/net10.0/O_O --render-icon assets/icon
```

输出各尺寸 PNG、`O_O.ico`（Windows 可执行文件图标）与 `O_O.icns`（macOS 打包时使用）。

## 插件

第一方插件在 `plugins/`，构建时复制到输出目录。用户插件放在 `~/Library/Application Support/O_O/plugins/<id>/`（Windows `%APPDATA%\O_O\plugins`），同 id 时覆盖内置插件。

## 工程结构

见 `docs/design/01-architecture.md` §2。
