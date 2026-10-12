"""Extract only bounded self-contained flag SVGs from the pinned MIT flag-icons release."""
import hashlib
import json
import pathlib
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

archive = pathlib.Path(sys.argv[1])
assert hashlib.sha256(archive.read_bytes()).hexdigest() == "e53d8025dbac33630570a20e0f4a892b28232976b822e82f1a5f966145ba6b84"
root = pathlib.Path(__file__).resolve().parents[1] / "ui/ZikWall.WinUI/Assets/Flags"
root.mkdir(parents=True, exist_ok=True)
count = 0
with zipfile.ZipFile(archive) as z:
    for item in z.infolist():
        if re.fullmatch(r"[^/]+/flags/4x3/[a-z]{2}\.svg", item.filename):
            assert item.file_size <= 1_000_000
            data = z.read(item)
            assert b"<!DOCTYPE" not in data and b"<!ENTITY" not in data
            svg = ET.fromstring(data)
            for element in svg.iter():
                assert element.tag.split("}")[-1] not in ("script", "foreignObject", "image")
                for key, value in element.attrib.items():
                    assert not key.split("}")[-1].lower().startswith("on")
                    if key.split("}")[-1] == "href":
                        assert value.startswith("#")
            (root / pathlib.PurePosixPath(item.filename).name).write_bytes(data)
            count += 1
        elif re.fullmatch(r"[^/]+/LICENSE", item.filename):
            (root / "LICENSE.txt").write_bytes(z.read(item))
(root / "hashes.json").write_text(json.dumps({p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.glob("*.svg"))}, indent=2) + "\n", encoding="utf-8")
print(f"Extracted {count} local flag SVGs")
