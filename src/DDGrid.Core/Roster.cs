namespace DDGrid.Core;

/// <summary>A simulated driver's name and where they are from, as the entry list shows it.</summary>
public readonly record struct BotIdentity(string Name, string Nation);

/// <summary>
/// The names the simulated drivers race under. They read like an entry list and belong to nobody: no
/// member is going to think a real driver turned up, and no real driver's name is on a car they never
/// drove.
/// </summary>
public static class Roster
{
    public static readonly IReadOnlyList<BotIdentity> All =
    [
        new("Milan Brunner", "CHE"),
        new("Théo Marchand", "FRA"),
        new("Rasmus Dahl", "DNK"),
        new("Bruno Salvi", "ITA"),
        new("Nils Achterberg", "NLD"),
        new("Kenji Arata", "JPN"),
        new("Anton Weiss", "DEU"),
        new("Diego Ferreira", "PRT"),
        new("Ilya Petrenko", "UKR"),
        new("Jonas Fellner", "AUT"),
        new("Marek Nowicki", "POL"),
        new("Felix Brandt", "DEU"),
        new("Lars Halvorsen", "NOR"),
        new("Pepe Aranda", "ESP"),
        new("Aiden Carrick", "IRL"),
        new("Oskar Lindqvist", "SWE"),
        new("Matteo Rovelli", "ITA"),
        new("Sem Broekhuis", "NLD"),
        new("Tomás Vlach", "CZE"),
        new("Yuki Mishima", "JPN"),
        new("Gustav Lindeman", "DNK"),
        new("Rafael Duarte", "BRA"),
        new("Emile Carron", "BEL"),
        new("Teo Varga", "HUN"),
        new("Luca Marchetti", "ITA"),
        new("Ben Thornley", "GBR"),
        new("Stefan Vogel", "DEU"),
        new("Andrés Quiroga", "ARG"),
        new("Joris Vandamme", "BEL"),
        new("Kimi Tammela", "FIN"),
    ];

    /// <summary>
    /// A field of drivers. The same seed brings the same drivers in the same order, so a race can be run
    /// again; different seeds give different line-ups.
    /// </summary>
    public static BotIdentity[] Field(int count, int seed)
    {
        if (count < 0 || count > All.Count)
            throw new ArgumentOutOfRangeException(nameof(count), $"There are {All.Count} names to hand out");

        var shuffled = All.ToArray();
        var random = new Random(seed);
        for (var i = shuffled.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled[..count];
    }

    /// <summary>
    /// The GUID of the nth simulated driver. Deliberately far below the smallest SteamID, so that
    /// everything that reads a result can tell a bot from a member by the number alone.
    /// </summary>
    public const ulong GuidBase = 1000;

    /// <summary>The smallest number Steam hands out; below it nobody is a person.</summary>
    public const ulong SmallestSteamId = 76561197960265728;

    public static ulong GuidOf(int index) => GuidBase + (ulong)index;

    public static bool IsSimulated(ulong guid) => guid < SmallestSteamId;
}
