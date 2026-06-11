<#
.SYNOPSIS
    Runs Autobahn WebSocket compliance tests against PicoNode WebSocket implementation.
.DESCRIPTION
    1. Builds a WebSocket echo server
    2. Installs Autobahn TestSuite if not present
    3. Runs Autobahn fuzzing client against the server
    4. Generates a compliance report
#>

$RepoRoot = Split-Path $PSScriptRoot -Parent
$ServerPort = 9002

# Check Python/Autobahn availability
$python = Get-Command python3 -ErrorAction SilentlyContinue
if (!$python) { $python = Get-Command python -ErrorAction SilentlyContinue }
if (!$python) {
    Write-Error "Python is required for Autobahn TestSuite"
    exit 1
}

# Install Autobahn if needed
$autobahn = & $python.Source -c "import autobahn; print(autobahn.__version__)" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing Autobahn TestSuite..." -ForegroundColor Cyan
    & $python.Source -m pip install autobahntestsuite
}

# Create Autobahn config
$configPath = "$RepoRoot\scripts\autobahn-config.json"
@"
{
    "outdir": "$RepoRoot\.tools\autobahn-reports",
    "servers": [
        {
            "agent": "PicoNode",
            "url": "ws://localhost:$ServerPort",
            "options": {"failByDrop": false}
        }
    ],
    "cases": ["*"],
    "exclude-cases": [],
    "exclude-agent-cases": {}
}
"@ | Out-File -Encoding UTF8 $configPath

# Start WebSocket echo server
Write-Host "Starting WebSocket echo server on port $ServerPort..." -ForegroundColor Cyan
$serverJob = Start-Job -ScriptBlock {
    Set-Location $using:RepoRoot
    dotnet run --project samples/PicoNode.Samples.Echo/PicoNode.Samples.Echo.csproj `
        -- --port $using:ServerPort
}

Start-Sleep -Seconds 3

# Run Autobahn fuzzing client
Write-Host "Running Autobahn TestSuite..." -ForegroundColor Cyan
& $python.Source -m autobahn.testee --ws "ws://localhost:$ServerPort/ws" --outdir "$RepoRoot\.tools\autobahn-reports"

# Cleanup
Stop-Job $serverJob -ErrorAction SilentlyContinue
Remove-Job $serverJob -ErrorAction SilentlyContinue

Write-Host "Test complete. Report: .tools/autobahn-reports/index.html" -ForegroundColor Cyan
