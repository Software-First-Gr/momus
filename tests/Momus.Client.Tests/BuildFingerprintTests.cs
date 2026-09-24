using Momus.Client.Internal;

namespace Momus.Client.Tests;

/// <summary>
/// The version the client reports is how the server tells a deploy from a restart. A build whose
/// informational version carries no revision — any Docker build that leaves <c>.git</c> out — used
/// to report the same version for ever, so no deploy was ever seen and <c>regression</c> never fired.
/// </summary>
public class BuildFingerprintTests
{
    [Fact]
    public void A_version_that_already_names_a_revision_is_left_alone()
    {
        using var dir = new ScratchDirectory("Momus.Core.dll");

        Assert.Equal("1.4.2+3f1c9d0", BuildFingerprint.Version("1.4.2+3f1c9d0", dir.Path, null));
    }

    [Fact]
    public void A_version_without_a_revision_gets_the_builds_fingerprint()
    {
        using var dir = new ScratchDirectory("Momus.Core.dll", "Momus.Client.dll");

        var version = BuildFingerprint.Version("1.0.0", dir.Path, null);

        Assert.Matches("^1\\.0\\.0\\+build\\.[0-9a-f]{12}$", version);
    }

    [Fact]
    public void The_same_assemblies_give_the_same_fingerprint_so_a_restart_is_not_a_deploy()
    {
        using var one = new ScratchDirectory("Momus.Core.dll", "Momus.Client.dll");
        using var other = new ScratchDirectory("Momus.Core.dll", "Momus.Client.dll");

        Assert.Equal(BuildFingerprint.Compute(one.Path, null), BuildFingerprint.Compute(other.Path, null));
    }

    [Fact]
    public void Any_shipped_assembly_changing_changes_the_fingerprint()
    {
        // Not only the entry assembly: a change inside a referenced project leaves the entry
        // assembly's module id where it was, because it compiles against a reference assembly.
        using var dir = new ScratchDirectory("Momus.Core.dll", "Momus.Client.dll");
        var before = BuildFingerprint.Compute(dir.Path, null);

        File.Copy(Path.Combine(AppContext.BaseDirectory, "xunit.core.dll"), Path.Combine(dir.Path, "Momus.Core.dll"), overwrite: true);

        Assert.NotEqual(before, BuildFingerprint.Compute(dir.Path, null));
    }

    [Fact]
    public void Files_that_are_not_managed_assemblies_are_ignored()
    {
        using var dir = new ScratchDirectory("Momus.Core.dll", "Momus.Client.dll");
        var before = BuildFingerprint.Compute(dir.Path, null);

        File.WriteAllText(Path.Combine(dir.Path, "native-looking.dll"), "not a PE file");

        Assert.Equal(before, BuildFingerprint.Compute(dir.Path, null));
    }

    [Fact]
    public void With_nothing_to_read_the_version_is_reported_as_it_was()
    {
        using var dir = new ScratchDirectory();

        Assert.Equal("1.0.0", BuildFingerprint.Version("1.0.0", dir.Path, null));
        Assert.Equal("1.0.0", BuildFingerprint.Version("1.0.0", Path.Combine(dir.Path, "missing"), null));
    }

    /// <summary>A temporary directory holding copies of some of the test run's own assemblies.</summary>
    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory(params string[] assemblies)
        {
            Path = Directory.CreateTempSubdirectory("momus-fingerprint-").FullName;
            foreach (var name in assemblies)
            {
                File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, name), System.IO.Path.Combine(Path, name));
            }
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
