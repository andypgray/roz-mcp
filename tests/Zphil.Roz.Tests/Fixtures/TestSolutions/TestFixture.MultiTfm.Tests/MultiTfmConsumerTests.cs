using TestFixture.MultiTfm;
using Xunit;

namespace TestFixture.MultiTfm.Tests;

public class MultiTfmConsumerTests
{
    [Fact]
    public void TestReferenceTarget_InvokesFromTestProject()
    {
        var consumer = new MultiTfmConsumer();
        consumer.TestReferenceTarget();
    }
}
