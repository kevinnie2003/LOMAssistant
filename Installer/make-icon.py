"""生成安装器图标 Installer/app.ico：朱红圆角底，米白咖啡杯和三缕热气（颜色取自安装器界面）。
图标改了才需要重跑：python -I -X utf8 Installer/make-icon.py；需要 Pillow。
小尺寸（16、20、24）去掉热气，只留杯子，免得糊成一团。"""
import math
import os
from PIL import Image, ImageDraw

S = 1024  # 画大再缩，边缘平滑
ACCENT = (140, 47, 38)
CARD = (255, 253, 249)
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def draw(with_steam):
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = int(S * 0.04)
    d.rounded_rectangle((m, m, S - m, S - m), radius=int(S * 0.22), fill=ACCENT)

    # 没有热气时杯子放大、居中
    cx = S * 0.47
    cy, w, h = (S * 0.60, S * 0.46, S * 0.34) if with_steam else (S * 0.50, S * 0.56, S * 0.42)
    line = int(S * (0.05 if with_steam else 0.07))
    top, bottom = cy - h / 2, cy + h / 2
    d.polygon([(cx - w / 2, top), (cx + w / 2, top), (cx + w * 0.38, bottom), (cx - w * 0.38, bottom)], fill=CARD)
    d.ellipse((cx - w * 0.38, bottom - h * 0.12, cx + w * 0.38, bottom + h * 0.12), fill=CARD)
    hx = cx + w * 0.44
    d.ellipse((hx - w * 0.06, top + h * 0.12, hx + w * 0.26, top + h * 0.62), outline=CARD, width=line)
    d.rounded_rectangle((cx - w * 0.68, bottom + h * 0.16, cx + w * 0.68, bottom + h * 0.16 + line), radius=line // 2, fill=CARD)

    if with_steam:
        width = int(S * 0.035)
        for i, dx in enumerate((-w * 0.22, 0, w * 0.22)):
            height = S * (0.16 if i == 1 else 0.12)
            base = top - S * 0.04
            pts = [(cx + dx + math.sin(k / 40 * math.pi * 2) * S * 0.022, base - k / 40 * height) for k in range(41)]
            d.line(pts, fill=CARD, width=width, joint='curve')
    return img


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    full, plain = draw(True), draw(False)
    frames = [(plain if size <= 24 else full).resize((size, size), Image.LANCZOS) for size in SIZES]
    # Pillow 用第一帧按 sizes 缩放；这里每个尺寸各自画好，用 append_images 原样写进去
    frames[-1].save(os.path.join(here, 'app.ico'), format='ICO', sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    print('Installer/app.ico', SIZES)


if __name__ == '__main__':
    main()
