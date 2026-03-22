from __future__ import annotations

from pathlib import Path
from typing import Iterable

from PIL import Image, ImageChops, ImageDraw, ImageFilter


ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 256, 512, 1024]
TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64]


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def blend(c1: tuple[int, int, int], c2: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(int(lerp(a, b, t)) for a, b in zip(c1, c2))


def render_base(size: int) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    scale = size / 1024.0
    corner = int(236 * scale)

    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shadow_draw = ImageDraw.Draw(shadow)
    shadow_draw.rounded_rectangle(
        [int(66 * scale), int(86 * scale), int(958 * scale), int(978 * scale)],
        radius=corner,
        fill=(0, 0, 0, 180),
    )
    shadow = shadow.filter(ImageFilter.GaussianBlur(max(4, int(30 * scale))))
    canvas.alpha_composite(shadow)

    shell = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shell_draw = ImageDraw.Draw(shell)
    shell_draw.rounded_rectangle(
        [int(84 * scale), int(84 * scale), int(940 * scale), int(940 * scale)],
        radius=corner,
        fill=(10, 18, 28, 24),
        outline=(150, 226, 255, 180),
        width=max(1, int(10 * scale)),
    )
    canvas.alpha_composite(shell)

    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    glow_draw.ellipse(
        [int(180 * scale), int(150 * scale), int(970 * scale), int(940 * scale)],
        fill=(46, 214, 255, 100),
    )
    glow = glow.filter(ImageFilter.GaussianBlur(max(6, int(82 * scale))))
    canvas.alpha_composite(glow)

    body = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    body_draw = ImageDraw.Draw(body)
    top = (20, 66, 104)
    bottom = (8, 18, 30)
    for y in range(size):
        t = y / max(1, size - 1)
        color = blend(top, bottom, t)
        alpha = int(lerp(210, 162, t))
        body_draw.rounded_rectangle(
            [int(92 * scale), y, int(932 * scale), y + 1],
            radius=corner,
            fill=(*color, alpha),
        )
    gloss = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gloss_draw = ImageDraw.Draw(gloss)
    gloss_draw.rounded_rectangle(
        [int(112 * scale), int(110 * scale), int(908 * scale), int(420 * scale)],
        radius=int(188 * scale),
        fill=(255, 255, 255, 42),
    )
    gloss = gloss.filter(ImageFilter.GaussianBlur(max(3, int(18 * scale))))
    body.alpha_composite(gloss)
    canvas.alpha_composite(body)

    return canvas


def render_glyph(size: int) -> Image.Image:
    glyph = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(glyph)
    scale = size / 1024.0

    primary = (120, 248, 255, 255)
    secondary = (96, 181, 255, 255)

    left_arm = [
        (int(326 * scale), int(774 * scale)),
        (int(455 * scale), int(250 * scale)),
        (int(540 * scale), int(250 * scale)),
        (int(442 * scale), int(774 * scale)),
    ]
    right_arm = [
        (int(584 * scale), int(774 * scale)),
        (int(512 * scale), int(250 * scale)),
        (int(598 * scale), int(250 * scale)),
        (int(708 * scale), int(774 * scale)),
    ]
    cross = [
        (int(390 * scale), int(566 * scale)),
        (int(422 * scale), int(472 * scale)),
        (int(633 * scale), int(472 * scale)),
        (int(670 * scale), int(566 * scale)),
    ]

    draw.polygon(left_arm, fill=primary)
    draw.polygon(right_arm, fill=secondary)
    draw.polygon(cross, fill=(218, 248, 255, 240))

    edge = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    edge_draw = ImageDraw.Draw(edge)
    edge_draw.line(
        [(int(455 * scale), int(250 * scale)), (int(510 * scale), int(250 * scale)), (int(584 * scale), int(774 * scale))],
        fill=(255, 255, 255, 84),
        width=max(1, int(18 * scale)),
        joint="curve",
    )
    edge_draw.line(
        [(int(422 * scale), int(472 * scale)), (int(633 * scale), int(472 * scale))],
        fill=(255, 255, 255, 78),
        width=max(1, int(12 * scale)),
    )
    edge = edge.filter(ImageFilter.GaussianBlur(max(1, int(6 * scale))))
    glyph.alpha_composite(edge)

    halo = glyph.filter(ImageFilter.GaussianBlur(max(3, int(24 * scale))))
    halo = ImageChops.multiply(halo, Image.new("RGBA", (size, size), (0, 255, 255, 210)))
    halo.putalpha(halo.getchannel("A").point(lambda value: min(255, int(value * 0.42))))
    halo.alpha_composite(glyph)
    return halo


def render_icon(size: int, tray: bool = False) -> Image.Image:
    base = render_base(size)
    glyph = render_glyph(size)
    icon = Image.alpha_composite(base, glyph)

    if tray:
        tray_mask = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        draw = ImageDraw.Draw(tray_mask)
        inset = int(size * 0.08)
        draw.rounded_rectangle(
            [inset, inset, size - inset, size - inset],
            radius=max(2, int(size * 0.22)),
            outline=(172, 238, 255, 188),
            width=max(1, int(size * 0.06)),
        )
        tray_mask = tray_mask.filter(ImageFilter.GaussianBlur(max(1, int(size * 0.04))))
        icon.alpha_composite(tray_mask)

    return icon


def save_family(master_sizes: Iterable[int], base_dir: Path, prefix: str, tray: bool = False) -> None:
    png_dir = base_dir / prefix
    png_dir.mkdir(parents=True, exist_ok=True)

    rendered: list[tuple[int, Image.Image]] = []
    for size in master_sizes:
        icon = render_icon(size, tray=tray)
        icon.save(png_dir / f"{size}.png")
        rendered.append((size, icon))

    ico_sizes = [(size, size) for size, _ in rendered if size <= 256]
    rendered[-1][1].save(
        base_dir / f"{prefix}.ico",
        sizes=ico_sizes,
    )
    rendered[-1][1].save(base_dir / f"{prefix}.png")


def main() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    output_dir = repo_root / "AlienFxLite.UI" / "Assets" / "Icons"
    output_dir.mkdir(parents=True, exist_ok=True)

    save_family(ICON_SIZES, output_dir, "app")
    save_family(TRAY_SIZES, output_dir, "tray", tray=True)
    print(f"Generated icon assets in {output_dir}")


if __name__ == "__main__":
    main()
