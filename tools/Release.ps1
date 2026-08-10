param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$GameDir = "D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator",
    [string]$NotesFile = "",
    [switch]$RepairExisting
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildPackagesScript = Join-Path $PSScriptRoot "Build-ReleasePackages.ps1"
$HostSourceRelative = "IronNestFCS/FcsHostMod.cs"
$OutputDir = Join-Path $RepoRoot "artifacts\release-v$Version"
$Tag = "v$Version"
$oldVersion = ""
$versionChanged = $false
$versionCommitted = $false

. (Join-Path $PSScriptRoot "Version.ps1")

foreach ($tool in @("git", "gh", "dotnet")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required command '$tool' was not found in PATH."
    }
}

if (-not (Test-Path $GameDir)) {
    throw "Game directory does not exist: $GameDir"
}

if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    if (-not [System.IO.Path]::IsPathRooted($NotesFile)) {
        $NotesFile = Join-Path $RepoRoot $NotesFile
    }
    if (-not (Test-Path $NotesFile)) {
        throw "Release notes file does not exist: $NotesFile"
    }
}

Push-Location $RepoRoot
try {
    & gh auth status
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run 'gh auth login' first."
    }

    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not determine the current git branch."
    }
    if ($branch -ne "master") {
        throw "Release must be run from master. Current branch: $branch"
    }

    $dirty = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect git working tree."
    }
    if ($dirty.Count -gt 0) {
        throw "Working tree must be clean before release. Commit or stash local changes first."
    }

    Write-Host "Updating master..."
    & git pull --ff-only origin master
    if ($LASTEXITCODE -ne 0) {
        throw "git pull --ff-only failed."
    }

    $Repository = (& gh repo view --json nameWithOwner --jq '.nameWithOwner').Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Repository)) {
        throw "Could not determine GitHub repository from the current checkout."
    }

    $remoteTagText = @(& git ls-remote --tags origin "refs/tags/$Tag") -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect remote tag $Tag."
    }
    $tagExists = -not [string]::IsNullOrWhiteSpace($remoteTagText)

    & gh release view $Tag --repo $Repository *> $null
    $releaseExists = ($LASTEXITCODE -eq 0)

    if (($tagExists -or $releaseExists) -and -not $RepairExisting) {
        throw "$Tag already exists. Use -RepairExisting only when intentionally repairing that published version."
    }

    $oldVersion = Get-IronNestFcsVersion -RepoRoot $RepoRoot
    $versionChanged = Set-IronNestFcsVersion -RepoRoot $RepoRoot -Version $Version
    if ($versionChanged) {
        Write-Host "Version: $oldVersion -> $Version"
    }
    else {
        Write-Host "Version already set to $Version"
    }

    Write-Host "Building release packages..."
    & $BuildPackagesScript -GameDir $GameDir -Configuration Release -Version $Version -OutputDir $OutputDir
    if ($LASTEXITCODE -ne 0) {
        throw "Release package build failed."
    }

    $assets = @(
        (Join-Path $OutputDir "IronNestFCS-Smart_v${Version}_en-US.zip"),
        (Join-Path $OutputDir "IronNestFCS-Smart_v${Version}_zh-CN.zip"),
        (Join-Path $OutputDir "SHA256SUMS.txt")
    )
    foreach ($asset in $assets) {
        if (-not (Test-Path $asset)) {
            throw "Expected release asset was not produced: $asset"
        }
    }

    if ($versionChanged) {
        & git add -- $HostSourceRelative
        if ($LASTEXITCODE -ne 0) {
            throw "git add failed."
        }
        & git commit -m "release: v$Version"
        if ($LASTEXITCODE -ne 0) {
            throw "Version commit failed."
        }
        $versionCommitted = $true
    }

    & git push origin master
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push master."
    }

    $trackedDirty = @(& git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify post-build git state."
    }
    if ($trackedDirty.Count -gt 0) {
        throw "Build left tracked files modified. Review git status before publishing."
    }

    if ($RepairExisting) {
        Write-Host "Repairing tag $Tag at current master..."
        & git tag -f -a $Tag -m "IronNestFCS Smart $Tag" HEAD
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update local tag $Tag."
        }
        & git push origin "refs/tags/$Tag" --force
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to move remote tag $Tag."
        }
    }
    else {
        & git tag -a $Tag -m "IronNestFCS Smart $Tag" HEAD
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create tag $Tag."
        }
        & git push origin "refs/tags/$Tag"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to push tag $Tag."
        }
    }

    $title = "IronNestFCS Smart v$Version"

    if ($releaseExists) {
        Write-Host "Replacing assets on existing GitHub Release $Tag..."
        & gh release upload $Tag @assets --clobber --repo $Repository
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to replace GitHub Release assets."
        }

        if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
            & gh release edit $Tag --title $title --notes-file $NotesFile --repo $Repository
        }
        else {
            & gh release edit $Tag --title $title --repo $Repository
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update GitHub Release metadata."
        }
    }
    else {
        Write-Host "Creating GitHub Release $Tag..."
        if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
            & gh release create $Tag @assets --verify-tag --title $title --notes-file $NotesFile --repo $Repository
        }
        else {
            $releasePrefix = @"
## Downloads / 下载

- IronNestFCS-Smart_v${Version}_en-US.zip — English UI
- IronNestFCS-Smart_v${Version}_zh-CN.zip — 简体中文 UI

Both packages contain the same DLLs; only the default UI language differs.

两个安装包包含相同 DLL，仅默认 UI 语言不同。

Extract the selected archive directly into the game directory.

将所选压缩包直接解压到游戏根目录。

---
"@
            & gh release create $Tag @assets --verify-tag --title $title --generate-notes --notes $releasePrefix --repo $Repository
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create GitHub Release."
        }
    }

    Write-Host ""
    Write-Host "Release complete: $title"
    Write-Host "Repository: $Repository"
    Write-Host "Assets: $OutputDir"
}
catch {
    if ($versionChanged -and -not $versionCommitted -and -not [string]::IsNullOrWhiteSpace($oldVersion)) {
        & git reset -- $HostSourceRelative *> $null
        try {
            Set-IronNestFcsVersion -RepoRoot $RepoRoot -Version $oldVersion | Out-Null
        }
        catch {
            Write-Warning "Release failed and automatic version rollback also failed. Check git status."
        }
    }
    throw
}
finally {
    Pop-Location
}
