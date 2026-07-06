using TestFixture.Services;

namespace TestFixture.Tests;

/// <summary>
///     Cross-assembly consumer of <see cref="SignatureChangeSurface.Greet" />, so precise-signature
///     tests can confirm second-assembly call sites are classified (tests using it must pass
///     <c>includeTests: true</c> — TestFixture.Tests is a test project).
/// </summary>
public class SignatureCrossAssemblyConsumer
{
    public string Use() => new SignatureChangeSurface().Greet("cross");

    // A second-assembly call site that a remove-unused change_signature must rewrite, proving the
    // apply-gate census includes test projects (excludeTests=false).
    public int UseTrim() => new SignatureChangeSurface().Trim("x", 9);
}
