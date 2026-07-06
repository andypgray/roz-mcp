namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Synchronous <see cref="IProgress{T}" /> for tests. Unlike <see cref="Progress{T}" />,
///     which posts callbacks via <c>SynchronizationContext</c> / thread pool and may deliver
///     them after the awaited work completes, this implementation invokes the handler
///     inline on the calling thread, eliminating callback-race flakes when assertions
///     read the recorded values immediately after the awaited call returns.
/// </summary>
internal sealed class RecordingProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
