"""Generate platform icons from the approved Nian Link brand artwork.

Requires Pillow 12.2.0.  The 16/20/24 px Windows frames are independent,
vector-drawn simplifications of the same overlapping-display mark; they are
not edits to the PNG master.  Larger frames come from the master.  Run from
the repository root.  Outputs are overwritten only after validation.
"""

from hashlib import sha256
from io import BytesIO
from pathlib import Path
import struct

import PIL
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
MASTER = ROOT / "assets/brand/nian-link-logo-approved.png"
MASTER_SHA256 = "1e834aefe5c44cae64f1e24557ea33fa962ef14a3afeaa60159b994ed866bfc5"
ICO = ROOT / "assets/brand/NianLink.ico"
ICNS = ROOT / "src/DesktopToolbox.Mac/Assets/NianDeskLink.icns"
HEADER = ROOT / "assets/brand/NianLinkBrand.png"
MAC_HEADER = ROOT / "src/DesktopToolbox.Mac/Assets/NianLinkBrand.png"
PREVIEW = ROOT / "tests/.work/brand-tiny-comparison.png"
SIZES = (16, 20, 24, 32, 48, 64, 128, 256)


def tiny_icon(size: int) -> Image.Image:
    """Draw a legible two-monitor mark at small sizes without reading paths."""
    scale = 8
    image = Image.new("RGBA", (size * scale, size * scale))
    draw = ImageDraw.Draw(image)

    def rect(values):
        return tuple(round(value * size * scale / 16) for value in values)

    def width(value):
        return max(1, round(value * size * scale / 16))

    draw.rounded_rectangle(rect((0.8, 0.8, 15.2, 15.2)), radius=width(3.4), fill="#437FEF")
    draw.rounded_rectangle(rect((2.8, 3.6, 9.0, 8.6)), radius=width(0.8), outline="#F1F7FF", width=width(0.9))
    draw.line([rect((5.9, 8.6)), rect((5.9, 9.7))], fill="#F1F7FF", width=width(0.8))
    draw.line([rect((4.7, 9.7)), rect((7.0, 9.7))], fill="#F1F7FF", width=width(0.8))
    draw.rounded_rectangle(rect((6.4, 6.6, 12.6, 11.6)), radius=width(0.8), fill="#D6E7FF", outline="white", width=width(0.9))
    draw.line([rect((9.5, 11.6)), rect((9.5, 12.5))], fill="white", width=width(0.8))
    draw.line([rect((8.1, 12.5)), rect((10.9, 12.5))], fill="white", width=width(0.8))
    draw.line([rect((10.3, 3.9)), rect((12.5, 3.9)), rect((13.2, 4.6)), rect((13.2, 5.8))], fill="white", width=width(0.8), joint="curve")
    draw.line([rect((12.6, 5.2)), rect((13.2, 5.9)), rect((13.8, 5.2))], fill="white", width=width(0.8), joint="curve")
    draw.line([rect((5.1, 11.9)), rect((3.2, 11.9)), rect((2.5, 11.2)), rect((2.5, 10.1))], fill="white", width=width(0.8), joint="curve")
    draw.line([rect((1.9, 10.7)), rect((2.5, 10.0)), rect((3.1, 10.7))], fill="white", width=width(0.8), joint="curve")
    return image.resize((size, size), Image.Resampling.LANCZOS)


def image_bytes(image: Image.Image, kind: str, **options) -> bytes:
    stream = BytesIO()
    image.save(stream, format=kind, **options)
    return stream.getvalue()


def validate_icns(data: bytes) -> None:
    if len(data) < 8 or data[:4] != b"icns" or struct.unpack(">I", data[4:8])[0] != len(data):
        raise ValueError("Invalid ICNS header or length")
    offset, chunks = 8, set()
    while offset < len(data):
        if offset + 8 > len(data):
            raise ValueError("Truncated ICNS chunk")
        kind, length = data[offset:offset + 4], struct.unpack(">I", data[offset + 4:offset + 8])[0]
        if length < 8 or offset + length > len(data):
            raise ValueError("Invalid ICNS chunk length")
        chunks.add(kind)
        offset += length
    if offset != len(data) or not {b"ic07", b"ic08", b"ic09", b"ic10", b"ic11", b"ic12", b"ic13", b"ic14"}.issubset(chunks):
        raise ValueError("ICNS does not contain all expected image sizes")


def main() -> None:
    if PIL.__version__ != "12.2.0":
        raise RuntimeError("Icon generation requires Pillow 12.2.0 for reproducibility")
    if sha256(MASTER.read_bytes()).hexdigest() != MASTER_SHA256:
        raise ValueError("Approved Logo master hash mismatch")
    with Image.open(MASTER) as image:
        if image.mode != "RGBA" or image.width != image.height or image.width < 1024:
            raise ValueError("Logo master must be square RGBA and at least 1024px")
        master = image.copy()
    tiny = [tiny_icon(size) for size in (16, 20, 24)]
    # Supply every frame explicitly: Pillow's ICO fallback thumbnails the last
    # provided image, which can otherwise duplicate a smaller custom frame.
    frames = tiny + [master.resize((size, size), Image.Resampling.LANCZOS) for size in SIZES if size >= 32]
    ico = image_bytes(frames[-1], "ICO", sizes=[(size, size) for size in SIZES], append_images=frames[:-1])
    with Image.open(BytesIO(ico)) as encoded:
        if encoded.ico.sizes() != {(size, size) for size in SIZES}:
            raise ValueError("ICO image size list is incomplete")
        for size, expected in zip((16, 20, 24), tiny):
            if encoded.ico.getimage((size, size)).convert("RGBA").tobytes() != expected.tobytes():
                raise ValueError("ICO tiny frame does not match the vector mark")
    icns = image_bytes(master.resize((1024, 1024), Image.Resampling.LANCZOS), "ICNS")
    validate_icns(icns)
    mac_header = image_bytes(master.resize((128, 128), Image.Resampling.LANCZOS), "PNG")

    # Magnify a 16px comparison without altering either source asset.
    comparison = Image.new("RGB", (512, 188), "white")
    for index, (symbol, color) in enumerate(((master.resize((16, 16), Image.Resampling.LANCZOS), (255, 255, 255)),
                                              (tiny[0], (255, 255, 255)),
                                              (master.resize((16, 16), Image.Resampling.LANCZOS), (20, 30, 45)),
                                              (tiny[0], (20, 30, 45)))):
        backdrop = Image.new("RGBA", (16, 16), color + (255,))
        backdrop.alpha_composite(symbol)
        comparison.paste(backdrop.convert("RGB").resize((128, 128), Image.Resampling.NEAREST), (index * 128, 24))
    ImageDraw.Draw(comparison).text((8, 160), "master         vector         master         vector", fill="black")
    preview = image_bytes(comparison, "PNG")

    for path, data in ((ICO, ico), (ICNS, icns), (HEADER, mac_header), (MAC_HEADER, mac_header), (PREVIEW, preview)):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print("master", MASTER_SHA256)
    for path in (ICO, ICNS, HEADER, MAC_HEADER, PREVIEW):
        print(path.relative_to(ROOT).as_posix(), sha256(path.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
