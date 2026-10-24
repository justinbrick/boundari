using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>A nation that belongs to a map: its name, flag, and where it starts (real-world position).</summary>
public sealed record NationSpec(string Name, string Flag, double Lat, double Lon);

/// <summary>
/// Fixed nations for the built-in maps, like OpenFront's map manifests: the nations
/// OpenFront puts on Europe, each starting near its real home, less the nine whose land is
/// outside Europe's play area (Morocco, Algeria, Tunisia, Libya, Syria, Iraq, Israel, Jordan,
/// Lebanon); Egypt is replaced by Cyprus.
/// </summary>
public static class MapNations
{
    public static IReadOnlyList<NationSpec>? For(string mapId) => mapId switch
    {
        "europe" => Europe,
        _ => null,
    };

    public static readonly NationSpec[] Europe =
    {
        new("Ireland", "ie", 53.3, -7.8),
        new("England", "gb-eng", 52.4, -1.5),
        new("Spain", "es", 40.2, -3.7),
        new("France", "fr", 47.3, 2.4),
        new("Italy", "it", 42.6, 12.6),
        new("Switzerland", "ch", 46.8, 8.2),
        new("Norway", "no", 61.2, 9.5),
        new("Sweden", "se", 60.8, 15.3),
        new("Finland", "fi", 62.6, 26.0),
        new("Belgium", "be", 50.6, 4.6),
        new("Netherlands", "nl", 52.2, 5.6),
        new("Germany", "de", 51.0, 10.2),
        new("Austria", "at", 47.6, 14.4),
        new("Poland", "pl", 52.1, 19.4),
        new("Czechia", "cz", 49.8, 15.4),
        new("Ukraine", "ua", 49.0, 31.4),
        new("Belarus", "by", 53.7, 28.0),
        new("Romania", "ro", 45.9, 24.9),
        new("Türkiye", "tr", 39.2, 33.0),
        new("Russia", "ru", 56.0, 40.5),
        new("Georgia", "ge", 42.1, 43.6),
        new("Portugal", "pt", 39.6, -8.1),
        new("Greece", "gr", 39.4, 21.9),
        new("Serbia", "rs", 44.0, 20.8),
        new("Hungary", "hu", 47.2, 19.4),
        new("Slovakia", "sk", 48.7, 19.6),
        new("Croatia", "hr", 45.6, 16.0),
        new("Bosnia and Herzegovina", "ba", 44.2, 17.8),
        new("Albania", "al", 41.1, 20.0),
        new("Bulgaria", "bg", 42.7, 25.3),
        new("Lithuania", "lt", 55.3, 23.9),
        new("Latvia", "lv", 56.9, 24.6),
        new("Estonia", "ee", 58.7, 25.6),
        new("Wales", "gb-wls", 52.3, -3.6),
        new("Scotland", "gb-sct", 56.8, -4.2),
        new("Kazakhstan", "kz", 49.4, 49.6),   // its European part, west of the Ural River
        new("Northern Ireland", "gb-nir", 54.6, -6.7),
        new("Sápmi", "", 68.6, 23.0),
        new("Denmark", "dk", 56.1, 9.2),
        new("Cyprus", "cy", 35.0, 33.2),      // in place of Egypt
        new("Iceland", "is", 64.9, -18.6),
        new("Andorra", "ad", 42.5, 1.55),
        new("Monaco", "mc", 43.74, 7.42),
    };
}
