# 开工前置步骤

> 这一页是给**你**看的，不是给 AI 看的。
> 任何一步卡住了，直接把命令和报错截图发给 Claude。

## 当前进度

| 步骤 | 状态 | 实测结果 |
|---|---|---|
| 0 · 打开终端 | ✅ 完成 | — |
| 1 · 安装 .NET 10 SDK | ✅ 完成 | `10.0.302` |
| 2 · 安装 Codex CLI | ✅ 完成 | `codex-cli 0.146.1` |
| **3 · 登录 Codex** | ⬜ **待你操作** | `~/.codex` 不存在，未登录 |
| **4 · 验证 Codex 能干活** | ⬜ **待你操作** | 依赖第 3 步 |
| 5 · 初始化 git 仓库 | ✅ 完成 | 提交 `49d81ba`，分支 `main` |
| 6 · 检查文档齐全 | ✅ 完成 | 6 个文件全在 |

**你现在要做的只剩第 3 步和第 4 步。** 做完就可以开工。

---

## 第 0 步：打开正确的终端 ✅

按 `Win` 键，输入 `powershell`，打开 **Windows PowerShell**。

然后把工作目录切到项目文件夹（**整行复制粘贴**，包括引号）：

```powershell
cd "e:\Claude_Project\AI Project\AP.4 CtrlTranslator v1.0"
```

后面所有命令都在这个窗口里执行。

---

## 第 1 步：安装 .NET 10 SDK ✅ 已完成

```powershell
winget install Microsoft.DotNet.SDK.10
```

**实测结果**：`dotnet --list-sdks` 输出 `10.0.302 [C:\Program Files\dotnet\sdk]`。通过。

---

## 第 2 步：安装 Codex CLI ✅ 已完成

```powershell
npm install -g @openai/codex
```

**实测结果**：`codex --version` 输出 `codex-cli 0.146.1`，
可执行文件在 `C:\Users\xiang\AppData\Roaming\npm\codex.ps1`，已在 PATH 上。通过。

---

## 第 3 步：登录 Codex ⬜ 待你操作

**这一步必须你本人做**，因为要在浏览器里登录你自己的 ChatGPT 账号。

在项目目录的 PowerShell 里运行：

```powershell
codex login
```

会自动打开浏览器让你登录 ChatGPT 账号（需要 Plus / Pro / Business 订阅）。

**✅ 通过标准**：运行下面这条命令能看到已登录信息

```powershell
codex login status
```

### 如果你想用 API Key 计费而不是订阅额度

注意语法：Key 是从**标准输入**读的，不是设个环境变量就行。

```powershell
"sk-你的key" | codex login --with-api-key
```

或者先设环境变量再管道进去：

```powershell
$env:OPENAI_API_KEY = "sk-你的key"
$env:OPENAI_API_KEY | codex login --with-api-key
```

---

## 第 4 步：⚠️ 关键验证 —— Codex 在你的 Windows 上真的能干活吗 ⬜ 待你操作

**这一步不能跳。** Codex CLI 对原生 Windows 的支持不如 Linux/macOS 成熟，
必须先确认它能在你这台机器上正常读写文件和跑命令，否则后面五个里程碑会全程踩坑。

在项目目录下运行（**整行复制**）：

```powershell
codex "在当前目录创建一个文件 hello-test.txt，内容写：Codex works on Windows。然后运行 dotnet --list-sdks 并把输出也追加到这个文件里。"
```

**✅ 通过标准**（三条全中才算过）：

1. Codex 没有报"不支持的平台"之类的错误
2. 项目目录下真的出现了 `hello-test.txt`
3. 打开这个文件，里面既有那句话，**也有 `10.0.302` 这个版本号**
   —— 这说明它能执行命令并读到结果，不只是会写文件

验证完删掉测试文件（`.gitignore` 里已经排除了它，不会污染仓库）：

```powershell
Remove-Item hello-test.txt
```

**❌ 如果这一步失败**：先别硬扛。把完整的报错发给 Claude，我们会改方案
（要么换 Claude Code 来写，要么调整 Codex 的运行方式）。

---

## 第 5 步：初始化 git 仓库 ✅ 已完成

**实测结果**：

- 仓库已建，默认分支 `main`
- `user.name = xiang`，`user.email = xiangsanfen@gmail.com`
- `.gitignore` 已创建（排除 `bin/` `obj/` `publish/` `.vs/` `*.log` 等）
- 首次提交 `49d81ba「初始化项目：需求规格书与里程碑计划」`，6 个文件 1558 行
- 工作区干净

---

## 第 6 步：检查文档齐了 ✅ 已完成

仓库中现有文件：

- `AGENTS.md`
- `.gitignore`
- `docs\SETUP.md`（就是本文件）
- `docs\SPEC.md`
- `docs\MILESTONES.md`
- `docs\PROMPTS.md`

---

## 第 3、4 步做完之后

打开 `docs\PROMPTS.md`，把 **M0 的那段 Prompt** 复制粘贴给 Codex，开始第一个里程碑。

**重要的使用纪律**（不遵守会很痛苦）：

| 纪律 | 原因 |
|---|---|
| **一次只给一个里程碑的 Prompt** | 一次性丢完，Codex 会在半路跑偏，而你看不出来 |
| **每个里程碑做完，照着验收清单一条条点** | 你不看代码，验收清单是你唯一的质量防线 |
| **验收不过，就把"第几条不过 + 实际现象"告诉 Codex 让它修** | 别自己猜原因，描述现象就行 |
| **验收全过了，才提交 git 并进入下一个里程碑** | 有了提交点，下一步搞砸了能回滚 |
| **卡住超过两轮修不好，就把日志文件发给 Claude** | 日志在 `%APPDATA%\CtrlTranslator\logs\` |

每个里程碑验收通过后执行：

```powershell
git add -A
git commit -m "M0 完成：骨架与托盘"   # 里程碑编号和名字自己换
```
