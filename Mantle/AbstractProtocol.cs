using iFix.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;

// This file defines FIX fields and messages in a version-agnostic way.
// These abstractions apply to all versions of FIX.

namespace iFix.Mantle
{
    public enum FieldAcceptance
    {
        // The tag was recongnized and its value successfully parsed.
        Accepted,
        // Tag number doesn't match.
        TagMismatch,
        // The tag was recognized but not parsed because there is already
        // parsed value associated with this tag.
        AlreadySet,
    }

    // Anything that can be serialized to and from a sequence of Core.Field: a single
    // field, a set of fields, a repeating group, or a message.
    public interface IFields
    {
        // TODO(roman): it would be nice if Fields property gave us human readable field names
        // in addition to tag numbers. Thes could be used by ToString().

        // Fields in serialized form.
        IEnumerable<Field> Fields { get; }

        // Attempts to set a field from its serialized form. If the tag isn't recognized
        // by the object, returns TagMismatch (this is not an error). If the object
        // already has value for associated with the given tag, returns AlreadySet.
        // Otherwise parses the value and returns Accepted. Throws if parsing fails.
        FieldAcceptance AcceptField(int tag, ArraySegment<byte> value);
    }

    // A single FIX message.
    public interface IMessage : IFields
    {
        string Protocol { get; }
    }

    // Factory and parser for FIX messages. The intent is to have separate factories
    // for different FIX versions and dialects.
    public interface IMessageFactory
    {
        // Returns null for unknown message types.
        // The 'fields' enumerator initially points to the first field in the
        // message, BeginString<8>.
        IMessage CreateMessage(IEnumerator<Field> fields);
    }

    // An adaptor from IEnumerable<IFields> to IFields: a sequence of IFields is IFields.
    // Derived classes shall implement IEnumerator<IFields> GetEnumerator().
    public abstract class FieldSet : IFields, IEnumerable<IFields>
    {
        class FieldEnumerator : IEnumerable<Field>
        {
            IEnumerable<IFields> _outer;

            public FieldEnumerator(IEnumerable<IFields> outer)
            {
                _outer = outer;
            }

            public IEnumerator<Field> GetEnumerator()
            {
                foreach (IFields fields in _outer)
                    foreach (Field field in fields.Fields)
                        yield return field;
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }
        }

        public FieldAcceptance AcceptField(int tag, ArraySegment<byte> value)
        {
            foreach (IFields fields in (IEnumerable<IFields>)this)
            {
                FieldAcceptance res = fields.AcceptField(tag, value);
                if (res != FieldAcceptance.TagMismatch)
                    return res;
            }
            return FieldAcceptance.TagMismatch;
        }

        public IEnumerable<Field> Fields { get { return new FieldEnumerator(this); } }

        public abstract IEnumerator<IFields> GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }

    // FIX repeating group: http://fixwiki.org/fixwiki/FPL:Tag_Value_Syntax#Repeating_Groups.
    // Derived classes shall implement int GroupSizeTag { get; }.
    // Use methods inherited from List<T> to manipulate the elements of the group.
    public abstract class FieldGroup<T> : List<T>, IFields where T : IFields, new()
    {
        class FieldEnumerator : IEnumerable<Field>
        {
            FieldGroup<T> _outer;

            public FieldEnumerator(FieldGroup<T> outer)
            {
                _outer = outer;
            }

            public IEnumerator<Field> GetEnumerator()
            {
                yield return new Field(
                    Serialization.SerializeInt(_outer.GroupSizeTag),
                    Serialization.SerializeInt(CountNonEmpty(_outer)));
                foreach (IFields fields in _outer)
                    foreach (Field field in fields.Fields)
                        yield return field;
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            static int CountNonEmpty(List<T> collection)
            {
                int res = 0;
                foreach (IFields fields in collection)
                {
                    foreach (Field field in fields.Fields)
                    {
                        ++res;
                        break;
                    }
                }
                return res;
            }
        }

        // Empty elements are ignored when serializing.
        public IEnumerable<Field> Fields { get { return new FieldEnumerator(this); } }

        public FieldAcceptance AcceptField(int tag, ArraySegment<byte> value)
        {
            if (Count == 0)
            {
                T elem = new T();
                FieldAcceptance res = elem.AcceptField(tag, value);
                Debug.Assert(res != FieldAcceptance.AlreadySet);
                if (res == FieldAcceptance.Accepted) Add(elem);
                return res;
            }
            else
            {
                FieldAcceptance res = this[Count - 1].AcceptField(tag, value);
                if (res == FieldAcceptance.AlreadySet)
                {
                    Add(new T());
                    res = this[Count - 1].AcceptField(tag, value);
                    Debug.Assert(res == FieldAcceptance.Accepted);
                }
                return res;
            }
        }

        // Returns the tag number of the NumInGroup field associated with the group.
        protected abstract int GroupSizeTag { get; }
    }

    // Base class for FIX fields with value type mapping to a struct CLR type,
    // such as int or DateTime.
    //
    // This is effectively a nullable type with two representations: parsed and
    // serialized. The two representations are kept in sync: whenever one is modified,
    // the other one is changed as well.
    public abstract class StructField<T> : IFields where T : struct
    {
        class FieldEnumerator : IEnumerable<Field>
        {
            StructField<T> _outer;

            public FieldEnumerator(StructField<T> outer)
            {
                _outer = outer;
            }

            public IEnumerator<Field> GetEnumerator()
            {
                if (_outer.HasValue)
                    yield return new Field(Serialization.SerializeInt(_outer.Tag), _outer._serializedValue);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }
        }

        T? _value;
        ArraySegment<byte> _serializedValue;

        public bool HasValue { get { return _value.HasValue; } }

        public T Value
        {
            get { return _value.Value; }
            set
            {
                _serializedValue = Serialize(value);
                _value = value;
            }
        }

        public IEnumerable<Field> Fields { get { return new FieldEnumerator(this); } }

        public FieldAcceptance AcceptField(int tag, ArraySegment<byte> value)
        {
            if (tag != Tag) return FieldAcceptance.TagMismatch;
            if (HasValue) return FieldAcceptance.AlreadySet;
            _value = Deserialize(value);
            _serializedValue = value;
            return FieldAcceptance.Accepted;
        }

        // Derived classes shall implement only the properties and methods listed below.
        protected abstract int Tag { get; }
        protected abstract ArraySegment<byte> Serialize(T value);
        protected abstract T Deserialize(ArraySegment<byte> bytes);
    }

    // The same as StructField<T> but for class types. The two classes have idential
    // interface.
    public abstract class ClassField<T> : IFields where T : class
    {
        class FieldEnumerator : IEnumerable<Field>
        {
            ClassField<T> _outer;

            public FieldEnumerator(ClassField<T> outer)
            {
                _outer = outer;
            }

            public IEnumerator<Field> GetEnumerator()
            {
                if (_outer.HasValue)
                    yield return new Field(Serialization.SerializeInt(_outer.Tag), _outer._serializedValue);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }
        }

        T _value;
        ArraySegment<byte> _serializedValue;

        public bool HasValue { get { return _value != null; } }

        public T Value
        {
            get { return _value; }
            set
            {
                _serializedValue = Serialize(value);
                _value = value;
            }
        }

        public IEnumerable<Field> Fields { get { return new FieldEnumerator(this); } }

        public FieldAcceptance AcceptField(int tag, ArraySegment<byte> value)
        {
            if (tag != Tag) return FieldAcceptance.TagMismatch;
            if (HasValue) return FieldAcceptance.AlreadySet;
            _value = Deserialize(value);
            _serializedValue = value;
            return FieldAcceptance.Accepted;
        }

        protected abstract int Tag { get; }
        protected abstract ArraySegment<byte> Serialize(T value);
        protected abstract T Deserialize(ArraySegment<byte> bytes);
    }

    public abstract class StringField : ClassField<string>
    {
        protected override ArraySegment<byte> Serialize(string value)
        {
            return Serialization.SerializeString(value);
        }
        protected override string Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseString(bytes);
        }
    }

    public abstract class IntField : StructField<int>
    {
        protected override ArraySegment<byte> Serialize(int value)
        {
            return Serialization.SerializeInt(value);
        }
        protected override int Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseInt(bytes);
        }
    }

    public abstract class LongField : StructField<long>
    {
        protected override ArraySegment<byte> Serialize(long value)
        {
            return Serialization.SerializeLong(value);
        }
        protected override long Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseLong(bytes);
        }
    }

    public abstract class DecimalField : StructField<decimal>
    {
        protected override ArraySegment<byte> Serialize(decimal value)
        {
            return Serialization.SerializeDecimal(value);
        }
        protected override decimal Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseDecimal(bytes);
        }
    }

    public abstract class BoolField : StructField<bool>
    {
        protected override ArraySegment<byte> Serialize(bool value)
        {
            return Serialization.SerializeBool(value);
        }
        protected override bool Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseBool(bytes);
        }
    }

    public abstract class CharField : StructField<char>
    {
        protected override ArraySegment<byte> Serialize(char value)
        {
            return Serialization.SerializeChar(value);
        }
        protected override char Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseChar(bytes);
        }
    }

    public abstract class TimestampField : StructField<DateTime>
    {
        protected override ArraySegment<byte> Serialize(DateTime value)
        {
            return Serialization.SerializeTimestamp(value);
        }
        protected override DateTime Deserialize(ArraySegment<byte> bytes)
        {
            return Deserialization.ParseTimestamp(bytes);
        }
    }

    // All FIX messages, regardless of protocol version and dialect, start with
    // Begin String <8>: http://www.onixs.biz/fix-dictionary/4.4/tagNum_8.html.
    public class BeginString : StringField
    {
        protected override int Tag { get { return 8; } }
    }
}
