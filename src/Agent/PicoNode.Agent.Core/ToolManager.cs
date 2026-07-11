using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace PicoNode.Agent.Domain;

/// <summary>
/// Manages external tools (ripgrep, fd) — checks PATH, downloads from GitHub releases.
/// Same pattern as pi's ensureTool / tools-manager.ts.
/// </summary>
public static class ToolManager
{
    private const string RgVersion = "14.1.1";
    private const string FdVersion = "10.2.0";

    public static string GetPlatformKey()
    {
        var os =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    public static string GetToolDir()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pico-agent"
        );
        return Path.Combine(baseDir, "tools");
    }

    public static async Task<string?> EnsureToolAsync(string tool, CancellationToken ct)
    {
        var pathOnPath = FindOnPath(tool);
        if (pathOnPath is not null)
            return pathOnPath;

        var cacheDir = GetToolDir();
        var pk = GetPlatformKey();
        var exe = OperatingSystem.IsWindows() ? $"{tool}.exe" : tool;
        var cached = Path.Combine(cacheDir, pk, exe);
        if (File.Exists(cached))
            return cached;

        try
        {
            Directory.CreateDirectory(Path.Combine(cacheDir, pk));
            var url = GetDownloadUrl(tool);
            var ext = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            var archive = Path.Combine(cacheDir, pk, $"{tool}{ext}");

            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(archive, bytes, ct);
            ExtractBinary(tool, archive, cached);
            File.Delete(archive);
            return cached;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindOnPath(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? $"{tool}.exe" : tool;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            if (File.Exists(Path.Combine(dir.Trim(), exe)))
                return Path.Combine(dir.Trim(), exe);
        }
        return null;
    }

    private static string GetDownloadUrl(string tool)
    {
        var (repo, version, asset) = tool switch
        {
            "rg" => ("BurntSushi/ripgrep", RgVersion, RgAssetName()),
            "fd" => ("sharkdp/fd", $"v{FdVersion}", FdAssetName()),
            _ => throw new ArgumentException($"Unknown tool: {tool}"),
        };
        return $"https://github.com/{repo}/releases/download/{version}/{asset}";
    }

    private static string RgAssetName()
    {
        var plat = RgPlatformSuffix();
        var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        return $"ripgrep-{RgVersion}-{plat}.{ext}";
    }

    private static string FdAssetName()
    {
        var plat = FdPlatformSuffix();
        var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        return $"fd-v{FdVersion}-{plat}.{ext}";
    }

    private static string RgPlatformSuffix()
    {
        var isWin = OperatingSystem.IsWindows();
        var isMac = OperatingSystem.IsMacOS();
        var arch = RuntimeInformation.ProcessArchitecture;
        return (isWin, isMac, arch) switch
        {
            (true, _, Architecture.X64) => "x86_64-pc-windows-msvc",
            (true, _, Architecture.Arm64) => "aarch64-pc-windows-msvc",
            (_, true, Architecture.Arm64) => "aarch64-apple-darwin",
            (_, true, _) => "x86_64-apple-darwin",
            (_, _, Architecture.Arm64) => "aarch64-unknown-linux-gnu",
            _ => "x86_64-unknown-linux-musl",
        };
    }

    private static string FdPlatformSuffix()
    {
        var isWin = OperatingSystem.IsWindows();
        var isMac = OperatingSystem.IsMacOS();
        var arch = RuntimeInformation.ProcessArchitecture;
        return (isWin, isMac, arch) switch
        {
            (true, _, Architecture.X64) => "x86_64-pc-windows-msvc",
            (true, _, Architecture.Arm64) => "aarch64-pc-windows-msvc",
            (_, true, Architecture.Arm64) => "aarch64-apple-darwin",
            (_, true, _) => "x86_64-apple-darwin",
            (_, _, Architecture.Arm64) => "aarch64-unknown-linux-gnu",
            _ => "x86_64-unknown-linux-gnu",
        };
    }

    private static void ExtractBinary(string tool, string archive, string output)
    {
        if (OperatingSystem.IsWindows())
        {
            using var zip = ZipFile.OpenRead(archive);
            var exe = $"{tool}.exe";
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase)
            );
            entry?.ExtractToFile(output, true);
        }
        else
        {
            var dir = Path.GetDirectoryName(output)!;
            using var proc = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments =
                        $"-xzf \"{archive}\" -C \"{dir}\" --strip-components=1 \"*/{tool}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            proc?.WaitForExit();
        }
    }
}
