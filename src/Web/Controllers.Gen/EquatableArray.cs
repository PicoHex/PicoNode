namespace Controllers.Gen;

/// <summary>
/// A read-only array whose <see cref="Equals(EquatableArray{T})"/> compares the
/// <em>elements</em> instead of the array reference.
/// <para>
/// Why this exists (task T5.6): an incremental generator's cache is decided by comparing the
/// values its steps produce. <see cref="ImmutableArray{T}"/> — the type the model used to
/// carry — compares its underlying array by reference, so two structurally identical models
/// are "different" and every step downstream of them re-runs on every keystroke. Wrapping the
/// collections in this struct is what makes a model built from the same source compare equal
/// to the previous one, so the pipeline can report the step as <c>Unchanged</c>.
/// </para>
/// <para>
/// The element type is constrained to <see cref="IEquatable{T}"/> so equality can never
/// silently degrade back to reference equality one level down: every model in this generator
/// is a <c>record</c>, which implements it.
/// </para>
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    /// <summary>An array with no elements; equal to every other empty instance.</summary>
    public static EquatableArray<T> Empty => new([]);

    public int Length => _items?.Length ?? 0;

    public bool IsEmpty => Length == 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        var left = _items ?? [];
        var right = other._items ?? [];

        if (ReferenceEquals(left, right))
            return true;
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            // A constrained call to IEquatable<T>.Equals — no boxing, no reflection, and no
            // EqualityComparer<T>.Default indirection to hide a reference comparison behind.
            if (left[i] is null)
            {
                if (right[i] is not null)
                    return false;
                continue;
            }

            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        // Consistent with Equals by construction: same elements in the same order give the
        // same hash, and an empty array hashes like any other empty array.
        var hash = 17;
        foreach (var item in _items ?? [])
            hash = unchecked((hash * 31) + (item is null ? 0 : item.GetHashCode()));
        return hash;
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) =>
        left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) =>
        !left.Equals(right);

    public IEnumerator<T> GetEnumerator() =>
        ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
