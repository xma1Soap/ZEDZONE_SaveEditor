using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZedZoneSaveEditor
{
    /// <summary>选一种物品加进背包（带物品小图）。</summary>
    internal sealed class AddItemDialog : Form
    {
        private readonly TextBox _search = new TextBox();
        private readonly ComboBox _kind = new ComboBox();
        private readonly ListView _list = new ListView();
        private readonly ImageList _images = new ImageList { ImageSize = new Size(48, 32), ColorDepth = ColorDepth.Depth32Bit };
        private readonly PictureBox _preview = new PictureBox();
        private readonly NumericUpDown _amount = new NumericUpDown();
        private readonly Label _hint = new Label();
        private readonly Button _ok = new Button();

        public ItemInfo Chosen { get; private set; }
        public int Amount => (int)_amount.Value;

        public AddItemDialog(string where = null)
        {
            Text = where == null ? "添加物品" : "添加物品到：" + where;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 680);
            MinimumSize = new Size(560, 420);
            MinimizeBox = false;
            ShowInTaskbar = false;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(t);

            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            top.Controls.Add(new Label { Text = "搜索（名字或编号）", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            _search.Width = 220;
            top.Controls.Add(_search);
            _kind.DropDownStyle = ComboBoxStyle.DropDownList;
            _kind.Width = 130;
            _kind.Items.Add("全部类型");
            foreach (ItemKind k in Enum.GetValues(typeof(ItemKind))) _kind.Items.Add(ItemDb.KindName(k));
            _kind.SelectedIndex = 0;
            top.Controls.Add(_kind);
            t.Controls.Add(top, 0, 0);

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.Dock = DockStyle.Fill;
            _list.SmallImageList = _images;
            foreach (var (c, w) in new[] { ("名称", 300), ("编号", 70), ("类型", 100), ("大小", 60), ("堆叠上限", 80) }) _list.Columns.Add(c, w);
            t.Controls.Add(_list, 0, 1);

            var amt = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
            amt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            amt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _preview.Size = new Size(96, 96);
            _preview.BackColor = Color.FromArgb(40, 42, 46);
            _preview.Margin = new Padding(3, 6, 10, 3);
            _preview.Paint += (s, e) =>
            {
                var it = Current();
                if (it != null) ItemIcons.Draw(e.Graphics, it.Id, new RectangleF(4, 4, _preview.Width - 8, _preview.Height - 8));
            };
            amt.Controls.Add(_preview, 0, 0);
            var amtRight = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var amtRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            amtRow.Controls.Add(new Label { Text = "数量", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            _amount.Minimum = 1;
            _amount.Maximum = 100;
            _amount.Width = 90;
            amtRow.Controls.Add(_amount);
            amtRight.Controls.Add(amtRow);
            _hint.AutoSize = true;
            _hint.ForeColor = Color.DimGray;
            _hint.MaximumSize = new Size(560, 0);
            _hint.Margin = new Padding(3, 6, 3, 3);
            amtRight.Controls.Add(_hint);
            amt.Controls.Add(amtRight, 1, 0);
            t.Controls.Add(amt, 0, 2);

            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            _ok.Text = "放进背包";
            _ok.AutoSize = true;
            _ok.Font = new Font(Font, FontStyle.Bold);
            _ok.Click += (s, e) => { Chosen = Current(); if (Chosen != null) { DialogResult = DialogResult.OK; Close(); } };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(_ok);
            t.Controls.Add(bottom, 0, 3);
            AcceptButton = _ok;
            CancelButton = cancel;

            _search.TextChanged += (s, e) => Fill();
            _kind.SelectedIndexChanged += (s, e) => Fill();
            _list.SelectedIndexChanged += (s, e) => OnSelect();
            _list.DoubleClick += (s, e) => _ok.PerformClick();
            _search.KeyDown += (s, e) =>
            {
                // 在搜索框里按上下键直接选列表
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
            OnSelect();
        }

        private ItemInfo Current() => _list.SelectedItems.Count == 1 ? (ItemInfo)_list.SelectedItems[0].Tag : null;

        private void Fill()
        {
            string q = _search.Text.Trim();
            int kind = _kind.SelectedIndex - 1;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var it in ItemDb.Addable())
            {
                if (kind >= 0 && (int)it.Kind != kind) continue;
                if (q.Length > 0 && it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && !it.Id.ToString().StartsWith(q)) continue;
                string key = it.Id.ToString();
                if (!_images.Images.ContainsKey(key) && ItemIcons.Has(it.Id)) _images.Images.Add(key, ItemIcons.Thumb(it.Id, 48, 32));
                _list.Items.Add(new ListViewItem(new[] { it.Name, it.Id.ToString(), it.KindName, $"{it.W}×{it.H}", it.Stack.ToString() })
                {
                    Tag = it, ImageKey = key,
                });
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            OnSelect();
        }

        private void OnSelect()
        {
            var it = Current();
            _ok.Enabled = it != null;
            _preview.Invalidate();
            if (it == null) { _hint.Text = "先在上面选一种物品（可以搜名字，比如“背包”“弹匣”“罐头”）。"; return; }
            _amount.Maximum = it.Stackable ? it.Stack * 20 : 50;
            string extra = "";
            switch (it.Kind)
            {
                case ItemKind.RangedWeapon: extra = "新加的枪是空的：不带弹匣、不带子弹，配件也没有。"; break;
                case ItemKind.Magazine: extra = "新加的弹匣是装满子弹的。"; break;
                case ItemKind.Backpack: extra = $"新加的背包是空的（{it.BagW}×{it.BagH} 格）。"; break;
                case ItemKind.Food: extra = "食物按刚做好算。"; break;
                case ItemKind.Clothing: extra = "衣服在游戏里分部位（头/上衣/裤子/鞋），每个部位是单独一件。"; break;
            }
            _hint.Text = $"{it.Name}，占 {it.W}×{it.H} 格。" +
                         (it.Stackable ? $"一格最多叠 {it.Stack} 个，超出的会分成几格放。" : "这个不能叠放，填几就放几件。") + extra;
        }

        /// <summary>截图自查用。</summary>
        internal static void Snapshot(string png, string search)
        {
            using (var d = new AddItemDialog { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                d.Show();
                d._search.Text = search;
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
