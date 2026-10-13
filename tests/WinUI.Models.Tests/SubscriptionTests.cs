using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class SubscriptionTests
{
    public static async Task RunAsync(bool network)
    {
        int checks = 0;
        void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
        var original = new BlocklistSubscription(Guid.NewGuid(), "Fixture", "https://lists.example/hosts", "", null, new([], ["old.example"], 0));
        byte[] bytes = Encoding.UTF8.GetBytes("0.0.0.0 ads.example\n192.0.2.0/24");
        using var handler = new FixtureHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var client = new HttpClient(handler);
        var updated = await BlocklistService.DownloadAsync(original, client, CancellationToken.None);
        Check(updated.Data.Domains.SequenceEqual(["ads.example"]) && updated.Data.Networks.SequenceEqual(["192.0.2.0/24"]) && updated.Updated is not null, "Subscription download must parse domains and IP networks and set refresh time.");
        var pinned = await BlocklistService.DownloadAsync(original with { ExpectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)) }, client, CancellationToken.None);
        Check(pinned.Data.Domains.SequenceEqual(updated.Data.Domains), "Matching hash pin must accept downloaded data.");
        async Task Reject(BlocklistSubscription item, string message)
        {
            try { await BlocklistService.DownloadAsync(item, client, CancellationToken.None); throw new Exception(message); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException) { checks++; }
        }
        await Reject(original with { ExpectedSha256 = new string('0', 64) }, "Wrong hash pin accepted.");
        await Reject(original with { Url = "http://lists.example/hosts" }, "Plain HTTP accepted.");
        handler.Response = () => new(HttpStatusCode.Redirect) { Content = new StringContent("ads.example") };
        await Reject(original, "Redirect accepted.");
        handler.Response = () => new(HttpStatusCode.ServiceUnavailable);
        await Reject(original, "Failed refresh accepted.");
        handler.Response = () => new(HttpStatusCode.OK) { Content = new StringContent("# empty list") };
        await Reject(original, "Empty list accepted.");
        Check(original.Data.Domains.SequenceEqual(["old.example"]) && original.Updated is null, "Failed updates must preserve the original subscription.");
        string directory = Path.Combine(Path.GetTempPath(), "ZikWall-subscriptions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PrivacyStore(directory);
            store.Save(new([updated], ["safe.ads.example"], true));
            var restored = store.Load();
            Check(restored.AutomaticUpdates && restored.Subscriptions.Single().Id == original.Id && restored.AllowedDomains.Single() == "safe.ads.example", "Subscriptions, exceptions and automatic refresh choice must survive restart.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} subscription download, pin, failure retention and persistence checks.");
        if (network)
        {
            var live = await BlocklistService.DownloadAsync(original with { Url = "https://raw.githubusercontent.com/StevenBlack/hosts/master/data/StevenBlack/hosts" }, CancellationToken.None);
            Check(live.Data.Domains.Count > 0 && live.Updated is not null, "Live HTTPS subscription returned no domains.");
            Console.WriteLine($"PASS: real HTTPS subscription download parsed {live.Data.Domains.Count} domains; no subscription or system setting was changed.");
        }
    }
    private sealed class FixtureHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response { get; set; } = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(Response());
    }
}
