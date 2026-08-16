<#
.SYNOPSIS
    Runs Autobahn WebSocket compliance tests against PicoNode's WebSocket implementation.
.DESCRIPTION
    1. Builds the PicoNode sample HTTP server (WebSocket echo at /ws)
    2. Installs Autobahn TestSuite if not present
    3. Runs the fuzzing client against the server
    4. Generates a compliance report under .tools/autobahn-reports

.NOTES
    Known environment requirement: the autobahntestsuite pip package needs a
    working `_version` module. On some Python/pip combinations the published
    wheel is missing it — use a pinned venv (e.g. autobahntestsuite==0.8.2 in
    a dedicated virtualenv) if `import autobahntestsuite` fails.

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

# ── 1. Check Python/Autobahn availability ────────────────────────────
$python = Get-Command python3 -ErrorAction SilentlyContinue
if (!$python) { $python = Get-Command python -ErrorAction SilentlyContinue }
if (!$python) {
    Write-Error "Python is required for Autobahn TestSuite"
    exit 1
}

& $python.Source -c "import autobahntestsuite" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing Autobahn TestSuite..." -ForegroundColor Cyan
    & $python.Source -m pip install autobahntestsuite
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to install autobahntestsuite (see NOTES in this script about pinning a venv)."
        exit 1
    }
}

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
    "exclude-cases": [],
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
    Write-Host "Running Autobahn fuzzing client..." -ForegroundColor Cyan
    & $python.Source -m autobahntestsuite.wstest -m fuzzingclient -s $configPath
    $exitCode = $LASTEXITCODE

    Write-Host "Test complete. Report: $ReportDir/index.html" -ForegroundColor Cyan
    exit $exitCode
} finally {
    if ($server -and !$server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
