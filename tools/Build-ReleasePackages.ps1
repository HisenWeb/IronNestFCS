param(
    [string]$GameDir = "D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator",
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "Version.ps1")
$DeclaredVersion = Get-IronNestFcsVersion -RepoRoot $RepoRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $DeclaredVersion
}
elif ($Version -ne $DeclaredVersion) {
    throw "Package version v$Version does not match Host version v$DeclaredVersion. Update the Host version first or use tools\Release.ps1."
}

$Solution = Join-Path $RepoRoot "IronNestFCS.sln"
$HostDll = Join-Path $RepoRoot "IronNestFCS\bin\$Configuration\IronNestFCS.dll"
$AbstractionsDll = Join-Path $RepoRoot "IronNestFCS.Abstractions\bin\$Configuration\IronNestFCS.Abstractions.dll"
$LogicDll = Join-Path $GameDir "UserData\IronNestFCS\IronNestFCS.Logic.dll"
$LicenseFile = Join-Path $RepoRoot "LICENSE"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "artifacts\release-v$Version"
}

if (-not (Test-Path $GameDir)) {
    throw "Game directory does not exist: $GameDir"
}

Write-Host "Building IronNestFCS Smart v$Version..."
& dotnet build $Solution -c $Configuration "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

foreach ($path in @($HostDll, $AbstractionsDll, $LogicDll, $LicenseFile)) {
    if (-not (Test-Path $path)) {
        throw "Expected release input was not produced: $path"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$Packages = @(
    @{
        Locale = "zh-CN"
        FileName = "IronNestFCS-Smart_v${Version}_zh-CN.zip"
        InstallText = @"
IronNestFCS Smart v$Version - 简体中文

安装：
1. 安装适用于 IL2CPP 的 MelonLoader。
2. 将本压缩包内容直接解压到游戏根目录。
3. 启动游戏。

本包与英文包使用完全相同的 DLL，仅默认 UI 语言不同。
UI 语言文件：UserData\IronNestFCS\language.txt
"@
    },
    @{
        Locale = "en-US"
        FileName = "IronNestFCS-Smart_v${Version}_en-US.zip"
        InstallText = @"
IronNestFCS Smart v$Version - English

Installation:
1. Install MelonLoader for IL2CPP.
2. Extract this archive directly into the game directory.
3. Start the game.

This package uses exactly the same DLLs as the Chinese package; only the default UI language differs.
UI language file: UserData\IronNestFCS\language.txt
"@
    }
)

$Hashes = @()
foreach ($package in $Packages) {
    $locale = $package.Locale
    $stage = Join-Path $OutputDir "_stage-$locale"
    $zip = Join-Path $OutputDir $package.FileName

    if (Test-Path $stage) {
        Remove-Item -Recurse -Force $stage
    }
    if (Test-Path $zip) {
        Remove-Item -Force $zip
    }

    $modsDir = Join-Path $stage "Mods"
    $userLibsDir = Join-Path $stage "UserLibs"
    $logicDir = Join-Path $stage "UserData\IronNestFCS"
    New-Item -ItemType Directory -Force -Path $modsDir, $userLibsDir, $logicDir | Out-Null

    Copy-Item -Force $HostDll (Join-Path $modsDir "IronNestFCS.dll")
    Copy-Item -Force $AbstractionsDll (Join-Path $userLibsDir "IronNestFCS.Abstractions.dll")
    Copy-Item -Force $LogicDll (Join-Path $logicDir "IronNestFCS.Logic.dll")
    Copy-Item -Force $LicenseFile (Join-Path $stage "LICENSE.txt")

    [System.IO.File]::WriteAllText((Join-Path $logicDir "language.txt"), $locale, $Utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $stage "INSTALL.txt"), $package.InstallText.Trim() + [Environment]::NewLine, $Utf8NoBom)

    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item -Recurse -Force $stage

    $hash = Get-FileHash -Algorithm SHA256 $zip
    $Hashes += $hash
    Write-Host "Created $($package.FileName)"
    Write-Host "  SHA256 $($hash.Hash)"
}

$hashLines = $Hashes | ForEach-Object {
    "$($_.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($_.Path))"
}
[System.IO.File]::WriteAllLines((Join-Path $OutputDir "SHA256SUMS.txt"), $hashLines, $Utf8NoBom)

Write-Host ""
Write-Host "Release packages ready: $OutputDir"
Write-Host "Upload both ZIP files and SHA256SUMS.txt to the same GitHub Release/tag v$Version."
