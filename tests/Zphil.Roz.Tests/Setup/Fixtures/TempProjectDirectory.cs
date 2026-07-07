namespace Zphil.Roz.Tests.Setup.Fixtures;

/// <summary>
///     An isolated temp directory for setup-configurator tests, deleted on disposal.
///     Replaces the per-fixture <c>Path.Combine(Path.GetTempPath(), ...) + Guid.NewGuid()</c>
///     pattern that otherwise gets reimplemented in every test class.
/// </summary>
internal sealed class TempProjectDirectory : IDisposable
{
    public TempProjectDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }

    public static implicit operator string(TempProjectDirectory directory) => directory.Path;
}
