# Weft 演示

在编辑器里：菜单 **Weft → 打开演示（新场景并进入 Play）**，新建一个场景、放进 `WeftDemo` 并进入 play。也可以自己在场景里放一个物体挂 `WeftDemo`。

可以试的：

- 橙色按钮：悬停变亮（`:hover`）、按下缩小（`:active` 与 `transition`），点击由页面发给 Unity（`weft.engine.send`），Unity 回一个事件确认（`Emit`）。
- 蓝色按钮：页面调用 Unity（`weft.engine.call`），Unity 完成调用（`CompleteCall`），返回的文字里有当前的图形 API。
- 图片框：滚轮缩放一张 `url(stripes.png)` 的图片（页面取消滚轮的默认动作）。
- 指针坐标：页面上任何地方的 CSS px 坐标与按键。
- 底部绿字：Unity 每 0.25 秒推来的视图模型（图形 API、帧率、鼠标是否在页面元素上、光标）。
- 方块上的标签：锚点（`data-anchor="1"`）每帧跟着方块走；点空白处时点击归游戏，方块变色，标签上的计数加一。

换图形 API：Project Settings → Player → Other Settings → Graphics APIs for Windows（或编辑器的启动参数 `-force-d3d11`、`-force-d3d12`、`-force-vulkan`）。换色彩空间（Gamma、Linear）：同一页的 Color Space。

打包时把 `Page` 文件夹拷到 `Assets/StreamingAssets/WeftDemo`（`WeftDemo` 在播放器里从那里读页面），或者在组件上填页面文件夹。
