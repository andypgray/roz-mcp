using System;

namespace TestFixture.Services;

/// <summary>
///     Dedicated surface for precise <c>SignatureChange</c> (analyze_change_impact <c>newSignature</c>)
///     and <c>change_signature</c> tests. Every method has isolated consumers so per-site verdicts do
///     not cross-talk. No member here is referenced by any other fixture, so reference counts elsewhere
///     stay pinned.
/// </summary>
public class SignatureChangeSurface
{
    /// <summary>Greets by name.</summary>
    /// <param name="name">The name to greet.</param>
    public string Greet(string name) => $"Hi {name}";

    /// <summary>Two same-type parameters — the reorder trap.</summary>
    public int Move(int x, int y) => x - y;

    /// <summary>A trailing optional parameter — one caller omits it, one passes it.</summary>
    public int Log(string msg, int level = 0) => msg.Length + level;

    /// <summary>A params tail — expanded and array-form callers.</summary>
    public int Sum(params int[] xs) => xs.Length;

    /// <summary>Overload targeted (by cursor) for the retarget probe.</summary>
    public int Handle(int value) => value;

    /// <summary>Sibling overload that catches a retargeted call.</summary>
    public long Handle(long value) => value;

    /// <summary>Referenced as a method group and via nameof — never invoked directly.</summary>
    public void Ping(int n)
    {
    }

    /// <summary>Single overload called with a long argument — for retype (cast / no-conversion) tests.</summary>
    public long Widen(long value) => value;

    /// <summary>Trailing optional unused in the body — cross-assembly remove-unused apply test.</summary>
    /// <param name="text">The text to measure.</param>
    /// <param name="unused">An unused knob.</param>
    public int Trim(string text, int unused = 0) => text.Length;

    /// <summary>Trailing optional unused in the body — in-project remove-unused apply test.</summary>
    public int Prune(string text, int unused = 0) => text.Length;

    /// <summary>Two differently-typed parameters — for the reorder-adds-names apply test.</summary>
    public string Rank(int score, string label) => $"{label}:{score}";

    /// <summary>Trailing optional unused in the body — for the drop-side-effecting-argument refusal test.</summary>
    public int Emit(int keep, int drop = 0) => keep;

    /// <summary>Trailing optional unused in the body — its consumer self-nests, for the nested-rewrite refusal test.</summary>
    public string Wrap(string s, int depth = 0) => s;

    /// <summary>Called only from the *.g.cs consumer — for the generated-file refusal test.</summary>
    public int Stamp(int a, int b) => a + b;
}

/// <summary>Isolated consumers of <see cref="SignatureChangeSurface" /> members.</summary>
public class SignatureChangeConsumer
{
    private readonly SignatureChangeSurface surface = new();

    public string GreetPositional() => surface.Greet("world");

    public string GreetNamed() => surface.Greet(name: "world");

    public int MovePositional() => surface.Move(1, 2);

    public int MoveNamed() => surface.Move(x: 1, y: 2);

    public int LogOmitted() => surface.Log("a");

    public int LogPassed() => surface.Log("a", 3);

    public int SumExpanded() => surface.Sum(1, 2, 3);

    public int SumArray() => surface.Sum(new[] { 1 });

    public int HandleInt() => surface.Handle(1);

    public long WidenCall() => surface.Widen(5L);

    public Action<int> PingGroup() => surface.Ping;

    public string PingNameof() => nameof(SignatureChangeSurface.Ping);

    public int TrimCall() => surface.Trim("a", 7);

    public int PruneCall() => surface.Prune("z", 5);

    public string RankCall() => surface.Rank(1, "top");

    public int EmitCall() => surface.Emit(1, Next());

    public string WrapNested() => surface.Wrap(surface.Wrap("a", 1), 2);

    private static int Next() => 1;
}
