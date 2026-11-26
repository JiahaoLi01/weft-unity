# 渲染管线与测试

## 1. 线程模型与零分配设计

- **Owner 工作线程**：React、D3 等复杂页面需要 8 MiB 以上栈空间，而 Unity 主线程默认仅 1 MiB。插件在原生层维护一条 16 MiB 栈深的专用 Owner 线程承载 Weft 运行时；需要 Owner 线程的调用同步派发执行，事件、视图模型、异步调用完成与锚点写入则直接走 Weft 任意线程接口。
- **每帧顺序**：PlayerLoop `Update` 开头先处理键盘与手柄输入 → `WeftView.Update` 轮询指针、推进页面并分发消息 → `WeftView.LateUpdate` 将最新帧快照排入绘制队列并通过 `CommandBuffer.IssuePluginEventAndData` 通知渲染线程。
- **托管零分配**：高频调用的 `Emit`、`SetModel`、`CompleteCall` 复用 View 内部 UTF-8 编码缓冲区，命中测试在光标名未变化时直接复用字符串实例，稳定运行期间每帧零 GC 分配。

## 2. 三大图形 API 渲染实现

渲染线程根据 Unity 当前图形 API 创建对应的 `RenderDevice`（切换运行时时自动创建新渲染器，旧渲染器待 GPU 完成全部录制后安全销毁）：

| 图形 API | 录制方式 | 状态保护与同步 |
| --- | --- | --- |
| **Direct3D 11** | 在 Unity 即时上下文直接录制，按目标格式创建 UNORM RTV | 使用 `SwapDeviceContextState` 隔离状态，绘制完成后切回 Unity 状态，避免破坏 Unity 的状态缓存 |
| **Direct3D 12** | 通过 `IUnityGraphicsD3D12v8.CommandRecordingState` 获取当前命令列表 | `RequestResourceState` 切至 Render Target；通过帧 Fence（`GetNextFrameFenceValue`）追踪 GPU 完成 |
| **Vulkan** | 通过 `IUnityGraphicsVulkanV2.AccessTexture` 将目标切至 Color Attachment，在 Render Pass 外录进当前命令缓冲 | 通过 `safeFrameNumber` 追踪回收并延迟销毁临时 `VkImageView` |

### 纹理格式与色彩空间合成

- `WeftSurface` 优先分配 8 位 `B8G8R8A8_UNorm`（不支持时回退至 `R8G8B8A8_UNorm`），Weft 输出 sRGB 编码、预乘 Alpha、首行在上的图像。
- `WeftComposite` 着色器在合成时自动翻转行序并做预乘混合；在 Linear 色彩空间项目中自动解码为线性值。
- 全屏模式在 `OnGUI` 绘制时会显式打开 `GL.sRGBWrite`（IMGUI 在 D3D11 / D3D12 下默认关闭它），确保 Linear 工程中界面亮度准确不偏暗。

## 3. 自动化测试

运行测试前请先执行 `Scripts~/BuildWeft.ps1`。`Scripts~/RunTests.ps1` 会先运行原生测试，再在临时目录生成独立测试工程（避开路径中的 `~` 文件夹导致 Unity Test Framework 丢失脚本的问题）并逐个 API 执行批处理 PlayMode 测试：

```powershell
# 原生测试 + D3D11 / D3D12 / Vulkan 全套 PlayMode 测试
powershell -File Scripts~/RunTests.ps1

# 指定图形 API
powershell -File Scripts~/RunTests.ps1 -Apis d3d12

# 仅运行原生测试（不需要 Unity）
powershell -File Scripts~/RunTests.ps1 -NativeOnly

# 使用 Input System 工程运行 PlayMode 测试
powershell -File Scripts~/RunTests.ps1 -Apis d3d11 -InputSystem
```

测试结果 XML 与完整日志输出至 `Build~/Tests/`。

### 覆盖范围

- **原生测试（`Native~/tests/plugin_tests.cpp`）**：通过替身 Unity 接口直接在真实 D3D11 / D3D12 设备（开启调试层）上验证像素输出、上下文状态保护、资源状态转换、消息往返、指针/键盘/IME、引擎纹理、异常目标容错、`localStorage`、脚本求值、帧信息、渲染计时、效果注册与 DevTools 启停。
- **PlayMode 测试（`Tests/Runtime` & `Tests/InputSystem`）**：在 Linear 色彩空间下覆盖像素读取与合成、IMGUI 屏幕合成、引擎纹理双向采样、消息/模型/调用/锚点、指针与 `PointerRouting`、键盘与 IME（含 Input System 专用测试）、手柄与空间导航（含 XInput 映射）、DevTools HTTP 接口（`/json/list`、`/json/version`）、View 性能表与日志级别、`localStorage` 跨页持久化和字体/效果清单加载。

## 4. 当前边界与限制

- **平台**：当前支持 Windows x64（macOS Metal、Linux 与主机平台待接入对应后端）。
- **输入**：支持鼠标、键盘、IME 与手柄（Input Manager 下手柄仅支持 Windows XInput）；暂未接入触控与手柄震动；右 Alt 统一作为 `Alt` 派发（AltGr 输入字符照常通过文本事件送达）。
- **桥接**：暂未暴露同步函数 `weft.engine.invoke`，推荐使用异步 `weft.engine.call`。
- **渲染顺序**：全屏 View 经由 `OnGUI` 在所有相机之后统一合成（内置管线、URP、HDRP 均适用）。
