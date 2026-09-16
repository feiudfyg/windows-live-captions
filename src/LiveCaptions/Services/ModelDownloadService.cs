using System.Net;
using System.Net.Http.Headers;

namespace LiveCaptions.Services;

/// <summary>
/// Downloads model files with progress reporting and HTTP range resume.
/// </summary>
public sealed class ModelDownloadService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveCaptions/1.0");
        return client;
    }

    public async Task EnsureDownloadedAsync(ModelEntry entry, IProgress<(string file, double ratio, long received, long total)>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelCatalog.ModelsDirectory);

        if (entry.Archive is { } archive)
        {
            await EnsureArchiveAsync(archive, progress, ct).ConfigureAwait(false);
            return;
        }

        foreach (var file in entry.Files)
        {
            var path = ModelCatalog.PathOf(file.FileName);
            await ResumeDownloadAsync(file, path, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Download a .tar.bz2 model bundle and extract it into the models directory.</summary>
    private static async Task EnsureArchiveAsync(ModelArchive archive,
        IProgress<(string file, double ratio, long received, long total)>? progress, CancellationToken ct)
    {
        var targetDirectory = Path.Combine(ModelCatalog.ModelsDirectory, archive.ExtractedDirectory);
        if (Directory.Exists(targetDirectory))
        {
            progress?.Report((archive.ExtractedDirectory, 1.0, 0, 0));
            return;
        }

        var downloads = Path.Combine(ModelCatalog.ModelsDirectory, "_downloads");
        Directory.CreateDirectory(downloads);
        var archivePath = Path.Combine(downloads, archive.ArchiveName);

        var file = new ModelFile(archive.ArchiveName, archive.Url, archive.ApproxBytes);
        await ResumeDownloadAsync(file, archivePath, progress, ct).ConfigureAwait(false);

        // bsdtar ships with Windows 10+ and handles .tar.bz2 natively. Extract into
        // a private directory and move it into place afterwards so an interrupted
        // extraction can never be mistaken for a finished model directory.
        var extractRoot = Path.Combine(ModelCatalog.ModelsDirectory, "_extract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractRoot);
        try
        {
            var tarPath = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (!File.Exists(tarPath)) tarPath = "tar.exe";

            var startInfo = new System.Diagnostics.ProcessStartInfo(tarPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-xjf");
            startInfo.ArgumentList.Add(archivePath);
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(extractRoot);

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 tar.exe 解压模型包");

            // Drain both pipes (a full stdout pipe would deadlock tar).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw;
            }

            var error = (await stderrTask.ConfigureAwait(false)).Trim();
            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"模型包解压失败: {error}");
            }

            var extracted = Path.Combine(extractRoot, archive.ExtractedDirectory);
            if (!Directory.Exists(extracted))
            {
                throw new InvalidOperationException($"模型包解压后未找到目录 {archive.ExtractedDirectory}");
            }

            if (!Directory.Exists(targetDirectory))
            {
                Directory.Move(extracted, targetDirectory);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
            }
            catch
            {
                // Leftover temp dirs are ignored (and can be deleted by hand).
            }
        }

        try
        {
            File.Delete(archivePath);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    private static async Task ResumeDownloadAsync(ModelFile file, string path,
        IProgress<(string file, double ratio, long received, long total)>? progress, CancellationToken ct)
    {
        await DownloadAsync(file, path, progress, ct, allowRestart: true).ConfigureAwait(false);
    }

    private static async Task DownloadAsync(ModelFile file, string path,
        IProgress<(string file, double ratio, long received, long total)>? progress, CancellationToken ct,
        bool allowRestart)
    {
        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The local file is at least as large as the server's copy: either it is
            // already complete or it is a corrupt leftover - start over once.
            if (allowRestart)
            {
                Log.Write($"[download] {file.FileName}: no range available from {existing} bytes; restarting");
                TryDelete(path);
                await DownloadAsync(file, path, progress, ct, allowRestart: false).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException($"无法下载 {file.FileName}：服务器返回 416");
        }

        if (response.StatusCode == HttpStatusCode.OK && existing > 0)
        {
            // Server ignored the range request: start over.
            existing = 0;
        }

        var totalKnown = false;
        var total = file.ApproxBytes;
        if (response.Content.Headers.ContentRange?.Length is { } rangeLength && rangeLength > 0)
        {
            total = rangeLength;
            totalKnown = true;
        }
        else if (response.Content.Headers.ContentLength is { } len && len > 0)
        {
            total = len + existing;
            totalKnown = true;
        }

        response.EnsureSuccessStatusCode();

        var mode = existing > 0 ? FileMode.Append : FileMode.Create;
        await using var target = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[1 << 20];
        long received = existing;
        var lastReport = DateTime.UtcNow;

        while (true)
        {
            // A half-open connection must not stall the UI forever: give up when no
            // data arrives for a while and keep the partial file for the next try.
            var readTask = source.ReadAsync(buffer, ct).AsTask();
            var finished = await Task.WhenAny(readTask, Task.Delay(StallTimeout, ct)).ConfigureAwait(false);
            if (finished != readTask)
            {
                _ = readTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"下载停滞（{StallTimeout.TotalSeconds:0} 秒无数据）；已保留 {FormatBytes(received)}，可重新下载续传");
            }

            var read = await readTask.ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 200)
            {
                lastReport = now;
                progress?.Report((file.FileName, total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0, received, total));
            }
        }

        if (totalKnown && received < total)
        {
            TryDelete(path);
            throw new InvalidDataException($"下载不完整：{received}/{total} 字节（已删除，请重试）");
        }

        progress?.Report((file.FileName, 1.0, received, total));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
