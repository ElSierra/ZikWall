using System.Globalization;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Only a timestamp and hashed event identities are saved here.
public sealed class LogReadStateStore(string directory)
{
    private readonly string path = Path.Combine(directory, "log-read-through.txt");
    private readonly string statePath = Path.Combine(directory, "log-read-state.json");
    public LogReadState LoadState()
    {
        if (!File.Exists(statePath)) return new(Load(), []);
        if (new FileInfo(statePath).Length > 64 * 1024 * 1024) throw new InvalidDataException("Log read state is too large.");
        var state = JsonSerializer.Deserialize<LogReadState>(File.ReadAllText(statePath));
        if (state is null || state.ReadEventIds is null
            || state.ReadEventIds.Any(x => x is null || x.Length != 64 || !x.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Log read state is invalid.");
        return state;
    }
    public void SaveState(LogReadState state)
    {
        Directory.CreateDirectory(directory);
        using (var file = new FileStream(statePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state);
            file.Flush(true);
        }
        File.Move(statePath + ".tmp", statePath, true);
    }
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
