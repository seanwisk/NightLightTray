using System.Buffers.Binary;

namespace NightLight;

// CloudStore uses marshaled Bond CompactBinary v1. Keep untouched fields byte-for-byte.
internal sealed class CompactBinary
{
    private static ReadOnlySpan<byte> Header => [0x43, 0x42, 0x01, 0x00];
    private readonly SortedDictionary<int, Field> fields = new();

    internal enum Kind : byte
    {
        Bool = 2, UInt8, UInt16, UInt32, UInt64, Float, Double, String,
        Struct, List, Set, Map, Int8, Int16, Int32, Int64, WString
    }

    private sealed record Field(Kind Type, byte[] Value, byte[]? Original = null);

    internal static CompactBinary Parse(byte[] bytes)
    {
        if (bytes.Length > 64 * 1024 || !bytes.AsSpan().StartsWith(Header))
            throw new InvalidDataException("Unsupported CloudStore record header or size.");

        var reader = new Reader(bytes.AsSpan(Header.Length));
        var result = reader.ReadStruct(0);
        reader.RequireEnd();
        return result;
    }

    internal CompactBinary GetStruct(int id)
    {
        var reader = new Reader(GetRequired(id, Kind.Struct));
        var result = reader.ReadStruct(0);
        reader.RequireEnd();
        return result;
    }

    internal byte[] GetBlob(int id)
    {
        var reader = new Reader(GetRequired(id, Kind.List));
        if (reader.ReadByte() != (byte)Kind.Int8)
            throw new InvalidDataException("CloudStore payload is not a byte list.");

        var result = reader.Take(reader.ReadCount()).ToArray();
        reader.RequireEnd();
        return result;
    }

    internal bool GetBool(int id, bool fallback) =>
        Get(id, Kind.Bool) is { } value ? value[0] != 0 : fallback;

    internal ulong? GetNumber(int id, Kind type)
    {
        if (Get(id, type) is not { } value)
            return null;

        var reader = new Reader(value);
        return reader.ReadVarint();
    }

    internal void SetStruct(int id, CompactBinary value) =>
        fields[id] = new(Kind.Struct, value.Encode(marshal: false));

    internal void SetBlob(int id, byte[] value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)Kind.Int8);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
        fields[id] = new(Kind.List, stream.ToArray());
    }

    internal void SetBool(int id, bool value) =>
        fields[id] = new(Kind.Bool, [value ? (byte)1 : (byte)0]);

    internal void SetNumber(int id, Kind type, ulong value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, value);
        fields[id] = new(type, stream.ToArray());
    }

    internal void Remove(int id) => fields.Remove(id);

    internal byte[] Encode(bool marshal = true)
    {
        using var stream = new MemoryStream();
        if (marshal)
            stream.Write(Header);

        foreach (var (id, field) in fields)
        {
            if (field.Original is { } original)
            {
                stream.Write(original);
                continue;
            }

            if (id < 6)
                stream.WriteByte((byte)((id << 5) | (int)field.Type));
            else
            {
                stream.WriteByte((byte)((id <= byte.MaxValue ? 0xc0 : 0xe0) | (int)field.Type));
                stream.WriteByte((byte)id);
                if (id > byte.MaxValue)
                    stream.WriteByte((byte)(id >> 8));
            }
            stream.Write(field.Value);
        }

        stream.WriteByte(0);
        return stream.ToArray();
    }

    private byte[]? Get(int id, Kind type)
    {
        if (!fields.TryGetValue(id, out Field? field))
            return null;
        if (field.Type != type)
            throw new InvalidDataException($"Unexpected type for CloudStore field {id}.");
        return field.Value;
    }

    private byte[] GetRequired(int id, Kind type) =>
        Get(id, type) ?? throw new InvalidDataException($"Missing CloudStore field {id}.");

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 128)
        {
            stream.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int offset;

        internal Reader(ReadOnlySpan<byte> bytes) => this.bytes = bytes;

        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > bytes.Length - offset)
                throw new InvalidDataException("Truncated CloudStore record.");
            ReadOnlySpan<byte> value = bytes.Slice(offset, count);
            offset += count;
            return value;
        }

        internal byte ReadByte() => Take(1)[0];

        internal ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                byte next = ReadByte();
                if (shift == 63 && next > 1)
                    throw new InvalidDataException("CloudStore integer overflow.");
                value |= (ulong)(next & 0x7f) << shift;
                if (next < 128)
                    return value;
            }
            throw new InvalidDataException("Invalid CloudStore integer.");
        }

        internal int ReadCount()
        {
            ulong count = ReadVarint();
            if (count > (ulong)(bytes.Length - offset))
                throw new InvalidDataException("Invalid CloudStore container length.");
            return (int)count;
        }

        internal void RequireEnd()
        {
            if (offset != bytes.Length)
                throw new InvalidDataException("Unexpected trailing CloudStore data.");
        }

        internal CompactBinary ReadStruct(int depth)
        {
            var result = new CompactBinary();
            ReadFields(depth, result);
            return result;
        }

        private void ReadFields(int depth, CompactBinary? result)
        {
            CheckDepth(depth);
            int lastId = -1;
            while (true)
            {
                int start = offset;
                byte header = ReadByte();
                if (header == 0)
                    return;

                var type = (Kind)(header & 31);
                int id = header >> 5;
                id = id switch
                {
                    6 => ReadByte(),
                    7 => BinaryPrimitives.ReadUInt16LittleEndian(Take(2)),
                    _ => id
                };
                if (id <= lastId)
                    throw new InvalidDataException("Unordered or duplicate CloudStore fields.");
                lastId = id;

                int valueStart = offset;
                SkipValue(type, depth);
                result?.fields.Add(id, new(type, bytes[valueStart..offset].ToArray(),
                    bytes[start..offset].ToArray()));
            }
        }

        private void SkipValue(Kind type, int depth)
        {
            CheckDepth(depth);
            switch (type)
            {
                case Kind.Bool:
                    if (ReadByte() > 1)
                        throw new InvalidDataException("Invalid CloudStore boolean.");
                    break;
                case Kind.Int8:
                case Kind.UInt8:
                    Take(1);
                    break;
                case Kind.Int16:
                case Kind.UInt16:
                    ReadBoundedNumber(ushort.MaxValue);
                    break;
                case Kind.Int32:
                case Kind.UInt32:
                    ReadBoundedNumber(uint.MaxValue);
                    break;
                case Kind.Int64:
                case Kind.UInt64:
                    ReadVarint();
                    break;
                case Kind.Float:
                    Take(4);
                    break;
                case Kind.Double:
                    Take(8);
                    break;
                case Kind.String:
                    Take(ReadCount());
                    break;
                case Kind.WString:
                    Take(ReadCount() * 2);
                    break;
                case Kind.Struct:
                    ReadFields(depth + 1, null);
                    break;
                case Kind.List:
                case Kind.Set:
                case Kind.Map:
                    Kind element = ReadContainerKind();
                    Kind value = type == Kind.Map ? ReadContainerKind() : default;
                    int count = ReadCount();
                    for (int i = 0; i < count; i++)
                    {
                        SkipValue(element, depth + 1);
                        if (type == Kind.Map)
                            SkipValue(value, depth + 1);
                    }
                    break;
                default:
                    throw new InvalidDataException("Unsupported CloudStore field type.");
            }
        }

        private Kind ReadContainerKind()
        {
            byte type = ReadByte();
            if (type < (byte)Kind.Bool || type > (byte)Kind.WString)
                throw new InvalidDataException("Unsupported CloudStore container type.");
            return (Kind)type;
        }

        private void ReadBoundedNumber(ulong max)
        {
            if (ReadVarint() > max)
                throw new InvalidDataException("CloudStore integer overflow.");
        }

        private static void CheckDepth(int depth)
        {
            if (depth > 16)
                throw new InvalidDataException("CloudStore record is nested too deeply.");
        }
    }
}
