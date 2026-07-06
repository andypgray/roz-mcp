using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Services;

/// <summary>
///     Maps diagnostic IDs to the <see cref="CodeFixProvider" /> that fixes them, harvested
///     from the workspace's <see cref="Project.AnalyzerReferences" />. Used by
///     <see cref="DiagnosticService" /> to annotate <c>get_diagnostics</c> output with the
///     bulk-fix path so the model can prefer <c>dotnet format analyzers</c> over per-site edits.
/// </summary>
/// <remarks>
///     Discovery is lazy and cached for the workspace's lifetime. Reload invalidates the cache
///     via <see cref="WorkspaceManager.RegisterBeforeReload" /> so a NuGet restore that adds a
///     new analyzer pack is picked up.
/// </remarks>
internal sealed class FixerCatalog : IDisposable
{
    private readonly IDisposable beforeReloadSubscription;
    private readonly Lock fieldLock = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<FixerCatalog> logger;
    private readonly WorkspaceManager workspaceManager;
    private IReadOnlyDictionary<string, FixerInfo>? cache;

    public FixerCatalog(WorkspaceManager workspaceManager, ILogger<FixerCatalog> logger)
    {
        this.workspaceManager = workspaceManager;
        this.logger = logger;
        beforeReloadSubscription = workspaceManager.RegisterBeforeReload(InvalidateCache);
    }

    public void Dispose()
    {
        beforeReloadSubscription.Dispose();
        gate.Dispose();
    }

    /// <summary>
    ///     Returns a map of diagnostic ID → fixer info for every <see cref="CodeFixProvider" />
    ///     declared on a <see cref="AnalyzerFileReference" /> in any project.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FixerInfo>> GetAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, FixerInfo>? snapshot;
        lock (fieldLock)
        {
            snapshot = cache;
        }

        if (snapshot is not null)
        {
            return snapshot;
        }

        await gate.WaitAsync(ct);
        try
        {
            lock (fieldLock)
            {
                snapshot = cache;
            }

            if (snapshot is not null)
            {
                return snapshot;
            }

            Solution solution = await workspaceManager.GetSolutionAsync(ct);
            // Discovery is reflection-heavy and CPU-bound — offload from the caller's thread.
            IReadOnlyDictionary<string, FixerInfo> built = await Task.Run(() => Discover(solution, ct), ct);

            lock (fieldLock)
            {
                cache = built;
            }

            return built;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Returns the live <see cref="CodeFixProvider" /> registered for <paramref name="diagnosticId" />,
    ///     or <c>null</c> when no analyzer pack in the solution ships a fixer for it. Backs
    ///     <c>apply_code_fix</c>, which needs the provider instance to run FixAll.
    /// </summary>
    public async Task<CodeFixProvider?> GetProviderAsync(string diagnosticId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, FixerInfo> map = await GetAsync(ct);
        return map.TryGetValue(diagnosticId, out FixerInfo? info) ? info.Provider : null;
    }

    private void InvalidateCache()
    {
        lock (fieldLock)
        {
            cache = null;
        }
    }

    private IReadOnlyDictionary<string, FixerInfo> Discover(Solution solution, CancellationToken ct)
    {
        IEnumerable<AnalyzerFileReference> refs = solution.Projects
            .SelectMany(p => p.AnalyzerReferences)
            .OfType<AnalyzerFileReference>()
            .DistinctBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, FixerInfo> result = new(StringComparer.Ordinal);
        foreach (AnalyzerFileReference aref in refs)
        {
            ct.ThrowIfCancellationRequested();
            HarvestFromAssembly(aref, result);
        }

        logger.LogInformation("Fixer catalog discovered {Count} fixable diagnostic IDs", result.Count);
        return result;
    }

    private void HarvestFromAssembly(AnalyzerFileReference aref, Dictionary<string, FixerInfo> result)
    {
        // Reuse the Assembly instance Roslyn already loaded for analyzer execution rather
        // than calling Assembly.LoadFrom — a parallel load would put a second copy of the
        // same DLL in the LoadFrom context and risk splitting CodeFixProvider type identity.
        Assembly assembly;
        try
        {
            assembly = aref.GetAssembly();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load analyzer assembly {Path}", aref.FullPath);
            return;
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types may fail to load (e.g. due to missing optional dependencies); the
            // ones that did load are still usable.
            types = ex.Types;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enumerate types in {Path}", aref.FullPath);
            return;
        }

        foreach (Type? type in types)
        {
            if (type is null || type.IsAbstract)
            {
                continue;
            }

            if (!typeof(CodeFixProvider).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is null)
            {
                continue;
            }

            CodeFixProvider? provider;
            try
            {
                provider = Activator.CreateInstance(type) as CodeFixProvider;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to instantiate CodeFixProvider {Type}", type.FullName);
                continue;
            }

            if (provider is null)
            {
                continue;
            }

            string providerName = type.FullName ?? type.Name;
            foreach (string id in provider.FixableDiagnosticIds)
            {
                result.TryAdd(id, new FixerInfo(id, providerName, provider));
            }
        }
    }
}

/// <summary>
///     Metadata about a single fixable diagnostic discovered via reflection.
/// </summary>
/// <param name="DiagnosticId">The diagnostic ID this fixer handles (e.g. <c>xUnit1051</c>).</param>
/// <param name="ProviderTypeName">Fully-qualified name of the <see cref="CodeFixProvider" /> type.</param>
/// <param name="Provider">
///     The live provider instance. <see cref="CodeFixProvider" />s are stateless singletons (VS treats
///     them as MEF singletons), so caching one for the workspace lifetime is safe and lets
///     <c>apply_code_fix</c> run FixAll without re-reflecting.
/// </param>
internal sealed record FixerInfo(string DiagnosticId, string ProviderTypeName, CodeFixProvider Provider);
