using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace ZedZoneSaveEditor
{
    /// <summary>ItemData / InventoryData 的字段号（照 dump.cs 里的 ProtoMember）。</summary>
    internal static class IF
    {
        public const int ItemId = 1, Count = 2, Durability = 3, Props = 4, Coor = 5, Rotate = 6, Pairs = 20, Inventory = 21;
        public const int InvId = 1001, InvItems = 1002, InvW = 1003, InvH = 1004, InvFeatureLimit = 1010;
        public const int PairKey = 1, PairValue = 2;
        public const float MaxDurability = 100f;              // ItemData.MAX_ITEM_DURABILITY
        public const int FeatureBattery = 10;                 // ItemFeatureType.Battery
        public const string AirPressureKey = "RangedWeaponAirPressure";
    }

    /// <summary>一件物品（背包格子里的，或者装备栏上的）。</summary>
    internal sealed class ItemView
    {
        public readonly PMsg Msg;
        public readonly ContainerView Owner;    // null = 装备栏上的
        public readonly string Slot;            // 装备栏位置名
        private readonly float _worldTime;

        public ItemView(PMsg msg, ContainerView owner, string slot, float worldTime)
        {
            Msg = msg;
            Owner = owner;
            Slot = slot;
            _worldTime = worldTime;
        }

        public int Id => Msg.GetInt(IF.ItemId);
        public ItemInfo Info => ItemDb.Get(Id);
        public string Name => Info.Name;

        public float Count { get => Msg.GetFloat(IF.Count); set => Msg.SetFloat(IF.Count, value); }
        public float Durability { get => Msg.GetFloat(IF.Durability); set => Msg.SetFloat(IF.Durability, value); }

        public List<float> Props => Msg.GetFloatList(IF.Props);
        public void SetProps(IList<float> p) => Msg.SetFloatList(IF.Props, p);

        public int X { get { var c = Msg.GetLongList(IF.Coor); return c.Count > 0 ? (int)c[0] : 0; } }
        public int Y { get { var c = Msg.GetLongList(IF.Coor); return c.Count > 1 ? (int)c[1] : 0; } }
        public bool Rotated => Msg.GetBool(IF.Rotate);
        public int W => Rotated ? Info.H : Info.W;
        public int H => Rotated ? Info.W : Info.H;

        public PMsg Inventory => Msg.GetMsg(IF.Inventory);
        public bool IsEquipped => Owner == null;

        public string CountText
        {
            get
            {
                float c = Count;
                string s = Math.Abs(c - Math.Round(c)) < 0.001 ? ((long)Math.Round(c)).ToString(CultureInfo.InvariantCulture) : c.ToString("0.##", CultureInfo.InvariantCulture);
                return Info.Stackable ? s + " / " + Info.Stack : s;
            }
        }

        public string DurabilityText => Durability.ToString("0.#", CultureInfo.InvariantCulture);

        public string PosText => IsEquipped ? Slot : $"({X},{Y}){(Rotated ? " 横放" : "")}";

        /// <summary>各类物品自己的那项状态（武器耐久、弹匣子弹、食物保质期……）。</summary>
        public string StateText
        {
            get
            {
                var info = Info;
                var p = Props;
                float P(int i) => i < p.Count ? p[i] : 0f;
                switch (info.Kind)
                {
                    case ItemKind.MeleeWeapon:
                        return $"武器 {P(0):0}/{info.ExtraFloat("hp"):0}";
                    case ItemKind.RangedWeapon:
                    {
                        string s = $"枪况 {P(0):0}/{info.ExtraFloat("hp"):0}";
                        int feed = info.ExtraInt("feed", -1);
                        if (feed == 0) s += P(1) > 0 ? "，带弹匣" : "，没弹匣";
                        else if (feed == 1) s += $"，子弹 {P(1):0}";
                        int parts = 0;
                        for (int i = 8; i <= 12; i += 2) if (P(i) > 0) parts++;
                        if (parts > 0) s += $"，配件 {parts} 个";
                        return s;
                    }
                    case ItemKind.Magazine:
                        return $"子弹 {P(1):0}/{info.ExtraInt("cap")}，弹匣 {P(0):0}%";
                    case ItemKind.GunPart:
                        return $"配件 {P(0):0}/{info.ExtraFloat("dur"):0}";
                    case ItemKind.Food:
                    {
                        float perish = info.ExtraFloat("perish");
                        if (perish <= 0 || p.Count == 0) return "";
                        float left = P(0) + perish - _worldTime;
                        return left <= 0 ? "已过期" : $"还能放 {left:0.#} 天";
                    }
                    case ItemKind.Container:
                        return P(0) > 0 ? $"装着 {ItemDb.Get((int)P(0)).Name} {P(1):0}/{info.ExtraFloat("cap"):0}" : "空的";
                    case ItemKind.Backpack:
                    case ItemKind.Clothing:
                    {
                        var inv = Inventory;
                        if (inv == null) return "";
                        int n = inv.GetMsgs(IF.InvItems).Count;
                        return n == 0 ? "里面是空的" : $"里面有 {n} 件";
                    }
                }
                return "";
            }
        }

        // ---------- 修改 ----------

        /// <summary>修好：耐久回满；武器、弹匣、配件的状态回满（枪上装的配件一起修）；食物变新鲜。</summary>
        public bool Repair()
        {
            bool changed = false;
            var info = Info;
            if (Durability < IF.MaxDurability) { Durability = IF.MaxDurability; changed = true; }   // 字段不在 = 0
            var p = Props;
            bool pc = false;
            void SetP(int i, float v)
            {
                if (i >= p.Count || v <= 0 || Math.Abs(p[i] - v) < 0.0001f) return;
                p[i] = v;
                pc = true;
            }
            switch (info.Kind)
            {
                case ItemKind.MeleeWeapon: SetP(0, info.ExtraFloat("hp")); break;
                case ItemKind.RangedWeapon:
                    SetP(0, info.ExtraFloat("hp"));
                    for (int i = 8; i + 1 < p.Count && i <= 12; i += 2)
                        if (p[i] > 0) SetP(i + 1, ItemDb.Get((int)p[i]).ExtraFloat("dur"));
                    break;
                case ItemKind.Magazine: SetP(0, IF.MaxDurability); break;
                case ItemKind.GunPart: SetP(0, info.ExtraFloat("dur")); break;
                case ItemKind.Food:
                    // 食物 properties[0] = 生产时间（游戏天数），设成现在就是刚做好的
                    if (info.ExtraFloat("perish") > 0 && p.Count > 0 && p[0] < _worldTime) { p[0] = _worldTime; pc = true; }
                    break;
            }
            if (pc) { SetProps(p); changed = true; }
            return changed;
        }

        /// <summary>补满：能堆叠的数量补到上限；弹匣装满子弹；液体容器装满（原来装的那种液体）。</summary>
        public bool Fill()
        {
            var info = Info;
            bool changed = false;
            if (info.Stackable && Count < info.Stack) { Count = info.Stack; changed = true; }
            var p = Props;
            if (info.Kind == ItemKind.Magazine && p.Count > 1 && p[1] < info.ExtraInt("cap"))
            {
                p[1] = info.ExtraInt("cap");
                SetProps(p);
                changed = true;
            }
            if (info.Kind == ItemKind.Container && p.Count > 1 && p[0] > 0 && p[1] < info.ExtraFloat("cap"))
            {
                p[1] = info.ExtraFloat("cap");
                SetProps(p);
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>一个能放东西的格子区（身上背包、背包里的背包、衣服口袋……）。</summary>
    internal sealed class ContainerView
    {
        public readonly PMsg Inv;
        public readonly ItemView OwnerItem;
        public readonly string Title;
        public readonly float WorldTime;

        public ContainerView(PMsg inv, ItemView ownerItem, string title, float worldTime)
        {
            Inv = inv;
            OwnerItem = ownerItem;
            Title = title;
            WorldTime = worldTime;
        }

        public int W => Inv.GetInt(IF.InvW);
        public int H => Inv.GetInt(IF.InvH);

        public List<ItemView> Items => Inv.GetMsgs(IF.InvItems).Select(m => new ItemView(m, this, null, WorldTime)).ToList();

        public bool[,] Occupied(PMsg skip = null)
        {
            int w = Math.Max(0, W), h = Math.Max(0, H);
            var occ = new bool[w, h];
            foreach (var it in Items)
            {
                if (it.Msg == skip) continue;
                for (int x = Math.Max(0, it.X); x < Math.Min(w, it.X + it.W); x++)
                    for (int y = Math.Max(0, it.Y); y < Math.Min(h, it.Y + it.H); y++)
                        occ[x, y] = true;
            }
            return occ;
        }

        /// <summary>找一块能放下 w×h 的空地（从上往下、从左往右）。放不下返回 null。</summary>
        public Point? FindSpot(int w, int h, bool[,] occ = null)
        {
            occ = occ ?? Occupied();
            int cw = occ.GetLength(0), ch = occ.GetLength(1);
            for (int y = 0; y + h <= ch; y++)
                for (int x = 0; x + w <= cw; x++)
                {
                    bool free = true;
                    for (int dx = 0; dx < w && free; dx++)
                        for (int dy = 0; dy < h && free; dy++)
                            if (occ[x + dx, y + dy]) free = false;
                    if (free) return new Point(x, y);
                }
            return null;
        }

        /// <summary>(x,y) 放一块 w×h 是否在格子里、不压到别的东西。</summary>
        public static bool Fits(bool[,] occ, int x, int y, int w, int h)
        {
            int cw = occ.GetLength(0), ch = occ.GetLength(1);
            if (x < 0 || y < 0 || x + w > cw || y + h > ch) return false;
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                    if (occ[x + dx, y + dy]) return false;
            return true;
        }

        private static void SetPos(PMsg item, int x, int y, bool rot)
        {
            item.SetLongList(IF.Coor, new long[] { x, y });
            if (rot) item.SetBool(IF.Rotate, true);
            else if (item.Has(IF.Rotate)) item.RemoveAll(IF.Rotate);
        }

        /// <summary>给一件物品挑位置：先试 prefer（原方向、再横过来），再从左上角找空地。</summary>
        private static (Point pos, bool rot)? Choose(bool[,] occ, ItemInfo info, Point? prefer, bool preferRot)
        {
            var tries = info.W != info.H ? new[] { preferRot, !preferRot } : new[] { false };
            if (prefer != null)
                foreach (bool r in tries)
                    if (Fits(occ, prefer.Value.X, prefer.Value.Y, r ? info.H : info.W, r ? info.W : info.H))
                        return (prefer.Value, r);
            foreach (bool r in tries)
            {
                var spot = FindSpotIn(occ, r ? info.H : info.W, r ? info.W : info.H);
                if (spot != null) return (spot.Value, r);
            }
            return null;
        }

        private static Point? FindSpotIn(bool[,] occ, int w, int h)
        {
            int cw = occ.GetLength(0), ch = occ.GetLength(1);
            for (int y = 0; y + h <= ch; y++)
                for (int x = 0; x + w <= cw; x++)
                    if (Fits(occ, x, y, w, h)) return new Point(x, y);
            return null;
        }

        /// <summary>把一件新物品放进来（prefer = 想放的格子）。放不下返回 null。</summary>
        public ItemView Place(PMsg item, Point? prefer = null)
        {
            var info = ItemDb.Get(item.GetInt(IF.ItemId));
            var c = Choose(Occupied(), info, prefer, false);
            if (c == null) return null;
            SetPos(item, c.Value.pos.X, c.Value.pos.Y, c.Value.rot);
            Inv.AddMsg(IF.InvItems, item);
            return new ItemView(item, this, null, WorldTime);
        }

        /// <summary>在同一个格子区里挪到 (x,y)，rot = 横放。放不下返回 false，原处不动。</summary>
        public bool MoveWithin(ItemView it, int x, int y, bool rot)
        {
            var info = it.Info;
            if (!Fits(Occupied(it.Msg), x, y, rot ? info.H : info.W, rot ? info.W : info.H)) return false;
            SetPos(it.Msg, x, y, rot);
            return true;
        }

        /// <summary>
        /// 从别的格子区搬进来。at = 想放的格子（放不下就自动找空地）。
        /// 放不下、或者想把背包放进它自己里面，返回 null，原处不动。
        /// </summary>
        public ItemView MoveIn(ItemView it, Point? at = null, bool? rot = null)
        {
            if (it.Owner == null || it.Owner.Inv == Inv) return null;
            if (CharacterInventory.Holds(it.Msg, Inv)) return null;
            var c = Choose(Occupied(), it.Info, at, rot ?? it.Rotated);
            if (c == null) return null;
            it.Owner.Remove(it);
            SetPos(it.Msg, c.Value.pos.X, c.Value.pos.Y, c.Value.rot);
            Inv.AddMsg(IF.InvItems, it.Msg);
            return new ItemView(it.Msg, this, null, WorldTime);
        }

        public void Remove(ItemView it) => Inv.RemoveMsg(it.Msg);

        /// <summary>能不能放下一件这种物品（skip = 先当它不在，比如马上要拿走的那件）。</summary>
        public bool CanPlace(ItemInfo info, PMsg skip) => Choose(Occupied(skip), info, null, false) != null;

        /// <summary>这几件东西按顺序一件件 MoveIn 进来能不能都放下（和 MoveIn 用同样的找位置办法模拟）。</summary>
        public bool CanPlaceAll(IEnumerable<ItemView> items)
        {
            var occ = Occupied();
            foreach (var it in items)
            {
                var info = it.Info;
                var c = Choose(occ, info, null, it.Rotated);
                if (c == null) return false;
                int w = c.Value.rot ? info.H : info.W, h = c.Value.rot ? info.W : info.H;
                for (int x = c.Value.pos.X; x < c.Value.pos.X + w; x++)
                    for (int y = c.Value.pos.Y; y < c.Value.pos.Y + h; y++)
                        occ[x, y] = true;
            }
            return true;
        }
    }

    internal enum SlotKind { Single, Array, List }

    /// <summary>
    /// 装备栏的一种槽位。规矩照游戏：CharacterEquipmentData 的字段、CharacterEquipmentPanel.GetEquipRoute / HasEmptySubSlot、
    /// HumanCharacterController.EquipClothing、CharacterWeaponSlotUI.CanEquip 和场景里 4 个武器槽的设置。
    /// </summary>
    internal sealed class EquipSlotDef
    {
        public int Field;
        public string Name;
        public SlotKind Kind;
        public int Index;            // Array：第几格
        public int Max = 1;          // List：最多几件
        public string Rule;          // 能放什么（给人看的）
        public bool Legacy;          // 旧版字段，游戏已经不用了：只显示、能卸下，不能往里装
        public Func<ItemInfo, bool> Test;

        public bool Accepts(ItemInfo info)
        {
            if (Legacy || info == null || !info.Known || Test == null) return false;
            // 带"外骨骼装备"特性的东西走游戏另外的流程，只能装到外骨骼那三个槽（Test 里按编号认）
            if (info.Features.Contains(EquipRules.FeatExoskeleton) && Field < 68) return false;
            return Test(info);
        }
    }

    internal static class EquipRules
    {
        public const int FeatExecutionTool = 6, FeatLighting = 13, FeatSpecial = 17, FeatHeadDecoration = 22,
            FeatHandheld = 38, FeatExoskeleton = 39;

        private static bool Cloth(ItemInfo i, int part) => i.Kind == ItemKind.Clothing && i.ClothPart == part;
        private static bool F(ItemInfo i, int f) => i.Features.Contains(f);
        // 武器槽：近战武器看槽能不能放近战；枪看大小（ItemAttr_RangedWeapon.weaponSlotSize，0 小 1 大）
        private static bool Gun(ItemInfo i, int size) => i.Kind == ItemKind.RangedWeapon && i.ExtraInt("slot", 1) == size;

        public static readonly EquipSlotDef[] Defs =
        {
            new EquipSlotDef { Field = 57, Kind = SlotKind.Array, Index = 0, Name = "武器 1", Rule = "长枪或近战武器", Test = i => i.Kind == ItemKind.MeleeWeapon || Gun(i, 1) },
            new EquipSlotDef { Field = 57, Kind = SlotKind.Array, Index = 1, Name = "武器 2", Rule = "长枪或近战武器", Test = i => i.Kind == ItemKind.MeleeWeapon || Gun(i, 1) },
            new EquipSlotDef { Field = 57, Kind = SlotKind.Array, Index = 2, Name = "武器 3", Rule = "手枪这类小型枪械", Test = i => Gun(i, 0) },
            new EquipSlotDef { Field = 57, Kind = SlotKind.Array, Index = 3, Name = "处决工具", Rule = "匕首这类处决工具", Test = i => F(i, FeatExecutionTool) },
            new EquipSlotDef { Field = 56, Name = "投掷物", Rule = "手雷、燃烧瓶等投掷物", Test = i => i.Kind == ItemKind.Throwable },
            new EquipSlotDef { Field = 50, Name = "头部", Rule = "衣服的头部", Test = i => Cloth(i, 0) },
            new EquipSlotDef { Field = 55, Name = "面罩", Rule = "口罩、面罩", Test = i => i.Kind == ItemKind.FaceMask },
            new EquipSlotDef { Field = 63, Name = "头饰", Rule = "头饰、头盔", Test = i => F(i, FeatHeadDecoration) },
            new EquipSlotDef { Field = 60, Kind = SlotKind.List, Max = 3, Name = "上衣", Rule = "衣服的上衣（最多穿 3 件）", Test = i => Cloth(i, 1) },
            new EquipSlotDef { Field = 59, Name = "披风", Rule = "披风", Test = i => i.Kind == ItemKind.Cloak },
            new EquipSlotDef { Field = 61, Kind = SlotKind.List, Max = 4, Name = "裤子", Rule = "衣服的裤子（最多穿 4 件）", Test = i => Cloth(i, 2) },
            new EquipSlotDef { Field = 53, Name = "鞋子", Rule = "衣服的鞋子", Test = i => Cloth(i, 3) },
            new EquipSlotDef { Field = 54, Name = "背包", Rule = "背包（弹壳收集器、保险箱、充电箱这类不能背）",
                Test = i => i.Kind == ItemKind.Backpack && i.Class == "ItemAttr_Backpack" && !i.Extra.ContainsKey("noequip") },
            new EquipSlotDef { Field = 65, Name = "照明工具", Rule = "手电筒、夜视仪等照明工具", Test = i => F(i, FeatLighting) },
            new EquipSlotDef { Field = 67, Kind = SlotKind.Array, Index = 0, Name = "手持设备", Rule = "暖手宝、小风扇等手持设备", Test = i => F(i, FeatHandheld) },
            new EquipSlotDef { Field = 62, Name = "特殊装备", Rule = "抓钩、弹道计算机等特殊装备", Test = i => F(i, FeatSpecial) },
            new EquipSlotDef { Field = 68, Name = "外骨骼", Rule = "白隼外骨骼装甲", Test = i => i.Id == 1083 },
            new EquipSlotDef { Field = 69, Name = "推进器", Rule = "白隼矢量推进器", Test = i => i.Id == 1084 },
            new EquipSlotDef { Field = 70, Name = "护盾", Rule = "白隼重型塔盾", Test = i => i.Id == 1085 },
            new EquipSlotDef { Field = 51, Name = "上衣（旧字段）", Legacy = true, Rule = "老版本存档留下的，游戏现在不读这里" },
            new EquipSlotDef { Field = 52, Name = "裤子（旧字段）", Legacy = true, Rule = "老版本存档留下的，游戏现在不读这里" },
        };

        /// <summary>装备数据里所有放物品的字段号。</summary>
        public static IEnumerable<int> Fields => Defs.Select(d => d.Field).Distinct();

        /// <summary>空位占位：游戏 SetWeaponData 等把空格子填成一个 itemId = -1 的物品。</summary>
        public static PMsg Placeholder()
        {
            var m = new PMsg();
            m.SetLong(IF.ItemId, -1);
            return m;
        }
    }

    /// <summary>装备栏上的一个位置（列表里的一行）。Item = null 表示空着。</summary>
    internal sealed class EquipPos
    {
        public EquipSlotDef Def;
        public int Index;            // Array/List 里的第几个
        public string Name;
        public ItemView Item;
        public bool Empty => Item == null;
    }

    /// <summary>人物身上的东西：身上背包、装备栏，以及里面套着的背包和口袋。</summary>
    internal sealed class CharacterInventory
    {
        public const int CdInventory = 14, CdEquipment = 18, EqBackpack = 54;
        public const int NoBackpackW = 6, NoBackpackH = 8;     // 不背背包时的随身格子（CharacterData 静态默认值）

        public readonly PMsg Cd;
        public readonly float WorldTime;
        public ItemView WornBackpack { get; private set; }     // 身上背着的背包（没背就是 null）
        public ContainerView Main { get; private set; }        // 主背包格子（背着背包时就是那个背包的格子）
        public List<ItemView> Equipped { get; private set; } = new List<ItemView>();
        public List<EquipPos> Slots { get; private set; } = new List<EquipPos>();
        private readonly byte[] _origMain;
        private bool _bagChanged;

        public CharacterInventory(PMsg cd, float worldTime)
        {
            Cd = cd;
            WorldTime = worldTime;
            Rebuild();
            _origMain = Main?.Inv.ToBytes();
        }

        /// <summary>按当前数据重新整理装备栏、身上背包。装备变动后调用。</summary>
        public void Rebuild()
        {
            var eq = Cd.GetMsg(CdEquipment);
            var slots = new List<EquipPos>();
            foreach (var def in EquipRules.Defs)
            {
                switch (def.Kind)
                {
                    case SlotKind.Single:
                    {
                        var m = eq?.GetMsg(def.Field);
                        var item = m != null && m.GetInt(IF.ItemId) >= 0 ? m : null;
                        if (def.Legacy && item == null) break;
                        slots.Add(Pos(def, 0, def.Name, item));
                        break;
                    }
                    case SlotKind.Array:
                    {
                        var list = eq?.GetMsgs(def.Field) ?? new List<PMsg>();
                        var m = def.Index < list.Count ? list[def.Index] : null;
                        slots.Add(Pos(def, def.Index, def.Name, m != null && m.GetInt(IF.ItemId) >= 0 ? m : null));
                        break;
                    }
                    case SlotKind.List:
                    {
                        var list = (eq?.GetMsgs(def.Field) ?? new List<PMsg>()).Where(m => m.GetInt(IF.ItemId) >= 0).ToList();
                        int n = Math.Max(def.Max, list.Count);
                        for (int i = 0; i < n; i++)
                            slots.Add(Pos(def, i, $"{def.Name} {i + 1}", i < list.Count ? list[i] : null));
                        break;
                    }
                }
            }
            Slots = slots;
            Equipped = slots.Where(p => p.Item != null).Select(p => p.Item).ToList();
            WornBackpack = slots.FirstOrDefault(p => p.Def.Field == EqBackpack)?.Item;
            var bagInv = WornBackpack?.Inventory;
            if (bagInv != null)
                Main = new ContainerView(bagInv, WornBackpack, $"{WornBackpack.Name}（身上背着）", WorldTime);
            else if (Cd.GetMsg(CdInventory) is PMsg inv)
                Main = new ContainerView(inv, null, "随身物品栏", WorldTime);
            else Main = null;
        }

        private EquipPos Pos(EquipSlotDef def, int index, string name, PMsg item) => new EquipPos
        {
            Def = def, Index = index, Name = name, Item = item == null ? null : new ItemView(item, null, name, WorldTime),
        };

        /// <summary>
        /// 背着背包时，存档里有两份一样的背包内容：装备栏背包里一份、人物 inventoryData 一份
        /// （游戏里是同一个对象：EquipBackpack 把 inventoryData 指向背包的格子）。改完两份一起写，保持一致。
        /// </summary>
        public void SyncBeforeSave()
        {
            if (WornBackpack == null || Main == null || Main.Inv != WornBackpack.Inventory) return;
            if (!_bagChanged && !Cd.Has(CdInventory)) return;
            var now = Main.Inv.ToBytes();
            if (!_bagChanged && _origMain != null && now.SequenceEqual(_origMain)) return;
            Cd.SetMsgBytes(CdInventory, now);
        }

        /// <summary>inv 是不是在这件物品里面（它自己的格子，或者里面套着的背包的格子）。</summary>
        public static bool Holds(PMsg item, PMsg inv)
        {
            var sub = item.GetMsg(IF.Inventory);
            if (sub == null) return false;
            if (sub == inv) return true;
            foreach (var child in sub.GetMsgs(IF.InvItems))
                if (Holds(child, inv)) return true;
            return false;
        }

        /// <summary>某件物品里面的格子（背包、衣服口袋）。</summary>
        public ContainerView InnerOf(ItemView it)
        {
            var inv = it.Inventory;
            if (inv == null) return null;
            return new ContainerView(inv, it, it.IsEquipped ? $"{it.Name}（{it.Slot}）" : it.Name, WorldTime);
        }

        /// <summary>身上所有格子区（主背包和里面套的，加上穿着的衣服口袋），连同"在哪"的说明。</summary>
        public List<(ContainerView box, string path)> AllContainers()
        {
            var r = new List<(ContainerView, string)>();
            void Walk(ContainerView c, string path, int depth)
            {
                r.Add((c, path));
                if (depth > 20) return;
                foreach (var it in c.Items)
                {
                    var inner = InnerOf(it);
                    if (inner != null) Walk(inner, path + " › " + it.Name, depth + 1);
                }
            }
            if (Main != null) Walk(Main, Main.Title, 0);
            foreach (var e in Equipped)
            {
                if (e == WornBackpack) continue;
                var inner = InnerOf(e);
                if (inner != null) Walk(inner, inner.Title, 0);
            }
            return r;
        }

        public int CountAll()
        {
            int n = 0;
            foreach (var (box, _) in AllContainers()) n += box.Items.Count;
            return n;
        }

        // ---------------- 装上 / 卸下 ----------------

        /// <summary>
        /// 把一件物品装到 pos。src = 它原来所在的格子区（null = 新造的物品）。
        /// 槽上原来有东西就换下来：放回 src（新物品就放进主背包）；背包槽特殊，见 EquipBackpack。
        /// 成功返回 null，失败返回原因（什么都不改）。
        /// </summary>
        public string Equip(EquipPos pos, PMsg item, ContainerView src)
        {
            var info = ItemDb.Get(item.GetInt(IF.ItemId));
            if (!pos.Def.Accepts(info)) return $"{info.Name} 不能装在「{pos.Name}」（这里只能放{pos.Def.Rule}）。";
            ItemView srcView = null;
            if (src != null)
            {
                srcView = src.Items.FirstOrDefault(x => x.Msg == item);
                if (srcView == null) return "找不到要装的那件东西。";
            }
            if (pos.Def.Field == EqBackpack) return EquipBackpack(pos, item, src, srcView);

            var old = pos.Item;
            ContainerView back = null;
            if (old != null)
            {
                back = src ?? Main;
                if (back == null) return $"换下来的 {old.Name} 没地方放。";
                if (Holds(old.Msg, back.Inv)) return $"要装的东西在 {old.Name} 里面，先把它拿出来再换。";
                if (!back.CanPlace(old.Info, item)) return $"换下来的 {old.Name} 在「{back.Title}」里放不下，先腾点地方。";
            }
            Point? at = srcView != null ? new Point(srcView.X, srcView.Y) : (Point?)null;
            src?.Remove(srcView);
            if (old != null) back.Place(old.Msg, at);
            PutRaw(pos, item);
            Rebuild();
            return null;
        }

        /// <summary>
        /// 换背包。游戏里换背包时旧背包掉在地上、不背背包时随身格子里的东西挪进新背包（EquipBackpack）。
        /// 修改器没有"地上"：旧背包（连同里面的东西）放进新背包里；原来没背背包就把随身格子里的东西挪进去。放不下就不换。
        /// </summary>
        private string EquipBackpack(EquipPos pos, PMsg bag, ContainerView src, ItemView srcView)
        {
            var info = ItemDb.Get(bag.GetInt(IF.ItemId));
            var old = WornBackpack;
            if (old != null && old.Msg == bag) return null;
            var inv = bag.GetMsg(IF.Inventory);
            if (inv == null)
            {
                inv = new PMsg();
                inv.SetString(IF.InvId, "");
                inv.SetInt(IF.InvW, Math.Max(1, info.BagW));
                inv.SetInt(IF.InvH, Math.Max(1, info.BagH));
                bag.AddMsg(IF.Inventory, inv);
            }
            var newBox = new ContainerView(inv, null, info.Name, WorldTime);
            List<ItemView> carry = null;
            if (old != null)
            {
                if (!newBox.CanPlace(old.Info, null)) return $"{info.Name} 里放不下原来的 {old.Name}（{old.Info.W}×{old.Info.H} 格），先腾点地方或者换个大的。";
            }
            else if (Main != null)
            {
                // 没背背包：随身格子里的东西（除了要背的这个）挪进新背包，先模拟一遍看放不放得下
                carry = Main.Items.Where(x => x.Msg != bag).ToList();
                if (!newBox.CanPlaceAll(carry)) return $"{info.Name} 里放不下随身物品栏里原来的东西，先拿掉一些。";
            }
            src?.Remove(srcView);
            if (old != null) newBox.Place(old.Msg);
            if (carry != null) foreach (var it in carry) newBox.MoveIn(it);
            Cd.GetOrAddMsg(CdEquipment).SetMsg(EqBackpack, bag);
            _bagChanged = true;
            Rebuild();
            return null;
        }

        /// <summary>把 pos 上的东西卸下来，放进 target 格子区。失败返回原因。</summary>
        public string Unequip(EquipPos pos, ContainerView target, Point? at = null)
        {
            var it = pos.Item;
            if (it == null) return "这个槽本来就是空的。";
            if (target == null) return "没地方放。";
            if (Holds(it.Msg, target.Inv) || target.OwnerItem?.Msg == it.Msg) return $"不能把 {it.Name} 放进它自己里面。";
            if (target.Place(it.Msg, at) == null) return $"「{target.Title}」里放不下 {it.Name}（{it.Info.W}×{it.Info.H} 格）。";
            TakeRaw(pos);
            if (pos.Def.Field == EqBackpack) SetNoBackpack();
            Rebuild();
            return null;
        }

        /// <summary>直接删掉 pos 上的东西（连里面装的）。</summary>
        public void Delete(EquipPos pos)
        {
            if (pos.Item == null) return;
            TakeRaw(pos);
            if (pos.Def.Field == EqBackpack) SetNoBackpack();
            Rebuild();
        }

        /// <summary>拖到「装备栏」上时自动挑位置：先找能放的空位，没有就换掉第一个能放的。</summary>
        public EquipPos AutoSlot(ItemInfo info)
        {
            var ok = Slots.Where(p => p.Def.Accepts(info)).ToList();
            return ok.FirstOrDefault(p => p.Empty) ?? ok.FirstOrDefault(p => p.Def.Kind != SlotKind.List);
        }

        private void PutRaw(EquipPos pos, PMsg item)
        {
            var eq = Cd.GetOrAddMsg(CdEquipment);
            switch (pos.Def.Kind)
            {
                case SlotKind.Single:
                    eq.SetMsg(pos.Def.Field, item);
                    break;
                case SlotKind.Array:
                {
                    var list = eq.GetMsgs(pos.Def.Field);
                    if (pos.Index < list.Count) eq.ReplaceMsg(list[pos.Index], item);
                    else
                    {
                        for (int i = list.Count; i < pos.Index; i++) eq.AddMsg(pos.Def.Field, EquipRules.Placeholder());
                        eq.AddMsg(pos.Def.Field, item);
                    }
                    break;
                }
                case SlotKind.List:
                    if (pos.Item != null) eq.ReplaceMsg(pos.Item.Msg, item);
                    else eq.AddMsg(pos.Def.Field, item);
                    break;
            }
        }

        private void TakeRaw(EquipPos pos)
        {
            var eq = Cd.GetMsg(CdEquipment);
            if (eq == null || pos.Item == null) return;
            // 武器栏、手持设备是定长数组：空出来的格子照游戏填占位；其他直接去掉
            if (pos.Def.Kind == SlotKind.Array) eq.ReplaceMsg(pos.Item.Msg, EquipRules.Placeholder());
            else eq.RemoveMsg(pos.Item.Msg);
        }

        /// <summary>卸下背包后：随身格子换成游戏默认的空格子（UnequipBackpack 新建一个 6×8 的 InventoryData）。</summary>
        private void SetNoBackpack()
        {
            var inv = new PMsg();
            inv.SetString(IF.InvId, "");
            inv.SetInt(IF.InvW, NoBackpackW);
            inv.SetInt(IF.InvH, NoBackpackH);
            Cd.SetMsg(CdInventory, inv);
            _bagChanged = true;
        }
    }

    /// <summary>照游戏 ItemManager.GetRandomDataByAttr（不随机的那条路）造一件新物品。</summary>
    internal static class ItemFactory
    {
        private static readonly Random Rng = new Random();

        public static PMsg Create(ItemInfo info, float count, float worldTime)
        {
            var m = new PMsg();
            if (info.Id != 0) m.SetInt(IF.ItemId, info.Id);       // protobuf-net 不写 0
            m.SetFloat(IF.Count, count);
            m.SetFloat(IF.Durability, IF.MaxDurability);
            float[] props = null;
            switch (info.Kind)
            {
                case ItemKind.Food:
                    props = new[] { worldTime, 0f, 0f };              // [0] 生产时间 = 现在
                    break;
                case ItemKind.MeleeWeapon:
                    props = new[] { info.ExtraFloat("hp") };
                    break;
                case ItemKind.RangedWeapon:
                {
                    props = new float[14];
                    props[0] = info.ExtraFloat("hp");
                    int feed = info.ExtraInt("feed", 1);
                    if (feed == 0 || feed == 2) props[1] = -1;       // 弹匣枪/电池枪：不带弹匣、电池
                    props[4] = info.ExtraInt("auto") == 1 ? 1f : 0f;  // 全自动枪默认连发
                    float air = info.ExtraFloat("air");
                    if (air > 0) SetPair(m, IF.AirPressureKey, air.ToString(CultureInfo.InvariantCulture));
                    break;
                }
                case ItemKind.Clothing:
                    props = new[] { (float)(0.05 + Rng.NextDouble() * 0.95) };
                    break;
                case ItemKind.Backpack:
                {
                    props = new[] { (float)(0.05 + Rng.NextDouble() * 0.95), info.BagW, info.BagH };
                    var inv = new PMsg();
                    inv.SetString(IF.InvId, "");
                    inv.SetInt(IF.InvW, info.BagW);
                    inv.SetInt(IF.InvH, info.BagH);
                    if (info.Extra.ContainsKey("appliance"))                // 冰箱/充电箱：只能放电池（PortableApplianceTicker.ApplyInventoryLimit）
                        inv.SetLongList(IF.InvFeatureLimit, new long[] { IF.FeatureBattery });
                    m.AddMsg(IF.Inventory, inv);
                    break;
                }
                case ItemKind.Magazine:
                    props = new[] { IF.MaxDurability, info.ExtraInt("cap") };   // 新弹匣是满的
                    break;
                case ItemKind.GunPart:
                    props = new[] { info.ExtraFloat("dur") };
                    break;
                case ItemKind.Container:
                    props = new[] { 0f, 0f };                         // 空瓶
                    break;
                case ItemKind.Liquid:
                    props = new[] { -1f };
                    break;
            }
            if (props != null) m.SetFloatList(IF.Props, props);
            return m;
        }

        private static void SetPair(PMsg item, string key, string value)
        {
            var pair = new PMsg();
            pair.SetString(IF.PairKey, key);
            pair.SetString(IF.PairValue, value);
            item.AddMsg(IF.Pairs, pair);
        }

        /// <summary>复制一件：一模一样，但如果是背包，里面的东西不复制（变成空背包）。</summary>
        public static PMsg CloneEmpty(PMsg item)
        {
            var c = item.Clone();
            var inv = c.GetMsg(IF.Inventory);
            if (inv != null) inv.RemoveAll(IF.InvItems);
            return c;
        }
    }
}
