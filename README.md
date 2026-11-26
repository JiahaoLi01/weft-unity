<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/JiahaoLi01/weft/main/docs/images/weft-logo-dark.gif">
    <img src="https://raw.githubusercontent.com/JiahaoLi01/weft/main/docs/images/weft-logo-light.gif" width="320" alt="Weft">
  </picture>
</p>

<h3 align="center">用 Web，写 Unity 界面。</h3>

<p align="center">
  <strong>Unity 6</strong> · <strong>Windows x64</strong> · <strong>D3D11 / D3D12 / Vulkan</strong> · <strong>C#</strong>
</p>

---

**weft-unity** 将 [Weft](https://github.com/JiahaoLi01/weft) 嵌入式 Web UI 运行时封装为 Unity Package。界面在独立的高栈深工作线程运行，绘制命令直接在 Unity 渲染线程录进当前图形 API 的命令列表。没有独立浏览器进程，没有 CPU 像素拷贝，也不受制于内置管线、URP 或 HDRP。

<p align="center">
  <img src="https://raw.githubusercontent.com/JiahaoLi01/weft/main/docs/images/showcase/pop-jrpg-battle.png" width="49%" alt="JRPG 3D In-Game UI">
  <img src="https://raw.githubusercontent.com/JiahaoLi01/weft/main/docs/images/showcase/4x-strategy.png" width="49%" alt="4X Strategy UI">
</p>

## 为什么是 weft-unity

- **一套包，三大后端。** 原生支持 Direct3D 11、Direct3D 12 与 Vulkan，复用 Unity 自己的设备、命令列表与帧同步，支持全屏叠画与任意 `RenderTexture` 3D 表面材质。
- **零拷贝纹理与自定义着色器。** `SetTexture` 将相机 `RenderTexture` 直接共享给 CSS `-weft-texture()`；`AddEffects` 注册离线编译的 HLSL 像素特效供 `-weft-effect()` 使用；渲染线程直驱 3D 世界锚点（`data-anchor`）。
- **两套输入系统，全量覆盖。** 自动适配新 Input System 与旧 Input Manager，提供指针路由（`Element` / `Cursor`）、键盘焦点、中文输入法候选窗跟随，以及手柄与空间导航。
- **开发连 Chrome，发布干干净净。** 编辑器与 Development 构建使用带 DevTools 的开发版 DLL，内建 **Window → Weft → Inspector** 面板与屏幕性能浮层；打包 Release 播放器时由 `WeftBuildProcessor` 自动替换为纯净发布版 DLL，剔除调试代码与 `WS2_32.dll`。

## 编译与安装

需要 **CMake 3.25+**、**Visual Studio 2022** 与一份 [Weft](https://github.com/JiahaoLi01/weft) 源码（安装 Vulkan SDK 1.4.363.0 时自动包含 Vulkan 后端）。在仓库根目录运行：

```powershell
powershell -File Scripts~/BuildWeft.ps1 -WeftSource F:/Projects/weft -UnityEditor E:/Unity/Editor
```

脚本会编出 Dev / Release 双构型 DLL 并拷贝着色器效果编译工具到 `Tools~/Effects/`。随后在游戏工程的 `Packages/manifest.json` 中以本地路径引入：

```json
"com.jiahaoli.weft": "file:F:/Projects/weft-unity"
```

## 快速上手

```csharp
// 全屏 View：自动跟随屏幕尺寸、接收指针并叠画在画面最上层
var view = gameObject.AddComponent<WeftView>();
view.Load("UI/Hud"); // Assets/StreamingAssets/UI/Hud

// 接收页面消息与异步请求（weft.engine.send / call）
view.MessageReceived += msg =>
{
    if (msg.IsCall && msg.Name == "inventory.load")
        view.CompleteCall(msg.CallId, true, "{\"gold\":12}");
};

// 向页面推送视图模型、事件与 3D 世界锚点
view.SetModel("player", "{\"hp\":73}");
view.Emit("units", json, latest: true);
view.WriteAnchors(anchors);

// 将相机 RenderTexture 暴露给 CSS -weft-texture(minimap)
WeftRuntime.SetTexture("minimap", minimapTexture);

// 与游戏逻辑分流输入
if (!view.PointerOverPage) { /* 鼠标交给游戏 */ }
if (Input.GetKeyDown(KeyCode.Space) && !view.KeyUsed(KeyCode.Space)) { /* 键盘交给游戏 */ }
if (!view.GamepadUsed(0, WeftGamepadButton.A)) { /* 手柄交给游戏 */ }
```

## 文档

| 文档 | 内容 |
| --- | --- |
| [接入与页面开发](Documentation~/getting-started.md) | 构建双 DLL、UPM 安装、页面清单、字体与自定义着色器效果、引擎纹理、`localStorage` 与示例场景 |
| [输入系统](Documentation~/input.md) | 指针路由（`Element` / `Cursor`）、键盘与焦点、中文 IME 输入法、手柄与空间导航 |
| [开发者工具与性能面板](Documentation~/devtools.md) | Dev / Release 自动切包、Chrome DevTools、热重载、Inspector 窗口、屏幕浮层与日志分级 |
| [渲染管线与测试](Documentation~/rendering-and-testing.md) | 线程模型、D3D11 / D3D12 / Vulkan 渲染实现、色彩空间合成、原生与 PlayMode 测试 |

## 目录结构

| 目录 | 说明 |
| --- | --- |
| `package.json` | UPM 包清单 `com.jiahaoli.weft` |
| `Native~/` | 构建 `weft.dll` 与原生测试的 CMake 工程（`~` 目录不会被 Unity 导入） |
| `Scripts~/` | `BuildWeft.ps1`（构建双 DLL 与效果工具）与 `RunTests.ps1`（多 API 自动化测试） |
| `Runtime/` | 运行时程序集 `Weft.Runtime`（`WeftRuntime`、`WeftView`、`WeftPage`、`WeftDebugTools` 等） |
| `Editor/` | 编辑器程序集 `Weft.Editor`（`Window > Weft > Inspector`、`Project Settings > Weft` 与打包切包器） |
| `Tests/` | PlayMode 测试（`Tests/Runtime` 与 `Tests/InputSystem`） |
| `Samples~/Demo` | 交互示例包（在 Package Manager 导入后从菜单 **Weft → 打开演示** 启动） |
| `Documentation~/` | 完整接入与开发文档 |
