# Disegna l'icona di Ponte iPhone (telefono con la foto e bollino verde con la freccia, 21/10) e scrive app.ico.
# Uso: python3 tools/genera-icona.py src/PonteIPhone/app.ico anteprima.png  (serve Pillow)
from PIL import Image, ImageDraw
import sys, struct, io
S=32; N=32*S
W=(255,255,255,255)
def P(v): return v*S
def cap_line(d,pts,w):
    for p,q in zip(pts,pts[1:]): d.line([p,q],fill=W,width=int(w))
    for x,y in pts: d.ellipse([x-w/2,y-w/2,x+w/2,y+w/2],fill=W)
# sfondo: quadrato arrotondato azzurro sfumato verso il blu
grad=Image.new("RGBA",(N,N)); g=ImageDraw.Draw(grad)
top,bot=(0x34,0x8f,0xf2),(0x10,0x4e,0xb5)
for y in range(N):
    t=y/(N-1); g.line([(0,y),(N,y)],fill=tuple(int(a+(b-a)*t) for a,b in zip(top,bot))+(255,))
mask=Image.new("L",(N,N),0); ImageDraw.Draw(mask).rounded_rectangle([P(1),P(1),P(31),P(31)],radius=P(7),fill=255)
img=Image.new("RGBA",(N,N),(0,0,0,0)); img.paste(grad,(0,0),mask); d=ImageDraw.Draw(img)
# telefono con la foto (sole e monti) sullo schermo
d.rounded_rectangle([P(6),P(3.5),P(20.5),P(27.5)],radius=P(3.2),fill=W)
d.rounded_rectangle([P(7.8),P(5.3),P(18.7),P(25.7)],radius=P(1.8),fill=(0x10,0x5e,0xc8,255))
x0,y0,x1,y1=7.8,7,18.7,21; w,h=x1-x0,y1-y0
d.ellipse([P(x1-w*.36),P(y0+h*.14),P(x1-w*.14),P(y0+h*.14+w*.22)],fill=(0xff,0xd2,0x4a,255))
d.polygon([(P(x0),P(y1)),(P(x0),P(y1-h*.12)),(P(x0+w*.32),P(y0+h*.42)),(P(x0+w*.56),P(y1-h*.3)),(P(x0+w*.7),P(y1-h*.42)),(P(x1),P(y1-h*.1)),(P(x1),P(y1))],fill=W)
# bollino verde con la freccia verso il basso (scarica sul PC)
d.ellipse([P(16.3),P(16.3),P(30.3),P(30.3)],fill=(0x0c,0x4a,0xa8,255))
d.ellipse([P(17.6),P(17.6),P(29),P(29)],fill=(0x2e,0xc4,0x6b,255))
cap_line(d,[(P(23.3),P(19.9)),(P(23.3),P(26.2))],P(1.9))
cap_line(d,[(P(20.6),P(23.5)),(P(23.3),P(26.2)),(P(26),P(23.5))],P(1.9))
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
