function Get-IronNestFcsVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $hostSource = Join-Path $RepoRoot "IronNestFCS\FcsHostMod.cs"
    if (-not (Test-Path $hostSource)) {
        throw "Host source does not exist: $hostSource"
    }

    $text = Get-Content -Raw $hostSource
    $pattern = '\[assembly:\s*MelonInfo\(typeof\(IronNestFCS\.FcsHostMod\),\s*"IronNestFCS Smart",\s*"(?<version>[^"]+)"'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) {
        throw "Could not read IronNestFCS version from $hostSource"
    }

    return $match.Groups["version"].Value
}

function Set-IronNestFcsVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Invalid version '$Version'. Expected semantic version such as 1.1.3 or 1.2.0-beta.1."
    }

    $hostSource = Join-Path $RepoRoot "IronNestFCS\FcsHostMod.cs"
    $text = Get-Content -Raw $hostSource
    $pattern = '\[assembly:\s*MelonInfo\(typeof\(IronNestFCS\.FcsHostMod\),\s*"IronNestFCS Smart",\s*"(?<version>[^"]+)"'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) {
        throw "Could not update IronNestFCS version in $hostSource"
    }

    $currentVersion = $match.Groups["version"].Value
    if ($currentVersion -eq $Version) {
        return $false
    }

    $versionGroup = $match.Groups["version"]
    $updated = $text.Substring(0, $versionGroup.Index) + $Version + $text.Substring($versionGroup.Index + $versionGroup.Length)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($hostSource, $updated, $utf8NoBom)
    return $true
}
