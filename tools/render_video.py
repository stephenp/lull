"""Render a sunrise-to-sunset time-lapse of the scene, frame by frame.

The sun's clock is time-lapsed across the day while the breeze animation runs in
real time (1/fps seconds per frame), so leaves sway naturally as the light moves.

  python3 tools/render_video.py out.mp4 --query "scene=window&deco=foliage&wall=knockdown&hud=1" \
      --lat 38.58 --lon -121.49 --date 2026-06-21 --tz America/Los_Angeles --seconds 20

Needs Playwright (Chromium) and ffmpeg. Set CHROME=/path/to/chrome to use a specific browser.
"""
import argparse, asyncio, os, pathlib, shutil, subprocess, sys, tempfile
from playwright.async_api import async_playwright

INDEX = (pathlib.Path(__file__).resolve().parent.parent / 'web' / 'index.html').as_uri()

FAKE_CLOCK = """
  (() => {
    const real = performance.now.bind(performance);
    let fake = null;
    performance.now = () => fake == null ? real() : fake;
    window.__setAnimClock = ms => { fake = ms; };
  })();
"""

async def main(a):
    w, h = map(int, a.size.split('x'))
    frames = int(a.seconds * a.fps)
    tmp = pathlib.Path(tempfile.mkdtemp(prefix='lull-frames-'))
    async with async_playwright() as p:
        b = await p.chromium.launch(executable_path=os.environ.get('CHROME') or None,
                                    args=['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'])
        ctx = await b.new_context(viewport={'width': w, 'height': h}, device_scale_factor=1, timezone_id=a.tz)
        await ctx.add_init_script(FAKE_CLOCK)
        pg = await ctx.new_page()
        await pg.goto(f'{INDEX}?lat={a.lat}&lon={a.lon}&t={a.date}T12:00:00&{a.query}')
        await pg.wait_for_timeout(800)
        st = await pg.evaluate('window.lull.getState()')
        start = st['sunrise'] - a.pad * 60000
        end = st['sunset'] + a.pad * 60000
        print(f'{frames} frames, {(end - start) / 3600000:.1f} h of daylight', file=sys.stderr)
        for i in range(frames):
            t = start + (end - start) * i / max(1, frames - 1)
            await pg.evaluate("""([anim, t]) => new Promise(res => {
                window.__setAnimClock(anim);
                window.lull.setConfig({ timeOverride: t, speed: 0 });
                requestAnimationFrame(() => requestAnimationFrame(res));
              })""", [20000 + i * 1000 / a.fps, t])
            await pg.screenshot(path=str(tmp / f'f{i:05d}.png'))
            if i % 30 == 0:
                print(f'  frame {i}/{frames}', file=sys.stderr)
        await b.close()
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-framerate', str(a.fps), '-i', str(tmp / 'f%05d.png'),
                    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', str(a.crf), '-pix_fmt', 'yuv420p',
                    '-movflags', '+faststart', a.out], check=True)
    if a.keep_frames:
        print(f'frames kept in {tmp}', file=sys.stderr)
    else:
        shutil.rmtree(tmp)

if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--query', default='scene=window&deco=foliage&wall=knockdown&hud=1')
    ap.add_argument('--lat', type=float, default=38.58)
    ap.add_argument('--lon', type=float, default=-121.49)
    ap.add_argument('--date', default='2026-06-21')
    ap.add_argument('--tz', default='America/Los_Angeles')
    ap.add_argument('--seconds', type=float, default=20)
    ap.add_argument('--fps', type=int, default=30)
    ap.add_argument('--size', default='1280x720')
    ap.add_argument('--pad', type=float, default=35, help='minutes before sunrise / after sunset')
    ap.add_argument('--crf', type=int, default=20)
    ap.add_argument('--keep-frames', action='store_true')
    asyncio.run(main(ap.parse_args()))
