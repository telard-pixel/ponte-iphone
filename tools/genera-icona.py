# Disegna l'icona di Ponte iPhone (telefono con freccia verso il basso) e scrive app.ico.
# Uso: python3 tools/genera-icona.py src/PonteIPhone/app.ico anteprima.png  (serve Pillow)
from PIL import Image, ImageDraw
import sys, struct, io
S=32; N=32*S
img=Image.new("RGBA",(N,N),(0,0,0,0)); d=ImageDraw.Draw(img)
def P(v): return v*S
d.rounded_rectangle([P(.5),P(.5),P(31.5),P(31.5)],radius=P(7.5),fill=(0x0b,0x63,0xb6,255))
# telefono
d.rounded_rectangle([P(9),P(4.5),P(23),P(27.5)],radius=P(3.2),outline=(255,255,255,255),width=int(P(2.1)))
d.rounded_rectangle([P(13.4),P(7),P(18.6),P(8.6)],radius=P(.8),fill=(255,255,255,255))
# freccia
w=int(P(2.2))
d.line([P(16),P(11.5),P(16),P(21)],fill=(255,255,255,255),width=w)
d.line([P(11.6),P(17),P(16.2),P(21.6)],fill=(255,255,255,255),width=w)
d.line([P(20.4),P(17),P(15.8),P(21.6)],fill=(255,255,255,255),width=w)
for cx,cy in ((16,11.5),(11.6,17),(20.4,17),(16,21.4)):
    d.ellipse([P(cx)-w/2,P(cy)-w/2,P(cx)+w/2,P(cy)+w/2],fill=(255,255,255,255))
sizes=[16,20,24,32,40,48,64,128,256]
imgs=[img.resize((s,s),Image.LANCZOS) for s in sizes]
img.resize((256,256),Image.LANCZOS).save(sys.argv[2])
def bmp_entry(im):
    w,h=im.size
    px=im.tobytes("raw","BGRA")
    rows=[px[y*w*4:(y+1)*w*4] for y in range(h)][::-1]
    mask_row=((w+31)//32)*4
    return struct.pack("<IiiHHIIiiII",40,w,h*2,1,32,0,0,0,0,0,0)+b"".join(rows)+b"\0"*(mask_row*h)
frames=[]
for s,im in zip(sizes,imgs):
    if s==256:
        b=io.BytesIO(); im.save(b,"PNG"); frames.append((s,b.getvalue()))
    else:
        frames.append((s,bmp_entry(im)))
out=struct.pack("<HHH",0,1,len(frames)); off=6+16*len(frames); body=b""
for s,data in frames:
    out+=struct.pack("<BBBBHHII",0 if s>=256 else s,0 if s>=256 else s,0,0,1,32,len(data),off+len(body)); body+=data
open(sys.argv[1],"wb").write(out+body)
