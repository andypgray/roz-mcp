using System.CommandLine;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Services;
using Zphil.Roz.Setup;
using Zphil.Roz.Symbols;

const string SetupCommandName = "setup";

Option<string?> toolsOption = new("--tools")
{
    Description = "Tool subset (preset, category key, or comma/semicolon-separated tool names; prefix with - to exclude)."
};
Option<string?> clientOption = new("--client")
{
    Description = "Client(s) to configure: claude, cursor, vscode, codex, or 'all'. Comma-separate for multiple."
};
Option<bool> pluginOption = new("--plugin")
{
    Description = "Force plugin-mode Claude Code setup (the roz-mcp plugin provides the server: no .mcp.json entry, plugin-prefixed permission rules)."
};
Option<bool> noPluginOption = new("--no-plugin")
{
    Description = "Force classic Claude Code setup (.mcp.json entry) even when the roz-mcp plugin appears enabled."
};

Command setupCommand = new(SetupCommandName, "Configure roz-mcp for a project (Claude Code, Cursor, VS Code Copilot Chat, or Codex CLI).")
{
    toolsOption,
    clientOption,
    pluginOption,
    noPluginOption
};
setupCommand.SetAction((parseResult, _) =>
    SetupCommand.RunAsync(
        parseResult.GetValue(toolsOption),
        parseResult.GetValue(clientOption),
        parseResult.GetValue(pluginOption),
        parseResult.GetValue(noPluginOption)));

RootCommand rootCommand = new("Roslyn-powered MCP server for C# semantic code navigation and editing.")
{
    setupCommand
};
rootCommand.SetAction((parseResult, _) => RunRootAsync(parseResult, args));

return await rootCommand.Parse(args).InvokeAsync();

static Task RunRootAsync(ParseResult parseResult, string[] args)
{
    if (!Console.IsInputRedirected)
    {
        parseResult.InvocationConfiguration.Output.WriteLine(
            "roz-mcp is an MCP stdio server. Run 'roz-mcp setup' to configure a project,");
        parseResult.InvocationConfiguration.Output.WriteLine(
            "or 'roz-mcp --help' for usage.");
        return Task.CompletedTask;
    }

    return StartMcpServerAsync(args);
}

// NoInlining prevents the JIT from loading Roslyn workspace types before MsBuildBootstrap.Initialize().
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task StartMcpServerAsync(string[] args)
{
    // Step 0, before logger init: ROZ_LOG_LEVEL / ROZ_SESSION_ID seeded from .roz.json must be
    // in the environment when InitializeFileLogger reads them. The seeder itself never logs.
    ProjectConfigSeeder.Seed();
    SerilogConfiguration.InitializeFileLogger();
    SerilogConfiguration.RegisterCrashHandlers();
    LogProjectConfigSeed();
    ParentProcessWatcher.Start();
    IdleTimeoutWatchdog.Start();

    MsBuildBootstrap.Initialize();
    await RunServerAsync(args);
}

// The seeder runs before Serilog exists, so its outcome is logged here, right after logger init.
static void LogProjectConfigSeed()
{
    ProjectConfigSeedResult? seed = ProjectConfigSeeder.Current;
    if (seed is null)
    {
        return;
    }

    if (seed.ConfigFilePath is not null)
    {
        // includeWarnings: false — the loop below logs each warning at Warning level (the only
        // visible signal under the default ROZ_LOG_LEVEL); repeating them here would double-log.
        // ReSharper disable ArgumentsStyleLiteral — keep the bool args self-documenting.
        Log.Information("Project config {ConfigFilePath}: {Summary}",
            seed.ConfigFilePath, seed.Summary(withValues: true, includeWarnings: false));
        // ReSharper restore ArgumentsStyleLiteral
    }

    foreach (string warning in seed.Warnings)
    {
        Log.Warning("Project config: {ConfigWarning}", warning);
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task RunServerAsync(string[] args)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.AddSerilogLogging();

    builder.Services.AddSingleton<WorkspaceManager>();
    builder.Services.AddSingleton<DiagnosticBaselineManager>();
    builder.Services.AddSingleton<FixerCatalog>();
    builder.Services.AddSingleton<EditSymbolResolver>();
    builder.Services.AddSingleton<EditVerificationService>();
    builder.Services.AddSingleton<SymbolEditService>();
    builder.Services.AddSingleton<RenameService>();
    builder.Services.AddSingleton<TextReplacementService>();
    builder.Services.AddSingleton<CodeFixService>();
    builder.Services.AddSingleton<ChangeSignatureService>();
    builder.Services.AddSingleton<DiagnosticService>();
    builder.Services.AddSingleton<DiRegistrationScanner>();
    builder.Services.AddSingleton<UsingDirectiveService>();
    builder.Services.AddSingleton<NavigationService>();
    builder.Services.AddSingleton<SymbolResolver>();
    builder.Services.AddSingleton<ReferenceService>();
    builder.Services.AddSingleton<MethodAnalysisService>();
    builder.Services.AddSingleton<ImpactAnalysisService>();
    builder.Services.AddSingleton<TypeHierarchyService>();
    builder.Services.AddSingleton<WorkspaceService>();
    builder.Services.AddSingleton<UnusedReferenceService>();
    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInstructions = ServerInstructions.Text;
        })
        .WithStdioServerTransport()
        .WithTrimmedTools(ToolSelector.GetEnabledTools())
        .WithPromptsFromAssembly()
        .WithResourcesFromAssembly()
        .WithEditSerializationFilter()
        .WithGlobalCallToolFilter();

    builder.Services.AddHostedService<McpLoggingHostedService>();

    IHost host = builder.Build();

    // Disposer is resolved lazily inside the lambda — not at registration time. Eager
    // resolution would instantiate WorkspaceManager (and start its background load) before
    // host startup, in front of MSBuild's expected lifecycle. The lambda also keeps
    // ServerShutdown ignorant of WorkspaceManager.
    ServerShutdown.RegisterDisposer(() =>
        host.Services.GetRequiredService<WorkspaceManager>().DisposeAsync());

    await host.RunAsync();
}
