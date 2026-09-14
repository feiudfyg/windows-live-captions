using System.Collections.ObjectModel;
using LiveCaptions.Interop;
using LiveCaptions.Models;
using LiveCaptions.Services;
using LiveCaptions.Services.Asr;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace LiveCaptions;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<CaptionItem> _items = [];

    private AudioCaptureService? _capture;
    private CaptionPipeline? _pipeline;
    private IAsrEngine? _asr;
    private ITranslator? _translator;
    private LocalLlamaServer? _llamaServer;
    private SettingsWindow? _settingsWindow;

    private bool _busy;
    private bool _dragging;
    private bool _resizing;
    private Point _dragStart;
    private PointInt32 _windowStart;
    private SizeInt32 _sizeStart;
    private DispatcherQueueTimer? _toolbarTimer;

    public MainWindow()
    {
        Log.Write("[boot] MainWindow ctor");
        InitializeComponent();
        _dispatcher = DispatcherQueue;

        LinesList.ItemsSource = _items;

        SetupWindow();
        SubscribeUiEvents();
        Closed += OnClosed;
        Log.Write("[boot] MainWindow ready");

        _ = TryAutoStartAsync();
    }

    private void SetupWindow()
    {
        Title = "LiveCaptions";
        var appWindow = AppWindow;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = App.Settings.AlwaysOnTop;
        appWindow.SetPresenter(presenter);

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // Backdrop is cosmetic - ignore failures (e.g. remote sessions).
        }

        var hwnd = Win32.GetHwnd(this);
        Win32.HideFromAltTab(hwnd);
        if (App.Settings.ClickThrough)
        {
            Win32.SetClickThrough(hwnd, true);
        }

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)App.Settings.WindowWidth;
        var h = (int)App.Settings.WindowHeight;
        var x = App.Settings.WindowX is { } sx ? (int)sx : area.X + (area.Width - w) / 2;
        var y = App.Settings.WindowY is { } sy ? (int)sy : area.Y + area.Height - h - 96;
        appWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void SubscribeUiEvents()
    {
        App.Ui.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UiState.ShowOriginal))
            {
                RefreshOriginalVisibility();
            }
        };
    }

    private void RefreshOriginalVisibility()
    {
        // Recreate the item containers so the computed visibility re-evaluates.
        LinesList.ItemsSource = null;
        LinesList.ItemsSource = _items;
    }

    private async Task TryAutoStartAsync()
    {
        if (!App.Settings.AutoStartCapture) return;
        await Task.Delay(300);
        await StartAsync();
    }
    private void StartCapture()
    {
        _capture = new AudioCaptureService();
        _capture.SamplesAvailable += (buffer, count) => _pipeline?.PushAudio(buffer, count);
        _capture.Failed += msg => SetStatus(msg, error: true);
        _capture.DeviceChanged += msg => Log.Write($"[audio] device: {msg}");
        _capture.Start();
        Log.Write("[audio] capture started");
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true;
        Log.Write($"[pipeline] StartAsync entered, triggered by: {Environment.StackTrace.Split('\n').Skip(3).FirstOrDefault()?.Trim()}");
        try
        {
            StopAll();
            _dispatcher.TryEnqueue(() => SetStatus("正在加载语音识别模型…"));

            var asr = CreateAsrEngine();
            await Task.Run(() => asr.LoadAsync()).ConfigureAwait(true);
            _asr = asr;
            SetStatus($"语音识别就绪 · {asr.Name} ({asr.Backend})");

            ITranslator? translator = null;
            if (App.Settings.TranslateEnabled)
            {
                if (App.Settings.LlmBackend.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                {
                    var llmPath = ResolveLlmPath();
                    SetStatus("正在启动 llama.cpp 服务…");
                    var server = new LocalLlamaServer(App.Settings);
                    await Task.Run(() => server.StartAsync(llmPath)).ConfigureAwait(true);
                    _llamaServer = server;

                    var httpTranslator = new HttpTranslationService(App.Settings, server.BaseAddress);
                    await Task.Run(() => httpTranslator.LoadAsync()).ConfigureAwait(true);
                    translator = httpTranslator;

                    // Warm up so the first subtitle is not delayed by CUDA graph setup.
                    SetStatus("正在预热翻译模型…");
                    try
                    {
                        var warmupTarget = LanguageCatalog.FindTarget(App.Settings.TargetLanguage);
                        await Task.Run(() => httpTranslator.TranslateAsync("Hello.", warmupTarget, null)).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[mt] warmup failed: {ex.Message}");
                    }

                    SetStatus($"翻译就绪 · {Path.GetFileNameWithoutExtension(llmPath)} (llama.cpp :{server.ActualPort})");
                }
                else if (App.Settings.LlmBackend.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("正在连接翻译服务…");
                    var httpTranslator = new HttpTranslationService(App.Settings);
                    await Task.Run(() => httpTranslator.LoadAsync()).ConfigureAwait(true);
                    translator = httpTranslator;
                    SetStatus($"翻译就绪 · {httpTranslator.ModelName} ({httpTranslator.Backend})");
                }
                else
                {
                    var llmPath = ResolveLlmPath();
                    if (File.Exists(llmPath))
                    {
                        SetStatus("正在加载翻译模型…");
                        translator = new TranslationService(llmPath, App.Settings);
                        await Task.Run(() => translator.LoadAsync()).ConfigureAwait(true);
                        SetStatus($"翻译就绪 · {translator.ModelName} ({translator.Backend})");
                    }
                    else
                    {
                        SetStatus("未找到翻译模型，请在设置中下载（本次仅显示原文）");
                    }
                }
            }

            _translator = translator;

            var pipeline = new CaptionPipeline(App.Settings, _dispatcher);
            pipeline.SetEngines(asr, translator);
            pipeline.ItemUpdated += OnItemUpdated;
            pipeline.ErrorOccurred += msg => SetStatus(msg, error: true);
            pipeline.Start();
            _pipeline = pipeline;

            StartCapture();
            var target = LanguageCatalog.FindTarget(App.Settings.TargetLanguage);
            SetStatus($"正在监听系统音频 · {asr.Name} · 翻译 → {target.DisplayName}");
            _dispatcher.TryEnqueue(() =>
            {
                App.Ui.IsListening = true;
                ListenIcon.Glyph = "\uE769"; // pause
                EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败: {ex.Message}", error: true);
            StopAll();
        }
        finally
        {
            _busy = false;
        }
    }

    private void StopAll()
    {
        if (_pipeline is not null)
        {
            _pipeline.ItemUpdated -= OnItemUpdated;
            var pipeline = _pipeline;
            _pipeline = null;
            _ = pipeline.DisposeAsync();
        }

        _capture?.Dispose();
        _capture = null;

        _translator?.Dispose();
        _translator = null;

        _llamaServer?.Dispose();
        _llamaServer = null;

        _asr?.Dispose();
        _asr = null;

        _dispatcher.TryEnqueue(() =>
        {
            App.Ui.IsListening = false;
            ListenIcon.Glyph = "\uE768"; // play
        });
    }

    private IAsrEngine CreateAsrEngine()
    {
        var s = App.Settings;
        if (string.Equals(s.AsrEngine, "whisper", StringComparison.OrdinalIgnoreCase))
        {
            var path = string.IsNullOrWhiteSpace(s.AsrModelPath) ? ModelCatalog.DefaultWhisperModel : s.AsrModelPath;
            if (!File.Exists(path)) throw new FileNotFoundException($"未找到 Whisper 模型: {path}");
            return new WhisperAsrEngine(path, s.WhisperUseCuda);
        }

        // sherpa-onnx models (Zipformer / Cohere Transcribe). Legacy settings values
        // ("zipformer", "qwen3-asr") fall through to this branch as well.
        var modelDirectory = string.IsNullOrWhiteSpace(s.AsrModelPath) || !Directory.Exists(s.AsrModelPath)
            ? ModelCatalog.DefaultSherpaModel
            : s.AsrModelPath;
        if (!Directory.Exists(modelDirectory))
        {
            throw new FileNotFoundException($"未找到 sherpa-onnx 模型目录: {s.AsrModelPath}（请在设置中下载）");
        }

        var language = string.Equals(s.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : s.SourceLanguage;
        return new SherpaAsrEngine(modelDirectory, language);
    }

    private static string ResolveLlmPath()
        => string.IsNullOrWhiteSpace(App.Settings.LlmModelPath) ? ModelCatalog.DefaultLlmModel : App.Settings.LlmModelPath;

    private void OnItemUpdated(CaptionItem item)
    {
        Log.Write($"[caption{(item.IsPartial ? "/partial" : "")}] {item.Original} || {item.Translation}");
        _dispatcher.TryEnqueue(() =>
        {
            if (!_items.Contains(item))
            {
                _items.Add(item);
                while (_items.Count > Math.Max(1, App.Settings.MaxLines))
                {
                    _items.RemoveAt(0);
                }

                EmptyHint.Visibility = Visibility.Collapsed;
            }

            LinesScroll.ChangeView(null, double.MaxValue, null, true);
        });
    }

    private void SetStatus(string text, bool error = false)
    {
        Log.Write(error ? $"[error] {text}" : $"[status] {text}");
        _dispatcher.TryEnqueue(() =>
        {
            App.Ui.StatusText = text;
            StatusText.Foreground = error
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0x8A, 0x80))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        });
    }

    // --- Interaction -------------------------------------------------------

    private static bool IsInsideButton(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase) return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Panel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing) return;
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (IsInsideButton(e.OriginalSource)) return;

        _dragging = true;
        _dragStart = point.Position;
        _windowStart = AppWindow.Position;
        Panel.CapturePointer(e.Pointer);
    }

    private void Panel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetCurrentPoint(null).Position;
        var scale = RasterizationScale;
        var x = _windowStart.X + (int)((pos.X - _dragStart.X) * scale);
        var y = _windowStart.Y + (int)((pos.Y - _dragStart.Y) * scale);
        AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// Pointer coordinates are in logical pixels while AppWindow works in
    /// physical pixels; without this conversion dragging drifts on scaled displays.
    /// </summary>
    private double RasterizationScale => RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

    private void Panel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Panel.ReleasePointerCapture(e.Pointer);
        PersistWindowBounds();
    }

    private void Panel_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed) return;

        _resizing = true;
        _dragStart = point.Position;
        _sizeStart = AppWindow.Size;
        ResizeGrip.CapturePointer(e.Pointer);
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        var pos = e.GetCurrentPoint(null).Position;
        var scale = RasterizationScale;
        var w = Math.Max(360, _sizeStart.Width + (int)((pos.X - _dragStart.X) * scale));
        var h = Math.Max(100, _sizeStart.Height + (int)((pos.Y - _dragStart.Y) * scale));
        AppWindow.Resize(new SizeInt32(w, h));
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ResizeGrip.ReleasePointerCapture(e.Pointer);
        PersistWindowBounds();
    }

    private void Grip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _resizing = false;

    private void Panel_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _toolbarTimer?.Stop();
        Toolbar.Opacity = 1;
    }

    private void Panel_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _toolbarTimer ??= _dispatcher.CreateTimer();
        _toolbarTimer.Interval = TimeSpan.FromMilliseconds(900);
        _toolbarTimer.IsRepeating = false;
        _toolbarTimer.Tick += (_, _) =>
        {
            _toolbarTimer.Stop();
            if (!_dragging && !_resizing)
            {
                Toolbar.Opacity = 0;
            }
        };
        _toolbarTimer.Start();
    }

    private void PersistWindowBounds()
    {
        var appWindow = AppWindow;
        var s = App.Settings;
        s.WindowX = appWindow.Position.X;
        s.WindowY = appWindow.Position.Y;
        s.WindowWidth = appWindow.Size.Width;
        s.WindowHeight = appWindow.Size.Height;
        App.SettingsService.Save();
    }

    // --- Toolbar -----------------------------------------------------------

    private async void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Write($"[ui] listen toggle, currently listening={App.Ui.IsListening}");
        if (App.Ui.IsListening)
        {
            StopAll();
            SetStatus("已暂停");
        }
        else
        {
            await StartAsync();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var wasListening = App.Ui.IsListening;
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += async (_, _) =>
        {
            _settingsWindow = null;
            App.ApplyAppearance();
            PersistWindowBounds();

            if (wasListening || App.Settings.AutoStartCapture)
            {
                await StartAsync();
            }
        };
        _settingsWindow.Activate();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        PersistWindowBounds();
        StopAll();
    }
}
