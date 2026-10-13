using System.Security;

namespace ZikWall.WinUI.Models;

// Formatting only: reuse the sampled connection and offline country assets.
public sealed record ConnectionNotification(string Title, string Body, string CountryCode, string Country, string Application = "", string Destination = "")
{
    public static ConnectionNotification From(RecentConnectionBatch batch)
    {
        var first = batch.Connections[0];
        string app = Short(first.Application, 64);
        string title = batch.Type == ConnectionNotificationType.NewApps ? "First network activity" : "Network activity";
        string country = first.CountryCode.Length == 2 && first.CountryName.Length > 0
            ? $"{Short(first.CountryName, 64)} (approx.)" : "Country unknown";
        string destination = first.RemoteAddress;
        if (batch.Count > 1) destination += $" · +{batch.Count - 1} more";
        return new(title, $"{app}\n{destination}", first.CountryCode, country, app, destination);
    }

    public string ToToastXml(string assetsDirectory)
    {
        // Only two ASCII letters can select a bundled asset. Never accept paths from data.
        string code = CountryCode.ToLowerInvariant();
        bool validCode = code.Length == 2 && code.All(c => c is >= 'a' and <= 'z');
        string path = Path.Combine(assetsDirectory, "Flags", validCode ? code + ".svg" : "unknown.svg");
        if (!File.Exists(path)) path = Path.Combine(assetsDirectory, "Flags", "unknown.svg");
        string flag = File.Exists(path)
            ? $"<image placement=\"appLogoOverride\" src=\"{SecurityElement.Escape(new Uri(Path.GetFullPath(path)).AbsoluteUri)}\" alt=\"{SecurityElement.Escape(Country)}\"/>" : "";
        string lines = Application.Length > 0
            ? $"<text hint-maxLines=\"1\">{SecurityElement.Escape(Application)}</text><text hint-maxLines=\"1\">{SecurityElement.Escape(Destination)}</text>"
            : $"<text>{SecurityElement.Escape(Body)}</text>";
        return $"<toast duration=\"short\"><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(Title)}</text>{lines}{flag}</binding></visual><audio silent=\"true\"/></toast>";
    }

    private static string Short(string value, int limit)
    {
        string line = value.Replace('\r', ' ').Replace('\n', ' ');
        return line.Length <= limit ? line : line[..(limit - 1)] + "…";
    }
}
