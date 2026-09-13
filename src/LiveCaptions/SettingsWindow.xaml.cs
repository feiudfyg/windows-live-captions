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
        AsrModelCombo.ItemsSource = ModelCatalog.All.Where(m => m.Kind == "asr").ToArray();
        AsrModelCombo.DisplayMemberPath = nameof(ModelEntry.DisplayName);
        var asrEntry = ModelCatalog.All.FirstOrDefault(m => m.Kind == "asr" &&
            (string.Equals(_working.AsrEngine, "whisper", StringComparison.OrdinalIgnoreCase)
                ? m.Id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase)
                : !m.Id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) && _working.AsrModelPath.Contains(m.Files[0].FileName, StringComparison.OrdinalIgnoreCase)));
        AsrModelCombo.SelectedItem = asrEntry ?? ModelCatalog.All.First(m => m.Kind == "asr");
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
        ShowOriginalCheck.IsChecked = _working.ShowOriginal;
        AlwaysOnTopCheck.IsChecked = _working.AlwaysOnTop;
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

    private void UpdateAsrPaths()
    {
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

        if (entry.Id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase))
        {
            _working.AsrEngine = "whisper";
            _working.AsrModelPath = ModelCatalog.PathOf(entry.Files[0].FileName);
            _working.AsrMmprojPath = "";
        }
        else
        {
            _working.AsrEngine = "qwen3-asr";
            _working.AsrModelPath = ModelCatalog.PathOf(entry.Files[0].FileName);
            _working.AsrMmprojPath = entry.Files.Length > 1 ? ModelCatalog.PathOf(entry.Files[1].FileName) : "";
        }

        UpdateAsrPaths();
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
        UpdateLabels();
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
                _working.AsrEngine = entry.Id.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) ? "whisper" : "qwen3-asr";
                _working.AsrModelPath = ModelCatalog.PathOf(entry.Files[0].FileName);
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
