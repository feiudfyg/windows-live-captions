namespace LiveCaptions.Services;

public sealed record ModelFile(string FileName, string Url, long ApproxBytes);

public sealed record ModelArchive(string ArchiveName, string Url, string ExtractedDirectory, long ApproxBytes);

public sealed record ModelEntry(
    string Id,
    string Kind,            // "asr" | "mmproj" | "llm"
    string DisplayName,
    string Description,
    ModelFile[] Files,
    ModelArchive? Archive = null,
    string? DefaultLanguage = null)
{
    public bool Exists => Archive is not null
        ? Directory.Exists(Path.Combine(ModelCatalog.ModelsDirectory, Archive.ExtractedDirectory))
        : Files.All(f => File.Exists(Path.Combine(ModelCatalog.ModelsDirectory, f.FileName)));

    /// <summary>Path used for loading: a file for plain models, a directory for archived ones.</summary>
    public string PrimaryPath => Archive is not null
        ? Path.Combine(ModelCatalog.ModelsDirectory, Archive.ExtractedDirectory)
        : ModelCatalog.PathOf(Files[0].FileName);

    public long TotalBytes => Archive is not null ? Archive.ApproxBytes : Files.Sum(f => f.ApproxBytes);
}

/// <summary>
/// Catalogue of the local models the app can download and run.
/// </summary>
public static class ModelCatalog
{
    public static string ModelsDirectory { get; } = AppPaths.ModelsDirectory;

    private const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp";
    private const string HauhauRepo = "https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive";
    private const string Qwen25Repo = "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF";
    private const string SherpaRepo = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models";

    public static readonly ModelEntry ZipformerJa = new(
        "zipformer-ja",
        "asr",
        "Zipformer 日语 (ReazonSpeech)",
        "RNN-T 架构：抗噪、不会复读退化，CPU 实时（约 110× 实时），适合日语视频",
        [],
        new ModelArchive("sherpa-onnx-zipformer-ja-reazonspeech-2024-08-01.tar.bz2",
            $"{SherpaRepo}/sherpa-onnx-zipformer-ja-reazonspeech-2024-08-01.tar.bz2",
            "sherpa-onnx-zipformer-ja-reazonspeech-2024-08-01", 400_000_000),
        "ja");

    public static readonly ModelEntry CohereTranscribe = new(
        "cohere-transcribe",
        "asr",
        "Cohere Transcribe 14 语言（SOTA）",
        "2026 年 SOTA 多语言识别（含日语），需指定音频语言（本项默认日语），int8 约 2.7 GB",
        [],
        new ModelArchive("sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01.tar.bz2",
            $"{SherpaRepo}/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01.tar.bz2",
            "sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01", 2_900_000_000),
        "ja");

    public static readonly ModelEntry ZipformerZhEn = new(
        "zipformer-zh-en",
        "asr",
        "Zipformer 中英双语",
        "中英文混合识别，抗噪、不会复读退化，CPU 实时",
        [],
        new ModelArchive("sherpa-onnx-zipformer-zh-en-2023-11-22.tar.bz2",
            $"{SherpaRepo}/sherpa-onnx-zipformer-zh-en-2023-11-22.tar.bz2",
            "sherpa-onnx-zipformer-zh-en-2023-11-22", 330_000_000),
        "zh");

    public static readonly ModelEntry ZipformerCantonese = new(
        "zipformer-yue",
        "asr",
        "Zipformer 粤语",
        "粤语识别，抗噪、不会复读退化，CPU 实时",
        [],
        new ModelArchive("sherpa-onnx-zipformer-cantonese-2024-03-13.tar.bz2",
            $"{SherpaRepo}/sherpa-onnx-zipformer-cantonese-2024-03-13.tar.bz2",
            "sherpa-onnx-zipformer-cantonese-2024-03-13", 330_000_000),
        "yue");

    public static readonly ModelEntry ZipformerKorean = new(
        "zipformer-ko",
        "asr",
        "Zipformer 韩语",
        "韩语识别，抗噪、不会复读退化，CPU 实时",
        [],
        new ModelArchive("sherpa-onnx-zipformer-korean-2024-06-24.tar.bz2",
            $"{SherpaRepo}/sherpa-onnx-zipformer-korean-2024-06-24.tar.bz2",
            "sherpa-onnx-zipformer-korean-2024-06-24", 330_000_000),
        "ko");

    public static readonly ModelEntry WhisperTurbo = new(
        "whisper-large-v3-turbo",
        "asr",
        "Whisper large-v3-turbo (备选引擎)",
        "whisper.cpp CUDA 加速，兼容性最好",
        [
            new ModelFile("ggml-large-v3-turbo.bin",
                $"{WhisperRepo}/resolve/main/ggml-large-v3-turbo.bin", 1_624_555_275),
        ]);

    public static readonly ModelEntry LlmQwen35Q8 = new(
        "qwen3.5-9b-q8",
        "llm",
        "Qwen3.5-9B Uncensored Q8_0 (推荐)",
        "9B 高精度量化，翻译质量最好，约 9GB 显存",
        [
            new ModelFile("Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q8_0.gguf",
                $"{HauhauRepo}/resolve/main/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q8_0.gguf", 9_524_000_000),
        ]);

    public static readonly ModelEntry LlmQwen35Q4 = new(
        "qwen3.5-9b-q4",
        "llm",
        "Qwen3.5-9B Uncensored Q4_K_M",
        "9B 中等量化，显存占用约 5.5GB，速度更快",
        [
            new ModelFile("Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf",
                $"{HauhauRepo}/resolve/main/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf", 5_626_000_000),
        ]);

    public static readonly ModelEntry LlmQwen25Small = new(
        "qwen2.5-1.5b",
        "llm",
        "Qwen2.5-1.5B Instruct (轻量)",
        "小模型，延迟最低，适合快速测试",
        [
            new ModelFile("qwen2.5-1.5b-instruct-q4_k_m.gguf",
                $"{Qwen25Repo}/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf", 1_117_321_312),
        ]);

    public static readonly ModelEntry[] All =
    [
        ZipformerJa, CohereTranscribe, ZipformerZhEn, ZipformerCantonese, ZipformerKorean,
        ZipformerZhEn, ZipformerCantonese, ZipformerKorean,
        LlmQwen35Q8, LlmQwen35Q4, LlmQwen25Small,
    ];

    public static string PathOf(string fileName) => Path.Combine(ModelsDirectory, fileName);

    public static string DefaultWhisperModel => PathOf(WhisperTurbo.Files[0].FileName);

    /// <summary>Default offline model (Japanese Zipformer - the recommended engine).</summary>
    public static string DefaultSherpaModel => ZipformerJa.PrimaryPath;
    public static string DefaultLlmModel => PathOf(LlmQwen35Q8.Files[0].FileName);
}
