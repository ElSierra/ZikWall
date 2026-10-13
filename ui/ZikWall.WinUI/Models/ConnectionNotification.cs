using System.Security;

namespace ZikWall.WinUI.Models;

// Formatting only: the presentation layer resolves the executable's icon to a local PNG.
public sealed record ConnectionNotification(string Title, string Body, string CountryCode, string Country, string Application = "", string Destination = "", string ProcessPath = "")
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
        return new(title, $"{app}\n{destination}", first.CountryCode, country, app, destination, first.ProcessPath);
    }

    public string ToToastXml(string assetsDirectory, string? appIconPath = null)
    {
        bool hasAppIcon = appIconPath is not null && Path.IsPathFullyQualified(appIconPath)
            && !appIconPath.StartsWith(@"\\") && File.Exists(appIconPath);
        string path = hasAppIcon ? appIconPath! : Path.Combine(assetsDirectory, "ZikWall.png");
        string icon = File.Exists(path)
            ? $"<image placement=\"appLogoOverride\" src=\"{SecurityElement.Escape(new Uri(Path.GetFullPath(path)).AbsoluteUri)}\" alt=\"{SecurityElement.Escape(hasAppIcon ? Application : "ZikWall")}\"/>" : "";
        string lines = Application.Length > 0
            ? $"<text hint-maxLines=\"1\">{SecurityElement.Escape(Application)}</text><text hint-maxLines=\"1\">{SecurityElement.Escape(Destination)}</text>"
            : $"<text>{SecurityElement.Escape(Body)}</text>";
        return $"<toast duration=\"short\"><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(Title)}</text>{lines}{icon}</binding></visual><audio silent=\"true\"/></toast>";
    }

    private static string Short(string value, int limit)
    {
        string line = value.Replace('\r', ' ').Replace('\n', ' ');
        return line.Length <= limit ? line : line[..(limit - 1)] + "…";
    }
}
