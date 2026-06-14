# PicoWeb AOT Publish Integration Test
$ErrorActionPreference = "Stop"
$tempDir = Join-Path $env:TEMP "PicoWebAotTest_$(Get-Random)"

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
    <ProjectReference Include="$PSScriptRoot\..\src\PicoWeb\PicoWeb.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $tempDir "AotTest.csproj")

    @"
using PicoWeb;
using PicoNode.Web;

var api = new WebApiBuilder()
    .ConfigureApp(o => new WebAppOptions { ServerHeader = "AotTest" })
    .Build();

api.MapGet("/api/health", (WebContext ctx) =>
{
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new { status = "ok" });
    return Results.Json(200, bytes);
});

var cts = new CancellationTokenSource();
var task = api.RunAsync("http://127.0.0.1:9876", cts.Token);
await Task.Delay(1000);

using var client = new HttpClient();
var response = await client.GetAsync("http://127.0.0.1:9876/api/health");
var body = await response.Content.ReadAsStringAsync();
cts.Cancel();
await task;

if (response.StatusCode -ne [System.Net.HttpStatusCode]::OK) { exit 1 }
if (-not $body.Contains("ok")) { exit 1 }
Write-Host "PASS"
"@ | Set-Content (Join-Path $tempDir "Program.cs")

    # AOT publish
    dotnet publish (Join-Path $tempDir "AotTest.csproj") -c Release -r win-x64 --self-contained

    # Run
    & (Join-Path $tempDir "bin\Release\net10.0\win-x64\publish\AotTest.exe")

    Write-Host "AOT integration test PASSED"
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
