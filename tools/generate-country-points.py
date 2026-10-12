"""Generate offline country positions from pinned Natural Earth 5.1.2 data.

Input: ne_50m_admin_0_countries.geojson (public domain). See docs/MAP-DATA.md.
Usage: python tools/generate-country-points.py path/to/input.geojson
"""
import hashlib
import json
import sys
from pathlib import Path

raw = Path(sys.argv[1]).read_bytes()
if hashlib.sha256(raw).hexdigest() != "3e458fc036ad0a66411f2c1e6cac49c5d7bfb81cb1123bc513b22511a2b7fdeb":
    raise ValueError("Natural Earth input hash mismatch")
features = json.loads(raw)["features"]
selected = {}
for feature in features:
    record = feature["properties"]
    code = record["ISO_A2_EH"]
    if len(code) == 2 and (code not in selected or record["POP_EST"] > selected[code]["POP_EST"]):
        selected[code] = record
points = {code: {"Latitude": row["LABEL_Y"], "Longitude": row["LABEL_X"], "Label": row["NAME_EN"]}
          for code, row in sorted(selected.items())}
output = Path(__file__).resolve().parent.parent / "ui/ZikWall.WinUI/Assets/Countries/centroids.json"
output.write_text(json.dumps(points, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print(f"Generated {len(points)} country positions; SHA-256 {hashlib.sha256(output.read_bytes()).hexdigest()}")
