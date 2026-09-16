using System.Text.Json.Serialization;
using LiveCaptions.Services;

namespace LiveCaptions.Models;

/// <summary>
/// Persisted application configuration (settings.json in %LOCALAPPDATA%\LiveCaptions).
/// </summary>
public sealed class AppSettings
{
    // Engines
    public string AsrEngine { get; set; } = "sherpa"; // "sherpa" | "whisper"
    public string AsrModelPath { get; set; } = "";
    public string AsrMmprojPath { get; set; } = "";
    public string AsrProvider { get; set; } = "auto"; // "auto" (CUDA when the runtime is installed) | "cuda" | "cpu"

    /// <summary>PyTorch sidecar (full precision Cohere Transcribe) settings.</summary>
    public int CohereAsrPort { get; set; } = 12360;
    public string CohereAsrPythonPath { get; set; } = "";
    public string CohereAsrScriptPath { get; set; } = "";
    public string CohereAsrDtype { get; set; } = "bfloat16";

    /// <summary>VAD choice: "auto" (FireRedVAD when the sidecar runs, else TEN), "firered", "ten", "silero".</summary>
    public string VadEngine { get; set; } = "auto";
    public string LlmModelPath { get; set; } = "";
    public string GpuBackend { get; set; } = "auto"; // "auto" (CUDA preferred) | "vulkan" | "cpu"
    public bool WhisperUseCuda { get; set; } = true;

    // Audio input
    public string AudioSource { get; set; } = "system"; // "system" (loopback) | "mic" | "both"

    // Language
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh";
    public bool TranslateEnabled { get; set; } = true;

    // Segmentation / latency
    public double VadThreshold { get; set; } = 0.008;
    public int PartialIntervalMs { get; set; } = 2000;
    public int FinalSilenceMs { get; set; } = 750;
    public int MaxUtteranceSeconds { get; set; } = 10;
    public int MinPartialSeconds { get; set; } = 1;
    public int PartialCommitSeconds { get; set; } = 5;
    public bool UseVad { get; set; } = true; // Silero VAD adaptive segmentation when the model is present
    public bool UseAsrContext { get; set; } = false;

    // LLM
    public string LlmBackend { get; set; } = "llama"; // "llama" (in-process GGUF) | "llamacpp" (managed llama-server) | "http" (external server)
    public string LlmEndpoint { get; set; } = "http://127.0.0.1:1234/v1";
    public string LlmEndpointModel { get; set; } = "";                 // empty = whatever the server has loaded
    public bool LlmHttpDisableThinking { get; set; } = true;           // pass chat_template_kwargs.enable_thinking=false
    public int LlamaServerPort { get; set; } = 12359;
    public string LlamaServerExtraArgs { get; set; } = "";
    public int GpuLayerCount { get; set; } = 999;
    public int ContextSize { get; set; } = 8192;
    public int MaxTranslateTokens { get; set; } = 320;
    public double TranslateTemperature { get; set; } = 0.2;
    public bool TranslatePartials { get; set; } = false;
    public int PartialTranslateDelayMs { get; set; } = 1200;

    // Appearance
    public int MaxLines { get; set; } = 3;
    public double FontSize { get; set; } = 22;
    public double PanelOpacity { get; set; } = 0.72;

    /// <summary>Panel background effect: "acrylic" (Windows acrylic), "blur" (Gaussian blur), "simple" (plain translucency).</summary>
    public string BackdropMode { get; set; } = "acrylic";

    public bool ShowOriginal { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 220;

    // Behaviour
    public bool AutoStartCapture { get; set; } = true;

    public void Validate()
    {
        AudioSource = AudioCaptureService.ParseMode(AudioSource) switch
        {
            AudioInputMode.Microphone => "mic",
            AudioInputMode.Both => "both",
            _ => "system",
        };
        // Ranges match the settings sliders, so a value shown in the dialog is the
        // value that gets persisted.
        if (MaxLines < 1) MaxLines = 1;
        if (MaxLines > 6) MaxLines = 6;
        if (FontSize < 12) FontSize = 12;
        if (FontSize > 44) FontSize = 44;
        if (PanelOpacity < 0.05) PanelOpacity = 0.05;
        if (PanelOpacity > 1.0) PanelOpacity = 1.0;
        if (ContextSize < 2048) ContextSize = 2048;
        if (ContextSize > 131072) ContextSize = 131072;
        if (PartialIntervalMs < 600) PartialIntervalMs = 600;
        if (PartialIntervalMs > 4000) PartialIntervalMs = 4000;
        if (FinalSilenceMs < 300) FinalSilenceMs = 300;
        if (FinalSilenceMs > 2000) FinalSilenceMs = 2000;
        if (MaxUtteranceSeconds < 6) MaxUtteranceSeconds = 6;
        if (MaxUtteranceSeconds > 60) MaxUtteranceSeconds = 60;
        if (GpuLayerCount < 0) GpuLayerCount = 0;
        if (GpuLayerCount > 1000) GpuLayerCount = 1000;
        if (CohereAsrPort < 1024) CohereAsrPort = 1024;
        if (CohereAsrPort > 65500) CohereAsrPort = 65500;
        if (LlamaServerPort < 1024) LlamaServerPort = 1024;
        if (LlamaServerPort > 65500) LlamaServerPort = 65500;
        // HttpTranslationService clamps max tokens to >= 64; a smaller value would
        // throw inside Math.Clamp for every single translation.
        if (MaxTranslateTokens < 64) MaxTranslateTokens = 64;
        if (MaxTranslateTokens > 4096) MaxTranslateTokens = 4096;
        if (TranslateTemperature < 0) TranslateTemperature = 0;
        if (TranslateTemperature > 2) TranslateTemperature = 2;
        if (PartialTranslateDelayMs < 0) PartialTranslateDelayMs = 0;
        if (PartialTranslateDelayMs > 10000) PartialTranslateDelayMs = 10000;
        if (MinPartialSeconds < 0) MinPartialSeconds = 0;
        if (MinPartialSeconds > 60) MinPartialSeconds = 60;
        if (PartialCommitSeconds < 1) PartialCommitSeconds = 1;
        if (PartialCommitSeconds > 600) PartialCommitSeconds = 600;
        if (VadThreshold < 0.0005) VadThreshold = 0.0005;
        if (VadThreshold > 0.1) VadThreshold = 0.1;

        if (string.IsNullOrWhiteSpace(VadEngine) || VadEngine is not ("auto" or "firered" or "ten" or "silero"))
        {
            VadEngine = "auto";
        }

        if (string.IsNullOrWhiteSpace(AsrProvider) || AsrProvider is not ("auto" or "cuda" or "cpu"))
        {
            AsrProvider = "auto";
        }

        if (string.IsNullOrWhiteSpace(LlmBackend) || LlmBackend is not ("llama" or "llamacpp" or "http"))
        {
            LlmBackend = "llama";
        }

        if (string.IsNullOrWhiteSpace(GpuBackend) || GpuBackend is not ("auto" or "vulkan" or "cpu"))
        {
            GpuBackend = "auto";
        }

        if (string.IsNullOrWhiteSpace(LlmEndpoint) ||
            !Uri.TryCreate(LlmEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            LlmEndpoint = "http://127.0.0.1:1234/v1";
        }
        if (string.IsNullOrWhiteSpace(AsrEngine)) AsrEngine = "sherpa";
        if (string.IsNullOrWhiteSpace(CohereAsrDtype) || CohereAsrDtype is not ("bfloat16" or "float16" or "float32"))
        {
            CohereAsrDtype = "bfloat16";
        }
        if (WindowWidth < 320) WindowWidth = 320;
        if (WindowHeight < 90) WindowHeight = 90;
        if (string.IsNullOrWhiteSpace(BackdropMode) || BackdropMode is not ("acrylic" or "blur" or "simple"))
        {
            BackdropMode = "acrylic";
        }
    }
}
