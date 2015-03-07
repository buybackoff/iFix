using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust
{
    class BiMapping<TKey, TValue>
    {
        readonly Dictionary<TKey, TValue> _direct;
        readonly Dictionary<TValue, TKey> _reverse;

        public BiMapping(Dictionary<TKey, TValue> direct, Dictionary<TValue, TKey> reverse)
        {
            _direct = direct;
            _reverse = reverse;
        }

        public bool ContainsKey(TKey key)
        {
            return _direct.ContainsKey(key);
        }

        public bool ContainsValue(TValue value)
        {
            return _reverse.ContainsKey(value);
        }

        public void Add(TKey key, TValue value)
        {
            // Note: ContainsKey() also checks for nullness.
            if (_direct.ContainsKey(key)) throw new ArgumentException("Duplicate key");
            if (_reverse.ContainsKey(value)) throw new ArgumentException("Duplicate value");
            _direct.Add(key, value);
            _reverse.Add(value, key);
        }

        public void Remove(TKey key)
        {
            if (key == null) throw new ArgumentNullException("Key is null");
            Remove(key, _direct, _reverse);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return _direct.TryGetValue(key, out value);
        }

        public TValue this[TKey key]
        {
            get
            {
                return _direct[key];
            }

            set
            {
                if (key == null) throw new ArgumentNullException("Key is null");
                if (value == null) throw new ArgumentNullException("Value is null");
                Remove(key, _direct, _reverse);
                Remove(value, _reverse, _direct);
                _direct.Add(key, value);
                _reverse.Add(value, key);
            }
        }

        static void Remove<T1, T2>(T1 t1, Dictionary<T1, T2> d1, Dictionary<T2, T1> d2)
        {
            T2 t2;
            if (d1.TryGetValue(t1, out t2))
            {
                Trace.Assert(d2.Remove(t2));
                Trace.Assert(d1.Remove(t1));
            }
        }
    }

    class BiDictionary<TFirst, TSecond>
    {
        readonly BiMapping<TFirst, TSecond> _byFirst;
        readonly BiMapping<TSecond, TFirst> _bySecond;

        public BiDictionary()
        {
            var direct = new Dictionary<TFirst, TSecond>();
            var reverse = new Dictionary<TSecond, TFirst>();
            _byFirst = new BiMapping<TFirst, TSecond>(direct, reverse);
            _bySecond = new BiMapping<TSecond, TFirst>(reverse, direct);
        }

        public BiMapping<TFirst, TSecond> ByFirst { get { return _byFirst; } }
        public BiMapping<TSecond, TFirst> BySecond { get { return _bySecond; } }
    }
}
