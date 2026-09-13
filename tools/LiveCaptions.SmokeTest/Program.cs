using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
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
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine("usage: asr-qwen <model.gguf> <mmproj.gguf> <audio.wav> [language]");
            Console.WriteLine("       asr-whisper <model.bin> <audio.wav> [language]");
            Console.WriteLine("       mt <model.gguf> <text> <target-language-name>");
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "capture" when args.Length >= 2 => await CaptureAsync(int.Parse(args[1]), args.Length > 2 ? args[2] : null),
                "asr-qwen" when args.Length >= 4 => await AsrQwenAsync(args[1], args[2], args[3], args.Length > 4 ? args[4] : null, args.Length > 5 ? args[5] : "auto"),
                "asr-whisper" when args.Length >= 3 => await AsrWhisperAsync(args[1], args[2], args.Length > 3 ? args[3] : null),
                "asr-zipformer" when args.Length >= 3 => AsrZipformer(args[1], args[2]),
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

    /// <summary>Zipformer (sherpa-onnx) ASR on a model directory.</summary>
    private static int AsrZipformer(string modelDirectory, string wavPath)
    {
        var samples = LoadWav16kMono(wavPath);

        string Find(string kind) => Directory.EnumerateFiles(modelDirectory, kind + "*.onnx")
            .OrderByDescending(f => f.Contains(".int8.", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .First();

        var config = new SherpaOnnx.OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Find("encoder");
        config.ModelConfig.Transducer.Decoder = Find("decoder");
        config.ModelConfig.Transducer.Joiner = Find("joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.ModelConfig.NumThreads = 4;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        var sw = Stopwatch.StartNew();
        using var recognizer = new SherpaOnnx.OfflineRecognizer(config);
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        recognizer.Decode(stream);
        sw.Stop();

        var text = stream.Result.Text.Trim();
        Console.WriteLine();
        Console.WriteLine($"[asr] {sw.ElapsedMilliseconds} ms ({(samples.Length / 16000.0) / (sw.ElapsedMilliseconds / 1000.0):0.00}x realtime)");
        Console.WriteLine($"[text] {text}");
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

    private static async Task<int> AsrQwenAsync(string modelPath, string mmprojPath, string wavPath, string? language, string backend)
    {
        var samples = LoadWav16kMono(wavPath);

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
        var mtmdParams = MtmdContextParams.Default();
        mtmdParams.UseGpu = true;
        mtmdParams.NThreads = Math.Min(16, Environment.ProcessorCount);
        var marker = NativeApi.MtmdDefaultMarker() ?? "<media>";
        mtmdParams.MediaMarker = marker;
        Console.WriteLine($"[mtmd] default marker: '{marker}'");

        Console.WriteLine("[mtmd] loading audio projector...");
        using var mtmd = await MtmdWeights.LoadFromFileAsync(mmprojPath, weights, mtmdParams);
        Console.WriteLine($"[mtmd] supports audio={mtmd.SupportsAudio}, sample rate={mtmd.SampleRate}");

        var prompt = $"<|im_start|><|im_end|>\n<|im_start|>user\n{marker}" +
                     (string.IsNullOrWhiteSpace(language) || language == "auto"
                         ? "Transcribe the audio."
                         : $"Transcribe the audio in {language}.") +
                     "<|im_end|>\n<|im_start|>assistant\n";

        context.NativeHandle.MemoryClear();
        mtmd.ClearMedia();
        using var embed = mtmd.LoadMedia(ToWav(samples, 16000));
        var executor = new InteractiveExecutor(context, mtmd);
        executor.Embeds.Clear();
        executor.Embeds.Add(embed);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 1024,
            SamplingPipeline = new GreedySamplingPipeline(),
            AntiPrompts = ["<|im_end|>", "<|im_start|>"],
        };

        Console.WriteLine("[asr] transcribing...");
        sw.Restart();
        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams))
        {
            sb.Append(token);
        }

        sw.Stop();
        var text = sb.ToString().Trim();
        var markerIndex = text.IndexOf("<asr_text>", StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            text = text[(markerIndex + "<asr_text>".Length)..].Trim();
        }

        Console.WriteLine();
        Console.WriteLine($"[asr] {sw.ElapsedMilliseconds} ms ({(samples.Length / 16000.0) / (sw.ElapsedMilliseconds / 1000.0):0.00}x realtime)");
        Console.WriteLine($"[text] {text}");
        return 0;
    }

    private static async Task<int> AsrWhisperAsync(string modelPath, string wavPath, string? language)
    {
        var samples = LoadWav16kMono(wavPath);

        var sw = Stopwatch.StartNew();
        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
        {
            UseGpu = true,
            UseFlashAttention = true,
        });
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
        Console.WriteLine($"[text] {sb.ToString().Trim()}");
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

        var prompt =
            "<|im_start|>system\n" +
            "You are a professional real-time subtitle translator. " +
            $"Translate the user's text into {targetLanguage}. " +
            "Output only the translation itself: no explanations, no notes, no quotes, no original text." +
            "<|im_end|>\n" +
            "<|im_start|>user\n" + text + "<|im_end|>\n" +
            "<|im_start|>assistant\n<think>\n\n</think>\n\n";

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 512,
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f },
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
        Console.WriteLine($"[text] {sb.ToString().Trim()}");
        return 0;
    }
}
