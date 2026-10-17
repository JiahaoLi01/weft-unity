// The demo page: a panel of things to try (hover, press and click a button, ask Unity, zoom an image with the wheel,
// pointer coordinates), the game's state as a view model, and a label anchored to the game's cube.
const root = document.getElementById('root');

function element(parent, tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  parent.appendChild(node);
  return node;
}

const panel = element(root, 'div', 'panel');
element(panel, 'div', 'title', 'Weft 演示');
const status = element(panel, 'div', 'status', '等 Unity 的数据……');

// A button: :hover and :active styles, and a click the game answers.
const row = element(panel, 'div', 'row');
const button = element(row, 'div', 'button', '点我');
const clicks = element(row, 'div', 'note', '还没点过');
let count = 0;
let acknowledged = 0;
button.addEventListener('click', () => {
  count += 1;
  weft.engine.send('clicked', {count});
  clicks.textContent = `点了 ${count} 次 · Unity 确认 ${acknowledged}`;
});
weft.engine.on('ack', v => {
  acknowledged = v.count;
  clicks.textContent = `点了 ${count} 次 · Unity 确认 ${acknowledged}`;
});

// A call: Unity completes it, the promise resolves with its text.
const askRow = element(panel, 'div', 'row');
const ask = element(askRow, 'div', 'button secondary', '问 Unity');
const answer = element(askRow, 'div', 'note', '');
ask.addEventListener('click', () => {
  answer.textContent = '……';
  weft.engine.call('greet', {from: 'page'}).then(
    text => { answer.textContent = text; },
    error => { answer.textContent = `失败：${error.message}`; });
});

// The wheel zooms a url() image; the page takes the wheel there (preventDefault).
element(panel, 'div', 'label', '在下面滚动滚轮缩放图片（url(stripes.png)）');
const frame = element(panel, 'div', 'frame');
const image = element(frame, 'div', 'image');
let zoom = 1;
frame.addEventListener('wheel', e => {
  e.preventDefault();
  zoom = Math.min(4, Math.max(0.25, zoom * Math.pow(1.1, -e.deltaY / 100)));
  image.style.transform = `scale(${zoom.toFixed(3)})`;
  wheel.textContent = `缩放 ${zoom.toFixed(2)}×（deltaY ${e.deltaY}）`;
});
const wheel = element(panel, 'div', 'note', '缩放 1.00×');

// The pointer anywhere over the View, in CSS px.
const pointer = element(panel, 'div', 'note', '指针：—');
root.addEventListener('mousemove', e => {
  pointer.textContent = `指针：${Math.round(e.clientX)}, ${Math.round(e.clientY)}（按键 ${e.buttons}）`;
});

// The game's state: graphics API, frame rate, whether the pointer is over the page and the cursor there.
const game = element(panel, 'div', 'game', '');
weft.engine.model('game').subscribe(v => {
  status.textContent = `${v.api} · ${v.fps} fps · 缩放 ${v.scale}`;
  game.textContent = `鼠标在页面元素上：${v.overPage ? '是' : '否'} · 光标 ${v.cursor} · 游戏收到的点击 ${v.gameClicks}`;
});

// A label the game anchors to its cube (key 1): placed by the anchor, not by layout.
const plate = element(root, 'div', 'plate');
plate.setAttribute('data-anchor', '1');
plate.setAttribute('data-anchor-pivot', '50% 100%');
element(plate, 'div', 'plate-name', '方块');
const plateNote = element(plate, 'div', 'plate-note', '点空白处让它变色');
weft.engine.on('gameClick', v => { plateNote.textContent = `游戏点击 ${v.count} 次`; });

element(root, 'div', 'hint', '点按钮交给页面；点空白处交给游戏（方块变色）');
