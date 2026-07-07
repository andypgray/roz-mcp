using System.Runtime.CompilerServices;

// Grants the TestFixture.Friend fixture project internal access, so analyze_change_impact's
// AccessibilityNarrow-to-internal verdict can be tested against [InternalsVisibleTo] (F12).
[assembly: InternalsVisibleTo("TestFixture.Friend")]
