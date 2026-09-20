using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DDGrid.Core;

/// <summary>A place on the track the game puts a car: a grid box or a pit box.</summary>
/// <param name="Index">The number in the name, e.g. 3 for AC_START_3. The game gives box n to the car
/// that starts nth.</param>
/// <param name="Position">World position, metres.</param>
/// <param name="HeadingRad">Where the car faces, radians, the same angle the game's cars use.</param>
public readonly record struct TrackSlot(int Index, Vector3 Position, float HeadingRad);

/// <summary>
/// Reads the grid and pit boxes out of a track's model. The server never has them: its content folder
/// holds only the files it needs for checksums, no .kn5, so a bot that places itself on the grid has to
/// be told where the boxes are. This runs once per track on a machine that has the game installed.
/// </summary>
public static class TrackModel
{
    /// <summary>Where the cars line up for a race.</summary>
    public const string GridPrefix = "AC_START_";

    /// <summary>Where the cars stand in the pits.</summary>
    public const string PitPrefix = "AC_PIT_";

    /// <summary>
    /// Every node named <paramref name="prefix"/> + a number, in index order. A node is a type, its name,
    /// the number of children, whether it is active, and for a dummy a 4x4 matrix; the boxes are dummies.
    /// The file is searched for the names instead of walking the whole tree, because walking it means
    /// parsing every mesh in a file of a few hundred megabytes for four dozen matrices.
    /// </summary>
    public static TrackSlot[] ReadSlots(ReadOnlySpan<byte> model, string prefix)
    {
        var pattern = Encoding.ASCII.GetBytes(prefix);
        var found = new SortedDictionary<int, TrackSlot>();

        for (var at = 0; at < model.Length;)
        {
            var hit = model[at..].IndexOf(pattern);
            if (hit < 0) break;
            var start = at + hit;
            at = start + pattern.Length;

            var digits = 0;
            while (start + pattern.Length + digits < model.Length && model[start + pattern.Length + digits] is >= (byte)'0' and <= (byte)'9')
                digits++;
            if (digits == 0) continue;

            var nameLength = pattern.Length + digits;
            // The name has to be a node header: an int32 type of 1 (dummy) and an int32 length that
            // matches exactly, or this is the name turning up somewhere else in the file.
            if (start < 8) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(model.Slice(start - 8, 4)) != 1) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(model.Slice(start - 4, 4)) != nameLength) continue;

            var matrixAt = start + nameLength + 4 + 1; // children count, then the active flag
            if (matrixAt + 64 > model.Length) continue;

            var matrix = model.Slice(matrixAt, 64);
            var index = int.Parse(Encoding.ASCII.GetString(model.Slice(start + pattern.Length, digits)));
            // Row-major: the last row of the matrix is the position, the third row is where the car faces.
            var position = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(matrix[48..]),
                BinaryPrimitives.ReadSingleLittleEndian(matrix[52..]),
                BinaryPrimitives.ReadSingleLittleEndian(matrix[56..]));
            var heading = MathF.Atan2(
                BinaryPrimitives.ReadSingleLittleEndian(matrix[32..]),
                BinaryPrimitives.ReadSingleLittleEndian(matrix[40..]));
            // Two models can hold the same box; the first one wins.
            found.TryAdd(index, new TrackSlot(index, position, heading));
        }

        return [.. found.Values];
    }

    public static TrackSlot[] ReadSlotsFromFile(string path, string prefix) => ReadSlots(File.ReadAllBytes(path), prefix);
}
