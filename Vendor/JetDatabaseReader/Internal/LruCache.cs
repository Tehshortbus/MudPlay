#nullable disable
#pragma warning disable
using System.Collections.Generic;
using System.Linq;

namespace JetDatabaseReader
{
    /// <summary>
    /// Thread-safe LRU (Least Recently Used) cache implementation.
    /// Evicts the oldest entry when capacity is reached.
    /// </summary>
    /// <typeparam name="TKey">The type of keys in the cache.</typeparam>
    /// <typeparam name="TValue">The type of values in the cache.</typeparam>
    internal sealed class LruCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
        private readonly LinkedList<CacheItem> _lruList;
        private readonly object _lock = new object();

        private sealed class CacheItem
        {
            public TKey Key { get; set; }
            public TValue Value { get; set; }
        }

        public LruCache(int capacity)
        {
            _capacity = capacity;
            _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
            _lruList = new LinkedList<CacheItem>();
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _cache.Count;
                }
            }
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    // Move to front (most recently used)
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                value = default;
                return false;
            }
        }

        public void Add(TKey key, TValue value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var existingNode))
                {
                    // Update existing entry
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                    existingNode.Value.Value = value;
                    return;
                }

                // Evict LRU item if at capacity
                if (_cache.Count >= _capacity)
                {
                    var last = _lruList.Last;
                    _lruList.RemoveLast();
                    _cache.Remove(last.Value.Key);
                }

                // Add new item
                var cacheItem = new CacheItem { Key = key, Value = value };
                var node = new LinkedListNode<CacheItem>(cacheItem);
                _lruList.AddFirst(node);
                _cache[key] = node;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lruList.Clear();
            }
        }

        public bool Remove(TKey key)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _cache.Remove(key);
                    return true;
                }
                return false;
            }
        }
    }
}
