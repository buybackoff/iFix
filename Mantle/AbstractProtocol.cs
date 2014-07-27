using iFix.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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

    public interface IFields
    {
        // TODO(roman): it would be nice to have human readable field names in addition to
        // tag numbers. The could be used by ToString().
        IEnumerable<Field> Fields { get; }
        FieldAcceptance AcceptField(int tag, ArraySegment<byte> value);
    }

    public interface IMessage : IFields
    {
        string Protocol { get; }
    }

    public interface IMessageFactory
    {
        // Returns null for unknown message types.
        IMessage CreateMessage(IEnumerator<Field> fields);
    }

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

        public IEnumerable<Field> Fields { get { return new FieldEnumerator(this); } }

        public FieldAcceptance AcceptField(int tag, ArraySegment<byte> value)
        {
            if (Count == 0)
                Add(new T());
            FieldAcceptance res = this[Count - 1].AcceptField(tag, value);
            if (res == FieldAcceptance.AlreadySet)
            {
                Add(new T());
                res = this[Count - 1].AcceptField(tag, value);
                Debug.Assert(res == FieldAcceptance.Accepted);
            }
            return res;
        }

        protected abstract int GroupSizeTag { get; }
    }

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

        protected abstract int Tag { get; }
        protected abstract ArraySegment<byte> Serialize(T value);
        protected abstract T Deserialize(ArraySegment<byte> bytes);
    }

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

    public class BeginString : StringField
    {
        protected override int Tag { get { return 8; } }
    }
}
