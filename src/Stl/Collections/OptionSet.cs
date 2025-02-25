namespace Stl.Collections;

[DataContract]
[Newtonsoft.Json.JsonObject(Newtonsoft.Json.MemberSerialization.OptOut)]
public class OptionSet : IServiceProvider
{
    private volatile ImmutableDictionary<Symbol, object> _items;

    [JsonIgnore]
    public ImmutableDictionary<Symbol, object> Items {
        get => _items;
        set => _items = value;
    }

    [DataMember(Order = 0)]
    [JsonPropertyName(nameof(Items)),  Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, NewtonsoftJsonSerialized<object>> JsonCompatibleItems
        => Items.ToDictionary(
            p => p.Key.Value,
            p => NewtonsoftJsonSerialized.New(p.Value),
            StringComparer.Ordinal);

    public object? this[Symbol key] {
        get => _items.TryGetValue(key, out var v) ? v : null;
        set {
            var spinWait = new SpinWait();
            var items = _items;
            while (true) {
                var newItems = value != null
                    ? items.SetItem(key, value)
                    : items.Remove(key);
                var oldItems = Interlocked.CompareExchange(ref _items, newItems, items);
                if (oldItems == items)
                    return;
                items = oldItems;
                spinWait.SpinOnce();
            }
        }
    }

    public object? this[Type optionType] {
        get => this[optionType.ToSymbol()];
        set => this[optionType.ToSymbol()] = value;
    }

    public OptionSet()
        => _items = ImmutableDictionary<Symbol, object>.Empty;

    [Newtonsoft.Json.JsonConstructor]
    public OptionSet(ImmutableDictionary<Symbol, object>? items)
        => _items = items ?? ImmutableDictionary<Symbol, object>.Empty;

    [JsonConstructor]
    public OptionSet(Dictionary<string, NewtonsoftJsonSerialized<object>>? jsonCompatibleItems)
        : this(jsonCompatibleItems?.ToImmutableDictionary(p => (Symbol) p.Key, p => p.Value.Value))
    { }

    public object? GetService(Type serviceType)
        => this[serviceType];

    public bool Contains(Type optionType)
        => this[optionType] != null;

    public bool Contains<T>()
        => this[typeof(T)] != null;

    public bool TryGet<T>(out T value)
    {
        var objValue = this[typeof(T)];
        if (objValue == null) {
            value = default!;
            return false;
        }
        value = (T) objValue;
        return true;
    }

    public T? Get<T>()
        => (T?) this[typeof(T)];

    public T GetOrDefault<T>(T @default)
    {
        var value = this[typeof(T)];
        return value == null ? @default : (T) value;
    }

    // ReSharper disable once HeapView.PossibleBoxingAllocation
    public void Set<T>(T value) => this[typeof(T)] = value;

    public void Remove<T>() => this[typeof(T)] = null;

    public bool Replace<T>(T expectedValue, T value)
    {
        var key = typeof(T).ToSymbol();
        var currentValue = (T?) this[key];
        if (EqualityComparer<T>.Default.Equals(currentValue!, expectedValue))
            return false;
        this[key] = value;
        return true;
    }

    public void Clear()
    {
        var spinWait = new SpinWait();
        var items = _items;
        while (true) {
            var oldItems = Interlocked.CompareExchange(
                ref _items, ImmutableDictionary<Symbol, object>.Empty, items);
            if (oldItems == items || oldItems.Count == 0)
                return;
            items = oldItems;
            spinWait.SpinOnce();
        }
    }
}
