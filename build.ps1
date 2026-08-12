# CtrlTranslator 打包脚本
#
# 用法：在项目根目录打开 PowerShell，执行
#     .\build.ps1
#
# 产物有两个：
#   publish\CtrlTranslator.exe                自包含单文件，**本地自测用**
#   installer\output\CtrlTranslator-Setup-*.exe  安装包，**这个才是发给别人的**
#
# 为什么只发安装包：裸 exe 没法安全地自我更新（"下载 exe → 替换自己 → 重启"这套
# 动作在杀毒软件眼里几乎等同蠕虫），而 M6 的一键更新是靠覆盖安装实现的。
# 裸 exe 仍然会被构建，因为它是安装包的输入，也方便你在本机直接验证打包产物。

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$project   = Join-Path $root 'src\CtrlTranslator\CtrlTranslator.csproj'
$outputDir = Join-Path $root 'publish'

Write-Host ''
Write-Host '=== CtrlTranslator 打包 ===' -ForegroundColor Cyan
Write-Host ''

# 打包前先确认程序没在运行，否则会因文件占用而失败。
$running = Get-Process CtrlTranslator -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "CtrlTranslator 正在运行（PID $($running.Id)），请先从托盘退出再打包。" -ForegroundColor Red
    exit 1
}

if (Test-Path $outputDir) {
    Write-Host '清理上一次的产物…'
    Remove-Item $outputDir -Recurse -Force
}

Write-Host '开始发布（自包含单文件，首次约需 1-2 分钟）…'
Write-Host ''

# PublishTrimmed 必须为 false —— WPF 不支持裁剪，开了会在运行时随机崩溃。
# IncludeNativeLibrariesForSelfExtract 用于把 SQLite 的原生库打进单文件。
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -o $outputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '打包失败，请把上面的完整输出发给 Claude。' -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $outputDir 'CtrlTranslator.exe'
if (-not (Test-Path $exe)) {
    Write-Host ''
    Write-Host "打包命令成功了，但没找到 $exe，请把完整输出发给 Claude。" -ForegroundColor Red
    exit 1
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host '=== 打包完成 ===' -ForegroundColor Green
Write-Host "自测用 exe：$exe"
Write-Host "大小：$sizeMb MB"
Write-Host ''
Write-Host '这个 exe 是自包含的，拷到任何一台 64 位 Windows 上双击即可运行，'
Write-Host '适合你本机验证打包产物。但**发给别人请用下面的安装包**——'
Write-Host '只有装过的版本才能收到程序内的一键更新。' -ForegroundColor DarkGray
Write-Host ''

# 顺手编一份安装包（装了 Inno Setup 才会跑；没装就跳过，不算失败）。
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $defaultIscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    if (Test-Path $defaultIscc) {
        $iscc = Get-Item $defaultIscc
    }
}

if ($iscc) {
    Write-Host '检测到 Inno Setup，顺便编译安装包…'
    $issPath = Join-Path $root 'installer\CtrlTranslator.iss'
    & $iscc.Source $issPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host '安装包编译失败，上面的输出发给 Claude 看看。' -ForegroundColor Yellow
    } else {
        $setupExe = Get-ChildItem (Join-Path $root 'installer\output') -Filter 'CtrlTranslator-Setup-*.exe' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($setupExe) {
            Write-Host "安装包：$($setupExe.FullName)" -ForegroundColor Green
        }
    }
} else {
    Write-Host '没检测到 Inno Setup（ISCC.exe），跳过安装包编译。' -ForegroundColor DarkGray
    Write-Host '想顺便出安装包的话：winget install JRSoftware.InnoSetup' -ForegroundColor DarkGray
}

Write-Host ''

# 打开产物目录，方便直接把 exe 拖到桌面。
Start-Process explorer.exe -ArgumentList $outputDir
