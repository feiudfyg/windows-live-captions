using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LiveCaptions.Models;
using LiveCaptions.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveCaptions.SmokeTest;

/// <summary>
/// Command line harness used to validate the GPU pipelines outside the UI:
///   asr-qwen &lt;model.gguf&gt; &lt;mmproj.gguf&gt; &lt;audio.wav&gt; [language]
///   asr-whisper &lt;model.bin&gt; &lt;audio.wav&gt; [language]
///   mt &lt;model.gguf&gt; &lt;text&gt; &lt;target-language-name&gt;
/// </summary>
internal static class Program
{
    private static string? TextOut;

    private static void WriteResult(string text)
    {
        Console.WriteLine($"[text] {text}");
        if (TextOut is null) return;

        try
        {
            File.AppendAllText(TextOut, text + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // best effort
        }
    }
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Optional: write recognised/translated text to a UTF-8 file so callers
        // do not have to fight console code pages.
        var textOut = Environment.GetEnvironmentVariable("SMOKE_TEXT_OUT");
        if (!string.IsNullOrEmpty(textOut))
        {
            TextOut = textOut;
            try
            {
                File.AppendAllText(TextOut, $"\n### {string.Join(' ', args.Select(a => Path.GetFileName(a)))}\n", Encoding.UTF8);
            }
            catch
            {
                // best effort
            }
        }

        if (args.Length == 0)
        {
            Console.WriteLine("       capture <seconds> [out.wav]");
            Console.WriteLine("       mic <seconds> [system|mic|both] [out.wav]");
            Console.WriteLine("       asr-whisper <model.bin> <audio.wav> [language]");
            Console.WriteLine("       mt <model.gguf> <text> <target-language-name>");
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "capture" when args.Length >= 2 => await CaptureAsync(int.Parse(args[1]), args.Length > 2 ? args[2] : null),
                "mic" when args.Length >= 2 => await AudioInputAsync(
                    args.Length > 2 ? args[2] : "mic",
                    int.Parse(args[1]),
                    args.Length > 3 ? args[3] : null),
                "asr-whisper" when args.Length >= 3 => await AsrWhisperAsync(args[1], args[2], args.Length > 3 ? args[3] : null),
                "asr-zipformer" or "asr-sherpa" when args.Length >= 3 => AsrSherpa(args[1], args[2], args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : "auto"),
                "vad" when args.Length >= 3 => Vad(args[1], args[2], args.Length > 3 ? float.Parse(args[3]) : 0.5f, args.Length > 4 ? float.Parse(args[4]) : 0.3f),
                "mt" when args.Length >= 4 => await TranslateAsync(args[1], args[2], args[3], args.Length > 4 ? args[4] : "auto"),
                _ => throw new ArgumentException("unknown command or missing arguments"),
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex}");
            Console.ResetColor();
            return 2;
        }
    }

        /// <summary>Capture system audio (WASAPI loopback) for N seconds and report levels.</summary>
    private static async Task<int> CaptureAsync(int seconds, string? outWav)
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        Console.WriteLine($"[capture] device: {device.FriendlyName}");

        using var capture = new WasapiLoopbackCapture(device);
        Console.WriteLine($"[capture] format: {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, {capture.WaveFormat.Encoding}");

        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
            ReadFully = false,
        };

        long bytesTotal = 0;
        capture.DataAvailable += (_, e) =>
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref bytesTotal, e.BytesRecorded);
        };

        ISampleProvider chain = buffer.ToSampleProvider();
        if (chain.WaveFormat.Channels >= 2)
        {
            chain = new StereoToMonoSampleProvider(chain) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        if (chain.WaveFormat.SampleRate != 16000)
        {
            chain = new WdlResamplingSampleProvider(chain, 16000);
        }

        capture.StartRecording();

        var all = new List<float>();
        var chunk = new float[1600];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var read = chain.Read(chunk, 0, chunk.Length);
            if (read > 0)
            {
                all.AddRange(chunk.AsSpan(0, read).ToArray());
            }
            else
            {
                await Task.Delay(10);
            }

            if (sw.ElapsedMilliseconds % 1000 < 30)
            {
                var last = all.Count >= 16000 ? all.Skip(all.Count - 16000).Take(16000).ToArray() : [];
                double rms = 0;
                foreach (var s in last) rms += (double)s * s;
                rms = last.Length > 0 ? Math.Sqrt(rms / last.Length) : 0;
                Console.WriteLine($"[capture] t={sw.Elapsed.TotalSeconds:0.0}s bytes={Interlocked.Read(ref bytesTotal)} lastSecondRms={rms:0.00000}");
            }
        }

        capture.StopRecording();
        Console.WriteLine($"[capture] total bytes={Interlocked.Read(ref bytesTotal)}, samples={all.Count}");

        if (!string.IsNullOrEmpty(outWav))
        {
            File.WriteAllBytes(outWav, ToWav(all.ToArray(), 16000));
            Console.WriteLine($"[capture] saved {outWav}");
        }

        return 0;
    }

    /// <summary>Capture microphone / system / both, mixing to 16 kHz mono.</summary>
    private static async Task<int> AudioInputAsync(string mode, int seconds, string? outWav)
    {
        mode = mode.ToLowerInvariant();
        var sources = new List<(IWaveIn Capture, ISampleProvider Chain, string Name)>();
        var devices = new List<NAudio.CoreAudioApi.MMDevice>();

        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        if (mode is "system" or "both")
        {
            var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            devices.Add(device);
            var capture = new WasapiLoopbackCapture(device);
            sources.Add((capture, BuildChain(capture), $"系统音频: {device.FriendlyName}"));
        }

        try
        {
            if (mode is "mic" or "both")
            {
                var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia);
                devices.Add(device);
                var capture = new NAudio.CoreAudioApi.WasapiCapture(device);
                sources.Add((capture, BuildChain(capture), $"麦克风: {device.FriendlyName}"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[capture] microphone unavailable: {ex.Message}");
        }

        if (sources.Count == 0)
        {
            Console.WriteLine("[capture] no input source");
            return 2;
        }

        foreach (var (capture, chain, name) in sources)
        {
            Console.WriteLine($"[capture] {name} -> {chain.WaveFormat.SampleRate} Hz {chain.WaveFormat.Channels} ch");
            capture.StartRecording();
        }

        var all = new List<float>();
        var sync = new object();

        var mix = new float[1600];
        var temp = new float[1600];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Array.Clear(mix);
            var read = 0;
            foreach (var (_, chain, _) in sources)
            {
                var n = chain.Read(temp, 0, temp.Length);
                for (var i = 0; i < n; i++) mix[i] += temp[i];
                if (n > read) read = n;
            }

            if (read > 0)
            {
                for (var i = 0; i < read; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
                lock (sync) all.AddRange(mix.AsSpan(0, read).ToArray());
            }
            else
            {
                await Task.Delay(10);
                continue;
            }

            if (sw.ElapsedMilliseconds % 1000 < 30)
            {
                float[] recent;
                int total;
                lock (sync)
                {
                    total = all.Count;
                    recent = all.Count >= 16000 ? all.Skip(all.Count - 16000).ToArray() : [.. all];
                }

                double rms = 0;
                foreach (var s in recent) rms += (double)s * s;
                rms = recent.Length > 0 ? Math.Sqrt(rms / recent.Length) : 0;
                Console.WriteLine($"[capture] t={sw.Elapsed.TotalSeconds:0.0}s samples={total} recentRms={rms:0.00000}");
            }
        }

        foreach (var (capture, _, _) in sources) capture.StopRecording();
        foreach (var (capture, _, _) in sources) capture.Dispose();
        foreach (var device in devices) device.Dispose();

        float[] final;
        lock (sync) final = all.ToArray();
        Console.WriteLine($"[capture] total samples={final.Length}");

        if (!string.IsNullOrEmpty(outWav))
        {
            File.WriteAllBytes(outWav, ToWav(final, 16000));
            Console.WriteLine($"[capture] saved {outWav}");
        }

        return 0;
    }

    private static ISampleProvider BuildChain(IWaveIn capture)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
            ReadFully = false,
        };

        capture.DataAvailable += (_, e) =>
        {
            try
            {
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch
            {
                // shutdown race
            }
        };

        ISampleProvider chain = buffer.ToSampleProvider();
        if (chain.WaveFormat.Channels >= 2)
        {
            chain = new StereoToMonoSampleProvider(chain) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        if (chain.WaveFormat.SampleRate != 16000)
        {
            chain = new WdlResamplingSampleProvider(chain, 16000);
        }

        return chain;
    }

    private static void ConfigureBackend(string backend)
    {
        var cudaDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12");
        var cudaAvailable = File.Exists(Path.Combine(cudaDir, "ggml-cuda.dll")) && File.Exists(Path.Combine(cudaDir, "cudart64_12.dll"));

        var useCuda = (backend.Equals("auto", StringComparison.OrdinalIgnoreCase) || backend.Equals("cuda", StringComparison.OrdinalIgnoreCase)) && cudaAvailable;
        var useVulkan = !backend.Equals("cpu", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"[backend] requested={backend} cuda={useCuda} vulkan={useVulkan} cudaRuntimeAvailable={cudaAvailable}");
        NativeLibraryConfig.All
            .WithLogCallback((level, message) => Console.WriteLine($"[llama-native] {level}: {message.TrimEnd()}"))
            .WithCuda(false)
            .WithVulkan(useVulkan)
            .WithAutoFallback(true);
        if (useCuda)
        {
            NativeLibraryConfig.All.WithSelectingPolicy(new Cuda12FirstSelectingPolicy(cudaDir));
        }
    }

    /// <summary>sherpa-onnx ASR (Zipformer transducer or Cohere Transcribe) on a model directory.</summary>
    private static int AsrSherpa(string modelDirectory, string wavPath, string? language, string providerSetting = "auto")
    {
        var samples = LoadWav16kMono(wavPath);

        var provider = providerSetting.Equals("cpu", StringComparison.OrdinalIgnoreCase)
            ? "cpu"
            : InstallSherpaCuda() ? "cuda" : "cpu";
        Console.WriteLine($"[asr] provider={provider}");

        string Find(string kind) => Directory.EnumerateFiles(modelDirectory, kind + "*.onnx")
            .OrderByDescending(f => f.Contains(".int8.", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .First();

        var joiner = Directory.EnumerateFiles(modelDirectory, "joiner*.onnx").FirstOrDefault();
        var isCohere = joiner is null;

        SherpaOnnx.OfflineRecognizerConfig BuildConfig(string prov)
        {
            var c = new SherpaOnnx.OfflineRecognizerConfig();
            c.FeatConfig.SampleRate = 16000;
            c.FeatConfig.FeatureDim = 80;
            if (isCohere)
            {
                c.ModelConfig.CohereTranscribe.Encoder = Find("encoder");
                c.ModelConfig.CohereTranscribe.Decoder = Find("decoder");
                c.ModelConfig.CohereTranscribe.Language = string.IsNullOrWhiteSpace(language) || language == "auto" ? "ja" : language;
                c.ModelConfig.CohereTranscribe.UsePunct = 1;
                c.ModelConfig.CohereTranscribe.UseItn = 1;
            }
            else
            {
                c.ModelConfig.Transducer.Encoder = Find("encoder");
                c.ModelConfig.Transducer.Decoder = Find("decoder");
                c.ModelConfig.Transducer.Joiner = joiner!;
            }

            c.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
            c.ModelConfig.NumThreads = 4;
            c.ModelConfig.Provider = prov;
            c.DecodingMethod = "greedy_search";
            return c;
        }

        var loadWatch = Stopwatch.StartNew();
        SherpaOnnx.OfflineRecognizer recognizer;
        try
        {
            recognizer = new SherpaOnnx.OfflineRecognizer(BuildConfig(provider));
        }
        catch (Exception ex) when (provider != "cpu")
        {
            Console.WriteLine($"[asr] {provider} failed: {ex.Message} - falling back to cpu");
            provider = "cpu";
            recognizer = new SherpaOnnx.OfflineRecognizer(BuildConfig(provider));
        }

        using (recognizer)
        {
            loadWatch.Stop();

            string text = "";
            long decodeMs = 0;
            for (var i = 0; i < 2; i++)
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, samples);
                var sw = Stopwatch.StartNew();
                recognizer.Decode(stream);
                sw.Stop();
                decodeMs = sw.ElapsedMilliseconds;
                text = stream.Result.Text.Trim();
            }

            Console.WriteLine();
            Console.WriteLine($"[asr] {(isCohere ? "cohere" : "zipformer")} {provider} load {loadWatch.ElapsedMilliseconds} ms, decode {decodeMs} ms ({(samples.Length / 16000.0) / (decodeMs / 1000.0):0.0}x realtime)");
            WriteResult(text);
        }

        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint LoadLibraryExW(string path, nint fileHandle, uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string path);

    /// <summary>Loads the CUDA sherpa-onnx runtime from the app's data folder, if present.</summary>
    private static bool InstallSherpaCuda()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? dataRoot = null;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LiveCaptions.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                dataRoot = Path.Combine(dir.FullName, "data");
                break;
            }

            dir = dir.Parent;
        }

        if (dataRoot is null) return false;

        var dll = Path.Combine(dataRoot, "runtime", "sherpa-cuda", "bin", "sherpa-onnx-c-api.dll");
        if (!File.Exists(dll)) return false;

        SetDllDirectoryW(Path.GetDirectoryName(dll)!);

        var handle = LoadLibraryExW(dll, 0, 0x8); // LOAD_WITH_ALTERED_SEARCH_PATH
        if (handle == 0)
        {
            Console.WriteLine($"[asr] CUDA runtime failed to load (win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
        }

        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            typeof(SherpaOnnx.OfflineRecognizer).Assembly,
            (name, _, _) => name is "sherpa-onnx-c-api" ? handle : 0);
        Console.WriteLine($"[asr] CUDA runtime: {dll}");
        return true;
    }

    /// <summary>
    /// Runs a VAD model (Silero or TEN VAD, picked from the file name) over a wav
    /// and reports speech runs + gap statistics so we can tune the segmentation
    /// rules against real material.
    /// </summary>
    private static int Vad(string modelPath, string wavPath, float threshold, float minSilence)
    {
        var samples = LoadWav16kMono(wavPath);

        var config = new SherpaOnnx.VadModelConfig();
        var isTen = Path.GetFileName(modelPath).Contains("ten-vad", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(modelPath).Contains("ten_vad", StringComparison.OrdinalIgnoreCase);
        if (isTen)
        {
            config.TenVad = new SherpaOnnx.TenVadModelConfig
            {
                Model = modelPath,
                Threshold = threshold,
                MinSilenceDuration = minSilence,
                MinSpeechDuration = 0.1f,
                WindowSize = 256,
                MaxSpeechDuration = 30.0f,
            };
        }
        else
        {
            config.SileroVad = new SherpaOnnx.SileroVadModelConfig
            {
                Model = modelPath,
                Threshold = threshold,
                MinSilenceDuration = minSilence,
                MinSpeechDuration = 0.2f,
                WindowSize = 512,
                MaxSpeechDuration = 30.0f,
            };
        }

        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.Provider = "cpu";

        using var vad = new SherpaOnnx.VoiceActivityDetector(config, 120f);

        const int block = 1600; // 100 ms, same cadence as the app pipeline
        var segments = new List<(long Start, long End)>();
        for (var i = 0; i + block <= samples.Length; i += block)
        {
            var chunk = new float[block];
            Array.Copy(samples, i, chunk, 0, block);
            vad.AcceptWaveform(chunk);
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                segments.Add((seg.Start, seg.Start + seg.Samples.Length));
                vad.Pop();
            }
        }

        var sb = new StringBuilder();
        var totalSec = samples.Length / 16000.0;
        double speechSec = segments.Sum(s => (s.End - s.Start) / 16000.0);
        sb.AppendLine($"[vad] {(isTen ? "TEN VAD" : "Silero")} {Path.GetFileName(wavPath)}: {segments.Count} runs, speech {speechSec:0.0}s / {totalSec:0.0}s ({speechSec / totalSec * 100:0}%)");

        var gaps = new List<double>();
        for (var i = 1; i < segments.Count; i++)
        {
            gaps.Add((segments[i].Start - segments[i - 1].End) / 16.0);
        }

        if (gaps.Count > 0)
        {
            var sorted = gaps.OrderBy(g => g).ToArray();
            double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * (sorted.Length - 1)))];
            sb.AppendLine($"[vad] gaps(ms): med={P(0.5):0} p75={P(0.75):0} p90={P(0.9):0} max={sorted[^1]:0}");
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var dur = (segments[i].End - segments[i].Start) / 16000.0;
            var gap = i > 0 ? (segments[i].Start - segments[i - 1].End) / 16.0 : 0;
            sb.AppendLine($"[vad] run{i,3}: {segments[i].Start / 16000.0,7:0.00}s dur={dur,6:0.00}s gapBefore={gap,6:0}");
        }

        Console.Write(sb.ToString());
        WriteResult(sb.ToString());
        return 0;
    }

    private static float[] LoadWav16kMono(string path)    {
        using var reader = new AudioFileReader(path);
        ISampleProvider chain = reader;
        Console.WriteLine($"[wav] {Path.GetFileName(path)}: {chain.WaveFormat.SampleRate} Hz, {chain.WaveFormat.Channels} ch");

        if (chain.WaveFormat.Channels >= 2)
        {
            chain = new StereoToMonoSampleProvider(chain) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        if (chain.WaveFormat.SampleRate != 16000)
        {
            chain = new WdlResamplingSampleProvider(chain, 16000);
        }

        var samples = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = chain.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        Console.WriteLine($"[wav] {samples.Count / 16000.0:0.00} s at 16 kHz mono");
        return samples.ToArray();
    }

    private static byte[] ToWav(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream(samples.Length * 2 + 44);
        using var bw = new BinaryWriter(ms);
        var dataSize = samples.Length * 2;

        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataSize);

        foreach (var sample in samples)
        {
            bw.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static async Task<int> AsrWhisperAsync(string modelPath, string wavPath, string? language)
    {
        var samples = LoadWav16kMono(wavPath);

        var sw = Stopwatch.StartNew();
        WhisperFactory factory;
        try
        {
            factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
            {
                UseGpu = true,
                UseFlashAttention = true,
            });
        }
        catch (Exception ex)
        {
            // Mirror the app: CUDA may be unavailable, whisper.cpp still runs on CPU.
            Console.WriteLine($"[whisper] GPU init failed: {ex.Message} - retrying on CPU");
            factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
        }

        using var factoryScope = factory;
        Console.WriteLine($"[whisper] factory ready in {sw.ElapsedMilliseconds} ms, backend={RuntimeOptions.LoadedLibrary}");

        using var processor = factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
            .WithThreads(Math.Min(8, Environment.ProcessorCount))
            .WithTemperature(0f)
            .WithNoSpeechThreshold(0.6f)
            .WithNoContext()
            .Build();

        Console.WriteLine("[asr] transcribing...");
        sw.Restart();
        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples))
        {
            var text = segment.Text?.Trim();
            if (!string.IsNullOrEmpty(text)) sb.Append(text).Append(' ');
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"[asr] {sw.ElapsedMilliseconds} ms ({(samples.Length / 16000.0) / (sw.ElapsedMilliseconds / 1000.0):0.00}x realtime)");
        WriteResult(sb.ToString().Trim());
        return 0;
    }

    private static async Task<int> TranslateAsync(string modelPath, string text, string targetLanguage, string backend)
    {
        ConfigureBackend(backend);

        var modelParams = new ModelParams(modelPath)
        {
            GpuLayerCount = 999,
            ContextSize = 8192,
            BatchSize = 2048,
            UBatchSize = 512,
        };

        Console.WriteLine("[llama] loading LLM weights...");
        var sw = Stopwatch.StartNew();
        using var weights = await LLamaWeights.LoadFromFileAsync(modelParams);
        Console.WriteLine($"[llama] weights loaded in {sw.ElapsedMilliseconds} ms");
        using var context = weights.CreateContext(modelParams);

        context.NativeHandle.MemoryClear();
        var executor = new InteractiveExecutor(context);

        // Use the app's own prompt/cleanup so offline numbers represent the shipped
        // pipeline (source-language clause, real-time ASR caveat, repetition guard).
        var target = LanguageCatalog.Target.FirstOrDefault(l =>
                         string.Equals(l.Code, targetLanguage, StringComparison.OrdinalIgnoreCase)
                         || l.EnglishName.Contains(targetLanguage, StringComparison.OrdinalIgnoreCase)
                         || l.DisplayName.Contains(targetLanguage, StringComparison.OrdinalIgnoreCase))
                     ?? LanguageCatalog.FindTarget("zh");
        var source = LanguageCatalog.FindSource("ja");
        var prompt = TranslationText.ChatMlPrompt(text, target, source);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = Math.Clamp(text.Length * 3, 64, 320),
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, RepeatPenalty = 1.1f },
            AntiPrompts = ["<|im_end|>", "<|im_start|>", "<|endoftext|>"],
        };

        Console.WriteLine("[mt] translating...");
        sw.Restart();
        var sb = new StringBuilder();
        var firstTokenAt = -1L;
        await foreach (var token in executor.InferAsync(prompt, inferenceParams))
        {
            if (firstTokenAt < 0) firstTokenAt = sw.ElapsedMilliseconds;
            sb.Append(token);
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"[mt] first token {firstTokenAt} ms, total {sw.ElapsedMilliseconds} ms");
        WriteResult(TranslationText.Clean(sb.ToString()));
        return 0;
    }
}
