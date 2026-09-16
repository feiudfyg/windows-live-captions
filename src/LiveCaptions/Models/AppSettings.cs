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
        if (MaxLines < 1) MaxLines = 1;
        if (MaxLines > 10) MaxLines = 10;
        if (FontSize < 10) FontSize = 10;
        if (FontSize > 72) FontSize = 72;
        if (PanelOpacity < 0.05) PanelOpacity = 0.05;
        if (PanelOpacity > 1.0) PanelOpacity = 1.0;
        if (ContextSize < 2048) ContextSize = 2048;
        if (ContextSize > 131072) ContextSize = 131072;
        if (PartialIntervalMs < 400) PartialIntervalMs = 400;
        if (FinalSilenceMs < 200) FinalSilenceMs = 200;
        if (MaxUtteranceSeconds < 5) MaxUtteranceSeconds = 5;
        if (MaxUtteranceSeconds > 120) MaxUtteranceSeconds = 120;
        if (GpuLayerCount < 0) GpuLayerCount = 0;
        if (CohereAsrPort < 1024) CohereAsrPort = 1024;
        if (CohereAsrPort > 65500) CohereAsrPort = 65500;
        if (string.IsNullOrWhiteSpace(CohereAsrDtype) || CohereAsrDtype is not ("bfloat16" or "float16" or "float32"))
        {
            CohereAsrDtype = "bfloat16";
        }
        if (WindowWidth < 320) WindowWidth = 320;
        if (WindowHeight < 90) WindowHeight = 90;
    }
}
