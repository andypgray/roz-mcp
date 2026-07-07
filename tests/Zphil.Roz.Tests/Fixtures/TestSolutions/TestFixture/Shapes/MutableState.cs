namespace TestFixture.Shapes;

public class MutableState
{
    public int Count;
    public string Name { get; set; } = "";
    public int NestedCount;

    // Multi-declarator field for F3: edit_symbol Remove/Replace of one declarator must be rejected,
    // not silently take the sibling with it. Private/unused so it adds no references to disturb.
    private int _x, _y;
}

public static class MutableStateConsumer
{
    public static int ReadCount(MutableState state)
    {
        int local = state.Count;
        return local;
    }

    public static void IncrementCount(MutableState state)
    {
        state.Count++;
    }

    public static void AssignCount(MutableState state, int value)
    {
        state.Count = value;
    }

    public static void CompoundCount(MutableState state, int delta)
    {
        state.Count += delta;
    }

    public static int TryReadCount(MutableState state)
    {
        if (Int32.TryParse(state.Name, out int n))
        {
            return n;
        }

        return state.Count;
    }

    public static (int, string) DeconstructCount(MutableState state)
    {
        int x;
        string y;
        (x, y) = (state.Count, state.Name);
        return (x, y);
    }

    public static void NestedDeconstructWrite(MutableState state)
    {
        string s;
        int trailing;
        ((state.NestedCount, s), trailing) = ((42, "nested"), 0);
        _ = s;
        _ = trailing;
    }

    public static MutableState MakeWithInitializer() =>
        new() { Count = 7, Name = "init" };

    public static void SetName(MutableState state, string newName)
    {
        state.Name = newName;
    }

    public static string ReadName(MutableState state) => state.Name;

    public static string NameOfCount() => nameof(MutableState.Count);

    public static string NameOfReadName() => nameof(ReadName);
}
