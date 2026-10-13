"""Compile the pinned DB-IP ASN Lite CSV into compact offline network-owner tables.
Usage: python tools/build-network-data.py temp/monitor-data/dbip-asn-lite-2026-10.csv.gz
No runtime downloads. DB-IP data is CC BY 4.0; see Assets/Networks/NOTICE.md.
"""
import csv
import gzip
import hashlib
import ipaddress
import json
import pathlib
import re
import struct
import sys

source = pathlib.Path(sys.argv[1])
expected = "ed175994548b59d168b2ab9faf1d64a1e2bf44e73099e1482e61c84b5a959af3"
assert hashlib.sha256(source.read_bytes()).hexdigest() == expected, "Unexpected source hash"
root = pathlib.Path(__file__).resolve().parents[1]
output = root / "ui/ZikWall.WinUI/Assets/Networks"
output.mkdir(parents=True, exist_ok=True)

# Registry names carry legal suffixes ("Amazon.com, Inc."); people recognise the brand.
SUFFIX = re.compile(r"(?:[,\s]+(?:inc|incorporated|llc|l\.l\.c|ltd|limited|corp|corporation|co|company|gmbh|b\.v|bv|s\.a|sa|ag|plc|pte|pty|s\.r\.l|srl|oy|ab|as|nv|n\.v|kk|k\.k)\.?)+$", re.IGNORECASE)
ALIASES = {
    "amazon.com": "Amazon", "amazon technologies": "Amazon", "amazon data services": "Amazon",
    "akamai international": "Akamai", "akamai technologies": "Akamai",
    "facebook": "Meta", "meta platforms": "Meta",
    "microsoft": "Microsoft", "google": "Google", "cloudflare": "Cloudflare",
}

def friendly(name):
    name = " ".join(name.split())
    cleaned = SUFFIX.sub("", name).strip(" ,") or name
    return ALIASES.get(cleaned.lower(), cleaned)[:80]

rows = {4: [], 6: []}
names = {}
with gzip.open(source, "rt", encoding="utf-8", newline="") as stream:
    consumed = 0
    for row in csv.reader(stream):
        consumed += sum(map(len, row))
        assert consumed <= 256 * 1024 * 1024 and len(row) == 4
        start, end = map(ipaddress.ip_address, row[:2])
        asn = int(row[2])
        assert start.version == end.version and int(start) <= int(end) and 0 < asn < 2**32
        rows[start.version].append((int(start), int(end), asn))
        names.setdefault(asn, friendly(row[3]))
        assert len(rows[4]) + len(rows[6]) <= 4_000_000

metadata = {"source": "https://download.db-ip.com/free/dbip-asn-lite-2026-10.csv.gz", "sourceSha256": expected, "edition": "2026-10", "license": "CC-BY-4.0", "files": {}}

def emit(name, data, **extra):
    (output / name).write_bytes(data)
    metadata["files"][name] = {**extra, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}

for version, width in [(4, 4), (6, 16)]:
    merged = []
    for start, end, asn in sorted(rows[version]):
        assert not merged or start > merged[-1][1], "Overlapping network ranges"
        if merged and merged[-1][2] == asn and merged[-1][1] + 1 == start: merged[-1][1] = end
        else: merged.append([start, end, asn])
    data = bytearray(b"ZWN1" + struct.pack("<I", len(merged)))
    for start, end, asn in merged:
        data += start.to_bytes(width, "big") + end.to_bytes(width, "big") + struct.pack("<I", asn)
    emit(f"ipv{version}.zwn", data, ranges=len(merged))

# Sorted (ASN, offset) index followed by the UTF-8 name blob; offsets[i + 1] ends name i.
blob, index = bytearray(), bytearray()
for asn in sorted(names):
    index += struct.pack("<II", asn, len(blob))
    blob += names[asn].encode("utf-8")
emit("names.zwn", b"ZWO1" + struct.pack("<I", len(names)) + index + struct.pack("<I", len(blob)) + blob, networks=len(names))
(output / "provenance.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
print(json.dumps(metadata, indent=2))
