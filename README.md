# weft-unity

把 [weft](https://github.com/JiahaoLi01/weft)（HTML、CSS、DOM 与 JavaScript 的游戏 UI 运行时）接进 Unity 6 的包。页面在插件自己的线程上运行，帧由 weft 对应当前图形 API 的后端在 Unity 的渲染线程上画进一张渲染纹理：用的是 Unity 自己的 GPU 设备与命令列表，不经 CPU 拷贝，也不另开设备。

当前状态（2026-10-22）：实验版。Windows x64、Unity 6000.0；Direct3D 11、Direct3D 12、Vulkan 三个后端，在 Unity 6000.0.24f1 上三个 API 的测试都通过，读 Input System 的工程在 Direct3D 11 上也通过（weft `input` 分支 c5d612d）。

## 目录

| 位置 | 内容 |
| --- | --- |
| `package.json` | UPM 包 `com.jiahaoli.weft` |
| `Native~/` | 把 weft 和插件自己的 C 代码编成 `weft.dll` 的 CMake 工程：链接 weft 的静态库，只导出 weft 的 C ABI、插件的 `weft_unity_*`（`Native~/include/weft_unity.h`）与 Unity 的 `UnityPluginLoad`/`UnityPluginUnload`（`weft.def` 从头文件生成） |
| `Scripts~/BuildWeft.ps1` | 用一份 weft 签出与 Unity 的插件头文件编译 DLL，放进 `Runtime/Plugins/x86_64`（连同导入它的 `.meta`，都不入库） |
| `Runtime/` | 运行时程序集 `Weft.Runtime`（命名空间 `Weft`；不叫 `Weft`，免得 `Weft.dll` 与原生的 `weft.dll` 同名）：`WeftRuntime`、`WeftView`、`WeftPage`、合成着色器 |
| `Tests/Runtime/` | PlayMode 测试（程序集 `Weft.Tests`） |
| `Tests/InputSystem/` | 读 Input System 的 PlayMode 测试（程序集 `Weft.Tests.InputSystem`，工程里有 Input System 包且启用它时才编译） |
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

// 页面每帧自己推进的东西（施法条、冷却）：只在开始时给它开始的时刻与时长。时刻在页面的时钟上（秒；页面里
// performance.now() 与 requestAnimationFrame 的时间是它的毫秒）
view.Emit("cast", string.Format(CultureInfo.InvariantCulture, "{{\"start\":{0:0.0000},\"duration\":2.4}}", view.PageTime));

// 每帧跟着场景走的元素：锚点（页面写 data-anchor="<Key>"），渲染线程直接移动，不跑脚本、不重排
view.WriteAnchors(anchors);              // WeftAnchor: Key, X, Y（逻辑 px）, Scale, Depth, Flags

// 引擎画的纹理：页面写 background-image: -weft-texture(minimap)，像 CSS 背景图一样铺排
WeftRuntime.SetTexture("minimap", minimapTexture);   // 线性、预乘的 RGBA：相机的 ARGBHalf 渲染纹理

// 游戏判断点击归谁
if (!view.PointerOverPage) { /* 给游戏 */ }

// 键盘：按在页面的元素上时 View 拿到键盘焦点（或者 Focus()），按键给页面的焦点元素，Tab、Enter、空格、方向键照 Chrome；
// 页面没用的键归游戏
view.Focus();
if (Input.GetKeyDown(KeyCode.Space) && !view.KeyUsed(KeyCode.Space)) { /* 给游戏 */ }
```

- 页面目录相对 `Application.streamingAssetsPath`（也可以给绝对路径），所以页面放在 `Assets/StreamingAssets/` 下，打包时原样带上。`WeftPage.FromSource(script, css)` 从字符串做页面。
- 页面里的图片：CSS 写 `background-image: url(../Textures/x.png)`（PNG）。相对路径从页面的基准（样式表的路径）算起，插件直接从文件系统读。读不到或不是 PNG 时是一条页面诊断，那个背景不画图。
- 页面的诊断（脚本异常、样式做不出场景、缺图）打成警告，也经 `DiagnosticReceived` 给游戏。
- 不是全屏的 View（`FullScreen = false`、`Size`）只画进 `view.Texture`，由游戏去显示（`RawImage` 或材质，用 `WeftComposite.Material`），指针事件由游戏用 `view.Pointer(...)` 给它，命中用 `view.HitTest(...)`。
- 字体：默认是 Windows 的系统字体。要加字体文件，在第一个页面载入之前：`WeftRuntime.Start(); WeftRuntime.AddFont("Noto Serif SC", path, 400);`（比如 `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` 里）。
- 一次运行（play）一个 weft 运行时：退出 play、域重载、退出游戏时停掉，下一次 play 从新的运行时开始（关掉域重载时也一样）。
- 键盘见下一节。
- 引擎纹理：`SetTexture` 绑的是纹理本身（GPU 上直接采样，不经 CPU），每帧画进去的新内容页面都看得到。要在 WeftView 的 `LateUpdate` 排进页面的绘制之前画好它：相机设成不自动渲染、在执行顺序早于 WeftView 的脚本的 `LateUpdate` 里 `Camera.Render()`，这样页面、纹理与锚点是同一帧的。纹理是 Unity 的（首行在下），weft 把它翻正；重建了纹理（改大小）就再绑一次，传 `null` 解绑，停掉运行时会解绑全部名字。支持的格式：RGBA16F、RGBA8（线性）、RGBA32F。

## 键盘

- 焦点：一次一个 View 有键盘焦点（`WeftView.FocusedView`），像浏览器里一个 iframe 有焦点。读键盘的 View（`Keyboard`，默认开）在鼠标按在它页面的元素上时拿到焦点，按在页面元素以外（游戏上）时还给游戏；`Focus()`、`Blur()` 由游戏给与收回。页面像浏览器窗口那样知道焦点：拿到时 window 的 focus 与焦点元素的 focus（`:focus`、`:focus-visible`、`document.hasFocus()` 只在有焦点时成立），失去时 blur；游戏窗口失去系统焦点时同样，这时按着的键算抬起（页面不收 keyup，同浏览器）。
- 读键盘：有焦点、读键盘的 View 在每帧脚本的 `Update` 之前（PlayerLoop 的 Update 开头）收到这一帧的键：工程用 Input System 时从它的事件（`InputSystem.onEvent`：按键状态与文字按到来的顺序），否则从 Input Manager（这一帧按下的键，先不打字的键，再这一帧的文字 `Input.inputString`，再抬起的键）。code 是物理键：Input Manager 要开着 Use Physical Keys（Unity 的默认）。key 是系统键盘布局用这个键打出的字符（`WeftKeys.KeyValue`），keyCode 由 weft 按布局推出。两套输入都不给系统的自动重复，插件照 Windows 做：最后按下的键按住时，过系统的键盘延迟后按系统的速度重复（`repeat` 为真）。
- 页面用没用：`view.KeyUsed(...)`（code、`KeyCode`，或 Input System 的 `Key`）说这一帧页面用了这个键：监听器阻止了它（`preventDefault`），weft 对它执行了默认动作（移了焦点、滚动、点击、编辑文本框），或输入法为页面的文本框吃掉了它。游戏跳过这样的键，同 Chrome 把页面没用的键还给浏览器。键在脚本的 `Update` 之前读，所以 `Update` 里就知道。空格在页面上滚动是由它的文字（keypress）做的，也算在空格上；可编辑文本框里打字的键按下时就算页面用了（玩家在聊天框里打字时角色不动）。
- 游戏自己给键：`Keyboard = false` 的 View 不读键盘，游戏用 `Key(...)`、`Text(...)` 给它（比如画在物体上、游戏自己判断点击的 View），用 `Focus()` 让它的页面知道有焦点。`WeftKeys.Code(KeyCode)`、Input System 的 `Key.WeftCode()` 给出 Unity 的键的 code。

## 文本框与输入法

- 页面的 `<input>`（文字类型）与 `<textarea>` 照 Chrome 编辑（weft 做：打字、选择、鼠标、事件）。`Pointer(...)` 带修饰键（默认取键盘按着的），Shift+点击扩展选区。
- `view.TextInput`（`WeftTextInput`）说文字往哪里去：页面里可编辑的文本框有焦点、View 有键盘焦点时 `Active`，`Composing`（输入法正在组合）、`Password`，光标与文本框的矩形（View 的逻辑像素，左上原点）。
- 输入法：读键盘的 View 在 `TextInput.Active` 且不是密码框时打开输入法（Input Manager 的 `Input.imeCompositionMode = On`，离开时恢复原来的；Input System 的 `Keyboard.SetIMEEnabled`），全屏 View 把候选窗放在光标下面（`Input.compositionCursorPos`、`Keyboard.SetIMECursorPosition`；画在物体上的 View 由游戏按它怎么显示纹理来放）。组合串变化时给页面（compositionstart、compositionupdate、input），上屏的文字结束组合（compositionend），不发 keypress，同 Chrome。Input Manager 从 `Input.compositionString` 读组合串，组合之后那一帧的文字是上屏的文字：`Input.inputString` 只有按键打出的 ASCII 字符（Unity 的文档如此，单独来的 `WM_CHAR` 它也不收），所以文本框有焦点时，这一帧的文字取 IMGUI 的键盘事件（`Event.PopEvent`，同 UGUI 的输入框在有焦点时那样取走它们，这期间 OnGUI 收不到这些事件）；输入法开着时，这一帧没有按键却来了文字（微软拼音把标点直接上屏，Unity 也不报它的键按下）也当作上屏的文字；Input System 从 `IMECompositionEvent` 读组合串，组合中（或组合刚结束、下一帧之前）的文字事件是上屏的文字。组合进行时按下的键（和开始组合的那个键）是输入法的：页面收到 key 为 "Process"、keyCode 229 的 keydown，`KeyUsed` 为真；按住时的重复同样。
- 游戏自己读输入法时（`Keyboard = false`）：`Compose(text, cursor)`、`CommitText(text)`、`Key(..., ime: true)`。
- 在宣传页的 Unity 播放器（Input Manager）里用真实的微软拼音试过（2026-10-22）：组合串、候选窗在光标下面、空格上屏、组合中的 Enter 只上屏字母（页面不当作发送）。标点直接上屏的那条（48b79ad）只有插件测试，还没在播放器里复测。

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
- 每帧：`Update` 开头（脚本的 `Update` 之前）把键盘给有焦点的 View；`Update` 里送指针、推进页面、分发消息；`LateUpdate` 把 View 最新的帧排进绘制队列（`weft_unity_queue_draw`，返回绘制号），再用 `CommandBuffer.IssuePluginEventAndData` 把绘制号交给渲染线程的插件事件。每帧调的不分配托管内存：`Emit`、`SetModel`、`CompleteCall` 把字符串编码进 View 自己的缓冲（够大之后不再分配），命中测试的光标名字节不变就是上一次的字符串。渲染线程按 Unity 的图形 API 用一个 `RenderDevice`：
  - D3D11：weft 的 D3D11 后端在 Unity 的即时上下文上画（目标的 RTV 用 UNORM 格式现做），用的是插件自己的上下文状态（`SwapDeviceContextState`），画完换回 Unity 的：Unity 缓存着即时上下文的状态，不会重新设置它以为已经设好的视口、着色器与资源。
  - D3D12：`IUnityGraphicsD3D12v8` 的 `CommandRecordingState` 给出 Unity 正在录的命令列表，目标先 `RequestResourceState` 到 render target；录制在 GPU 上完成与否看 Unity 的帧 fence（`GetNextFrameFenceValue` 与 fence 的完成值）。
  - Vulkan：`IUnityGraphicsVulkanV2` 的 `AccessTexture` 把目标转到 color attachment，事件配置成在 render pass 外，录进 Unity 的命令缓冲；完成看 `safeFrameNumber`。目标的 image view 由插件做、帧安全后销毁。
- weft 的渲染器只认创建它的线程：它在渲染线程上建。一个 weft 后端只画一个运行时的 View，所以运行时换了（新的 play）就换一个渲染器，旧的等它的录制都完成后销毁。
- 渲染纹理是 8 位 BGRA UNORM（不能做渲染目标时用 RGBA），每帧先清空。weft 写的是 sRGB 编码、预乘、首行在上的像素：`WeftComposite` 的着色器把它翻正、预乘混合；线性色彩空间的工程里先解码成线性（半透明边缘的混合因此在线性空间里做，和浏览器略有不同）。全屏 View 在 OnGUI 里合成时打开 sRGB 写入：IMGUI 在 D3D11、D3D12 上关着它，否则解码后的颜色原样写进屏幕，整页偏暗。

## 测试

```powershell
powershell -File Scripts~/RunTests.ps1                     # 原生测试，再在 d3d11、d3d12、vulkan 上各跑一遍 PlayMode 测试
powershell -File Scripts~/RunTests.ps1 -Apis d3d12
powershell -File Scripts~/RunTests.ps1 -NativeOnly         # 只跑原生测试，不需要 Unity
powershell -File Scripts~/RunTests.ps1 -Apis d3d11 -InputSystem   # 在读 Input System 的工程里跑 PlayMode 测试
```

- 原生测试（`Native~/tests/plugin_tests.cpp`，`BuildWeft.ps1` 一起编）用替身的 Unity 接口在真实的 D3D11、D3D12 设备上驱动 `weft.dll`：Unity 的渲染线程、D3D12 的帧（命令列表、帧 fence、`RequestResourceState` 的状态跟踪）都由测试自己扮演。查画出来的像素、消息往返、指针与命中、键盘（Tab、Enter、文字、窗口焦点到页面的焦点元素）、画不进的目标（记一次失败，不崩溃）、D3D11 的绘制之后 Unity 的视口不变、引擎纹理在先后几个 View 里正着显示、停掉再起的运行时（换一个渲染器，旧的等录制完成后销毁）。装了 Windows 的 Graphics Tools 时打开 D3D11/D3D12 调试层，调试层报的错误算失败。每一步有时限，卡住时报出卡在哪一步。
- PlayMode 测试（`Tests/Runtime`）在批处理模式的 Unity 里跑，每个图形 API 一遍（`-force-d3d11`、`-force-d3d12`、`-force-vulkan`），工程是线性色彩空间（`PrebuildSetup` 进入 play 之前设好）。测试画一个页面并读回像素（位置、清空、首行在上）、合成后的方向与线性解码、像 OnGUI 那样关着 sRGB 写入合成到屏幕、引擎纹理（相机画的纹理在页面里正着显示、颜色按线性解码，解绑后不画，相机在 weft 画过之后照常渲染）、事件/视图模型/调用的往返、指针点击与命中、`url()` 图片与缺图诊断、脚本异常、全屏 View 跟随屏幕大小、停掉再起的运行时；键盘（`WeftKeyboardTests`：测试代替设备给键）：Tab 移焦点、Enter 点击、页面没用与阻止的键（`KeyUsed`、阻止的键的文字丢掉）、按下给焦点与收回、焦点在 View 间移动、按系统速度的重复、修饰键与锁定键、窗口焦点、游戏自己给的键；文本框与输入法：`TextInput`、打字、拼音的组合与上屏（开始组合的键在组合事件之前、组合先结束再来文字）、"Process" 键与 `KeyUsed`、密码框不开输入法（Input Manager 时查 `Input.imeCompositionMode`）、游戏自己给的组合与上屏。
- `-InputSystem` 在另一个工程（`<ProjectDir>-input-system`）里跑：它有 Input System 包、Active Input Handling 是 Input System（脚本第一次打开工程生成设置后改好），全部测试再跑一遍，另加 `WeftInputSystemTests`（Input System 测试框架的键盘：状态事件与文字事件按顺序到页面、修饰键、`KeyUsed(Key)`、没有焦点时键归游戏；输入法：文本框有焦点时打开、候选窗在光标下面并跟着光标、`IMECompositionEvent` 的组合与上屏、焦点回到游戏时关掉）。结果在 `Build~/Tests/<api>-input-system.xml`。
- 测试工程由脚本生成在临时目录（`-ProjectDir`，默认 `%TEMP%/weft-unity-tests`），引用本仓库的包：路径里有 `~` 结尾的文件夹时，Unity 的测试框架找不到它自己的脚本，进入 play 后一直不开始测试。结果与日志在 `Build~/Tests/<api>.xml`、`.log`。批处理模式需要有效的 Unity 许可证。

## 限制

- 只有 Windows x64；macOS（Metal）、Linux、主机平台等 weft 有对应的后端。
- 输入有鼠标（移动、按键、滚轮、离开）、键盘与输入法，来自 Input System 或旧的 Input Manager；触摸与手柄还没有。右 Alt 一律是 Alt（AltGr 打出的字符照常从文字到页面，keydown 的 key 不是 AltGraph）。
- 同步函数（`weft.engine.invoke`）还没有接。
- 页面里的图片只有 PNG 背景图（`url()`），没有 `<img>`、JPEG 与 `data:` URL。
- 全屏 View 在 OnGUI 里合成；URP、HDRP 下同样在所有相机之后画。
