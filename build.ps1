# CtrlTranslator 打包脚本
#
# 用法：在项目根目录打开 PowerShell，执行
#     .\build.ps1
#
# 产物：publish\CtrlTranslator.exe —— 单个文件，双击即用。
# 目标机器不需要安装 .NET 运行时，拷到任何一台 64 位 Windows 上都能跑。

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
Write-Host "产物：$exe"
Write-Host "大小：$sizeMb MB"
Write-Host ''
Write-Host '这个 exe 是自包含的：拷到任何一台 64 位 Windows 上双击即可运行，'
Write-Host '不需要安装 .NET 运行时，也不需要装任何别的东西。'
Write-Host ''

# 打开产物目录，方便直接把 exe 拖到桌面。
Start-Process explorer.exe -ArgumentList $outputDir
