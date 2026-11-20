import os
d=os.path.dirname(os.path.abspath(__file__))
s=open(os.path.join(d,'src.html')).read().replace('/*SOLAR*/',open(os.path.join(d,'solar.js')).read())
open(os.path.join(d,'index.html'),'w').write(s)
