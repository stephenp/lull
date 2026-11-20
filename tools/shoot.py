import sys, asyncio, os, pathlib
from playwright.async_api import async_playwright
# usage: shoot.py outdir name=query ...
out=sys.argv[1]; os.makedirs(out,exist_ok=True)
url=(pathlib.Path(__file__).resolve().parent.parent / 'web' / 'index.html').as_uri()
async def main():
    async with async_playwright() as p:
        b=await p.chromium.launch(executable_path=os.environ.get('CHROME') or None, args=['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader','--ignore-gpu-blocklist'])
        ctx=await b.new_context(viewport={'width':1440,'height':900},timezone_id=os.environ.get('TZ_ID', 'Europe/Berlin'))
        pg=await ctx.new_page()
        logs=[]
        pg.on('console',lambda m: logs.append(m.text))
        for a in sys.argv[2:]:
            name,q=a.split('=',1)
            await pg.goto(url+'?'+q); await pg.wait_for_timeout(1200)
            await pg.screenshot(path=f'{out}/{name}.png')
        print('\n'.join(logs))
        print(await pg.evaluate("JSON.stringify({gl:!!document.createElement('canvas').getContext('webgl'), s:window.lull.getState()})"))
        await b.close()
asyncio.run(main())
