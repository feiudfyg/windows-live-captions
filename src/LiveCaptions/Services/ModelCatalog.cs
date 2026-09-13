namespace LiveCaptions.Services;

public sealed record ModelFile(string FileName, string Url, long ApproxBytes);

public sealed record ModelEntry(
    string Id,
    string Kind,            // "asr" | "mmproj" | "llm"
    string DisplayName,
    string Description,
    ModelFile[] Files)
{
    public bool Exists => Files.All(f => File.Exists(Path.Combine(ModelCatalog.ModelsDirectory, f.FileName)));

    public long TotalBytes => Files.Sum(f => f.ApproxBytes);
}

/// <summary>
/// Catalogue of the local models the app can download and run.
/// </summary>
public static class ModelCatalog
{
    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveCaptions", "models");

    private const string QwenAsrRepo = "https://huggingface.co/ggml-org";
    private const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp";
    private const string HauhauRepo = "https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive";
    private const string Qwen25Repo = "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF";

    public static readonly ModelEntry QwenAsr17B = new(
        "qwen3-asr-1.7b",
        "asr",
        "Qwen3-ASR-1.7B (推荐)",
        "业界最强开源 ASR 之一，支持 52 种语言/方言，GPU 加速",
        [
            new ModelFile("Qwen3-ASR-1.7B-Q8_0.gguf",
                $"{QwenAsrRepo}/Qwen3-ASR-1.7B-GGUF/resolve/main/Qwen3-ASR-1.7B-Q8_0.gguf", 2_173_000_000),
            new ModelFile("mmproj-Qwen3-ASR-1.7B-Q8_0.gguf",
                $"{QwenAsrRepo}/Qwen3-ASR-1.7B-GGUF/resolve/main/mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", 355_000_000),
        ]);

    public static readonly ModelEntry QwenAsr06B = new(
        "qwen3-asr-0.6b",
        "asr",
        "Qwen3-ASR-0.6B (轻量)",
        "0.6B 版本，速度更快、精度略低，适合低显存设备",
        [
            new ModelFile("Qwen3-ASR-0.6B-Q8_0.gguf",
                $"{QwenAsrRepo}/Qwen3-ASR-0.6B-GGUF/resolve/main/Qwen3-ASR-0.6B-Q8_0.gguf", 806_000_000),
            new ModelFile("mmproj-Qwen3-ASR-0.6B-Q8_0.gguf",
                $"{QwenAsrRepo}/Qwen3-ASR-0.6B-GGUF/resolve/main/mmproj-Qwen3-ASR-0.6B-Q8_0.gguf", 215_000_000),
        ]);

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
        QwenAsr17B, QwenAsr06B, WhisperTurbo, LlmQwen35Q8, LlmQwen35Q4, LlmQwen25Small,
    ];

    public static string PathOf(string fileName) => Path.Combine(ModelsDirectory, fileName);

    public static string DefaultAsrModel => PathOf(QwenAsr17B.Files[0].FileName);
    public static string DefaultAsrMmproj => PathOf(QwenAsr17B.Files[1].FileName);
    public static string DefaultWhisperModel => PathOf(WhisperTurbo.Files[0].FileName);
    public static string DefaultLlmModel => PathOf(LlmQwen35Q8.Files[0].FileName);
}
