"""Relative GPU cost per redraw (SwiftShader = CPU rasteriser, so only ratios are meaningful)."""
import asyncio, os, pathlib, sys
from playwright.async_api import async_playwright
INDEX = (pathlib.Path(__file__).resolve().parent.parent / 'web' / 'index.html').as_uri()
CASES = sys.argv[1:] or ['deco=foliage&wall=knockdown&scene=window', 'deco=none&wall=wood&scene=arch',
                         'deco=curtain&wall=limewash&scene=window', 'deco=none&wall=wood&scene=blinds']
async def main():
    async with async_playwright() as p:
        b = await p.chromium.launch(executable_path=os.environ.get('CHROME') or None, 
                                    args=['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
        ctx = await b.new_context(viewport={'width': 960, 'height': 600}, device_scale_factor=1, timezone_id='Europe/Berlin')
        pg = await ctx.new_page()
        for c in CASES:
            await pg.goto(f'{INDEX}?t=2026-06-21T17:00:00&{c}')
            await pg.wait_for_timeout(500)
            ms = await pg.evaluate('window.lull._bench(8)')
            print(f'{ms:8.1f} ms  {c}')
        await b.close()
asyncio.run(main())
