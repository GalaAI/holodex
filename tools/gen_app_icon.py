"""Builds assets/omniwand.ico, the Omniwand app icon (exe, desktop shortcut, taskbar, Alt+Tab).

Each size comes from the brand drawing ratified for it (E:/ArkStation/.ark/packages/ui/src/brand/assets/ark-lockup),
never one artwork scaled across every size:
- 16          ark-aperture-favicon-16.svg   (hinted for 16px)
- 20, 24, 32  ark-aperture-favicon-32.svg   (32px and up drawing)
- 40 to 256   ark-aperture-emblem-green-tile.svg (the full aperture emblem, "use it at 48px and above")
App icons use the -tile versions: the cream A does not read on light desktops without the tile.

Rasterizing reuses sharp from Ark's node_modules (librsvg); Pillow packs the per-size PNGs into one ICO.
Run: python tools/gen_app_icon.py (from the omniwand folder)
"""
import io
import json
import pathlib
import subprocess

from PIL import Image

ARK = pathlib.Path("E:/ArkStation/.ark")
BRAND = ARK / "packages/ui/src/brand/assets/ark-lockup"
OUT = pathlib.Path(__file__).resolve().parent.parent / "assets" / "omniwand.ico"

SOURCES = [
    (16, "ark-aperture-favicon-16.svg"),
    (20, "ark-aperture-favicon-32.svg"),
    (24, "ark-aperture-favicon-32.svg"),
    (32, "ark-aperture-favicon-32.svg"),
    (40, "ark-aperture-emblem-green-tile.svg"),
    (48, "ark-aperture-emblem-green-tile.svg"),
    (64, "ark-aperture-emblem-green-tile.svg"),
    (128, "ark-aperture-emblem-green-tile.svg"),
    (256, "ark-aperture-emblem-green-tile.svg"),
]

# sharp is a pnpm transitive dependency in Ark; resolve its real folder instead of guessing a version.
SHARP = next((ARK / "node_modules/.pnpm").glob("sharp@*/node_modules/sharp"))

RENDER_JS = r"""
const sharp = require(process.argv[1]);
const jobs = JSON.parse(process.argv[2]);
(async () => {
  const out = [];
  for (const j of jobs) {
    const buf = await sharp(j.file, { density: Math.max(72, (72 * j.size) / 16) })
      .resize(j.size, j.size, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
      .png().toBuffer();
    out.push(buf.toString('base64'));
  }
  process.stdout.write(JSON.stringify(out));
})().catch((e) => { console.error(e); process.exit(1); });
"""


def main() -> None:
    jobs = [{"size": s, "file": str(BRAND / f)} for s, f in SOURCES]
    res = subprocess.run(["node", "-e", RENDER_JS, str(SHARP), json.dumps(jobs)],
                         capture_output=True, text=True, check=True)
    import base64
    images = [Image.open(io.BytesIO(base64.b64decode(b))).convert("RGBA") for b in json.loads(res.stdout)]
    for (size, _), img in zip(SOURCES, images):
        assert img.size == (size, size), (size, img.size)
    OUT.parent.mkdir(exist_ok=True)
    largest = images[-1]
    largest.save(OUT, format="ICO", sizes=[(s, s) for s, _ in SOURCES], append_images=images[:-1])
    check = Image.open(OUT)
    print(f"wrote {OUT.name}: {sorted(check.info.get('sizes', []))} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
