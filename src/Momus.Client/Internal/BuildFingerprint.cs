using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Momus.Client.Internal;

/// <summary>
/// A version that changes when the build does. The server recognises a deploy as a version it has
/// not seen before, and the SDK only puts the commit in <c>InformationalVersion</c> when it can see
/// the repository — which most Dockerfiles exclude. Such a build reports "1.0.0" forever, every
/// deploy looks like a restart, and <c>regression</c> can never fire.
/// </summary>
/// <remarks>
/// The fingerprint is a hash of the module version id of every managed assembly shipped with the
/// application, plus the runtime version. Compilers are deterministic, so the same source gives
/// the same ids and a rebuild is not mistaken for a deploy. The entry assembly alone would not do:
/// it compiles against its projects' reference assemblies, and a change inside a method body
/// leaves its id unchanged — measured on .NET 10, a body-only change in a referenced library moved
/// the library's id and not the application's. Package upgrades change the fingerprint too, which
/// is right: they are deploys.
/// </remarks>
internal static class BuildFingerprint
{
    /// <summary>
    /// <paramref name="informationalVersion"/> as it is when it already names a revision (anything
    /// after a <c>+</c>), otherwise with <c>+build.&lt;fingerprint&gt;</c> appended.
    /// </summary>
    public static string? Version(string? informationalVersion, string directory, Assembly? entry)
    {
        if (informationalVersion?.Contains('+') == true) return informationalVersion;

        var fingerprint = Compute(directory, entry);
        return fingerprint is null
            ? informationalVersion
            : $"{(string.IsNullOrEmpty(informationalVersion) ? "0.0.0" : informationalVersion)}+build.{fingerprint}";
    }

    /// <summary>Twelve hex characters, or null when nothing could be read. Never throws.</summary>
    internal static string? Compute(string directory, Assembly? entry)
    {
        try
        {
            var parts = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (ModuleVersionId(file) is { } id) parts.Add($"{Path.GetFileName(file)}:{id:N}");
            }

            // A single-file publish has no loose assemblies; the entry one is still in memory.
            if (parts.Count == 0 && entry is not null) parts.Add($"{entry.GetName().Name}:{entry.ManifestModule.ModuleVersionId:N}");
            if (parts.Count == 0) return null;

            parts.Add($"runtime:{Environment.Version}");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
        catch (Exception)
        {
            // Reporting "1.0.0" is the old behaviour, not a failure.
            return null;
        }
    }

    /// <summary>Read from the file's metadata, without loading it. Null for anything that is not a managed assembly.</summary>
    private static Guid? ModuleVersionId(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
