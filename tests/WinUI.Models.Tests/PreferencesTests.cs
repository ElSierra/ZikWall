using ZikWall.WinUI.Services;

static class PreferencesTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        string directory = Path.Combine(Path.GetTempPath(), "ZikWall-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "monitor-preferences.json");
            // Previous releases saved only this field. New fields retain their original UI defaults.
            File.WriteAllText(path, "{\"HideWindowsSystemApps\":true}");
            var store = new MonitorPreferencesStore(directory);
            var migrated = store.Load();
            Check(migrated.HideWindowsSystemApps && migrated.Scope == 1 && migrated.Refresh == 1 && migrated.RecentAlerts,
                "Previous-version settings must migrate without resetting the hide preference or new defaults.");
            var changed = new MonitorPreferences(true, 1, 2, 0, 2, 2, false, true, true, "fixture-adapter", 2, true, true);
            store.Save(changed);
            Check(new MonitorPreferencesStore(directory).Load() == changed, "All monitor choices must survive another version/store instance.");
            store.Save(changed with { HideWindowsSystemApps = false });
            Check(store.Load() == changed with { HideWindowsSystemApps = false }, "Changing one toggle must retain all other choices.");
            File.WriteAllText(path, "{\"Group\":-1,\"Refresh\":99,\"Scope\":99,\"UsageSort\":99}");
            Check(store.Load() == new MonitorPreferences(), "Unknown choice indexes must fall back to valid UI defaults.");

            var themePanel = new AppearancePreferencesStore(directory);
            var dnsPanel = new AppearancePreferencesStore(directory);
            themePanel.Update(value => value with { Theme = 2 });
            dnsPanel.Update(value => value with { DnsEndpoint = "https://dns.example/dns-query", BootstrapAddress = "192.0.2.1" });
            themePanel.Update(value => value with { Theme = 1 });
            var expected = new AppearancePreferences(1, "https://dns.example/dns-query", "192.0.2.1");
            Check(new AppearancePreferencesStore(directory).Load() == expected, "Theme and DNS edits from separate panels must survive reopening without clobbering.");
            string appPath = Path.Combine(directory, "appearance-preferences.json");
            File.WriteAllText(appPath, "{broken");
            try { themePanel.Update(value => value with { Theme = 0 }); throw new Exception("Corrupt file was silently replaced."); }
            catch (System.Text.Json.JsonException) { Check(File.ReadAllText(appPath) == "{broken", "Keep unreadable settings for recovery."); }
            File.WriteAllText(appPath, "{\"Theme\":99,\"DnsEndpoint\":null}");
            Check(themePanel.Load() == new AppearancePreferences(), "Invalid theme/null DNS settings must be safe to restore.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} preference migration, restart, validation and independent panel update checks.");
    }
}
