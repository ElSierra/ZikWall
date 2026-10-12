"""Build the static map from the pinned public-domain Natural Earth 5.1.2 land data.
Download URL and SHA-256 are recorded in docs/MAP-DATA.md. No remote code executes.
"""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "temp" / "natural-earth-land.geojson").read_bytes()
if hashlib.sha256(source).hexdigest() != "9e0729ee253ca7d7a5c4ae9395fb1902264c5377c52e224d13dd85010e2835d9":
    raise ValueError("Natural Earth input hash mismatch")
paths = []
for feature in json.loads(source)["features"]:
    geometry = feature["geometry"]
    polygons = geometry["coordinates"] if geometry["type"] == "MultiPolygon" else [geometry["coordinates"]]
    for polygon in polygons:
        rings = []
        for ring in polygon:
            points = [(2 * (float(x) + 180), 2 * (90 - float(y))) for x, y in ring]
            rings.append("M" + " L".join(f"{x:.1f},{y:.1f}" for x, y in points) + " Z")
        paths.append('<path d="' + ' '.join(rings) + '"/>')
svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 720 360"><g fill="#6b92ad" fill-rule="evenodd">' + ''.join(paths) + '</g></svg>\n'
(root / "ui" / "ZikWall.WinUI" / "Assets" / "World.svg").write_text(svg, encoding="utf-8")
