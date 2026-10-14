"""Cover image and title/end cards for the CivicOps ACT III submission. Synthetic product imagery only."""
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import sys
B='/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'; R='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'
INK=(10,30,42); TEAL=(7,93,97); LIME=(216,245,143)
def base(title, sub, foot, shot, out):
    W,H=1920,1080; im=Image.new('RGB',(W,H),INK); d=ImageDraw.Draw(im)
    for y in range(H):  # subtle gradient
        d.line([(0,y),(W,y)],fill=(10,30+int(25*y/H),42+int(30*y/H)))
    s=Image.open(shot).convert('RGB'); s.thumbnail((960,820)); 
    sh=Image.new('RGB',(s.width+24,s.height+24),(0,0,0)).filter(ImageFilter.GaussianBlur(8))
    x,y=W-s.width-90,(H-s.height)//2; im.paste(sh,(x-6,y+6)); im.paste(s,(x,y))
    d=ImageDraw.Draw(im)
    d.rounded_rectangle([90,120,420,170],radius=24,fill=LIME); d.text((112,130),'CULLTRON · ACT III',font=ImageFont.truetype(B,26),fill=INK)
    yy=230
    for line in title: d.text((90,yy),line,font=ImageFont.truetype(B,76),fill='white'); yy+=92
    yy+=20
    for line in sub: d.text((90,yy),line,font=ImageFont.truetype(R,32),fill=(190,215,215)); yy+=46
    d.text((90,H-110),foot,font=ImageFont.truetype(R,24),fill=(150,180,180))
    im.save(out)
import os
media=os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','media')+'/'
base(['CivicOps'],['Accountable municipal operations.','Voice, SMS and WhatsApp to one case.','Agencies, SOS and a human','decision at every dispatch.'],
     'Synthetic demo data · simulated providers · culltron.app', media+'agency-board.png','cover.png')
base(['CivicOps','demo'],['Three-minute synthetic walkthrough.','All providers simulated.','No real people, places or calls.'],
     'Culltron CivicOps · AMD Developer Hackathon ACT III', media+'control-room.png','raw/title.png')
base(['Thank you'],['Human approval at every dispatch,','mutual-aid request and SOS.'],
     'Culltron CivicOps · synthetic demo', media+'map-control-room.png','raw/end.png')
