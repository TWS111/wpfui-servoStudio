<#
.SYNOPSIS
    Build soemfoe.dll - standalone SOEM library with FoE for servoStudio.
.DESCRIPTION
    Downloads SOEM source and compiles a shared library with FoE module.
    Copies the output DLL to the application output directory.
.PREREQUISITES
    - Git (for cloning SOEM) or internet access (ZIP fallback)
    - CMake 3.12+
    - Visual Studio Build Tools (MSVC)
    - Npcap SDK (https://npcap.com/#download) or SOEM bundled wpcap
    
    Npcap SDK install:
    1. Download: https://npcap.com/dist/npcap-sdk-1.13.zip
    2. Extract to native\npcap-sdk\
    3. Or install to default path, script will auto-detect
#>

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$nativeDir = Join-Path $scriptDir "native"
$soemDir   = Join-Path $nativeDir "SOEM"
$buildDir  = Join-Path $nativeDir "build"

# Default output to app bin directory
if (-not $OutputDir) {
    $OutputDir = Join-Path $scriptDir "bin\Debug\net9.0-windows10.0.26100.0"
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build soemfoe.dll (SOEM + FoE)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Download SOEM source (if not present)
if (-not (Test-Path (Join-Path $soemDir "include\soem\soem.h"))) {
    Write-Host "`n[1/4] Downloading SOEM source..." -ForegroundColor Yellow
    if (Test-Path $soemDir) { Remove-Item -Recurse -Force $soemDir }

    # Try git clone first, fallback to ZIP download on timeout
    $cloneSuccess = $false
    try {
        $gitJob = Start-Job { param($d) git clone --depth 1 https://github.com/OpenEtherCATsociety/SOEM.git $d 2>&1 } -ArgumentList $soemDir
        $finished = Wait-Job $gitJob -Timeout 60
        if ($finished -and $finished.State -eq 'Completed') {
            Receive-Job $gitJob | Out-Null
            $cloneSuccess = Test-Path (Join-Path $soemDir "include\soem\soem.h")
        }
        Remove-Job $gitJob -Force -ErrorAction SilentlyContinue
    } catch { }

    if (-not $cloneSuccess) {
        Write-Host "  Git clone timed out, downloading ZIP..." -ForegroundColor Yellow
        if (Test-Path $soemDir) { Remove-Item -Recurse -Force $soemDir }
        $zipPath = Join-Path $nativeDir "soem.zip"
        $extractDir = Join-Path $nativeDir "soem_extract"
        Invoke-WebRequest -Uri "https://github.com/OpenEtherCATsociety/SOEM/archive/refs/heads/master.zip" -OutFile $zipPath -UseBasicParsing
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
        Move-Item (Join-Path $extractDir "SOEM-master") $soemDir
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path (Join-Path $soemDir "include\soem\soem.h"))) {
        throw "SOEM download failed - include\soem\soem.h not found"
    }
    Write-Host "  SOEM source ready" -ForegroundColor Green
} else {
    Write-Host "`n[1/4] SOEM source already present, skipping download" -ForegroundColor Green
}

# 2. Check Npcap/WinPcap SDK
Write-Host "`n[2/4] Checking Npcap SDK..." -ForegroundColor Yellow
$npcapSdkDir = Join-Path $nativeDir "npcap-sdk"
$wpcapInclude = ""
$wpcapLib = ""

if (Test-Path (Join-Path $npcapSdkDir "Include\pcap.h")) {
    $wpcapInclude = Join-Path $npcapSdkDir "Include"
    $wpcapLib = Join-Path $npcapSdkDir "Lib\x64"
    Write-Host "  Found Npcap SDK: $npcapSdkDir" -ForegroundColor Green
} elseif (Test-Path "C:\Program Files\Npcap\Npcap SDK\Include\pcap.h") {
    $wpcapInclude = "C:\Program Files\Npcap\Npcap SDK\Include"
    $wpcapLib = "C:\Program Files\Npcap\Npcap SDK\Lib\x64"
    Write-Host "  Found system Npcap SDK" -ForegroundColor Green
} else {
    # Check SOEM bundled wpcap
    $soemWpcap = Join-Path $soemDir "oshw\win32\wpcap\Include"
    if (Test-Path (Join-Path $soemWpcap "pcap.h")) {
        Write-Host "  Using SOEM bundled WinPcap headers" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "  WARNING: Npcap SDK not found!" -ForegroundColor Red
        Write-Host "  1. Download: https://npcap.com/dist/npcap-sdk-1.13.zip" -ForegroundColor Red
        Write-Host "  2. Extract to: $npcapSdkDir" -ForegroundColor Red
        Write-Host "  3. Ensure $npcapSdkDir\Include\pcap.h exists" -ForegroundColor Red
        Write-Host "  4. Re-run this script" -ForegroundColor Red
        Write-Host ""
        throw "Npcap SDK not found"
    }
}

# 3. CMake configure + build
Write-Host "`n[3/4] CMake configure and build..." -ForegroundColor Yellow

if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

$cmakeArgs = @(
    "-S", $nativeDir,
    "-B", $buildDir,
    "-A", "x64",
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DSOEM_DIR=$soemDir"
)

# Pass external Npcap SDK to CMake if found
if ($wpcapInclude) {
    $cmakeArgs += "-DCMAKE_C_FLAGS=/I`"$wpcapInclude`""
    $cmakeArgs += "-DCMAKE_SHARED_LINKER_FLAGS=/LIBPATH:`"$wpcapLib`""
}

cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

cmake --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }

# 4. Copy to output directory
Write-Host "`n[4/4] Copying soemfoe.dll..." -ForegroundColor Yellow
$dllSource = Join-Path $buildDir "$Configuration\soemfoe.dll"
if (-not (Test-Path $dllSource)) {
    $dllSource = Join-Path $buildDir "soemfoe.dll"
}

if (Test-Path $dllSource) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    Copy-Item $dllSource -Destination $OutputDir -Force
    Write-Host "`nsoemfoe.dll built and copied to:" -ForegroundColor Green
    Write-Host "  $OutputDir\soemfoe.dll" -ForegroundColor Green
} else {
    throw "Build output not found: $dllSource"
}

Write-Host "`nBuild complete!" -ForegroundColor Cyan
