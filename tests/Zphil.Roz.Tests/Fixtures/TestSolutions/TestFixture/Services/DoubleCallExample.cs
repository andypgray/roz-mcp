namespace TestFixture.Services;

/// <summary>
///     Fixture for CR-19: a single calling method with two call sites of the same member.
///     find_references (invocations) must report the caller-symbol count in its header, not the
///     call-site count — the truncation guard and TotalCount are both in caller-symbol units.
/// </summary>
public class DoubleCallExample
{
    public int Ping() => 0;

    // Two call sites of Ping from one calling method.
    public int PingTwice() => Ping() + Ping();
}
