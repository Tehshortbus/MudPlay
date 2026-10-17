using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace MudPlay.Services;

// Fills a GameTable from a table's JSON file in one pass over the bytes, a block at
// a time, so neither the whole file nor a parsed document is ever in memory (Rooms
// is 22 MB on disk and 54 MB as a parsed document).
//
// The file is what the importer writes: an array of flat objects. Anything else
// well-formed is still read without loss or complaint:
// a top level that is not an array gives a table with no rows, an array element
// that is not an object gives a row with no fields, and a field holding a nested
// object or array keeps that value's exact JSON text. Malformed JSON, or text that
// is not valid Unicode, throws JsonException, which the cache reports as an
// unreadable table.
internal static class GameTableReader
{
    // Larger than any field the importer writes; a longer token grows it.
    private const int BlockBytes = 128 * 1024;

    // The block stops growing here. No field the importer writes comes within a
    // thousandth of it, so a single value this long is a damaged file, and doubling
    // on towards the largest array there is would fail far less plainly.
    private const int MaxTokenBytes = 256 * 1024 * 1024;

    public static GameTable Read(Stream stream) => Read(stream, MaxTokenBytes);

    internal static GameTable Read(Stream stream, int maxTokenBytes)
    {
        Builder builder = new();
        byte[] block = ArrayPool<byte>.Shared.Rent(BlockBytes);
        try
        {
            JsonReaderState state = default;
            int filled = 0;
            long before = 0;
            bool final = false;
            while (true)
            {
                while (!final && filled < block.Length)
                {
                    int read = stream.Read(block, filled, block.Length - filled);
                    if (read == 0) final = true;
                    filled += read;
                }

                Utf8JsonReader reader = new(block.AsSpan(0, filled), final, state);
                try
                {
                    while (reader.Read()) builder.Token(ref reader, before);
                }
                catch (InvalidOperationException ex)
                {
                    // A name or an escaped text that is not valid Unicode (half a
                    // surrogate pair, a broken UTF-8 sequence): the file is as
                    // unreadable as one with broken syntax.
                    throw NotUnicode(ex);
                }
                if (final) break;

                state = reader.CurrentState;
                int used = (int)reader.BytesConsumed;
                if (used == 0)
                {
                    // One token longer than the block: it needs to be whole to be read.
                    if (block.Length >= maxTokenBytes)
                        throw new JsonException($"A single value in the table is longer than {maxTokenBytes} bytes.");
                    byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Min(block.Length * 2, maxTokenBytes));
                    block.AsSpan(0, filled).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(block);
                    block = larger;
                    continue;
                }
                block.AsSpan(used, filled - used).CopyTo(block);
                filled -= used;
                before += used;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
        return builder.Build(stream);
    }

    private static JsonException NotUnicode(Exception? inner) =>
        new("The table holds text that is not valid Unicode.", inner);

    // An append-only list kept in fixed pieces. A table's size is not known until its
    // last row, and an array regrown as it fills throws away several times the final
    // size on the way; pieces cost the final size once, plus the copy that joins them.
    private sealed class Pieces<T>
    {
        private const int Shift = 12;
        private const int Size = 1 << Shift;

        private readonly List<T[]> _pieces = new();
        private T[] _last = Array.Empty<T>();

        public int Count { get; private set; }

        public void Add(T item)
        {
            int at = Count & (Size - 1);
            if (at == 0) _pieces.Add(_last = new T[Size]);
            _last[at] = item;
            Count++;
        }

        public T this[int index]
        {
            get => _pieces[index >> Shift][index & (Size - 1)];
            set => _pieces[index >> Shift][index & (Size - 1)] = value;
        }

        public T[] ToArray()
        {
            T[] all = new T[Count];
            for (int i = 0; i < _pieces.Count; i++)
                _pieces[i].AsSpan(0, Math.Min(Size, Count - (i << Shift))).CopyTo(all.AsSpan(i << Shift));
            return all;
        }
    }

    // The distinct texts met so far, each stored once. Kept in pieces like the cells,
    // but a text is never split across two, so finding one again is a plain compare.
    private sealed class TextPool
    {
        private const int PieceBytes = 32 * 1024;

        private readonly List<byte[]> _pieces = new();
        private byte[] _last = Array.Empty<byte>();
        private int _lastUsed;
        private int _totalBytes;

        private readonly Pieces<int> _piece = new();
        private readonly Pieces<int> _offset = new();
        private readonly Pieces<int> _length = new();
        private readonly Pieces<int> _hash = new();
        // Open-addressed table of ids (stored + 1; 0 is an empty slot).
        private int[] _slots = new int[512];
        // The id (stored + 1) of each one-byte text met so far. Seven in ten of a
        // Rooms table's strings are the "0" of an exit that leads nowhere, and this
        // answers them without a hash or a probe.
        private readonly int[] _oneByte = new int[256];

        private ReadOnlySpan<byte> Bytes(int id) => _pieces[_piece[id]].AsSpan(_offset[id], _length[id]);

        public int IdOf(ReadOnlySpan<byte> utf8)
        {
            if (utf8.Length == 1 && _oneByte[utf8[0]] is not 0 and int known) return known - 1;

            int hash = Hash(utf8);
            int mask = _slots.Length - 1;
            int slot = hash & mask;
            while (_slots[slot] != 0)
            {
                int id = _slots[slot] - 1;
                if (_hash[id] == hash && Bytes(id).SequenceEqual(utf8)) return id;
                slot = (slot + 1) & mask;
            }

            if (_pieces.Count == 0 || utf8.Length > _last.Length - _lastUsed)
            {
                _pieces.Add(_last = new byte[Math.Max(PieceBytes, utf8.Length)]);
                _lastUsed = 0;
            }
            utf8.CopyTo(_last.AsSpan(_lastUsed));
            int added = _hash.Count;
            _piece.Add(_pieces.Count - 1);
            _offset.Add(_lastUsed);
            _length.Add(utf8.Length);
            _hash.Add(hash);
            _lastUsed += utf8.Length;
            _totalBytes += utf8.Length;
            _slots[slot] = added + 1;
            if (utf8.Length == 1) _oneByte[utf8[0]] = added + 1;
            if ((added + 1) * 2 > _slots.Length) GrowSlots();
            return added;
        }

        // Most texts are a handful of bytes (an exit, a short name) and a table has
        // hundreds of thousands of them: eight bytes a step, no setup.
        private static int Hash(ReadOnlySpan<byte> utf8)
        {
            const ulong Mix = 0x9E3779B97F4A7C15UL;
            ulong hash = (ulong)utf8.Length * Mix;
            while (utf8.Length >= sizeof(ulong))
            {
                hash = (hash ^ BinaryPrimitives.ReadUInt64LittleEndian(utf8)) * Mix;
                hash ^= hash >> 32;
                utf8 = utf8[sizeof(ulong)..];
            }
            ulong tail = 0;
            for (int i = 0; i < utf8.Length; i++) tail |= (ulong)utf8[i] << (8 * i);
            hash = (hash ^ tail) * Mix;
            return (int)(hash ^ (hash >> 32));
        }

        private void GrowSlots()
        {
            int[] slots = new int[_slots.Length * 2];
            int mask = slots.Length - 1;
            for (int id = 0; id < _hash.Count; id++)
            {
                int slot = _hash[id] & mask;
                while (slots[slot] != 0) slot = (slot + 1) & mask;
                slots[slot] = id + 1;
            }
            _slots = slots;
        }

        // Every text end to end, and where each id starts (one more entry closes the last).
        public (byte[] Text, int[] Start) Join()
        {
            byte[] text = new byte[_totalBytes];
            int[] start = new int[_hash.Count + 1];
            int at = 0;
            for (int id = 0; id < _hash.Count; id++)
            {
                start[id] = at;
                Bytes(id).CopyTo(text.AsSpan(at));
                at += _length[id];
            }
            start[^1] = at;
            return (text, start);
        }
    }

    private sealed class Builder
    {
        private readonly Pieces<byte> _kinds = new();
        private readonly Pieces<int> _values = new();
        private readonly Pieces<int> _rowShape = new();
        private readonly Pieces<int> _rowStart = new();
        private readonly TextPool _texts = new();

        private readonly List<GameTable.Shape> _shapes = new();
        private readonly List<byte[][]> _shapeUtf8 = new();
        private readonly Dictionary<string, int> _shapeIds = new(StringComparer.Ordinal);

        // Nested values met during the pass, as (cell, file offset, length): their
        // text is read back from the file once the pass is over.
        private readonly List<(int Cell, long Start, long Length)> _nested = new();

        private bool _started;
        private bool _rootIsArray;
        private bool _inRow;
        // The shape the row being read is expected to have (the last row's), how far
        // its names have matched, and the row's own names once one did not.
        private int _expected = -1;
        private byte[][] _expectedUtf8 = Array.Empty<byte[]>();
        private int _fields;
        private List<string>? _names;
        private long _nestedStart = -1;
        private byte[] _scratch = new byte[256];

        public void Token(ref Utf8JsonReader reader, long before)
        {
            if (!_started)
            {
                _started = true;
                _rootIsArray = reader.TokenType == JsonTokenType.StartArray;
                return;
            }
            if (!_rootIsArray) return;

            int depth = reader.CurrentDepth;
            if (_nestedStart >= 0)
            {
                // Inside a field's nested value: only its closing token matters.
                if (depth == 2 && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    _nested.Add((_values.Count - 1, _nestedStart, before + reader.BytesConsumed - _nestedStart));
                    _nestedStart = -1;
                }
                return;
            }

            if (depth == 1)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        _inRow = true;
                        _fields = 0;
                        _names = null;
                        _rowStart.Add(_values.Count);
                        break;
                    case JsonTokenType.EndObject:
                        _inRow = false;
                        _rowShape.Add(CloseRow());
                        break;
                    case JsonTokenType.EndArray:
                        // Closes an element that was itself an array; the table's own
                        // closing bracket is at depth 0.
                        break;
                    default:
                        // An element that is not an object: a row with no fields.
                        _rowStart.Add(_values.Count);
                        _rowShape.Add(ShapeId(Array.Empty<string>()));
                        break;
                }
                return;
            }
            if (depth != 2 || !_inRow) return;

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    Name(ref reader);
                    break;
                case JsonTokenType.Number:
                    Number(reader.ValueSpan);
                    break;
                case JsonTokenType.String:
                    Add(GameTable.StringCell, _texts.IdOf(Unescaped(ref reader)));
                    break;
                case JsonTokenType.Null:
                    Add(GameTable.NullCell, 0);
                    break;
                case JsonTokenType.True:
                    Add(GameTable.TrueCell, 0);
                    break;
                case JsonTokenType.False:
                    Add(GameTable.FalseCell, 0);
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    Add(reader.TokenType == JsonTokenType.StartObject ? GameTable.ObjectCell : GameTable.ArrayCell, 0);
                    _nestedStart = before + reader.TokenStartIndex;
                    break;
            }
        }

        private void Add(byte kind, int value)
        {
            _kinds.Add(kind);
            _values.Add(value);
        }

        private void Name(ref Utf8JsonReader reader)
        {
            if (_names is null)
            {
                byte[][] expected = _expectedUtf8;
                if (_fields < expected.Length
                    && (reader.ValueIsEscaped
                        ? reader.ValueTextEquals(expected[_fields])
                        : reader.ValueSpan.SequenceEqual(expected[_fields])))
                {
                    _fields++;
                    return;
                }
                // This row parts from the expected shape here: carry on by name.
                _names = new List<string>();
                if (_expected >= 0) _names.AddRange(_shapes[_expected].Names.AsSpan(0, _fields));
            }
            _names.Add(reader.GetString()!);
            _fields++;
        }

        private int CloseRow()
        {
            if (_names is null && _expected >= 0 && _fields == _shapes[_expected].Names.Length) return _expected;
            string[] names = _names is not null
                ? _names.ToArray()
                : _expected >= 0 ? _shapes[_expected].Names.AsSpan(0, _fields).ToArray() : Array.Empty<string>();
            int id = ShapeId(names);
            // A row without fields (a stray non-object, an empty object) never becomes
            // the expectation: the next real row would then miss it.
            if (names.Length != 0)
            {
                _expected = id;
                _expectedUtf8 = _shapeUtf8[id];
            }
            return id;
        }

        private int ShapeId(string[] names)
        {
            // Length-prefixed, so no two different lists share a key.
            StringBuilder key = new();
            foreach (string name in names) key.Append(name.Length).Append(':').Append(name);
            string text = key.ToString();
            if (_shapeIds.TryGetValue(text, out int id)) return id;
            id = _shapes.Count;
            _shapes.Add(new GameTable.Shape(names));
            _shapeUtf8.Add(Array.ConvertAll(names, Encoding.UTF8.GetBytes));
            _shapeIds[text] = id;
            return id;
        }

        // A whole number written the plain way is kept as the number; anything else
        // ("7.8", "1e3", "-0", past 32 bits) keeps its text so it reads back exactly.
        private void Number(ReadOnlySpan<byte> text)
        {
            // Nearly every cell is a few digits: read those without the general parser.
            int start = text[0] == (byte)'-' ? 1 : 0;
            if (text.Length - start <= 9)
            {
                int digits = 0;
                int at = start;
                for (; at < text.Length; at++)
                {
                    uint digit = (uint)(text[at] - '0');
                    if (digit > 9) break;
                    digits = digits * 10 + (int)digit;
                }
                if (at == text.Length && !(start == 1 && digits == 0))
                {
                    Add(GameTable.Int32Cell, start == 1 ? -digits : digits);
                    return;
                }
            }

            if (Utf8Parser.TryParse(text, out int value, out int used) && used == text.Length
                && !(value == 0 && text.Length != 1))
                Add(GameTable.Int32Cell, value);
            else
                Add(GameTable.NumberCell, _texts.IdOf(text));
        }

        private ReadOnlySpan<byte> Unescaped(ref Utf8JsonReader reader)
        {
            if (!reader.ValueIsEscaped)
            {
                // The JSON reader passes a string's bytes through unexamined, and a
                // bad one would later decode to U+FFFD without a word.
                if (!Utf8.IsValid(reader.ValueSpan)) throw NotUnicode(null);
                return reader.ValueSpan;
            }
            // Unescaping never lengthens the text.
            if (_scratch.Length < reader.ValueSpan.Length) _scratch = new byte[reader.ValueSpan.Length * 2];
            int written = reader.CopyString(_scratch);
            return _scratch.AsSpan(0, written);
        }

        public GameTable Build(Stream stream)
        {
            foreach ((int cell, long start, long length) in _nested)
            {
                byte[] raw = new byte[length];
                stream.Position = start;
                stream.ReadExactly(raw);
                if (!Utf8.IsValid(raw)) throw NotUnicode(null);
                _values[cell] = _texts.IdOf(raw);
            }

            (byte[] text, int[] textStart) = _texts.Join();
            bool uniform = _shapes.Count == 1;
            return new GameTable(
                _shapes.ToArray(),
                uniform ? null : _rowShape.ToArray(),
                uniform ? null : _rowStart.ToArray(),
                _rowShape.Count,
                _kinds.ToArray(), _values.ToArray(), text, textStart);
        }
    }
}
