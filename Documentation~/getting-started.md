# 接入与页面开发

本文介绍如何编译安装 `weft-unity`、加载页面，以及使用字体清单、自定义 UI 着色器效果、图片、引擎纹理和 `localStorage`。

## 1. 编译与安装

构建环境需要 **CMake 3.25+**、**Visual Studio 2022**、本地 [Weft](https://github.com/JiahaoLi01/weft) 源码以及 Unity 编辑器目录（读取 `Data/PluginAPI` 头文件；安装 Vulkan SDK 1.4.363.0 时自动包含 Vulkan 后端）。

```powershell
powershell -File Scripts~/BuildWeft.ps1 -WeftSource F:/Projects/weft -UnityEditor E:/Unity/Editor
```

如果不指定 `-WeftSource`，脚本会依次查找环境变量 `WEFT_SOURCE_DIR` 与本仓库同级的 `weft` 目录；不指定 `-UnityEditor` 时查找 `UNITY_EDITOR_DIR` 或默认路径 `E:/Unity/Editor`。

> Unity 编辑器加载原生 DLL 后会持续占用文件句柄；重新编译前需先关闭加载了本包的 Unity 编辑器。

脚本生成的产物（均在 `.gitignore` 中，不随源码入库）：

| 路径 | 用途 |
| --- | --- |
| `Runtime/Plugins/x86_64/weft.dll` | 开发版 DLL（`WEFT_DEVTOOLS=ON`），供编辑器、PlayMode 与 Development 构建使用 |
| `Runtime/Plugins~/Release/x86_64/weft.dll` | 发布版 DLL（`WEFT_DEVTOOLS=OFF`），无调试代码与 `WS2_32.dll`；打 Release 播放器时自动替换 |
| `Tools~/Effects/` | 离线效果编译脚本（`compile_effect.cmake`、`check_effect.cmake`、`scene.hlsl`） |

由于预编译 DLL 不入库（Unity 通过 git URL 安装到 `Library/PackageCache` 时目录为只读），请克隆本仓库并完成编译后，在游戏项目的 `Packages/manifest.json` 中通过本地路径引用：

```json
{
  "dependencies": {
    "com.jiahaoli.weft": "file:F:/Projects/weft-unity"
  }
}
```

## 2. 创建 View 与加载页面

在任意 GameObject 上添加 `WeftView` 组件即可使用。页面目录默认相对于 `Application.streamingAssetsPath`（也支持绝对路径），建议将页面放在 `Assets/StreamingAssets/` 下，打包时自动原样随包发布。

```csharp
var view = gameObject.AddComponent<WeftView>();
view.Load("UI/Hud");
```

也可以直接从内存字符串构造页面：

```csharp
var page = WeftPage.FromSource(jsCode, cssCode, baseDirectory);
page.ForgivingCss = true;
page.WebPlatform = true;
view.Load(page);
```

页面目录支持清单模式（`page.json`）或默认模式（`page.js` + `page.css` + 可选 `keyframes.css`）：

```json
{
  "scripts": ["vendor.js", "page.js"],
  "stylesheet": "page.css",
  "keyframes": "keyframes.css",
  "root": "#root",
  "forgivingCss": true,
  "webPlatform": true,
  "spatialNavigation": false
}
```

### 全屏模式与离屏纹理模式

- **全屏模式（`FullScreen = true`，默认）**：自动跟随 `Screen.width × Screen.height`，在 `Update` 中读取指针，并在 `OnGUI` 中通过 `Order` 排序叠画在最上层（兼容内置管线、URP 与 HDRP）。
- **纹理模式（`FullScreen = false`）**：按 `Size` 渲染到 `view.Texture`，交由 `RawImage` 或 3D 表面材质（使用 `WeftComposite.Material`）自由摆放；指针由游戏调用 `view.Pointer(...)` 与 `view.HitTest(...)` 传入。

### 生命周期

每次进入 Play Mode 对应一个独立的 Weft 运行时；退出 Play Mode、触发 Domain Reload 或退出应用时会自动调用 `WeftRuntime.Stop()` 清理资源（即使关闭了 Domain Reload 也能正确重启新运行时）。

## 3. 宿主通信与锚点

```csharp
// 页面 -> Unity：监听 weft.engine.send 与 weft.engine.call
view.MessageReceived += msg =>
{
    if (msg.IsCall && msg.Name == "shop.buy")
    {
        view.CompleteCall(msg.CallId, true, "{\"ok\":true}");
    }
};

// Unity -> 页面：事件与视图模型（均为零托管分配复用缓冲区）
view.Emit("combat.hit", "{\"damage\":42}", latest: true);
view.SetModel("player", "{\"hp\":73,\"mp\":120}");

// 3D 世界锚点：页面元素写 data-anchor="1"，渲染线程直接定位，不触发脚本与重排
view.WriteAnchors(anchors);
```

对于冷却倒计时、进度条等连续动效，推荐只在开始时传递起始时间与时长。起始时间使用 `view.PageTime`（单位秒，与页面内的 `performance.now()` 和 `requestAnimationFrame` 同源）。

> 锚点元素的尺寸仍由 CSS 布局决定，通常需要为其指定明确宽度并设置 `position: absolute`。

## 4. 字体与自定义着色器效果

### 字体清单

默认自动加载 Windows 系统字体及回退链。需要注册自定义字体时，在第一个页面加载前（例如 `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`）启动运行时并加载 `fonts.json`：

```csharp
WeftRuntime.Start();
WeftRuntime.AddFonts("UI/fonts.json");
```

`fonts.json` 格式与 `weft-unreal` 一致，支持字重、斜体、字体集索引、`unicodeRange` 与全局回退链：

```json
{
  "faces": [
    { "family": "GameUI", "path": "NotoSansSC-Regular.otf", "weight": 400 },
    { "family": "GameUI", "path": "Inter-Bold.ttf", "weight": 700, "unicodeRange": "U+0020-007F" }
  ],
  "fallback": ["GameUI"]
}
```

也可以逐条调用 `WeftRuntime.AddFont(family, path, weight, italic, faceIndex, unicodeRange)`、`SetGenericFamily` 与 `AddFallbackFamily`。

### 自定义 UI 着色器效果（`-weft-effect`）

使用 `BuildWeft.ps1` 复制出来的脚本离线编译 HLSL 像素效果：

```powershell
cmake -DSOURCE=path/to/foil.hlsl -DOUTPUT=Assets/StreamingAssets/UI/Effects/foil -P Tools~/Effects/compile_effect.cmake
```

在运行时加载 `effects.json` 清单：

```csharp
WeftRuntime.EnsureStarted();
WeftRuntime.AddEffects("UI/Effects/effects.json");
```

之后页面即可使用 `background-image: -weft-effect(foil)`，并通过 CSS 自定义属性控制着色器参数。同一运行时内重复注册同名效果会自动跳过。

## 5. 图片资源与引擎纹理

### 页面图片（`url()`）

CSS 中的 `background-image: url(../Textures/icon.png)` 基于样式表路径解析并从文件系统直接读取（PNG / JPEG / WebP）。缺失文件或无效图像会触发 `WeftDiagnosticKind.Scene` 诊断，该元素跳过背景图绘制。

### 引擎纹理（`-weft-texture`）

通过 `WeftRuntime.SetTexture` 将 Unity 的 `RenderTexture` 或纹理直接暴露给 GPU 渲染器：

```csharp
WeftRuntime.SetTexture("minimap", minimapRenderTexture);
```

页面中写 `background-image: -weft-texture(minimap)` 即可将其作为标准 CSS 背景图渲染（支持圆角裁切、混合与遮罩）：

- **格式要求**：线性预乘 RGBA；推荐在所有图形 API 上使用 `RenderTextureFormat.ARGBHalf`（也支持线性 RGBA8 与 RGBA32F）。
- **每帧更新**：纹理以 live 方式绑定，不经 CPU 拷贝。将渲染它的相机设为手动渲染，并在早于 `WeftView.LateUpdate` 的脚本中调用 `camera.Render()`，可确保 3D 纹理、锚点与界面完全同帧。
- **生命周期**：纹理重建（如尺寸变化）后需再次调用 `SetTexture`；传入 `null` 解绑；运行时停止时自动清空全部绑定。

## 6. 持久化存储（`localStorage`）

在开启 `webPlatform: true` 的页面中，可通过宿主 API 恢复或保存该 View 的 `localStorage`：

```csharp
// 加载页面前恢复存档
view.SetStorageItem("locale", "zh-CN");

// 仅当修订号变化时导出最新键值对
var items = new Dictionary<string, string>();
if (view.GetStorage(ref savedRevision, items))
{
    SaveUiStorage(items);
}

// 清空该 View 的存储
view.ClearStorage();
```

## 7. 示例场景

在 Unity Package Manager 中选择 **Weft → Samples → Demo → Import**，随后点击顶部菜单 **Weft → 打开演示（新场景并进入 Play）** 即可体验按钮交互、异步调用、滚轮缩放图片、实时视图模型、3D 锚点与输入穿透。

也可在不打开 Unity 的情况下，用原生测试工具直接校验页面是否能正常加载以及点击后的消息输出：

```powershell
Build~/Native/Release/weft_unity_tests.exe --page Samples~/Demo/Page --click 93,127
```
