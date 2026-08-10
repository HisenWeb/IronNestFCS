param(
    [Parameter(Position = 0)]
    [ValidatePattern('^$|^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "",
    [string]$GameDir = "D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator",
    [string]$NotesFile = "",
    [switch]$RepairExisting
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildPackagesScript = Join-Path $PSScriptRoot "Build-ReleasePackages.ps1"
$HostSourceRelative = "IronNestFCS/FcsHostMod.cs"
$oldVersion = ""
$versionChanged = $false
$versionCommitted = $false

. (Join-Path $PSScriptRoot "Version.ps1")

if ([string]::IsNullOrWhiteSpace($Version)) {
    $currentVersion = Get-IronNestFcsVersion -RepoRoot $RepoRoot
    $match = [regex]::Match($currentVersion, '^(\d+)\.(\d+)\.(\d+)$')
    if (-not $match.Success) {
        throw "Automatic version increment requires a stable current version in x.y.z form. Current version: $currentVersion"
    }

    $major = [int]$match.Groups[1].Value
    $minor = [int]$match.Groups[2].Value
    $patch = [int]$match.Groups[3].Value + 1
    $Version = "$major.$minor.$patch"
    Write-Host "Auto-selected release version: $Version (current: $currentVersion)"
}

$OutputDir = Join-Path $RepoRoot "artifacts\release-v$Version"
$Tag = "v$Version"

foreach ($tool in @("git", "gh", "dotnet")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required command '$tool' was not found in PATH."
    }
}

if (-not (Test-Path $GameDir)) {
    throw "Game directory does not exist: $GameDir"
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
    & git pull --ff-only origin refs/heads/master
    if ($LASTEXITCODE -ne 0) {
        throw "git pull --ff-only failed."
    }

    if ([string]::IsNullOrWhiteSpace($NotesFile)) {
        $NotesFile = Join-Path $RepoRoot "release-notes\v$Version.md"
    }
    elseif (-not [System.IO.Path]::IsPathRooted($NotesFile)) {
        $NotesFile = Join-Path $RepoRoot $NotesFile
    }
    if (-not (Test-Path $NotesFile)) {
        $expected = Join-Path $RepoRoot "release-notes\v$Version.md"
        throw "Release notes are required. Create '$expected' or pass -NotesFile <path>. The release will not be published without notes."
    }
    Write-Host "Release notes: $NotesFile"

    $Repository = (& gh repo view --json nameWithOwner --jq '.nameWithOwner').Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Repository)) {
        throw "Could not determine GitHub repository from the current checkout."
    }

    $remoteTagText = @(& git ls-remote --tags origin "refs/tags/$Tag") -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect remote tag $Tag."
    }
    $tagExists = -not [string]::IsNullOrWhiteSpace($remoteTagText)

    $releaseListJson = @(& gh release list --repo $Repository --limit 1000 --json tagName) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect GitHub releases."
    }
    $releaseList = @()
    if (-not [string]::IsNullOrWhiteSpace($releaseListJson)) {
        $releaseList = @($releaseListJson | ConvertFrom-Json)
    }
    $releaseExists = @($releaseList | Where-Object { $_.tagName -eq $Tag }).Count -gt 0

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

    & git push origin "refs/heads/master:refs/heads/master"
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

        & gh release edit $Tag --title $title --notes-file $NotesFile --repo $Repository
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update GitHub Release metadata."
        }
    }
    else {
        Write-Host "Creating GitHub Release $Tag..."
        & gh release create $Tag @assets --verify-tag --title $title --notes-file $NotesFile --repo $Repository
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
