namespace Zphil.Roz.Pipeline;

/// <summary>
///     Provides a hint message shown when a tool's response is truncated,
///     suggesting parameters the caller can use to narrow results.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NarrowingHintAttribute(string hint) : Attribute
{
    public string Hint { get; } = hint;
}
