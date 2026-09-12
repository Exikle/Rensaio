using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace RensaioBackend.Data.Converters
{
    public static class GenericValueComparer
    {
        public static ValueComparer<T> Create<T>()
        {
            return typeof(T).IsGenericType &&
                   typeof(T).GetGenericTypeDefinition() == typeof(List<>)
                ? CreateListComparer<T>()
                : new ValueComparer<T>(
                    (a, b) => EqualityComparer<T>.Default.Equals(a, b),
                    a => a == null ? 0 : EqualityComparer<T>.Default.GetHashCode(a),
                    a => a
                );
        }

        private static ValueComparer<T> CreateListComparer<T>()
        {
            var elementType = typeof(T).GetGenericArguments()[0];

            return new ValueComparer<T>(
                (a, b) => SequenceEqual(a, b),
                a => GetSequenceHashCode(a),
                a => CloneList(a)
            );
        }

        private static bool SequenceEqual<T>(T? a, T? b)
        {
            if (a is IEnumerable<object> listA && b is IEnumerable<object> listB)
                return listA.SequenceEqual(listB);
            return EqualityComparer<T>.Default.Equals(a, b);
        }

        private static int GetSequenceHashCode<T>(T? list)
        {
            if (list is IEnumerable<object> sequence)
                return sequence.Aggregate(0, (hash, item) => HashCode.Combine(hash, item?.GetHashCode() ?? 0));
            return list?.GetHashCode() ?? 0;
        }

        private static T CloneList<T>(T? list)
        {
            if (list is IEnumerable<object> source && list is IList<object> original)
            {
                var cloned = Activator.CreateInstance(typeof(T)) as IList<object>;
                foreach (var item in original)
                    cloned?.Add(item);
                return (T)(object)cloned!;
            }

            return list!;
        }
    }
}
