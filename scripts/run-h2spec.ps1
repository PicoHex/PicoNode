<#
.SYNOPSIS
    Runs h2spec protocol compliance tests against PicoNode's HTTP/2 implementation.
.DESCRIPTION
    1. Builds the PicoNode sample HTTP server
    2. Downloads h2spec (v2.6.0) if not present (Windows zip / Linux tar.gz)
    3. Starts the server on the given port
    4. Runs the strict h2spec suite against it
    5. Kills the server and propagates h2spec's exit code (0 = all tests passed)

.PARAMETER Port
    Port the sample server listens on (default 7003).

.EXAMPLE
    pwsh ./scripts/run-h2spec.ps1
    pwsh ./scripts/run-h2spec.ps1 -Port 7003
#>
param(
    [int]$Port = 7003
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$H2SpecDir = Join-Path $RepoRoot ".tools/h2spec"
$H2SpecVersion = "2.6.0"
# Works in both Windows PowerShell 5.1 and PowerShell Core.
$IsWindowsPlatform = $env:OS -eq 'Windows_NT'

function Get-H2SpecExe {
    if ($IsWindowsPlatform) {
        return Join-Path $H2SpecDir "h2spec.exe"
    }
    return Join-Path $H2SpecDir "h2spec"
}

# ── 1. Ensure h2spec is available ────────────────────────────────────
$h2spec = Get-H2SpecExe
if (!(Test-Path $h2spec)) {
    $asset = if ($IsWindowsPlatform) {
        "h2spec_windows_amd64.zip"
    } else {
        "h2spec_linux_amd64.tar.gz"
    }
    $url = "https://github.com/summerwind/h2spec/releases/download/v$H2SpecVersion/$asset"
    $archive = Join-Path $H2SpecDir $asset

    Write-Host "Downloading h2spec v$H2SpecVersion ($asset)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $H2SpecDir | Out-Null
    Invoke-WebRequest -Uri $url -OutFile $archive

    if ($IsWindowsPlatform) {
        Expand-Archive -Path $archive -DestinationPath $H2SpecDir -Force
    } else {
        tar -xzf $archive -C $H2SpecDir
    }
    Remove-Item $archive

    if (!(Test-Path $h2spec)) {
        Write-Error "h2spec binary not found after extraction at $h2spec"
        exit 1
    }
}

# ── 2. Build and start the sample server ─────────────────────────────
Write-Host "Building sample server..." -ForegroundColor Cyan
& dotnet build "$RepoRoot/samples/PicoNode.Samples.Http/PicoNode.Samples.Http.csproj" -c Release -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "Sample server build failed."
    exit 1
}

Write-Host "Starting test server on port $Port..." -ForegroundColor Cyan
# The sample stays alive without stdin (Task.Delay(Timeout.Infinite)), so a
# detached process needs no stdin plumbing.
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
    # Wait for the server to accept connections.
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

    # ── 3. Run h2spec (strict suite — includes strict test cases) ────
    Write-Host "Running h2spec (strict)..." -ForegroundColor Cyan
    & $h2spec -h 127.0.0.1 -p $Port -S
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "h2spec: ALL TESTS PASSED" -ForegroundColor Green
    } else {
        Write-Host "h2spec: FAILURES DETECTED (exit code $exitCode)" -ForegroundColor Red
    }

    exit $exitCode
} finally {
    if ($server -and !$server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
