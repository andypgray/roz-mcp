using TestFixture.Shapes;

namespace TestFixture.Services;

public interface IGenericRepository<T> where T : class, IShape
{
    T GetById(int id);
    void Add(T item);
}
