# PicoWeb AOT Publish Integration Test
param(
    # Optional; auto-detected from the current host when omitted.
    [string]$RuntimeIdentifier = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "tests/PicoWeb.AotVerify/PicoWeb.AotVerify.csproj"
$isWindowsHost = $env:OS -eq 'Windows_NT'

if (-not $RuntimeIdentifier) {
    $osName = if (
        [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows
        )
    ) {
        'win'
    }
    elseif (
        [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX
        )
    ) {
        'osx'
    }
    else {
        'linux'
    }
    $archName = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $RuntimeIdentifier = "$osName-$archName"
}
$exeName = if ($RuntimeIdentifier -like 'win-*') { 'PicoWeb.AotVerify.exe' } else { 'PicoWeb.AotVerify' }

# ILC locates the MSVC toolchain via vswhere.exe (Windows hosts only); add its
# standard installer location when the shell does not already provide it.
if ($isWindowsHost) {
    $vswhereDir = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if (
        (Test-Path (Join-Path $vswhereDir "vswhere.exe")) -and
        -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)
    ) {
        $env:PATH = "$vswhereDir;$env:PATH"
    }
}

Write-Host "AOT publish verification project: tests/PicoWeb.AotVerify ($RuntimeIdentifier)"

# A committed project inside the repository, not a generated temp project:
# a generated project outside the tree made MSBuild resolve the referenced
# projects' relative references against the wrong base on macOS/Linux (MSB3202).
# Do not pass -p:PublishAot=true here: it is a global property that would flow
# into the netstandard2.0 generator projects and override the PublishAot=false
# guard in Directory.Build.targets (NETSDK1207). The repo baseline already
# defaults the minimal AOT tier (PublishAot + TrimMode=full).
# The publish output is captured because ILC's trim/AOT analysis is part of the
# verdict, not decoration. The generated controller endpoints are what this gate
# exists for, and a reflection-dependent path inside them can still answer a probe
# (a preserved type activates fine) while the publish names the offending line with
# IL2026/IL3050. The baseline is ZERO such warnings, so one of them is a regression
# and is reported with the line that caused it rather than left in a passing log.
dotnet publish $projectPath -c Release -r $RuntimeIdentifier --self-contained 2>&1 |
    Tee-Object -Variable publishOutput
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $RuntimeIdentifier with exit code $LASTEXITCODE"
}

$trimWarnings = @($publishOutput | Select-String -Pattern 'warning IL[23][0-9]{3}')
if ($trimWarnings.Count -gt 0) {
    $trimWarnings | ForEach-Object { Write-Host $_ }
    throw "AOT publish emitted $($trimWarnings.Count) trim/AOT warning(s) for ${RuntimeIdentifier} - a reflection-dependent path survived the compile (IL2026/IL3050 name it). Fix the path; do not suppress the warning."
}

# Run the native binary (exit code carries the verdict).
$exePath = Join-Path $repoRoot "tests/PicoWeb.AotVerify/bin/Release/net10.0/$RuntimeIdentifier/publish/$exeName"
& $exePath
if ($LASTEXITCODE -ne 0) { throw "AOT integration test failed with exit code $LASTEXITCODE" }

Write-Host "AOT integration test PASSED ($RuntimeIdentifier)"
