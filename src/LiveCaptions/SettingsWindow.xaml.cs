using System.Text.Json;
using LiveCaptions.Interop;
using LiveCaptions.Models;
using LiveCaptions.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LiveCaptions;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly ModelDownloadService _downloader = new();
    private CancellationTokenSource? _downloadCts;
    private bool _initializing = true;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "LiveCaptions 设置";
        AppWindow.Resize(new SizeInt32(780, 900));

        _working = Clone(App.Settings);
        LoadIntoUi();
        _initializing = false;
    }

    private static AppSettings Clone(AppSettings source)
        => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source)) ?? new AppSettings();

    private void LoadIntoUi()
    {
        AudioSourceCombo.ItemsSource = new[] { "系统音频（扬声器回环）", "麦克风", "两者混合" };
        AudioSourceCombo.SelectedIndex = _working.AudioSource.ToLowerInvariant() switch
        {
            "mic" => 1,
            "both" => 2,
            _ => 0,
        };

        AsrProviderCombo.ItemsSource = new[] { "自动（安装 CUDA 运行库时用 GPU）", "CPU", "CUDA（GPU）" };
        AsrProviderCombo.SelectedIndex = _working.AsrProvider.ToLowerInvariant() switch
        {
            "cpu" => 1,
            "cuda" => 2,
            _ => 0,
        };

        var asrEntries = ModelCatalog.All.Where(m => m.Kind == "asr").ToArray();
        AsrModelCombo.ItemsSource = asrEntries;
        AsrModelCombo.DisplayMemberPath = nameof(ModelEntry.DisplayName);
        AsrModelCombo.SelectedItem = asrEntries.FirstOrDefault(m => ModelMatches(m, _working.AsrEngine, _working.AsrModelPath))
            ?? asrEntries[0];
        ApplyAsrEntry((ModelEntry)AsrModelCombo.SelectedItem);
        UpdateAsrPaths();

        LlmModelCombo.ItemsSource = ModelCatalog.All.Where(m => m.Kind == "llm").ToArray();
        LlmModelCombo.DisplayMemberPath = nameof(ModelEntry.DisplayName);
        LlmModelCombo.SelectedItem = ModelCatalog.All.FirstOrDefault(m => m.Kind == "llm" && IsSameModel(_working.LlmModelPath, m))
            ?? ModelCatalog.All.First(m => m.Kind == "llm");
        UpdateLlmPath();

        SourceLangCombo.ItemsSource = LanguageCatalog.Source;
        SourceLangCombo.DisplayMemberPath = nameof(LanguageOption.DisplayName);
        SourceLangCombo.SelectedItem = LanguageCatalog.FindSource(_working.SourceLanguage);

        TargetLangCombo.ItemsSource = LanguageCatalog.Target;
        TargetLangCombo.DisplayMemberPath = nameof(LanguageOption.DisplayName);
        TargetLangCombo.SelectedItem = LanguageCatalog.FindTarget(_working.TargetLanguage);

        TranslateCheck.IsChecked = _working.TranslateEnabled;
        TranslatePartialsCheck.IsChecked = _working.TranslatePartials;

        LlmBackendCombo.ItemsSource = new[] { "本地 GGUF（内置 llama.cpp，无需额外组件）", "本地 llama.cpp 服务（托管进程，支持最新模型）", "HTTP 服务（外部，如 vLLM / LM Studio）" };
        LlmBackendCombo.SelectedIndex = _working.LlmBackend.ToLowerInvariant() switch
        {
            "llamacpp" => 1,
            "http" => 2,
            _ => 0,
        };
        LlmEndpointBox.Text = _working.LlmEndpoint;
        LlmEndpointModelBox.Text = _working.LlmEndpointModel;
        LlmDisableThinkingCheck.IsChecked = _working.LlmHttpDisableThinking;
        LlamaServerPortBox.Text = _working.LlamaServerPort.ToString();
        LlamaServerArgsBox.Text = _working.LlamaServerExtraArgs;
        LlamaServerRuntimeText.Text = LocalLlamaServer.RuntimeAvailable
            ? $"运行时: {LocalLlamaServer.ServerPath}"
            : "未找到 llama-server.exe，请运行 scripts/fetch-llama-server.ps1 下载运行时";
        UpdateBackendVisibility();
        ShowOriginalCheck.IsChecked = _working.ShowOriginal;
        AlwaysOnTopCheck.IsChecked = _working.AlwaysOnTop;
        UseVadCheck.IsChecked = _working.UseVad;
        AutoStartCheck.IsChecked = _working.AutoStartCapture;

        GpuBackendCombo.ItemsSource = new[] { "自动（优先 CUDA）", "Vulkan", "CPU（仅调试）" };
        GpuBackendCombo.SelectedIndex = _working.GpuBackend switch
        {
            "vulkan" => 1,
            "cpu" => 2,
            _ => 0,
        };

        FontSizeSlider.Value = _working.FontSize;
        OpacitySlider.Value = _working.PanelOpacity;
        MaxLinesSlider.Value = _working.MaxLines;
        VadSlider.Value = _working.VadThreshold;
        PartialSlider.Value = _working.PartialIntervalMs;
        SilenceSlider.Value = _working.FinalSilenceMs;
        MaxUttSlider.Value = _working.MaxUtteranceSeconds;

        UpdateLabels();
        StatusText.Text = $"模型目录: {ModelCatalog.ModelsDirectory}";
    }

    private static bool IsSameModel(string path, ModelEntry entry)
        => !string.IsNullOrWhiteSpace(path) &&
           entry.Files.Any(f => path.EndsWith(f.FileName, StringComparison.OrdinalIgnoreCase));

    private static bool ModelMatches(ModelEntry entry, string engine, string path)
    {
        if (string.Equals(entry.Id, "cohere-py", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(engine, "cohere-py", StringComparison.OrdinalIgnoreCase);
        }

        var pathMatches = entry.Archive is { } archive
            ? path.Contains(archive.ExtractedDirectory, StringComparison.OrdinalIgnoreCase)
            : entry.Files.Length > 0 && path.Contains(entry.Files[0].FileName, StringComparison.OrdinalIgnoreCase);

        if (!pathMatches) return false;

        var expectedEngine = entry.Id switch
        {
            var id when id.StartsWith("zipformer", StringComparison.OrdinalIgnoreCase) => "sherpa",
            var id when id.StartsWith("cohere", StringComparison.OrdinalIgnoreCase) => "sherpa",
            var id when id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) => "whisper",
            _ => "sherpa",
        };

        return string.Equals(engine, expectedEngine, StringComparison.OrdinalIgnoreCase) ||
               // legacy value from before the engine was renamed to "sherpa"
               (expectedEngine == "sherpa" && engine.Equals("zipformer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Language-specific models pin the source language when selected.</summary>
    private void ApplyDefaultLanguage(ModelEntry entry)
    {
        if (entry.DefaultLanguage is not { } code) return;
        if (SourceLangCombo.SelectedItem is not LanguageOption option || option.Code == code) return;

        SourceLangCombo.SelectedItem = LanguageCatalog.FindSource(code);
    }

    private void UpdateAsrPaths()
    {
        if (string.Equals(_working.AsrEngine, "cohere-py", StringComparison.OrdinalIgnoreCase))
        {
            AsrPathText.Text = "由本地 Python 环境提供（先运行 scripts/fetch-cohere-asr.ps1，权重在 data/hf-cache）";
            MmprojPathText.Text = "";
            return;
        }

        AsrPathText.Text = string.IsNullOrWhiteSpace(_working.AsrModelPath)
            ? "未选择模型文件"
            : _working.AsrModelPath;
        MmprojPathText.Text = string.IsNullOrWhiteSpace(_working.AsrMmprojPath)
            ? "未选择音频编码器 (mmproj)"
            : $"音频编码器: {_working.AsrMmprojPath}";
    }

    private void UpdateLlmPath()
    {
        LlmPathText.Text = string.IsNullOrWhiteSpace(_working.LlmModelPath)
            ? "未选择模型文件"
            : _working.LlmModelPath;
    }

    private void UpdateLabels()
    {
        FontSizeLabel.Text = $"字幕字号: {_working.FontSize:0}";
        OpacityLabel.Text = $"面板不透明度: {_working.PanelOpacity:0.00}";
        LinesLabel.Text = $"最大显示行数: {_working.MaxLines}";
        VadLabel.Text = $"静音阈值: {_working.VadThreshold:0.000}";
        PartialLabel.Text = $"部分结果间隔: {_working.PartialIntervalMs:0} ms";
        SilenceLabel.Text = $"断句静音时长: {_working.FinalSilenceMs:0} ms";
        MaxUttLabel.Text = $"单句最长时长: {_working.MaxUtteranceSeconds:0} s";
    }

    // --- Event handlers ----------------------------------------------------

    private void AsrModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || AsrModelCombo.SelectedItem is not ModelEntry entry) return;
        ApplyAsrEntry(entry);
        UpdateAsrPaths();
    }

    /// <summary>Map a catalog entry to the engine id, model path and default language.</summary>
    private void ApplyAsrEntry(ModelEntry entry)
    {
        if (string.Equals(entry.Id, "cohere-py", StringComparison.OrdinalIgnoreCase))
        {
            _working.AsrEngine = "cohere-py";
            _working.AsrModelPath = "";
            _working.AsrMmprojPath = "";
            ApplyDefaultLanguage(entry);
            return;
        }

        if (entry.Id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase))
        {
            _working.AsrEngine = "whisper";
            _working.AsrModelPath = entry.PrimaryPath;
            _working.AsrMmprojPath = "";
        }
        else
        {
            _working.AsrEngine = "sherpa";
            _working.AsrModelPath = entry.PrimaryPath;
            _working.AsrMmprojPath = "";
            ApplyDefaultLanguage(entry);
        }
    }

    private void LlmBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateBackendVisibility();
    }

    private void UpdateBackendVisibility()
    {
        var index = LlmBackendCombo.SelectedIndex;
        var managed = index == 1;
        var http = index == 2;

        LlamaServerPanel.Visibility = managed ? Visibility.Visible : Visibility.Collapsed;
        HttpBackendPanel.Visibility = http ? Visibility.Visible : Visibility.Collapsed;

        // A model file is required for "llama" and "llamacpp", not for external HTTP.
        LlmModelCombo.IsEnabled = !http;
        LlmDownloadButton.IsEnabled = !http;

        var port = 0;
        if (managed && int.TryParse(LlamaServerPortBox.Text, out var parsed) && parsed > 0)
        {
            port = parsed;
            try
            {
                var listener = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners().Any(e => e.Port == port);
                LlamaServerPortHint.Text = listener ? "端口已被占用（可能已有服务在运行）" : "端口可用";
            }
            catch
            {
                LlamaServerPortHint.Text = "";
            }
        }
        else
        {
            LlamaServerPortHint.Text = "";
        }
    }

    private void LlmModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LlmModelCombo.SelectedItem is not ModelEntry entry) return;
        _working.LlmModelPath = ModelCatalog.PathOf(entry.Files[0].FileName);
        UpdateLlmPath();
    }

    private void FontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.FontSize = e.NewValue;
        ApplyLiveAppearance();
        UpdateLabels();
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.PanelOpacity = e.NewValue;
        ApplyLiveAppearance();
        UpdateLabels();
    }

    private void ShowOriginalCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _working.ShowOriginal = ShowOriginalCheck.IsChecked == true;
        ApplyLiveAppearance();
    }

    /// <summary>Preview appearance changes on the overlay while the settings window is open.</summary>
    private void ApplyLiveAppearance()
    {
        App.Ui.ShowOriginal = _working.ShowOriginal;
        App.Ui.TranslationFontSize = _working.FontSize;
        App.Ui.OriginalFontSize = Math.Max(10, Math.Round(_working.FontSize * 0.64));
        App.Ui.PanelOpacity = _working.PanelOpacity;
    }

    private void MaxLinesSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.MaxLines = (int)e.NewValue;
        UpdateLabels();
    }

    private void VadSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.VadThreshold = e.NewValue;
        UpdateLabels();
    }

    private void PartialSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.PartialIntervalMs = (int)e.NewValue;
        UpdateLabels();
    }

    private void SilenceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.FinalSilenceMs = (int)e.NewValue;
        UpdateLabels();
    }

    private void MaxUttSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        _working.MaxUtteranceSeconds = (int)e.NewValue;
        UpdateLabels();
    }

    private async void BrowseAsrModel_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync(".gguf");
        if (file is null) return;
        _working.AsrModelPath = file;

        var mmproj = await PickFileAsync(".gguf");
        if (mmproj is not null)
        {
            _working.AsrMmprojPath = mmproj;
        }

        UpdateAsrPaths();
    }

    private async void BrowseLlmModel_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync(".gguf");
        if (file is null) return;
        _working.LlmModelPath = file;
        UpdateLlmPath();
    }

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Win32.GetHwnd(this));
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void AsrDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (AsrModelCombo.SelectedItem is not ModelEntry entry) return;
        await DownloadAsync(entry);
    }

    private async void LlmDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (LlmModelCombo.SelectedItem is not ModelEntry entry) return;
        await DownloadAsync(entry);
    }

    private async Task DownloadAsync(ModelEntry entry)
    {
        if (_downloadCts is not null) return;

        if (entry.Files.Length == 0 && entry.Archive is null)
        {
            // Environment-provided backend (PyTorch sidecar): nothing to download here.
            DownloadPanel.Visibility = Visibility.Visible;
            DownloadText.Text = "该项由 scripts/fetch-cohere-asr.ps1 准备 Python 环境与权重（data/hf-cache）";
            return;
        }

        _downloadCts = new CancellationTokenSource();
        DownloadPanel.Visibility = Visibility.Visible;
        AsrDownloadButton.IsEnabled = false;
        LlmDownloadButton.IsEnabled = false;

        var progress = new Progress<(string file, double ratio, long received, long total)>(p =>
        {
            DownloadBar.Value = p.ratio;
            DownloadText.Text = p.total > 0
                ? $"正在下载 {p.file} · {ModelDownloadService.FormatBytes(p.received)} / {ModelDownloadService.FormatBytes(p.total)} ({p.ratio:P0})"
                : $"正在下载 {p.file} · {ModelDownloadService.FormatBytes(p.received)}";
        });

        try
        {
            await _downloader.EnsureDownloadedAsync(entry, progress, _downloadCts.Token);
            DownloadText.Text = $"下载完成: {entry.DisplayName}";
            if (entry.Kind == "asr")
            {
                _working.AsrEngine = entry.Id switch
                {
                    var id when id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) => "whisper",
                    _ => "sherpa",
                };
                _working.AsrModelPath = entry.PrimaryPath;
                _working.AsrMmprojPath = entry.Files.Length > 1 ? ModelCatalog.PathOf(entry.Files[1].FileName) : "";
                UpdateAsrPaths();
            }
            else
            {
                _working.LlmModelPath = ModelCatalog.PathOf(entry.Files[0].FileName);
                UpdateLlmPath();
            }
        }
        catch (OperationCanceledException)
        {
            DownloadText.Text = "下载已取消";
        }
        catch (Exception ex)
        {
            DownloadText.Text = $"下载失败: {ex.Message}";
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            AsrDownloadButton.IsEnabled = true;
            LlmDownloadButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _working.TranslateEnabled = TranslateCheck.IsChecked == true;
        _working.TranslatePartials = TranslatePartialsCheck.IsChecked == true;
        _working.AudioSource = AudioSourceCombo.SelectedIndex switch
        {
            1 => "mic",
            2 => "both",
            _ => "system",
        };
        _working.AsrProvider = AsrProviderCombo.SelectedIndex switch
        {
            1 => "cpu",
            2 => "cuda",
            _ => "auto",
        };
        _working.UseVad = UseVadCheck.IsChecked == true;
        _working.LlmBackend = LlmBackendCombo.SelectedIndex switch
        {
            1 => "llamacpp",
            2 => "http",
            _ => "llama",
        };
        _working.LlmEndpoint = string.IsNullOrWhiteSpace(LlmEndpointBox.Text) ? "http://127.0.0.1:1234/v1" : LlmEndpointBox.Text.Trim();
        _working.LlmEndpointModel = LlmEndpointModelBox.Text.Trim();
        _working.LlmHttpDisableThinking = LlmDisableThinkingCheck.IsChecked == true;
        _working.LlamaServerExtraArgs = LlamaServerArgsBox.Text.Trim();
        if (int.TryParse(LlamaServerPortBox.Text, out var port) && port is > 0 and < 65536)
        {
            _working.LlamaServerPort = port;
        }
        else
        {
            LlamaServerPortBox.Text = _working.LlamaServerPort.ToString();
        }
        _working.ShowOriginal = ShowOriginalCheck.IsChecked == true;
        _working.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        _working.AutoStartCapture = AutoStartCheck.IsChecked == true;
        _working.PanelOpacity = OpacitySlider.Value;
        _working.GpuBackend = GpuBackendCombo.SelectedIndex switch
        {
            1 => "vulkan",
            2 => "cpu",
            _ => "auto",
        };

        if (SourceLangCombo.SelectedItem is LanguageOption src) _working.SourceLanguage = src.Code;
        if (TargetLangCombo.SelectedItem is LanguageOption tgt) _working.TargetLanguage = tgt.Code;

        _working.Validate();
        App.SettingsService.Update(_working);
        StatusText.Text = "设置已保存，将在主窗口重新启动监听后生效";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
