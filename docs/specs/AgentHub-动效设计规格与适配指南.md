# AgentHub 动效设计规格与适配指南

> 用途：记录「方案 D · 纵深 Space」视觉版 + 历轮动效改造的**完整设计规格与实现位置**，供新版本布局/页面变化时做适配移植。
> 涉及文件：`src/Shell.cs`（约 3.5k 行）、`src/RightDock.cs`
> 基线提交：`73af312`（方案 D 视觉）→ `472ee3e`（交互反馈与动效优化，已推送 `qihai-333/agenthub`）

---

## 目录

1. [视觉基调（方案 D）](#1-视觉基调方案-d)
2. [动画引擎](#2-动画引擎)
3. [动效清单与参数](#3-动效清单与参数)
4. [开屏入场动画相位](#4-开屏入场动画相位)
5. [帧率优化架构（缓存清单）](#5-帧率优化架构缓存清单)
6. [关键布局常量](#6-关键布局常量)
7. [状态变量清单](#7-状态变量清单)
8. [已知坑与适配注意事项](#8-已知坑与适配注意事项)

---

## 1. 视觉基调（方案 D）

深空「纵深 Space」风格，全部 GDI+ 自绘：

| 元素 | 规格 |
|---|---|
| 侧栏 Dock | 深空岩板：纵向明暗渐变（`24,26,36` → `12,14,20`）+ 顶部高光棱（白 70alpha，高 Px(30)）+ 噪点玻璃（`_noiseBmp` 128×128 Tile）+ 底部内阴影（黑 110alpha，高 Px(56)）+ 右缘细线（`44,46,58`） |
| 背景 | 星野（`_starBmp` 全屏位图，启动时随机生成）+ 整层环境光（PathGradientBrush 大圆径向渐变，中心 `16×AmbientAlpha()+6` alpha 应用色） |
| 光带 | 无应用铺满内容区时，Dock 右缘 → 内容区横向渐变软光（宽 `max(Px(24), min(Px(150), Width-_sideW))`，alpha `55×AmbientAlpha()`） |
| 环境光色 | `_acc` 向目标应用色 `want` 平滑过渡（`Blend(_acc, want, 0.045f)`，约 500ms 交叉渐变） |
| 每应用主题色 | `AppHue(agent)`；选中图标、涟漪、点亮光晕、Dock 背景收缩均用该色 |
| 星芒点缀 | 12 个固定星点十字线闪烁（`SparkSpots` 二维表），相位 `_breath` |

**适配提示**：岩板/星空/光带/环境光的**绘制函数可整体复用**（`DrawRockSlab` / `DrawStarfield` / `DrawBeam` / `DrawAmbient` / `DrawSparks`），新布局只需改它们接收的矩形参数。

---

## 2. 动画引擎

- **主循环**：`Timer _anim`，`Interval = 3ms`，配合 `Native.timeBeginPeriod(1)` 提升定时器精度；无活跃动画时 `_anim.Stop()` + `timeEndPeriod(1)`（省 CPU）。
- **推进方式**：`Approach(v, target, rate)`：`v + (target-v)*rate`，`|Δ|<0.002` 视为收敛。
- **缓动**：`EaseOut(t) = 1-(1-t)³`（三次缓出）。
- **active 机制**：`AnimTick` 中每段动画置 `active=true`；结束时按状态选择重绘区域——
  - Dock 呼吸且状态稳定：`Invalidate(new Rectangle(0,0,_sideW,Height))`（只重绘 Dock）
  - 其余：`Invalidate(ClientRectangle)`（全屏）
- **帧率统计**：AnimTick 顶部 `_fpsFrames++`，每秒写一条 `Program.Log("fps=N")` 到 `data\app.log`。

**适配提示**：新增动画一律走 AnimTick + active 模式，不要自建 Timer；重绘区域能窄则窄。

---

## 3. 动效清单与参数

### 3.1 图标 / 按钮按压反馈（Shell.cs）

| 项 | 值 |
|---|---|
| 按压深度 | `ScaleRect(r, 0.84f)`（按住期间保持） |
| 背景收缩 | 按压时图标底圆角矩形向内收缩：hover 收缩 `6×_pressT`、选中描边收缩 `5×_pressT`（px，按 `_s` 缩放） |
| 触发 | `OnMouseDown` 记录 `_pressIdx`（图标 index）或 `_miscObj`（-2=加号、-1=无、-3=退出，常量 `PressPlus`/`PressExit`）；`OnMouseUp` 置 `_pressHeld=false; _relT=0` 并触发涟漪 |
| 按住钉 1 | `_pressT`：按下时 `=1f`（保持），松开后 `Approach(_, 0, 0.14f)` 衰减 |
| 松开回弹 | `_relT`：`Approach(_, 1f, 0.13f)`；缩放 `ScaleRect(r, ReleaseSpring(_relT))` |

### 3.2 松开回弹曲线（`ReleaseSpring`）

```
ReleaseSpring(t) = 1 + (-0.16)·e^(-5t)·cos(9t)     // t=0 起 0.84，t≥1 → 1
```
- 过冲约 2.8%（`-0.16·e^(-5·0.35)·cos(9·0.35)` 峰 ≈ +0.028）
- 时间常数：约 0.7s 内收敛。

### 3.3 涟漪（`DrawRipple`）

| 项 | 值 |
|---|---|
| 推进 | `_rippleT = Approach(_, 1f, 0.08f)`（约 300ms 完成） |
| 半径 | `rad = r.Width × 1.15 × EaseOut(t)`，t<0.03 钳制 |
| 主环 | alpha `150×(1-t)`，线宽 `max(1.6, Px(2))`，圆头线帽 |
| 外环 | alpha 减半（`a/2`），半径 ×1.08，线宽 `max(1, Px(1))`，构成双环层次 |
| 中心淡晕 | alpha `a/3`，直径 ×1.1（半径 ×0.55），SolidBrush 填充 |
| 颜色 | 触发对象应用色（图标用 `AppHue`；加号/退出用默认色） |
| 触发对象 | 松开时 `_pressIdx>=0 ? _pressIdx : _miscObj` |

### 3.4 选中卡片弹出（Dock 图标点亮）

| 项 | 值 |
|---|---|
| 弹出缩放 | `ScaleRect(pr, PopScale(_popT))`，`PopScale(t)=1+0.09·e^(-5t)·cos(9t)`（起点 1.09） |
| 推进 | `_popT = Approach(_, 1f, 0.11f)` |
| 光晕脉冲 | 弹出期间（`_popT<1 && _popIdx==_shown`）Dock 点亮光晕 `boost=1+0.8×(1-_popT)`、选中岩板亮面 `boost=1+0.9×(1-_popT)` |
| 触发点 | `ShowChosen` / `FinishStart` / `ShowDock` 三处，在 `_expandT=0` 旁同时置 `_popT=0; _popIdx=目标` |

### 3.5 模式选择 → 工作区转场（`DrawModeTransition` + `_modeT`）

| 项 | 值 |
|---|---|
| 时长 | **约 300ms**（`_modeT = Approach(_, 1f, 0.032f)`，原 600ms / 0.016，已减半） |
| 相位 | 确认弹跳 `conf = Clamp01(_modeT / 0.30f)`；收拢淡出 `outT = Clamp01((_modeT-0.30f) / 0.70f)` |
| 选中卡弹跳 | `pop = 1 + 0.11·sin(π·min(1, conf))` |
| 双卡收拢 | `sc = 1 - 0.68·EaseOut(outT)`，两卡向中心缩放；整体 alpha `1 - EaseOut(outT)`（下限 0.02） |
| 确认光晕 | 半径 `max(Px(24), max(卡宽,卡高)×1.55×(1-EaseOut(conf)))`，alpha `95×(1-conf)`，应用色；半径加下限防退化椭圆 |
| Dock 滑入 | `dx = -(1-EaseOut(Clamp01(_modeT/0.55f)))·_sideW`，岩板从左滑入 |
| 触发 | 模式卡 `OnMouseDown` 设 `_modeSel=1/0; _modeT=0; Kick()` |
| 复位路径 | 三处「回开屏」都置 `_modeSel=-1; _modeT=1f`（`SwitchModeFromPanel`、`ExitSynergy` 内含、右侧栏面板切换） |

### 3.6 右侧栏按钮按压（RightDock.cs）

- 两个按钮各自 `_press/_held/_relT` + 16ms 自管 `_anim`。
- 参数：按压 `0.86`，松开走 `ReleaseSpring`（同 Shell 曲线，实现复刻一份）。

### 3.7 转场期帧率诊断

- AnimTick `_modeT` 收敛时输出 `trans fps=N dur=Nms`（`_transFrames` / `_transMark` 累计）。

---

## 4. 开屏入场动画相位

`DrawModeChooser` 内：`T = _enterT × 2.5`，各元素按 T 分段淡入（`_enterDur = 2.5s`；系统减弱动效时 0.30s；切换模式时 0.35s）：

| 元素 | 相位（T 秒） | 说明 |
|---|---|---|
| pBg 星野 | 0 – 0.40 | 背景淡入 |
| pDots 光点 | 0.40 – 1.10 | 三颗光点飞入 |
| pLines 轨迹线 | 0.55 – 1.05 出 / 1.05 – 1.30 隐 | 划线 + 隐去 |
| pIcon Logo | 0.80 – 1.60 | `_logo` 位图弹出（中心缩放） |
| pRing 光环 | 1.35 – 1.80 | 橙色圆环扩散 |
| pBrand 品牌名 | 1.40 – 2.00 | 「AgentHub」+ 副标题「把 AI 应用收进一条侧栏」上浮 |
| pMode 标题 | 1.90 – 2.25 | 「选择你的工作模式」 |
| pCardA / pCardB 模式卡 | 2.05 – 2.50 / 2.12 – 2.50 | 左卡「多 agent 协同模式」、右卡「普通工作站模式」滑入 |

**适配提示**：相位表是硬时序，若新开屏去掉某元素，直接把对应 `pXxx` 段删除并整体前移即可；卡片淡入沿用 `DrawModeCardFaded`（离屏缓存 + alpha 混合）。

---

## 5. 帧率优化架构（缓存清单）

> 全部为"把每帧 `new` 的对象改为一次性创建 + 每帧仅改属性"，Dispose 统一清理。

| 缓存对象 | 字段 | 说明 |
|---|---|---|
| 5 个字体 | `_fLogoBig` / `_fBrandSub` / `_fModeTitle` / `_fCardTitle` / `_fCardSub` | 开屏标题/品牌/模式卡文本，替代每帧 `new Font` |
| 模式卡离屏位图 ×2 | `_cardBmpA/B` + `_cardBmpAcc` | 卡静态内容烘焙；`ColorClose(_acc, _cardBmpAcc)` 判定重建 |
| 开屏中央光晕刷 | `_haloBrush`（PathGradientBrush） | 每帧仅改 `CenterColor` + `FillEllipse` |
| 整层环境光刷 | `_ambientBrush`（PathGradientBrush，半径 2×max(W,H)） | 每帧仅改 `CenterColor`；SurroundColors 透明可固定 |
| 转场确认光晕刷 | `_modeHaloBrush`（PathGradientBrush） | 同上，半径渐变时 FillEllipse 缩小（渐变中心固定） |
| 光带刷 | `_beamBrush`（LinearGradientBrush） | 每帧改 `LinearColors` |
| 岩板刷 ×3 | `_slabBrush` / `_slabHiBrush` / `_slabShBrush`（LinearGradientBrush） | 纵向渐变与 x 无关，固定矩形建刷，Fill 任意 x |
| 岩板噪点 | `_slabTexBrush`（TextureBrush，Tile） | 噪点位图固定 |
| 岩板画笔 | `_slabPen` | 顶线/右缘线，每帧改 Color |
| Dock 点亮刷 | `_pillHaloBrush`（PathGradientBrush） | 每帧仅改 `CenterColor`（含 boost 脉冲） |
| 星芒画笔 | DrawSparks 内单 Pen 复用 | 每帧改 Color |
| **开屏停留页烘焙** | `_bootBmp`（2560×1528）+ `_bootAcc` | 入场完成后整页（星空+开屏全貌）画进位图；停留期每帧 `DrawImageUnscaled` + 动态星芒，**帧率 16→50+** |
| **工作区静态背景烘焙** | `_bgBmp` + `_bgAcc` + `_bgShown` | 星空+环境光+光带烘焙；OnPaint 按 `e.ClipRectangle` **1:1 源区拷贝**（静止期只重绘 Dock → 只 blit 92px 宽），工作区静止时零重绘 |

**烘焙重建条件**：
- `_bootBmp`：`ColorClose(_acc, _bootAcc)` 为假时重建
- `_bgBmp`：`ColorClose(_acc, _bgAcc)` 或 `_bgShown != _shown` 时重建
- 烘焙失败会 `Program.Log("bake failed: …")` 并回退到逐帧绘制路径

**适配提示**：新版本改布局后，烘焙函数（`BakeBootPage` / `BakeWorkBg`）内部调用的绘制函数不变，**只需确保烘焙时机与失效条件正确**（开屏完成 / 环境光或选中态变化）。

---

## 6. 关键布局常量

| 常量 | 值 | 作用 |
|---|---|---|
| `_sideW` | `Px(92)` | Dock 宽度（模式卡布局、光带起点、转场滑入距离都用它） |
| `RightWPx` | `Px(260)` | 右侧栏面板宽度（模式卡尺寸计算时扣除） |
| 模式卡 | `w = max(Px(200), min(Px(540), (cw-gap×3)/2))`；`h = Px(300)`；`y = Height×0.44`；`gap = Px(48)` | 开屏/模式页双卡（boot 时 left=0） |
| `_enterDur` | 2.5s / 0.30s（reduceMotion）/ 0.35s（切模式） | 开屏总时长 |
| 图标尺寸 | `Px(18)`（选中槽内）、`_sideW` 槽宽 | Dock 内 |
| `ItemTopPx` / `ItemRectNow` | 图标槽位计算 | Dock 内图标布局 |

**适配提示**：所有 UI 尺寸经 `Px(int)`（DPI 缩放）。若新版本改 Dock 宽度，**同步改 `_sideW` 即可联动**（布局、转场、光带、烘焙都引用它）。

---

## 7. 状态变量清单

（动画/交互相关，改状态机时勿漏）

| 字段 | 含义 |
|---|---|
| `_pressIdx` / `_miscObj` / `_pressHeld` / `_pressT` | 按压对象与按住进度（`_miscObj`：`PressPlus=-2` / `PressExit=-3`） |
| `_relT` | 松开回弹进度（配合 `ReleaseSpring`） |
| `_rippleT` / `_rippleIdx` | 涟漪进度与对象 |
| `_popT` / `_popIdx` | 卡片弹出进度与对象 |
| `_hoverT` / `_hover` | hover 高亮进度与对象 |
| `_modeT` / `_modeSel` | 模式转场进度（0→1，0.032/tick）与目标模式 |
| `_expandT` | Dock 展开进度 |
| `_enterT` / `_enterClock` / `_breath` | 开屏进度（Stopwatch 计时）/ 星芒相位 |
| `_acc` | 当前环境光色（向 `want` 收敛） |
| `_shown` / `_chosen` / `_mode` | 选中应用 / 协同集合 / 当前模式（1=协同 0=普通） |
| `_slideT` / `DockGone` | Dock 滑动进出 |
| `_transFrames` / `_transMark` | 转场期帧率诊断 |

---

## 8. 已知坑与适配注意事项

1. **PathGradientBrush 退化椭圆直接抛 `OutOfMemoryException`**（不是内存不足，是路径为空）——所有光晕半径必须加下限（如 `Math.Max(Px(24), …)`），进度区间避免贴 0/1（如 `conf > 0.01f && conf < 0.97f`）。
2. `Px()` 只接受 int，传 float 编译错——写 `(float)Px(1)`。
3. 安装时 exe 被运行中进程占用会 Copy 失败——先 `Stop-Process AgentHub` 再覆盖。
4. 验证流程：`Start-Process AgentHub.exe --doctor` 看 `data\doctor.txt`；正常启动看 `data\app.log` 的 `fps=` / `bake ok` / `bake failed`。
5. 编译命令（= build.bat 等价，0 error）：`csc.exe /nologo /t:winexe /platform:anycpu /o+ /codepage:65001 /out:bin\AgentHub.exe /win32manifest:src\app.manifest /win32icon:icon.ico /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll /r:System.Web.Extensions.dll src\*.cs`。
6. 推送 GitHub：git 全局代理 `http://127.0.0.1:7897`（Clash），代理未开时用 `git -c http.proxy= push origin main` 直连。
7. 提交基线：`73af312`（方案 D）→ `472ee3e`（本次全部动效+帧率优化），已推 `qihai-333/agenthub` main。
