using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

/// <summary>
///     Tests for <see cref="XmlDocFormatter" /> symbol-based documentation resolution,
///     covering property overrides, event overrides, explicit interface implementations,
///     and max inheritdoc depth.
/// </summary>
public class XmlDocFormatterSymbolTests
{
    private static CSharpCompilation CreateCompilation(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // ── Property overrides ───────────────────────────────────────────────

    [Fact]
    public void Format_PropertyOverride_ResolvesBaseClassDocs()
    {
        // Arrange
        var source = """
                     using System;

                     public abstract class Animal
                     {
                         /// <summary>Gets the name of the animal.</summary>
                         public abstract string Name { get; }
                     }

                     public class Dog : Animal
                     {
                         /// <inheritdoc/>
                         public override string Name => "Dog";
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol dogType = compilation.GetTypeByMetadataName("Dog")!;
        IPropertySymbol nameProperty = dogType.GetMembers("Name").OfType<IPropertySymbol>().Single();

        // Act
        string? result = XmlDocFormatter.Format(nameProperty);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Gets the name of the animal.");
    }

    [Fact]
    public void Format_PropertyExplicitInterfaceImpl_ResolvesInterfaceDocs()
    {
        // Arrange
        var source = """
                     using System;

                     public interface ILabeled
                     {
                         /// <summary>Gets the label text.</summary>
                         string Label { get; }
                     }

                     public class Widget : ILabeled
                     {
                         /// <inheritdoc/>
                         string ILabeled.Label => "widget";
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol widgetType = compilation.GetTypeByMetadataName("Widget")!;
        IPropertySymbol labelProperty = widgetType.GetMembers()
            .OfType<IPropertySymbol>()
            .Single(p => p.Name == "ILabeled.Label");

        // Act
        string? result = XmlDocFormatter.Format(labelProperty);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Gets the label text.");
    }

    [Fact]
    public void Format_PropertyImplicitInterfaceImpl_ResolvesInterfaceDocs()
    {
        // Arrange
        var source = """
                     using System;

                     public interface INamed
                     {
                         /// <summary>Gets the display name.</summary>
                         string DisplayName { get; }
                     }

                     public class Item : INamed
                     {
                         /// <inheritdoc/>
                         public string DisplayName => "item";
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol itemType = compilation.GetTypeByMetadataName("Item")!;
        IPropertySymbol displayNameProperty = itemType.GetMembers("DisplayName")
            .OfType<IPropertySymbol>().Single();

        // Act
        string? result = XmlDocFormatter.Format(displayNameProperty);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Gets the display name.");
    }

    // ── Event overrides ──────────────────────────────────────────────────

    [Fact]
    public void Format_EventOverride_ResolvesBaseClassDocs()
    {
        // Arrange
        var source = """
                     using System;

                     public abstract class BaseNotifier
                     {
                         /// <summary>Raised when the state changes.</summary>
                         public virtual event EventHandler? StateChanged;

                         protected void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
                     }

                     public class ConcreteNotifier : BaseNotifier
                     {
                         /// <inheritdoc/>
                         public override event EventHandler? StateChanged;
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol concreteType = compilation.GetTypeByMetadataName("ConcreteNotifier")!;
        IEventSymbol stateChangedEvent = concreteType.GetMembers("StateChanged")
            .OfType<IEventSymbol>().Single();

        // Act
        string? result = XmlDocFormatter.Format(stateChangedEvent);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Raised when the state changes.");
    }

    [Fact]
    public void Format_EventExplicitInterfaceImpl_ResolvesInterfaceDocs()
    {
        // Arrange
        var source = """
                     using System;

                     public interface IObservable
                     {
                         /// <summary>Raised when data is updated.</summary>
                         event EventHandler? DataUpdated;
                     }

                     public class DataSource : IObservable
                     {
                         /// <inheritdoc/>
                         event EventHandler? IObservable.DataUpdated { add { } remove { } }
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol dataSourceType = compilation.GetTypeByMetadataName("DataSource")!;
        IEventSymbol dataUpdatedEvent = dataSourceType.GetMembers()
            .OfType<IEventSymbol>()
            .Single(e => e.Name == "IObservable.DataUpdated");

        // Act
        string? result = XmlDocFormatter.Format(dataUpdatedEvent);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Raised when data is updated.");
    }

    [Fact]
    public void Format_EventImplicitInterfaceImpl_ResolvesInterfaceDocs()
    {
        // Arrange
        var source = """
                     using System;

                     public interface INotifier
                     {
                         /// <summary>Raised before reset occurs.</summary>
                         event EventHandler? Resetting;
                     }

                     public class Manager : INotifier
                     {
                         /// <inheritdoc/>
                         public event EventHandler? Resetting;
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol managerType = compilation.GetTypeByMetadataName("Manager")!;
        IEventSymbol resettingEvent = managerType.GetMembers("Resetting")
            .OfType<IEventSymbol>().Single();

        // Act
        string? result = XmlDocFormatter.Format(resettingEvent);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Raised before reset occurs.");
    }

    // ── Max inheritdoc depth ─────────────────────────────────────────────

    [Fact]
    public void Format_ExceedsMaxInheritDocDepth_ReturnsNull()
    {
        // Arrange — chain of 12 classes, each with <inheritdoc/>, exceeding the limit of 10
        var source = """
                     using System;

                     public abstract class Level0
                     {
                         /// <inheritdoc/>
                         public abstract void Do();
                     }

                     public abstract class Level1 : Level0
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level2 : Level1
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level3 : Level2
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level4 : Level3
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level5 : Level4
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level6 : Level5
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level7 : Level6
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level8 : Level7
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level9 : Level8
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public abstract class Level10 : Level9
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }

                     public class Level11 : Level10
                     {
                         /// <inheritdoc/>
                         public override void Do() { }
                     }
                     """;
        CSharpCompilation compilation = CreateCompilation(source);
        INamedTypeSymbol level11Type = compilation.GetTypeByMetadataName("Level11")!;
        IMethodSymbol doMethod = level11Type.GetMembers("Do").OfType<IMethodSymbol>().Single();

        // Act
        string? result = XmlDocFormatter.Format(doMethod);

        // Assert
        result.ShouldBeNull();
    }
}
