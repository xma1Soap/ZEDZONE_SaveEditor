using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ZedZoneSaveEditor
{
    internal sealed class MainForm : Form
    {
        // 一个可编辑的数字项：记住打开时的值，保存时只写改过的
        private sealed class NumBinding
        {
            public NumericUpDown Box;
            public decimal Original;
            public Action<float> Apply;
            public bool Changed => Box.Value != Original;
        }

        private readonly TabControl _tabs = new TabControl();
        private readonly ListView _listSingle = MakeList("角色", "槽位", "存档时间", "世界");
        private readonly ListView _listMulti = MakeList("角色", "上次所在世界", "存档时间");

        private readonly Label _lblTitle = new Label();
        private readonly Label _lblPath = new Label();
        private readonly Label _lblWarn = new Label();
        private readonly Panel _editor = new Panel();
        private readonly TabControl _rightTabs = new TabControl();
        private readonly InventoryPanel _invPanel = new InventoryPanel();
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _gameState = new ToolStripStatusLabel();
        private readonly Button _btnHeal = new Button();
        private readonly Button _btnRevert = new Button();
        private readonly Button _btnSave = new Button();

        private readonly TextBox _txtName = new TextBox();
        private readonly Label _lblInfo = new Label();
        private readonly CheckBox _chkClearBuffs = new CheckBox();
        private readonly Label _lblBuffs = new Label();
        private readonly TableLayoutPanel _bodyTable = new TableLayoutPanel();

        private NumericUpDown _numAttrPt, _numSkillPt, _numPerkPt, _numStr, _numAgi, _numCon;
        private NumericUpDown _numHp, _numEnergy, _numFatigue, _numHunger, _numThirst, _numBlood, _numTemp, _numRad;
        private Label _hintHp, _hintEnergy, _hintHunger, _hintThirst, _hintAttr;
        private readonly List<(BodyPart part, NumericUpDown box, Label max)> _bodyRows = new List<(BodyPart, NumericUpDown, Label)>();

        private readonly List<NumBinding> _bindings = new List<NumBinding>();
        private LoadedSave _current;
        private string _origName = "";
        private bool _healBodyParts;
        private bool _loading;
        private bool _switchingSelection;
        private bool _snapshotMode;

        private const string AppTitle = "隔离区 ZED ZONE 人物存档修改器 v1.3";

        public MainForm()
        {
            Text = AppTitle;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1300, 880);
            MinimumSize = new Size(1000, 640);

            BuildLayout();
            BuildEditor();
            SetEditorEnabled(false);

            var timer = new Timer { Interval = 2000 };
            timer.Tick += (s, e) => RefreshGameState();
            timer.Start();
            RefreshGameState();

            Load += (s, e) => RefreshLists();
            FormClosing += (s, e) =>
            {
                if (!_snapshotMode && IsDirty() && !ConfirmDiscard()) e.Cancel = true;
            };
        }

        // ================= 布局 =================

        private static ListView MakeList(params string[] cols)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                Dock = DockStyle.Fill,
            };
            foreach (var c in cols) lv.Columns.Add(c, 100);
            return lv;
        }

        private void BuildLayout()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Controls.Add(split);
            var strip = new StatusStrip();
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            strip.Items.Add(_status);
            strip.Items.Add(_gameState);
            Controls.Add(strip);

            // ---- 左边：存档列表 ----
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.Panel1.Controls.Add(left);

            _tabs.Dock = DockStyle.Fill;
            var tpS = new TabPage("单机存档");
            var tpM = new TabPage("联机角色");
            tpS.Controls.Add(_listSingle);
            tpM.Controls.Add(_listMulti);
            _tabs.TabPages.Add(tpS);
            _tabs.TabPages.Add(tpM);
            left.Controls.Add(_tabs, 0, 0);

            var tip = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 6, 3, 3),
                Text = "单机存档：自己开的世界（单机或当房主），每个槽位一份人物。\n" +
                       "联机角色：去别人房间玩时用的角色，存在你自己电脑上。\n" +
                       "改之前请完全退出游戏。每次保存前都会自动备份。"
            };
            left.Controls.Add(tip, 0, 1);

            var btns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            btns.Controls.Add(MakeButton("刷新列表", (s, e) => { if (!IsDirty() || ConfirmDiscard()) RefreshLists(); }));
            btns.Controls.Add(MakeButton("打开其他文件…", (s, e) => OpenOtherFile()));
            btns.Controls.Add(MakeButton("打开存档文件夹", (s, e) => OpenFolder(_tabs.SelectedIndex == 0 ? GameFiles.SingleDir : GameFiles.MultiDir)));
            btns.Controls.Add(MakeButton("打开备份文件夹", (s, e) => OpenFolder(GameFiles.BackupRoot)));
            left.Controls.Add(btns, 0, 2);

            _listSingle.SelectedIndexChanged += OnListSelect;
            _listMulti.SelectedIndexChanged += OnListSelect;

            // ---- 右边：编辑区 ----
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6) };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.Panel2.Controls.Add(right);

            _lblTitle.AutoSize = true;
            _lblTitle.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            _lblTitle.Text = "在左边选一个存档";
            _lblPath.AutoSize = false;
            _lblPath.AutoEllipsis = true;
            _lblPath.Dock = DockStyle.Fill;
            _lblPath.Height = 20;
            _lblPath.ForeColor = Color.DimGray;
            _lblWarn.AutoSize = true;
            _lblWarn.ForeColor = Color.FromArgb(180, 40, 20);
            _lblWarn.MaximumSize = new Size(760, 0);
            right.Controls.Add(_lblTitle, 0, 0);
            right.Controls.Add(_lblPath, 0, 1);
            right.Controls.Add(_lblWarn, 0, 2);

            _editor.Dock = DockStyle.Fill;
            _editor.AutoScroll = true;
            _rightTabs.Dock = DockStyle.Fill;
            var tpChar = new TabPage("人物");
            tpChar.Controls.Add(_editor);
            var tpInv = new TabPage("背包物品");
            tpInv.Controls.Add(_invPanel);
            _rightTabs.TabPages.Add(tpChar);
            _rightTabs.TabPages.Add(tpInv);
            _rightTabs.SelectedIndexChanged += (s, e) => _btnHeal.Visible = _rightTabs.SelectedIndex == 0;
            right.Controls.Add(_rightTabs, 0, 3);
            _invPanel.Changed += UpdateTitleDirty;
            _invPanel.Status += SetStatus;

            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            _btnSave.Text = "保存修改";
            _btnSave.Font = new Font(Font, FontStyle.Bold);
            _btnSave.AutoSize = true;
            _btnSave.Padding = new Padding(10, 2, 10, 2);
            _btnSave.Click += (s, e) => DoSave();
            _btnRevert.Text = "撤销改动";
            _btnRevert.AutoSize = true;
            _btnRevert.Click += (s, e) => { if (_current != null && (!IsDirty() || ConfirmDiscard())) Reload(); };
            _btnHeal.Text = "一键治疗（回满）";
            _btnHeal.AutoSize = true;
            _btnHeal.Click += (s, e) => DoHeal();
            bottom.Controls.Add(_btnSave);
            bottom.Controls.Add(_btnRevert);
            bottom.Controls.Add(_btnHeal);
            right.Controls.Add(bottom, 0, 4);

            Shown += (s, e) => { try { split.SplitterDistance = Math.Min(340, Width / 3); } catch { } };
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            return b;
        }

        private void BuildEditor()
        {
            var flow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _editor.Controls.Add(flow);

            // ---- 基本 ----
            var gBase = Group("基本信息", out var tBase);
            AddRow(tBase, "名字", _txtName, null);
            _txtName.Width = 220;
            _txtName.MaxLength = 32;
            _txtName.TextChanged += (s, e) => MarkDirtyLater();
            _lblInfo.AutoSize = true;
            _lblInfo.ForeColor = Color.DimGray;
            _lblInfo.MaximumSize = new Size(380, 0);
            tBase.Controls.Add(_lblInfo, 0, tBase.RowCount);
            tBase.SetColumnSpan(_lblInfo, 3);
            tBase.RowCount++;

            // ---- 点数 ----
            var gPts = Group("可分配点数（进游戏后自己去加）", out var tPts);
            _numAttrPt = AddNum(tPts, "属性点", 0, 9999, 0, null, v => _current.Character.AttrPoint = (int)v);
            _numSkillPt = AddNum(tPts, "技能点", 0, 9999, 0, null, v => _current.Character.SkillPoint = (int)v);
            _numPerkPt = AddNum(tPts, "天赋点", 0, 9999, 0, null, v => _current.Character.PerkPoint = (int)v);

            // ---- 属性 ----
            var gAttr = Group("属性", out var tAttr);
            _numStr = AddNum(tAttr, "力量", 1, Rules.MaxLegalAttr, 0, null, v => _current.Character.Strength = (int)v);
            _numAgi = AddNum(tAttr, "敏捷", 1, Rules.MaxLegalAttr, 0, null, v => _current.Character.Agility = (int)v);
            _numCon = AddNum(tAttr, "体质", 1, Rules.MaxLegalAttr, 0, null, v => _current.Character.Constitution = (int)v);
            _numCon.ValueChanged += (s, e) => UpdateHints();
            _hintAttr = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(340, 0),
                Text = $"单项最高 {Rules.MaxLegalAttr}：超过的话游戏会判定角色被改过（上传给别人雇佣时不收）。\n体质影响血量、饱食、水分、体力和身体各部位的上限。" };
            tAttr.Controls.Add(_hintAttr, 0, tAttr.RowCount);
            tAttr.SetColumnSpan(_hintAttr, 3);
            tAttr.RowCount++;

            // ---- 状态 ----
            var gStat = Group("当前状态", out var tStat);
            _numHp = AddNum(tStat, "血量", 0, 1000000, 1, _hintHp = Hint(), v => _current.Character.SetStatus(F.Hp, v));
            _numEnergy = AddNum(tStat, "体力", 0, 100000, 1, _hintEnergy = Hint(), v => _current.Character.SetStatus(F.Energy, v));
            _numFatigue = AddNum(tStat, "精力", 0, 100, 1, Hint("0～100，越高越精神，睡觉会涨"), v => _current.Character.SetStatus(F.Fatigue, v));
            _numHunger = AddNum(tStat, "饱食", 0, 100000, 1, _hintHunger = Hint(), v => _current.Character.SetStatus(F.Hunger, v));
            _numThirst = AddNum(tStat, "水分", 0, 100000, 1, _hintThirst = Hint(), v => _current.Character.SetStatus(F.Thirst, v));
            _numBlood = AddNum(tStat, "血液量", 0, 100000, 0, Hint($"正常 {Rules.MaxBlood:0}，低于 {Rules.DeadBlood:0} 会死"), v => _current.Character.SetStatus(F.BloodVolume, v));
            _numTemp = AddNum(tStat, "体温", 0, 60, 1, Hint($"正常 {Rules.NormalTemperature}（游戏里 {Rules.MinTemperature}～{Rules.MaxTemperature}）"), v => _current.Character.SetStatus(F.BodyTemperature, v));
            _numRad = AddNum(tStat, "辐射剂量", 0, 1000000, 1, Hint("0 = 没有辐射"), v => _current.Character.SetStatus(F.Radiation, v));

            // ---- 身体部位 ----
            var gBody = Group("身体部位血量", out var tBody);
            _bodyTable.AutoSize = true;
            _bodyTable.ColumnCount = 4;
            tBody.Controls.Add(_bodyTable, 0, 0);
            tBody.SetColumnSpan(_bodyTable, 3);

            // ---- 状态效果 ----
            var gBuff = Group("状态效果（buff / debuff）", out var tBuff);
            _lblBuffs.AutoSize = true;
            _lblBuffs.MaximumSize = new Size(260, 0);
            tBuff.Controls.Add(_lblBuffs, 0, 0);
            tBuff.SetColumnSpan(_lblBuffs, 3);
            _chkClearBuffs.Text = "保存时全部清除";
            _chkClearBuffs.AutoSize = true;
            tBuff.Controls.Add(_chkClearBuffs, 0, 1);
            tBuff.SetColumnSpan(_chkClearBuffs, 3);
            var buffHint = Hint("中毒、感染这类坏效果和吃饱这类好效果会一起去掉。");
            tBuff.Controls.Add(buffHint, 0, 2);
            tBuff.SetColumnSpan(buffHint, 3);

            flow.Controls.Add(gBase, 0, 0);
            flow.Controls.Add(gPts, 1, 0);
            flow.Controls.Add(gAttr, 0, 1);
            flow.Controls.Add(gBuff, 1, 1);
            flow.Controls.Add(gStat, 0, 2);
            flow.Controls.Add(gBody, 1, 2);
        }

        private static GroupBox Group(string title, out TableLayoutPanel table)
        {
            var g = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8), Margin = new Padding(4), Dock = DockStyle.Fill };
            table = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.Controls.Add(table);
            return g;
        }

        private static Label Hint(string text = "") =>
            new Label { AutoSize = true, ForeColor = Color.DimGray, Text = text, MaximumSize = new Size(250, 0), Anchor = AnchorStyles.Left };

        private static void AddRow(TableLayoutPanel t, string label, Control c, Label hint)
        {
            int row = t.RowCount;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
            c.Anchor = AnchorStyles.Left;
            t.Controls.Add(c, 1, row);
            if (hint != null) t.Controls.Add(hint, 2, row);
            t.RowCount = row + 1;
        }

        private NumericUpDown AddNum(TableLayoutPanel t, string label, decimal min, decimal max, int decimals, Label hint, Action<float> apply)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 110, ThousandsSeparator = false, TextAlign = HorizontalAlignment.Right };
            n.ValueChanged += (s, e) => MarkDirtyLater();
            AddRow(t, label, n, hint);
            _bindings.Add(new NumBinding { Box = n, Apply = apply });
            return n;
        }

        // ================= 列表 =================

        private void RefreshLists()
        {
            _switchingSelection = true;
            try
            {
                FillList(_listSingle, GameFiles.ListSingle(), e => new[] { e.CharName, e.SlotText, Time(e.SavedAt), Short(e.WorldId) });
                FillList(_listMulti, GameFiles.ListMulti(), e => new[] { e.CharName, e.Extra, Time(e.SavedAt) });
            }
            finally { _switchingSelection = false; }

            int s = _listSingle.Items.Count, m = _listMulti.Items.Count;
            _tabs.TabPages[0].Text = $"单机存档 ({s})";
            _tabs.TabPages[1].Text = $"联机角色 ({m})";
            if (s == 0 && m > 0 && _current == null) _tabs.SelectedIndex = 1;
            SetStatus(s == 0 && m == 0
                ? "没找到存档。游戏存档应该在：" + GameFiles.DataRoot
                : $"找到 {s} 个单机存档、{m} 个联机角色。");
            if (s == 0 && !Directory.Exists(GameFiles.SingleDir))
                _listSingle.Items.Add(new ListViewItem(new[] { "（还没有单机存档）", "", "", "" }) { ForeColor = Color.Gray });
        }

        private static void FillList(ListView lv, List<SaveEntry> entries, Func<SaveEntry, string[]> cols)
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var e in entries)
            {
                var it = new ListViewItem(cols(e)) { Tag = e };
                if (e.Error != null) it.ForeColor = Color.Gray;
                lv.Items.Add(it);
            }
            foreach (ColumnHeader c in lv.Columns) c.Width = -2;
            lv.EndUpdate();
        }

        private static string Time(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm") ?? "";
        private static string Short(string id) => string.IsNullOrEmpty(id) ? "" : id.Length > 8 ? id.Substring(0, 8) : id;

        private void OnListSelect(object sender, EventArgs e)
        {
            if (_switchingSelection) return;
            var lv = (ListView)sender;
            if (lv.SelectedItems.Count == 0) return;
            if (!(lv.SelectedItems[0].Tag is SaveEntry entry)) return;
            if (_current != null && _current.Entry.Path == entry.Path) return;
            if (IsDirty() && !ConfirmDiscard())
            {
                _switchingSelection = true;
                lv.SelectedItems.Clear();
                _switchingSelection = false;
                return;
            }
            Open(entry);
        }

        private void OpenOtherFile()
        {
            if (IsDirty() && !ConfirmDiscard()) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "选一个存档文件",
                Filter = "隔离区存档 (*.game;*.mpchar)|*.game;*.mpchar|所有文件 (*.*)|*.*",
                InitialDirectory = Directory.Exists(GameFiles.SingleDir) ? GameFiles.SingleDir : GameFiles.DataRoot,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var kind = GameFiles.GuessKind(dlg.FileName);
                    Open(new SaveEntry { Kind = kind, Path = dlg.FileName });
                }
                catch (Exception ex) { Error("打不开这个文件：" + ex.Message); }
            }
        }

        private static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        // ================= 打开 / 显示 =================

        private void Open(SaveEntry entry)
        {
            try
            {
                var s = GameFiles.Load(entry);
                ShowSave(s);
                SetStatus("已读取：" + Path.GetFileName(entry.Path));
            }
            catch (Exception ex)
            {
                _current = null;
                SetEditorEnabled(false);
                _lblTitle.Text = "读取失败";
                _lblPath.Text = entry.Path;
                _lblWarn.Text = ex.Message;
            }
        }

        private void Reload()
        {
            if (_current == null) return;
            Open(_current.Entry);
        }

        private void ShowSave(LoadedSave s)
        {
            _loading = true;
            try
            {
                _current = s;
                var c = s.Character;
                var e = s.Entry;
                _lblTitle.Text = (e.Kind == SaveKind.Single ? "单机存档 · " + e.SlotText : "联机角色") + " · " + c.Name;
                _lblPath.Text = e.Path;
                _lblWarn.Text = string.Join("\n", s.Warnings);

                _origName = c.Name;
                _txtName.Text = c.Name;
                _lblInfo.Text = $"性别：{(c.Gender == 1 ? "女" : "男")}　　硬核：{(c.Hardcore ? "是" : "否")}\n已学天赋 {c.PerkCount} 个，技能节点 {c.SkillNodeCount} 个\n角色 ID：{c.Id}";

                Set(_numAttrPt, c.AttrPoint);
                Set(_numSkillPt, c.SkillPoint);
                Set(_numPerkPt, c.PerkPoint);
                Set(_numStr, c.Strength);
                Set(_numAgi, c.Agility);
                Set(_numCon, c.Constitution);
                Set(_numHp, c.GetStatus(F.Hp));
                Set(_numEnergy, c.GetStatus(F.Energy));
                Set(_numFatigue, c.GetStatus(F.Fatigue));
                Set(_numHunger, c.GetStatus(F.Hunger));
                Set(_numThirst, c.GetStatus(F.Thirst));
                Set(_numBlood, c.GetStatus(F.BloodVolume));
                Set(_numTemp, c.GetStatus(F.BodyTemperature));
                Set(_numRad, c.GetStatus(F.Radiation));

                BuildBodyRows(s);
                _invPanel.Bind(s);

                var buffs = c.BuffIds;
                _lblBuffs.Text = buffs.Count == 0 ? "现在没有状态效果。" : $"现在有 {buffs.Count} 个（编号 {string.Join("、", buffs)}）。";
                _chkClearBuffs.Checked = false;
                _chkClearBuffs.Enabled = buffs.Count > 0;
                _healBodyParts = false;

                SetEditorEnabled(true);
            }
            finally { _loading = false; }
            UpdateHints();
        }

        private void BuildBodyRows(LoadedSave s)
        {
            _bodyTable.SuspendLayout();
            _bodyTable.Controls.Clear();
            _bodyTable.RowCount = 0;
            _bindings.RemoveAll(b => _bodyRows.Any(r => r.box == b.Box));
            _bodyRows.Clear();
            foreach (var part in s.Character.BodyParts)
            {
                var p = part;
                var n = new NumericUpDown { Minimum = 0, Maximum = 100000, DecimalPlaces = 1, Width = 90, TextAlign = HorizontalAlignment.Right };
                var max = new Label { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
                string state = "";
                if (p.BleedStop > s.WorldTime && p.BleedStop > 0) state = "流血中";
                if (p.HasBandage) state = state.Length > 0 ? state + "、已包扎" : "已包扎";
                var st = new Label { AutoSize = true, ForeColor = Color.FromArgb(180, 40, 20), Text = state, Anchor = AnchorStyles.Left };
                int row = _bodyTable.RowCount;
                _bodyTable.Controls.Add(new Label { Text = p.Name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
                _bodyTable.Controls.Add(n, 1, row);
                _bodyTable.Controls.Add(max, 2, row);
                _bodyTable.Controls.Add(st, 3, row);
                _bodyTable.RowCount = row + 1;
                Set(n, p.Hp);
                n.ValueChanged += (sender, e) => MarkDirtyLater();
                _bindings.Add(new NumBinding { Box = n, Original = n.Value, Apply = v => p.Hp = v });
                _bodyRows.Add((p, n, max));
            }
            if (_bodyRows.Count == 0)
                _bodyTable.Controls.Add(new Label { Text = "存档里没有身体部位数据。", AutoSize = true }, 0, 0);
            _bodyTable.ResumeLayout();
        }

        /// <summary>设控件的值，同时记下"打开时的值"。</summary>
        private void Set(NumericUpDown n, float v)
        {
            decimal d = 0;
            if (!float.IsNaN(v) && !float.IsInfinity(v)) d = (decimal)v;
            if (d < n.Minimum) n.Minimum = d;
            if (d > n.Maximum) n.Maximum = d;
            n.Value = d;
            var b = _bindings.FirstOrDefault(x => x.Box == n);
            if (b != null) b.Original = n.Value;
        }

        private int Con => (int)_numCon.Value;

        private void UpdateHints()
        {
            if (_current == null) return;
            int con = Con;
            _hintHp.Text = $"基础上限 {Rules.MaxHpBase(con):0}，天赋、技能和难度另有加成（修改器算不出准确上限）";
            _hintEnergy.Text = $"上限 {Rules.MaxEnergyBase(con):0}（技能树的体力加成另算）";
            _hintHunger.Text = $"上限 {Rules.MaxHunger(con):0}，越高越饱";
            _hintThirst.Text = $"上限 {Rules.MaxThirst(con):0}，越高越不渴";
            foreach (var r in _bodyRows) r.max.Text = $"/ {r.part.Max(con):0}";
        }

        private void SetEditorEnabled(bool on)
        {
            _editor.Enabled = on;
            _invPanel.Enabled = on;
            if (!on) _invPanel.Clear();
            _btnHeal.Enabled = on;
            _btnRevert.Enabled = on;
            _btnSave.Enabled = on;
            if (!on)
            {
                _lblInfo.Text = "";
                _lblBuffs.Text = "";
            }
        }

        // ================= 改动 =================

        private void MarkDirtyLater()
        {
            if (_loading) return;
            UpdateTitleDirty();
        }

        private void UpdateTitleDirty()
        {
            Text = AppTitle + (IsDirty() ? "  *有改动没保存" : "");
        }

        private bool IsDirty()
        {
            if (_current == null) return false;
            if (_txtName.Text != _origName) return true;
            if (_chkClearBuffs.Checked || _healBodyParts || _invPanel.Dirty) return true;
            return _bindings.Any(b => b.Changed);
        }

        private bool ConfirmDiscard() =>
            MessageBox.Show(this, "当前存档有改动还没保存，要放弃这些改动吗？", "有改动没保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        /// <summary>照游戏自己的"治好所有伤"：身体各部位回满并止血，饱食水分精力回满，体温血液恢复正常，辐射清零。</summary>
        private void DoHeal()
        {
            if (_current == null) return;
            int con = Con;
            SetValue(_numEnergy, Math.Max(_numEnergy.Value, (decimal)Rules.MaxEnergyBase(con)));
            SetValue(_numFatigue, (decimal)Rules.MaxFatigue);
            SetValue(_numHunger, (decimal)Rules.MaxHunger(con));
            SetValue(_numThirst, (decimal)Rules.MaxThirst(con));
            SetValue(_numBlood, (decimal)Rules.MaxBlood);
            SetValue(_numTemp, (decimal)Rules.NormalTemperature);
            SetValue(_numRad, 0);
            SetValue(_numHp, Math.Max(_numHp.Value, (decimal)Rules.MaxHpBase(con)));
            foreach (var r in _bodyRows) SetValue(r.box, (decimal)r.part.Max(con));
            _healBodyParts = true;
            UpdateTitleDirty();
            SetStatus("已填好回满的数值，并会在保存时给所有部位止血。血量只补到基础上限，想更多可以手动填。还没保存，点「保存修改」才会写进存档。");
        }

        private static void SetValue(NumericUpDown n, decimal v)
        {
            if (v > n.Maximum) v = n.Maximum;
            if (v < n.Minimum) v = n.Minimum;
            n.Value = v;
        }

        private void DoSave()
        {
            if (_current == null) return;
            if (!IsDirty()) { SetStatus("没有改动，不用保存。"); return; }
            string name = _txtName.Text.Trim();
            if (name.Length == 0) { Error("名字不能是空的。"); return; }
            if (GameFiles.GameRunning())
            {
                Error("游戏正在运行。\n\n游戏开着的时候会定时存档、退出时也会存档，会把这里改的覆盖掉。请先完全退出游戏再保存。");
                return;
            }

            var s = _current;
            try
            {
                var c = s.Character;
                if (name != _origName)
                {
                    c.Name = name;
                    if (s.Entry.Kind == SaveKind.Multi) s.Root.SetString(GameFiles.MpName, name);
                }
                foreach (var b in _bindings) if (b.Changed) b.Apply((float)b.Box.Value);
                if (_healBodyParts) foreach (var r in _bodyRows) r.part.StopBleeding();
                if (_chkClearBuffs.Checked) c.ClearBuffs();

                string backup = GameFiles.Save(s);
                Open(s.Entry);
                UpdateTitleDirty();
                RefreshListsKeepSelection();
                SetStatus("已保存。原文件备份在：" + backup);
                if (!_snapshotMode)
                    MessageBox.Show(this, "保存好了。\n\n原来的存档已备份到：\n" + backup + "\n\n进游戏后如果有问题，退出游戏，把备份文件复制回去覆盖就能恢复。",
                        "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                // 内存里的数据可能已经半改了，重新读一遍文件
                Error("保存失败，存档没有被改动：\n" + ex.Message);
                try { Open(s.Entry); } catch { }
            }
        }

        private void RefreshListsKeepSelection()
        {
            string path = _current?.Entry.Path;
            RefreshLists();
            if (path == null) return;
            _switchingSelection = true;
            try
            {
                foreach (var lv in new[] { _listSingle, _listMulti })
                    foreach (ListViewItem it in lv.Items)
                        if (it.Tag is SaveEntry e && e.Path == path) { it.Selected = true; it.EnsureVisible(); }
            }
            finally { _switchingSelection = false; }
        }

        private void RefreshGameState()
        {
            bool running = GameFiles.GameRunning();
            _gameState.Text = running ? "游戏正在运行：先退出游戏再保存" : "游戏未运行";
            _gameState.ForeColor = running ? Color.FromArgb(180, 40, 20) : Color.DimGray;
        }

        private void SetStatus(string text) => _status.Text = text;

        /// <summary>开发自查用：窗口放到屏幕外，选中第一份存档，把整个窗口画成图片。</summary>
        internal void Snapshot(string png, int tab, bool heal, int rightTab = 0, string equipDlg = null)
        {
            _snapshotMode = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-5000, -5000);
            Show();
            Application.DoEvents();
            _tabs.SelectedIndex = tab;
            var lv = tab == 0 ? _listSingle : _listMulti;
            if (lv.Items.Count > 0) lv.Items[0].Selected = true;
            Application.DoEvents();
            if (heal) DoHeal();
            _rightTabs.SelectedIndex = Math.Min(rightTab, 1);
            Application.DoEvents();
            if (rightTab == 1) { _invPanel.PreviewSelect(4); Application.DoEvents(); }
            if (rightTab == 2) { _invPanel.PreviewEquip(3, 0, equipDlg); Application.DoEvents(); }
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }
            Close();
        }

        private void Error(string text)
        {
            if (_snapshotMode) { _lastError = text; return; }
            MessageBox.Show(this, text, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private string _lastError;

        /// <summary>
        /// 界面自测（--uitest 报告 --root 临时目录）：打开第一份联机角色，在背包页把按钮走一遍，
        /// 点保存，再读回来看改动在不在。只能对临时副本用。
        /// </summary>
        internal int UiTest(string report)
        {
            if (GameFiles.DataRoot.IndexOf(@"LevenLiu\ZEDZONE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                System.IO.File.WriteAllText(report, "界面自测会改存档，只能配合 --root 临时目录 用在副本上。", new System.Text.UTF8Encoding(true));
                return 2;
            }
            var log = new System.Text.StringBuilder();
            int fails = 0;
            void Check(bool ok, string what) { if (!ok) fails++; log.AppendLine((ok ? "[通过] " : "[失败] ") + what); }
            _snapshotMode = true;
            GameFiles.SkipGameCheck = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-5000, -5000);
            Show();
            Application.DoEvents();
            try
            {
                _tabs.SelectedIndex = 1;
                Check(_listMulti.Items.Count > 0, "列表里有联机角色");
                _listMulti.Items[0].Selected = true;
                Application.DoEvents();
                Check(_current != null && _current.Inventory != null, "打开了存档、读到了背包");
                _rightTabs.SelectedIndex = 1;
                Application.DoEvents();
                int total = _current.Inventory.CountAll();
                foreach (var (what, ok) in _invPanel.RunUiTest()) Check(ok, what);
                Application.DoEvents();
                Check(IsDirty(), "窗口知道有改动没保存");
                Check(Text.Contains("有改动没保存"), "标题栏提示有改动");
                int expect = _current.Inventory.CountAll();
                string path = _current.Entry.Path;
                DoSave();
                Check(_lastError == null, "保存没报错" + (_lastError != null ? "：" + _lastError : ""));
                Check(!IsDirty(), "保存后没有未保存的改动");
                var again = GameFiles.Load(new SaveEntry { Kind = SaveKind.Multi, Path = path });
                int now = again.Inventory.CountAll();
                Check(now == expect && now != total, $"读回来物品数 {now}（改之前 {total}，界面上改完是 {expect}）");
                var all = again.Inventory.AllContainers().SelectMany(c => c.box.Items).ToList();
                var bag = all.Find(x => x.Id == 501 && x.Inventory != null && x.Inventory.GetMsgs(IF.InvItems).Any(m => m.GetInt(IF.ItemId) == 420));
                Check(bag != null, "新背包和里面的枪都在");
                Check(again.Inventory.WornBackpack?.Id == 515 && again.Inventory.Main.Items.Any(x => x.Id == 503), "换上的新背包、放在里面的旧背包都保存了");
                var b14 = again.Inventory.Cd.GetBytes(CharacterInventory.CdInventory);
                var bBag = again.Inventory.WornBackpack?.Msg.GetBytes(IF.Inventory);
                Check(b14 != null && bBag != null && b14.SequenceEqual(bBag), "随身物品栏和身上背包两份一致");
                var slots = again.Inventory.Slots;
                Check(slots.First(p => p.Def.Field == 57 && p.Index == 3).Item?.Id == 70 && slots.Count(p => p.Def.Field == 60 && !p.Empty) == 3 &&
                      !slots.First(p => p.Def.Field == 50).Empty && !slots.First(p => p.Def.Field == 57 && p.Index == 1).Empty,
                      "装上的匕首、头部、3 件上衣、装回去的武器 2 都保存了");
                Check(again.Inventory.Equipped.All(x => x.Durability >= 100), "装备修好的结果保存了");
            }
            catch (Exception ex)
            {
                fails++;
                log.AppendLine("[失败] 出错：" + ex);
            }
            log.AppendLine(fails == 0 ? "界面自测全部通过" : $"界面自测有 {fails} 项失败");
            System.IO.File.WriteAllText(report, log.ToString(), new System.Text.UTF8Encoding(true));
            Close();
            return fails == 0 ? 0 : 1;
        }
    }
}
