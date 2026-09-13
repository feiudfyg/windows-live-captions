using LLama.Native;

namespace LiveCaptions.Services;

/// <summary>
/// Configures the llama.cpp native runtime exactly once, before any LLamaSharp
/// API is used. Re-configuring after the native library has loaded throws.
/// </summary>
internal static class LlamaRuntime
{
    private static readonly object Gate = new();
    private static bool _configured;

    /// <summary>
    /// True when the CUDA 12 runtime (cudart/cublas) that the llama.cpp Cuda12
    /// backend links against is present next to the app.
    /// </summary>
    public static bool CudaRuntimeAvailable { get; } =
        File.Exists(CudaDllPath("ggml-cuda.dll")) && File.Exists(CudaDllPath("cudart64_12.dll"));

    private static string CudaDllPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12", name);

    private static string CudaDirectory
        => Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12");

    /// <summary>backend: "auto" (CUDA when available, else Vulkan), "vulkan" or "cpu".</summary>
    public static void EnsureConfigured(string backend)
    {
        lock (Gate)
        {
            if (_configured) return;

            var useCuda = backend.Equals("auto", StringComparison.OrdinalIgnoreCase) && CudaRuntimeAvailable;
            var useVulkan = !backend.Equals("cpu", StringComparison.OrdinalIgnoreCase);

            // Built-in CUDA probing is disabled: it looks for a folder named after the
            // detected CUDA major version and can never match the shipped cuda12 folder.
            NativeLibraryConfig.All
                .WithCuda(false)
                .WithVulkan(useVulkan)
                .WithAutoFallback(true);

            if (useCuda)
            {
                NativeLibraryConfig.All.WithSelectingPolicy(new Cuda12FirstSelectingPolicy(CudaDirectory));
            }

            _configured = true;
            Log.Write($"[llama] backend config: {backend} (cuda={useCuda}, vulkan={useVulkan}, cudaRuntimeAvailable={CudaRuntimeAvailable})");
        }
    }

    public static string DescribeBackend(string backend)
    {
        if (backend.Equals("cpu", StringComparison.OrdinalIgnoreCase)) return "CPU";
        if (backend.Equals("vulkan", StringComparison.OrdinalIgnoreCase)) return "llama.cpp Vulkan";
        return CudaRuntimeAvailable ? "llama.cpp CUDA" : "llama.cpp Vulkan";
    }
}
