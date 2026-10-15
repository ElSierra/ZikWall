"""Build the offline map texture from public-domain Natural Earth shaded relief.

Input: https://naturalearth.s3.amazonaws.com/50m_raster/NE1_50M_SR_W.zip
Usage: python tools/build-map-relief.py temp/natural-earth-relief.zip
Requires Pillow. No runtime tile service or IP-location requests.
"""
import hashlib
import io
from pathlib import Path
import sys
import zipfile
from PIL import Image, ImageChops

source = Path(sys.argv[1]).read_bytes()
if hashlib.sha256(source).hexdigest() != "9e85417223414bbed425aea5dca0f0b4c5661fc94d4f14494140a21a08dfa450":
    raise ValueError("Natural Earth relief archive hash mismatch")
with zipfile.ZipFile(io.BytesIO(source)) as archive:
    with archive.open("NE1_50M_SR_W/NE1_50M_SR_W.tif") as stream:
        with Image.open(stream) as earth:
            # A 2K texture is sharp at desktop size and bounded to 8 MiB decoded.
            terrain = earth.resize((2048, 1024), Image.Resampling.LANCZOS).convert("RGB")
            red, green, blue = terrain.split()
            # Cartographic night palette: retain relief detail while making
            # ocean routes and destination markers legible. This is styling,
            # not a claim about current vegetation, weather or night imagery.
            water = ImageChops.subtract(blue, red).point(lambda value: min(255, max(0, (value - 12) * 20)))
            land = Image.merge("RGB", (red.point(lambda v: int(v * .50)), green.point(lambda v: int(v * .64)), blue.point(lambda v: int(v * .60))))
            ocean = Image.merge("RGB", (red.point(lambda v: int(v * .12)), green.point(lambda v: int(v * .24)), blue.point(lambda v: int(v * .40))))
            Image.composite(ocean, land, water).save(
                Path(__file__).resolve().parents[1] / "ui/ZikWall.WinUI/Assets/EarthRelief.png", optimize=True)
