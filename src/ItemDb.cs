using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ZedZoneSaveEditor
{
    /// <summary>游戏里的物品类型（ItemType）。</summary>
    internal enum ItemKind
    {
        Material = 0, Food = 1, FaceMask = 2, MeleeWeapon = 3, RangedWeapon = 4, Clothing = 5, Ammo = 6, Backpack = 7,
        Throwable = 8, Magazine = 9, Arrow = 10, GunPart = 11, Deployable = 12, Container = 13, Liquid = 14, Cloak = 15,
    }

    /// <summary>一种物品的配置（从游戏资源导出的 items.tsv）。</summary>
    internal sealed class ItemInfo
    {
        public int Id;
        public string Name;
        public int W = 1, H = 1;
        public int Stack = 1;
        public ItemKind Kind;
        public bool Hidden;
        public string Class = "";
        public int BagW, BagH;            // 背包类：里面的格子数
        public float Weight;
        public bool Known = true;         // false = 物品表里没有（模组物品或新版本加的）
        public int ClothPart = -1;        // 衣服：0 头 1 上衣 2 裤子 3 鞋
        public readonly Dictionary<string, string> Extra = new Dictionary<string, string>();
        public readonly HashSet<int> Features = new HashSet<int>();   // 物品特性 ItemFeatureType（照明工具、头饰……）

        public float ExtraFloat(string key, float def = 0f) =>
            Extra.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;

        public int ExtraInt(string key, int def = 0) => (int)Math.Round(ExtraFloat(key, def));

        public bool Stackable => Stack > 1;

        public string KindName => ItemDb.KindName(Kind);
    }

    internal static class ItemDb
    {
        public static readonly string[] ClothPartNames = { "头部", "上衣", "裤子", "鞋子" };

        private static Dictionary<int, ItemInfo> _byId;
        private static List<ItemInfo> _addable;

        public static string KindName(ItemKind k)
        {
            switch (k)
            {
                case ItemKind.Material: return "材料/工具";
                case ItemKind.Food: return "食物/药品";
                case ItemKind.FaceMask: return "口罩";
                case ItemKind.MeleeWeapon: return "近战武器";
                case ItemKind.RangedWeapon: return "枪械/弓弩";
                case ItemKind.Clothing: return "衣服";
                case ItemKind.Ammo: return "弹药";
                case ItemKind.Backpack: return "背包/箱子";
                case ItemKind.Throwable: return "投掷物";
                case ItemKind.Magazine: return "弹匣";
                case ItemKind.Arrow: return "箭";
                case ItemKind.GunPart: return "枪械配件";
                case ItemKind.Deployable: return "可放置物";
                case ItemKind.Container: return "液体容器";
                case ItemKind.Liquid: return "液体";
                case ItemKind.Cloak: return "披风";
                default: return "其他";
            }
        }

        private static Dictionary<int, ItemInfo> ById
        {
            get
            {
                if (_byId == null) Load();
                return _byId;
            }
        }

        public static int Count => ById.Count;

        private static void Load()
        {
            var d = new Dictionary<int, ItemInfo>();
            var asm = Assembly.GetExecutingAssembly();
            using (var st = asm.GetManifestResourceStream("items.tsv"))
            using (var rd = new StreamReader(st ?? throw new InvalidOperationException("程序里缺物品表 items.tsv"), Encoding.UTF8))
            {
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split('\t');
                    if (p.Length < 8) continue;
                    var it = new ItemInfo
                    {
                        Id = int.Parse(p[0], CultureInfo.InvariantCulture),
                        Name = p[1],
                        W = Math.Max(1, int.Parse(p[2], CultureInfo.InvariantCulture)),
                        H = Math.Max(1, int.Parse(p[3], CultureInfo.InvariantCulture)),
                        Stack = Math.Max(1, int.Parse(p[4], CultureInfo.InvariantCulture)),
                        Kind = (ItemKind)int.Parse(p[5], CultureInfo.InvariantCulture),
                        Hidden = p[6] == "1",
                        Class = p[7],
                    };
                    if (p.Length > 8 && p[8].Contains("x"))
                    {
                        var bs = p[8].Split('x');
                        it.BagW = int.Parse(bs[0], CultureInfo.InvariantCulture);
                        it.BagH = int.Parse(bs[1], CultureInfo.InvariantCulture);
                    }
                    if (p.Length > 9) float.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out it.Weight);
                    if (p.Length > 10 && p[10].Length > 0)
                        foreach (var kv in p[10].Split(','))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq > 0) it.Extra[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                        }
                    if (p.Length > 11 && p[11].Length > 0)
                        foreach (var f in p[11].Split(','))
                            if (int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fv)) it.Features.Add(fv);
                    d[it.Id] = it;
                }
            }
            _byId = d;
        }

        /// <summary>
        /// 按编号找物品。衣服在存档里是"套装编号 + 部位"（比如 10001 = 10000 那套的上衣），
        /// 游戏 ItemManager.FindItemAttrById 找不到原编号时会退回到整十的套装编号，这里一样。
        /// </summary>
        public static ItemInfo Get(int id)
        {
            if (ById.TryGetValue(id, out var it))
            {
                if (it.Kind == ItemKind.Clothing && it.ClothPart < 0) return ClothPiece(it, 0);
                return it;
            }
            if (id >= 0 && ById.TryGetValue(id / 10 * 10, out var set) && set.Kind == ItemKind.Clothing)
                return ClothPiece(set, id % 10);
            return new ItemInfo { Id = id, Name = "物品#" + id, Known = false, Kind = ItemKind.Material };
        }

        public static bool Exists(int id) => Get(id).Known;

        private static readonly Dictionary<int, ItemInfo> _pieces = new Dictionary<int, ItemInfo>();

        private static ItemInfo ClothPiece(ItemInfo set, int part)
        {
            int id = set.Id + part;
            if (_pieces.TryGetValue(id, out var r)) return r;
            r = new ItemInfo
            {
                Id = id, W = set.W, H = set.H, Stack = set.Stack, Kind = set.Kind, Hidden = set.Hidden, Class = set.Class,
                Weight = set.Weight, ClothPart = part,
                Name = set.Name + "·" + (part >= 0 && part < ClothPartNames.Length ? ClothPartNames[part] : "部位" + part),
                Known = part >= 0 && part < ClothPartNames.Length,
            };
            foreach (var kv in set.Extra) r.Extra[kv.Key] = kv.Value;
            r.Features.UnionWith(set.Features);
            _pieces[id] = r;
            return r;
        }

        /// <summary>添加物品时可选的全部物品（衣服拆成四个部位）。</summary>
        public static List<ItemInfo> Addable()
        {
            if (_addable != null) return _addable;
            var r = new List<ItemInfo>();
            foreach (var it in ById.Values.OrderBy(x => x.Id))
            {
                if (it.Kind == ItemKind.Clothing)
                    for (int p = 0; p < ClothPartNames.Length; p++) r.Add(ClothPiece(it, p));
                else r.Add(it);
            }
            _addable = r;
            return r;
        }
    }
}
