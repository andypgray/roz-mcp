namespace Zphil.Roz.Enums;

/// <summary>
///     Controls the amount of detail in tool responses during progressive detail reduction.
///     When output exceeds the character limit, rendering is retried at progressively lower
///     detail levels until it fits.
/// </summary>
internal enum DetailLevel
{
    /// <summary>Full detail: body + docs + members at requested depth.</summary>
    Full = 0,

    /// <summary>High detail: most content retained, largest items (e.g. source bodies) removed.</summary>
    High = 1,

    /// <summary>Medium detail: secondary content (e.g. documentation) also removed.</summary>
    Medium = 2,

    /// <summary>Low detail: compact summary — signatures, locations, or grouped counts only.</summary>
    Low = 3,

    /// <summary>Minimal detail: names and locations only, most compact representation.</summary>
    Minimal = 4
}
