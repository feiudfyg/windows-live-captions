using LLama.Abstractions;
using LLama.Native;

namespace LiveCaptions.Services;

/// <summary>
/// Selects the locally shipped CUDA 12 backend before LLamaSharp's built-in
/// candidates.
///
/// LLamaSharp derives the CUDA folder name from the *detected* CUDA major
/// version (13 on a machine with a CUDA 13 toolkit), but the backend package
/// only ships a "cuda12" runtime folder - so the built-in candidate list never
/// finds it and silently falls back to Vulkan. This policy probes the local
/// cuda12 folder first and then hands over to the default policy (Vulkan/CPU).
/// </summary>
internal sealed class Cuda12FirstSelectingPolicy : INativeLibrarySelectingPolicy
{
    private readonly string _directory;

    public Cuda12FirstSelectingPolicy(string directory) => _directory = directory;

    public IEnumerable<INativeLibrary> Apply(NativeLibraryConfig.Description description, SystemInfo systemInfo,
        NativeLogConfig.LLamaLogCallback? logCallback)
    {
        var fileName = description.Library switch
        {
            NativeLibraryName.LLama => "llama.dll",
            NativeLibraryName.Mtmd => "mtmd.dll",
            _ => null,
        };

        if (fileName is not null)
        {
            var path = Path.Combine(_directory, fileName);
            if (File.Exists(path))
            {
                yield return new NativeLibraryFromPath(path);
            }
        }

        foreach (var library in new DefaultNativeLibrarySelectingPolicy().Apply(description, systemInfo, logCallback))
        {
            yield return library;
        }
    }
}
