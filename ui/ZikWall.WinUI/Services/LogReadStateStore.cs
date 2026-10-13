using System.Globalization;

namespace ZikWall.WinUI.Services;

// Only a timestamp is saved here; no application names or destinations.
public sealed class LogReadStateStore(string directory)
{
    private readonly string path = Path.Combine(directory, "log-read-through.txt");
    public DateTimeOffset Load()
    {
        if (!File.Exists(path)) return DateTimeOffset.MinValue;
        if (new FileInfo(path).Length > 128) throw new InvalidDataException("Log read state is invalid.");
        return DateTimeOffset.TryParseExact(File.ReadAllText(path), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : DateTimeOffset.MinValue;
    }
    public void Save(DateTimeOffset value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".tmp", value.ToString("O", CultureInfo.InvariantCulture));
        File.Move(path + ".tmp", path, true);
    }
}
