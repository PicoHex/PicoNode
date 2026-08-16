<#
.SYNOPSIS
    Runs Autobahn WebSocket compliance tests against PicoNode's WebSocket implementation.
.DESCRIPTION
    1. Builds the PicoNode sample HTTP server (WebSocket echo at /ws)
    2. Installs Autobahn TestSuite if not present
    3. Runs the fuzzing client against the server
    4. Generates a compliance report under .tools/autobahn-reports

.NOTES
    The autobahntestsuite pip package (pinned 0.8.2) is Python-2-era code.
    This script installs a known-good dependency set (autobahn 19.11.2, no
    wsaccel), extracts the sdist and applies scripts/patch-autobahntestsuite.py
    (verified: clean sdist + patch ⇒ full package imports on Python 3.12).

.PARAMETER Port
    Port the sample server listens on (default 7003).

.EXAMPLE
    pwsh ./scripts/run-autobahn.ps1
#>
param(
    [int]$Port = 7003
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$ReportDir = Join-Path $RepoRoot ".tools/autobahn-reports"
$ABTSVersion = "0.8.2"

# ── 1. Check Python/Autobahn availability ────────────────────────────
$python = Get-Command python3 -ErrorAction SilentlyContinue
if (!$python) { $python = Get-Command python -ErrorAction SilentlyContinue }
if (!$python) {
    Write-Error "Python is required for Autobahn TestSuite"
    exit 1
}

& $python.Source -c "import sys; assert sys.version_info >= (3, 7)"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Python >= 3.7 is required."
    exit 1
}

Write-Host "Installing Autobahn TestSuite toolchain..." -ForegroundColor Cyan
# Known-good dependency set: autobahn 19.11.2 is the last line compatible
# with the 0.8.2 test-suite code after the py3 patch; wsaccel's Cython
# utf8validator crashes on Python 3.12 and must not shadow the pure-python
# fallback.
& $python.Source -m pip install "autobahntestsuite==$ABTSVersion" "autobahn==19.11.2" 2>&1 | Out-Null
& $python.Source -m pip uninstall -y wsaccel 2>&1 | Out-Null

# Patch the installed package: extract the pristine sdist and apply the
# deterministic py2→py3 patch set.
$sp = & $python.Source -c "import sysconfig; print(sysconfig.get_paths()['purelib'])"
$pkgDir = Join-Path $sp "autobahntestsuite"
$sdistDir = Join-Path $RepoRoot ".tools/abts-sdist"
New-Item -ItemType Directory -Force -Path $sdistDir | Out-Null
& $python.Source -m pip download "autobahntestsuite==$ABTSVersion" --no-deps --no-binary :all: -d $sdistDir 2>&1 | Out-Null
$archive = Join-Path $sdistDir "autobahntestsuite-$ABTSVersion.tar.gz"
if (!(Test-Path $archive)) {
    Write-Error "Failed to download autobahntestsuite sdist to $archive"
    exit 1
}

$extractDir = Join-Path $sdistDir "extracted"
if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
tar -xzf $archive -C $extractDir
$pristine = Join-Path $extractDir "autobahntestsuite-$ABTSVersion/autobahntestsuite"
& $python.Source (Join-Path $RepoRoot "scripts/patch-autobahntestsuite.py") $pristine
if ($LASTEXITCODE -ne 0) {
    Write-Error "Autobahn test-suite patch failed."
    exit 1
}

if (Test-Path $pkgDir) { Remove-Item -Recurse -Force $pkgDir }
Copy-Item -Recurse $pristine $pkgDir

# ── 2. Write the fuzzing client spec ─────────────────────────────────
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$configPath = Join-Path $RepoRoot "scripts/autobahn-fuzzingclient.json"
@"
{
    "outdir": "$($ReportDir.Replace('\', '/'))",
    "servers": [
        {
            "agent": "PicoNode",
            "url": "ws://127.0.0.1:$Port/ws",
            "options": { "failByDrop": false }
        }
    ],
    "cases": ["*"],
    "exclude-cases": ["10.*"],
    "exclude-agent-cases": {}
}
"@ | Out-File -Encoding UTF8 $configPath

# ── 3. Build and start the WebSocket echo server ─────────────────────
# The WebSocket echo lives in the HTTP sample (PicoNode.Samples.Http, /ws).
Write-Host "Building sample server..." -ForegroundColor Cyan
& dotnet build "$RepoRoot/samples/PicoNode.Samples.Http/PicoNode.Samples.Http.csproj" -c Release -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "Sample server build failed."
    exit 1
}

Write-Host "Starting server on port $Port..." -ForegroundColor Cyan
$server = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList @(
        "run",
        "--project", "$RepoRoot/samples/PicoNode.Samples.Http/PicoNode.Samples.Http.csproj",
        "-c", "Release",
        "--no-build",
        "--", "--port", "$Port"
    ) `
    -PassThru `
    -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $client.Connect("127.0.0.1", $Port)
            $client.Close()
            $ready = $true
            break
        } catch {
            Start-Sleep -Seconds 1
        }
    }

    if (!$ready) {
        Write-Error "Server did not start listening on port $Port within 30s."
        exit 1
    }

    # ── 4. Run the fuzzing client ────────────────────────────────────
    # The patched package needs its own directory first on sys.path (the
    # _version shim) and UTF-8 mode for report generation.
    $env:PYTHONUTF8 = "1"
    $runner = Join-Path $RepoRoot "scripts/run-autobahn-client.py"
    Write-Host "Running Autobahn fuzzing client..." -ForegroundColor Cyan
    & $python.Source $runner $configPath
    $exitCode = $LASTEXITCODE

    Write-Host "Test complete. Report: $ReportDir/index.html" -ForegroundColor Cyan
    exit $exitCode
} finally {
    if ($server -and !$server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
