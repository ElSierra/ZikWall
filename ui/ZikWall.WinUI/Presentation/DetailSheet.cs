using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace ZikWall.WinUI.Presentation;

// Structured content for detail windows: a header, labelled cards and a closing note.
// Cards are parsed from XAML so {ThemeResource} brushes follow the window's own theme.
internal sealed class DetailSheet
{
    private const string Namespace = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
    private readonly StackPanel root = new() { Spacing = 12, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };

    internal DetailSheet Header(string title, string subtitle = "", string executable = "", string badge = "")
    {
        var grid = new Grid { ColumnSpacing = 14, Margin = new(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var icon = new AppIcon { Executable = executable, Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (badge.Length > 0) text.Children.Add(Caption(badge.ToUpperInvariant(), accent: true));
        text.Children.Add(Themed<TextBlock>($"<TextBlock {Namespace} Style='{{ThemeResource SubtitleTextBlockStyle}}' TextWrapping='Wrap' IsTextSelectionEnabled='True'/>", x => x.Text = title));
        if (subtitle.Length > 0) text.Children.Add(Caption(subtitle));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        root.Children.Add(grid);
        return this;
    }

    // Rows with an empty value are skipped so callers can pass optional facts directly.
    internal DetailSheet Section(string heading, params (string Label, string Value)[] rows)
    {
        var visible = rows.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
        if (visible.Count == 0) return this;
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = new(128) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (int i = 0; i < visible.Count; i++)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var label = Caption(visible[i].Label);
            label.Margin = new(0, 2, 0, 0); // Caption text is smaller; align its baseline with the value.
            var value = new TextBlock { Text = visible[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            grid.Children.Add(label); grid.Children.Add(value);
        }
        return Card(heading, grid);
    }

    // Application, endpoint and destination facts shared by connection and history-event windows.
    internal DetailSheet Connection(Models.ConnectionEntry entry)
    {
        bool remote = entry.RemotePort is > 0;
        Section("Application",
            ("Name", entry.Application),
            ("Process", entry.ProcessName.Length > 0 && entry.ProcessName != entry.Application ? $"{entry.ProcessName} · PID {entry.ProcessId}" : $"PID {entry.ProcessId}"),
            ("Path", entry.ProcessPath),
            ("Package", entry.PackageFamily));
        Section("Endpoint",
            ("Protocol", entry.Protocol),
            ("State", entry.State),
            ("Direction", entry.DirectionLabel),
            ("Local", entry.LocalEndpoint),
            ("Remote", remote ? entry.RemoteEndpoint : entry.RemotePort is null ? "Not available for UDP" : ""),
            ("Scope", remote ? "" : entry.NetworkScope));
        if (remote) Section("Destination",
            ("Hostname", entry.HostNames.Length > 0 ? entry.HostNames : "Unknown"),
            ("Name source", entry.HostNames.Length > 0 ? entry.HostNameSource + " · may not be the site the app requested" : ""),
            ("Network", entry.NetworkDescription),
            ("Country", entry.CountryLabel),
            ("Scope", entry.NetworkScope));
        return this;
    }

    // A compact table; every column after the first is right-aligned for numbers. The footer row is emphasized.
    internal DetailSheet Table(string heading, string[] columns, IReadOnlyList<string[]> rows, string[]? footer = null)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (int c = 1; c < columns.Length; c++) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        void AddRow(string[] cells, Func<string, TextBlock> create)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int c = 0; c < columns.Length; c++)
            {
                var cell = create(c < cells.Length ? cells[c] : "");
                if (c > 0) cell.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        AddRow(columns, text => Caption(text));
        foreach (var row in rows) AddRow(row, text => new TextBlock { Text = text, IsTextSelectionEnabled = true });
        if (footer is not null) AddRow(footer, text => new TextBlock { Text = text, IsTextSelectionEnabled = true, FontWeight = FontWeights.SemiBold });
        return Card(heading, grid);
    }

    internal DetailSheet Card(string heading, UIElement content)
    {
        if (heading.Length > 0)
            root.Children.Add(Themed<TextBlock>($"<TextBlock {Namespace} Style='{{ThemeResource BodyStrongTextBlockStyle}}' Margin='2,8,0,0'/>", x => x.Text = heading));
        root.Children.Add(Themed<Border>($"<Border {Namespace} Background='{{ThemeResource CardBackgroundFillColorDefaultBrush}}' BorderBrush='{{ThemeResource CardStrokeColorDefaultBrush}}' BorderThickness='1' CornerRadius='8' Padding='16,14'/>", x => x.Child = content));
        return this;
    }

    internal DetailSheet Actions(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4, 0, 0) };
        foreach (var button in buttons) panel.Children.Add(button);
        root.Children.Add(panel);
        return this;
    }

    internal DetailSheet Add(UIElement element) { root.Children.Add(element); return this; }

    internal DetailSheet Note(string text)
    {
        var panel = new Grid { ColumnSpacing = 8, Margin = new(2, 8, 2, 0) };
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        panel.Children.Add(Themed<FontIcon>($"<FontIcon {Namespace} Glyph='&#xE946;' FontSize='14' Margin='0,2,0,0' VerticalAlignment='Top' Foreground='{{ThemeResource TextFillColorSecondaryBrush}}'/>", _ => { }));
        var caption = Caption(text);
        Grid.SetColumn(caption, 1);
        panel.Children.Add(caption);
        root.Children.Add(panel);
        return this;
    }

    internal UIElement Build() => root;

    internal static TextBlock Caption(string text, bool accent = false) => Themed<TextBlock>(
        $"<TextBlock {Namespace} Style='{{ThemeResource CaptionTextBlockStyle}}' TextWrapping='Wrap' IsTextSelectionEnabled='True' Foreground='{{ThemeResource {(accent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")}}}'/>",
        x => { x.Text = text; if (accent) x.FontWeight = FontWeights.SemiBold; });

    private static T Themed<T>(string xaml, Action<T> setup) where T : UIElement
    {
        var element = (T)XamlReader.Load(xaml);
        setup(element);
        return element;
    }
}
