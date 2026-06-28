#!/usr/bin/env python3
"""Encode the BSMSO title logo PNG into an embedded GameCube ResTIMG (CMPR) header."""

from __future__ import annotations

import argparse
import struct
from collections import deque
from pathlib import Path

from PIL import Image
from reversebox.image.image_encoder import ImageEncoder
from reversebox.image.image_formats import ImageFormats


def swizzle_cmpr(linear_data: bytes, width: int, height: int) -> bytes:
    tile_width = 2
    tile_height = 2
    dxt_block_size = 8
    num_block_width = width // 8
    num_block_height = height // 8
    tile_size = tile_width * tile_height * dxt_block_size
    out = bytearray(len(linear_data))

    for ty in range(0, num_block_height, tile_height):
        for tx in range(0, num_block_width, tile_width):
            for y in range(tile_height):
                for x in range(tile_width):
                    src_block = ((ty + y) * num_block_width + (tx + x)) * dxt_block_size
                    dst_index = (
                        (ty // tile_height) * num_block_width // tile_width + (tx // tile_width)
                    ) * tile_size + (y * tile_width + x) * dxt_block_size
                    out[dst_index : dst_index + dxt_block_size] = linear_data[
                        src_block : src_block + dxt_block_size
                    ]
    return bytes(out)


def next_pow2(value: int) -> int:
    power = 1
    while power < value:
        power <<= 1
    return power


def remove_black_background(image: Image.Image, threshold: int = 24) -> Image.Image:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    pixels = rgba.load()

    def is_bg(x: int, y: int) -> bool:
        r, g, b, _a = pixels[x, y]
        return r <= threshold and g <= threshold and b <= threshold

    visited = [[False] * width for _ in range(height)]
    queue: deque[tuple[int, int]] = deque()

    for x in range(width):
        if is_bg(x, 0):
            queue.append((x, 0))
        if is_bg(x, height - 1):
            queue.append((x, height - 1))
    for y in range(height):
        if is_bg(0, y):
            queue.append((0, y))
        if is_bg(width - 1, y):
            queue.append((width - 1, y))

    while queue:
        x, y = queue.popleft()
        if x < 0 or y < 0 or x >= width or y >= height or visited[y][x]:
            continue
        if not is_bg(x, y):
            continue
        visited[y][x] = True
        pixels[x, y] = (0, 0, 0, 0)
        queue.append((x + 1, y))
        queue.append((x - 1, y))
        queue.append((x, y + 1))
        queue.append((x, y - 1))

    bbox = rgba.getbbox()
    if bbox:
        rgba = rgba.crop(bbox)
    return rgba


def pad_to_pow2(image: Image.Image, max_width: int = 512) -> tuple[Image.Image, int, int]:
    width, height = image.size
    if width > max_width:
        scale = max_width / width
        width = max_width
        height = max(1, int(height * scale))
        image = image.resize((width, height), Image.Resampling.LANCZOS)

    padded_w = next_pow2(width)
    padded_h = next_pow2(height)
    canvas = Image.new("RGBA", (padded_w, padded_h), (0, 0, 0, 0))
    canvas.paste(image, ((padded_w - width) // 2, (padded_h - height) // 2))
    return canvas, width, height


def build_restimg(image: Image.Image) -> bytes:
    encoder = ImageEncoder()
    encoded = encoder.encode_image(image, ImageFormats.CMPR)
    linear = bytes(encoded)
    swizzled = swizzle_cmpr(linear, image.width, image.height)

    header = struct.pack(
        ">BBHHBBBBHIBBBBBBBBBxhI",
        14,  # CMPR
        1,  # alpha enabled
        image.width,
        image.height,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        1,
        1,
        0,
        0,
        1,
        0,
        0x20,
    )
    return header + swizzled


def emit_header(
    data: bytes,
    tex_width: int,
    tex_height: int,
    content_width: int,
    content_height: int,
    output_path: Path,
) -> None:
    lines = [
        "#pragma once",
        "",
        "#include <Dolphin/types.h>",
        "",
        "namespace smso::title_logo {",
        f"inline constexpr u16 kTexWidth = {tex_width};",
        f"inline constexpr u16 kTexHeight = {tex_height};",
        f"inline constexpr u16 kContentWidth = {content_width};",
        f"inline constexpr u16 kContentHeight = {content_height};",
        "",
        "alignas(32) inline const u8 sTitleLogoTIMG[] = {",
    ]

    row = "    "
    for index, byte in enumerate(data):
        row += f"0x{byte:02x}, "
        if (index + 1) % 16 == 0:
            lines.append(row.rstrip())
            row = "    "
    if row.strip():
        lines.append(row.rstrip())

    lines.extend(
        [
            "};",
            "",
            "}  // namespace smso::title_logo",
            "",
        ]
    )
    output_path.write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    default_input = (
        repo_root
        / ".cursor/projects/c-Users-young-OneDrive-Desktop-SMSOBB/assets/"
        / "c__Users_young_AppData_Roaming_Cursor_User_workspaceStorage_empty-window_images_"
        / "Max_a_make_the_Better_text-51d1ee0c-ee95-4e52-95ea-fcb52fa1d6cc.png"
    )

    parser = argparse.ArgumentParser()
    parser.add_argument("--input", default=str(default_input))
    parser.add_argument("--output", default=str(repo_root / "module/src/p_title_logo.hxx"))
    parser.add_argument("--max-width", type=int, default=512)
    args = parser.parse_args()

    input_path = Path(args.input)
    if not input_path.exists():
        raise SystemExit(f"Input PNG not found: {input_path}")

    image = Image.open(input_path)
    image = remove_black_background(image)
    padded, content_w, content_h = pad_to_pow2(image, max_width=args.max_width)
    timg = build_restimg(padded)
    emit_header(
        timg,
        padded.width,
        padded.height,
        content_w,
        content_h,
        Path(args.output),
    )
    print(
        f"Wrote {args.output} ({padded.width}x{padded.height}, "
        f"content {content_w}x{content_h}, {len(timg)} bytes)"
    )


if __name__ == "__main__":
    main()
