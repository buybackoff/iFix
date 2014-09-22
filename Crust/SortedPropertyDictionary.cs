using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Crust
{
    // A kind of bimap. Index from TValue to TProperty is unordered unique. Index from TProperty
    // to TValue is ordered non-unique.
    //
    // For example, SortedPropertyDictionary<Person, int> can be used to track people and their
    // age in years. Add(person, age) adds a new person to the container. Update(person, newAge)
    // updates the age of the existing person (e.g., when the said person has a birthday).
    // SmallestProperty(out person, out age) returns the youngest person and their age.
    class SortedPropertyDictionary<TValue, TProperty>
    {
        Dictionary<TValue, TProperty> _valToProp = new Dictionary<TValue,TProperty>();
        SortedDictionary<TProperty, HashSet<TValue>> _propToVal = new SortedDictionary<TProperty,HashSet<TValue>>();

        public int Count { get { return _valToProp.Count; } }

        // Throws if val already exists in the dictionary.
        public void Add(TValue val, TProperty prop)
        {
            _valToProp.Add(val, prop);
            Insert(_propToVal, prop, val);
        }

        // Works whether the value already exists or not.
        public void Update(TValue val, TProperty prop)
        {
            TProperty oldProp;
            if (_valToProp.TryGetValue(val, out oldProp))
                Erase(_propToVal, _valToProp[val], val);
            Insert(_propToVal, prop, val);
            _valToProp[val] = prop;
        }

        // Works whether the value exists or not.
        public void Remove(TValue val)
        {
            TProperty prop;
            if (_valToProp.TryGetValue(val, out prop))
            {
                Erase(_propToVal, prop, val);
                _valToProp.Remove(val);
            }
        }

        // Throws if the dictionary is empty.
        public void SmallestProperty(out TValue val, out TProperty prop)
        {
            if (_propToVal.Count == 0) throw new InvalidOperationException("SortedPropertyDictionary is empty");
            var first = _propToVal.First();
            prop = first.Key;
            val = first.Value.First();
        }

        static void Insert(SortedDictionary<TProperty, HashSet<TValue>> dict, TProperty prop, TValue val)
        {
            HashSet<TValue> values;
            if (!dict.TryGetValue(prop, out values))
            {
                values = new HashSet<TValue>();
                dict[prop] = values;
            }
            values.Add(val);
        }

        static void Erase(SortedDictionary<TProperty, HashSet<TValue>> dict, TProperty prop, TValue val)
        {
            HashSet<TValue> values = dict[prop];
            values.Remove(val);
            if (values.Count == 0)
                dict.Remove(prop);
        }
    }
}
