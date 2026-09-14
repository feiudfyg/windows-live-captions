using System.Diagnostics;
using System.Text;
using LiveCaptions.Models;

namespace LiveCaptions.Services;

/// <summary>
/// Manages a local llama-server (llama.cpp) child process that the app uses as
/// its translation backend. Replaces heavier external frontends: the official
/// nightly builds track the latest architectures (e.g. Qwen3.8 MTP) that the
/// bundled LLamaSharp runtime cannot load yet.
/// </summary>
public sealed class LocalLlamaServer : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly AppSettings _settings;
    private Process? _process;

    public LocalLlamaServer(AppSettings settings) => _settings = settings;

    /// <summary>Port actually in use (may differ from the configured one when it was busy).</summary>
    public int ActualPort { get; private set; }

    public string BaseAddress => $"http://127.0.0.1:{ActualPort}/v1";

    public static string ServerPath => Path.Combine(AppPaths.RuntimeDirectory, "llama.cpp", "llama-server.exe");

    public static bool RuntimeAvailable => File.Exists(ServerPath);

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(string modelPath, CancellationToken ct = default)
    {
        if (!RuntimeAvailable)
        {
            throw new FileNotFoundException(
                $"未找到 llama-server.exe（{ServerPath}）。请运行 scripts/fetch-llama-server.ps1 下载运行时。");
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"未找到翻译模型: {modelPath}");
        }

        var configured = _settings.LlamaServerPort;
        if (await IsReadyAsync(configured, ct).ConfigureAwait(false))
        {
            ActualPort = configured;
            return; // a llama-server instance is already serving on this port
        }

        ActualPort = IsPortFree(configured) ? configured : FindFreePort(configured);
        if (ActualPort != configured)
        {
            Log.Write($"[llama-server] port {configured} is busy, using {ActualPort}");
        }

        var arguments = new List<string>
        {
            "-m", modelPath,
            "--host", "127.0.0.1",
            "--port", ActualPort.ToString(),
            "-ngl", _settings.GpuLayerCount.ToString(),
            "-c", _settings.ContextSize.ToString(),
            "--flash-attn", "auto",
            "--reasoning", "off",
        };

        if (!string.IsNullOrWhiteSpace(_settings.LlamaServerExtraArgs))
        {
            arguments.AddRange(SplitArguments(_settings.LlamaServerExtraArgs));
        }

        var startInfo = new ProcessStartInfo(ServerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(ServerPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Log.Write($"[llama-server] starting: {Path.GetFileName(modelPath)} on port {ActualPort}");
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 llama-server.exe");
        _process.OutputDataReceived += (_, e) => LogLine(e.Data);
        _process.ErrorDataReceived += (_, e) => LogLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"llama-server 已退出（代码 {_process.ExitCode}），详见日志 livecaptions.log");
            }

            if (await IsReadyAsync(ActualPort, ct).ConfigureAwait(false))
            {
                Log.Write($"[llama-server] ready on port {ActualPort}");
                return;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        Dispose();
        throw new TimeoutException("llama-server 启动超时（5 分钟）");
    }

    private static async Task<bool> IsReadyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{port}/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            return !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return true;
        }
    }

    private static int FindFreePort(int start)
    {
        for (var port = start + 1; port < Math.Min(start + 100, 65535); port++)
        {
            if (IsPortFree(port)) return port;
        }

        return start;
    }

    private static void LogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Log.Write($"[llama-server] {line}");
    }

    /// <summary>Split a command line into arguments, honouring double quotes.</summary>
    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    public void Dispose()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                Log.Write("[llama-server] stopping");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Process teardown is best-effort.
        }
        finally
        {
            process.Dispose();
        }
    }
}
