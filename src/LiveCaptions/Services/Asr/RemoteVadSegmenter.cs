using System.Collections.Concurrent;
using System.Text.Json;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// FireRedVAD streaming detector served by the PyTorch sidecar (/vad endpoint).
///
/// The audio thread must never block on HTTP, so blocks are queued and posted by
/// a background worker in 300 ms batches; <see cref="SpeechActive"/> reflects the
/// most recent completed batch (bounded extra latency, capture stays live).
/// </summary>
public sealed class RemoteVadSegmenter : IVad
{
    private const int BlockSamples = 1600;      // 100 ms at 16 kHz
    private const int BatchBlocks = 3;          // one request per 300 ms of audio
    private const int MaxQueuedBlocks = 50;     // ~5 s of backlog before dropping
    private const int UnavailableAfterFailures = 3;
    private const int RetryAfterMs = 5000;      // keep probing while the sidecar is down

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _baseAddress;
    private readonly ConcurrentQueue<float[]> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _pendingGate = new();
    private readonly float[] _pending = new float[BlockSamples];
    private int _pendingCount;
    private int _queuedBlocks;

    private volatile int _consecutiveFailures;
    private volatile bool _speechActive;
    private volatile bool _resetPending = true;
    private long _fedSamples;
    private long _droppedBlocks;
    private long _nextRetryAt;

    public RemoteVadSegmenter(string baseAddress)
    {
        _baseAddress = baseAddress.TrimEnd('/');
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>False while the sidecar keeps failing; the pipeline then uses the
    /// energy gate. The worker keeps probing so the detector recovers by itself.</summary>
    public bool IsAvailable => _consecutiveFailures < UnavailableAfterFailures;

    public string Engine => "FireRedVAD (流式)";

    public long FedSamples => Interlocked.Read(ref _fedSamples);

    public bool SpeechActive => _speechActive;

    /// <summary>Called on the audio thread: only buffers and signals the worker.</summary>
    public void Accept(float[] samples, int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _fedSamples, count);

        var offset = 0;
        while (offset < count)
        {
            bool flushed;
            lock (_pendingGate)
            {
                var take = Math.Min(BlockSamples - _pendingCount, count - offset);
                Array.Copy(samples, offset, _pending, _pendingCount, take);
                _pendingCount += take;
                offset += take;

                flushed = _pendingCount == BlockSamples;
                if (flushed)
                {
                    var block = new float[BlockSamples];
                    Array.Copy(_pending, block, BlockSamples);
                    _pendingCount = 0;
                    Enqueue(block);
                }
            }

            if (flushed) _signal.Release();
        }
    }

    private void Enqueue(float[] block)
    {
        _queue.Enqueue(block);
        if (Interlocked.Increment(ref _queuedBlocks) > MaxQueuedBlocks && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queuedBlocks);
            var dropped = Interlocked.Increment(ref _droppedBlocks);
            if (dropped % 20 == 1)
            {
                Log.Write($"[vad] FireRed worker is behind; dropped {dropped} blocks");
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        var batch = new List<float>(BlockSamples * BatchBlocks);
        var bytes = new byte[BlockSamples * BatchBlocks * sizeof(float)];

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            batch.Clear();
            while (batch.Count < BlockSamples * BatchBlocks && _queue.TryDequeue(out var block))
            {
                Interlocked.Decrement(ref _queuedBlocks);
                batch.AddRange(block);
            }

            if (batch.Count == 0) continue;

            // While the sidecar is down, keep probing slowly so the detector can
            // recover without restarting the app.
            var now = Environment.TickCount64;
            if (_consecutiveFailures >= UnavailableAfterFailures && now < Interlocked.Read(ref _nextRetryAt))
            {
                continue;
            }

            Buffer.BlockCopy(batch.ToArray(), 0, bytes, 0, batch.Count * sizeof(float));

            var url = _resetPending ? $"{_baseAddress}/vad?reset=1" : $"{_baseAddress}/vad";
            try
            {
                using var content = new ByteArrayContent(bytes, 0, batch.Count * sizeof(float));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var response = await _http.PostAsync(url, content, _cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"vad endpoint returned {(int)response.StatusCode}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: _cts.Token).ConfigureAwait(false);
                _speechActive = document.RootElement.TryGetProperty("speech", out var speech) && speech.GetBoolean();
                _resetPending = false;

                if (_consecutiveFailures > 0)
                {
                    Log.Write("[vad] FireRed endpoint recovered");
                    _consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A broken VAD must not stop capture: treat audio as speech so the
                // pipeline keeps decoding instead of silently discarding runs.
                _speechActive = true;
                var failures = _consecutiveFailures + 1;
                _consecutiveFailures = failures;
                Interlocked.Exchange(ref _nextRetryAt, Environment.TickCount64 + RetryAfterMs);
                if (failures == UnavailableAfterFailures)
                {
                    Log.Write($"[vad] FireRed unavailable ({ex.Message}); pipeline falls back to the energy gate");
                }
                else if (failures == 1)
                {
                    Log.Write($"[vad] FireRed request failed: {ex.Message}");
                }
            }
        }
    }

    public void Reset()
    {
        lock (_pendingGate)
        {
            _pendingCount = 0;
        }

        while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _queuedBlocks);
        _speechActive = false;
        _resetPending = true;
        Interlocked.Exchange(ref _fedSamples, 0);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
        _signal.Dispose();
        _http.Dispose();
    }
}
