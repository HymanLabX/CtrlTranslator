# 开工前置步骤 —— ✅ 全部完成

> 这一页是给**你**看的，不是给 AI 看的。
> 前置已全部就绪，可以直接开 M0。本页保留下来是为了：
> 换机器时照着重做一遍，以及记录几个踩过的坑。

## 完成情况

| 步骤 | 状态 | 实测结果 |
|---|---|---|
| 0 · 打开终端 | ✅ | Windows PowerShell |
| 1 · 安装 .NET 10 SDK | ✅ | `10.0.302` |
| 2 · 安装 Codex CLI | ✅ | `codex-cli 0.146.1` |
| 3 · 登录 Codex | ✅ | `D:\.Codex` 已登录（Logged in using ChatGPT） |
| 4 · 验证 Codex 能干活 | ✅ | 四项全过（见下） |
| 5 · 初始化 git 仓库 | ✅ | 分支 `main`，提交 `49d81ba` |
| 6 · 检查文档齐全 | ✅ | 6 个文件全在 |

---

## 🚀 怎么启动 Codex（记住这一条就行）

**开一个普通的 PowerShell，敲两行：**

```powershell
cd "e:\Claude_Project\AI Project\AP.4 CtrlTranslator v1.0"
codex
```

不需要设任何环境变量 —— `CODEX_HOME` 已经在系统级和用户级都设成了 `D:\.Codex`。

启动后应该看到：

```
model:     gpt-5.6-sol xhigh
directory: E:\···\AI Project\AP.4 CtrlTranslator v1.0
```

### ⛔ 不要用 cockpit 面板的「CLI 快速启动」

那个按钮会把 `CODEX_HOME` 覆盖成 `C:\Users\xiang\.antigravity_cockpit\instances\codex\cli-b1b729924ed3`
—— 一个 5.3 GB 的**孤儿目录**：`auth.json` 早没了（只剩 `.broken` 残骸），
cockpit 自己的注册表 `codex_instances.json` 里也是 `"instances": []`，一条记录都没有。
点它只会报「复制来源实例需要目标目录为空」。

真正能用的是 `D:\.Codex`（10 GB，已登录，你的 ap.1 / ap.3 项目一直在用它）。

---

## 各步骤的实测记录

### 第 1 步 · .NET 10 SDK

```powershell
winget install Microsoft.DotNet.SDK.10
```

结果：`dotnet --list-sdks` → `10.0.302 [C:\Program Files\dotnet\sdk]`

> 装之前这台机器只有 .NET 8 **运行时**，没有 SDK（能运行不能编译）。

### 第 2 步 · Codex CLI

```powershell
npm install -g @openai/codex
```

结果：`codex --version` → `codex-cli 0.146.1`，
可执行文件 `C:\Users\xiang\AppData\Roaming\npm\codex.ps1`，已在 PATH。

### 第 3 步 · 登录

无需操作 —— `D:\.Codex` 里已有有效凭据。验证方式：

```powershell
codex login status
```

输出 `Logged in using ChatGPT` 即为通过。

> 换机器时才需要 `codex login`（浏览器 OAuth），
> 或 `"sk-你的key" | codex login --with-api-key`（注意 Key 是从**标准输入**读，不是设环境变量）。

### 第 4 步 · 能力验证 ✅ 已通过

给 Codex 的验证任务：

```
在当前目录建 hello-test.txt，写入 Codex works on Windows，然后把 dotnet --list-sdks 和 git log --oneline 的输出追加进去，最后读出全文给我看
```

四项全过：

| 验证点 | 实测 |
|---|---|
| 工作目录正确 | ✅ `E:\Claude_Project\AI Project\AP.4 CtrlTranslator v1.0` |
| 能写文件 | ✅ `hello-test.txt` 建出来了 |
| 能执行命令**并读到输出** | ✅ 文件里有 `10.0.302` |
| 能访问 git | ✅ 文件里有 `891c23c`、`49d81ba` |

测试文件已清理。`.gitignore` 里也排除了它。

### 第 5 步 · git 仓库

分支 `main`，`user.name = xiang`，`user.email = xiangsanfen@gmail.com`，
`.gitignore` 排除 `bin/` `obj/` `publish/` `.vs/` `*.log` `hello-test.txt`。

---

## ⚠️ 已知摩擦：每条命令都要人工审批

**现象**：Codex 执行命令时报

```
windows sandbox: runner failed during SpawnChild:
CreateProcessAsUserW failed: 5 (拒绝访问。)
cmd="C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.4.0_x64__8wekyb3d8bbwe\pwsh.exe"
```

然后掉进人工审批，你点同意才继续。

**原因**：这台机器的 `pwsh` **只有微软商店版**（装在 `C:\Program Files\WindowsApps\`）。
商店应用的 ACL 拒绝沙箱创建的降权令牌，所以 `CreateProcessAsUserW` 永远返回"拒绝访问"。
独立安装版 `C:\Program Files\PowerShell\7\pwsh.exe` 不存在。

**当前决定：不改，接受审批。** 好处是每条命令你都看得见。

**省点击的技巧**：审批弹窗上如果有「总是允许这条命令」（区别于「本次允许」），
对 `dotnet build` 这类会重复几十次的命令选它。

**如果哪天嫌烦了**，修复方法是装独立版 PowerShell 7（沙箱防护仍然保留）：

```powershell
winget install Microsoft.PowerShell
```

装完落在 `C:\Program Files\PowerShell\7\`，ACL 正常，沙箱就能启动它，审批自然消失。

---

## 其他环境事实（排查问题时可能用得上）

| 项 | 值 |
|---|---|
| `CODEX_HOME` | `D:\.Codex`（用户级 + 系统级都已设置） |
| Codex 模型 | `gpt-5.6-sol`，`model_reasoning_effort = "xhigh"` |
| Windows 沙箱 | `[windows] sandbox = "elevated"` |
| 本项目信任状态 | `trust_level = "trusted"`（已自动加入） |
| 代理 | `D:\.Codex\.env` 里配了 `HTTP_PROXY` / `HTTPS_PROXY` / `NO_PROXY` |
| C 盘剩余 | 18.9 GB（偏紧，注意别让构建产物堆积） |

> **代理提醒**：`dotnet build` 第一次会去 NuGet 拉包。如果代理设置串到 dotnet 上，
> 会报出一堆看起来像"编译错误"、其实是网络失败的信息。遇到就先查网络。

---

## 开工

打开 `docs\PROMPTS.md`。因为 Codex TUI 的 Ctrl+V 被绑给了"粘贴图片"，
长 prompt 粘不进去，所以**不要粘**，直接敲这一行：

```
读 docs/PROMPTS.md，找到「M0 · 骨架与托盘」那一节代码块里的 prompt，严格按它执行
```

后面每个里程碑同理，把「M0 · 骨架与托盘」换成对应的标题就行。

> 如果确实要粘贴文本，用**鼠标右键单击**（经典控制台）或 **Ctrl+Shift+V**（Windows Terminal），
> 别用 Ctrl+V。

**重要的使用纪律**（不遵守会很痛苦）：

| 纪律 | 原因 |
|---|---|
| **一次只给一个里程碑** | 一次性丢完，Codex 会在半路跑偏，而你看不出来 |
| **做完照着验收清单一条条点** | 你不看代码，验收清单是你唯一的质量防线 |
| **不过就报"第几条 + 实际现象"** | 别自己猜原因，描述现象就行 |
| **全过了才 commit 并进下一个** | 有提交点，下一步搞砸了能回滚 |
| **卡两轮修不好就找 Claude** | 日志在 `%APPDATA%\CtrlTranslator\logs\` |

每个里程碑验收通过后：

```powershell
git add -A
git commit -m "M0 完成：骨架与托盘"   # 编号和名字自己换
```

## 额度提醒

启动时 Codex 提示 **周额度剩余不到 25%**，另有 **1 次额度重置可用**（`/usage`）。
`gpt-5.6-sol` + `xhigh` 是最烧额度的组合，建议按里程碑难度分配（用 `/model` 切换）：

| 里程碑 | 建议档位 | 理由 |
|---|---|---|
| M0 骨架 | `medium` | 建项目、托盘、读写 JSON，标准活儿 |
| **M1 划词取词** | **`xhigh`** | 键盘钩子状态机 + 剪贴板还原时序，全项目最难处 |
| M2 翻译闭环 | `high` | 流式解析、浮窗定位有难度 |
| M3 / M4 | `medium` | 界面与打包，机械活 |

那 1 次重置留到 M1 卡住时再用。
