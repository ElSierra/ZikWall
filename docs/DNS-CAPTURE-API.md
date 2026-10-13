# Windows passive DNS capture API research

Research date: 2026-10-13. This investigation reads Microsoft documentation, Microsoft's packet decoder source and the installed Windows provider metadata. It does not start or stop capture, change DNS settings, install a driver or alter an existing trace session.

## Recommendation

Implementation follow-up: ZikWall uses the documented NetEvent configuration
below, with `MultiLayer=true`, and consumes its uniquely owned real-time ETW
session. A controlled test on this PC captured the outgoing unique DNS question
but missed its reply with `MultiLayer=false`; enabling it produced the expected
hostname and successful cleanup. Microsoft defines this setting as capture at
various layers of the stack. The test does not identify a particular offload or
driver as the cause. See `DOMAIN-HINTS.md` for the final implementation and limits.

Use the existing native ETW consumer infrastructure with the built-in `Microsoft-Windows-NDIS-PacketCapture` provider, after proving that direct `EnableTraceEx2` starts packet delivery on the supported Windows versions. Decode only complete DNS replies and retain bounded name/address/TTL observations. Do not assume a successful enable call proves packets are being captured.

The documented configuration alternative is the built-in NetEventPacketCapture CIM provider, exposed through `New-NetEventSession`, `Add-NetEventPacketCaptureProvider` and `Start-NetEventSession`. It supports local real-time capture, IPv4/IPv6 EtherTypes and IP-protocol filtering without installing a new capture driver. An external ETW consumer is still required. **Its documented API does not expose a TCP/UDP port filter.** [1][2]

I did not find a Microsoft-published native `NdisCapture` IOCTL or provider-private filter structure that safely supplies a port-53 rule through `EnableTraceEx2`. Do not invent a structure or treat an event-ID/keyword filter as a transport-port filter. Generic ETW payload filtering operates on manifest properties; this provider has a binary `Fragment` property, not a declared `SourcePort` property. Provider-side schematized filters require an agreement with the provider, which the general ETW API does not define. [3]

The initial native ETW probe should use an owned session, enable packet events at information level, produce a controlled DNS reply, count events and parse one packet, then stop only that owned session. If packet delivery requires provider-specific configuration, use the documented CIM setup or keep capture unavailable with a clear status. Do not hijack a pre-existing diagnostic capture.

## Exact provider and event format

Installed Microsoft metadata was read using:

```powershell
wevtutil gp Microsoft-Windows-NDIS-PacketCapture /ge:true /gm:true
Get-WinEvent -ListProvider Microsoft-Windows-NDIS-PacketCapture |
    Select-Object -ExpandProperty Events |
    Where-Object { $_.Id -in 1001,1002,1003,1016 } |
    Format-List Id,Template
netsh trace show provider Microsoft-Windows-NDIS-PacketCapture
netsh trace show capturefilterhelp
```

These read-only commands require sufficient permission on this computer. Resource/message file: `%WinDir%\system32\drivers\ndiscap.sys`. Provider GUID: **`{2ED6006E-4729-4609-B423-3EE7BCD678EF}`**. The GUID and packet decoding are independently present in Microsoft's `etl2pcapng` source at commit `b06ebcf73263002d81c4d84233d4010ae68b20b4`. [4]

| Keyword | Mask | Meaning |
| --- | --- | --- |
| Ethernet802.3 | `0x1` | Ethernet media |
| WirelessWAN | `0x200` | Raw IP media in Microsoft's decoder |
| Tunnel | `0x8000` | Tunnel media |
| Native802.11 | `0x10000` | Native Wi-Fi frame, not Ethernet |
| VMSwitch | `0x1000000` | Virtual switch |
| PacketTruncated | `0x2000000` | Truncated packet |
| PacketStart | `0x40000000` | First fragment of packet |
| PacketEnd | `0x80000000` | Last fragment of packet |
| ut:SendPath | `0x100000000` | Outgoing path |
| ut:ReceivePath | `0x200000000` | Incoming path |
| ut:Packet | `0x40000000000` | Packet events |

Event **1001 version 0** is the packet-fragment event at level 4. Installed template order:

```text
UInt32 MiniportIfIndex
UInt32 LowerIfIndex
UInt32 FragmentSize
Binary Fragment[FragmentSize]
UInt64 GftFlowEntryId
UInt64 GftOffloadInformation
```

Thus the version-0 packet begins at byte 12 of `UserData`, with its length at byte 8. Validate `UserDataLength >= 12`, then `FragmentSize <= UserDataLength - 12`; do not interpret trailing fields as packet bytes. Prefer TDH when supporting unknown versions or alternate event layouts. Microsoft's decoder explicitly reads `FragmentSize` then that many `Fragment` bytes, with a warning about adjacent variable properties. [4]

Event **1002 version 0** is **metadata**, not another packet payload:

```text
UInt32 MiniportIfIndex
UInt32 LowerIfIndex
UInt32 MetadataSize
Binary Metadata[MetadataSize]
```

Event **1003** is a virtual-switch fragment with strings and a repeated destination structure before `FragmentSize`. Its packet does not start at a fixed byte-12 offset. Use TDH if implementing it. Event **1016** describes an active capture rule (`UInt8 RuleId`, `UInt8 Directive`, `UInt16 Length`, binary `Value`); this output schema does not establish the controller's input API.

Microsoft's decoder classifies native-802.11 events as Wi-Fi, WirelessWAN as raw IP and other NDIS events as Ethernet. It accumulates packet fragments until `PacketEnd`. A simpler bounded DNS implementation may accept only events carrying both `PacketStart` and `PacketEnd`, but must document the resulting coverage limitation and reject truncation. Packet events do not establish an application PID; hostname-to-IP observations can support an existing connection's remote IP, but are not proof that that process requested that particular name. [4]

## Filtering, ownership and coverage

`Add-NetEventPacketCaptureProvider` exposes `EtherType`, `IpProtocols`, `IpAddresses`, link-layer addresses, capture direction/type and truncation. For conventional DNS, EtherTypes are IPv4 `0x0800` and IPv6 `0x86DD`; IP protocols are UDP 17 and TCP 6. The **documented default truncation is 128 bytes**, which is insufficient for many DNS replies. Set an explicit packet limit and reject incomplete DNS messages. Its docs say only one packet-capture provider can be used at a time; never remove somebody else's provider to obtain the slot. [1]

The installed `netsh trace show capturefilterhelp` likewise has no native transport-port option. `CustomIp` allows a value at a fixed offset from the IP header. That is not a general port-53 filter because IPv4 options and IPv6 extension headers change the transport-header position. User-mode port filtering after validated IP-header traversal is necessary unless a separately verified provider-specific filtering method is adopted.

Use bounded real-time buffers, no packet file output, bounded parser work and bounded TTL cache. Reject unrelated packet types as early as possible. Broad NDIS packet capture still has capture/ETW overhead even if the callback drops most events. Do not claim DNS-only kernel capture when the implementation enables all receive packets. ETW's documentation explicitly distinguishes cheap provider filtering from filters that run after event generation. [3]

Keep session handles and ownership explicit; stop through the handle created by this instance, close the consumer and join its worker. Existing `src/udpstats.c` demonstrates ZikWall's own guard/session-GUID checks for recovering only an orphaned ZikWall session. Never stop `netsh`, `pktmon`, or another application's session by guessing its name.

Plain DNS over UDP/TCP can expose names; encrypted DNS payloads do not. Existing Windows DNS-client observations remain useful for DNS resolved through that API, including when its transport is encrypted. Packet capture must complement, not replace, those hints. Name-to-IP associations can be ambiguous on shared hosting and expire with TTL; they do not reveal URL paths or necessarily the server's canonical name.

## Raw sockets comparison

Windows **does support both IPv4 and IPv6** `SIO_RCVALL`; it is incorrect to dismiss it as IPv4-only. Microsoft requires an explicitly bound local interface: IPv4 `AF_INET/SOCK_RAW/IPPROTO_IP`, IPv6 `AF_INET6/SOCK_RAW/IPPROTO_IPV6`; wildcard binding is not allowed. `RCVALL_IPLEVEL` avoids promiscuous NIC mode. Capture is restricted to the bound interface, requiring separate sockets and adapter-change handling for broad coverage. A raw IPv6 receive normally omits IPv6 headers, which differs from an Ethernet/raw-IP packet decoder's input. These differences, administrator requirements and absent application attribution make raw sockets a less direct fit for the existing native ETW architecture. [5][6]

## Sources

1. Microsoft, [Add-NetEventPacketCaptureProvider](https://learn.microsoft.com/en-us/powershell/module/neteventpacketcapture/add-neteventpacketcaptureprovider?view=windowsserver2025-ps). Also checked installed `MSFT_NetEventPacketCaptureProvider.cdxml` and `Get-Command ... -Syntax`.
2. Microsoft, [New-NetEventSession](https://learn.microsoft.com/en-us/powershell/module/neteventpacketcapture/new-neteventsession?view=windowsserver2025-ps), `RealtimeLocal` capture mode.
3. Microsoft, [EnableTraceEx2](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-enabletraceex2), filtering semantics and performance.
4. Microsoft, [etl2pcapng source](https://github.com/microsoft/etl2pcapng/blob/b06ebcf73263002d81c4d84233d4010ae68b20b4/src/main.c), keyword constants around line 716, provider GUID around line 893 and `EventCallback` around line 1868. Downloaded research checkout is `temp/etl2pcapng`.
5. Microsoft, [SIO_RCVALL](https://learn.microsoft.com/en-us/windows/win32/winsock/sio-rcvall), IPv4/IPv6 sockets, binding, mode and interface scope.
6. Microsoft, [TCP/IP raw sockets](https://learn.microsoft.com/en-us/windows/win32/winsock/tcp-ip-raw-sockets-2), access requirements and receive-header behavior.
