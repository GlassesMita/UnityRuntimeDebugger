# 打进一个可直接解压到游戏根目录的发布包。
#
#   pwsh ./scripts/pack-release.ps1
#   pwsh ./scripts/pack-release.ps1 -Version 1.2.0
#
# 产物：dist/UnityRuntimeDebugger.Mono-<Version>.zip
#
# 为什么是 zip 而不是单个 DLL：Mono 宿主把依赖以独立程序集分发（见 README「Mono 版为什么不一样」），
# 一共 17 个 DLL，必须平铺在 BepInEx/plugins 下。只发主 DLL 会让用户遇到
# "Could not load file or assembly 'System.Text.Json'"。
param(
    [string]$Version,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $Version) {
    $Version = (Select-String -Path "Directory.Build.props" -Pattern "<Version>(.+?)</Version>").Matches[0].Groups[1].Value
}
Write-Host "packing UnityRuntimeDebugger.Mono $Version ($Configuration)"

# ── 构建 ────────────────────────────────────────────────────────────────────────
dotnet build "src/BepInEx.Mono/UnityRuntimeDebugger.BepInEx.Mono.csproj" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$buildDir = "src/BepInEx.Mono/bin/$Configuration/netstandard2.0"
$mainDll = Join-Path $buildDir "UnityRuntimeDebugger.Mono.dll"
if (-not (Test-Path $mainDll)) { throw "missing $mainDll" }

# ── 组织目录：BepInEx/plugins 布局，解压即覆盖到游戏根目录 ────────────────────────
$stage = "dist/UnityRuntimeDebugger.Mono-$Version"
if (Test-Path "dist") { Remove-Item "dist" -Recurse -Force }
$pluginDir = Join-Path $stage "BepInEx/plugins"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

# 只要运行时 DLL；PDB/XML/依赖清单都不进包。
Get-ChildItem $buildDir -Filter *.dll | Copy-Item -Destination $pluginDir

$count = (Get-ChildItem $pluginDir -Filter *.dll).Count
Write-Host "  staged $count dll(s)"
if ($count -lt 2) {
    throw "expected the dependency DLLs beside the plugin; only $count found. Check CopyLocalLockFileAssemblies."
}

# ── 安装说明 ────────────────────────────────────────────────────────────────────
# 安装说明单一来源：packaging/安装说明.txt（CI 也用同一份）。
$readmePath = Join-Path $repo "packaging/安装说明.txt"
if (Test-Path $readmePath) {
    $readme = Get-Content $readmePath -Raw
    # 版本号占位，避免说明里的版本与包不符
    $readme = $readme -replace 'Unity Runtime Debugger .*? — BepInEx 6 Mono', "Unity Runtime Debugger $Version — BepInEx 6 Mono"
    $readme = $readme -replace '（共 \d+ 个 DLL，全部保留）', "（共 $count 个 DLL，全部保留）"
    $readme = $readme -replace '(?m)^  \* plugins 下 \d+ 个 DLL 齐全且平铺在同一层。', "  * plugins 下 $count 个 DLL 齐全且平铺在同一层。"
} else {
    throw "missing packaging/安装说明.txt"
}
Set-Content -Path (Join-Path $stage "安装说明.txt") -Value $readme -Encoding UTF8

# ── 压缩 ────────────────────────────────────────────────────────────────────────
$zip = "dist/UnityRuntimeDebugger.Mono-$Version.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "packed $zip ($sizeMb MB, $count dlls)"
Write-Output $zip
