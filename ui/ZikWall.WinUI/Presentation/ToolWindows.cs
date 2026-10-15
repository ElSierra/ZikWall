using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace ZikWall.WinUI.Presentation;

// One window per tool and owner. Closing releases its view and theme subscription.
internal sealed class ToolWindows
{
    private static readonly ConditionalWeakTable<Window, ToolWindows> owners = new();
    private readonly Dictionary<string, Window> windows = new();
    private readonly Window owner;

    private ToolWindows(Window owner)
    {
        this.owner = owner;
        owner.Closed += (_, _) => { foreach (var window in windows.Values.ToArray()) window.Close(); };
    }

    internal static ToolWindows For(Window owner) => owners.GetValue(owner, value => new(value));
    internal int Count => windows.Count;
    internal IReadOnlyCollection<Window> Open => windows.Values;

    internal Window Show(string key, string title, Func<UIElement> content, bool scroll = true, Action? closed = null, int width = 760, int height = 850)
    {
        if (windows.TryGetValue(key, out var existing))
        {
            if (existing.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            existing.Activate();
            return existing;
        }
        var source = (FrameworkElement)owner.Content;
        var window = new Window { Title = title + " · ZikWall", SystemBackdrop = new MicaBackdrop() };
        var root = new Grid { RequestedTheme = source.ActualTheme };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var caption = new TextBlock { Text = title, Margin = new(16, 8, 140, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        root.Children.Add(caption);
        var view = content();
        FrameworkElement body = scroll
            ? new ScrollViewer { Content = view, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            : (FrameworkElement)view;
        body.Margin = new(20, 12, 20, 20);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        window.Content = root;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(caption);
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ZikWall.ico"));
        void ApplyTheme(FrameworkElement sender, object args)
        {
            root.RequestedTheme = source.ActualTheme;
            window.AppWindow.TitleBar.ButtonForegroundColor = source.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            window.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            window.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        source.ActualThemeChanged += ApplyTheme;
        ApplyTheme(source, EventArgs.Empty);
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = source.XamlRoot?.RasterizationScale ?? 1;
        int physicalWidth = Math.Min((int)(width * scale), area.Width), physicalHeight = Math.Min((int)(height * scale), area.Height);
        window.AppWindow.MoveAndResize(new(area.X + (area.Width - physicalWidth) / 2, area.Y + (area.Height - physicalHeight) / 2, physicalWidth, physicalHeight));
        windows.Add(key, window);
        window.Closed += (_, _) =>
        {
            windows.Remove(key);
            source.ActualThemeChanged -= ApplyTheme;
            if (scroll) ((ScrollViewer)body).Content = null;
            root.Children.Clear();
            window.Content = null;
            closed?.Invoke();
        };
        window.Activate();
        return window;
    }

    internal Window Text(string key, string title, string text) => Show(key, title, () => new TextBlock
    { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

    // Detail sheets are short; a compact window keeps them from floating in empty space.
    internal Window Details(string key, string title, Func<DetailSheet, DetailSheet> build, int height = 640) =>
        Show(key, title, () => build(new DetailSheet()).Build(), width: 640, height: height);

    internal Task<bool> EditAsync(string key, string title, UIElement form, Action<Window> opened)
    {
        var completion = new TaskCompletionSource<bool>();
        bool saved = false;
        Window? editor = null;
        editor = Show(key, title, () =>
        {
            var content = new Grid { RowSpacing = 16 };
            content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.Children.Add(new ScrollViewer { Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Name = "PrimaryButton", Content = "Save" };
            var cancel = new Button { Content = "Cancel" };
            save.Click += (_, _) => { saved = true; editor!.Close(); };
            cancel.Click += (_, _) => editor!.Close();
            buttons.Children.Add(save); buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 1); content.Children.Add(buttons);
            return content;
        }, scroll: false, closed: () => completion.TrySetResult(saved));
        opened(editor);
        return completion.Task;
    }
}
