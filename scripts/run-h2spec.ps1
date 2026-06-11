<#
.SYNOPSIS
    Runs h2spec protocol compliance tests against PicoNode HTTP/2 implementation.
.DESCRIPTION
    1. Builds a minimal HTTP/2 echo server from the solution
    2. Downloads h2spec if not present
    3. Runs h2spec against the server
    4. Reports pass/fail
#>

$RepoRoot = Split-Path $PSScriptRoot -Parent
$H2SpecDir = "$RepoRoot\.tools\h2spec"
$H2SpecExe = "$H2SpecDir\h2spec.exe"
$H2SpecVersion = "2.6.0"
$H2SpecUrl = "https://github.com/summerwind/h2spec/releases/download/v$H2SpecVersion/h2spec_windows_amd64.tar.gz"
$ServerPort = 9001

# Ensure h2spec is available
if (!(Test-Path $H2SpecExe)) {
    Write-Host "Downloading h2spec v$H2SpecVersion..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $H2SpecDir | Out-Null
    $archive = "$H2SpecDir\h2spec.tar.gz"
    Invoke-WebRequest -Uri $H2SpecUrl -OutFile $archive
    tar -xzf $archive -C $H2SpecDir
    Remove-Item $archive
}

# Start the test server
Write-Host "Starting test server on port $ServerPort..." -ForegroundColor Cyan
$serverJob = Start-Job -ScriptBlock {
    param($port)
    # Build and run a minimal HTTP/2 echo server
    # This uses the library's WebApp + WebServer to create a test endpoint
    Set-Location $using:RepoRoot
    dotnet run --project samples/PicoNode.Samples.Http/PicoNode.Samples.Http.csproj `
        -- --port $port --h2c
} -ArgumentList $ServerPort

Start-Sleep -Seconds 3

# Run h2spec
Write-Host "Running h2spec..." -ForegroundColor Cyan
$results = & $H2SpecExe -p $ServerPort -o json 2>&1
$exitCode = $LASTEXITCODE

# Parse and report results
$json = $results | ConvertFrom-Json
$passed = ($json | Where-Object { $_.status -eq "pass" }).Count
$failed = ($json | Where-Object { $_.status -eq "fail" }).Count

Write-Host "Results:" -ForegroundColor Cyan
Write-Host "  Passed: $passed" -ForegroundColor Green
Write-Host "  Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })

# Cleanup
Stop-Job $serverJob -ErrorAction SilentlyContinue
Remove-Job $serverJob -ErrorAction SilentlyContinue

exit $exitCode
