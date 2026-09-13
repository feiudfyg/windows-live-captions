using LLama.Abstractions;
using LLama.Native;

namespace LiveCaptions.SmokeTest;

/// <summary>
/// Same policy as the app: probe the local cuda12 folder first, then fall back
/// to LLamaSharp's default candidates (Vulkan/CPU).
/// </summary>
internal sealed class Cuda12FirstSelectingPolicy : INativeLibrarySelectingPolicy
{
    private readonly string _directory;

    public Cuda12FirstSelectingPolicy(string directory) => _directory = directory;

    public IEnumerable<INativeLibrary> Apply(NativeLibraryConfig.Description description, SystemInfo systemInfo,
        NativeLogConfig.LLamaLogCallback? logCallback)
    {
        var fileName = description.Library == NativeLibraryName.Mtmd ? "mtmd.dll" : "llama.dll";
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path))
        {
            yield return new NativeLibraryFromPath(path);
        }

        foreach (var library in new DefaultNativeLibrarySelectingPolicy().Apply(description, systemInfo, logCallback))
        {
            yield return library;
        }
    }
}
