using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Registers the MCP client logging provider after the host starts,
///     forwarding log messages to the MCP client as notifications/message.
/// </summary>
internal sealed class McpLoggingHostedService(
    McpServer server,
    ILoggerFactory loggerFactory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ILoggerProvider mcpProvider = server.AsClientLoggerProvider();
        loggerFactory.AddProvider(mcpProvider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
