using System.Text;

namespace Karvon.Ports.Jprq;

/// <summary>
/// The little of Go's encoding/gob that jprq's control messages need: one struct of strings, integers and byte slices,
/// written after its type's definition (as a new gob.Encoder writes the first value of a type), and read by the
/// definition that comes with it (the ids are the sender's: Go numbers its types as a process first sends them).
/// </summary>
public static class Gob
{
    // Go's own type ids.
    public const int Bool = 1, Int = 2, Uint = 3, Bytes = 5, String = 6;

    /// <summary>The id of a struct's type as a new encoder gives it.</summary>
    const int FirstUserType = 64;

    /// <summary>A struct as gob writes it: its definition, then its value (zero fields left out, as Go leaves them).</summary>
    public static byte[] Encode(string typeName, IReadOnlyList<(string Name, int Type, object? Value)> fields)
    {
        var definition = new Writer();
        definition.Int(-FirstUserType);
        definition.Uint(3); // wireType.StructT
        definition.Uint(1); // structType.CommonType
        definition.Uint(1).String(typeName);
        definition.Uint(1).Int(FirstUserType);
        definition.Uint(0);
        definition.Uint(1).Uint((ulong)fields.Count); // structType.Field
        foreach (var (name, type, _) in fields)
        {
            definition.Uint(1).String(name);
            definition.Uint(1).Int(type);
            definition.Uint(0);
        }
        definition.Uint(0).Uint(0);

        var value = new Writer();
        value.Int(FirstUserType);
        var last = -1;
        for (var i = 0; i < fields.Count; i++)
        {
            var (_, type, field) = fields[i];
            if (IsZero(field))
                continue;
            value.Uint((ulong)(i - last));
            last = i;
            switch (type)
            {
                case String:
                    value.String((string)field!);
                    break;
                case Bytes:
                    value.Bytes((byte[])field!);
                    break;
                case Uint:
                    value.Uint(Convert.ToUInt64(field));
                    break;
                case Int:
                    value.Int(Convert.ToInt64(field));
                    break;
                case Bool:
                    value.Uint(1);
                    break;
                default:
                    throw new NotSupportedException($"gob type {type}");
            }
        }
        value.Uint(0);

        var message = new Writer();
        message.Uint((ulong)definition.Length).Raw(definition.ToArray());
        message.Uint((ulong)value.Length).Raw(value.ToArray());
        return message.ToArray();
    }

    static bool IsZero(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        byte[] b => b.Length == 0,
        bool b => !b,
        _ => Convert.ToUInt64(value) == 0,
    };

    /// <summary>The struct value in <paramref name="data"/>, its fields by name (strings, ulong, long, bool, byte[]); missing ones were zero.</summary>
    public static IReadOnlyDictionary<string, object> Decode(ReadOnlySpan<byte> data)
    {
        var types = new Dictionary<long, List<(string Name, long Type)>>();
        var reader = new Reader(data);
        while (!reader.AtEnd)
        {
            var length = (int)reader.Uint();
            var end = reader.Position + length;
            var id = reader.Int();
            if (id < 0)
                types[-id] = reader.StructDefinition();
            else
                return reader.Struct(types.TryGetValue(id, out var fields) ? fields : throw new FormatException($"gob: no definition of type {id}"));
            if (reader.Position != end)
                throw new FormatException("gob: a message's length is wrong");
        }
        throw new FormatException("gob: no value");
    }

    sealed class Writer
    {
        readonly MemoryStream _bytes = new();

        public int Length => (int)_bytes.Length;

        public byte[] ToArray() => _bytes.ToArray();

        public Writer Raw(byte[] bytes)
        {
            _bytes.Write(bytes);
            return this;
        }

        /// <summary>Under 128 a byte; else the count of bytes negated, then the bytes, high first.</summary>
        public Writer Uint(ulong value)
        {
            if (value < 0x80)
            {
                _bytes.WriteByte((byte)value);
                return this;
            }
            Span<byte> big = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(big, value);
            var first = big.IndexOfAnyExcept((byte)0);
            _bytes.WriteByte((byte)-(8 - first));
            _bytes.Write(big[first..]);
            return this;
        }

        /// <summary>Shifted left one bit; a negative one complemented, its low bit set.</summary>
        public Writer Int(long value) => Uint(value < 0 ? ((ulong)~value << 1) | 1 : (ulong)value << 1);

        public Writer String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        public Writer Bytes(byte[] value)
        {
            Uint((ulong)value.Length);
            return Raw(value);
        }
    }

    ref struct Reader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public readonly bool AtEnd => Position >= _data.Length;

        byte Next() => Position < _data.Length ? _data[Position++] : throw new FormatException("gob: the message ends early");

        public ulong Uint()
        {
            var first = Next();
            if (first < 0x80)
                return first;
            var count = -(sbyte)first;
            if (count > 8)
                throw new FormatException("gob: an integer is too long");
            ulong value = 0;
            for (var i = 0; i < count; i++)
                value = (value << 8) | Next();
            return value;
        }

        public long Int()
        {
            var u = Uint();
            return (u & 1) != 0 ? ~(long)(u >> 1) : (long)(u >> 1);
        }

        public byte[] Bytes()
        {
            var length = (int)Uint();
            if (length < 0 || Position + length > _data.Length)
                throw new FormatException("gob: the message ends early");
            var bytes = _data.Slice(Position, length).ToArray();
            Position += length;
            return bytes;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        /// <summary>A wireType that is a struct's: its fields' names and types, in order.</summary>
        public List<(string Name, long Type)> StructDefinition()
        {
            var fields = new List<(string, long)>();
            for (var field = -1L; Uint() is var delta and > 0;)
            {
                field += (long)delta;
                if (field != 2)
                    throw new NotSupportedException("gob: only structs are read");
                for (var inner = -1L; Uint() is var d and > 0;)
                {
                    inner += (long)d;
                    if (inner == 0)
                        Skip(); // CommonType: its name and id
                    else if (inner == 1)
                        for (var count = Uint(); count > 0; count--)
                        {
                            var (name, type) = ("", 0L);
                            for (var f = -1L; Uint() is var fd and > 0;)
                            {
                                f += (long)fd;
                                if (f == 0)
                                    name = String();
                                else
                                    type = Int();
                            }
                            fields.Add((name, type));
                        }
                }
            }
            return fields;
        }

        /// <summary>A CommonType (a name and an id): passed over.</summary>
        void Skip()
        {
            for (var f = -1L; Uint() is var d and > 0;)
            {
                f += (long)d;
                if (f == 0)
                    Bytes();
                else
                    Int();
            }
        }

        public Dictionary<string, object> Struct(List<(string Name, long Type)> fields)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var field = -1L; Uint() is var delta and > 0;)
            {
                field += (long)delta;
                if (field >= fields.Count)
                    throw new FormatException("gob: a field the type does not have");
                var (name, type) = fields[(int)field];
                values[name] = type switch
                {
                    Gob.String => String(),
                    Gob.Bytes => Bytes(),
                    Gob.Uint => Uint(),
                    Gob.Int => Int(),
                    Gob.Bool => Uint() != 0,
                    _ => throw new NotSupportedException($"gob: field {name} of type {type}"),
                };
            }
            return values;
        }
    }
}
