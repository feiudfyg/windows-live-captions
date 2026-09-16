using System.Runtime.InteropServices;
using LiveCaptions.Interop;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Optional CUDA build of the sherpa-onnx native runtime.
///
/// The NuGet package only ships a CPU runtime. When the CUDA bundle is present
/// under data/runtime/sherpa-cuda/bin (sherpa-onnx-c-api.dll plus the CUDA
/// onnxruntime, cuDNN and cuBLAS DLLs in the same folder), that build is loaded
/// instead and the recognizer is asked for the "cuda" provider.
/// </summary>
internal static class SherpaRuntime
{
    private static readonly object Gate = new();
    private static bool _resolverInstalled;

    public static string CudaDirectory => Path.Combine(AppPaths.RuntimeDirectory, "sherpa-cuda", "bin");

    public static string CudaLibraryPath => Path.Combine(CudaDirectory, "sherpa-onnx-c-api.dll");

    public static bool CudaAvailable => File.Exists(CudaLibraryPath);

    /// <summary>Maps the settings value ("auto"/"cuda"/"cpu") to a provider name.</summary>
    public static string ResolveProvider(string? setting)
    {
        if (string.Equals(setting, "cpu", StringComparison.OrdinalIgnoreCase)) return "cpu";

        if (string.Equals(setting, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            if (CudaAvailable) return "cuda";

            Log.Write("[asr] CUDA requested but the sherpa CUDA runtime is not installed; using cpu");
            return "cpu";
        }

        return CudaAvailable ? "cuda" : "cpu";
    }

    /// <summary>
    /// Redirects the sherpa-onnx P/Invoke to the CUDA build. Must run before the
    /// first recognizer is created; safe to call repeatedly.
    /// </summary>
    public static bool Install(string provider)
    {
        if (!string.Equals(provider, "cuda", StringComparison.OrdinalIgnoreCase) || !CudaAvailable) return false;

        lock (Gate)
        {
            if (_resolverInstalled) return true;

            // ORT loads cuDNN/cuBLAS lazily at runtime, so the folder also has to
            // be on the process-wide search path.
            Win32.SetDllSearchDirectory(CudaDirectory);

            // Its own directory must win over the application directory, where the
            // CPU build of the same DLLs is deployed by the NuGet runtime package.
            var handle = Win32.LoadLibraryFromOwnDirectory(CudaLibraryPath);
            if (handle == 0)
            {
                Log.Write($"[asr] CUDA runtime failed to load: {CudaLibraryPath} (win32 error {Marshal.GetLastWin32Error()})");
                return false;
            }

            var assembly = typeof(SherpaOnnx.OfflineRecognizer).Assembly;
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name is "sherpa-onnx-c-api" ? handle : 0);
            _resolverInstalled = true;
            Log.Write($"[asr] sherpa CUDA runtime installed: {CudaLibraryPath}");
            return true;
        }
    }
}
