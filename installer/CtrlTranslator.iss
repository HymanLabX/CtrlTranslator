; CtrlTranslator 安装包脚本（Inno Setup）
;
; 为什么要有这个：
; 只发一个"裸" exe（单文件自解压、没有安装/卸载记录）在杀毒软件的启发式规则里
; 属于比较可疑的形态。做成标准安装包——写 Program Files/用户目录、生成开始菜单项、
; 在"应用和功能"里留卸载记录——是常见软件该有的样子，能降低被误判的概率。
; 这不是万能药，真正解决误报还是要靠代码签名，但这一步几乎零成本，值得先做。
;
; 用法：
;   1. 先执行仓库根目录的 build.ps1，生成 publish\CtrlTranslator.exe
;   2. 用 Inno Setup 编译本文件（图形界面打开，或命令行 ISCC installer\CtrlTranslator.iss）
;   产物：installer\output\CtrlTranslator-Setup-<版本号>.exe
;
; 安装方式选了"当前用户"为默认（不需要管理员权限，装的时候不弹 UAC），
; 跟 app 本身"不需要管理员权限就能跑"的设计保持一致；页面上仍然给了
; "为本机所有用户安装"的选项，需要的人可以自己切换（会要求管理员权限）。

#define MyAppName "CtrlTranslator"
; 版本号默认 1.0.0；CI 发版时会用 /DMyAppVersion=<tag 去掉 v 前缀> 覆盖，
; 这样安装包文件名和 exe 的 Release 版本号能对上。
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "CtrlTranslator"
#define MyAppExeName "CtrlTranslator.exe"
#define MyAppURL "https://github.com/HuayouXiang/CtrlTranslator"

[Setup]
AppId={{8331A63C-0924-44FD-BE82-9F712630854D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 默认按当前用户安装（无需管理员权限），但允许用户在安装向导里切换成
; "为本机所有用户安装"（会要求管理员权限）。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 装/卸载前如果程序正在跑，Inno 会检测到这个互斥体并提示用户先关掉它，
; 名字要跟 App.xaml.cs 里的 MutexName 完全一致。
AppMutex=Local\CtrlTranslator.SingleInstance
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=CtrlTranslator-Setup-{#MyAppVersion}
SetupIconFile=..\src\CtrlTranslator\Assets\ctrltranslator.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 已经自带自包含运行时（PublishSingleFile）的 exe 本身就有几十 MB，
; 安装包不需要额外校验 .NET 运行时之类的东西。
DisableWelcomePage=no
DisableReadyPage=yes

[Languages]
; 简体中文语言包不再随 Inno Setup 官方安装包附带（"非官方翻译"需要单独下载），
; 所以直接把文件提交进仓库、用相对路径引用，保证不管本机还是 CI 装的 Inno Setup
; 有没有带这个语言包，编译出来的效果都一样。来源见该文件头部注释。
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\CtrlTranslator.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; 静默安装（= 程序内自动更新）装完必须自己把程序拉回来，否则用户点了「立即更新」之后
; 托盘图标就消失了，看上去像把软件更新没了。
; runasoriginaluser：若这次是提权安装（装在 Program Files 的情况），也要以普通用户身份
; 启动程序 —— 以管理员跑的话，UIPI 会让它收不到普通程序的按键，划词直接废掉。
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
; 顺手清掉开机自启注册项（app 是直接写 HKCU Run 键的，安装包本身不管这一项，
; 卸载时补一刀清理，免得留垃圾）。找不到这一项也不算错误。
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v CtrlTranslator /f"; Flags: runhidden; RunOnceId: "RemoveAutostartEntry"

[Code]
{
  静默安装时（也就是程序内的一键更新）必须先等旧进程退干净。

  时序是这样的：旧版程序下载好安装包 → 校验 SHA256 → 启动本安装包 → 自己退出。
  启动和退出之间必然有一小段重叠，而上面 AppMutex 的检查一旦撞上还活着的进程，
  在 /SILENT /SUPPRESSMSGBOXES 下会直接按"取消"处理 —— 更新静默失败，用户毫不知情。

  InitializeSetup 跑在 AppMutex 检查之前，所以在这里等最多 10 秒最稳。
  只在静默模式下等：正常双击安装时，让 AppMutex 照常提示用户手动关掉程序更直观。
}
function InitializeSetup(): Boolean;
var
  WaitedMs: Integer;
begin
  if WizardSilent() then
  begin
    WaitedMs := 0;
    while (WaitedMs < 10000) and CheckForMutexes('Local\CtrlTranslator.SingleInstance') do
    begin
      Sleep(200);
      WaitedMs := WaitedMs + 200;
    end;
  end;
  Result := True;
end;
