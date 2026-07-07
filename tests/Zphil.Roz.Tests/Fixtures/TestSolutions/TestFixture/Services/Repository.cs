namespace TestFixture.Services;

public class Repository<T> where T : class, new()
{
    private readonly List<T> _items = [];

    public void Add(T item) => _items.Add(item);

    public IReadOnlyList<T> GetAll() => _items;

    public T CreateDefault() => new();
}
