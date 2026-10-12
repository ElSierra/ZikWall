"""Compile the pinned DB-IP Lite CSV into compact sorted offline lookup tables.
Usage: python tools/build-country-data.py temp/monitor-data/dbip-country-lite-2026-10.csv.gz
No runtime downloads. DB-IP data is CC BY 4.0; see Assets/Countries/NOTICE.md.
"""
import csv
import gzip
import hashlib
import ipaddress
import json
import pathlib
import struct
import sys

source = pathlib.Path(sys.argv[1])
expected = "097426b8ddae89157d444a59ac1847e873f7943c32d52becc4371c8b0273af80"
assert hashlib.sha256(source.read_bytes()).hexdigest() == expected, "Unexpected source hash"
root = pathlib.Path(__file__).resolve().parents[1]
output = root / "ui/ZikWall.WinUI/Assets/Countries"
output.mkdir(parents=True, exist_ok=True)
rows = {4: [], 6: []}
codes = set()
with gzip.open(source, "rt", encoding="utf-8", newline="") as stream:
    consumed = 0
    for row in csv.reader(stream):
        consumed += sum(map(len, row))
        assert consumed <= 128 * 1024 * 1024 and len(row) == 3
        start, end = map(ipaddress.ip_address, row[:2])
        code = row[2].upper()
        assert start.version == end.version and int(start) <= int(end)
        assert len(code) == 2 and code.isascii() and code.isalpha()
        rows[start.version].append((int(start), int(end), code))
        codes.add(code)
        assert len(rows[4]) + len(rows[6]) <= 2_000_000
metadata = {"source": "https://download.db-ip.com/free/dbip-country-lite-2026-10.csv.gz", "sourceSha256": expected, "edition": "2026-10", "license": "CC-BY-4.0", "files": {}}
for version, width in [(4, 4), (6, 16)]:
    entries = sorted(rows[version])
    data = bytearray(b"ZWC1" + struct.pack("<I", len(entries)))
    previous = -1
    for start, end, code in entries:
        assert start > previous, "Overlapping country ranges"
        previous = end
        data += start.to_bytes(width, "big") + end.to_bytes(width, "big") + code.encode("ascii")
    name = f"ipv{version}.zwc"
    (output / name).write_bytes(data)
    metadata["files"][name] = {"ranges": len(entries), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
(output / "provenance.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
print(json.dumps(metadata, indent=2))
