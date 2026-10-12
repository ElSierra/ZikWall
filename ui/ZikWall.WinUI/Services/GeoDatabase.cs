using System.Globalization;
using System.Net;
using Microsoft.VisualBasic.FileIO;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed record GeoLocation(double Latitude, double Longitude, string Label);
public sealed class GeoDatabase
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, GeoLocation> entries = new();
    private readonly SortedSet<int> prefixes = new(Comparer<int>.Create((a, b) => b.CompareTo(a)));
    public int Count => entries.Count;
    public static GeoDatabase Read(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaximumBytes) throw new InvalidDataException("Geolocation CSV exceeds 8 MB.");
        var database = new GeoDatabase();
        using var reader = new TextFieldParser(stream, System.Text.Encoding.UTF8, true, true) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        reader.SetDelimiters(",");
        if (!(reader.ReadFields() ?? []).SequenceEqual(new[] { "network", "latitude", "longitude", "label" })) throw new InvalidDataException("CSV header must be network,latitude,longitude,label.");
        while (!reader.EndOfData)
        {
            var row = reader.ReadFields() ?? [];
            if (row.Length != 4 || row[3].Length > 160 || row[3].Any(char.IsControl) || !double.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) || !double.TryParse(row[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)
                || !double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180) throw new InvalidDataException("Invalid geolocation CSV row.");
            var (address, prefix) = FirewallRule.ParseNetwork(row[0]);
            if (address.Length == 0 || prefix == 0) throw new InvalidDataException("Location rows require a specific IP network.");
            string key = Key(IPAddress.Parse(address), (int)prefix);
            if (!database.entries.TryAdd(key, new(latitude, longitude, row[3]))) throw new InvalidDataException("Duplicate geolocation network.");
            database.prefixes.Add((int)prefix);
            if (database.entries.Count > 50000) throw new InvalidDataException("Geolocation CSV exceeds 50,000 networks.");
        }
        return database;
    }
    public GeoLocation? Find(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return null;
        int bits = ip.GetAddressBytes().Length * 8;
        foreach (int prefix in prefixes) if (prefix <= bits && entries.TryGetValue(Key(ip, prefix), out var location)) return location;
        return null;
    }
    private static string Key(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (int index = 0; index < bytes.Length; index++)
        {
            int remaining = prefix - index * 8;
            bytes[index] &= remaining >= 8 ? (byte)255 : remaining <= 0 ? (byte)0 : (byte)(255 << (8 - remaining));
        }
        return $"{Convert.ToHexString(bytes)}/{prefix}";
    }
}
