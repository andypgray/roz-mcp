namespace Zphil.Roz.Models;

/// <summary>One element of a read-only batch: either a successful result or a captured error.</summary>
// ReSharper disable once UnusedTypeParameter — T binds the discriminated union variants to a common result type
internal abstract record BatchItem<T>(string Name);

internal sealed record BatchItemSuccess<T>(string Name, T Value) : BatchItem<T>(Name);

internal sealed record BatchItemError<T>(string Name, string Error) : BatchItem<T>(Name);
