<#
.SYNOPSIS
    Builds, smoke-tests, and stages a ClaudeUsageTray release: the
    self-contained exe, the framework-dependent (-fx) exe, and the
    per-user installer, all from one script instead of hand-typed
    dotnet/ISCC commands.

.WHY THIS EXISTS
    Every prior release was built by re-typing the same dotnet publish /
    ISCC commands by hand, each session, from memory. That's exactly how
    v2.0.4/2.0.5's -fx build shipped WITHOUT the native SQLite binary
    (e_sqlite3.dll): the self-contained publish command had
    -p:IncludeNativeLibrariesForSelfExtract=true, the -fx one didn't -- a
    one-flag difference nobody was checking for, that turned into a real
    friend's "the app doesn't open after updating" with no error shown
    anywhere. A hand-typed process has no way to catch that; a checked-in
    script does, especially combined with the smoke test below.

.WHAT THE SMOKE TEST CATCHES
    Publishing correctly and RUNNING correctly are different things -- a
    single-file publish can look successful (file exists, right size
    range) while still crashing on first launch because a native
    dependency didn't get bundled. So after each publish, this script
    copies ONLY that one exe into an empty scratch folder (simulating
    exactly what a user gets from a portable-exe download, or a self-
    update's file swap) and launches it for real. If it doesn't still be
    running a few seconds later, or its own startup trace log
    (diagnostico_inicio.txt -- see App.xaml.cs) contains "FATAL", the
    script aborts loudly instead of letting a broken build reach Releases/.

.PARAMETER SkipInstaller
    Skip compiling the Inno Setup installer (e.g. if Inno Setup isn't
    installed on this machine, or only the portable exes are needed).
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$wpfDir = Join-Path $root 'ClaudeUsageTray.Wpf'
$csproj = Join-Path $wpfDir 'ClaudeUsageTray.Wpf.csproj'
$releasesDir = Join-Path (Split-Path -Parent $root) 'Releases'

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  FAIL: $msg" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# Version: read once from the csproj, used everywhere below (installer arg,
# archive folder name) -- a single source of truth instead of a version typed
# separately in two or three places, which is exactly the kind of thing that
# quietly drifts out of sync.
# ---------------------------------------------------------------------------
Write-Step "Reading version from csproj"
$csprojContent = Get-Content $csproj -Raw
if ($csprojContent -notmatch '<Version>([\d.]+)</Version>') {
    throw "Could not find <Version> in $csproj"
}
$version = $Matches[1]
Write-Ok "Version = $version"

# ---------------------------------------------------------------------------
# Sanity build first -- fail fast on a compile error instead of discovering
# it three publish commands later.
# ---------------------------------------------------------------------------
Write-Step "dotnet build (Release)"
& dotnet build $csproj -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw "Release build failed" }
Write-Ok "Build succeeded"

# ---------------------------------------------------------------------------
# Smoke test: copy ONE exe alone into an empty folder, launch it for real,
# and confirm it's still alive and didn't log a fatal startup error a few
# seconds later. This is what would have caught the missing-e_sqlite3.dll
# bug before it ever reached Releases/.
# ---------------------------------------------------------------------------
function Test-ExeStartsCleanly {
    param([string]$ExePath, [string]$Label)

    Write-Step "Smoke-testing $Label"
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) "ClaudeUsageTray-smoketest-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $testExe = Join-Path $scratch 'ClaudeUsageTray.exe'
    Copy-Item $ExePath $testExe

    $proc = $null
    try {
        $proc = Start-Process -FilePath $testExe -PassThru
        Start-Sleep -Seconds 4

        $traceLog = Join-Path $scratch 'diagnostico_inicio.txt'
        $traceContent = if (Test-Path $traceLog) { Get-Content $traceLog -Raw } else { '' }

        $stillRunning = $false
        try { $stillRunning = -not (Get-Process -Id $proc.Id -ErrorAction Stop).HasExited } catch { $stillRunning = $false }

        if (-not $stillRunning) {
            Write-Fail "$Label process exited within 4 seconds of launch"
            if ($traceContent) { Write-Host "--- diagnostico_inicio.txt ---`n$traceContent" -ForegroundColor Yellow }
            throw "$Label failed to stay running -- see trace above. Aborting before this build reaches Releases/."
        }
        if ($traceContent -match 'FATAL') {
            Write-Fail "$Label logged a FATAL startup error"
            Write-Host "--- diagnostico_inicio.txt ---`n$traceContent" -ForegroundColor Yellow
            throw "$Label logged a fatal startup error -- see trace above. Aborting before this build reaches Releases/."
        }

        Write-Ok "$Label started cleanly and is still running"
    }
    finally {
        if ($proc -and -not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {} }
        Start-Sleep -Milliseconds 300
        try { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue } catch {}
    }
}

# ---------------------------------------------------------------------------
# Self-contained (ClaudeUsageTray.exe) -- no .NET runtime needed on the
# target machine. IncludeNativeLibrariesForSelfExtract + EnableCompression
# both matter: without the first, native deps (SQLite) end up as loose
# files next to the exe instead of embedded in it; without the second the
# exe is ~2.4x bigger for no benefit (confirmed: 180MB vs 78MB for the
# exact same content).
# ---------------------------------------------------------------------------
Write-Step "Publishing self-contained build"
$publishSc = Join-Path $wpfDir 'publish_sc'
& dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $publishSc
if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed" }
$scExe = Join-Path $publishSc 'ClaudeUsageTray.exe'
Write-Ok "Published to $scExe"
Test-ExeStartsCleanly -ExePath $scExe -Label "self-contained build"

# ---------------------------------------------------------------------------
# Framework-dependent (-fx) -- needs the .NET 8 Desktop Runtime already on
# the machine, but much smaller. THE FLAG THAT WAS MISSING: without
# IncludeNativeLibrariesForSelfExtract here too, this build silently ships
# without e_sqlite3.dll and crashes on first launch -- this is the exact bug
# that prompted writing this whole script.
# ---------------------------------------------------------------------------
Write-Step "Publishing framework-dependent (-fx) build"
$publishFx = Join-Path $wpfDir 'publish_fx'
& dotnet publish $csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishFx
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent publish failed" }
$fxExe = Join-Path $publishFx 'ClaudeUsageTray.exe'
Write-Ok "Published to $fxExe"
Test-ExeStartsCleanly -ExePath $fxExe -Label "framework-dependent (-fx) build"

# ---------------------------------------------------------------------------
# Installer payload: a copy of the self-contained build (so people who
# install via Setup never need the separate runtime either).
# ---------------------------------------------------------------------------
$publishInstaller = Join-Path $wpfDir 'publish_installer'
New-Item -ItemType Directory -Path $publishInstaller -Force | Out-Null
Copy-Item $scExe (Join-Path $publishInstaller 'ClaudeUsageTray.exe') -Force

$setupExe = $null
if (-not $SkipInstaller) {
    Write-Step "Compiling installer"
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Fail "ISCC.exe not found in the usual Inno Setup locations -- skipping installer"
    }
    else {
        & $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer compile failed" }
        $setupExe = Join-Path $releasesDir 'ClaudeUsageTraySetup.exe'
        Write-Ok "Installer compiled to $setupExe"
    }
}

# ---------------------------------------------------------------------------
# Archive the previous flat build into Releases/<its own version>/, prune to
# the 5 most recent, then promote the new build to flat = latest. Reads the
# OUTGOING build's version from its own file, not from hand-tracked state --
# see feedback_release_exe_naming.
# ---------------------------------------------------------------------------
Write-Step "Archiving previous build and staging new one"
$flatSc = Join-Path $releasesDir 'ClaudeUsageTray.exe'
if (Test-Path $flatSc) {
    $oldVersion = (Get-Item $flatSc).VersionInfo.FileVersion
    if ($oldVersion) {
        $oldVersion = ($oldVersion -split '\.')[0..2] -join '.'
        $archiveDir = Join-Path $releasesDir $oldVersion
        if ($oldVersion -ne $version -and -not (Test-Path $archiveDir)) {
            New-Item -ItemType Directory -Path $archiveDir | Out-Null
            Move-Item $flatSc (Join-Path $archiveDir 'ClaudeUsageTray.exe') -Force
            $flatFx = Join-Path $releasesDir 'ClaudeUsageTray-fx.exe'
            if (Test-Path $flatFx) { Move-Item $flatFx (Join-Path $archiveDir 'ClaudeUsageTray-fx.exe') -Force }
            Write-Ok "Archived v$oldVersion to $archiveDir"
        }
    }
}

Copy-Item $scExe $flatSc -Force
Copy-Item $fxExe (Join-Path $releasesDir 'ClaudeUsageTray-fx.exe') -Force
Write-Ok "Staged v$version as the flat (latest) build in $releasesDir"

# Keep only the 5 most recent version subfolders.
$versionDirs = Get-ChildItem $releasesDir -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } `
    | Sort-Object { [Version]$_.Name } -Descending
$versionDirs | Select-Object -Skip 5 | ForEach-Object {
    Write-Host "  Pruning old archive: $($_.Name)"
    Remove-Item -Recurse -Force $_.FullName
}

Write-Host "`n=== Done: v$version staged in $releasesDir ===" -ForegroundColor Cyan
if ($setupExe) { Write-Host "Installer: $setupExe" }
Write-Host "Self-contained: $flatSc"
Write-Host "Framework-dependent: $(Join-Path $releasesDir 'ClaudeUsageTray-fx.exe')"
