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
    private bool _closing;
    private bool _pendingRestart;
    private bool _dragging;
    private bool _resizing;
    private (int X, int Y) _cursorStart;
    private PointInt32 _windowStart;
    private SizeInt32 _sizeStart;
    private DispatcherQueueTimer? _toolbarTimer;
    private System.ComponentModel.PropertyChangedEventHandler? _uiHandler;

    public MainWindow()
    {
        Log.Write("[boot] MainWindow ctor");
        InitializeComponent();
        _dispatcher = DispatcherQueue;

        LinesList.ItemsSource = _items;
        TranslateToggle.IsChecked = App.Settings.TranslateEnabled;

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

        ApplyBackdrop();

        var hwnd = Win32.GetHwnd(this);
        Win32.HideFromAltTab(hwnd);
        ApplyClickThrough(hwnd);
        RegisterClickThroughHotKey(hwnd);

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)App.Settings.WindowWidth;
        var h = (int)App.Settings.WindowHeight;
        int x, y;
        if (App.Settings.WindowX is { } sx && App.Settings.WindowY is { } sy)
        {
            // A monitor may have been unplugged since the position was saved: keep
            // the overlay inside the work area of whatever display it points at.
            var saved = DisplayArea.GetFromPoint(new PointInt32((int)sx, (int)sy), DisplayAreaFallback.Primary).WorkArea;
            x = Math.Clamp((int)sx, saved.X, Math.Max(saved.X, saved.X + saved.Width - w));
            y = Math.Clamp((int)sy, saved.Y, Math.Max(saved.Y, saved.Y + saved.Height - h));
            if (x != (int)sx || y != (int)sy)
            {
                Log.Write($"[ui] window position clamped to the work area ({sx},{sy} -> {x},{y})");
            }
        }
        else
        {
            x = area.X + (area.Width - w) / 2;
            y = area.Y + area.Height - h - 96;
        }

        appWindow.MoveAndResize(new RectInt32(x, y, w, h));

        // Style juggling (tool-window flag, click-through) can drop the topmost
        // bit, so re-assert it as the very last step.
        Win32.SetTopMost(hwnd, App.Settings.AlwaysOnTop);
        Log.Write($"[ui] final topmost={App.Settings.AlwaysOnTop} exstyle=0x{Win32.GetExtendedStyle(hwnd):X}");
    }

    private const int ClickThroughHotKeyId = 0x4C43; // 'LC'
    private Win32.WindowProc? _windowProc;
    private nint _previousWindowProc;

    /// <summary>Click-through is applied live and can always be undone with Ctrl+Alt+L.</summary>
    private void ApplyClickThrough(nint hwnd)
    {
        Win32.SetClickThrough(hwnd, App.Settings.ClickThrough);
        Log.Write(App.Settings.ClickThrough
            ? "[ui] click-through on (Ctrl+Alt+L to turn it off)"
            : "[ui] click-through off");
    }

    private void RegisterClickThroughHotKey(nint hwnd)
    {
        const uint ModAlt = 0x0001;
        const uint ModControl = 0x0002;
        const uint VkL = 0x4C;

        _windowProc = (h, message, wParam, lParam) =>
        {
            if (message == 0x0312 && wParam.ToInt64() == ClickThroughHotKeyId)
            {
                ToggleClickThrough();
                return 0;
            }

            return Win32.CallPreviousWindowProc(_previousWindowProc, h, message, wParam, lParam);
        };

        _previousWindowProc = Win32.SetWindowProc(hwnd, _windowProc);
        if (!Win32.RegisterToggleHotKey(hwnd, ClickThroughHotKeyId, ModAlt | ModControl, VkL))
        {
            Log.Write("[ui] Ctrl+Alt+L hotkey registration failed; use the settings window to change click-through");
        }
    }

    private void ToggleClickThrough()
    {
        App.Settings.ClickThrough = !App.Settings.ClickThrough;
        App.SettingsService.Update(App.Settings);
        ApplyClickThrough(Win32.GetHwnd(this));
        SetStatus(App.Settings.ClickThrough
            ? "鼠标穿透已开启（Ctrl+Alt+L 关闭）"
            : "鼠标穿透已关闭");
    }

    /// <summary>
    /// Panel background effect: acrylic (Windows backdrop), Gaussian blur
    /// (tint-free acrylic = live blur without the milky sheet), or plain
    /// translucency. Failures are cosmetic only and fall back to acrylic.
    /// </summary>
    internal void ApplyBackdrop()
    {
        try
        {
            var hwnd = Win32.GetHwnd(this);
            Win32.PrepareWindowFrame(hwnd);
            Win32.SetTopMost(hwnd, App.Settings.AlwaysOnTop);
            Log.Write($"[ui] topmost={App.Settings.AlwaysOnTop} exstyle=0x{Win32.GetExtendedStyle(hwnd):X}");
            switch (App.Settings.BackdropMode.ToLowerInvariant())
            {
                case "blur":
                    WindowTransparency.DisableAccent(hwnd);
                    WindowTransparency.ExtendFrame(hwnd, false);
                    SystemBackdrop = new CleanBlurBackdrop();
                    Log.Write("[ui] backdrop: Gaussian blur (tint-free acrylic)");
                    break;
                case "simple":
                    // Three parts have to line up (each alone leaves an opaque black
                    // window): the DWM frame extended over the client area, the legacy
                    // accent policy, and a backdrop object that disables the system
                    // backdrop. Result: content composited straight against the
                    // desktop, sharp and unblurred.
                    WindowTransparency.EnableSimple(hwnd);
                    SystemBackdrop = new TransparentBackdrop(hwnd);
                    Log.Write("[ui] backdrop: plain translucency (transparent window background)");
                    break;
                default:
                    // Leaving "simple" behind must reset the frame/accent state, or the
                    // acrylic sheet is composited over a still-extended frame.
                    WindowTransparency.DisableAccent(hwnd);
                    WindowTransparency.ExtendFrame(hwnd, false);
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    Log.Write("[ui] backdrop: acrylic");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] backdrop '{App.Settings.BackdropMode}' failed: {ex.Message} - falling back to acrylic");
            try
            {
                WindowTransparency.DisableAccent(Win32.GetHwnd(this));
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
                SystemBackdrop = null;
            }
        }
    }

    private void SubscribeUiEvents()
    {
        _uiHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(UiState.ShowOriginal))
            {
                RefreshOriginalVisibility();
            }
        };
        App.Ui.PropertyChanged += _uiHandler;

        _toolbarTimer = _dispatcher.CreateTimer();
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
        _capture = new AudioCaptureService(AudioCaptureService.ParseMode(App.Settings.AudioSource));
        _capture.SamplesAvailable += (buffer, count) => _pipeline?.PushAudio(buffer, count);
        _capture.Failed += msg => SetStatus(msg, error: true);
        _capture.DeviceChanged += msg => Log.Write($"[audio] device: {msg}");
        _capture.Start();
        Log.Write($"[audio] capture started ({_capture.Mode})");
    }

    private async Task StartAsync()
    {
        if (_busy)
        {
            // Applying settings while models load must not be silently dropped:
            // remember it and re-run as soon as the current start-up finishes.
            _pendingRestart = true;
            Log.Write("[pipeline] start requested while loading; queued");
            return;
        }

        SetBusy(true);
        TranslateToggle.IsChecked = App.Settings.TranslateEnabled;
        Log.Write($"[pipeline] StartAsync entered, triggered by: {Environment.StackTrace.Split('\n').Skip(3).FirstOrDefault()?.Trim()}");
        try
        {
            await StopAllAsync();
            if (CheckClosed()) return;
            _dispatcher.TryEnqueue(() => SetStatus("正在加载语音识别模型…"));

            var asr = CreateAsrEngine();
            _asr = asr; // reachable by StopAllAsync while the model loads
            await Task.Run(() => asr.LoadAsync()).ConfigureAwait(true);
            if (CheckClosed()) return;
            SetStatus($"语音识别就绪 · {asr.Name} ({asr.Backend})");

            ITranslator? translator = null;
            if (App.Settings.TranslateEnabled)
            {
                if (App.Settings.LlmBackend.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                {
                    var llmPath = ResolveLlmPath();
                    SetStatus("正在启动 llama.cpp 服务…");
                    var server = new LocalLlamaServer(App.Settings);
                    _llamaServer = server; // ditto: kill it if the window closes first
                    await Task.Run(() => server.StartAsync(llmPath)).ConfigureAwait(true);
                    if (CheckClosed()) return;

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

                    if (CheckClosed()) return;
                    SetStatus($"翻译就绪 · {Path.GetFileNameWithoutExtension(llmPath)} (llama.cpp :{server.ActualPort})");
                }
                else if (App.Settings.LlmBackend.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("正在连接翻译服务…");
                    var httpTranslator = new HttpTranslationService(App.Settings);
                    await Task.Run(() => httpTranslator.LoadAsync()).ConfigureAwait(true);
                    translator = httpTranslator;
                    if (CheckClosed()) return;
                    SetStatus($"翻译就绪 · {httpTranslator.ModelName} ({httpTranslator.Backend})");
                }
                else
                {
                    var llmPath = ResolveLlmPath();
                    if (File.Exists(llmPath))
                    {
                        SetStatus("正在加载翻译模型…");
                        translator = new TranslationService(llmPath, App.Settings);
                        _translator = translator; // free multi-GB weights if the window closes mid-load
                        await Task.Run(() => translator.LoadAsync()).ConfigureAwait(true);
                        if (CheckClosed()) return;
                        SetStatus($"翻译就绪 · {translator.ModelName} ({translator.Backend})");
                    }
                    else
                    {
                        SetStatus("未找到翻译模型，请在设置中下载（本次仅显示原文）");
                    }
                }
            }

            _translator = translator;
            if (CheckClosed()) return;

            // Adaptive segmentation needs the tiny TEN VAD model; fetch it once.
            if (App.Settings.UseVad && !ModelCatalog.TenVad.Exists)
            {
                try
                {
                    var progress = new Progress<(string file, double ratio, long received, long total)>(p =>
                        SetStatus($"正在获取分段模型 · {p.ratio * 100:0}%"));
                    SetStatus("正在获取自适应分段模型 (TEN VAD)…");
                    await new ModelDownloadService()
                        .EnsureDownloadedAsync(ModelCatalog.TenVad, progress)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log.Write($"[vad] download failed: {ex.Message}");
                }
            }

            if (CheckClosed()) return;

            var pipeline = new CaptionPipeline(App.Settings, _dispatcher);
            pipeline.SetEngines(asr, translator);
            pipeline.SetVad(CreateVad(asr));

            pipeline.ItemUpdated += OnItemUpdated;
            pipeline.ErrorOccurred += msg => SetStatus(msg, error: true);
            pipeline.Start();
            _pipeline = pipeline;
            if (CheckClosed()) return;

            StartCapture();
            if (_capture is null || !_capture.IsRunning)
            {
                // Starting the capture can fail (device gone, service hung): say so
                // instead of pretending to listen.
                SetStatus("音频捕获启动失败：请检查音频设备后重新点击开始", error: true);
                await StopAllAsync();
                return;
            }

            var target = LanguageCatalog.FindTarget(App.Settings.TargetLanguage);
            var source = LanguageCatalog.FindSource(App.Settings.SourceLanguage);
            var input = AudioCaptureService.ModeLabel(AudioCaptureService.ParseMode(App.Settings.AudioSource));
            SetStatus(translator is null
                ? $"正在监听{input} · {asr.Name} · 仅原文字幕（{source.DisplayName}）"
                : $"正在监听{input} · {asr.Name} · {source.DisplayName} → {target.DisplayName}");
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
            await StopAllAsync();
        }
        finally
        {
            SetBusy(false);
            if (_pendingRestart)
            {
                _pendingRestart = false;
                _dispatcher.TryEnqueue(() => _ = StartAsync());
            }
        }
    }

    /// <summary>True when the window is closing: start-up must unwind instead of
    /// touching a disposed window or leaving engines behind.</summary>
    private bool CheckClosed()
    {
        if (!_closing) return false;
        Log.Write("[pipeline] start aborted: window closing");
        return true;
    }

    /// <summary>
    /// Loading state: spinner next to the status text, start/pause and the
    /// translation toggle disabled. Settings and close stay available so the app
    /// never feels frozen.
    /// </summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _dispatcher.TryEnqueue(() =>
        {
            LoadRing.IsActive = busy;
            LoadRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ListenButton.IsEnabled = !busy;
            TranslateToggle.IsEnabled = !busy;
        });
    }

    private async Task StopAllAsync()
    {
        if (_pipeline is not null)
        {
            _pipeline.ItemUpdated -= OnItemUpdated;
            var pipeline = _pipeline;
            _pipeline = null;

            // Wait for the audio/translate loops to finish before the engines they
            // use are disposed - otherwise a native decode can outlive its model.
            try
            {
                await pipeline.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Write($"[pipeline] dispose failed: {ex.Message}");
            }
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

    /// <summary>
    /// Picks the segmentation model: FireRedVAD (streamed by the PyTorch sidecar)
    /// when requested and available, otherwise the in-process sherpa VAD.
    /// </summary>
    private static IVad? CreateVad(IAsrEngine asr)
    {
        var settings = App.Settings;
        if (!settings.UseVad) return null;

        var wantsFireRed = settings.VadEngine.Equals("firered", StringComparison.OrdinalIgnoreCase)
                           || (settings.VadEngine.Equals("auto", StringComparison.OrdinalIgnoreCase)
                               && App.Settings.AsrEngine.Equals("cohere-py", StringComparison.OrdinalIgnoreCase));

        if (wantsFireRed && asr is CoherePyTorchAsrEngine { ServerAddress: { } address })
        {
            Log.Write("[vad] using FireRedVAD streaming via the ASR sidecar");
            return new RemoteVadSegmenter(address);
        }

        var vadPath = ModelCatalog.DefaultVadModel;
        if (!File.Exists(vadPath)) return null;

        var preferSilero = settings.VadEngine.Equals("silero", StringComparison.OrdinalIgnoreCase);
        if (preferSilero && ModelCatalog.SileroVad.Exists)
        {
            vadPath = ModelCatalog.PathOf(ModelCatalog.SileroVad.Files[0].FileName);
        }

        return new VadSegmenter(vadPath);
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

        var language = string.Equals(s.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : s.SourceLanguage;

        // Full precision Cohere Transcribe through the PyTorch sidecar.
        if (string.Equals(s.AsrEngine, "cohere-py", StringComparison.OrdinalIgnoreCase))
        {
            return new CoherePyTorchAsrEngine(new PyTorchAsrServer(s), language);
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

        return new SherpaAsrEngine(modelDirectory, language, s.AsrProvider);
    }

    private static string ResolveLlmPath()
        => string.IsNullOrWhiteSpace(App.Settings.LlmModelPath) ? ModelCatalog.DefaultLlmModel : App.Settings.LlmModelPath;

    private void OnItemUpdated(CaptionItem item)
    {
        // Local diagnostics; truncated so long sessions do not fill the log with text.
        var original = item.Original.Length > 200 ? item.Original[..200] : item.Original;
        var translation = item.Translation ?? "";
        if (translation.Length > 200) translation = translation[..200];
        Log.Write($"[caption{(item.IsPartial ? "/partial" : "")}] {original} || {translation}");
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
        _cursorStart = Win32.GetCursorPosition();
        _windowStart = AppWindow.Position;
        Panel.CapturePointer(e.Pointer);
    }

    private void Panel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        // Screen cursor coordinates are physical pixels, exactly what AppWindow
        // works in - no DPI conversion and no feedback from the window moving.
        var cursor = Win32.GetCursorPosition();
        var x = _windowStart.X + (cursor.X - _cursorStart.X);
        var y = _windowStart.Y + (cursor.Y - _cursorStart.Y);
        AppWindow.Move(new PointInt32(x, y));
    }

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
        _cursorStart = Win32.GetCursorPosition();
        _sizeStart = AppWindow.Size;
        ResizeGrip.CapturePointer(e.Pointer);
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        var cursor = Win32.GetCursorPosition();
        var w = Math.Max(360, _sizeStart.Width + (cursor.X - _cursorStart.X));
        var h = Math.Max(100, _sizeStart.Height + (cursor.Y - _cursorStart.Y));
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
        if (_toolbarTimer is null) return;
        _toolbarTimer.Stop();
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
            await StopAllAsync();
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
        _pipeline?.ResetLiveCaption();
        EmptyHint.Visibility = Visibility.Visible;
    }

    private async void TranslateToggle_Click(object sender, RoutedEventArgs e)
        => await SetTranslateEnabledAsync(TranslateToggle.IsChecked == true);

    private async void TranslateMenu_Click(object sender, RoutedEventArgs e)
        => await SetTranslateEnabledAsync(!App.Settings.TranslateEnabled);

    /// <summary>Captions-only mode: toggling translation reconfigures the running pipeline.</summary>
    private async Task SetTranslateEnabledAsync(bool enabled)
    {
        if (App.Settings.TranslateEnabled != enabled)
        {
            App.Settings.TranslateEnabled = enabled;
            App.SettingsService.Save();
            Log.Write($"[ui] translate -> {enabled}");
        }

        TranslateToggle.IsChecked = enabled;
        App.ApplyAppearance();

        if (App.Ui.IsListening)
        {
            await StartAsync();
        }
        else
        {
            SetStatus(enabled ? "翻译已开启 · 点击 ▶ 开始监听" : "仅原文字幕模式 · 点击 ▶ 开始监听");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var wasListening = App.Ui.IsListening;
        var window = new SettingsWindow();
        _settingsWindow = window;
        window.Closed += async (_, _) =>
        {
            _settingsWindow = null;

            // Closing without saving only discards the preview; engines are not
            // touched and nothing is reloaded.
            App.ApplyAppearance();
            PersistWindowBounds();

            if (!window.Applied)
            {
                Log.Write("[ui] settings closed without saving");
                return;
            }

            Log.Write("[ui] settings applied, reloading engines");
            ApplyBackdrop();
            ApplyClickThrough(Win32.GetHwnd(this));
            // Only restart if it was actually running: AutoStartCapture is about
            // launching the app, not about silently starting a paused session.
            if (wasListening)
            {
                if (_busy)
                {
                    SetStatus("设置已保存；当前加载完成后自动重启监听");
                }

                await StartAsync();
            }
            else
            {
                SetStatus("设置已更新 · 点击 ▶ 开始监听");
            }
        };
        window.Activate();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        PersistWindowBounds();
        if (_uiHandler is not null)
        {
            App.Ui.PropertyChanged -= _uiHandler;
            _uiHandler = null;
        }

        _toolbarTimer?.Stop();
        try
        {
            Win32.UnregisterToggleHotKey(Win32.GetHwnd(this), ClickThroughHotKeyId);
        }
        catch
        {
            // The window may already be gone; nothing to clean up in that case.
        }

        _ = StopAllAsync();
    }
}
