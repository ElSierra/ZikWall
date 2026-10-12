using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed class PolicyStore(string directory)
{
    public string FilePath => Path.Combine(directory, "policy.json");
    public PolicyDocument Load()
    {
        if (!File.Exists(FilePath)) return PolicyDocument.Empty();
        if (new FileInfo(FilePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Policy file is too large.");
        using var stream = File.OpenRead(FilePath);
        var policy = JsonSerializer.Deserialize<PolicyDocument>(stream) ?? throw new InvalidDataException("Empty policy file.");
        policy.Validate();
        return policy;
    }
    public void Save(PolicyDocument policy)
    {
        policy.Validate();
        Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Policy file is too large.");
        string temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, FilePath, true);
    }
}
