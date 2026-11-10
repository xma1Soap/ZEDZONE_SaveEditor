"""
从游戏资源导出物品表，给修改器用（编号、中文名、格子尺寸、堆叠上限、类型、是否隐藏、类名、背包容量、新建物品要用的属性）。
用法: python -I tools/export_items.py [游戏目录] [Il2CppDumper 的 DummyDll 目录（或设环境变量 ZEDZONE_DUMMYDLL）] [输出文件]
需要: pip install UnityPy TypeTreeGeneratorAPI
游戏更新加了新物品时重新跑一遍，再编译修改器（输出文件会被打进 exe）。

物品配置是 Resources/properties/item/<编号> 下的 ItemAttr 系列 ScriptableObject。
最后一列 feat 是物品特性 itemFeatures（ItemFeatureType 编号），装备栏判断能装进哪个槽要用。
前面的基础字段（到 itemType 为止）结构固定，直接按字节读；
背包容量 inventorySize 在子类后部，借 UnityPy + 类型树生成器整对象读。
"""
import sys, os, struct
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

sys.stdout.reconfigure(encoding='utf-8')
GAME = sys.argv[1] if len(sys.argv) > 1 else r'D:\SteamLibrary\steamapps\common\ZED ZONE'
DUMMY = sys.argv[2] if len(sys.argv) > 2 else os.environ.get('ZEDZONE_DUMMYDLL', '')
if not DUMMY or not os.path.isdir(DUMMY):
    raise SystemExit('需要 Il2CppDumper 生成的 DummyDll 目录：作为第 2 个参数传入，或者设环境变量 ZEDZONE_DUMMYDLL')
OUT = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'items.tsv')
DATA = os.path.join(GAME, 'ZEDZONE_Data')

gen = TypeTreeGenerator('2023.1.18f1')
gen.load_local_dll_folder(DUMMY)
env = UnityPy.load(os.path.join(DATA, 'globalgamemanagers'),
                   os.path.join(DATA, 'globalgamemanagers.assets'),
                   os.path.join(DATA, 'resources.assets'))
env.typetree_generator = gen


def file_named(suffix):
    return [f for n, f in env.files.items() if n.lower().replace('\\', '/').endswith(suffix)][0]


ggm = file_named('/globalgamemanagers')
gga = file_named('/globalgamemanagers.assets')
res = file_named('/resources.assets')

rm = [o for o in ggm.objects.values() if o.type.name == 'ResourceManager'][0].read_typetree()
entries = [(k, v) for k, v in rm['m_Container'] if k.startswith('properties/item/')]


class Reader:
    def __init__(self, b, pos=0):
        self.b, self.p = b, pos

    def align(self):
        self.p = (self.p + 3) & ~3

    def i32(self):
        v = struct.unpack_from('<i', self.b, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        v = struct.unpack_from('<f', self.b, self.p)[0]
        self.p += 4
        return v

    def u8(self):
        v = self.b[self.p]
        self.p += 1
        return v

    def s(self):
        n = self.i32()
        if n < 0 or self.p + n > len(self.b):
            raise ValueError('bad string')
        v = self.b[self.p:self.p + n].decode('utf-8', 'replace')
        self.p += n
        self.align()
        return v


script_cache = {}


def script_name(raw):
    # MonoBehaviour 开头: m_GameObject(12) m_Enabled(1,对齐到4) m_Script(FileID int32, PathID int64)
    fid, pid = struct.unpack_from('<iq', raw, 16)
    key = (fid, pid)
    if key not in script_cache:
        so = gga.objects.get(pid) if fid == 1 else None   # FileID 1 = globalgamemanagers.assets
        script_cache[key] = so.read_typetree()['m_ClassName'] if so else ''
    return script_cache[key]


def base_fields(raw):
    r = Reader(raw, 28)
    t = {'m_Name': r.s(), 'modGuid': r.s(), 'itemId': r.i32(), 'itemName': r.s(), 'itemDescription': r.s(),
         'itemName_Runtime': r.s(), 'itemDescription_WithLanguage': r.s(), 'maxGenerationNumber': r.i32()}
    t['itemSize'] = {'x': r.i32(), 'y': r.i32()}
    t['stackNumber'] = r.i32()
    t['weight'] = r.f32()
    t['itemPrice'] = r.f32()
    t['itemRarity'] = r.f32()
    t['hiddenItem'] = r.u8()
    r.align()
    t['itemType'] = r.i32()
    t['toolType'] = r.i32()
    n = r.i32()
    if n < 0 or n > 64:
        raise ValueError('bad itemFeatures')
    t['itemFeatures'] = [r.i32() for _ in range(n)]
    return t


def clean(s):
    return (s or '').replace('\t', ' ').replace('\r', ' ').replace('\n', ' ').strip()


def num(v):
    return ('%.3f' % v).rstrip('0').rstrip('.') if isinstance(v, float) else str(v)


def extras(cls, f):
    """新建物品时要照游戏 ItemManager.GetRandomDataByAttr 填的属性，写成 key=value 用逗号隔开。"""
    if cls == 'ItemAttr_Food':
        return f"perish={num(f['perishTime'])}" if f and f.get('perishTime') else ''   # 保质期（游戏天数）
    if cls in ('ItemAttr', 'ItemAttr_Liquid', 'ItemAttr_FaceMask', 'ItemAttr_Throwable',
               'ItemAttr_Arrow', 'ItemAttr_Deployable', 'ItemAttr_Cloak'):
        return ''          # 这些只用通用字段（食物的生产时间用存档里的游戏时间）
    if f is None:
        return None
    if cls == 'ItemAttr_MeleeWeapon':
        return f"hp={num(f['hp'])}"
    if cls == 'ItemAttr_RangedWeapon':
        air = sum(f['airPressureConfig'].values()) if f.get('isAirPowerd') else 0
        return (f"hp={num(f['hp'])},feed={f['feedingType']},auto={1 if f['gunActionType'] == 0 else 0},"
                f"mag={f['defaultMagazineId']},air={num(float(air))},slot={f['weaponSlotSize']},cap={f['magazineSize']},wt={f['weaponType']}")
    if cls == 'ItemAttr_Magazine':
        return f"cap={f['magazineSize']}"
    if cls == 'ItemAttr_GunPart':
        return f"dur={num(f['firingDurability'])}"
    if cls == 'ItemAttr_Ammo':
        return f"n={f['ammoNumber']}"
    if cls == 'ItemAttr_Container':
        return f"cap={num(f['totalCapacity'])}"
    if cls == 'ItemAttr_Backpack':
        feats = f.get('itemFeatures') or []
        out = ['appliance=1'] if (35 in feats or 36 in feats) else []
        if f.get('isShellCollector') or f.get('isSafebox') or f.get('isAppliance'):
            out.append('noequip=1')     # 游戏 CharacterEquipmentPanel.GetEquipRoute：这三种背包不能背
        return ','.join(out)
    if cls == 'ItemAttr_Clothing':
        parts = []
        for k in ('headAttr', 'torseAttr', 'legAttr', 'footAttr'):
            v = f[k]['inventorySize']
            parts.append(f"{v['x']}x{v['y']}")
        return 'pockets=' + '/'.join(parts)
    return ''


rows, classes, mismatch, full_fail = [], {}, 0, 0
for path, ptr in entries:
    obj = res.objects.get(ptr['m_PathID'])
    if obj is None:
        continue
    raw = obj.get_raw_data()
    cls = script_name(raw)
    if not cls.startswith('ItemAttr') or cls == 'ItemAttrs':
        continue
    t = base_fields(raw)
    classes[cls] = classes.get(cls, 0) + 1
    full = None
    try:
        full = obj.read_typetree()
    except Exception:
        full_fail += 1
    if full is not None:
        # 用整对象读的结果核对手动读的基础字段
        for k in ('itemId', 'itemName', 'stackNumber', 'itemType', 'hiddenItem', 'itemFeatures'):
            if full.get(k) != t[k]:
                mismatch += 1
                print('不一致', path, k, full.get(k), t[k])
        if full.get('itemSize') != t['itemSize']:
            mismatch += 1
            print('不一致', path, 'itemSize', full.get('itemSize'), t['itemSize'])
    bag = ''
    if cls == 'ItemAttr_Backpack':
        v = (full or {}).get('inventorySize')
        if not v:
            raise SystemExit(f'背包 {path} 读不出容量')
        bag = f"{v['x']}x{v['y']}"
    extra = extras(cls, full)
    if extra is None:
        raise SystemExit(f'{path} ({cls}) 读不出新建物品要用的属性')
    rows.append((t['itemId'], clean(t['itemName']), t['itemSize']['x'], t['itemSize']['y'], t['stackNumber'],
                 t['itemType'], t['hiddenItem'], cls, bag, round(t['weight'], 3), extra,
                 ','.join(str(x) for x in t['itemFeatures'])))

rows.sort()
ids = [r[0] for r in rows]
if len(ids) != len(set(ids)):
    raise SystemExit('物品编号有重复')
with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    f.write('# 隔离区物品表，由 tools/export_items.py 从游戏资源导出\n')
    f.write('# id\tname\tw\th\tstack\ttype\thidden\tclass\tbag\tweight\textra\tfeat\n')
    for r in rows:
        f.write('\t'.join(str(x) for x in r) + '\n')
print('物品', len(rows), '个；整对象读失败', full_fail, '个（只影响核对）；基础字段不一致', mismatch, '处')
print('类:', classes)
print('写到', os.path.abspath(OUT))
