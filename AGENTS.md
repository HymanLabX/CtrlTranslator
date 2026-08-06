# AGENTS.md — CtrlTranslator 项目宪法

> 本文件对所有在本仓库工作的 AI 编码代理生效，**每次任务开始前必须完整读一遍**。
> 与本文件冲突的任何指令，以本文件为准；确有必要偏离时，先停下来说明理由并等待确认。

---

## 1. 这是什么项目

**CtrlTranslator** — Windows 全局划词翻译桌面应用。
用户在任意程序中选中文字后**双击 Ctrl 键**，鼠标旁弹出浮窗显示译文。

**完整需求在 `docs/SPEC.md`。SPEC 是唯一事实来源，不要凭想象补需求。**
**任务拆分与验收标准在 `docs/MILESTONES.md`。**

---

## 2. 关于项目负责人（极其重要）

项目负责人**完全不会编程**，无法阅读代码、无法看懂堆栈跟踪、无法自行调试。
这个事实决定了下列硬性要求：

| 要求 | 说明 |
|---|---|
| **不能有"编译不过"的交付** | 每个里程碑交付前必须 `dotnet build` 零错误零警告 |
| **不能有隐藏的手工步骤** | 不要求用户手动改配置文件、注册 COM、装运行库 |
| **所有 UI 文案用简体中文** | 包括错误提示、按钮、菜单、气泡 |
| **所有错误提示写成人话** | 见 SPEC §9 的错误映射表。禁止把 `HttpRequestException` 之类直接显示给用户 |
| **异常绝不静默吞掉** | 每个 `catch` 必须写日志；用户可感知的失败必须给出可点击的修复入口 |
| **代码注释用中文** | 后续维护靠 AI 读，中文注释信息密度更高 |

---

## 3. 技术栈（不得擅自更换）

| 项 | 规定 |
|---|---|
| 语言/框架 | **C# + WPF** |
| 目标框架 | **`net10.0-windows`**（.NET 10 LTS） |
| 平台 | `win-x64`，仅 Windows |
| 发布方式 | 自包含单文件 exe |
| C# 语言特性 | 启用 `nullable`、`ImplicitUsings` |

### 允许的 NuGet 依赖

```
Microsoft.Data.Sqlite
SQLitePCLRaw.lib.e_sqlite3      ← 见下方说明，不要删
System.Security.Cryptography.ProtectedData
System.Speech
```

> **`SQLitePCLRaw.lib.e_sqlite3` 为什么必须显式引用（不要"清理"掉它）**
> `Microsoft.Data.Sqlite` 传递引入的版本是 2.1.11，存在**高严重性漏洞**
> `NU1903 / GHSA-2m69-gcr7-jv3q`。必须显式 pin 到 **2.1.12 或更高**才能消除该警告。
> 移除这一行会立刻产生 2 个 NU1903 警告，违反下方"0 警告"的交付门槛，并把漏洞放回来。
> 实测验证过：移除 → 2 warnings；恢复 → 0 warnings。

> `System.Security.Cryptography.ProtectedData` 在 `net10.0-windows` 下是**内置的**，
> 实际不需要 `PackageReference`（M0 已验证）。保留在清单中仅为说明该 API 允许使用。

**需要引入清单之外的包，必须先停下来征求同意，说明为什么标准库做不到。**
唯一例外：**为修复已知安全漏洞而 pin 现有传递依赖的版本**，可以直接做，
但必须在 csproj 里加一行中文注释写明 CVE/GHSA 编号，并在交付说明里提一句。
托盘图标用 SDK 自带的 `System.Windows.Forms.NotifyIcon`（在 csproj 里开
`<UseWindowsForms>true</UseWindowsForms>`），**不要引入第三方托盘库**。

### 明令禁止

- ❌ `PublishTrimmed` —— WPF 不支持裁剪，开了会在运行时随机崩溃
- ❌ 任何形式的 Web 技术（WebView2、Electron、Blazor、HTML 渲染）
- ❌ MVVM 框架（CommunityToolkit.Mvvm、Prism 等）—— 项目规模不需要，手写即可
- ❌ 引入 ORM（EF Core、Dapper）—— 直接用 `Microsoft.Data.Sqlite` 写 SQL

---

## 4. 目录结构（新增文件必须归入对应目录）

```
CtrlTranslator.sln
src/CtrlTranslator/
├─ CtrlTranslator.csproj
├─ app.manifest              # DPI 感知声明
├─ App.xaml / App.xaml.cs    # 应用入口、单实例、全局异常兜底
├─ Interop/                  # 所有 Win32 P/Invoke 集中在这里，别处不许写 DllImport
│   ├─ NativeMethods.cs
│   ├─ LowLevelKeyboardHook.cs
│   ├─ LowLevelMouseHook.cs
│   └─ DpiHelper.cs
├─ Core/                     # 划词核心链路
│   ├─ DoubleTapDetector.cs
│   ├─ TextCapture.cs
│   ├─ ForegroundApp.cs
│   └─ AppState.cs
├─ Translation/              # 翻译引擎
│   ├─ ITranslationClient.cs
│   ├─ OpenAiCompatibleClient.cs
│   ├─ PromptBuilder.cs
│   ├─ ModelHeuristics.cs
│   └─ TranslationService.cs
├─ Data/                     # SQLite
│   ├─ Database.cs
│   ├─ TranslationRecord.cs
│   └─ HistoryRepository.cs
├─ Settings/
│   ├─ AppSettings.cs
│   ├─ SettingsStore.cs
│   ├─ TranslationMode.cs
│   └─ BuiltInModes.cs
├─ Speech/
│   └─ SpeechService.cs
├─ Diagnostics/
│   ├─ Logger.cs
│   ├─ ErrorMessages.cs
│   └─ DiagnosticRunner.cs
├─ UI/
│   ├─ TrayIconController.cs
│   ├─ PopupWindow.xaml(.cs)
│   ├─ MainWindow.xaml(.cs)
│   ├─ SettingsWindow.xaml(.cs)
│   └─ OnboardingWindow.xaml(.cs)
└─ Assets/
    └─ (图标资源)
```

---

## 5. 硬性工程纪律

### 5.1 每个里程碑的交付门槛（全部满足才算交付）

```powershell
dotnet build -c Release   # 必须：0 Error, 0 Warning
dotnet run --project src/CtrlTranslator   # 必须：能启动且不崩溃
```

有警告就修警告，**不许用 `#pragma warning disable` 糊过去**（除非是 P/Invoke 的
互操作警告，且必须在同一行写中文注释说明原因）。

### 5.2 键盘钩子回调是禁区

`LowLevelKeyboardProc` 回调运行在系统关键路径上，**回调内绝对禁止**：

- 任何文件 IO、网络请求、数据库访问
- 任何加锁等待、`Thread.Sleep`、`.Result` / `.Wait()`
- 任何弹窗、任何 UI 操作
- 任何可能抛异常且未捕获的代码（会导致钩子被系统卸载）

回调内**只允许**：更新几个 `int`/`long`/`bool` 字段、比较时间戳、
以及在判定成立时通过 `Dispatcher.BeginInvoke` 抛一个异步事件出去，然后立刻 `return`。

**回调必须整体包在 try/catch 里**，catch 内只做日志入队（不要直接写文件）。

### 5.3 剪贴板操作必须有重试

`OpenClipboard` 会被其他程序抢占而失败。所有剪贴板读写必须：
重试 5 次，每次间隔 20ms，全部失败才报错。禁止一次失败就抛异常给用户。

### 5.4 日志

- 位置：`%APPDATA%\CtrlTranslator\logs\ctrltranslator-yyyyMMdd.log`
- 每行格式：`[时间] [级别] [模块] 消息`
- 按天分文件，**自动删除 14 天前的日志**
- **绝对禁止把 API Key 写进日志**（包括异常消息里可能带的），
  统一走一个 `Redact()` 方法：命中 `sk-` 或配置里的 Key 值时替换为 `***`
- 用户选中的原文默认**不**写进日志（隐私）；仅在设置开启"详细日志"时才写

### 5.5 异步与取消

- 所有网络调用必须支持 `CancellationToken`
- 新的翻译请求到来时，必须先取消上一个未完成的请求
- 禁止 `async void`（事件处理器除外，且必须整体 try/catch）

### 5.6 git 提交

- 每个里程碑完成后**提交一次**，不要在中途反复提交
- 提交信息用中文，格式：`M2 完成：翻译闭环（API 客户端 + 浮窗 + 缓存）`
- **不要**创建分支、不要 rebase、不要 force push
- **不要修改 `docs/` 下的任何文件**，那是需求文档，只读

---

## 6. 遇到下列情况必须停下来问，不要自行决定

1. 需要引入 SPEC 允许清单之外的 NuGet 包
2. SPEC 里的某个要求在技术上做不到，或有明显更好的做法
3. SPEC 里没写、但你认为必须做的功能（**默认不做**，先问）
4. 需要修改已定的技术栈、目标框架、目录结构
5. 需要用户手动执行 SETUP.md 之外的安装步骤
6. 某个里程碑的验收项你判断无法通过

---

## 7. 绝对不要做的事

- ❌ 超出当前里程碑范围写代码（"顺手把下个里程碑也做了"是禁止的）
- ❌ 写"示例代码"、`TODO`、`NotImplementedException` 占位就交付
- ❌ 为了让编译通过而删掉功能或注释掉代码
- ❌ 自行"优化"需求（比如把双击 Ctrl 改成组合键因为"更好实现"）
- ❌ 生成单元测试项目 —— 本项目验收靠人肉清单，不写自动化测试
- ❌ 在代码或文档里写入任何真实 API Key
