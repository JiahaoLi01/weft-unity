# weft-unity

把 [weft](https://github.com/JiahaoLi01/weft)（HTML、CSS、DOM 与 JavaScript 的游戏 UI 运行时）接进 Unity 6 的包。页面在插件自己的线程上运行，帧由 weft 对应当前图形 API 的后端在 Unity 的渲染线程上画进一张渲染纹理：用的是 Unity 自己的 GPU 设备与命令列表，不经 CPU 拷贝，也不另开设备。

当前状态（2026-10-17）：实验版。Windows x64、Unity 6000.0；Direct3D 11、Direct3D 12、Vulkan 三个后端，在 Unity 6000.0.24f1 上三个 API 的测试都通过（weft main 3379d90）。

## 目录

| 位置 | 内容 |
| --- | --- |
| `package.json` | UPM 包 `com.jiahaoli.weft` |
| `Native~/` | 把 weft 和插件自己的 C 代码编成 `weft.dll` 的 CMake 工程：链接 weft 的静态库，只导出 weft 的 C ABI、插件的 `weft_unity_*`（`Native~/include/weft_unity.h`）与 Unity 的 `UnityPluginLoad`/`UnityPluginUnload`（`weft.def` 从头文件生成） |
| `Scripts~/BuildWeft.ps1` | 用一份 weft 签出与 Unity 的插件头文件编译 DLL，放进 `Runtime/Plugins/x86_64`（连同导入它的 `.meta`，都不入库） |
| `Runtime/` | 运行时程序集 `Weft.Runtime`（命名空间 `Weft`；不叫 `Weft`，免得 `Weft.dll` 与原生的 `weft.dll` 同名）：`WeftRuntime`、`WeftView`、`WeftPage`、合成着色器 |
| `Tests/Runtime/` | PlayMode 测试（程序集 `Weft.Tests`） |
| `Samples~/Demo` | 示例：手动试的全屏页面（Package Manager 里导入，菜单 Weft → 打开演示） |
| `Native~/tests/` | 原生测试：用替身 Unity 接口在 D3D11 设备上驱动 `weft.dll`，不需要 Unity |
| `Scripts~/RunTests.ps1` | 先跑原生测试，再在批处理模式下逐个图形 API 跑 PlayMode 测试（测试工程由脚本生成） |

`~` 结尾的文件夹（`Native~`、`Scripts~`、`Samples~`、编译用的 `Build~`）Unity 不导入，所以包里只有 `Runtime/` 与 `Tests/` 是资源（各带 `.meta`）。

## 编译 weft.dll

需要 CMake 3.25+、Visual Studio 2022；装了 Vulkan SDK 1.4.363.0 时连 Vulkan 后端一起编。

```powershell
powershell -File Scripts~/BuildWeft.ps1 -WeftSource F:/Projects/weft -UnityEditor E:/Unity/Editor
```

不给 `-WeftSource` 时找环境变量 `WEFT_SOURCE_DIR`，再找本仓库旁边的 `weft` 目录；不给 `-UnityEditor` 时找 `UNITY_EDITOR_DIR`，再找 `E:/Unity/Editor`（要它的 `Data/PluginAPI`）。编辑器加载 DLL 后不再放开它：重编前关掉用这个包的编辑器。

## 安装

DLL 不入库，所以包要从一份签出装（git URL 装进的 `Library/PackageCache` 是只读的，放不进编好的 DLL）：克隆本仓库，编译 `weft.dll`，在工程的 `Packages/manifest.json` 里写

```json
"com.jiahaoli.weft": "file:F:/Projects/weft-unity"
```

## 用法

```csharp
// 一个全屏 View：跟着屏幕大小、接鼠标、画在所有东西上面（OnGUI）
var view = gameObject.AddComponent<WeftView>();
view.Load("UI/Hud");                     // StreamingAssets/UI/Hud：page.json，或 page.js 加 page.css、keyframes.css
view.MessageReceived += m =>             // weft.engine.send / weft.engine.call
{
    if (m.IsCall) view.CompleteCall(m.CallId, true, "{\"gold\":12}");
};

// 低频数据：事件与视图模型（JSON）
view.Emit("units", json, latest: true);              // weft.engine.on('units', ...)
view.SetModel("camera", "{\"tier\":\"Mid\"}");        // weft.engine.model('camera')

// 每帧跟着场景走的元素：锚点（页面写 data-anchor="<Key>"），渲染线程直接移动，不跑脚本、不重排
view.WriteAnchors(anchors);              // WeftAnchor: Key, X, Y（逻辑 px）, Scale, Depth, Flags

// 游戏判断点击归谁
if (!view.PointerOverPage) { /* 给游戏 */ }
```

- 页面目录相对 `Application.streamingAssetsPath`（也可以给绝对路径），所以页面放在 `Assets/StreamingAssets/` 下，打包时原样带上。`WeftPage.FromSource(script, css)` 从字符串做页面。
- 页面里的图片：CSS 写 `background-image: url(../Textures/x.png)`（PNG）。相对路径从页面的基准（样式表的路径）算起，插件直接从文件系统读。读不到或不是 PNG 时是一条页面诊断，那个背景不画图。
- 页面的诊断（脚本异常、样式做不出场景、缺图）打成警告，也经 `DiagnosticReceived` 给游戏。
- 不是全屏的 View（`FullScreen = false`、`Size`）只画进 `view.Texture`，由游戏去显示（`RawImage` 或材质，用 `WeftComposite.Material`），指针事件由游戏用 `view.Pointer(...)` 给它，命中用 `view.HitTest(...)`。
- 字体：默认是 Windows 的系统字体。要加字体文件，在第一个页面载入之前：`WeftRuntime.Start(); WeftRuntime.AddFont("Noto Serif SC", path, 400);`（比如 `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` 里）。
- 一次运行（play）一个 weft 运行时：退出 play、域重载、退出游戏时停掉，下一次 play 从新的运行时开始（关掉域重载时也一样）。

## 示例

Package Manager 里选 Weft → Samples → Demo → Import，然后菜单 **Weft → 打开演示（新场景并进入 Play）**：一个全屏页面，可以试按钮的悬停、按下与点击（Unity 回事件确认）、页面调用 Unity（`weft.engine.call`）、滚轮缩放一张 `url()` 图片、指针坐标、Unity 推来的视图模型（图形 API、帧率、鼠标是否在页面元素上），以及跟着一个转动方块走的锚点标签；点页面元素以外的地方时点击归游戏，方块变色。说明见示例里的 `README.md`。

## 写页面时注意

weft 的 scene 页面只支持 CSS 的一部分（见 weft 的 `docs/compatibility.md`）。目前踩到的：

- 过渡：weft e0aced1 起支持 `transition` 简写，时长等列表与 `transition-property` 一一对应，样式写错时是诊断。更早的 weft 上要写分开的属性、只给一个时长：简写会让整页载入失败（`weft_view_load` 只返回 invalid argument），时长列表会在样式生效时成为场景诊断（整帧不画）。演示的样式按更早的写法写，两种 weft 都能用。
- 锚点元素（`data-anchor`）的大小由布局决定：给它宽度并 `position: absolute`，否则它是占满一行的块。

检查一个页面能不能载入、点击后发什么消息，不用开 Unity：

```powershell
Build~/Native/Release/weft_unity_tests.exe --page Samples~/Demo/Page --click 93,127
```

## 线程与渲染

- weft 的页面要 8 MiB 以上的栈（React、D3），Unity 主线程只有 1 MiB：插件开一个 16 MiB 栈的线程（owner 线程）跑运行时，`weft_unity_*` 里要 owner 的函数把工作交给它并等它做完。事件、视图模型、调用的完成与锚点是 weft 的"任意线程"函数，直接调。
- 每帧：`Update` 里送指针、推进页面、分发消息；`LateUpdate` 把 View 最新的帧排进绘制队列（`weft_unity_queue_draw`，返回绘制号），再用 `CommandBuffer.IssuePluginEventAndData` 把绘制号交给渲染线程的插件事件。渲染线程按 Unity 的图形 API 用一个 `RenderDevice`：
  - D3D11：weft 的 D3D11 后端在 Unity 的即时上下文上画（目标的 RTV 用 UNORM 格式现做）。
  - D3D12：`IUnityGraphicsD3D12v8` 的 `CommandRecordingState` 给出 Unity 正在录的命令列表，目标先 `RequestResourceState` 到 render target；录制在 GPU 上完成与否看 Unity 的帧 fence（`GetNextFrameFenceValue` 与 fence 的完成值）。
  - Vulkan：`IUnityGraphicsVulkanV2` 的 `AccessTexture` 把目标转到 color attachment，事件配置成在 render pass 外，录进 Unity 的命令缓冲；完成看 `safeFrameNumber`。目标的 image view 由插件做、帧安全后销毁。
- weft 的渲染器只认创建它的线程：它在渲染线程上建。一个 weft 后端只画一个运行时的 View，所以运行时换了（新的 play）就换一个渲染器，旧的等它的录制都完成后销毁。
- 渲染纹理是 8 位 BGRA UNORM（不能做渲染目标时用 RGBA），每帧先清空。weft 写的是 sRGB 编码、预乘、首行在上的像素：`WeftComposite` 的着色器把它翻正、预乘混合；线性色彩空间的工程里先解码成线性（半透明边缘的混合因此在线性空间里做，和浏览器略有不同）。

## 测试

```powershell
powershell -File Scripts~/RunTests.ps1                     # 原生测试，再在 d3d11、d3d12、vulkan 上各跑一遍 PlayMode 测试
powershell -File Scripts~/RunTests.ps1 -Apis d3d12
powershell -File Scripts~/RunTests.ps1 -NativeOnly         # 只跑原生测试，不需要 Unity
```

- 原生测试（`Native~/tests/plugin_tests.cpp`，`BuildWeft.ps1` 一起编）用替身的 Unity 接口在真实的 D3D11、D3D12 设备上驱动 `weft.dll`：Unity 的渲染线程、D3D12 的帧（命令列表、帧 fence、`RequestResourceState` 的状态跟踪）都由测试自己扮演。查画出来的像素、消息往返、指针与命中、画不进的目标（记一次失败，不崩溃）、停掉再起的运行时（换一个渲染器，旧的等录制完成后销毁）。装了 Windows 的 Graphics Tools 时打开 D3D11/D3D12 调试层，调试层报的错误算失败。每一步有时限，卡住时报出卡在哪一步。
- PlayMode 测试（`Tests/Runtime`）在批处理模式的 Unity 里跑，每个图形 API 一遍（`-force-d3d11`、`-force-d3d12`、`-force-vulkan`）。测试画一个页面并读回像素（位置、清空、首行在上）、合成后的方向与线性解码、事件/视图模型/调用的往返、指针点击与命中、`url()` 图片与缺图诊断、脚本异常、全屏 View 跟随屏幕大小、停掉再起的运行时。
- 测试工程由脚本生成在临时目录（`-ProjectDir`，默认 `%TEMP%/weft-unity-tests`），引用本仓库的包：路径里有 `~` 结尾的文件夹时，Unity 的测试框架找不到它自己的脚本，进入 play 后一直不开始测试。结果与日志在 `Build~/Tests/<api>.xml`、`.log`。批处理模式需要有效的 Unity 许可证。

## 限制

- 只有 Windows x64；macOS（Metal）、Linux、主机平台等 weft 有对应的后端。
- 输入只有鼠标（移动、按键、滚轮、离开），来自 Input System 或旧的 Input Manager；键盘、IME、触摸与手柄还没有。
- 同步函数（`weft.engine.invoke`）与引擎纹理（`-weft-texture()`）还没有接。
- 页面里的图片只有 PNG 背景图（`url()`），没有 `<img>`、JPEG 与 `data:` URL。
- 全屏 View 在 OnGUI 里合成；URP、HDRP 下同样在所有相机之后画。
