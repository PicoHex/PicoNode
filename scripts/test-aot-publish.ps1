# PicoWeb AOT Publish Integration Test
param(
    # Optional; auto-detected from the current host when omitted.
    [string]$RuntimeIdentifier = ""
)

$ErrorActionPreference = "Stop"

# Use the portable temp API: $env:TEMP is not set on Linux/macOS runners, so
# Join-Path $env:TEMP would throw before the gate even starts.
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "PicoWebAotTest_$(Get-Random)"
$isWindowsHost = $env:OS -eq 'Windows_NT'

# Canonical absolute path: a `..` inside a ProjectReference is not normalised on
# macOS/Linux, which makes MSBuild resolve the referenced project's own relative
# references against the temp directory (MSB3202: project file not found).
$picoWebProject = (
    Resolve-Path (Join-Path $PSScriptRoot "../src/Web/PicoWeb/PicoWeb.csproj")
).Path

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
$exeName = if ($RuntimeIdentifier -like 'win-*') { 'AotTest.exe' } else { 'AotTest' }

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

try {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    # Create test project
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <StripSymbols>true</StripSymbols>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$picoWebProject" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $tempDir "AotTest.csproj")

    @"
using PicoJetson;
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(_ => new WebAppOptions { ServerHeader = "AotTest" })
    .Build();
var app = api.App;

app.MapGet("/api/health", (WebContext ctx, CancellationToken _) =>
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new HealthDto { Status = "ok" });
    return ValueTask.FromResult(Results.Json(200, bytes));
});

// Port 0: the OS assigns a free port, so a second concurrent run (or an
// unrelated service) cannot collide with this gate.
await using var server = new WebServer(
    app,
    new WebServerOptions
    {
        Endpoint = new System.Net.IPEndPoint(
            System.Net.IPAddress.Loopback,
            0
        ),
    }
);
await server.StartAsync();
var port = ((System.Net.IPEndPoint)server.LocalEndPoint!).Port;

// Probe the listener (retry instead of a fixed sleep).
using var client = new HttpClient();
HttpResponseMessage? response = null;
string? body = null;
for (var attempt = 0; attempt < 25; attempt++)
{
    try
    {
        response = await client.GetAsync("http://127.0.0.1:" + port + "/api/health");
        body = await response.Content.ReadAsStringAsync();
        break;
    }
    catch (HttpRequestException)
    {
        await Task.Delay(200);
    }
}

// Graceful stop before disposal (DisposeAsync then no-ops) so the probe result
// is final before the process tears the server down.
await server.StopAsync();

// Also exercise the documented api.RunAsync(...) entry point under AOT: port 0
// keeps the OS-assigned-port guarantee, and cancellation drives ProcessShutdown.
using (var runCts = new CancellationTokenSource())
{
    var runTask = api.RunAsync("http://127.0.0.1:0", runCts.Token);
    await Task.Delay(200);
    await runCts.CancelAsync();
    await runTask;
}

if (response is null || body is null)
{
    Console.WriteLine("FAIL: server did not answer");
    return 1;
}

if (response.StatusCode != System.Net.HttpStatusCode.OK)
{
    Console.WriteLine("FAIL: status " + response.StatusCode);
    return 1;
}

if (!body.Contains("ok"))
{
    Console.WriteLine("FAIL: unexpected body '" + body + "'");
    return 1;
}

Console.WriteLine("PASS");
return 0;

public sealed class HealthDto
{
    public string Status { get; set; } = string.Empty;
}
"@ | Set-Content (Join-Path $tempDir "Program.cs")

    # AOT publish
    dotnet publish (Join-Path $tempDir "AotTest.csproj") -c Release -r $RuntimeIdentifier --self-contained
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $RuntimeIdentifier with exit code $LASTEXITCODE"
    }

    # Run (exit code carries the verdict)
    & (Join-Path $tempDir "bin/Release/net10.0/$RuntimeIdentifier/publish/$exeName")
    if ($LASTEXITCODE -ne 0) { throw "AOT integration test failed with exit code $LASTEXITCODE" }

    Write-Host "AOT integration test PASSED ($RuntimeIdentifier)"
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
