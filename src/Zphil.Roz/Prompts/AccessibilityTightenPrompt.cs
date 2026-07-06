using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that narrows over-broad accessibility — members declared more visibly than their
///     usage requires (public/protected used only in-assembly → internal; anything used only within
///     its declaring type → private). Each narrowing is verified safe per-site via
///     <c>analyze_change_impact</c> before it's applied, behind the same public-API gate as
///     <c>cleanup_dead_code</c>.
/// </summary>
[McpServerPromptType]
internal sealed class AccessibilityTightenPrompt
{
    /// <summary>
    ///     Emits an iterative accessibility-narrowing recipe scoped to <paramref name="scope" />,
    ///     gated by a public-API confirmation step governed by <paramref name="publicApiHandling" />.
    /// </summary>
    [McpServerPrompt(Name = "tighten_accessibility", Title = "Tighten accessibility")]
    [Description(
        "Narrow over-broad C# accessibility (e.g. a public member only used internally) where it's "
        + "provably safe, confirming before touching any public API. Applies edits.")]
    public static string TightenAccessibility(
        [Description(
            "What to scan: a type, namespace, file path, or project name "
            + "(e.g. 'ShapeService', 'TestFixture.Shapes', 'src/Foo.cs', or 'MyApp.Core').")]
        string scope,
        [Description(
            "How to treat externally-visible (public/protected) members: 'ask' (default) lists the "
            + "public→internal narrowings and waits for confirmation; 'exclude' keeps the whole public "
            + "surface untouched (library consumed externally); 'include' narrows public members freely "
            + "(app with no external consumers).")]
        [AllowedValues("ask", "exclude", "include")]
        string publicApiHandling = "ask")
    {
        string publicApiGate = PromptFragments.GetPublicApiGate(publicApiHandling, "narrow", "narrowing");
        string verifyStep = PromptFragments.GetVerifyStep(
            "the narrowing",
            "If something no longer compiles, that narrowing wasn't safe — restore the wider modifier and tell me what needed it.",
            "the step-1 baseline");

        return
            $"""
             Tighten over-broad accessibility in `{scope}` using the roz-mcp tools — narrow members
             declared more visibly than their usage requires, provably safely. Two narrowings:
             `public`/`protected` → `internal` when nothing outside the declaring assembly uses it, and any
             member → `private` when only its declaring type uses it.

             {PromptFragments.ToolPreflight()}

             Work this loop:

             1. **Enumerate candidates.** List members in the scope (`get_symbols_overview` for a file or a
                project; {PromptFragments.NamespaceScopeHint}; `find_symbol depth=1` for a type) and read
                each member's accessibility from its
                `[public …]` / `[protected …]` / `[internal …]` tag. Skip surface that is broad by design:
                program entry points, `[ApiController]` controllers and their actions, `[Fact]`/`[Theory]`/
                test methods, Blazor components (`ComponentBase` subclasses / `.razor` code-behind),
                serialization/binding DTOs, and members that implement an interface or override a base member
                (their accessibility is constrained). {PromptFragments.BaselineCapture}

             2. **Measure usage breadth.** In one batched `find_references` (`referenceKinds=all`, `includeTests=true`)
                over the candidates, read each one's per-`[ProjectName]` distribution:
                - referenced only within its **declaring type** → candidate for `private`
                - referenced within its **declaring assembly** but beyond its type → candidate for `internal`
                - referenced from another **production** assembly → leave as-is; it earns its visibility
                - referenced from another assembly but **only a test project** (every non-test use is
                  in-assembly) → narrowable to `internal` *only* if the declaring project grants that test
                  assembly `InternalsVisibleTo`; flag it for the IVT suggestion in step 4 (test projects:
                  by name like `*.Tests`, or the project type from `get_workspace_info`).

             3. **Cover the Razor blind spot — this is where tightening bites.** {PromptFragments.RazorBlindSpot}.
                A `public` component or helper used only from `.razor`/`.cshtml` (often in another project)
                looks in-assembly-only — or unused — here, so narrowing it would break the markup at build
                time. Text-search `*.razor`/`*.cshtml` for each candidate; if it appears outside the declaring
                assembly, do NOT narrow it. Component types are the classic trap.

             4. {publicApiGate}

                **InternalsVisibleTo.** For candidates pinned `public` *only* by a test project (step 2),
                each becomes `internal` if its declaring project grants the test assembly access. Group them
                by declaring project and **suggest** the one-line grant — an
                `<InternalsVisibleTo Include="<TestAssembly>" />` item in the SDK-style `.csproj` (or
                `[assembly: InternalsVisibleTo("<TestAssembly>")]`) — skipping any project that already has
                it. Propose these alongside the narrowings and ask me which grants to add —
                {PromptFragments.AsMultipleChoice(true)}, one option per proposed `InternalsVisibleTo` grant;
                add only what I tick, never silently. Do NOT IVT-and-narrow a framework convention like
                `public partial class Program` (the `WebApplicationFactory<Program>` host) — that's public by
                design; leave it.

             5. **Verify each narrowing is safe.** For each surviving candidate, run `analyze_change_impact`
                `changeKind=AccessibilityNarrow` `newAccessibility=<internal|private>`; narrow it only if
                every site is `Compatible` (no `Unsafe`). Mind the rules the tool enforces: the new level must
                be strictly narrower, and a top-level type can only go to `internal`. One case it can't see:
                `Compatible` checks reference *access*, not member *exposure* — if the candidate is the **type**
                of a more-accessible member (a component `[Parameter]`, or a public property / return /
                parameter / base type / generic constraint), it's pinned to that member's accessibility and
                narrowing it is a `CS0053` "inconsistent accessibility" break. Skip those up front (the build
                is the backstop, but don't spend the cycle).

             6. **Apply.** First add any `InternalsVisibleTo` grants I approved (the
                `<InternalsVisibleTo Include="…" />` item on the declaring project), then flip the modifiers
                with `edit_symbol` `action=Replace` (supply the full declaration with the new modifier — doc
                comments are preserved automatically), or `replace_content` for a single-keyword swap. Batch
                the edits. If the editing tools aren't loaded on this server, fall back to your normal
                file-editing tools.

             7. **Verify.** {verifyStep}

             8. **Loop.** Narrowing one member can unlock another (a type whose last public member just became
                internal may itself be narrowable). Re-run steps 1–7 until a pass changes nothing. Then
                summarize: what you narrowed and to what, and what you left and why (cross-assembly use,
                markup, the public-API gate, by-design surface).

             Start with step 1.
             """;
    }
}
