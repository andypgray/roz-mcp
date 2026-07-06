using System.ComponentModel;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Drift-guards for the enum value lists published to clients. <see cref="SchemaTrimmer" />
///     removes the JSON-schema <c>enum</c> array, so the allowed values must be discoverable in
///     text: enums on default-preset tools list their values in <c>server-instructions.md</c>
///     (shipped with every session); enums used only by non-default tools (<c>edit_symbol</c>'s
///     <c>action</c>/<c>position</c>, <c>get_unused_references</c>' <c>dependencyKind</c>) inline
///     them in the parameter description instead, so sessions without those tools don't pay for
///     them. A mismatch between the C# enum and the published text is silently user-visible —
///     callers see the text but pass values the enum can't parse.
/// </summary>
public class ToolDescriptionsTests
{
    [Fact]
    public void ServerInstructions_ListEverySymbolicKindMember()
    {
        foreach (string name in Enum.GetNames<SymbolicKind>())
        {
            ServerInstructions.Text.ShouldContain(name);
        }
    }

    [Fact]
    public void ServerInstructions_ListEverySymbolMatchModeMember()
    {
        foreach (string name in Enum.GetNames<SymbolMatchMode>())
        {
            ServerInstructions.Text.ShouldContain(name);
        }
    }

    [Fact]
    public void ServerInstructions_ListEveryDiagnosticSeverityMember()
    {
        // Microsoft.CodeAnalysis.DiagnosticSeverity — the enum used by get_diagnostics.
        foreach (string name in Enum.GetNames<DiagnosticSeverity>())
        {
            ServerInstructions.Text.ShouldContain(name);
        }
    }

    [Fact]
    public void EditSymbolRequest_PositionDescription_ListsEveryInsertPositionMember()
    {
        string description = GetPropertyDescription<EditSymbolRequest>(nameof(EditSymbolRequest.Position));
        foreach (string name in Enum.GetNames<InsertPosition>())
        {
            description.ShouldContain(name);
        }
    }

    [Fact]
    public void EditSymbolRequest_ActionDescription_ListsEveryEditSymbolActionMember()
    {
        string description = GetPropertyDescription<EditSymbolRequest>(nameof(EditSymbolRequest.Action));
        foreach (string name in Enum.GetNames<EditSymbolAction>())
        {
            description.ShouldContain(name);
        }
    }

    [Fact]
    public void GetUnusedReferences_DependencyKindDescription_ListsEveryUnusedReferencesKindMember()
    {
        ParameterInfo dependencyKind = typeof(WorkspaceTools)
            .GetMethod(nameof(WorkspaceTools.GetUnusedReferences))!
            .GetParameters()
            .Single(p => p.Name == "dependencyKind");
        string description = dependencyKind.GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (string name in Enum.GetNames<UnusedReferencesKind>())
        {
            description.ShouldContain(name);
        }
    }

    private static string GetPropertyDescription<T>(string propertyName)
    {
        PropertyInfo property = typeof(T).GetProperty(propertyName)!;
        return property.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }

    [Fact]
    public void ServerInstructions_ListEveryChangeKindMember()
    {
        foreach (string name in Enum.GetNames<ChangeKind>())
        {
            ServerInstructions.Text.ShouldContain(name);
        }
    }

    [Fact]
    public void ServerInstructions_ListEveryAccessibilityLevelMember()
    {
        foreach (string name in Enum.GetNames<AccessibilityLevel>())
        {
            ServerInstructions.Text.ShouldContain(name);
        }
    }
}
