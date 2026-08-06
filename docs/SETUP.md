# 开工前置步骤（你自己做，约 20 分钟）

> 这一页是给**你**看的，不是给 AI 看的。
> 全部做完并且每一步都"打勾"之后，再去看 `docs/PROMPTS.md`。
> 任何一步卡住了，直接把命令和报错截图发给 Claude。

---

## 第 0 步：打开正确的终端

按 `Win` 键，输入 `powershell`，打开 **Windows PowerShell**。

然后把工作目录切到项目文件夹（**整行复制粘贴**，包括引号）：

```powershell
cd "e:\Claude_Project\AI Project\AP.4 CtrlTranslator v1.0"
```

后面所有命令都在这个窗口里执行。

---

## 第 1 步：安装 .NET 10 SDK

你机器上现在**只有 .NET 8 运行时，没有 SDK**（能运行 .NET 程序，但不能编译）。

```powershell
winget install Microsoft.DotNet.SDK.10
```

装完之后**关掉 PowerShell 窗口，重新开一个**（环境变量需要刷新），然后验证：

```powershell
dotnet --list-sdks
```

**✅ 通过标准**：输出里有一行以 `10.` 开头，例如 `10.0.100 [C:\Program Files\dotnet\sdk]`。

**❌ 如果 winget 失败**：去 https://dotnet.microsoft.com/download/dotnet/10.0 手动下载
「SDK x64」安装包，双击安装，然后重开 PowerShell 再验证一次。

---

## 第 2 步：安装 Codex CLI

你机器上已经有 Node 和 npm，直接装：

```powershell
npm install -g @openai/codex
```

验证：

```powershell
codex --version
```

**✅ 通过标准**：输出一个版本号。

**❌ 如果提示 `codex` 不是命令**：关掉 PowerShell 重开一次再试。还不行的话，运行
`npm config get prefix`，把输出的那个路径加到系统 PATH 环境变量里。

---

## 第 3 步：登录 Codex

```powershell
codex login
```

会打开浏览器让你登录 ChatGPT 账号（需要 Plus / Pro / Business 订阅）。

**✅ 通过标准**：浏览器显示登录成功，终端不再提示未认证。

> 如果你想用 API Key 计费而不是订阅额度，改用：
> `$env:OPENAI_API_KEY = "sk-..."`，然后 `codex` 会自动读取。

---

## 第 4 步：⚠️ 关键验证 —— Codex 在你的 Windows 上真的能干活吗

**这一步不能跳。** Codex CLI 对原生 Windows 的支持不如 Linux/macOS 成熟，
必须先确认它能在你这台机器上正常读写文件和跑命令，否则后面五个里程碑会全程踩坑。

在项目目录下运行：

```powershell
codex "在当前目录创建一个文件 hello-test.txt，内容写：Codex works on Windows。然后运行 dotnet --list-sdks 并把输出也追加到这个文件里。"
```

**✅ 通过标准**（三条全中才算过）：

1. Codex 没有报"不支持的平台"之类的错误
2. 项目目录下真的出现了 `hello-test.txt`
3. 打开这个文件，里面既有那句话，**也有 dotnet SDK 的版本输出**（说明它能执行命令并读到结果）

验证完删掉测试文件：

```powershell
Remove-Item hello-test.txt
```

**❌ 如果这一步失败**：先别硬扛。把完整的报错发给 Claude，我们会改方案
（要么换 Claude Code 来写，要么调整 Codex 的运行方式）。

---

## 第 5 步：初始化 git 仓库

Codex 依赖 git 来追踪自己改了什么、以及在出错时回滚。

```powershell
git init
git config user.name "xiang"
git config user.email "xiangsanfen@gmail.com"
```

创建 `.gitignore`（**整段复制粘贴，一次性执行**）：

```powershell
@'
bin/
obj/
*.user
.vs/
publish/
*.log
'@ | Set-Content -Path .gitignore -Encoding utf8
```

提交现有文档：

```powershell
git add .
git commit -m "初始化项目：需求规格书与里程碑计划"
```

**✅ 通过标准**：`git log --oneline` 能看到这一条提交。

---

## 第 6 步：检查文档齐了

```powershell
Get-ChildItem -Recurse -File | Select-Object -ExpandProperty FullName
```

**✅ 通过标准**：以下五个文件都在

- `AGENTS.md`
- `docs\SETUP.md`（就是本文件）
- `docs\SPEC.md`
- `docs\MILESTONES.md`
- `docs\PROMPTS.md`

---

## 全部打勾之后

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
git add .
git commit -m "M0 完成：骨架与托盘"   # 里程碑编号和名字自己换
```
