"""Render an XP-style 298×168 Cisco service pane. Requires Pillow at generation time."""
from pathlib import Path
from itertools import product
from PIL import Image, ImageDraw, ImageFont

assets = Path(__file__).resolve().parent.parent / 'Assets'
small = ImageFont.truetype(str(assets / 'xp/fonts/tahoma.ttf'), 11)
title = ImageFont.truetype(str(assets / 'xp/fonts/trebucbd.ttf'), 13)
control_icon = Image.open(assets / 'xp/control-panel.ico').convert('RGBA').resize((16, 16), Image.Resampling.LANCZOS)
power_icon = Image.open(assets / 'xp/power.ico').convert('RGBA').resize((20, 20), Image.Resampling.LANCZOS)


def gradient(draw, box, top, bottom):
    x1, y1, x2, y2 = box
    for y in range(y1, y2 + 1):
        t = (y - y1) / max(1, y2 - y1)
        c = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
        draw.line((x1, y, x2, y), fill=c)


for appliance, state in product(('lamp', 'aircon'), ('on', 'off', 'unavailable')):
    out = assets / appliance
    caption = 'Floor lamp' if appliance == 'lamp' else 'Air conditioner'
    on = state == 'on'
    image = Image.new('RGB', (298, 168), '#ece9d8')
    d = ImageDraw.Draw(image)
    d.fontmode = '1'  # Crisp XP-era text at native resolution.
    # Luna blue window frame and title bar.
    d.rectangle((0, 0, 297, 167), outline='#003cba', width=3)
    gradient(d, (3, 3, 294, 27), (62, 148, 255), (0, 66, 204))
    d.line((4, 3, 293, 3), fill='#83baff')
    image.paste(control_icon, (8, 7), control_icon)
    d.text((30, 7), caption, font=title, fill='#003399')
    d.text((29, 6), caption, font=title, fill='white')
    # Classic inset group box, with room for the lamp and colour controls.
    d.rectangle((12, 38, 285, 121), outline='#aca899')
    d.line((13, 122, 286, 122), fill='white')
    d.rectangle((20, 32, 75, 46), fill='#ece9d8')
    d.text((22, 33), 'Power', font=small, fill='#003399')
    image.paste(power_icon, (24, 56), power_icon)
    d.text((22, 84), 'Turned on' if on else 'Turned off' if state == 'off' else 'Unavailable', font=small, fill='#222222')
    d.text((212, 61), 'Tap lamp' if appliance == 'lamp' else 'Tap AC', font=small, fill='#222222')
    d.text((212, 77), 'to toggle', font=small, fill='#222222')
    # Raised lamp button stays inside its existing touch rectangle.
    d.rounded_rectangle((106, 43, 192, 123), radius=3, fill='#faf9f2', outline='#003c74')
    d.line((109, 45, 189, 45), fill='white')
    d.line((109, 120, 189, 120), fill='#d6d0b8', width=2)
    if appliance == 'lamp':
        if on:
            d.ellipse((117, 48, 181, 108), fill='#fff1b9')
        color = '#d6a42a' if on else '#758499'
        d.line((149, 79, 149, 113), fill=color, width=5)
        d.ellipse((130, 110, 168, 117), fill=color)
        d.polygon([(131, 50), (167, 50), (180, 80), (118, 80)], fill='#e8bc4f' if on else '#a3b2c4', outline=color)
        d.line((134, 53, 165, 53), fill='#fff2bc' if on else '#d8e1ed', width=2)
        d.line((126, 75, 173, 75), fill='#c79012' if on else '#7c8fa8', width=2)
    else:
        d.rounded_rectangle((113, 58, 185, 91), radius=4, fill='#f4f7fa', outline='#728ba4')
        d.line((117, 62, 181, 62), fill='white', width=2)
        d.rectangle((118, 80, 180, 87), fill='#55738d')
        for y in (81, 84, 87):
            d.line((120, y, 179, y), fill='#b5c5d5')
        d.rectangle((174, 69, 178, 72), fill='#40b83a' if on else '#999999')
        if on:
            for x in (132, 148, 164):
                d.line((x, 95, x - 3, 101, x + 2, 109), fill='#399bde', width=2)
    # XP raised colour buttons, with static colour chips and no active indicator.
    presets = [('#ffe0a3', ('Warm', 'white')), ('#e5f2ff', ('Cold', 'white')),
               ('#a020f0', ('Purple',)), ('#ff3333', ('Red',)), ('#64beff', ('Light', 'blue'))]
    for i, (swatch, lines) in enumerate(presets):
        x = 24 + i * 50
        d.rounded_rectangle((x, 129, x + 48, 155), radius=3, fill='#f5f4ea', outline='#003c74')
        d.line((x + 3, 131, x + 45, 131), fill='white')
        d.line((x + 3, 153, x + 45, 153), fill='#d6d0b8')
        if appliance == 'lamp':
            d.rectangle((x + 3, 134, x + 8, 150), fill=swatch, outline='#999999')
        else:
            lines = (f'{18 + i * 2}C',)
        for j, line in enumerate(lines):
            y = 136 + j * 11 if len(lines) == 2 else 142
            d.text((x + 28, y), line, anchor='mm', font=small, fill='#111111')
    image.save(out / f'{state}.png', optimize=True)
