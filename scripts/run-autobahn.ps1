<#
.SYNOPSIS
    Runs the Autobahn WebSocket conformance suite (fuzzing client) against
    PicoNode's WebSocket server.

.DESCRIPTION
    Uses the official frozen test-suite image (crossbario/autobahn-testsuite)
    — the maintainers intentionally keep the suite on a PyPy2.7 + OpenSSL 1.1
    toolchain inside Docker, so no Python 2/3 porting happens here.

    1. Builds and starts the PicoNode sample HTTP server (WebSocket echo /ws)
    2. Runs the fuzzing client container against it (host network)
    3. Parses the report (index.json) and fails on any FAILED case

.PARAMETER Port
    Port the sample server listens on (default 7003).

.NOTES
    Requires Docker. On Windows use Docker Desktop with Linux containers.
    Report output: .tools/autobahn-reports/servers/index.html

.EXAMPLE
    pwsh ./scripts/run-autobahn.ps1
#>
param(
    [int]$Port = 7003
)

$ErrorActionPreference = "Stop"
trap {
    $tail = (Get-Content $LogFile -Tail 30 -ErrorAction SilentlyContinue) -join "`n"
    Write-Output "::error title=conformance script failed::$($_.Exception.Message)`n$tail"
    exit 1
}

$RepoRoot = Split-Path $PSScriptRoot -Parent
$ReportDir = Join-Path $RepoRoot ".tools/autobahn-reports/servers"

# ── 1. Docker must be available ──────────────────────────────────────
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (!$docker) {
    Write-Error "Docker is required (the Autobahn testsuite is distributed as a frozen image). Install Docker and retry."
    exit 1
}
& $docker.Source info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker daemon is not running."
    exit 1
}

# ── 2. Build and start the WebSocket echo server ─────────────────────
# The WebSocket echo lives in the HTTP sample (PicoNode.Samples.Http, /ws).
Write-Host "Building sample server..." -ForegroundColor Cyan
$LogFile = Join-Path $RepoRoot ".tools/autobahn-ci.log"
New-Item -ItemType Directory -Force -Path (Split-Path $LogFile) | Out-Null
function Log($msg) {
    Write-Host $msg
    Add-Content -Path $LogFile -Value $msg
}
Log "autobahn gate: platform=$([System.Environment]::OSVersion.Platform) docker=$(& $docker.Source --version 2>&1)"
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

    # ── 3. Write the fuzzing client spec ──────────────────────────────
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    $configDir = Join-Path $RepoRoot "scripts/autobahn"
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $configPath = Join-Path $configDir "fuzzingclient.json"
    @"
{
    "outdir": "/reports",
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

    # ── 4. Run the fuzzing client container ───────────────────────────
    # --network host lets the container reach the server on 127.0.0.1.
    Write-Host "Running Autobahn fuzzing client (official frozen image)..." -ForegroundColor Cyan
    & $docker.Source run --rm --network host `
        -v "$($ReportDir.Replace('\', '/')):/reports" `
        -v "$($configPath.Replace('\', '/')):/config/fuzzingclient.json" `
        crossbario/autobahn-testsuite `
        wstest -m fuzzingclient -s /config/fuzzingclient.json 2>&1 | Tee-Object -FilePath $LogFile -Append
    $exitCode = $LASTEXITCODE
    Log "fuzzing client exit code: $exitCode"
    if ($exitCode -ne 0) {
        $tail = (Get-Content $LogFile -Tail 25) -join "`n"
        Write-Host "::error title=autobahn fuzzing client failed (exit $exitCode)::$tail"
    }

    # ── 5. Parse the report and fail on any FAILED case ───────────────
    $reportJson = Join-Path $ReportDir "index.json"
    if (!(Test-Path $reportJson)) {
        Write-Error "No report generated at $reportJson (fuzzing client exit code $exitCode)."
        exit 1
    }

    $verdict = & $RepoRoot/scripts/check-autobahn-report.py $reportJson
    if ($LASTEXITCODE -ne 0) {
        Write-Error $verdict
        exit 1
    }

    Write-Host $verdict
    Write-Host "Report: $ReportDir/index.html" -ForegroundColor Cyan
    exit 0
} finally {
    if ($server -and !$server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
