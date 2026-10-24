namespace Semantics.Core;

// A generic type with a nested type and calls into the base class library.
public class Repository<T> where T : class
{
    private readonly List<T> _items = new(); // @field-items

    public void Add(T item) => _items.Add(item); // @repo-add

    public T? Find(Func<T, bool> predicate) => _items.FirstOrDefault(predicate); // @repo-find

    public class Cursor // @cursor
    {
        public int Position; // @cursor-position
    }
}
