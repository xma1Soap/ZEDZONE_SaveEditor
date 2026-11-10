using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZedZoneSaveEditor
{
    /// <summary>
    /// 命令行自检：ZedZoneSaveEditor.exe --selftest 报告.txt
    /// 把找到的存档复制到临时文件夹再测，原文件只读不写。
    /// 另外用第一份联机角色拼一个假的单机存档，测单机那条路。
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string reportPath, IList<string> extraFiles)
        {
            var sb = new StringBuilder();
            int fails = 0;
            void Log(string s) { sb.AppendLine(s); Console.WriteLine(s); }
            void Check(bool ok, string what) { if (!ok) fails++; Log((ok ? "  [通过] " : "  [失败] ") + what); }

            string realRoot = GameFiles.DataRoot;
            Log("数据目录: " + realRoot);
            var files = new List<(string path, SaveKind kind)>();
            foreach (var e in GameFiles.ListSingle()) files.Add((e.Path, SaveKind.Single));
            foreach (var e in GameFiles.ListMulti()) files.Add((e.Path, SaveKind.Multi));
            foreach (var f in extraFiles) files.Add((f, GameFiles.GuessKind(f)));

            string tmpRoot = Path.Combine(Path.GetTempPath(), "ZedZoneSaveEditor-selftest-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(Path.Combine(tmpRoot, "GameData", "GameData"));
            Directory.CreateDirectory(Path.Combine(tmpRoot, "GameData", "MpCharacters"));

            // 用第一份联机角色拼一个单机存档（字段号照 GameData）
            foreach (var (path, kind) in files)
            {
                if (kind != SaveKind.Multi) continue;
                var env = PMsg.Parse(File.ReadAllBytes(path));
                var cdBytes = env.First(GameFiles.MpCharBytes).Data;
                var gd = new PMsg();
                gd.SetString(GameFiles.GdId, "selftest-world");
                var pf = new PField { Num = GameFiles.GdPlayer, Wire = 2, Data = cdBytes };
                gd.Fields.Add(pf);
                gd.SetFloat(GameFiles.GdInGameTime, 1234.5f);
                gd.SetLong(GameFiles.GdCreated, DateTime.UtcNow.AddDays(-3).ToFileTimeUtc());
                gd.SetLong(GameFiles.GdSaving, DateTime.UtcNow.AddDays(-2).ToFileTimeUtc());
                var shot = new PField { Num = 50, Wire = 2, Data = new byte[50000] };
                new Random(1).NextBytes(shot.Data);
                gd.Fields.Add(shot);
                gd.SetInt(GameFiles.GdSlot, 2);
                string fake = Path.Combine(tmpRoot, "selftest-world_SlotIndex2.game");
                File.WriteAllBytes(fake, gd.ToBytes());
                files.Add((fake, SaveKind.Single));
                Log("拼了一个假的单机存档: " + fake);
                break;
            }

            int idx = 0;
            foreach (var (path, kind) in files)
            {
                idx++;
                Log("");
                Log($"=== {idx}. {(kind == SaveKind.Single ? "单机" : "联机")}  {path}");
                try
                {
                    byte[] orig = File.ReadAllBytes(path);
                    var root = PMsg.Parse(orig);
                    Check(Same(root.ToBytes(), orig), "不改直接写回，和原文件一字不差");
                    var cdMsg = root.GetMsg(kind == SaveKind.Single ? GameFiles.GdPlayer : GameFiles.MpCharBytes);
                    Check(cdMsg != null, "找到人物数据");
                    Check(Same(root.ToBytes(), orig), "解析人物数据后写回，仍一字不差");

                    // 复制一份到临时目录再改
                    string sub = kind == SaveKind.Single ? Path.Combine("GameData", "GameData") : Path.Combine("GameData", "MpCharacters");
                    string copy = Path.Combine(tmpRoot, sub, Path.GetFileName(path));
                    if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        File.Copy(path, copy, true);
                    GameFiles.DataRoot = tmpRoot;
                    var entry = GameFiles.Describe(copy, kind);
                    var s = GameFiles.Load(entry);
                    var c = s.Character;
                    Log($"  名字={c.Name} 性别={c.Gender} 硬核={c.Hardcore} 属性点={c.AttrPoint} 技能点={c.SkillPoint} 天赋点={c.PerkPoint}");
                    Log($"  力量={c.Strength} 敏捷={c.Agility} 体质={c.Constitution}");
                    Log($"  血量={c.GetStatus(F.Hp)} 体力={c.GetStatus(F.Energy)} 精力={c.GetStatus(F.Fatigue)} 饱食={c.GetStatus(F.Hunger)} 水分={c.GetStatus(F.Thirst)} 血液={c.GetStatus(F.BloodVolume)} 体温={c.GetStatus(F.BodyTemperature)} 辐射={c.GetStatus(F.Radiation)}");
                    foreach (var bp in c.BodyParts) Log($"  部位 {bp.Name}: {bp.Hp} / 上限 {bp.Max(c.Constitution)}（基础 {bp.DefaultHp}） 止血时间={bp.BleedStop} 绷带={bp.HasBandage}");
                    Log($"  状态效果: {string.Join(",", c.BuffIds)}  天赋 {c.PerkCount} 个  技能节点 {c.SkillNodeCount} 个");
                    foreach (var w in s.Warnings) Log("  提示: " + w);

                    // 改一批值
                    string newName = c.Name + "改";
                    c.Name = newName;
                    if (kind == SaveKind.Multi) s.Root.SetString(GameFiles.MpName, newName);
                    c.AttrPoint = c.AttrPoint + 3;
                    c.SkillPoint = 99;
                    c.PerkPoint = 7;
                    c.Strength = 30; c.Agility = 25; c.Constitution = 40;
                    c.SetStatus(F.Hunger, Rules.MaxHunger(40));
                    c.SetStatus(F.Thirst, Rules.MaxThirst(40));
                    c.SetStatus(F.Fatigue, 100f);
                    c.SetStatus(F.Radiation, 0f);
                    c.SetStatus(F.Hp, 12345.5f);
                    foreach (var bp in c.BodyParts) bp.Heal(40);
                    c.ClearBuffs();

                    GameFiles.SkipGameCheck = true;
                    string backup = GameFiles.Save(s);
                    Check(File.Exists(backup) && Same(File.ReadAllBytes(backup), orig), "备份文件和原文件一致");

                    var s2 = GameFiles.Load(GameFiles.Describe(copy, kind));
                    var c2 = s2.Character;
                    Check(c2.Name == newName, "名字改好了");
                    if (kind == SaveKind.Multi) Check(s2.Root.GetString(GameFiles.MpName) == newName, "外层角色名也改了");
                    Check(c2.SkillPoint == 99 && c2.PerkPoint == 7, "点数改好了");
                    Check(c2.Strength == 30 && c2.Agility == 25 && c2.Constitution == 40, "属性改好了");
                    Check(c2.GetStatus(F.Hunger) == 160f && c2.GetStatus(F.Fatigue) == 100f && c2.GetStatus(F.Hp) == 12345.5f, "状态改好了");
                    bool partsOk = c2.BodyParts.Count > 0;
                    foreach (var bp in c2.BodyParts) partsOk &= bp.Hp == bp.Max(40) && bp.BleedStop == 0f && !bp.HasBandage;
                    Check(partsOk, "身体部位回满并止血");
                    Check(c2.BuffIds.Count == 0, "状态效果清掉了");

                    // 人物数据里没改的字段要原样保留
                    var before = PMsg.Parse(orig).GetMsg(kind == SaveKind.Single ? GameFiles.GdPlayer : GameFiles.MpCharBytes);
                    var after = c2.Msg;
                    var touched = new HashSet<int> { F.Name, F.AttrPoint, F.SkillPoint, F.PerkPoint, F.Property, F.Status, F.BodyParts, F.Buffs };
                    bool keep = true;
                    var fa = before.Fields.FindAll(f => !touched.Contains(f.Num));
                    var fb = after.Fields.FindAll(f => !touched.Contains(f.Num));
                    keep = fa.Count == fb.Count;
                    for (int i = 0; keep && i < fa.Count; i++) keep = fa[i].Num == fb[i].Num && Same(fa[i].Raw, fb[i].Raw);
                    Check(keep, $"人物数据里其他 {fa.Count} 项（背包、装备、天赋等）原样保留");

                    var ex1 = PMsg.Parse(orig);
                    Check(ex1.Fields.Count == s2.Root.Fields.Count, "文件顶层项数不变");
                    Log($"  文件大小 {orig.Length} -> {new FileInfo(copy).Length}");

                    // 背包测试用另一份副本（从原文件重新开始）
                    string invCopy = Path.Combine(tmpRoot, sub, Path.GetFileNameWithoutExtension(path) + "_inv" + Path.GetExtension(path));
                    File.Copy(path, invCopy, true);
                    InventoryTest(invCopy, kind, orig, Log, Check);
                }
                catch (Exception ex)
                {
                    fails++;
                    Log("  [失败] 出错: " + ex);
                }
                finally
                {
                    GameFiles.DataRoot = realRoot;
                    GameFiles.SkipGameCheck = false;
                }
            }

            Log("");
            Log(fails == 0 ? "全部通过" : $"有 {fails} 项失败");
            Log("临时文件在: " + tmpRoot);
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(true));
            return fails == 0 ? 0 : 1;
        }

        /// <summary>背包：改数量耐久、删除、复制、各类新物品、修好补满，存盘后读回核对。</summary>
        private static void InventoryTest(string copy, SaveKind kind, byte[] orig, Action<string> Log, Action<bool, string> Check)
        {
            Log("  --- 背包测试 ---");
            Check(ItemDb.Count > 500, $"物品表读进来了（{ItemDb.Count} 种）");
            var s = GameFiles.Load(GameFiles.Describe(copy, kind));
            var inv = s.Inventory;
            Check(inv != null && inv.Main != null, "找到身上的背包");
            if (inv?.Main == null) return;
            int cdField = kind == SaveKind.Single ? GameFiles.GdPlayer : GameFiles.MpCharBytes;
            Log($"  {inv.Main.Title} {inv.Main.W}x{inv.Main.H}，第一层 {inv.Main.Items.Count} 件，连里层一共 {inv.CountAll()} 件，装备栏 {inv.Equipped.Count} 件");
            foreach (var e in inv.Equipped) Log($"    装备 {e.Slot}: {e.Name} 耐久={e.DurabilityText} {e.StateText}");
            int unknown = 0;
            void CountUnknown(ContainerView c)
            {
                foreach (var it in c.Items)
                {
                    if (!it.Info.Known) { unknown++; Log("    物品表里没有: " + it.Id); }
                    var x = inv.InnerOf(it);
                    if (x != null) CountUnknown(x);
                }
            }
            CountUnknown(inv.Main);
            Check(unknown == 0, $"背包里的物品都能在物品表里查到名字（查不到 {unknown} 个）");
            int totalBefore = inv.CountAll();
            var main = inv.Main;

            // 1) 删东西腾出 7x10 的空地（顺便测删除）
            int deleted = 0, delCount = 0;
            while (main.FindSpot(7, 10) == null && main.Items.Count > 0)
            {
                var victim = main.Items.OrderByDescending(x => x.W * x.H).First();   // 先删占地最大的
                var vc = inv.InnerOf(victim);
                deleted += 1 + (vc != null ? CountAll(inv, vc) : 0);
                delCount++;
                main.Remove(victim);
            }
            Log($"  为腾地方删了 {delCount} 件（连里面的共 {deleted} 件）");

            // 2) 改数量和耐久
            var stackItem = main.Items.Find(x => x.Info.Stackable && x.Count < x.Info.Stack);
            var durItem = main.Items.Find(x => !x.Info.Stackable && x.Inventory == null && (stackItem == null || x.Msg != stackItem.Msg));
            if (stackItem != null) stackItem.Count = stackItem.Info.Stack;
            if (durItem != null) durItem.Durability = 55.5f;
            var touched = new HashSet<PMsg>();
            if (stackItem != null) touched.Add(stackItem.Msg);
            if (durItem != null) touched.Add(durItem.Msg);

            // 3) 加一个巨蜥皮背包（7x10，里面 22x36），新物品都放进它里面
            var bag = main.Place(ItemFactory.Create(ItemDb.Get(515), 1, s.WorldTime));
            Check(bag != null, "新背包放进了身上背包");
            if (bag == null) return;
            var box = inv.InnerOf(bag);
            Check(box != null && box.W == 22 && box.H == 36 && box.Items.Count == 0, "新背包是空的、22x36");
            var want = new (int id, int amount)[]
            {
                (0, 150), (210, 1), (300, 1), (420, 1), (491, 1), (704, 1), (810, 1), (501, 1), (1066, 1), (10001, 1), (1101, 1), (101, 40), (220, 3),
            };
            int addedNew = 0;
            foreach (var (id, amount) in want)
            {
                var info = ItemDb.Get(id);
                int left = amount;
                while (left > 0)
                {
                    int n = info.Stackable ? Math.Min(left, info.Stack) : 1;
                    var v = box.Place(ItemFactory.Create(info, n, s.WorldTime));
                    if (v == null) break;
                    addedNew++;
                    left -= n;
                }
                Check(left == 0, $"新物品放进去了：{info.Name} x{amount}");
            }

            // 4) 复制一个装着东西的背包（复制出来的应该是空的）
            var withInner = main.Items.Find(x => x.Inventory != null && x.Inventory.GetMsgs(IF.InvItems).Count > 0 && x.Msg != bag.Msg);
            bool cloned = false;
            if (withInner != null)
            {
                var cv = box.Place(ItemFactory.CloneEmpty(withInner.Msg));
                cloned = cv != null;
                Check(cv != null && cv.Inventory.GetMsgs(IF.InvItems).Count == 0 && cv.Id == withInner.Id, $"复制了一个空的 {withInner.Name}");
                Check(withInner.Inventory.GetMsgs(IF.InvItems).Count > 0, "原来那个背包里的东西还在");
            }

            // 5) 修好、补满：身上背包第二层的东西都修一遍
            int repaired = 0, filled = 0;
            foreach (var it in main.Items)
            {
                var c = inv.InnerOf(it);
                if (c == null || it.Msg == bag.Msg) continue;
                foreach (var x in c.Items) { if (x.Repair()) repaired++; if (x.Fill()) filled++; }
                touched.Add(it.Msg);
            }
            var keepBytes = new List<byte[]>();
            foreach (var it in main.Items) if (!touched.Contains(it.Msg) && it.Msg != bag.Msg) keepBytes.Add(it.Msg.ToBytes());
            Log($"  第二层修好 {repaired} 件、补满 {filled} 件");

            GameFiles.SkipGameCheck = true;
            GameFiles.Save(s);

            // ---------- 读回来核对 ----------
            var r = GameFiles.Load(GameFiles.Describe(copy, kind));
            var inv2 = r.Inventory;
            var cd2 = r.Root.GetMsg(cdField);
            int added = 1 + addedNew + (cloned ? 1 : 0);
            Check(inv2.CountAll() == totalBefore - deleted + added, $"物品总数对得上（{totalBefore} - {deleted} + {added} = {inv2.CountAll()}）");
            var bagInv = cd2.GetMsg(CharacterInventory.CdEquipment)?.GetMsg(CharacterInventory.EqBackpack)?.GetBytes(IF.Inventory);
            var f14 = cd2.GetBytes(CharacterInventory.CdInventory);
            if (bagInv != null && f14 != null) Check(Same(bagInv, f14), "身上背包的两份内容一致（装备栏那份和人物那份）");
            var main2 = inv2.Main;
            var bag2 = main2.Items.Find(x => x.Id == 515 && x.X == bag.X && x.Y == bag.Y);
            Check(bag2 != null, "新背包读回来还在原位置");
            if (bag2 == null) return;
            var box2 = inv2.InnerOf(bag2);
            var items2 = box2.Items;
            ItemView One(int id) => items2.Find(x => x.Id == id);
            List<float> P(int id) => One(id)?.Props ?? new List<float>();
            var wood = items2.FindAll(x => x.Id == 0);
            Check(wood.Count == 2 && wood.Sum(x => x.Count) == 150 && wood.Max(x => x.Count) == 100, "木材 150 个分成 100 + 50 两格");
            Check(P(210).Count == 3 && Math.Abs(P(210)[0] - r.WorldTime) < 0.01, "罐头的生产时间 = 现在");
            Check(P(300).Count == 1 && P(300)[0] == 100, "撬棍状态满（100）");
            var gun = P(420);
            Check(gun.Count == 14 && gun[0] == 400 && gun[1] == -1 && gun[4] == 1, "塔沃尔：14 项属性、枪况 400、没弹匣、连发");
            var air = One(491)?.Msg.GetMsgs(IF.Pairs).Find(x => x.GetString(IF.PairKey) == IF.AirPressureKey);
            Check(air != null && air.GetString(IF.PairValue) == "28", "气枪带满气压（28）");
            Check(P(704).Count == 2 && P(704)[0] == 100 && P(704)[1] == ItemDb.Get(704).ExtraInt("cap"), "弹匣是满的");
            Check(P(810).Count == 1 && P(810)[0] == 1800, "红点瞄准镜耐久满（1800）");
            var b501 = One(501);
            var b501inv = b501?.Inventory;
            Check(b501inv != null && b501inv.GetInt(IF.InvW) == 10 && b501inv.GetInt(IF.InvH) == 18 && b501inv.GetMsgs(IF.InvItems).Count == 0
                  && P(501).Count == 3 && P(501)[1] == 10 && P(501)[2] == 18, "探险背包：空的、10x18");
            Check(One(1066)?.Inventory?.GetLongList(IF.InvFeatureLimit).Contains(IF.FeatureBattery) == true, "充电箱只能放电池");
            Check(One(10001)?.Name.EndsWith("上衣") == true && One(10001).Info.Kind == ItemKind.Clothing, "衣服上衣编号 10001 认得出");
            Check(P(1101).Count == 2 && P(1101)[0] == 0 && P(1101)[1] == 0, "水壶是空的");
            Check(One(101)?.Count == 40, "子弹 40 发");
            Check(items2.FindAll(x => x.Id == 220).Count == 3, "绷带（不能叠）放了 3 件");
            // 不重叠、不出界
            bool fit = true;
            var occ = new int[box2.W, box2.H];
            foreach (var it in items2)
                for (int x = it.X; x < it.X + it.W; x++)
                    for (int y = it.Y; y < it.Y + it.H; y++)
                    {
                        if (x < 0 || y < 0 || x >= box2.W || y >= box2.H) { fit = false; continue; }
                        if (++occ[x, y] > 1) fit = false;
                    }
            Check(fit, "新放的东西都在格子里、互不重叠");
            // 没碰过的物品一字不差
            var now = main2.Items.Select(x => x.Msg.ToBytes()).ToList();
            int kept = keepBytes.Count(k => now.Any(n => Same(n, k)));
            Check(kept == keepBytes.Count, $"没动过的 {keepBytes.Count} 件物品原样保留（{kept}）");
            if (stackItem != null) Check(main2.Items.Exists(x => x.Id == stackItem.Id && x.Count == stackItem.Info.Stack), $"{stackItem.Name} 数量补到 {stackItem.Info.Stack}");
            if (durItem != null) Check(main2.Items.Exists(x => x.Id == durItem.Id && Math.Abs(x.Durability - 55.5f) < 0.01), $"{durItem.Name} 耐久改成 55.5");
            // 人物数据里除了背包（14）和装备（18）别的都不变
            var before = PMsg.Parse(orig).GetMsg(cdField);
            var ignore = new HashSet<int> { CharacterInventory.CdInventory, CharacterInventory.CdEquipment };
            var fa = before.Fields.FindAll(f => !ignore.Contains(f.Num));
            var fb = cd2.Fields.FindAll(f => !ignore.Contains(f.Num));
            bool keep = fa.Count == fb.Count;
            for (int i = 0; keep && i < fa.Count; i++) keep = fa[i].Num == fb[i].Num && Same(fa[i].Raw, fb[i].Raw);
            Check(keep, "人物数据里背包以外的部分原样保留");
            // 装备栏里除了背包，其他装备不变
            var eqA = before.GetMsg(CharacterInventory.CdEquipment);
            var eqB = cd2.GetMsg(CharacterInventory.CdEquipment);
            if (eqA != null && eqB != null)
            {
                var ea = eqA.Fields.FindAll(f => f.Num != CharacterInventory.EqBackpack);
                var eb = eqB.Fields.FindAll(f => f.Num != CharacterInventory.EqBackpack);
                bool same = ea.Count == eb.Count;
                for (int i = 0; same && i < ea.Count; i++) same = ea[i].Num == eb[i].Num && Same(ea[i].Raw, eb[i].Raw);
                Check(same, "其他装备（武器等）原样保留");
            }
            Log($"  背包测试后文件大小 {orig.Length} -> {new FileInfo(copy).Length}");
        }

        private static int CountAll(CharacterInventory inv, ContainerView c)
        {
            int n = 0;
            foreach (var it in c.Items) { n++; var x = inv.InnerOf(it); if (x != null) n += CountAll(inv, x); }
            return n;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
