// Two classes named 'NameTwin' in different namespaces. Used to test
// that find_overloads reports ambiguity rather than silently picking one.
namespace TestFixture.Services.Twins.Alpha
{
    public class NameTwin
    {
        public NameTwin(int id) { }
        public NameTwin(int id, string label) { }
        public void Execute() { }
        public int this[int i] => i;
    }
}

namespace TestFixture.Services.Twins.Beta
{
    public class NameTwin
    {
        public NameTwin() { }
        public NameTwin(double value) { }
        public void Execute(int count) { }
        public string this[string key] => key;
    }
}
