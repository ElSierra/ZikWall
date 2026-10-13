using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MainWindow
{
    internal async Task ExerciseRulesAsync()
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Select(string tag) => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == tag);
        Select("all");
        await Task.Delay(300);
        Check(entries.Count == 0, "An empty policy must not contain sample rules.");
        var rule = new FirewallRule(Guid.NewGuid(), "Diagnostic application block", Environment.ProcessPath!, "127.0.0.1", 6, 59999, 1, true, true, DateTimeOffset.UtcNow.AddMinutes(5));
        PolicyPanel.AddDraft(rule);
        Check(entries.Count == 1 && ((RuleEntry)RuleList.SelectedItem).PolicyRuleId == rule.Id && EditButton.IsEnabled, "Saving a rule must update the browser and editor action.");
        Check(DetailSummary.Text.Contains("Filtering is off"), "Saved rules must not be presented as enforced while filtering is off.");
        Select("blocked");
        Check(RuleList.Items.Count == 1, "Block filter missed the saved rule.");
        Select("allowed");
        Check(RuleList.Items.Count == 0, "Allow filter included a block rule.");
        Select("temporary");
        Check(RuleList.Items.Count == 1, "Temporary filter missed the saved rule.");
        Search.Text = "no match";
        await Task.Delay(200);
        Check(RuleList.Items.Count == 0 && EmptyState.Visibility == Visibility.Visible, $"Search must show the empty state. Text={Search.Text}; View={view}; Count={RuleList.Items.Count}; Empty={EmptyState.Visibility}; Expected={RuleQuery.Filter(entries, view, Search.Text).Count()}");
        Search.Text = "";
        await Task.Delay(200);
        await PolicyPanel.ExerciseRuleEditorCancelAsync(rule.Id);
        await PolicyPanel.ExerciseRuleEditorSaveAsync(rule.Id);
        Select("all");
        Check(entries.Single().State == "Disabled" && entries.Single().Category == "System rules" && entries.Single().Protocol == "UDP", "Editor changes must update the rule browser.");
        PolicyPanel.RemoveRule(rule.Id);
        Check(entries.Count == 0, "Removing a saved rule must update the browser.");
        PolicyPanel.AddDraft(rule);
        Select("all");
        await Task.Delay(300);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var folder = await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory);
        var file = await folder.CreateFileAsync("rules-preview.png", CreationCollisionOption.ReplaceExisting);
        using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        Check(!PolicyPanel.IsFiltering, "The rule browser diagnostic must never enable filtering.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rules-selftest.txt"), "PASS: empty policy, saved-rule synchronization, scope filters, search, editor cancellation, removal and native render. Isolated diagnostic policy; filtering remained off.");
    }
}
