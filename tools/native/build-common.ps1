# Shared helpers for the NopeView build scripts.
# Every entry script dot-sources this file; do not run it directly.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WorkspaceRoot {
    param([Parameter(Mandatory)][string]$ScriptRoot)
    return (Split-Path -Parent $ScriptRoot)
}

function Get-RepoRoot {
    param([Parameter(Mandatory)][string]$ScriptRoot)
    $workspace = Get-WorkspaceRoot -ScriptRoot $ScriptRoot
    $repo = Join-Path $workspace '.repos\NopeView'
    if (-not (Test-Path -LiteralPath $repo)) {
        throw "Repository not found: $repo"
    }
    return $repo
}

# The APK cannot read the repository root, so the Android source tree keeps its
# own copy of the licence files under src/main/assets/licenses. A copy that lags
# behind the root would ship a stale notice, so fail the build before it starts
# and name the file to copy.
function Assert-LicenseAssetsMatch {
    param([Parameter(Mandatory)][string]$Repo)

    $assetRoot = Join-Path $Repo 'src\android\app\src\main\assets\licenses'
    $pairs = @(
        @{ Name = 'LICENSE'; Source = Join-Path $Repo 'LICENSE' },
        @{ Name = 'THIRD-PARTY-NOTICES.md'; Source = Join-Path $Repo 'THIRD-PARTY-NOTICES.md' }
    )

    foreach ($pair in $pairs) {
        if (-not (Test-Path -LiteralPath $pair.Source)) {
            throw "The licence file is missing from the repository root: $($pair.Source)"
        }
        $copy = Join-Path $assetRoot $pair.Name
        if (-not (Test-Path -LiteralPath $copy)) {
            throw "The Android licence asset is missing: $copy. Copy it from the repository root before building."
        }
        $sourceHash = (Get-FileHash -LiteralPath $pair.Source -Algorithm SHA256).Hash
        $copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
        if ($sourceHash -ne $copyHash) {
            throw "The Android licence asset is out of date: $copy. Copy $($pair.Source) over it before building."
        }
    }
}

function Resolve-Dotnet {
    $candidates = @()
    $command = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $candidates += 'C:\Program Files\dotnet\dotnet.exe'
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'dotnet SDK not found. Install the .NET SDK or add dotnet.exe to PATH.'
}

function Resolve-Gradle {
    param([Parameter(Mandatory)][string]$WorkspaceRoot)
    if ($env:GRADLE_HOME) {
        $fromHome = Join-Path $env:GRADLE_HOME 'bin\gradle.bat'
        if (Test-Path -LiteralPath $fromHome) { return $fromHome }
    }
    $command = Get-Command 'gradle.bat' -ErrorAction SilentlyContinue
    if (-not $command) { $command = Get-Command 'gradle' -ErrorAction SilentlyContinue }
    if ($command) { return $command.Source }

    $toolsRoot = Join-Path $WorkspaceRoot '.tools'
    if (Test-Path -LiteralPath $toolsRoot) {
        $fromTools = Get-ChildItem -LiteralPath $toolsRoot -Directory -Filter 'gradle-*' |
            ForEach-Object { Join-Path $_.FullName 'bin\gradle.bat' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($fromTools) { return $fromTools }
    }

    $distsRoot = Join-Path $env:USERPROFILE '.gradle\wrapper\dists'
    if (Test-Path -LiteralPath $distsRoot) {
        $fromDists = Get-ChildItem -LiteralPath $distsRoot -Recurse -Filter 'gradle.bat' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($fromDists) { return $fromDists.FullName }
    }

    throw 'Gradle not found. Set GRADLE_HOME, add gradle to PATH, or unpack a distribution under .tools\gradle-<version>.'
}

function Resolve-AndroidSdk {
    $candidates = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw 'Android SDK not found. Set ANDROID_HOME or install the SDK in the default location.'
}

function Resolve-JavaHome {
    if ($env:JAVA_HOME -and (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
        return $env:JAVA_HOME
    }
    throw 'JAVA_HOME is not set to a valid JDK. Install a JDK 17 and point JAVA_HOME at it.'
}

function Reset-Directory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Write-Host "==> Cleaned $Label"
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$StepName,
        # Tools such as the dotnet CLI resolve their own settings, including
        # global.json, from the working directory, so the caller decides it.
        [string]$WorkingDirectory
    )
    Write-Host "==> $StepName"
    Write-Host "    $Executable $($Arguments -join ' ')"

    # Native stderr is redirected into the log, so relax the global preference
    # around the call; the exit code decides success.
    $previous = $ErrorActionPreference
    $previousLocation = $null
    if ($WorkingDirectory) {
        $previousLocation = Get-Location
        Push-Location -LiteralPath $WorkingDirectory
    }
    $ErrorActionPreference = 'Continue'
    try {
        # Append rather than overwrite, so a header the caller wrote before the
        # step survives, and several steps share one log.
        & $Executable @Arguments 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        if ($previousLocation) { Pop-Location }
    }

    if ($exitCode -ne 0) {
        throw "$StepName failed with exit code $exitCode. See $LogPath"
    }
}
