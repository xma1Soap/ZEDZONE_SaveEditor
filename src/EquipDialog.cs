using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZedZoneSaveEditor
{
    /// <summary>给装备栏的一个槽位挑东西：身上已有的（从背包里挪过来），或者一件新的。只列这个槽能放的。</summary>
    internal sealed class EquipDialog : Form
    {
        private readonly TextBox _search = new TextBox();
        private readonly ListView _list = new ListView();
        private readonly ImageList _images = new ImageList { ImageSize = new Size(48, 32), ColorDepth = ColorDepth.Depth32Bit };
        private readonly PictureBox _preview = new PictureBox();
        private readonly NumericUpDown _amount = new NumericUpDown();
        private readonly Label _amountLabel = new Label();
        private readonly Label _hint = new Label();
        private readonly Button _ok = new Button();
        private readonly ListViewGroup _gHave = new ListViewGroup("have", "身上已有的（会从原来的地方挪过来）");
        private readonly ListViewGroup _gNew = new ListViewGroup("new", "新的");
        private readonly List<(ItemView it, ContainerView box, string path)> _have;
        private readonly List<ItemInfo> _new;
        private readonly EquipPos _pos;

        /// <summary>选了身上已有的：物品和它所在的格子区。</summary>
        public ItemView ChosenItem { get; private set; }
        public ContainerView ChosenFrom { get; private set; }
        /// <summary>选了新的：物品种类和数量。</summary>
        public ItemInfo ChosenNew { get; private set; }
        public int Amount => (int)_amount.Value;

        public EquipDialog(CharacterInventory inv, EquipPos pos)
        {
            _pos = pos;
            Text = $"装到「{pos.Name}」" + (pos.Item != null ? $"（换下 {pos.Item.Name}）" : "");
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 640);
            MinimumSize = new Size(560, 420);
            MinimizeBox = false;
            ShowInTaskbar = false;

            _have = new List<(ItemView, ContainerView, string)>();
            if (inv != null)
                foreach (var (box, path) in inv.AllContainers())
                    foreach (var it in box.Items)
                        if (pos.Def.Accepts(it.Info) && !(pos.Def.Field == CharacterInventory.EqBackpack && it.Msg == inv.WornBackpack?.Msg))
                            _have.Add((it, box, path));
            _new = ItemDb.Addable().Where(i => pos.Def.Accepts(i)).ToList();

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(t);

            t.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(3, 3, 3, 6),
                Text = $"「{pos.Name}」能放：{pos.Def.Rule}。" +
                       (pos.Item != null ? $"\n现在装着 {pos.Item.Name}，换下来的会放回新东西原来的位置（新的就放进身上背包）。" : ""),
            }, 0, 0);

            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            top.Controls.Add(new Label { Text = "搜索（名字或编号）", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            _search.Width = 220;
            top.Controls.Add(_search);
            t.Controls.Add(top, 0, 1);

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.Dock = DockStyle.Fill;
            _list.SmallImageList = _images;
            _list.ShowGroups = true;
            _list.Groups.Add(_gHave);
            _list.Groups.Add(_gNew);
            foreach (var (c, w) in new[] { ("名称", 260), ("在哪", 220), ("耐久", 60), ("大小", 60) }) _list.Columns.Add(c, w);
            t.Controls.Add(_list, 0, 2);

            var amt = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
            amt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            amt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _preview.Size = new Size(96, 96);
            _preview.BackColor = Color.FromArgb(40, 42, 46);
            _preview.Margin = new Padding(3, 6, 10, 3);
            _preview.Paint += (s, e) =>
            {
                int id = CurrentId();
                if (id >= 0) ItemIcons.Draw(e.Graphics, id, new RectangleF(4, 4, _preview.Width - 8, _preview.Height - 8));
            };
            amt.Controls.Add(_preview, 0, 0);
            var amtRight = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var amtRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            _amountLabel.Text = "数量";
            _amountLabel.AutoSize = true;
            _amountLabel.Margin = new Padding(3, 7, 3, 3);
            amtRow.Controls.Add(_amountLabel);
            _amount.Minimum = 1;
            _amount.Maximum = 1;
            _amount.Width = 90;
            amtRow.Controls.Add(_amount);
            amtRight.Controls.Add(amtRow);
            _hint.AutoSize = true;
            _hint.ForeColor = Color.DimGray;
            _hint.MaximumSize = new Size(560, 0);
            _hint.Margin = new Padding(3, 6, 3, 3);
            amtRight.Controls.Add(_hint);
            amt.Controls.Add(amtRight, 1, 0);
            t.Controls.Add(amt, 0, 3);

            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            _ok.Text = "装上";
            _ok.AutoSize = true;
            _ok.Font = new Font(Font, FontStyle.Bold);
            _ok.Click += (s, e) =>
            {
                var tag = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag : null;
                if (tag is ValueTuple<ItemView, ContainerView, string> h) { ChosenItem = h.Item1; ChosenFrom = h.Item2; }
                else if (tag is ItemInfo info) ChosenNew = info;
                else return;
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(_ok);
            t.Controls.Add(bottom, 0, 4);
            AcceptButton = _ok;
            CancelButton = cancel;

            _search.TextChanged += (s, e) => Fill();
            _list.SelectedIndexChanged += (s, e) => OnSelect();
            _list.DoubleClick += (s, e) => _ok.PerformClick();
            _search.KeyDown += (s, e) =>
            {
                if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && _list.Items.Count > 0)
                {
                    int i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
                    i = Math.Max(0, Math.Min(_list.Items.Count - 1, i + (e.KeyCode == Keys.Down ? 1 : -1)));
                    _list.Items[i].Selected = true;
                    _list.Items[i].EnsureVisible();
                    e.Handled = true;
                }
            };
            Fill();
        }

        private string Key(int id)
        {
            string key = id.ToString();
            if (!_images.Images.ContainsKey(key) && ItemIcons.Has(id)) _images.Images.Add(key, ItemIcons.Thumb(id, 48, 32));
            return key;
        }

        private bool Match(string name, int id, string q) =>
            q.Length == 0 || name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || id.ToString().StartsWith(q);

        private void Fill()
        {
            string q = _search.Text.Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var h in _have)
            {
                if (!Match(h.it.Name, h.it.Id, q)) continue;
                _list.Items.Add(new ListViewItem(new[] { h.it.Name, h.path, h.it.DurabilityText, $"{h.it.Info.W}×{h.it.Info.H}" }, _gHave)
                {
                    Tag = h, ImageKey = Key(h.it.Id),
                });
            }
            foreach (var i in _new)
            {
                if (!Match(i.Name, i.Id, q)) continue;
                _list.Items.Add(new ListViewItem(new[] { i.Name, "（新的）", "100", $"{i.W}×{i.H}" }, _gNew) { Tag = i, ImageKey = Key(i.Id), ForeColor = Color.FromArgb(40, 40, 40) });
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            OnSelect();
        }

        private int CurrentId()
        {
            var tag = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag : null;
            if (tag is ValueTuple<ItemView, ContainerView, string> h) return h.Item1.Id;
            if (tag is ItemInfo i) return i.Id;
            return -1;
        }

        private void OnSelect()
        {
            var tag = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag : null;
            _ok.Enabled = tag != null;
            _preview.Invalidate();
            if (tag is ItemInfo info && info.Stackable)
            {
                _amount.Maximum = info.Stack;
                _amount.Value = info.Stack;
                _amount.Enabled = _amountLabel.Enabled = true;
            }
            else
            {
                _amount.Maximum = 1;
                _amount.Value = 1;
                _amount.Enabled = _amountLabel.Enabled = false;
            }
            if (tag == null)
                _hint.Text = _have.Count + _new.Count == 0 ? "没有能装在这里的东西。" : "在上面选一件。";
            else if (tag is ItemInfo ni)
                _hint.Text = $"新造一件 {ni.Name} 直接装上。" + (ni.Kind == ItemKind.RangedWeapon ? "新枪是空的（不带弹匣、子弹、配件）。" : "") +
                             (ni.Kind == ItemKind.Backpack ? "新背包是空的，原来背的背包（连同里面的东西）会放进新背包里。" : "");
            else if (tag is ValueTuple<ItemView, ContainerView, string> h)
                _hint.Text = $"把「{h.Item3}」里的 {h.Item1.Name} 拿出来装上。" +
                             (_pos.Def.Field == CharacterInventory.EqBackpack ? "原来背的背包（连同里面的东西）会放进这个背包里。" : "");
        }

        /// <summary>截图自查用。</summary>
        internal static void Snapshot(string png, CharacterInventory inv, EquipPos pos)
        {
            using (var d = new EquipDialog(inv, pos) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                d.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(d.Width, d.Height))
                {
                    d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height));
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                }
                d.Close();
            }
        }
    }
}
