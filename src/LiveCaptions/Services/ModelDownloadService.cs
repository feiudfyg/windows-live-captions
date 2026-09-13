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

        // bsdtar ships with Windows 10+ and handles .tar.bz2 natively.
        var startInfo = new System.Diagnostics.ProcessStartInfo("tar.exe")
        {
            Arguments = $"-xjf \"{archivePath}\" -C \"{ModelCatalog.ModelsDirectory}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 tar.exe 解压模型包");
        var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0 || !Directory.Exists(targetDirectory))
        {
            throw new InvalidOperationException($"模型包解压失败: {error.Trim()}");
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

    private static async Task ResumeDownloadAsync(ModelFile file, string path,
        IProgress<(string file, double ratio, long received, long total)>? progress, CancellationToken ct)
    {
        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var total = file.ApproxBytes;
        if (response.Content.Headers.ContentRange?.Length is { } rangeLength && rangeLength > 0)
        {
            total = rangeLength;
        }
        else if (response.Content.Headers.ContentLength is { } len && len > 0)
        {
            total = len + existing;
        }

        if (response.StatusCode == HttpStatusCode.OK && existing > 0)
        {
            // Server ignored the range request: start over.
            existing = 0;
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
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
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

        progress?.Report((file.FileName, 1.0, received, total));
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
