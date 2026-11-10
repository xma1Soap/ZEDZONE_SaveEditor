"""
从游戏资源导出物品图标，拼成一张图（src/icons.png）+ 位置表（src/icons.tsv），打进修改器 exe。
用法: python -I tools/export_icons.py [游戏目录]
需要: pip install UnityPy Pillow；先跑 export_items.py 生成 src/items.tsv。

图标来源（Resources 下的 Sprite）：
- 一般物品：sprites/items/<分类>/<编号>
- 口罩：sprites/character/humanfacemask/<编号>
- 衣服：sprites/character/human/human<套装编号> 是整套贴图（64x96，按固定位置排：
  左下 头部、左中 上衣、右中 裤子、右下 鞋子），按部位裁出来，编号 = 套装编号 + 部位。
游戏更新加了新物品时和 export_items.py 一起重新跑。
"""
import sys, os, re
import UnityPy
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
GAME = sys.argv[1] if len(sys.argv) > 1 else r'D:\SteamLibrary\steamapps\common\ZED ZONE'
SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src')
DATA = os.path.join(GAME, 'ZEDZONE_Data')

items = {}
for line in open(os.path.join(SRC, 'items.tsv'), encoding='utf-8'):
    if line.startswith('#'):
        continue
    p = line.rstrip('\n').split('\t')
    items[int(p[0])] = p[7]   # 类名

env = UnityPy.load(os.path.join(DATA, 'globalgamemanagers'))
ggm = [f for n, f in env.files.items() if n.lower().replace('\\', '/').endswith('/globalgamemanagers')][0]
ext = [e.path for e in ggm.externals]
rm = [o for o in ggm.objects.values() if o.type.name == 'ResourceManager'][0].read_typetree()

# 只加载用到的资源文件
need = {}
for k, v in rm['m_Container']:
    if k.startswith('sprites/items/') or k.startswith('sprites/character/humanfacemask/') or re.match(r'sprites/character/human/human\d+$', k):
        need.setdefault(k, []).append(v)
fnames = sorted({os.path.basename(ext[v['m_FileID'] - 1]) for vs in need.values() for v in vs if v['m_FileID'] > 0})
env2 = UnityPy.load(*[os.path.join(DATA, n) for n in fnames])
files = {os.path.basename(n).lower(): f for n, f in env2.files.items()}


def load(path, want_texture=False):
    """按资源路径取图：优先 Sprite（已经裁好），没有再用 Texture2D。"""
    best = None
    for v in need.get(path, []):
        f = files.get(os.path.basename(ext[v['m_FileID'] - 1]).lower())
        o = f.objects.get(v['m_PathID']) if f else None
        if o is None:
            continue
        if o.type.name == 'Sprite' and not want_texture:
            return o.read().image.convert('RGBA')
        if o.type.name == 'Texture2D' and best is None:
            best = o.read().image.convert('RGBA')
    return best


def trim(im):
    box = im.getbbox()
    return im.crop(box) if box else None


icons = {}
by_id = {}
for k in need:
    if k.startswith('sprites/items/'):
        last = k.rsplit('/', 1)[1]
        if re.fullmatch(r'\d+', last):
            by_id.setdefault(int(last), k)
for iid, cls in sorted(items.items()):
    if cls == 'ItemAttr_Clothing':
        tex = load(f'sprites/character/human/human{iid}', want_texture=True)
        if tex is None or tex.size != (64, 96):
            continue
        # 区域 (x0, y0, x1, y1)，y 从上往下
        parts = [(0, 80, 16, 96), (0, 16, 32, 48), (32, 48, 48, 64), (32, 64, 48, 80)]
        for part, box in enumerate(parts):
            im = trim(tex.crop(box))
            if im is None and part == 0:
                im = trim(tex.crop((16, 80, 32, 96)))
            if im is not None:
                icons[iid + part] = im
        continue
    path = by_id.get(iid)
    im = load(path) if path else None
    if im is None and cls == 'ItemAttr_FaceMask':
        im = load(f'sprites/character/humanfacemask/{iid}')
    if im is not None:
        im = trim(im) or im
        icons[iid] = im

# 拼图（一行一行排，1 像素间隔）
W = 1024
x = y = rowh = 0
rects = {}
for iid, im in sorted(icons.items(), key=lambda t: (-t[1].size[1], t[0])):
    w, h = im.size
    if x + w > W:
        x, y, rowh = 0, y + rowh + 1, 0
    rects[iid] = (x, y, w, h)
    x += w + 1
    rowh = max(rowh, h)
H = y + rowh
atlas = Image.new('RGBA', (W, H), (0, 0, 0, 0))
for iid, (x, y, w, h) in rects.items():
    atlas.paste(icons[iid], (x, y))
atlas.save(os.path.join(SRC, 'icons.png'), optimize=True)
with open(os.path.join(SRC, 'icons.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('# 物品图标在 icons.png 里的位置，由 tools/export_icons.py 导出\n# id\tx\ty\tw\th\n')
    for iid in sorted(rects):
        f.write('%d\t%d\t%d\t%d\t%d\n' % ((iid,) + rects[iid]))
missing = sorted(i for i, c in items.items() if c != 'ItemAttr_Clothing' and i not in icons)
print('图标', len(rects), '个，拼图', W, 'x', H, '；没图标的物品', len(missing), missing)
