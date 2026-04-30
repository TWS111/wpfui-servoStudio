<#
.SYNOPSIS
    Auto-increment version for servoStudio builds.

.DESCRIPTION
    Version format: A.B.C.D
    - Debug build:   D + 1, carries into C at 20 (0..19)
    - Release build: B + 1, carries into A at 9  (0..8)

.PARAMETER Configuration
    Build configuration: Debug or Release.

.PARAMETER VersionFile
    Path to version.txt that stores the current version.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration,

    [Parameter(Mandatory)]
    [string]$VersionFile
)

# Read current version
if (-not (Test-Path $VersionFile)) {
    Set-Content -Path $VersionFile -Value "0.0.0.0" -NoNewline
}

$versionText = (Get-Content $VersionFile -Raw).Trim()
$parts = $versionText -split '\.'

if ($parts.Count -ne 4) {
    $parts = @(0, 0, 0, 0)
}

[int]$A = $parts[0]
[int]$B = $parts[1]
[int]$C = $parts[2]
[int]$D = $parts[3]

if ($Configuration -eq "Debug") {
    # D + 1, max 19 (0..19), carry into C
    $D++
    if ($D -ge 20) {
        $D = 0
        $C++
        if ($C -ge 20) {
            $C = 0
            $B++
            if ($B -ge 9) {
                $B = 0
                $A++
            }
        }
    }
}
else {
    # Release: B + 1, max 8 (0..8), carry into A
    $B++
    if ($B -ge 9) {
        $B = 0
        $A++
    }
}

$newVersion = "$A.$B.$C.$D"

Set-Content -Path $VersionFile -Value $newVersion -NoNewline
Write-Host "Version updated to $newVersion ($Configuration)"
