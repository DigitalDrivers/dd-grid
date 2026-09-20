using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DDGrid.Core.Protocol;

/// <summary>
/// Writes the game's packets. Numbers go in as they sit in memory, smallest byte first, the way the game
/// and the server both read them; a string is its length and then its bytes.
/// </summary>
public ref struct PacketWriter(Span<byte> buffer)
{
    private readonly Span<byte> _buffer = buffer;

    public int Length { get; private set; }

    public readonly ReadOnlySpan<byte> Written => _buffer[..Length];

    public void Byte(byte value) => _buffer[Length++] = value;

    public void Id(ClientPacket packet) => Byte((byte)packet);

    public void Value<T>(T value) where T : unmanaged
    {
        MemoryMarshal.Write(_buffer[Length..], in value);
        Length += Marshal.SizeOf<T>();
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_buffer[Length..]);
        Length += bytes.Length;
    }

    /// <summary>A string as one or two bytes of length and then its UTF-8 bytes.</summary>
    public void Utf8(string value, bool longLength = false) => String(value, Encoding.UTF8, longLength, 1);

    /// <summary>A string whose length counts characters, not bytes, and four bytes per character.</summary>
    public void Utf32(string value) => String(value, Encoding.UTF32, false, 4);

    private void String(string value, Encoding encoding, bool longLength, int bytesPerCharacter)
    {
        var prefix = longLength ? 2 : 1;
        var written = encoding.GetBytes(value, _buffer[(Length + prefix)..]);
        var count = written / bytesPerCharacter;
        if (longLength) BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(Length, 2), (ushort)count);
        else _buffer[Length] = (byte)count;
        Length += prefix + written;
    }
}
