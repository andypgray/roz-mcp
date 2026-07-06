using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

// TestClassifier.SetOverrides mutates process-global statics; IsTestProjectTests (Extensions) reads
// the same statics via IsTestProject(). Both classes share this non-parallel collection so the
// ["TestFixture"] namespace override here cannot race IsTestProjectTests' ShouldBe(false). (TQ-H4)
[CollectionDefinition("TestClassifierStatics", DisableParallelization = true)]
public class TestClassifierStaticsCollection;

[Collection("TestClassifierStatics")]
public class TestClassifierTests(WorkspaceFixture fixture) : IDisposable
{
    public void Dispose() => TestClassifier.SetOverrides(null, null);

    [Fact]
    public async Task IsConfiguredAsTest_NoConfig_ReturnsFalse()
    {
        // Arrange
        TestClassifier.SetOverrides([], []);
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.Single(p => p.Name == "TestFixture");

        // Act
        bool result = TestClassifier.IsConfiguredAsTest(project);

        // Assert
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(new[] { "TestFixture" }, true)]
    [InlineData(new[] { "testfixture" }, true)]
    [InlineData(new[] { "SomeOtherFolder" }, false)]
    [InlineData(new[] { "SomeFolder", "TestFixture" }, true)]
    public async Task IsConfiguredAsTest_PathPrefixes_ReturnsExpected(string[] pathPrefixes, bool expected)
    {
        TestClassifier.SetOverrides(pathPrefixes, []);
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.Single(p => p.Name == "TestFixture");

        bool result = TestClassifier.IsConfiguredAsTest(project);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData(new[] { "TestFixture" }, true)]
    [InlineData(new[] { "testfixture" }, true)]
    [InlineData(new[] { "TestFix" }, false)]
    public async Task IsConfiguredAsTest_NamespacePrefixes_ReturnsExpected(string[] namespacePrefixes, bool expected)
    {
        TestClassifier.SetOverrides([], namespacePrefixes);
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.Single(p => p.Name == "TestFixture");

        bool result = TestClassifier.IsConfiguredAsTest(project);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task IsTestProject_ConfiguredProject_ReturnsTrue()
    {
        // Arrange — TestFixture is NOT a test project by heuristic, but is configured as one
        TestClassifier.SetOverrides([], ["TestFixture"]);
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.Single(p => p.Name == "TestFixture");

        // Act
        bool result = project.IsTestProject();

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task IsTestProject_HeuristicProject_StillWorks()
    {
        // Arrange — TestFixture.Tests is a test project by heuristic (xUnit refs)
        TestClassifier.SetOverrides([], []);
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project? testProject = solution.Projects.FirstOrDefault(p => p.Name == "TestFixture.Tests");

        // Assert
        testProject.ShouldNotBeNull();
        testProject.IsTestProject().ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParsePrefixes_NullOrWhitespace_ReturnsEmpty(string? value) =>
        TestClassifier.ParsePrefixes(value).ShouldBeEmpty();

    [Theory]
    [InlineData("tests;specs", new[] { "tests", "specs" })]
    [InlineData("tests,specs", new[] { "tests", "specs" })]
    [InlineData("tests,specs;fixtures", new[] { "tests", "specs", "fixtures" })]
    [InlineData(@"tests\integration;specs", new[] { "tests/integration", "specs" })]
    [InlineData("tests/;specs/", new[] { "tests", "specs" })]
    [InlineData("  tests  ,  specs  ", new[] { "tests", "specs" })]
    public void ParsePrefixes_ValidInput_SplitsAndNormalises(string input, string[] expected) =>
        TestClassifier.ParsePrefixes(input).ShouldBe(expected);
}
