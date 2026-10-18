# AGENT HUB

一个 Windows 上的「AI 应用工作台」：左侧一条窄侧栏，点谁就把谁的真实窗口铺到右侧，切换只是显示/隐藏，不重启、不丢状态。

> 不是网页聚合器。它接管的是**应用本身真实的窗口**，所以登录态、会话、正在跑的任务都原样保留。

## 它解决的问题

日常要在豆包、Trae、Qoder、Codex、DSH 之间来回切，窗口越开越多、Alt+Tab 里全是它们。

AGENT HUB 把它们收进一条侧栏：

- 同时最多选入 **3 个**应用，点一下切过去，再点一下换回来
- 侧栏固定 92px，**永远不覆盖内容区**
- 启动时图标像一叠卡片展开；**谁被用过就自动浮到最上面**（顺序会记住）
- 没选应用时，内容区是一块黑底，中间用橙色写着「开始工作」
- 退出时结束没在用的应用，只留最后显示的那个
- 下次启动自动恢复上次的三个

## 操作

| 操作 | 效果 |
|---|---|
| 左键点图标 | 未选 → 选入并显示；已选 → 切过去 |
| 右键点图标 | 取消 / 编辑 / 移除（后两项只对自己添加的应用） |
| `Alt` + `1`~`9` | 等同左键点第 N 个 |
| `Alt` + `` ` `` | 收起 / 展开侧栏 |
| 鼠标碰到屏幕最左边缘 | 侧栏收起后自动展开 |
| 底部虚线「+」 | 添加应用 |
| 左下角电源图标 | 退出 |

## 名额

同时最多 3 个已选，判定标准是**进程存活**：

- 选满 3 个后再点第四个 → 底部弹一条提示，不打断你
- 右键「取消」= 结束整棵进程树 + 释放名额
- 应用自己关掉窗口（包括缩进托盘）→ 2 秒内自动释放名额

## 添加自己的应用

点底部「+」：

- 上半部分列出**当前所有可见窗口**，选一行，名称和程序路径自动填好
- 也可以直接填或浏览 exe 路径（应用没开着时用这条）

添加后写入 `custom.json`，不污染 `agents.json`。

## 下载后怎么用

从 [Releases](https://github.com/mihayoufamily/agenthub/releases) 下载 `AgentHub-1.0.0-win64.zip`，解压后双击 `AgentHub.exe`。

- 不需要装 .NET SDK，不需要任何运行时，单文件 450KB
- **首次运行是黑底 + 空侧栏**，点侧栏底部的「+」添加你的应用：可以从当前打开的窗口里挑，也可以手填 exe 路径
- 添加的应用写进同目录的 `custom.json`，下次启动自动恢复上次打开的三个

> 仓库里的 `bin/` 是构建产物、不进 git，所以**直接下源码 zip 是找不到 exe 的**——要么下 Release，要么自己编。

## 从源码构建

双击 `build.bat` 就行，它会调用 Windows 自带的 .NET Framework 4 编译器（Win10/11 都有）：

```bat
build.bat
```

产物 `bin\AgentHub.exe`。

也可以用 PowerShell 版 `build.ps1`，但如果提示"禁止运行脚本"，先执行一次：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

## 运行

把 `AgentHub.exe` 单独放到一个目录就行（**不要放在被沙箱管辖的工作区里**，否则进程会拿到低完整性令牌，表现为点应用一直"正在启动"）。

```powershell
.\bin\AgentHub.exe
```

没有 `agents.json` 时从空白开始，用「+」添加应用。

## 配置

`agents.json` 是内置应用列表（`agents.example.json` 是模板）：

```json
{
  "id": "doubao",
  "name": "豆包",
  "short": "豆",
  "kind": "exe",
  "exe": "C:\\Path\\To\\Doubao.exe",
  "proc": "Doubao",
  "title": "豆包",
  "frame": "offset"
}
```

| 字段 | 说明 |
|---|---|
| `exe` | 可执行文件，也可写 `.lnk` 快捷方式 |
| `proc` | 进程名，多个用逗号分隔 |
| `title` | 窗口标题包含该串也算命中（进程名拿不到时的兜底） |
| `frame` | 省略 = 去掉标题栏；`offset` = 保留边框、把非客户区推到屏幕外（给 WebView2/Tauri 类窗口用） |

`custom.json` 由界面里添加的应用自动写入，格式相同。

## 出问题时

日志在 exe 同目录的 `data\app.log`，关键行：

```
start doubao            ← 点了哪个
find -> 123456          ← 找到的窗口句柄，0 = 没找到
apply ... offset=False  ← 开始接管
apply rejected: ...     ← 窗口拒绝被移动，后面 want= 是期望位置
drop dsh window-gone    ← 应用自己关掉了，已释放名额
```

如果窗口被留在半接管状态（程序异常退出），运行 `AgentHub.exe --restore` 还原。
`AgentHub.exe --doctor` 会输出一份环境自检报告。

## 已知限制

- 只支持 Windows（依赖 `SetWindowPos` / 窗口样式 / 进程树操作）
- 应用以管理员权限运行时，本程序需要同等权限才能接管它的窗口
- 个别应用会拒绝被移动，或自己恢复窗口大小（表现为铺不满）
- 只接管顶层窗口，不接管浏览器标签页

## 结构

```
agenthub/
├─ src/*.cs            源码（WinForms，无第三方依赖）
├─ build.ps1           编译脚本
├─ icon.ico            应用图标（多尺寸）
├─ brand/logo.jpg      品牌源图
├─ tools/MakeIconFromImage.cs  从源图生成 icon.ico
├─ agents.example.json 配置模板
├─ GLOSSARY.md         领域术语表
└─ docs/adr/           架构决策记录
```
