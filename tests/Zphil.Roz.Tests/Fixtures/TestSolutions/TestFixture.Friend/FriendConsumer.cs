using TestFixture.Services;

namespace TestFixture.Friend;

/// <summary>Friend-assembly consumer of ImpactSurface.FriendVisible() — TestFixture grants this
/// project [InternalsVisibleTo], so AccessibilityNarrow to internal stays Compatible here (F12).</summary>
public class FriendConsumer
{
    public int Use() => new ImpactSurface().FriendVisible();
}
