"""Build the Holodex UI Lab page.

Reads src/ (body.html, data.js, app.js, emblem.json), the JSON beside this folder's README (sizes, tokens, maps),
the element maps in renders/, and the @ark/tokens and @ark/ui CSS from an Ark checkout. Writes template.html
(placeholders left in) and dist/index.html (the page that is published).

Run: python tools/ui-lab/src/build.py [--ark PATH] [--ark-rev REV]
"""
import argparse,json,os,re,subprocess
from pathlib import Path
SRC=Path(__file__).resolve().parent          # tools/ui-lab/src
LAB=SRC.parent                               # tools/ui-lab
REPO=LAB.parent.parent                       # the holodex checkout
ap=argparse.ArgumentParser(description=__doc__.split('\n')[0])
ap.add_argument('--ark',default=str(REPO.parent/'ark'),help='Ark checkout to copy @ark/tokens and @ark/ui CSS from (default: ../ark beside the holodex checkout)')
ap.add_argument('--ark-rev',default=None,help='revision label written into the CSS comments (default: the short HEAD of the Ark checkout)')
args=ap.parse_args()
ARK=Path(args.ark).resolve()
if not (ARK/'packages'/'tokens'/'src').is_dir() or not (ARK/'packages'/'ui'/'src').is_dir():
    raise SystemExit('no Ark checkout at %s (expected packages/tokens/src and packages/ui/src); pass --ark PATH'%ARK)
rev=args.ark_rev
if not rev:
    try: rev=subprocess.run(['git','-C',str(ARK),'rev-parse','--short=8','HEAD'],capture_output=True,text=True,check=True).stdout.strip()
    except Exception: rev='unknown'
def rd(p):
    try: return Path(p).read_text(encoding='utf-8')
    except FileNotFoundError: raise SystemExit('missing %s; if it is an Ark file, point --ark at a checkout of current origin/main'%p)
def wr(p,s): Path(p).parent.mkdir(parents=True,exist_ok=True); Path(p).write_text(s,encoding='utf-8',newline='\n')
A=str(ARK)+'/'
R=LAB/'renders'
tok=['colors','spacing','chassis','typography']
ui=['actions/button','actions/text-link','actions/context-switcher','controls/text-field','controls/search-field','controls/universal-widget','status/led','status/badge','status/lifecycle-tag','surfaces/panel','layout/workspace-frame','data/inspectable-row','data/gala-thread','brand/eyebrow','layout/accordion','layout/page-header','surfaces/aura','surfaces/stage-canvas','surfaces/trace-beam','surfaces/registration-marks','brand/ark-mark','surfaces/edge-photons']
def strip(c): return re.sub(r'\n\s*\n+','\n',re.sub(r'/\*.*?\*/','',c,flags=re.S)).strip()
css='/* @ark/tokens (origin/main '+rev+'): '+', '.join(tok)+' */\n'+'\n'.join(strip(rd(A+'packages/tokens/src/%s.css'%t)) for t in tok)
css+='\n/* @ark/ui (origin/main '+rev+'), copied unchanged: '+', '.join(ui)+' */\n'+'\n'.join(strip(rd(A+'packages/ui/src/%s.css'%t)) for t in ui)
uicss=rd(A+'packages/ui/src/ui.css'); tail=uicss[uicss.index('/* Phosphor disclosure aura'):]
css+='\n'+strip(tail)
# Bandit, Ryozan's Phosphor Lite: the preview copy of its tokens kept in the Shell Inventory page (SHELL_TOKENS.bandit).
inv=rd(A+'packages/ui/src/surfaces/shell-inventory/shell-inventory.html')
m=re.search(r"SHELL_TOKENS = \{\s*bandit: \{(.*?)\}\s*\};",inv,re.S)
pairs=re.findall(r"'(--[\w-]+)':\s*'([^']*)'",m.group(1)); assert len(pairs)>15
css+='\n/* Bandit (SH-02, Phosphor Lite), preview copy of the @ryozan/ui tokens from the Shell Inventory page */\n:root[data-shell="bandit"]{'+';'.join(k+':'+v for k,v in pairs)+'}'
assert 'url("http' not in css and 'url(http' not in css and '@import' not in css
head='''<title>Holodex UI Lab</title>
<meta name="claude:viewport-fit-sides" content="cover">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo+Black&family=Archivo:wght@600;700;800&family=Inter:wght@400;600;800&family=JetBrains+Mono:wght@400;500;600&family=Space+Grotesk:wght@400;500;600&display=swap">
'''
em=json.loads(rd(SRC/'emblem.json'))
mark='<span class="rp-mark" role="img" aria-label="Ark Omnitek" data-state="active"><svg aria-hidden="true" viewBox="'+em['ARK_EMBLEM_VIEWBOX']+'"><g class="rp-mark__cube"><path d="'+em['ARK_EMBLEM_CUBE_PATH']+'"/>'+''.join('<path d="'+d+'"/>' for d in em['STUBS'])+'</g><path class="rp-mark__globe" d="'+em['ARK_EMBLEM_GLOBE_PATH']+'"/></svg></span>'
els=sum(len(json.loads(rd(R/f))['elements']) for f in sorted(os.listdir(R)) if f.endswith('.json'))
invj=json.dumps({'pages':len(json.loads(rd(LAB/'sizes.json'))),'elements':els,'maps':len(json.loads(rd(LAB/'maps.json')))})
body=rd(SRC/'body.html'); data=rd(SRC/'data.js'); app=rd(SRC/'app.js')
tpl=head+'<style>\n'+css+'\n</style>\n'+body+'\n<script>\n'+data+app+'</script>\n'
tpl=tpl.replace('__ARKMARK__',mark)
wr(LAB/'template.html',tpl)
out=tpl.replace('__INV__',invj).replace('__SIZES__',rd(LAB/'sizes.json')).replace('__TOKENS__',rd(LAB/'tokens.json')).replace('__MAPS__',rd(LAB/'maps.json'))
wr(LAB/'dist'/'index.html',out)
bad=[out[max(0,m.start()-50):m.start()+20] for m in re.finditer('[\u2013\u2014]',out)]
print('ark',ARK,'rev',rev)
print(len(out),len(css),'dashes',len(bad),bad[:4])
mine=body[body.index('<style>'):body.index('</style>')]+app
print('hex in own code:',re.findall(r'#[0-9a-fA-F]{3,6}\b',mine)[:8]); print('px in own css:',re.findall(r'[\d.]+px',body[body.index('<style>'):body.index('</style>')]))
