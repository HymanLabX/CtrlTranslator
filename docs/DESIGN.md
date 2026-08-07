# CtrlTranslator 视觉设计规格书

> 方向：**克制工具风**（Raycast / Linear 一路）。
> 目标用户长时间连续阅读英文学术论文，界面必须**耐看、不抢注意力、久看不累**。
> 本文件与 `SPEC.md` 同级：`SPEC.md` 管功能，本文件管外观。冲突时功能优先。

---

## 0. 这次改的是外观，不是功能

**这是一次重新蒙皮（re-skin），不是重写。**

- 所有现有功能必须**原样工作**，交互逻辑一行都不许改
- 不许删改任何事件处理器、不许改窗口的 `ShowActivated` / `Topmost` / `SizeToContent` 等行为属性
- 不许动 `Toolbar` 的 `Background="Transparent"`（去掉它拖动会失效，这是踩过的坑）
- 改完之后 `MILESTONES.md` 里所有已通过的验收项必须仍然通过

---

## 1. 设计原则（判断取舍时依据这四条）

1. **默认安静，交互才发声。** 图标平时是次要色，悬停才变主色；边框极淡；阴影几乎看不见。
2. **一处强调色，绝不滥用。** 只有焦点态、当前选中项、主按钮可以用强调色。其余一律灰阶。
3. **信息密度适中。** 不做大留白的"设计感"，工具要一眼看完；但行距要够，读译文不累。
4. **不用 emoji。** 全部换成单色矢量图标。emoji 在不同 Windows 版本渲染不一致，且彩色会抢注意力。

---

## 2. 设计令牌

全部定义在 `Themes/Tokens.Light.xaml` 与 `Themes/Tokens.Dark.xaml`，
**键名两边必须完全一致**，切换主题时整体替换字典即可。

### 2.1 颜色

**中性色阶采用暖纸调**（类似电子墨水阅读器）。原因有二：

1. 浮窗要浮在**纯白的 Word / PDF 文档**上，冷白底会和文档融为一体、看不见边界
2. 主场景是长时间连续阅读，暖调减少蓝光感知，比冷灰更耐看

> ⚠️ 不要用网上流传的"绿豆沙护眼色" `#C7EDCC`，它饱和度过高、会压低文字对比度，
> 长时间读反而更累。

| 令牌键 | 浅色 | 深色 | 用途 |
|---|---|---|---|
| `Color.Bg` | `#F7F3EC` | `#15130F` | 窗口底（比 Surface 略深，做层次） |
| `Color.Surface` | `#FBF8F3` | `#1B1917` | **浮窗**、卡片 |
| `Color.SurfaceSubtle` | `#F2EDE4` | `#221F1B` | 输入框、只读区 |
| `Color.Border` | `#E0D8CA` | `#302B25` | 常规边框、分隔线 |
| `Color.BorderStrong` | `#CFC5B4` | `#453E35` | 输入框边框、**浮窗外框** |
| `Color.Text` | `#1F1B16` | `#EDE7DE` | 正文（暖黑，不是纯黑） |
| `Color.TextSecondary` | `#6E655A` | `#A79D91` | 次要说明、图标默认色 |
| `Color.TextTertiary` | `#9A9086` | `#766D62` | 占位符、禁用态 |
| `Color.Accent` | `#4F7DF3` | `#6C93F7` | 强调 |
| `Color.AccentHover` | `#3D6AE0` | `#8AA9F9` | 强调悬停 |
| `Color.AccentSubtle` | `#EFF4FE` | `#1E2739` | 强调色的浅底 |
| `Color.Hover` | `#F4F4F5` | `#22262E` | 悬停覆盖层 |
| `Color.Pressed` | `#E4E4E7` | `#2A2E37` | 按下覆盖层 |
| `Color.Success` | `#16A34A` | `#4ADE80` | 诊断 ✓ |
| `Color.Warning` | `#B45309` | `#FBBF24` | 警告文字 |
| `Color.WarningBg` | `#FEF6E7` | `#2A2415` | 警告条底 |
| `Color.Danger` | `#DC2626` | `#F87171` | 错误文字、诊断 ✗ |
| `Color.DangerBg` | `#FEF0F0` | `#2A1A1A` | 错误条底 |

每个颜色令牌都要同时提供 `SolidColorBrush` 版本，键名为 `Brush.Xxx`
（例如 `Brush.Text`），XAML 里一律引用画笔而非颜色。

> ⛔ **代码里禁止出现任何硬编码颜色。**
> 现有的 `PopupWindow.xaml.cs` 里有 `Color.FromRgb(35, 37, 41)` 之类，必须全部清掉，
> 改为 `(SolidColorBrush)FindResource("Brush.Surface")` 这种取法。

### 2.2 字体

不依赖任何需要额外安装的字体，全部用系统自带并给出回退链。

| 令牌键 | 值 | 用途 |
|---|---|---|
| `Font.Ui` | `Segoe UI, Microsoft YaHei UI, Microsoft YaHei` | 所有界面文字 |
| `Font.Mono` | `Cascadia Mono, Consolas, Courier New` | 音标、JSON 编辑框、诊断报告 |

| 令牌键 | 字号 | 字重 | 用途 |
|---|---|---|---|
| `Size.Caption` | 11 | Normal | 辅助说明、时间戳 |
| `Size.BodySmall` | 12 | Normal | 表单标签、列表次要行 |
| `Size.Body` | 13 | Normal | 界面正文、按钮文字 |
| `Size.BodyLarge` | 14 | Normal | **浮窗译文默认值**（受设置项 10–24 控制） |
| `Size.Title` | 15 | SemiBold | 分组标题 |
| `Size.Heading` | 18 | SemiBold | 窗口主标题、向导页标题 |

**行距**：浮窗译文 `LineHeight = 字号 × 1.6`；其余界面文字 1.4。
译文是要连续阅读的，行距不能按普通 UI 给。

### 2.3 间距与圆角

间距只用这几档：`4 / 8 / 12 / 16 / 20 / 24 / 32`。不要出现 5、7、13 这种数。

| 令牌键 | 值 | 用途 |
|---|---|---|
| `Radius.Small` | 4 | 按钮、输入框、标签 |
| `Radius.Medium` | 8 | 卡片、提示条、列表项 |
| `Radius.Large` | 10 | 浮窗外壳 |

### 2.4 阴影

只有浮窗有阴影，其余窗口一律无阴影（靠边框区分层次）。

```xml
<DropShadowEffect BlurRadius="24" ShadowDepth="4" Direction="270"
                  Opacity="0.18" Color="#000000" />   <!-- 浅色 -->
<DropShadowEffect BlurRadius="24" ShadowDepth="4" Direction="270"
                  Opacity="0.50" Color="#000000" />   <!-- 深色 -->
```

浮窗外框用 **1.5px** `Brush.BorderStrong`（不是 1px 的 `Brush.Border`）。
浮窗要压在别的程序窗口上，边界必须足够明确 —— 实测 1px 淡边框在白色 Word 文档上
几乎看不见，浮窗像是"糊"在文档里。

### 2.5 动效

**克制**。只做两件事，其余一律不加动画：

- 悬停/按下的背景色过渡：**120ms**，`CubicEase / EaseOut`
- 浮窗出现时的淡入：**140ms**，仅 `Opacity` 从 0 到 1，**不要位移、不要缩放**

> 浮窗出现频繁（每次划词都出现），任何位移或缩放动画重复几百次后都会烦人。

---

## 3. 控件规范

全部定义在 `Themes/Controls.xaml`。每个控件都要覆盖 `Normal / Hover / Pressed / Disabled / Focused` 五态。

### 3.1 按钮

四个变体，用 `Style` 的 `x:Key` 区分：

| 样式键 | 外观 | 用在哪 |
|---|---|---|
| `Button.Primary` | 强调色实底、白字、圆角 4 | 「保存」「完成」「立即关闭思考模式」 |
| `Button.Secondary` | 透明底、1px `Border` 边框、正文色 | 「取消」「重试」「去设置」 |
| `Button.Ghost` | 无边框无底，悬停才出 `Hover` 底 | 列表内的次要操作 |
| `Button.Icon` | 28×28 方形、无边框、只有图标 | **浮窗工具条的五个按钮** |

统一规则：

- 高度：普通按钮 `28`，图标按钮 `28×28`
- 内边距：普通按钮 `12,0`
- 禁用态：`Opacity = 0.4`，且**不响应悬停**
- 焦点态：外描 1px `Brush.Accent`，**不要用系统默认的虚线焦点框**
- **删除所有默认 WPF 按钮外观**（那层灰色渐变和立体边框是"原始感"的最大来源）

### 3.2 输入类

`TextBox` / `PasswordBox` / `ComboBox` / `CheckBox` / `Slider` 全部重写模板：

- 输入框：`SurfaceSubtle` 底、1px `Border` 边框、圆角 4、内边距 `8,6`
- 聚焦时边框变 `Brush.Accent`，**不要外发光**
- `CheckBox`：16×16 方框，圆角 3；勾选时填充 `Accent` + 白色对勾图标
- 占位符文字用 `TextTertiary`

### 3.3 TabControl（设置窗口）

**不要用默认的立体标签页。** 改成下划线式：

```
 翻译服务   翻译行为   触发方式   隐私与安全   系统
 ━━━━━━━━
```

- 标签文字：未选中 `TextSecondary`，选中 `Text` 且加 SemiBold
- 选中项下方 2px `Accent` 下划线，宽度与文字等宽
- 标签栏下方一条 1px `Border` 分隔线贯穿整个窗口宽度
- 标签间距 24，无边框无底色

### 3.4 列表（主窗口历史）

- 行高 `Auto`，内边距 `12,10`
- 行与行之间 1px `Border` 分隔线，**不要交替行底色**
- 悬停整行 `Hover` 底，选中行 `AccentSubtle` 底 + 左侧 2px `Accent` 竖条
- 每行两段：第一行原文（`Text`，13px，单行省略号），第二行译文（`TextSecondary`，12px，单行省略号）
- 右侧靠边：模式标签 + 时间（`TextTertiary`，11px）+ 星标图标

### 3.5 ScrollBar

默认 WPF 滚动条又宽又丑。重写为：

- 宽度 8，无箭头按钮，无轨道底色
- 滑块：`BorderStrong` 色，圆角 4，`Opacity 0.5`；悬停 `Opacity 1`

---

## 4. 图标

### 4.1 规范

- **全部替换掉现有的 emoji**（🔊 ☆ 📋 📌 ✕ 等）
- 形式：`Path` 矢量，定义在 `Themes/Icons.xaml` 里，键名 `Icon.Xxx`（`Geometry` 资源）
- 画布：**16×16**；描边宽度 **1.5**；`StrokeLineCap="Round"`、`StrokeLineJoin="Round"`
- 颜色：**不在图标里写死颜色**，由使用处的 `Foreground` 决定
- 默认色 `TextSecondary`，悬停 `Text`，激活态（如已收藏、已钉住）用 `Accent`

### 4.2 需要的图标清单

| 键名 | 造型 | 用在哪 |
|---|---|---|
| `Icon.Speaker` | 喇叭 + 两道声波弧线 | 浮窗朗读 |
| `Icon.Stop` | 圆角实心方块 | 朗读中 |
| `Icon.Star` | 五角星描边 | 收藏（未选中） |
| `Icon.StarFilled` | 五角星实心 | 收藏（已选中，`Accent` 色） |
| `Icon.Copy` | 两个错位圆角矩形 | 复制译文 |
| `Icon.Check` | 对勾 | 复制成功后短暂替换 |
| `Icon.Pin` | 图钉 | 钉住 |
| `Icon.Close` | 叉 | 关闭 |
| `Icon.Search` | 放大镜 | 主窗口搜索框 |
| `Icon.Trash` | 垃圾桶 | 删除记录 |
| `Icon.Settings` | 三条带滑块的横线 | 设置入口 |
| `Icon.Pulse` | 心电波形 | 一键诊断 |
| `Icon.Refresh` | 环形箭头 | 拉取模型、重试 |
| `Icon.Download` | 下箭头 + 托盘横线 | 导出 CSV |
| `Icon.Folder` | 文件夹 | 打开日志/数据文件夹 |
| `Icon.Warning` | 三角形 + 感叹号 | 警告条 |
| `Icon.Error` | 圆形 + 叉 | 错误提示 |
| `Icon.Info` | 圆形 + i | 一般提示 |

---

## 5. 各窗口的具体要求

### 5.1 浮窗（最重要，用户看得最多）

```
┌──────────────────────────────────────────┐
│  ♪   ☆   ⧉                    ⌖    ✕    │  ← 28×28 图标按钮，间距 4
│                                          │     左三个贴左，右两个贴右
├──────────────────────────────────────────┤  ← 1px Border 分隔线
│                                          │
│  残余应变 ε_res 在图 2d 中作为施加应变     │  ← 14px，行距 ×1.6
│  的函数进行量化。                          │
│                                          │
└──────────────────────────────────────────┘
```

- 外壳：`Surface` 底（暖纸色 `#FBF8F3`）、**1.5px `BorderStrong` 边框**、圆角 10、阴影按 §2.4
- 内边距 `16`；工具条与译文之间加 1px 分隔线，上下各留 12
- **加载态**：不要默认 `ProgressBar`。改为一行居中的 `TextSecondary` 文字
  「翻译中… 1.2s」，左侧一个 12×12 的旋转圆环（描边 1.5，270° 弧，匀速 800ms 一圈）
- **词典模式排版**：
  - 音标行：`Font.Mono`，12px，`TextSecondary`
  - 词性：SemiBold，`Accent` 色，行内
  - 释义：正文色
  - 例句：12px，`TextSecondary`，左侧 2px `Border` 竖线 + 左padding 8
- **提示条**（警告/错误）：圆角 8，`WarningBg` / `DangerBg` 底，
  左侧 16×16 图标 + 文字 + 右侧按钮组；文字用 `Warning` / `Danger` 色

### 5.2 设置窗口

- 窗口尺寸 `760×560`，最小 `680×480`
- 顶部标签栏按 §3.3 改成下划线式
- 表单布局：左列标签（`BodySmall`、`TextSecondary`、右对齐、宽 120）+ 右列控件
- 每个分组之间 24 间距，组内 12
- 每项下方可选一行 `Caption` + `TextTertiary` 的说明文字
- 底部固定操作条：右下角「取消」`Button.Secondary` + 「保存」`Button.Primary`，
  上方 1px 分隔线

### 5.3 主窗口（历史与收藏）

- 窗口尺寸 `900×620`
- 顶部：搜索框（带 `Icon.Search` 前缀，占满宽度）+ 右侧「导出 CSV」`Button.Secondary`
- 下方左右分栏：左 55% 列表（按 §3.4），右 45% 详情
- 详情区：原文块 + 译文块，中间 1px 分隔线，各自右上角一个复制图标按钮
- 空状态：居中 `TextTertiary` 文字「还没有翻译记录。选中文字后双击 Ctrl 试试。」

### 5.4 诊断窗口

- 每项一行：状态图标（✓ `Success` / ✗ `Danger` / ⚠ `Warning`）+ 项目名 + 右侧耗时（`TextTertiary`）
- 未执行的项显示为 `TextTertiary` 的空心圆
- 执行中的项显示旋转圆环
- 失败项下方缩进显示原因（`BodySmall`、`Danger`）
- 底部「复制诊断报告」`Button.Secondary`

### 5.5 引导向导

- 窗口尺寸 `560×460`，不可调整大小
- 顶部：步骤指示器，四个圆点 + 连线，当前 `Accent` 实心，已完成 `Accent` 描边，未到 `Border`
- 中部内容区，标题用 `Size.Heading`
- 底部：左「跳过设置」`Button.Ghost`，右「上一步」`Button.Secondary` +「下一步」`Button.Primary`

---

## 6. 应用图标

现有图标是简单几何箭头，需要重做。

**造型**：圆角方形徽标（圆角 = 边长 22%），底为 `#4F7DF3 → #3D6AE0` 的 135° 线性渐变，
内部白色的 **「A」与「文」左右并置**，中间一条细竖线分隔。
一眼传达"翻译"，且在 16×16 下仍可辨认。

**两个变体**：

| 文件 | 用途 | 差异 |
|---|---|---|
| `ctrltranslator.ico` | 正常态 | 如上 |
| `ctrltranslator-paused.ico` | 划词已暂停 | 底色换成 `#71717A` 灰，整体不透明度 70% |

**尺寸**：一个 `.ico` 内含 `16 / 20 / 24 / 32 / 48 / 64 / 128 / 256` 八种。
16 和 20 这两档必须**单独手工调整**（细节要减，竖线可去掉），
直接从 256 缩下来会糊成一团。

**生成方式**：写一个一次性的 C# 或 PowerShell 脚本用 `System.Drawing` 生成，
脚本放 `tools/generate-icons.ps1`，不要手工画。生成完把 `.ico` 提交进仓库。

---

## 7. 实现约束

### 7.1 文件结构

```
src/CtrlTranslator/Themes/
├─ Tokens.Light.xaml      # 浅色令牌（Color + Brush + 尺寸）
├─ Tokens.Dark.xaml       # 深色令牌，键名与浅色完全一致
├─ Icons.xaml             # 所有 Geometry 图标资源
└─ Controls.xaml          # 所有控件样式，只引用令牌键，不写死值
```

在 `App.xaml` 中合并；主题切换 = 运行时替换 `Tokens.*.xaml` 那一项，
`Controls.xaml` 与 `Icons.xaml` 保持不动。

### 7.2 硬性要求

1. **`Controls.xaml` 里不许出现任何字面颜色值**，一律 `{DynamicResource Brush.Xxx}`
   —— 用 `DynamicResource` 而不是 `StaticResource`，否则运行时换主题不会生效
2. **所有 `.cs` 文件里不许再有 `Color.FromRgb` / `Brushes.Xxx`**
3. 主题切换必须**立即生效**，已打开的窗口全部跟着变（浮窗、设置、主窗口都要）
4. 改完后 `dotnet build -c Release` **0 警告 0 错误**
5. 现有全部功能与交互**保持不变**

### 7.3 明确不要做的

- ❌ 不引入任何 UI 框架或图标库（MaterialDesign、HandyControl、MahApps 等一律不许）
- ❌ 不做圆角窗口以外的异形窗口、不做亚克力/云母材质
- ❌ 不加除 §2.5 之外的任何动画
- ❌ 不改任何窗口的行为属性（`ShowActivated`、`Topmost`、`SizeToContent`…）
- ❌ 不动 `Toolbar` 的 `Background="Transparent"`
