param(
    [string]$GameDir = "D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "Version.ps1")
$Version = Get-IronNestFcsVersion -RepoRoot $RepoRoot

$Solution = Join-Path $RepoRoot "IronNestFCS.sln"
$HostDll = Join-Path $RepoRoot "IronNestFCS\bin\$Configuration\IronNestFCS.dll"
$AbstractionsDll = Join-Path $RepoRoot "IronNestFCS.Abstractions\bin\$Configuration\IronNestFCS.Abstractions.dll"
$LogicDll = Join-Path $GameDir "UserData\IronNestFCS\IronNestFCS.Logic.dll"
$ModsDir = Join-Path $GameDir "Mods"
$UserLibsDir = Join-Path $GameDir "UserLibs"

if (-not (Test-Path $GameDir)) {
    throw "Game directory does not exist: $GameDir"
}

Write-Host "Building IronNestFCS Smart v$Version..."
& dotnet build $Solution -c $Configuration "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

foreach ($path in @($HostDll, $AbstractionsDll, $LogicDll)) {
    if (-not (Test-Path $path)) {
        throw "Expected build output was not produced: $path"
    }
}

New-Item -ItemType Directory -Force -Path $ModsDir | Out-Null
New-Item -ItemType Directory -Force -Path $UserLibsDir | Out-Null

Write-Host "Deploying Host..."
Copy-Item -Force $HostDll (Join-Path $ModsDir "IronNestFCS.dll")

Write-Host "Deploying shared ABI..."
Copy-Item -Force $AbstractionsDll (Join-Path $UserLibsDir "IronNestFCS.Abstractions.dll")

# IronNestFCS.Logic.csproj already outputs directly to UserData\IronNestFCS.
Write-Host "Logic output: $LogicDll"
Write-Host ""
Write-Host "Full IronNestFCS stack deployed. Restart the game once."
Write-Host "Expected startup banner: IronNestFCS Smart v$Version"
Write-Host "Expected host message: Press F9 to hot reload TaskSystem."
Write-Host "After a full Host/Abstractions deployment, normal Logic-only edits can use F9 again."
