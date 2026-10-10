namespace WindowsDynamicBar.Core.Search;

/// <summary>Small thread-safe least-recently-used cache with a hard size limit. Evicted values are
/// handed to <see cref="Evicted"/> (e.g. to release native handles).</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    public int Capacity { get; }
    public int Count { get { lock (_sync) return _map.Count; } }
    public event Action<TValue>? Evicted;

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        TValue? evicted = default;
        bool didEvict = false;
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, value));
            _order.AddFirst(node);
            _map[key] = node;
            if (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                evicted = last.Value.Value;
                didEvict = true;
            }
        }
        if (didEvict) Evicted?.Invoke(evicted!);
    }

    public void Clear()
    {
        List<TValue> all;
        lock (_sync)
        {
            all = _order.Select(n => n.Value).ToList();
            _order.Clear();
            _map.Clear();
        }
        foreach (var v in all) Evicted?.Invoke(v);
    }
}
