using System.Collections.Generic;

namespace VNTextPatch.Shared.Util
{
    internal class EnumeratorList<T>
    {
        private readonly List<IEnumerator<T>?> _enumerators = [];

        public void Add(IEnumerable<T> enumerable)
        {
            _enumerators.Add(enumerable.GetEnumerator());
        }

        public int Count => _enumerators.Count;

        public bool MoveNext()
        {
            bool success = true;
            for (int i = 0; i < _enumerators.Count; i++)
            {
                if (!MoveNext(i))
                    success = false;
            }

            return success;
        }

        public bool MoveNext(int index)
        {
            IEnumerator<T>? enumerator = _enumerators[index];

            if (enumerator is null)
                return false;

            if (!enumerator.MoveNext())
            {
                enumerator.Dispose();
                _enumerators[index] = null;
                return false;
            }

            return true;
        }

        public bool IsOpen(int index)
        {
            return _enumerators[index] is not null;
        }

        public T GetCurrent(int index)
        {
            IEnumerator<T>? enumerator = _enumerators[index];

            if (enumerator is null)
                throw new InvalidOperationException("The enumerator has already been disposed.");

            return enumerator.Current;
        }

        public T GetCurrentOrDefault(int index, T defaultValue)
        {
            var enumerator = _enumerators[index];
            return enumerator != null ? enumerator.Current : defaultValue;
        }
    }
}
