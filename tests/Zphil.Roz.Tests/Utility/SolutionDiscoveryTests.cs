using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Tests.Utility;

public class SolutionDiscoveryTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "roslyn-discovery-tests", Guid.NewGuid().ToString("N"));

    public SolutionDiscoveryTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private string CreateDir(params string[] segments)
    {
        string dir = Path.Combine([tempRoot, .. segments]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CreateSlnFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "");
        return path;
    }

    // --- Explicit path ---

    [Fact]
    public void DiscoverSolution_ExplicitPath_ReturnsThatPath()
    {
        string slnPath = CreateSlnFile(tempRoot, "Explicit.sln");

        string result = FileUtility.DiscoverSolution(slnPath);

        result.ShouldBe(Path.GetFullPath(slnPath));
    }

    [Fact]
    public void DiscoverSolution_ExplicitPath_NotFound_Throws()
    {
        string missing = Path.Combine(tempRoot, "DoesNotExist.sln");

        UserErrorException ex = Should.Throw<UserErrorException>(() => FileUtility.DiscoverSolution(missing));

        ex.Message.ShouldContain("Solution file not found");
    }

    [Fact]
    public void DiscoverSolution_ExplicitPathTakesPriority_OverCwdSolution()
    {
        string cwdDir = CreateDir("cwd-priority");
        CreateSlnFile(cwdDir, "CwdSolution.sln");
        string explicitSln = CreateSlnFile(tempRoot, "Explicit.sln");

        string result = FileUtility.DiscoverSolution(
            explicitSln, cwdDir);

        result.ShouldBe(Path.GetFullPath(explicitSln));
    }

    // --- CWD discovery ---

    [Fact]
    public void DiscoverSolution_SingleSlnInCwd_FindsIt()
    {
        string cwdDir = CreateDir("single-sln");
        string slnPath = CreateSlnFile(cwdDir, "MyProject.sln");

        string result = FileUtility.DiscoverSolution(workingDirectory: cwdDir);

        result.ShouldBe(slnPath);
    }

    [Fact]
    public void DiscoverSolution_SingleSlnxInCwd_FindsIt()
    {
        string cwdDir = CreateDir("single-slnx");
        string slnxPath = CreateSlnFile(cwdDir, "MyProject.slnx");

        string result = FileUtility.DiscoverSolution(workingDirectory: cwdDir);

        result.ShouldBe(slnxPath);
    }

    // --- Multiple solutions in CWD ---

    [Fact]
    public void DiscoverSolution_MultipleSlnInCwd_WalksParent()
    {
        // Parent has a single sln; child CWD has two — should skip CWD and find parent's
        string parentDir = CreateDir("multi-parent");
        string parentSln = CreateSlnFile(parentDir, "Parent.sln");
        string cwdDir = CreateDir("multi-parent", "child");
        CreateSlnFile(cwdDir, "First.sln");
        CreateSlnFile(cwdDir, "Second.sln");

        string result = FileUtility.DiscoverSolution(workingDirectory: cwdDir);

        result.ShouldBe(parentSln);
    }

    [Fact]
    public void DiscoverSolution_SlnAndSlnxInCwd_WalksParent()
    {
        // .sln + .slnx = two solutions — should skip CWD
        string parentDir = CreateDir("mixed-parent");
        string parentSln = CreateSlnFile(parentDir, "Parent.sln");
        string cwdDir = CreateDir("mixed-parent", "child");
        CreateSlnFile(cwdDir, "Project.sln");
        CreateSlnFile(cwdDir, "Project.slnx");

        string result = FileUtility.DiscoverSolution(workingDirectory: cwdDir);

        result.ShouldBe(parentSln);
    }

    // --- Parent / grandparent walk ---

    [Fact]
    public void DiscoverSolution_NotInCwd_FoundInParent()
    {
        string parentDir = CreateDir("parent-walk");
        string parentSln = CreateSlnFile(parentDir, "Found.sln");
        string cwdDir = CreateDir("parent-walk", "src");

        string result = FileUtility.DiscoverSolution(workingDirectory: cwdDir);

        result.ShouldBe(parentSln);
    }

    [Fact]
    public void DiscoverSolution_NotInCwd_FoundInGrandparent()
    {
        string grandparentDir = CreateDir("grandparent-walk");
        string grandparentSln = CreateSlnFile(grandparentDir, "Root.sln");
        string cwdDir = CreateDir("grandparent-walk", "src", "app");

        string result = FileUtility.DiscoverSolution(workingDirectory: cwdDir);

        result.ShouldBe(grandparentSln);
    }

    // --- No solution found ---

    [Fact]
    public void DiscoverSolution_NoSolutionAnywhere_Throws()
    {
        // Arrange — a leaf directory with no solution files anywhere up the chain.
        string cwdDir = CreateDir("empty-root", "deep", "nested");

        // Guard: DiscoverSolution walks parents to the drive root, so a stray .sln/.slnf/.slnx in ANY
        // ancestor of our temp dir would make it FIND one (and not throw). Fail loudly on a polluted
        // environment rather than letting this test pass — or throw — for the wrong reason.
        for (DirectoryInfo? dir = new(cwdDir); dir is not null; dir = dir.Parent)
        {
            string[] solutionFiles;
            try
            {
                solutionFiles = Directory.EnumerateFiles(dir.FullName, "*.slnx")
                    .Concat(Directory.EnumerateFiles(dir.FullName, "*.slnf"))
                    .Concat(Directory.EnumerateFiles(dir.FullName, "*.sln"))
                    .ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            solutionFiles.ShouldBeEmpty($"Stray solution file under ancestor '{dir.FullName}' makes this test meaningless — clean it.");
        }

        // Act & Assert — no solution anywhere up the chain, so discovery throws.
        UserErrorException ex = Should.Throw<UserErrorException>(() => FileUtility.DiscoverSolution(workingDirectory: cwdDir));

        ex.Message.ShouldContain("No .sln, .slnf or .slnx file found");
    }

    // --- Multiple solutions with no single-solution fallback ---

    [Fact]
    public void DiscoverSolution_MultipleSolutionsNowhere_ThrowsWithFileNames()
    {
        // CWD has multiple solutions, no parent has exactly one — should list the files
        string cwdDir = CreateDir("multi-only");
        CreateSlnFile(cwdDir, "Alpha.sln");
        CreateSlnFile(cwdDir, "Beta.slnx");

        UserErrorException ex = Should.Throw<UserErrorException>(() => FileUtility.DiscoverSolution(workingDirectory: cwdDir));

        ex.Message.ShouldContain("Multiple solution files found");
        ex.Message.ShouldContain("Alpha.sln");
        ex.Message.ShouldContain("Beta.slnx");
        ex.Message.ShouldContain(RozEnvVars.SolutionPath.Name);
    }

    [Fact]
    public void DiscoverSolution_MultipleInParent_NoSingleAnywhere_ReportsClosestAmbiguous()
    {
        // CWD is empty, parent has multiple — reports the parent's ambiguity
        string parentDir = CreateDir("ambiguous-parent");
        CreateSlnFile(parentDir, "One.sln");
        CreateSlnFile(parentDir, "Two.sln");
        string cwdDir = CreateDir("ambiguous-parent", "src");

        UserErrorException ex = Should.Throw<UserErrorException>(() => FileUtility.DiscoverSolution(workingDirectory: cwdDir));

        ex.Message.ShouldContain("Multiple solution files found");
        ex.Message.ShouldContain("One.sln");
        ex.Message.ShouldContain("Two.sln");
    }
}

[CollectionDefinition("SolutionDiscovery", DisableParallelization = true)]
public class SolutionDiscoveryCollection;

/// <summary>
///     Tests that involve the <see cref="RozEnvVars.SolutionPath" /> environment variable are
///     serialized to avoid race conditions from concurrent env var mutations.
/// </summary>
[Collection("SolutionDiscovery")]
public class SolutionDiscoveryEnvVarTests : IDisposable
{
    private readonly string? originalEnvValue;
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "roslyn-discovery-env-tests", Guid.NewGuid().ToString("N"));

    public SolutionDiscoveryEnvVarTests()
    {
        Directory.CreateDirectory(tempRoot);
        originalEnvValue = Environment.GetEnvironmentVariable(RozEnvVars.SolutionPath.Name);
    }

    public void Dispose()
    {
        // Always restore the original env var value
        Environment.SetEnvironmentVariable(RozEnvVars.SolutionPath.Name, originalEnvValue);

        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private string CreateSlnFile(string name)
    {
        string path = Path.Combine(tempRoot, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void DiscoverSolution_EnvVar_ReturnsThatPath()
    {
        string slnPath = CreateSlnFile("EnvVar.sln");
        Environment.SetEnvironmentVariable(RozEnvVars.SolutionPath.Name, slnPath);

        string result = FileUtility.DiscoverSolution();

        result.ShouldBe(Path.GetFullPath(slnPath));
    }

    [Fact]
    public void DiscoverSolution_EnvVar_NotFound_Throws()
    {
        string missing = Path.Combine(tempRoot, "Missing.sln");
        Environment.SetEnvironmentVariable(RozEnvVars.SolutionPath.Name, missing);

        UserErrorException ex = Should.Throw<UserErrorException>(() => FileUtility.DiscoverSolution());

        ex.Message.ShouldContain(RozEnvVars.SolutionPath.Name);
    }
}
