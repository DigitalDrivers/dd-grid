using System.Runtime.InteropServices;
using System.Text;

namespace DDGrid.Core.Protocol;

/// <summary>Reads the game's packets, the other way round from <see cref="PacketWriter"/>.</summary>
public ref struct PacketReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;

    public int Position { get; private set; }

    public readonly int Remaining => _buffer.Length - Position;

    public byte Byte() => _buffer[Position++];

    public T Value<T>() where T : unmanaged
    {
        var value = MemoryMarshal.Read<T>(_buffer[Position..]);
        Position += Marshal.SizeOf<T>();
        return value;
    }

    public void Skip(int count) => Position += count;

    public string Utf8()
    {
        var count = Byte();
        var text = Encoding.UTF8.GetString(_buffer.Slice(Position, count));
        Position += count;
        return text;
    }

    public string Utf32()
    {
        var count = Byte() * 4;
        var text = Encoding.UTF32.GetString(_buffer.Slice(Position, count));
        Position += count;
        return text;
    }
}
