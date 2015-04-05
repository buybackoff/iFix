using iFix.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust
{
    // A queue where elements are sorted by key. If two elements have the same key,
    // the order of their insertion is preserved.
    class PriorityQueue<TKey, TValue>
    {
        readonly SortedDictionary<Tuple<TKey, long>, TValue> _data = new SortedDictionary<Tuple<TKey, long>, TValue>();
        long _index = 0;

        // Any elements in the queue?
        public bool Any()
        {
            return _data.Count > 0;
        }

        // Requires: queue is not empty.
        public KeyValuePair<TKey, TValue> Front()
        {
            Assert.True(Any());
            var elem = _data.First();
            return new KeyValuePair<TKey, TValue>(elem.Key.Item1, elem.Value);
        }

        // Requires: queue is not empty.
        public KeyValuePair<TKey, TValue> Pop()
        {
            Assert.True(Any());
            var elem = _data.First();
            var res = new KeyValuePair<TKey, TValue>(elem.Key.Item1, elem.Value);
            _data.Remove(elem.Key);
            return res;
        }

        public void Push(TKey key, TValue value)
        {
            _data.Add(Tuple.Create(key, _index++), value);
        }
    }
}
