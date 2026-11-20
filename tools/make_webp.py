"""Build the README's animated hero (docs/preview.webp) from frames kept by render_video.py.

  python3 tools/render_video.py docs/preview.mp4 --size 1920x1080 --crf 16 --keep-frames
  python3 tools/make_webp.py /path/to/lull-frames-XXXX docs/preview.webp

Needs Pillow (pip3 install pillow). Uses Pillow because Homebrew's ffmpeg usually lacks a webp encoder.
"""
import argparse, glob
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument('frames', help='directory of f00000.png… frames from render_video.py --keep-frames')
ap.add_argument('out')
ap.add_argument('--width', type=int, default=1280)
ap.add_argument('--fps', type=int, default=30, help='frame rate the frames were rendered at')
ap.add_argument('--step', type=int, default=1, help='keep every Nth frame (2 halves size and fps)')
ap.add_argument('--quality', type=int, default=80)
a = ap.parse_args()

files = sorted(glob.glob(f'{a.frames}/f*.png'))[::a.step]
first = Image.open(files[0])
size = (a.width, round(a.width * first.height / first.width))
frames = [Image.open(f).convert('RGB').resize(size, Image.LANCZOS) for f in files]
frames[0].save(a.out, save_all=True, append_images=frames[1:], duration=round(1000 * a.step / a.fps),
               loop=0, quality=a.quality, method=6)
