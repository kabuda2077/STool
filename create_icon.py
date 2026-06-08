from PIL import Image, ImageDraw, ImageFont
import os

# 创建多个尺寸的图标
sizes = [16, 24, 32, 48, 64, 128, 256]
images = []

for size in sizes:
    # 创建图像
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 背景圆形 - 使用主色调蓝色
    margin = size // 10
    draw.ellipse([margin, margin, size - margin, size - margin],
                 fill=(37, 99, 235, 255))  # #2563EB

    # 绘制 S 字母
    font_size = int(size * 0.6)
    try:
        # 尝试使用系统字体
        font = ImageFont.truetype("segoeui.ttf", font_size)
    except:
        try:
            font = ImageFont.truetype("arial.ttf", font_size)
        except:
            font = ImageFont.load_default()

    # 计算文字居中位置
    text = "S"
    bbox = draw.textbbox((0, 0), text, font=font)
    text_width = bbox[2] - bbox[0]
    text_height = bbox[3] - bbox[1]

    x = (size - text_width) // 2 - bbox[0]
    y = (size - text_height) // 2 - bbox[1]

    # 绘制白色 S 字母
    draw.text((x, y), text, fill=(255, 255, 255, 255), font=font)

    images.append(img)

# 保存为 ICO 文件
output_path = "Resources/STool.ico"
images[0].save(output_path, format='ICO', sizes=[(img.width, img.height) for img in images])

print(f"图标已创建: {output_path}")
print(f"包含尺寸: {[img.size for img in images]}")
