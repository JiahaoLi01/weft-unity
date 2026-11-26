# Weft 示例场景

在 Unity 编辑器中，点击菜单 **Weft → 打开演示（新场景并进入 Play）** 即可自动创建演示场景并运行；也可以手动在空物体上挂载 `WeftDemo` 组件。

## 示例演示内容

- **橙色按钮**：`:hover` 悬停高亮、`:active` 缩放过渡；点击后页面经 `weft.engine.send` 发消息给 Unity，Unity 通过 `Emit` 回传确认事件。
- **蓝色按钮**：页面发起异步调用 `weft.engine.call`，Unity 通过 `CompleteCall` 返回当前运行的图形 API 名称。
- **图片框**：加载 `url(stripes.png)`，支持滚轮缩放（页面阻止默认滚动）。
- **实时状态与锚点**：顶部显示 CSS 逻辑坐标与按键状态；底部通过视图模型每 0.25 秒刷新帧率、图形 API 与光标；3D 旋转方块上方附带 `data-anchor="1"` 锚点标签，点击空白区域将事件透传给游戏以切换方块颜色。

## 切换图形 API 与色彩空间

在 **Project Settings → Player → Other Settings** 中调整 **Graphics APIs for Windows**（或启动参数 `-force-d3d11` / `-force-d3d12` / `-force-vulkan`）与 **Color Space**（Gamma / Linear）。

打包独立播放器时，将 `Page` 目录复制到 `Assets/StreamingAssets/WeftDemo` 即可随包加载。
