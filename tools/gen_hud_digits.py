from PIL import Image, ImageDraw, ImageFont
OUT = r"C:\Program Files\Rockstar Games\Grand Theft Auto V Legacy\scripts\ExtendedLSC\hud"
SS=4; S=128*SS
def font(sz):
    for f in ["ariblk.ttf","arialbd.ttf","arial.ttf"]:
        try: return ImageFont.truetype(f,sz)
        except: pass
    return ImageFont.load_default()
fnt=font(int(86*SS))
for ch in list("0123456789")+["N","R"]:
    img=Image.new("RGBA",(S,S),(0,0,0,0)); d=ImageDraw.Draw(img)
    bb=d.textbbox((0,0),ch,font=fnt); w=bb[2]-bb[0]; h=bb[3]-bb[1]
    x=(S-w)/2-bb[0]; y=(S-h)/2-bb[1]
    # dark outline
    for ox in range(-3,4):
        for oy in range(-3,4):
            d.text((x+ox*SS,y+oy*SS),ch,font=fnt,fill=(12,10,10,255))
    # chrome fill (white core)
    d.text((x,y),ch,font=fnt,fill=(252,250,245,255))
    img=img.resize((128,128), Image.LANCZOS)
    name = ch if ch.isalpha() else ch
    img.save(OUT+rf"\gd_{name}.png")
print("gear digit sprites generated (gd_0..gd_9, gd_N, gd_R)")
