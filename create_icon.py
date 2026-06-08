from PIL import Image, ImageDraw, ImageFont

# 单张高清 256 图,交给 PIL 生成各尺寸 —— 品牌蓝圆角方块 + 居中白色粗体 S
SIZE = 256
img = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

margin = int(SIZE * 0.06)
radius = int(SIZE * 0.22)
draw.rounded_rectangle(
    [margin, margin, SIZE - margin - 1, SIZE - margin - 1],
    radius=radius, fill=(37, 99, 235, 255))

font = None
for name in ("segoeuib.ttf", "arialbd.ttf", "seguisb.ttf", "segoeui.ttf", "arial.ttf"):
    try:
        font = ImageFont.truetype(name, int(SIZE * 0.6))
        break
    except Exception:
        pass
if font is None:
    font = ImageFont.load_default()

draw.text((SIZE / 2, SIZE / 2), "S", font=font, fill=(255, 255, 255, 255), anchor="mm")

output_path = "Resources/STool.ico"
img.save(output_path, format='ICO',
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

print(f"图标已创建: {output_path}")
