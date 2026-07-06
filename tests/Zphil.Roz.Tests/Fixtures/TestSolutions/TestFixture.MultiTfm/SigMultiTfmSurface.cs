namespace TestFixture.MultiTfm;

/// <summary>
///     Self-contained multi-TFM surface (this project targets net8.0;net10.0 and does not reference
///     TestFixture) for the precise-signature TFM-dedup test: a call site compiled under both TFMs must
///     be classified once, and the fork must rewrite the declaration in every DocumentId.
/// </summary>
public class SigMultiTfmSurface
{
    /// <summary>Scales a value.</summary>
    /// <param name="value">The value to scale.</param>
    public int Scale(int value) => value * 2;
}

/// <summary>Consumer of the multi-TFM Scale surface (no cref — keeps the census to the single call).</summary>
public class SigMultiTfmConsumer
{
    private readonly SigMultiTfmSurface surface = new();

    public int UseScale() => surface.Scale(21);
}
