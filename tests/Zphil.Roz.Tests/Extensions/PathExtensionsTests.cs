using Zphil.Roz.Extensions;

namespace Zphil.Roz.Tests.Extensions;

public class PathExtensionsTests
{
    [Theory]
    [InlineData("src/proj/obj/Debug/X.g.cs", "obj", true)] // /obj/
    [InlineData(@"src\proj\obj\Debug\X.g.cs", "obj", true)] // \obj\
    [InlineData("obj/Debug/X.g.cs", "obj", true)] // leading obj/
    [InlineData(@"obj\Debug\X.g.cs", "obj", true)] // leading obj\
    [InlineData("src/proj/OBJ/X.cs", "obj", true)] // case-insensitive
    [InlineData("src/objective/File.cs", "obj", false)] // segment boundary: objective != obj
    [InlineData("src/proj/bin/X.dll", "bin", true)] // bin segment
    public void ContainsDirectorySegment_VariousPaths_ReturnsExpected(
        string path, string segment, bool expected) =>
        PathExtensions.ContainsDirectorySegment(path, segment).ShouldBe(expected);
}
