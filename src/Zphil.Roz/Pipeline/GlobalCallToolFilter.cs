using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Pipeline;

internal static class GlobalCallToolFilter
{
    // SDK argument-marshalling stacks observed so far nest two to three exceptions deep
    // (JsonException → JsonException → inner); 8 is loose headroom against pathological loops.
    private const int MaxExceptionChainDepth = 8;

    /// <summary>
    ///     Catch unhandled exceptions from tool calls (returning them as <see cref="CallToolResult.IsError" />) and truncate
    ///     oversized text responses.
    /// </summary>
    public static IMcpServerBuilder WithGlobalCallToolFilter(this IMcpServerBuilder builder)
    {
        return builder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                // Idle-timeout watchdog: stamp on entry and (in finally) on exit so "idle" is
                // measured from when the user last got a response. The in-flight count this
                // bumps stops a long first call (cold solution load) from self-killing.
                IdleTimeoutWatchdog.EnterCall();
                try
                {
                    CallToolResult result;
                    try
                    {
                        WorkspaceManager? workspaceManager = context.Server.Services?.GetService<WorkspaceManager>();
                        if (workspaceManager is not null)
                        {
                            try
                            {
                                await workspaceManager.ReconcileAllExternalEditsAsync(cancellationToken);
                            }
                            catch (Exception reconcileEx) when (reconcileEx is not OperationCanceledException)
                            {
                                // External-edit reconcile is best-effort: a sweep failure must not crash a tool call.
                                context.Server.Services?.GetService<ILoggerFactory>()
                                    ?.CreateLogger(typeof(GlobalCallToolFilter))
                                    .LogWarning(reconcileEx, "External-edit reconcile failed before tool '{ToolName}'", context.Params.Name);
                            }
                        }

                        if (UnknownParameterGuard.Validate(context.Params.Name, context.Params.Arguments) is { } unknownParamError)
                        {
                            throw new UserErrorException(unknownParamError);
                        }

                        BatchRequestArgumentNormalizer.Normalize(context.Params.Name, context.Params.Arguments);

                        result = await next(context, cancellationToken);
                    }
                    catch (UserErrorException ex)
                    {
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = ex.Message }],
                            IsError = true
                        };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The SDK's argument-marshalling layer can wrap a converter-thrown
                        // UserErrorException an arbitrary depth deep (JsonException→JsonException→…
                        // for enums inside record-array parameters). Walk the chain so the
                        // valid-value list from EnumValidationConverterFactory still surfaces.
                        if (FindUserError(ex) is { } wrapped)
                        {
                            return new CallToolResult
                            {
                                Content = [new TextContentBlock { Text = wrapped.Message }],
                                IsError = true
                            };
                        }

                        context.Server.Services?.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(GlobalCallToolFilter))
                            .LogWarning(ex, "Tool '{ToolName}' failed", context.Params.Name);

                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = ex.Message }],
                            IsError = true
                        };
                    }

                    if (result.IsError is not true)
                    {
                        string toolName = context.Params.Name;
                        foreach (ContentBlock contentBlock in result.Content)
                        {
                            if (contentBlock is TextContentBlock textBlock)
                            {
                                textBlock.Text = ResponseTruncator.TruncateIfNeeded(textBlock.Text, toolName);
                            }
                        }
                    }

                    return result;
                }
                finally
                {
                    IdleTimeoutWatchdog.ExitCall();
                }
            });
        });
    }

    private static UserErrorException? FindUserError(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < MaxExceptionChainDepth; depth++)
        {
            if (ex is UserErrorException user)
            {
                return user;
            }

            ex = ex.InnerException;
        }

        return null;
    }
}
