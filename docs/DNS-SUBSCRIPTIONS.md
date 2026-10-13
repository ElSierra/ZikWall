# Encrypted DNS and subscriptions

The DNS and subscriptions page implements a local UDP/TCP resolver on `127.0.0.1:53`, forwarding queries to an HTTPS DNS endpoint. The default is Cloudflare's `https://cloudflare-dns.com/dns-query` with bootstrap IP `1.1.1.1`. Bootstrap connects directly to the supplied provider IP while HTTPS still validates its certificate against the endpoint hostname. There is no certificate-validation bypass, plaintext fallback or redirect following.

Starting the proxy does **not** change system DNS. To use it for normal applications, the user must configure the adapter to use `127.0.0.1`. Restore normal adapter DNS before stopping the proxy or closing ZikWall; otherwise DNS resolution will stop. The UI reminds the user when stopping/exiting with the proxy active. The selected provider receives forwarded DNS questions. No DNS question history is saved.

Domain lists block matching names and descendants with NXDOMAIN. Explicit allowed domains and their descendants override those lists. This is resolver-level filtering: it does not identify the requesting application and cannot prevent applications from using cached IPs, direct IPs or their own DNS/DoH service. It is not WFP domain attribution. DoT and DoQ are not implemented.

Subscriptions support plain domains, common hosts-file entries, simple `||domain^` entries and IPv4/IPv6 CIDRs. Unsupported lines are counted; empty/invalid lists are rejected. Downloads require HTTPS, normal TLS validation, no redirects, a 20-second deadline, an 8 MB limit and entry-count limits. An optional SHA-256 pin rejects changed content until the user supplies a matching pin. Automatic refresh is opt-in and runs every 24 hours while the page's controller is active. Failed refreshes retain the prior data.

Domain updates take effect in the running proxy. **Copy IPs to policy** creates a snapshot of IP/CIDR block rules in the selected policy profile, ready for review and explicit Apply. Copied IP rules are separate from subscriptions; refreshing/removing a subscription does not rewrite those copies. No third-party list is subscribed by default.

Validation includes real loopback UDP and TCP blocking, DNS framing/ID preservation, malformed-query rejection, domain boundaries/exceptions and bounded response reads. An explicit network smoke test resolved `example.com` through the local proxy and certificate-validated Cloudflare HTTPS. No system DNS settings were changed during these tests.

On 13 October 2026, the live DoH check passed again, and a real HTTPS download of
the StevenBlack source list parsed 2,852 domains without saving a subscription.
Deterministic checks also cover successful refresh, matching/mismatched SHA-256
pins, rejected HTTP URLs/redirects/failed responses/empty lists, retention of the
previous data, and settings persistence. The UI now catches validation errors
(`InvalidDataException`) as well as transport errors, so invalid domains or list
content produce an error banner instead of escaping the event handler. A native
view check exercises invalid domain exception entry.

Reproduce both optional public-network checks with:

```powershell
dotnet run --project tests/WinUI.Models.Tests -c Release -- bin/profile_internal.xml --doh --subscriptions
```

These checks verify the resolver and download paths. They do not claim that this
machine's adapters currently use ZikWall DNS, or exercise a 24-hour refresh wait.
