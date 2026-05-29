#Requires -Version 5.1
<#
.SYNOPSIS
    GX Servo Studio MSI 打包脚本
.DESCRIPTION
    1. 从 version.txt 读取版本号
    2. 以 Release + win-x64 + self-contained 发布应用
    3. 若未安装 WiX 工具链则自动安装
    4. 调用 dotnet build 生成 MSI
.EXAMPLE
    .\build_installer.ps1
    .\build_installer.ps1 -SkipPublish      # 复用已有发布目录
    .\build_installer.ps1 -NoIncrement      # 不递增版本号
#>

[CmdletBinding()]
param(
    # 跳过 dotnet publish 阶段（复用 installer\publish\ 已有内容）
    [switch]$SkipPublish,

    # 不递增版本号（直接使用 version.txt 当前值）
    [switch]$NoIncrement
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$projectCsproj    = "$root\samples\Wpf.Ui.servoStudio\servoStudio.csproj"
$versionFile      = "$root\samples\Wpf.Ui.servoStudio\version.txt"
$projectSourceDir = "$root\samples\Wpf.Ui.servoStudio\"
$publishDir       = "$root\installer\publish"
$packageWxs       = "$root\installer\Package.wxs"
$outputDir        = "$root\installer\bin\Release"

# ──────────────────────────────────────────────
# 1. 读取版本号
# ──────────────────────────────────────────────
$version = (Get-Content $versionFile -Raw).Trim()
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  GX Servo Studio  v$version  MSI 打包" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ──────────────────────────────────────────────
# 2. dotnet publish（自包含 win-x64）
# ──────────────────────────────────────────────
if (-not $SkipPublish)
{
    Write-Host "[1/3] dotnet publish  Release | win-x64 | self-contained ..." -ForegroundColor Yellow

    if (Test-Path $publishDir)
    {
        Remove-Item $publishDir -Recurse -Force
    }

    $publishArgs = @(
        'publish', $projectCsproj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $publishDir,
        "/p:Version=$version",
        "/p:AssemblyVersion=$version",
        "/p:FileVersion=$version",
        '/p:IncrementVersionOnBuild=false',  # 禁止 increment-version.ps1 在 publish 时再递增
        '/nologo'
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0)
    {
        Write-Error "dotnet publish 失败，退出代码：$LASTEXITCODE"
        exit 1
    }

    Write-Host "  => 发布目录：$publishDir" -ForegroundColor Green
}
else
{
    Write-Host "[1/3] 跳过 dotnet publish（使用已有目录：$publishDir）" -ForegroundColor DarkYellow

    if (-not (Test-Path "$publishDir\servoStudio.exe"))
    {
        Write-Error "发布目录不存在或不含 servoStudio.exe，请先运行完整发布。"
        exit 1
    }
}

Write-Host ""

# ──────────────────────────────────────────────
# 3. 安装 WiX 工具链（若未安装）
# ──────────────────────────────────────────────
Write-Host "[2/3] 检查 WiX 工具链 ..." -ForegroundColor Yellow

$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd)
{
    Write-Host "  WiX 未安装，正在通过 dotnet tool 安装 WiX v7 ..." -ForegroundColor Yellow

    & dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0)
    {
        Write-Error "WiX 安装失败。"
        exit 1
    }

    # 刷新 PATH，使 wix 命令在当前会话中可用
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'User') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'Machine')
}

$wixVersion = & wix --version 2>&1
Write-Host "  WiX 版本：$wixVersion" -ForegroundColor Green

# 添加必要扩展（首次运行接受 EULA，已存在则静默跳过）
foreach ($ext in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext'))
{
    Write-Host "  确保扩展已安装：$ext" -ForegroundColor DarkYellow
    & wix extension add $ext --acceptEula wix7 2>&1 | Out-Null
}

Write-Host ""

# ──────────────────────────────────────────────
# 4. 构建 MSI（使用 wix build CLI）
# ──────────────────────────────────────────────
Write-Host "[3/3] 构建 MSI 安装包 ..." -ForegroundColor Yellow

# 确保发布目录路径以反斜杠结尾（WiX 预处理变量拼接路径需要）
$publishDirNorm   = $publishDir.TrimEnd('\') + '\'
$projectSourceNorm = $projectSourceDir.TrimEnd('\') + '\'

# 确保输出目录存在
if (-not (Test-Path $outputDir))
{
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$buildArgs = @(
    'build', $packageWxs,
    '-d', "PublishDir=$publishDirNorm",
    '-d', "ProductVer=$version",
    '-d', "ProjectDir=$projectSourceNorm",
    '-ext', 'WixToolset.UI.wixext',
    '-ext', 'WixToolset.Util.wixext',
    '-arch', 'x64',
    '-culture', 'zh-CN',
    '-o', "$outputDir\GXServoStudio-$version.msi"
)

& wix @buildArgs
if ($LASTEXITCODE -ne 0)
{
    Write-Error "MSI 构建失败，退出代码：$LASTEXITCODE"
    exit 1
}

# ──────────────────────────────────────────────
# 5. 完成报告
# ──────────────────────────────────────────────
$msiPath = "$outputDir\GXServoStudio-$version.msi"
if (Test-Path $msiPath)
{
    $sizeMB = [Math]::Round((Get-Item $msiPath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  MSI 构建成功！" -ForegroundColor Green
    Write-Host "  文件：$msiPath" -ForegroundColor Green
    Write-Host "  大小：${sizeMB} MB" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
}
else
{
    Write-Warning "未找到预期的 MSI 文件：$msiPath"
    Write-Host "请检查 $outputDir 目录中的实际输出文件。"
}
