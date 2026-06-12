import math
from PIL import Image, ImageDraw, ImageFont
OUT = r"C:\Program Files\Rockstar Games\Grand Theft Auto V Legacy\scripts\ExtendedLSC\hud"
SS=2; S=512*SS
cx=cy=S/2
# THINNER band; numbers on dark. Outer ring reserved for the cyan NOS fill bar.
R_in, R_out = S*0.355, S*0.452     # main RPM band (thinner than before)
A_lo, A_hi = 235, 125
def v2a(v): return A_lo + (A_hi-A_lo)*(v/10.0)
def pol(r,a): a=math.radians(a); return (cx+r*math.cos(a), cy-r*math.sin(a))
def font(sz):
    for f in ["ariblk.ttf","arialbd.ttf","arial.ttf"]:
        try: return ImageFont.truetype(f,sz)
        except: pass
    return ImageFont.load_default()
def lerp(c1,c2,t): return tuple(int(c1[i]+(c2[i]-c1[i])*t) for i in range(3))

# ---- FACE: dark band w/ a THIN warm gradient rail on the inner edge + readable white numbers ----
face=Image.new("RGBA",(S,S),(0,0,0,0)); d=ImageDraw.Draw(face)
n=620
darkstops=[(0.0,(46,44,34)),(0.5,(30,28,26)),(0.8,(26,18,18)),(1.0,(34,16,16))]
railstops=[(0.0,(196,200,90)),(0.35,(150,140,70)),(0.65,(120,90,60)),(0.82,(210,70,60)),(1.0,(235,70,64))]
def grad(stops,t):
    for j in range(len(stops)-1):
        if stops[j][0]<=t<=stops[j+1][0]:
            tt=(t-stops[j][0])/(stops[j+1][0]-stops[j][0]+1e-6); return lerp(stops[j][1],stops[j+1][1],tt)
    return stops[-1][1]
railW=(R_out-R_in)*0.30
for i in range(n):
    v0=10.0*i/n; v1=10.0*(i+1)/n; t=v0/10.0
    a0=math.radians(v2a(v0)); a1=math.radians(v2a(v1))
    # dark base band
    col=grad(darkstops,t)
    p=[(cx+R_in*math.cos(a0),cy-R_in*math.sin(a0)),(cx+R_out*math.cos(a0),cy-R_out*math.sin(a0)),
       (cx+R_out*math.cos(a1),cy-R_out*math.sin(a1)),(cx+R_in*math.cos(a1),cy-R_in*math.sin(a1))]
    d.polygon(p, fill=col+(255,))
    # thin warm rail on inner edge
    rc=grad(railstops,t); ri0=R_in; ri1=R_in+railW
    pr=[(cx+ri0*math.cos(a0),cy-ri0*math.sin(a0)),(cx+ri1*math.cos(a0),cy-ri1*math.sin(a0)),
        (cx+ri1*math.cos(a1),cy-ri1*math.sin(a1)),(cx+ri0*math.cos(a1),cy-ri0*math.sin(a1))]
    d.polygon(pr, fill=rc+(255,))
    # redline ticks (outer) 8.3..10
    if v0>=8.3:
        rr0=R_out-railW*0.8
        pp=[(cx+rr0*math.cos(a0),cy-rr0*math.sin(a0)),(cx+R_out*math.cos(a0),cy-R_out*math.sin(a0)),
            (cx+R_out*math.cos(a1),cy-R_out*math.sin(a1)),(cx+rr0*math.cos(a1),cy-rr0*math.sin(a1))]
        d.polygon(pp, fill=(228,60,60,255))
# ticks + numbers
fnum=font(int(30*SS))
for v in range(0,11):
    p1=pol(R_in+railW,v2a(v)); p2=pol(R_out-2*SS,v2a(v))
    d.line([p1,p2], fill=(235,235,228,235), width=3*SS)
    tp=pol(R_in+ (R_out-R_in)*0.62, v2a(v)); s=str(v); bb=d.textbbox((0,0),s,font=fnum)
    for ox,oy in [(-2,0),(2,0),(0,-2),(0,2)]:
        d.text((tp[0]-(bb[2]-bb[0])/2+ox*SS, tp[1]-(bb[3]-bb[1])/2-bb[1]+oy*SS), s, font=fnum, fill=(15,12,12,255))
    d.text((tp[0]-(bb[2]-bb[0])/2, tp[1]-(bb[3]-bb[1])/2-bb[1]), s, font=fnum, fill=(250,248,242,255))
for k in range(0,101):
    v=k/10.0
    if abs(v*10%10)<0.001: continue
    p1=pol(R_out-6*SS,v2a(v)); p2=pol(R_out-2*SS,v2a(v))
    d.line([p1,p2], fill=(180,182,176,150), width=1*SS+1)
face=face.resize((512,512), Image.LANCZOS); face.save(OUT+r"\tach_face.png")

# ---- SHORT needle that rides the arch: a thin chrome sliver crossing the band at the value ----
nd=Image.new("RGBA",(S,S),(0,0,0,0)); dn=ImageDraw.Draw(nd)
# drawn pointing +X, occupying the band radius span so when centered+rotated it sits on the arch
r_a=R_in-6*SS; r_b=R_out+4*SS; hw=4.0*SS
ax=cx+r_a; bx=cx+r_b
dn.polygon([(ax,cy-hw*0.5),(bx,cy-hw),(bx+8*SS,cy),(bx,cy+hw),(ax,cy+hw*0.5)], fill=(120,126,136,255))
dn.polygon([(ax+2*SS,cy-hw*0.25),(bx-2*SS,cy-hw*0.5),(bx+5*SS,cy),(bx-2*SS,cy+hw*0.5),(ax+2*SS,cy+hw*0.25)], fill=(252,253,255,255))
nd=nd.resize((512,512), Image.LANCZOS); nd.save(OUT+r"\tach_needle.png")

# ---- NOS dot (soft cyan) for the outer fused bar ----
dot=Image.new("RGBA",(64,64),(0,0,0,0)); dd=ImageDraw.Draw(dot)
dd.ellipse([10,10,54,54], fill=(70,210,245,255))
dd.ellipse([18,18,46,46], fill=(150,235,255,255))
dot.save(OUT+r"\nos_dot.png")
# unlit dot (dim) for the empty track
ud=Image.new("RGBA",(64,64),(0,0,0,0)); du=ImageDraw.Draw(ud)
du.ellipse([14,14,50,50], fill=(40,55,62,200))
ud.save(OUT+r"\nos_dot_off.png")

# ---- gear tab (dark plate, chrome rim) ----
gt=Image.new("RGBA",(256*SS,256*SS),(0,0,0,0)); dg=ImageDraw.Draw(gt)
dg.rounded_rectangle([24*SS,24*SS,232*SS,232*SS], radius=46*SS, fill=(30,26,28,255), outline=(205,208,214,255), width=4*SS)
gt=gt.resize((256,256), Image.LANCZOS); gt.save(OUT+r"\gear_tab.png")
print("v3 art: thin band, dark readable numbers, short arch needle, nos dots. R_in=%.2f R_out=%.2f"%(R_in/S,R_out/S))
