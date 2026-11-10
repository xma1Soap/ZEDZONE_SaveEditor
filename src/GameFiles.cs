using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ZedZoneSaveEditor
{
    internal enum SaveKind { Single, Multi }

    /// <summary>列表里的一项存档。</summary>
    internal sealed class SaveEntry
    {
        public SaveKind Kind;
        public string Path;
        public string CharName = "";
        public string WorldId = "";
        public int Slot = -1;
        public DateTime? SavedAt;
        public string Extra = "";     // 列表里额外显示的一列（上次世界、自动存档等）
        public string Error;          // 读不出来时的原因

        public string SlotText => Slot < 0 ? "?" : Slot == GameFiles.AutoSaveSlot ? "自动存档" : "槽位 " + (Slot + 1);
    }

    /// <summary>打开后的一份存档。</summary>
    internal sealed class LoadedSave
    {
        public SaveEntry Entry;
        public byte[] Original;
        public long FileLength;
        public DateTime FileWriteUtc;
        public PMsg Root;
        public CharacterView Character;
        public CharacterInventory Inventory;     // 身上的背包和装备
        public float WorldTime;                  // 世界时间，判断是否还在流血用
        public readonly List<string> Warnings = new List<string>();
    }

    internal static class GameFiles
    {
        public const int AutoSaveSlot = 5;      // GameDataManager.autoSaveSlotIndex

        // GameData（单机/房主世界存档）字段号
        public const int GdId = 1, GdPlayer = 2, GdInGameTime = 3, GdCreated = 5, GdSaving = 17, GdSlot = 18;
        // MpCharacterProfile（联机角色档）字段号
        public const int MpVersion = 1, MpCharBytes = 2, MpLastWorld = 3, MpSavedAt = 4, MpName = 5, MpId = 6,
            MpWorldTime = 8, MpDead = 11, MpLastWorldName = 14, MpHardcore = 16;

        /// <summary>游戏数据根目录：%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE（可用 --root 改）。</summary>
        public static string DataRoot = DefaultRoot();

        public static string SingleDir => Path.Combine(DataRoot, "GameData", "GameData");
        public static string MultiDir => Path.Combine(DataRoot, "GameData", "MpCharacters");
        public static string BackupRoot => Path.Combine(DataRoot, "SaveEditorBackups");

        private static string DefaultRoot()
        {
            string low = null;
            try
            {
                var id = new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16"); // FOLDERID_LocalAppDataLow
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
                {
                    low = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                }
            }
            catch { }
            if (string.IsNullOrEmpty(low))
                low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            return Path.Combine(low, "LevenLiu", "ZEDZONE");
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

        /// <summary>只给自检用：自检改的是临时副本，游戏开着也没关系。</summary>
        public static bool SkipGameCheck;

        public static bool GameRunning()
        {
            if (SkipGameCheck) return false;
            try { return Process.GetProcessesByName("ZEDZONE").Length > 0; }
            catch { return false; }
        }

        public static DateTime? FromFileTime(long t)
        {
            if (t <= 0) return null;
            try { return DateTime.FromFileTime(t); } catch { return null; }
        }

        // ---------- 列表 ----------

        public static List<SaveEntry> ListSingle()
        {
            var r = new List<SaveEntry>();
            if (!Directory.Exists(SingleDir)) return r;
            foreach (var path in Directory.GetFiles(SingleDir, "*.game"))
            {
                if (!path.EndsWith(".game", StringComparison.OrdinalIgnoreCase)) continue;
                r.Add(Describe(path, SaveKind.Single));
            }
            r.Sort((a, b) => Nullable.Compare(b.SavedAt, a.SavedAt));
            return r;
        }

        public static List<SaveEntry> ListMulti()
        {
            var r = new List<SaveEntry>();
            if (!Directory.Exists(MultiDir)) return r;
            foreach (var path in Directory.GetFiles(MultiDir, "*.mpchar"))
            {
                if (!path.EndsWith(".mpchar", StringComparison.OrdinalIgnoreCase)) continue;
                r.Add(Describe(path, SaveKind.Multi));
            }
            r.Sort((a, b) => Nullable.Compare(b.SavedAt, a.SavedAt));
            return r;
        }

        /// <summary>按扩展名猜，猜不出就看内容：单机档第 1 项是文字（世界 id），角色档第 1 项是数字（版本号）。</summary>
        public static SaveKind GuessKind(string path)
        {
            string n = Path.GetFileName(path).ToLowerInvariant();
            if (n.Contains(".mpchar")) return SaveKind.Multi;
            if (n.Contains(".game")) return SaveKind.Single;
            var root = PMsg.Parse(File.ReadAllBytes(path));
            var f1 = root.First(1);
            return f1 != null && f1.Wire == 0 ? SaveKind.Multi : SaveKind.Single;
        }

        public static SaveEntry Describe(string path, SaveKind kind)
        {
            var e = new SaveEntry { Kind = kind, Path = path };
            try
            {
                var root = PMsg.Parse(File.ReadAllBytes(path));
                FillEntry(e, root);
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                e.CharName = "（读取失败）";
            }
            return e;
        }

        private static void FillEntry(SaveEntry e, PMsg root)
        {
            if (e.Kind == SaveKind.Single)
            {
                e.WorldId = root.GetString(GdId) ?? "";
                e.Slot = root.Has(GdSlot) ? root.GetInt(GdSlot) : SlotFromName(e.Path);
                long t = root.GetLong(GdSaving);
                if (t <= 0) t = root.GetLong(GdCreated);
                e.SavedAt = FromFileTime(t);
                var cd = root.GetMsg(GdPlayer);
                e.CharName = cd?.GetString(F.Name) ?? "（没有人物数据）";
                e.Extra = e.SlotText;
            }
            else
            {
                e.CharName = root.GetString(MpName) ?? "";
                e.SavedAt = FromFileTime(root.GetLong(MpSavedAt));
                string world = root.GetString(MpLastWorldName) ?? "";
                if (root.GetBool(MpDead)) world = "【已死亡】" + world;
                e.Extra = world;
                e.WorldId = root.GetString(MpLastWorld) ?? "";
            }
        }

        private static int SlotFromName(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path);
            int i = n.LastIndexOf("_SlotIndex", StringComparison.Ordinal);
            if (i >= 0 && int.TryParse(n.Substring(i + "_SlotIndex".Length), out int s)) return s;
            return -1;
        }

        // ---------- 打开 ----------

        public static LoadedSave Load(SaveEntry e)
        {
            var fi = new FileInfo(e.Path);
            var s = new LoadedSave
            {
                Entry = e,
                Original = File.ReadAllBytes(e.Path),
                FileLength = fi.Length,
                FileWriteUtc = fi.LastWriteTimeUtc,
            };
            s.Root = PMsg.Parse(s.Original);
            FillEntry(e, s.Root);
            var cd = s.Root.GetMsg(e.Kind == SaveKind.Single ? GdPlayer : MpCharBytes);
            if (cd == null) throw new FormatException("这个文件里没有人物数据");
            s.Character = new CharacterView(cd);

            if (e.Kind == SaveKind.Single)
            {
                s.WorldTime = s.Root.GetFloat(GdInGameTime);
                CheckArchiveOverride(s);
            }
            else
            {
                s.WorldTime = s.Root.GetFloat(MpWorldTime);
                if (s.Root.GetBool(MpDead))
                    s.Warnings.Add("这个角色已经死亡（硬核模式），游戏不会再让它进世界。修改器不会帮你复活它。");
            }
            try { s.Inventory = new CharacterInventory(cd, s.WorldTime); }
            catch (Exception ex) { s.Warnings.Add("背包数据读不懂，背包页不能用：" + ex.Message); }
            if (s.Character.SkillNodeCount > Rules.MaxLegalSkillNodes)
                s.Warnings.Add($"已解锁技能节点 {s.Character.SkillNodeCount} 个，超过游戏认可的 {Rules.MaxLegalSkillNodes} 个。");
            return s;
        }

        /// <summary>
        /// 进单机世界时，如果这个角色的角色档（.mpchar）最后是在别的世界存的、而且比这个存档新，
        /// 游戏会直接用角色档里的人物（WorldEntry.ResolveSnapshotCharacterSource），这里改的就不生效。
        /// </summary>
        private static void CheckArchiveOverride(LoadedSave s)
        {
            string id = s.Character.Id;
            if (string.IsNullOrEmpty(id)) return;
            string mp = Path.Combine(MultiDir, id + ".mpchar");
            if (!File.Exists(mp)) return;
            try
            {
                var env = PMsg.Parse(File.ReadAllBytes(mp));
                if (env.GetBool(MpDead)) return;
                string last = env.GetString(MpLastWorld);
                if (string.IsNullOrEmpty(last) || last == s.Entry.WorldId) return;
                long worldTime = s.Root.GetLong(GdSaving);
                if (worldTime == 0) worldTime = s.Root.GetLong(GdCreated);
                if (env.GetLong(MpSavedAt) <= worldTime) return;
                string lastName = env.GetString(MpLastWorldName);
                s.Warnings.Add($"这个角色后来在别的世界（{(string.IsNullOrEmpty(lastName) ? "?" : lastName)}）玩过，那边更新。" +
                               "进这个单机存档时，游戏会用「联机角色」页里的同名角色，这里改的不会生效。要改请去「联机角色」页。");
            }
            catch { }
        }

        // ---------- 保存 ----------

        /// <summary>先备份，再写入。返回备份文件路径。</summary>
        public static string Save(LoadedSave s)
        {
            if (GameRunning())
                throw new InvalidOperationException("游戏正在运行。游戏开着的时候会定时存档、退出时也会存档，会把这里改的覆盖掉。请先完全退出游戏再保存。");

            var fi = new FileInfo(s.Entry.Path);
            if (!fi.Exists) throw new IOException("文件不见了：" + s.Entry.Path);
            if (fi.Length != s.FileLength || fi.LastWriteTimeUtc != s.FileWriteUtc)
                throw new IOException("这个文件在你打开之后被改过（可能是游戏存了档）。为了不丢进度，请点「撤销改动」重新读取后再改。");

            s.Inventory?.SyncBeforeSave();
            byte[] bytes = s.Root.ToBytes();
            Validate(bytes, s);

            string backup = Backup(s.Entry.Path);
            string tmp = s.Entry.Path + ".editor-tmp";
            File.WriteAllBytes(tmp, bytes);
            try
            {
                File.Replace(tmp, s.Entry.Path, null);
            }
            catch
            {
                File.Copy(tmp, s.Entry.Path, true);
                File.Delete(tmp);
            }
            return backup;
        }

        /// <summary>写盘前自检：新数据能完整读回，人物以外的部分和原来一字不差。</summary>
        private static void Validate(byte[] bytes, LoadedSave s)
        {
            var root = PMsg.Parse(bytes);
            int cdField = s.Entry.Kind == SaveKind.Single ? GdPlayer : MpCharBytes;
            var cd = root.GetMsg(cdField) ?? throw new FormatException("自检失败：写出的数据里找不到人物");
            foreach (int n in new[] { F.Status, F.Property, F.BodyParts })
            {
                var sub = cd.GetMsg(n);
                if (n == F.BodyParts && sub != null) sub.GetMsgs(F.BodyPartArray);
            }
            cd.GetMsgs(F.Buffs);
            ValidateItems(cd, PMsg.Parse(s.Original).GetMsg(cdField));

            // 允许变的只有人物数据本身（联机档还有外层的角色名），其他顶层字段必须一字不差
            bool Allowed(int num) => num == cdField || (s.Entry.Kind == SaveKind.Multi && num == MpName);
            var a = PMsg.Parse(s.Original).Fields.FindAll(f => !Allowed(f.Num));
            var b = root.Fields.FindAll(f => !Allowed(f.Num));
            if (a.Count != b.Count) throw new FormatException("自检失败：人物以外的数据数量变了");
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Num != b[i].Num || !Same(a[i].Raw, b[i].Raw))
                    throw new FormatException($"自检失败：人物以外的数据（字段 {a[i].Num}）被意外改动");
            }
        }

        /// <summary>物品数据整棵读一遍（背包里套背包也读到底），并检查身上背包的两份内容还一致。</summary>
        internal static void ValidateItems(PMsg cd, PMsg origCd)
        {
            int count = 0;
            void Item(PMsg it, int depth)
            {
                if (++count > 200000 || depth > 64) throw new FormatException("自检失败：物品层数或数量不对劲");
                it.GetInt(IF.ItemId);
                it.GetFloatList(IF.Props);
                if (it.GetLongList(IF.Coor).Count > 2) throw new FormatException("自检失败：物品坐标格式不对");
                it.GetMsgs(IF.Pairs);
                var inv = it.GetMsg(IF.Inventory);
                if (inv != null) Inv(inv, depth + 1);
            }
            void Inv(PMsg inv, int depth)
            {
                foreach (var it in inv.GetMsgs(IF.InvItems)) Item(it, depth);
            }
            var main = cd.GetMsg(CharacterInventory.CdInventory);
            if (main != null) Inv(main, 0);
            var eq = cd.GetMsg(CharacterInventory.CdEquipment);
            if (eq != null)
                foreach (var field in EquipRules.Fields)
                    foreach (var it in eq.GetMsgs(field)) Item(it, 0);

            // 身上背包的格子和人物 inventoryData 是同一份：原来一样（或者原来没背背包）的，现在背着背包也必须一样
            byte[] Pair(PMsg c, out byte[] bag)
            {
                var b = c?.GetMsg(CharacterInventory.CdEquipment)?.GetMsg(CharacterInventory.EqBackpack);
                bag = b != null && b.GetInt(IF.ItemId) >= 0 ? b.GetBytes(IF.Inventory) : null;
                return c?.GetBytes(CharacterInventory.CdInventory);
            }
            var o14 = Pair(origCd, out var oBag);
            var n14 = Pair(cd, out var nBag);
            bool wasLinked = o14 != null && (oBag == null || Same(o14, oBag));
            if (wasLinked && nBag != null && (n14 == null || !Same(n14, nBag)))
                throw new FormatException("自检失败：身上背包的两份内容对不上");
            if (o14 != null && n14 == null)
                throw new FormatException("自检失败：随身物品栏不见了");
        }

        private static bool Same(byte[] x, byte[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        public static string Backup(string path)
        {
            string dir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, Path.GetFileName(path));
            int n = 2;
            while (File.Exists(dst)) dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_" + n++ + Path.GetExtension(path));
            File.Copy(path, dst);
            return dst;
        }
    }
}
