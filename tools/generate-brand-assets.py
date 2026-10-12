"""Convert the user-supplied ZikWall logo to Windows assets (requires Pillow).

The source PNG is preserved verbatim in tools/branding/ZikWall-source.png.
Keep its colors, proportions, transparency and padding; only resample it.
Pillow is a development dependency, never a build or runtime dependency.
"""
from pathlib import Path
from shutil import copyfile
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "ui" / "ZikWall.WinUI" / "Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    with Image.open(ROOT / "tools" / "branding" / "ZikWall-source.png") as source:
        art = source.convert("RGBA")
    if art.width != art.height:
        raise ValueError("The source logo must be square to preserve its proportions.")
    art.resize((256, 256), Image.Resampling.LANCZOS).save(
        ASSETS / "ZikWall.png", optimize=True)
    art.save(ASSETS / "ZikWall.ico", sizes=[(n, n) for n in SIZES])
    for name in ("100.ico", "101.ico"):
        copyfile(ASSETS / "ZikWall.ico", ROOT / "src" / "res" / name)
    print("Generated ZikWall.png and nine-size Windows icons for WinUI and native resources.")


if __name__ == "__main__":
    main()
