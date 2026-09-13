using LiveCaptions.Models;
using LiveCaptions.Services;
using Microsoft.UI.Xaml;

namespace LiveCaptions;

public partial class App : Application
{
    private static UiState? _ui;

    public static SettingsService SettingsService { get; } = new();
    public static AppSettings Settings => SettingsService.Settings;

    /// <summary>
    /// Shared UI state instance (declared as a resource in App.xaml).
    /// Resolved lazily: touching Application.Resources inside the App constructor throws.
    /// </summary>
    public static UiState Ui => _ui ??= (UiState)Current.Resources["Ui"];

    private Window? _window;

    public App()
    {
        Log.Write("[boot] App ctor");
        UnhandledException += (_, e) => Log.Write($"[fatal] unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"[fatal] appdomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write($"[fatal] task: {e.Exception}");
        try
        {
            InitializeComponent();
            Log.Write("[boot] App initialized");
        }
        catch (Exception ex)
        {
            Log.Write($"[fatal] App.InitializeComponent: {ex}");
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write("[boot] OnLaunched");
        SettingsService.Load();
        Log.Write($"[boot] settings loaded, engine={Settings.AsrEngine}, llm={Settings.LlmModelPath}");
        ApplyAppearance();

        try
        {
            _window = new MainWindow();
            _window.Activate();
            Log.Write("[boot] main window activated");
        }
        catch (Exception ex)
        {
            Log.Write($"[fatal] MainWindow: {ex}");
            throw;
        }
    }

    public static void ApplyAppearance()
    {
        var s = Settings;
        Ui.ShowOriginal = s.ShowOriginal;
        Ui.TranslationFontSize = s.FontSize;
        Ui.OriginalFontSize = Math.Max(10, Math.Round(s.FontSize * 0.64));
        Ui.PanelOpacity = s.PanelOpacity;
    }
}
