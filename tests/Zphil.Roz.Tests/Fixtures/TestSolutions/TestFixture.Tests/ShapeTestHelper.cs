namespace TestFixture.Shapes;

/// <summary>
///     Test helper in TestFixture.Shapes namespace — ensures the namespace spans multiple projects
///     for namespace dedup testing.
/// </summary>
internal static class ShapeTestHelper
{
    public static string DescribeAll(params IShape[] shapes) =>
        string.Join(", ", shapes.Select(s => s.Describe()));
}
