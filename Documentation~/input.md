# 输入系统

`weft-unity` 自动适配 Unity 的 **Input System**（检测到 `com.unity.inputsystem >= 1.8.0` 且已启用时生效）与传统 **Input Manager**，覆盖指针路由、键盘焦点、中文输入法与手柄空间导航。

## 1. 指针路由

全屏 `WeftView` 每帧自动轮询鼠标位置、按键与滚轮，并更新 `PointerOverPage` 与 `Cursor`。可通过 `PointerRouting` 控制哪些元素拦截游戏点击：

| 模式 | 行为 | 适用场景 |
| --- | --- | --- |
| `WeftPointerRouting.Element`（默认） | 指针位于除 `#root` 外的任意页面元素上时 `PointerOverPage` 为 `true` | 常规面板、对话框、菜单 |
| `WeftPointerRouting.Cursor` | 仅当命中元素的 CSS `cursor` 为可交互值（非 `auto`、`default`、`none`）时 `PointerOverPage` 为 `true` | 全屏 HUD 中带有非交互视觉层，空白处点击需透传给游戏世界 |

游戏侧通过 `if (!view.PointerOverPage)` 判断点击是否归游戏处理。对于离屏或 3D 表面上的 `WeftView`（`FullScreen = false`），由游戏自行做射线求交后调用 `view.Pointer(...)` 与 `view.HitTest(...)`。

## 2. 键盘与焦点

同一时间最多只有一个 `WeftView` 持有键盘焦点（`WeftView.FocusedView`）：

- **获取与释放**：当 `Keyboard = true`（默认）时，鼠标点击页面元素会自动将焦点交给该 View；点击空白区域则调用 `Blur()` 将焦点归还游戏。也可以主动调用 `view.Focus()` / `view.Blur()`。
- **窗口焦点联动**：游戏窗口失去系统焦点时，当前页面自动收到 `blur`，同时清空已按下的键位状态，防止切回窗口后卡键。
- **执行时机**：键盘输入在每帧 PlayerLoop 的 `Update` 开头（所有业务脚本 `Update` 之前）处理，因此游戏逻辑在 `Update` 里即可立即查询 `view.KeyUsed(...)`：

```csharp
if (Input.GetKeyDown(KeyCode.Space) && !view.KeyUsed(KeyCode.Space))
{
    Jump();
}
```

使用 Input System 时，支持通过 `Key.WeftCode()` 与 `view.KeyUsed(Key)` 配合检查：

- 若页面脚本调用了 `preventDefault()`，或 Weft 执行了默认交互（`Tab` 切焦点、`Enter` / `Space` 点击按钮、方向键滚动、文本框编辑），或中文输入法拦截了按键，`KeyUsed` 在当帧返回 `true`。
- 物理键码基于系统键盘布局自动计算字符值与 `keyCode`；按住按键时遵循 Windows 系统延迟与重复速率生成 `repeat` 事件。
- 若由游戏自行接管键盘（`Keyboard = false`），可通过 `view.Key(...)` 与 `view.Text(...)` 手动喂入事件。

## 3. 文本框与中文输入法（IME）

页面中的文本 `<input>` 与 `<textarea>` 原生支持选区、剪贴板快捷键与 `Shift + 点击` 扩选。

- **自动开关**：当焦点位于可编辑的非密码输入框且 View 持有键盘焦点时，插件自动开启系统输入法（Input Manager 下切换 `Input.imeCompositionMode`，Input System 下调用 `Keyboard.SetIMEEnabled`）；切出输入框或进入密码框时自动恢复关闭。
- **候选窗定位**：全屏 View 自动将系统候选窗定位在输入框光标正下方（`Input.compositionCursorPos` / `Keyboard.SetIMECursorPosition`）。离屏或 3D 界面可通过 `view.TextInput` 读取逻辑坐标自行换算屏幕位置。
- **组合与上屏**：
  - **Input System**：直接读取 `IMECompositionEvent` 派发 `compositionstart`、`compositionupdate`、`input` 与 `compositionend`。
  - **Input Manager**：读取 `Input.compositionString`，并在文本框聚焦期间通过 IMGUI 事件队列收取非 ASCII 上屏字符与直接上屏的中文标点（此期间 `OnGUI` 不会重复收到这些键盘事件）。
  - 输入法组合期间按下的键以 `key = "Process"`、`keyCode = 229` 派发，且 `KeyUsed` 返回 `true`，避免组合中的回车或字母误触游戏操作。

如果游戏自行接管输入法（`Keyboard = false`），可调用 `view.Compose(text, cursor)`、`view.CommitText(text)` 与 `view.Key(..., ime: true)`。

## 4. 手柄与空间导航

### Gamepad API

当 `Gamepads = true`（默认）时，插件每帧在 `Update` 之前读取最多 4 个手柄，按标准 Gamepad 布局同步给所有页面（支持 `navigator.getGamepads()`、`gamepadconnected` 与 `gamepaddisconnected`）：

- **Input System**：读取 `Gamepad.all`，按物理面键位置映射（南 A、东 B、西 X、北 Y），扳机超过 `30/255` 视为按下，保留原始摇杆值（Y 轴向下）。
- **Input Manager**：在 Windows 上直接读取 4 个 XInput 槽位（支持 `XInputGetStateEx` 读取 Home 键），未连接槽位按每秒 1 次降频探测，避免高频调用阻塞主线程。
- 与浏览器一致，手柄在首次按下按钮或推动摇杆过半前不会出现在 `getGamepads()` 中。

### 空间导航与防误触

在页面清单中开启 `"spatialNavigation": true`（或设置 `page.SpatialNavigation = true`）后，持有焦点的 View 自动启用方向导航：

- 十字键与左摇杆（推过一半）将焦点移向对应方向最近的可聚焦元素，并同步悬停状态；长按 0.5 秒后按每 0.1 秒重复。
- 手柄 **A** 触发 `Enter`（点击），**B** 触发 `Escape`（失焦）。

游戏逻辑通过 `view.GamepadUsed(slot, button)` 判断输入是否已被页面消费（含持续按住与松开当帧）：

```csharp
if (!view.GamepadUsed(0, WeftGamepadButton.A))
{
    Interact();
}
if (!view.GamepadUsed(0, WeftGamepadButton.LeftStick))
{
    MoveCharacter();
}
```
