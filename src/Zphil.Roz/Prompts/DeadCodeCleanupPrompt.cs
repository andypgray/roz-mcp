using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompts — user-invoked slash commands (<c>/mcp__roz__*</c>) that package a
///     multi-step Roslyn workflow into one recipe. Unlike tools, a prompt is invoked by the
///     user, not the model; its value is shipping a canned, semantically-aware workflow to
///     every consumer of the published tool. The returned string becomes a single user-role
///     message that the agent then executes against the server's tools.
/// </summary>
[McpServerPromptType]
internal sealed class DeadCodeCleanupPrompt
{
    /// <summary>
    ///     Emits an iterative dead-code-removal recipe scoped to <paramref name="scope" />,
    ///     gated by a public-API confirmation step governed by <paramref name="publicApiHandling" />.
    /// </summary>
    [McpServerPrompt(Name = "cleanup_dead_code", Title = "Clean up dead code")]
    [Description(
        "Find and remove genuinely-unused C# code in a scope, conservatively — checks for hidden uses "
        + "and confirms before touching any public API. Applies edits.")]
    public static string CleanupDeadCode(
        [Description(
            "What to scan: a type, namespace, file path, or project name "
            + "(e.g. 'ShapeService', 'TestFixture.Shapes', 'src/Foo.cs', or 'MyApp.Core').")]
        string scope,
        [Description(
            "How to treat externally-visible (public/protected) members: 'ask' (default) lists "
            + "them and waits for your confirmation before removing any; 'exclude' keeps the whole "
            + "public surface (library consumed externally); 'include' treats public members as "
            + "fair game (app with no external consumers).")]
        [AllowedValues("ask", "exclude", "include")]
        string publicApiHandling = "ask")
    {
        string publicApiGate = PromptFragments.GetPublicApiGate(publicApiHandling, "delete", "deleting");
        string verifyStep = PromptFragments.GetVerifyStep(
            "your deletion",
            "If something no longer compiles, that symbol was NOT dead — restore it and tell me what reached it.",
            "the step-1 baseline");

        return
            $"""
             Clean up dead code in `{scope}` using the roz-mcp tools — iteratively and conservatively.

             "Dead" means a declared symbol with **no live production use** — either zero real references, or
             references *only* from test projects (production-dead: it and the tests that solely cover it are
             both removable). Two tools, two blind spots — treat a symbol as dead only when BOTH agree it's
             unused:
             - `find_references` sees C#→C# uses that text search misses (interface dispatch,
               virtual/abstract overrides, DI registration), so "no text-search hits" is NOT proof of death.
             - But {PromptFragments.RazorBlindSpot} — a symbol it calls "unused" may still be live in markup.

             {PromptFragments.ToolPreflight()}

             Work this loop:

             1. **Enumerate** every declared member in the scope:
                - a type → `find_symbol` with `symbolNames=[<type>]` and `depth=1`
                - a file → `get_symbols_overview` with `filePaths=[<file>]`
                - a project → `get_symbols_overview` with `project=<name>`; {PromptFragments.NamespaceScopeHint}.
                  Process a large scope type-by-type so the batched calls below stay manageable.

                {PromptFragments.BaselineCapture}

             2. **Find references in one batched call.** Call `find_references` with `referenceKinds=all`,
                `includeTests=true`, and `symbolNames=[...]` listing every candidate at once — one call, not
                one per symbol. **Watch for truncation:** if the response reports it — a "showing N of M"
                note or a `--- RESPONSE TRUNCATED ---` footer — re-batch in smaller groups and re-run; a
                symbol's absence from a *truncated* response is NOT proof of zero references. Classify each
                by *where* its references live (the `[ProjectName]` tags):
                - **none** outside its own declaration → dead.
                - **only in test projects** → *test-only-reachable*: nothing in production uses it, so it and
                  the tests that exist solely to exercise it are dead weight (handled at step 4).
                - **any production reference** → live; drop it.
                Both 'dead' and 'test-only-reachable' candidates go through steps 3–4.

             3. **Rule out invisible reachability.** For each candidate, KEEP it (do not delete) if any of
                these hold:
                - **Used in Razor/markup** — `find_references` can't see `.razor`/`.cshtml` or inline
                  `@code`, so text-search the markup sources (`*.razor`, `*.cshtml`, …) for the name.
                  If it appears, keep it. Do this for EVERY candidate — it's the workspace's biggest
                  blind spot and the easiest way to delete live code by mistake.
                - **DI-registered** — `find_references` annotates registrations it detects across the
                  supported containers; for a type or a `.ctor`, DI detection is the fallback used when
                  there are no direct callers. A registered symbol is live.
                - **Implements an interface / overrides a base member** — call `find_implementations`;
                  such members are reached by dispatch, not by a direct call.
                - **Framework or reflection entry point** — `Main`, event handlers, `[Fact]`/`[Test]`
                  methods, serialization/model targets, source-generated or reflection-driven usage.
                  When in doubt, keep it.

             4. {publicApiGate}

                **Test-only-reachable code — confirm the pair.** A candidate whose only references are in
                test projects isn't truly used — nothing in production reaches it. It must clear step 3 first:
                production may reach it via DI / dispatch / reflection / markup, which `find_references` can't
                see and which keeps it live regardless of tests. For the survivors, pair each prod symbol with
                the test(s) that exist solely to cover it — both are dead weight — and **ask me which pairs
                to remove**: {PromptFragments.AsMultipleChoice(true)}, one option per prod+test pair. A test
                that also exercises live code is NOT dead; flag it for manual trimming
                rather than deleting it. These pairs remain subject to the public-API gate above: when the
                pair's production symbol is externally visible, the gate's handling wins — under `exclude` it
                stays, full stop; under `ask` it needs my tick like any other public member (being
                test-only-reachable doesn't exempt public surface).

             5. **Remove.** Delete the confirmed-dead symbols with `edit_symbol` `action=Remove`, batching
                the ops into one call. Remove members before the types that contain them; for an approved
                test-only pair, delete the production symbol and its exclusive test(s) together. If
                `edit_symbol` isn't loaded on this server, fall back to your normal file-editing tools — use
                the locations from steps 1–2 to delete each declaration directly.

             6. **Verify.** {verifyStep}

             7. **Loop.** Removing a member can orphan others that only it referenced — including test
                fixtures or helpers once their last test is gone. Re-run steps 1–6 until a full pass finds
                nothing new.

             Finish with a summary: what you removed (flag any production+test pairs), and what you kept and
             why (markup, DI, dispatch, public API, reflection). Start with step 1.
             """;
    }
}
