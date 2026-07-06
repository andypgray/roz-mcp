// Top-level statements — no explicit Main method or class declaration.
// Used to test that get_symbols_overview detects top-level statement files.

var builder = new List<string>();
builder.Add("Hello");
builder.Add("World");

foreach (var item in builder)
{
    Console.WriteLine(item);
}
