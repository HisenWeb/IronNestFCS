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
elseif ($Version -ne $DeclaredVersion) {
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
$FileName = "IronNestFCS-Smart_v${Version}.zip"
$Stage = Join-Path $OutputDir "_stage"
$Zip = Join-Path $OutputDir $FileName
$InstallText = @"
IronNestFCS Smart v$Version

简体中文：
1. 安装适用于 IL2CPP 的 MelonLoader。
2. 将本压缩包内容直接解压到游戏根目录。
3. 启动游戏。FCS 界面会自动跟随游戏语言；无法识别时使用英文。

English:
1. Install MelonLoader for IL2CPP.
2. Extract this archive directly into the game directory.
3. Start the game. The FCS UI follows the game's language automatically and falls back to English if detection is unavailable.
"@

if (Test-Path $Stage) {
    Remove-Item -Recurse -Force $Stage
}
if (Test-Path $Zip) {
    Remove-Item -Force $Zip
}

$ModsDir = Join-Path $Stage "Mods"
$UserLibsDir = Join-Path $Stage "UserLibs"
$LogicDir = Join-Path $Stage "UserData\IronNestFCS"
New-Item -ItemType Directory -Force -Path $ModsDir, $UserLibsDir, $LogicDir | Out-Null

Copy-Item -Force $HostDll (Join-Path $ModsDir "IronNestFCS.dll")
Copy-Item -Force $AbstractionsDll (Join-Path $UserLibsDir "IronNestFCS.Abstractions.dll")
Copy-Item -Force $LogicDll (Join-Path $LogicDir "IronNestFCS.Logic.dll")
Copy-Item -Force $LicenseFile (Join-Path $Stage "LICENSE.txt")
[System.IO.File]::WriteAllText((Join-Path $Stage "INSTALL.txt"), $InstallText.Trim() + [Environment]::NewLine, $Utf8NoBom)

Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $Zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $Stage

$Hash = Get-FileHash -Algorithm SHA256 $Zip
$HashLine = "$($Hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($Hash.Path))"
[System.IO.File]::WriteAllText((Join-Path $OutputDir "SHA256SUMS.txt"), $HashLine + [Environment]::NewLine, $Utf8NoBom)

Write-Host "Created $FileName"
Write-Host "  SHA256 $($Hash.Hash)"
Write-Host ""
Write-Host "Release package ready: $OutputDir"
Write-Host "Upload the universal ZIP and SHA256SUMS.txt to the same GitHub Release/tag v$Version."
