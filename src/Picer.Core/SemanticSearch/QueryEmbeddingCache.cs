using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.SemanticSearch;

/// <summary>Small in-memory cache; keys contain digests, never question text or credentials.</summary>
public sealed class QueryEmbeddingCache(int capacity = 64)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (float[] Vector, DateTimeOffset Expires)> _items = new();
    private static string Key(string scope, string model, string query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\0" + model + "\0" + query)));
    public float[]? Read(string scope, string model, string query)
    {
        lock (_sync)
            return _items.TryGetValue(Key(scope, model, query), out var entry) && entry.Expires > DateTimeOffset.UtcNow
                ? entry.Vector.ToArray() : null;
    }
    public void Store(string scope, string model, string query, float[] vector)
    {
        if (capacity <= 0 || vector.Length is < 1 or > 4096 || vector.Any(x => !float.IsFinite(x))) return;
        lock (_sync)
        {
            var key = Key(scope, model, query);
            if (!_items.ContainsKey(key) && _items.Count >= capacity)
                _items.Remove(_items.MinBy(x => x.Value.Expires).Key);
            _items[key] = (vector.ToArray(), DateTimeOffset.UtcNow.AddMinutes(2));
        }
    }
}
