using System.Diagnostics;
using LiveCaptions.Models;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Manages the Python sidecar that serves Cohere Transcribe at full precision
/// (official bf16 weights, PyTorch CUDA). Mirrors <see cref="LocalLlamaServer"/>:
/// the app starts the process on demand, waits for /health and reuses a server
/// that is already listening on the configured port.
/// </summary>
public sealed class PyTorchAsrServer : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly AppSettings _settings;
    private Process? _process;

    public PyTorchAsrServer(AppSettings settings) => _settings = settings;

    /// <summary>Port actually in use (may differ from the configured one when it was busy).</summary>
    public int ActualPort { get; private set; }

    public string BaseAddress => $"http://127.0.0.1:{ActualPort}";

    public bool IsRunning => _process is { HasExited: false };

    public string PythonPath => string.IsNullOrWhiteSpace(_settings.CohereAsrPythonPath)
        ? Path.Combine(AppPaths.DataRoot, "python-env", "Scripts", "python.exe")
        : _settings.CohereAsrPythonPath;

    public string ScriptPath => string.IsNullOrWhiteSpace(_settings.CohereAsrScriptPath)
        ? ResolveScript()
        : _settings.CohereAsrScriptPath;

    /// <summary>Hugging Face cache used for the gated model weights.</summary>
    public static string HuggingFaceHome => Path.Combine(AppPaths.DataRoot, "hf-cache");

    private static string ResolveScript()
    {
        var candidates = new List<string>();
        if (AppPaths.RepositoryRoot is { } root)
        {
            candidates.Add(Path.Combine(root, "tools", "cohere_pytorch", "server.py"));
        }

        candidates.Add(Path.Combine(AppPaths.RuntimeDirectory, "cohere-asr", "server.py"));
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!File.Exists(PythonPath))
        {
            throw new FileNotFoundException(
                $"未找到 Python 环境（{PythonPath}）。请先运行 scripts/fetch-cohere-asr.ps1 准备全精度 PyTorch 识别环境。");
        }

        if (!File.Exists(ScriptPath))
        {
            throw new FileNotFoundException($"未找到 sidecar 服务脚本: {ScriptPath}");
        }

        var configured = _settings.CohereAsrPort;
        if (await IsReadyAsync(configured, ct).ConfigureAwait(false))
        {
            ActualPort = configured;
            Log.Write($"[cohere-asr] reusing server on port {configured}");
            return;
        }

        ActualPort = IsPortFree(configured) ? configured : FindFreePort(configured);
        if (ActualPort != configured)
        {
            Log.Write($"[cohere-asr] port {configured} is busy, using {ActualPort}");
        }

        var startInfo = new ProcessStartInfo(PythonPath)
        {
            WorkingDirectory = Path.GetDirectoryName(ScriptPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(ActualPort.ToString());
        startInfo.ArgumentList.Add("--dtype");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_settings.CohereAsrDtype) ? "bfloat16" : _settings.CohereAsrDtype);

        var vadModel = Path.Combine(AppPaths.ModelsDirectory, "FireRedVAD", "Stream-VAD");
        if (Directory.Exists(vadModel))
        {
            startInfo.ArgumentList.Add("--vad-model");
            startInfo.ArgumentList.Add(vadModel);
        }

        startInfo.Environment["HF_HOME"] = HuggingFaceHome;
        startInfo.Environment["HF_HUB_DISABLE_XET"] = "1";
        startInfo.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        // Weights are cached by the setup script; a flaky network must not stall startup.
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_VERBOSITY"] = "error";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var tokenPath = Path.Combine(HuggingFaceHome, "token");
        if (File.Exists(tokenPath))
        {
            try
            {
                startInfo.Environment["HF_TOKEN"] = File.ReadAllText(tokenPath).Trim();
            }
            catch (Exception ex)
            {
                Log.Write($"[cohere-asr] could not read HF token: {ex.Message}");
            }
        }

        Log.Write($"[cohere-asr] starting {Path.GetFileName(PythonPath)} on port {ActualPort} (model load ~11s)");
        _process?.Dispose();
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Python sidecar");
        _process.OutputDataReceived += (_, e) => LogLine(e.Data);
        _process.ErrorDataReceived += (_, e) => LogLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"ASR 服务已退出（代码 {_process.ExitCode}），详见日志 livecaptions.log");
            }

            if (await IsReadyAsync(ActualPort, ct).ConfigureAwait(false))
            {
                Log.Write($"[cohere-asr] ready on port {ActualPort}");
                return;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        Dispose();
        throw new TimeoutException("ASR 服务启动超时（3 分钟）");
    }

    /// <summary>Any healthy server on the configured port is used as-is.</summary>
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
        Log.Write($"[cohere-asr] {line}");
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
                Log.Write("[cohere-asr] stopping");
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    Log.Write("[cohere-asr] still alive after 5s; killing again");
                    try { process.Kill(entireProcessTree: true); } catch { }
                    if (!process.WaitForExit(5000)) Log.Write("[cohere-asr] could not be stopped");
                }
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
