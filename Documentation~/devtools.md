# 开发者工具与性能面板

`weft-unity` 对齐了 `weft-unreal` 的完整调试工具链：支持 Dev / Release 双构型自动打包切换、Chrome DevTools 远程审查、热重载、Unity 编辑器 Inspector 窗口与屏幕实时性能浮层。

## 1. Dev / Release 双构型与打包切换

`Scripts~/BuildWeft.ps1` 同时构建两份 ABI 完全相同的 DLL：

| 构型 | 产物位置 | 适用目标 |
| --- | --- | --- |
| **Dev**（`WEFT_DEVTOOLS=ON`） | `Runtime/Plugins/x86_64/weft.dll` | Unity 编辑器、PlayMode 与勾选 Development Build 的 Windows x64 播放器 |
| **Release**（`WEFT_DEVTOOLS=OFF`） | `Runtime/Plugins~/Release/x86_64/weft.dll` | 非 Development 的 Release Windows x64 播放器（无调试代码与 `WS2_32.dll`） |

打包 Windows x64 播放器时，`Editor/WeftBuildProcessor.cs` 会自动检查构建选项：

- **Development Build**：保留 Dev 版 `weft.dll`，可通过命令行参数或代码随时打开 Chrome DevTools。
- **Release Build（默认）**：自动将输出目录中的 `weft.dll` 替换为纯净的 Release 版。

如需在某个 Release 测试包中临时保留 DevTools，二选一：

1. 在编辑器打开 **Project Settings → Weft**，勾选 **Include DevTools in Release Builds**（保存在 `ProjectSettings/WeftSettings.json`）。
2. 在自动化打包命令行附加 `-WeftDevToolsInRelease`。

运行时可通过 `WeftRuntime.HasDevTools` 查询当前加载的是否为开发版 DLL。

## 2. 启动与连接 Chrome DevTools

开发版 DLL 默认不监听端口，可按需通过以下任意方式启动：

- **编辑器窗口**：打开 **Window → Weft → Inspector**，输入可选监听地址（留空或 `0` 表示从 `9230` 起自动选取首个空闲端口；`0.0.0.0:9230` 表示允许局域网设备连接），点击 **Start**。
- **C# 代码**：

  ```csharp
  if (WeftDebugTools.StartDevTools("9230", out var error))
  {
      Debug.Log($"DevTools listening on {WeftDebugTools.DevToolsAddress}");
  }
  // 停止监听
  WeftDebugTools.StopDevTools();
  ```

- **命令行参数**：启动播放器时附加 `-WeftDevTools`、`-WeftDevTools=9240` 或 `-WeftDevTools=0.0.0.0:9230`（附加 `-WeftDebug` 可同时开启屏幕浮层）。
- **环境变量**：设置 `WEFT_DEVTOOLS_LISTEN=9230` 或 `0.0.0.0:9230`。

监听开启后，Unity Console 会打印监听地址以及每个已加载页面的调试链接：

```text
Weft DevTools: [Hud] devtools://devtools/bundled/devtools_app.html?ws=127.0.0.1:9230/devtools/page/1-1
```

在编辑器 Inspector 窗口点击对应行的 **Copy address** 粘贴到 Chrome 地址栏，或打开 `chrome://inspect` → `Configure` 添加 `127.0.0.1:9230`，即可实时检查 DOM、计算样式、盒模型、Console 日志与页面拾取。

### 热重载

配合 Weft 主仓库的监视脚本，本地修改 CSS / JS 保存后即热更新运行中的 Unity 游戏界面：

```sh
node path/to/weft/tools/devtools_watch.mjs 127.0.0.1:9230 Assets/StreamingAssets/UI/Hud
```

## 3. Inspector 窗口与屏幕性能浮层

- **编辑器窗口**：**Window → Weft → Inspector**，每 0.5 秒刷新所有活跃 `WeftView` 的实时状态，支持一键启停 DevTools、复制页面调试链接和勾选 **On screen** 浮层。
- **屏幕浮层**：代码中调用 `WeftDebugTools.ShowOverlay(true)` 或启动播放器时传入 `-WeftDebug`，即可在游戏画面左上方叠加显示性能表格。
- **程序化查询**：调用 `WeftDebugTools.GetViews(list)` 获取结构化数据，或用 `WeftDebugTools.FormatViews(list)` 输出对齐文本表。

| 列 | 含义 |
| --- | --- |
| `View` | DevTools 目标标识（`<slot>-<generation>`） |
| `Page` | 页面名称（`WeftView.PageName`，样式表所在目录名） |
| `Size` / `Scale` | 逻辑尺寸（CSS px）与设备像素比 |
| `Elements` | 文档 DOM 元素总数（最多每秒统计一次） |
| `Game ms` | 主线程 Weft 耗时（推进、输入、命中与提交绘制），过去 1 秒平均 / 最慢帧 |
| `Draw ms` | 渲染线程录制耗时，过去 1 秒平均 / 最慢帧 |
| `Animated` | 页面是否存在持续动画（CSS 动画/过渡或 `requestAnimationFrame`） |
| `Errors` / `Warnings` | 页面加载以来的异常/错误数与样式/警告数 |

性能计时仅在 Inspector 窗口打开、屏幕浮层开启或调用 `GetViews` 后的 2 秒窗口内激活，平时主线程与渲染线程均不产生额外计时开销。

## 4. 日志分级

`WeftView` 将页面内部诊断按严重程度路由到 Unity Console（前缀统一为 `weft [<PageName>]:`），同时触发 `DiagnosticReceived` 事件：

| 诊断类型 | Unity Console 级别 | 计数归属 |
| --- | --- | --- |
| `WeftDiagnosticKind.Console`（`console.log` / `info` / `debug`） | `Debug.Log` | — |
| `WeftDiagnosticKind.ConsoleWarning`、`WeftDiagnosticKind.Scene`、超出上限丢弃的诊断 | `Debug.LogWarning` | `WarningCount` |
| `Script`、`Handler`、`Frame`、`ConsoleError`、资源超限中止 | `Debug.LogError` | `ErrorCount` |

关闭 `WeftView` 上的 **Log Diagnostics** 可静音控制台打印，但 `ErrorCount`、`WarningCount` 与 `DiagnosticReceived` 仍正常更新。
