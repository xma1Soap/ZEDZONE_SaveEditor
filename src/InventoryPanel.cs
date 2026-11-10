using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZedZoneSaveEditor
{
    /// <summary>
    /// 「背包物品」页：左边是格子区（背包、背包里的背包、装备栏），中间是物品列表，右边是格子图。
    /// 格子图里可以拖动物品换位置、旋转、拖进背包；也可以拖到左边树上的背包里。
    /// </summary>
    internal sealed class InventoryPanel : UserControl
    {
        public event Action Changed;
        public event Action<string> Status;

        private const string DragFormat = "ZedZoneSaveEditor.Items";

        private readonly TreeView _tree = new TreeView();
        private readonly Label _lblBox = new Label();
        private readonly ListView _list = new ListView();
        private readonly ImageList _icons = new ImageList { ImageSize = new Size(32, 24), ColorDepth = ColorDepth.Depth32Bit };
        private readonly NumericUpDown _numCount = new NumericUpDown();
        private readonly NumericUpDown _numDur = new NumericUpDown();
        private readonly Label _lblSel = new Label();
        private readonly Button _btnAdd = new Button(), _btnCopy = new Button(), _btnDel = new Button(),
            _btnRepair = new Button(), _btnFill = new Button(), _btnAll = new Button(), _btnUnequip = new Button();
        private readonly GridView _grid = new GridView();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();

        private LoadedSave _save;
        private CharacterInventory _inv;
        private ContainerView _box;          // 当前选中的格子区；null + _equipView = 装备栏
        private bool _equipView;
        private bool _updating;
        private ContainerView _dragSource;   // 拖到树上时，东西原来在哪个格子区
        private bool _dragFromEquip;         // 拖的是装备栏上的东西
        private bool _equipColumns;          // 列表现在是装备栏的列（多一列「槽位」）
        private TreeNode _dropHighlight;

        public bool Dirty { get; private set; }

        /// <summary>界面自测时不弹确认框。</summary>
        internal bool Quiet;

        public InventoryPanel()
        {
            Dock = DockStyle.Fill;
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Controls.Add(split);
            HandleCreated += (s, e) => { try { split.SplitterDistance = 215; } catch { } };

            // ---- 左：格子区 ----
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.AllowDrop = true;
            _tree.AfterSelect += (s, e) => SelectNode(e.Node);
            _tree.DragEnter += (s, e) => TreeDragOver(e);
            _tree.DragOver += (s, e) => TreeDragOver(e);
            _tree.DragLeave += (s, e) => HighlightDrop(null);
            _tree.DragDrop += (s, e) => TreeDragDrop(e);
            var leftTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            leftTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftTable.Controls.Add(_tree, 0, 0);
            leftTable.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(205, 0), Margin = new Padding(3, 6, 3, 3),
                Text = "把物品拖到这里的背包上，就是放进那个背包；拖到「装备栏」上就是装备上。\n这里只有人物身上带的东西，基地箱子、地上的东西属于世界存档，不在这里。"
            }, 0, 1);
            split.Panel1.Controls.Add(leftTable);

            // ---- 右：工具条 + 列表 + 编辑 | 格子图 ----
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.Panel2.Controls.Add(right);

            _lblBox.AutoSize = true;
            _lblBox.Font = new Font(Font, FontStyle.Bold);
            _lblBox.Margin = new Padding(3, 4, 3, 4);
            right.Controls.Add(_lblBox, 0, 0);
            right.SetColumnSpan(_lblBox, 2);

            // 工具条（放在列表上面，一眼能看到）
            var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            Btn(bar, _btnAdd, "＋ 添加物品…", () => DoAdd(null));
            _btnAdd.Font = new Font(Font, FontStyle.Bold);
            _btnAdd.Padding = new Padding(6, 2, 6, 2);
            Btn(bar, _btnUnequip, "卸下到背包", DoUnequip);
            _btnUnequip.Visible = false;
            Btn(bar, _btnCopy, "复制一个", DoCopy);
            Btn(bar, _btnDel, "删除", DoDelete);
            Btn(bar, _btnRepair, "修好选中的", DoRepair);
            Btn(bar, _btnFill, "补满选中的", DoFill);
            Btn(bar, _btnAll, "全选", SelectAll);
            var tip = new ToolTip();
            tip.SetToolTip(_btnAdd, "从全部物品里搜一种放进当前背包（也可以在右边格子图的空格上点右键「在这里添加」）。");
            tip.SetToolTip(_btnRepair, "耐久回满；武器、弹匣、枪械配件修到全新（枪上装的配件一起修）；食物变成刚做好的。");
            tip.SetToolTip(_btnFill, "能堆叠的东西数量补到上限；弹匣装满子弹；水壶之类装满原来装的液体。");
            tip.SetToolTip(_btnUnequip, "把选中的装备卸下来，放进身上背包的空位。");
            tip.SetToolTip(_btnCopy, "照选中的物品再放一个到空位上。背包的话只复制空背包，里面的东西不复制。");
            right.Controls.Add(bar, 0, 1);

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = true;
            _list.Dock = DockStyle.Fill;
            _list.SmallImageList = _icons;
            SetColumns(false);
            _list.SelectedIndexChanged += (s, e) => OnItemSelect();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DoDelete();
                if (e.Control && e.KeyCode == Keys.A) SelectAll();
            };
            _list.ItemDrag += (s, e) => StartDrag(Selected());
            _list.DoubleClick += (s, e) => OnListDoubleClick();
            _list.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = _list.HitTest(e.Location).Item;
                if (hit != null && !hit.Selected)
                {
                    _updating = true;
                    foreach (ListViewItem li in _list.Items) li.Selected = li == hit;
                    _updating = false;
                    OnItemSelect();
                }
                ShowMenu(_list, e.Location, Selected().FirstOrDefault(), null);
            };
            right.Controls.Add(_list, 0, 2);

            // 选中提示 + 数量耐久
            _lblSel.AutoSize = true;
            _lblSel.Margin = new Padding(3, 6, 3, 0);
            _lblSel.MaximumSize = new Size(460, 0);
            right.Controls.Add(_lblSel, 0, 3);
            var edit = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            edit.Controls.Add(new Label { Text = "数量", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            _numCount.Width = 90;
            _numCount.DecimalPlaces = 0;
            _numCount.TextAlign = HorizontalAlignment.Right;
            _numCount.ValueChanged += (s, e) => ApplyEdit(it => it.Count = (float)_numCount.Value);
            edit.Controls.Add(_numCount);
            edit.Controls.Add(new Label { Text = "耐久", AutoSize = true, Margin = new Padding(12, 7, 3, 3) });
            _numDur.Width = 70;
            _numDur.Maximum = 100;
            _numDur.DecimalPlaces = 1;
            _numDur.TextAlign = HorizontalAlignment.Right;
            _numDur.ValueChanged += (s, e) => ApplyEdit(it => it.Durability = (float)_numDur.Value);
            edit.Controls.Add(_numDur);
            edit.Controls.Add(new Label { Text = "（0～100）", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 7, 3, 3) });
            right.Controls.Add(edit, 0, 4);

            // 格子图 + 操作提示
            var gridBox = new Panel { Dock = DockStyle.Fill, Margin = new Padding(6, 3, 0, 3) };
            _grid.Dock = DockStyle.Fill;
            var gridHint = new Label
            {
                Dock = DockStyle.Bottom, AutoSize = false, Height = 84, ForeColor = Color.DimGray, Padding = new Padding(2, 4, 2, 0),
                Text = "拖动物品换位置；拖着时点右键或按 R 转向；拖到背包上就放进去，拖到左边的背包也行，拖到左边「装备栏」就是装上。右键有更多操作。"
            };
            gridBox.Controls.Add(_grid);
            gridBox.Controls.Add(gridHint);
            right.Controls.Add(gridBox, 1, 1);
            right.SetRowSpan(gridBox, 4);

            _grid.ItemClicked += it => SelectItems(new[] { it.Msg });
            _grid.ItemDoubleClicked += OpenInner;
            _grid.MoveRequested += (it, x, y, rot) => MoveItemTo(it, x, y, rot);
            _grid.DropIntoBag += (it, bag) => MoveIntoBag(it, bag);
            _grid.DragOut += it => StartDrag(new List<ItemView> { it });
            _grid.ContextRequested += (it, cell, pt) =>
            {
                if (it != null) SelectItems(new[] { it.Msg });
                ShowMenu(_grid, pt, it, cell);
            };

            Clear();
        }

        private static void Btn(Control parent, Button b, string text, Action click)
        {
            b.Text = text;
            b.AutoSize = true;
            b.Click += (s, e) => click();
            parent.Controls.Add(b);
        }

        // ================= 绑定 =================

        public void Clear()
        {
            _save = null;
            _inv = null;
            _box = null;
            _equipView = false;
            Dirty = false;
            _tree.Nodes.Clear();
            _list.Items.Clear();
            _grid.SetBox(null, null);
            _lblBox.Text = "在左边选一个存档";
            UpdateButtons();
        }

        public void Bind(LoadedSave s)
        {
            Clear();
            _save = s;
            _inv = s?.Inventory;
            if (_inv == null)
            {
                _lblBox.Text = s == null ? "在左边选一个存档" : "这个存档的背包数据读不出来。";
                return;
            }
            RebuildTree(null);
        }

        private void RebuildTree(PMsg keepInv)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            TreeNode select = null, main = null;
            if (_inv.Main != null)
            {
                main = BoxNode(_inv.Main, 0, keepInv, ref select);
                _tree.Nodes.Add(main);
            }
            else _tree.Nodes.Add(new TreeNode("（身上没有背包）") { ForeColor = Color.Gray });
            var eq = new TreeNode($"装备栏（{_inv.Equipped.Count} 件）") { Tag = "equip" };
            foreach (var it in _inv.Equipped)
            {
                if (it == _inv.WornBackpack) continue;
                var inner = _inv.InnerOf(it);
                if (inner != null) eq.Nodes.Add(BoxNode(inner, 1, keepInv, ref select));
            }
            _tree.Nodes.Add(eq);
            _tree.ExpandAll();
            _tree.EndUpdate();
            if (select == null) select = _equipView || main == null ? eq : main;
            _tree.SelectedNode = select;
            select.EnsureVisible();
            SelectNode(select);       // 页面还没显示时 TreeView 不发 AfterSelect，这里自己调一次
        }

        private TreeNode BoxNode(ContainerView c, int depth, PMsg keepInv, ref TreeNode select)
        {
            var node = new TreeNode($"{c.Title} {c.W}×{c.H}") { Tag = c };
            if (c.Inv == keepInv) select = node;
            if (depth > 20) return node;
            foreach (var it in c.Items)
            {
                var inner = _inv.InnerOf(it);
                if (inner != null) node.Nodes.Add(BoxNode(inner, depth + 1, keepInv, ref select));
            }
            return node;
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                yield return n;
                foreach (var c in AllNodes(n.Nodes)) yield return c;
            }
        }

        private void SelectNode(TreeNode n)
        {
            _box = n?.Tag as ContainerView;
            _equipView = n?.Tag as string == "equip";
            FillList(null);
        }

        /// <summary>打开某个背包（左边树上选中它）。</summary>
        private void OpenInner(ItemView it)
        {
            var inv = it?.Inventory;
            if (inv == null) return;
            var node = AllNodes(_tree.Nodes).FirstOrDefault(n => n.Tag is ContainerView c && c.Inv == inv);
            if (node != null) _tree.SelectedNode = node;
        }

        private List<ItemView> CurrentItems()
        {
            if (_equipView) return _inv.Equipped.ToList();
            return _box?.Items ?? new List<ItemView>();
        }

        private string IconKey(int id)
        {
            string key = id.ToString();
            if (!_icons.Images.ContainsKey(key) && ItemIcons.Has(id)) _icons.Images.Add(key, ItemIcons.Thumb(id, 32, 24));
            return key;
        }

        private void SetColumns(bool equip)
        {
            if (_list.Columns.Count > 0 && _equipColumns == equip) return;
            _equipColumns = equip;
            _list.Columns.Clear();
            var cols = equip
                ? new[] { ("槽位", 116), ("名称", 150), ("数量", 50), ("耐久", 44), ("状态", 150) }
                : new[] { ("名称", 170), ("数量", 64), ("耐久", 44), ("状态", 160) };
            foreach (var (t, w) in cols) _list.Columns.Add(t, w);
        }

        private static ItemView ItemOf(ListViewItem li) => li.Tag as ItemView ?? (li.Tag as EquipPos)?.Item;

        private List<EquipPos> SelectedPos() =>
            _list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as EquipPos).Where(x => x != null).ToList();

        private void FillList(ICollection<PMsg> select, EquipSlotDef selDef = null, int selIndex = 0)
        {
            _updating = true;
            try
            {
                var items = CurrentItems();
                _list.BeginUpdate();
                _list.Items.Clear();
                SetColumns(_equipView);
                if (_equipView)
                    foreach (var pos in _inv.Slots)
                    {
                        var it = pos.Item;
                        var li = it == null
                            ? new ListViewItem(new[] { pos.Name, "（空）", "", "", "能放" + pos.Def.Rule }) { ForeColor = Color.Gray }
                            : new ListViewItem(new[] { pos.Name, it.Name, it.CountText, it.DurabilityText, it.StateText }) { ImageKey = IconKey(it.Id) };
                        li.Tag = pos;
                        if (it != null && !it.Info.Known) li.ForeColor = Color.Gray;
                        if ((select != null && it != null && select.Contains(it.Msg)) || (selDef == pos.Def && selIndex == pos.Index)) li.Selected = true;
                        _list.Items.Add(li);
                    }
                else foreach (var it in items)
                {
                    var info = it.Info;
                    var li = new ListViewItem(new[] { it.Name, it.CountText, it.DurabilityText, it.StateText }) { Tag = it, ImageKey = IconKey(it.Id) };
                    if (!info.Known) li.ForeColor = Color.Gray;
                    if (select != null && select.Contains(it.Msg)) li.Selected = true;
                    _list.Items.Add(li);
                }
                _list.Columns[_list.Columns.Count - 1].Width = -2;    // 最后一列占满剩下的宽度
                _list.EndUpdate();
                if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();

                if (_equipView)
                    _lblBox.Text = $"装备栏：穿着、拿着的东西（{items.Count} 件）。选中一个槽位点「＋ 装上…」，或者把背包里的东西拖到左边「装备栏」上。";
                else if (_box != null)
                {
                    var occ = _box.Occupied();
                    int free = 0;
                    foreach (bool b in occ) if (!b) free++;
                    _lblBox.Text = $"{_box.Title}：{_box.W}×{_box.H} 格，{items.Count} 件东西，空格 {free} 个";
                }
                else _lblBox.Text = "";
                _grid.EmptyText = _equipView
                    ? "装备栏没有格子图。\n\n选中左边列表里的槽位，点「＋ 装上…」挑一件装上（只列这个槽能放的东西）；\n也可以把背包里的东西拖到最左边的「装备栏」上，会自动装到能放的空槽。\n\n把装备从列表拖到最左边的某个背包上，就是卸下放进那个背包。"
                    : null;
                _grid.SetBox(_equipView ? null : _box, Selected().Select(x => x.Msg).ToList());
            }
            finally { _updating = false; }
            OnItemSelect();
        }

        /// <summary>选中的物品（装备栏里空着的槽不算）。</summary>
        private List<ItemView> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(ItemOf).Where(x => x != null).ToList();

        private void OnItemSelect()
        {
            if (_updating) return;
            _updating = true;
            try
            {
                var sel = Selected();
                var rows = SelectedPos();
                if (_equipView && rows.Count == 1 && rows[0].Item == null)
                {
                    var pos = rows[0];
                    _lblSel.Text = pos.Def.Legacy ? $"空槽：{pos.Name}" : $"空槽：{pos.Name}，能放{pos.Def.Rule}。点「＋ 装上…」或者双击挑一件装上。";
                    _numCount.Enabled = false;
                    _numDur.Enabled = false;
                }
                else if (sel.Count == 1)
                {
                    var it = sel[0];
                    var info = it.Info;
                    _lblSel.Text = $"选中：{it.Name}　编号 {it.Id}，{(info.Known ? info.KindName : "物品表里没有")}，{info.W}×{info.H} 格，位置 {it.PosText}";
                    decimal max = Math.Max(info.Stack, (decimal)Math.Ceiling(Math.Max(1f, it.Count)));
                    _numCount.Minimum = 1;
                    _numCount.Maximum = Math.Max(1, max);
                    _numCount.Value = Clamp((decimal)Math.Round(it.Count), _numCount.Minimum, _numCount.Maximum);
                    _numCount.Enabled = info.Stackable || it.Count > 1;
                    _numDur.Value = Clamp((decimal)it.Durability, 0, 100);
                    _numDur.Enabled = true;
                }
                else
                {
                    _lblSel.Text = sel.Count == 0 ? "没选中物品（可以按住 Ctrl 或 Shift 多选）" : $"选中了 {sel.Count} 件（多选时可以一起修好、补满、删除，或拖到左边的背包里）";
                    _numCount.Enabled = false;
                    _numDur.Enabled = false;
                }
                _grid.SetSelection(sel.Select(x => x.Msg).ToList());
            }
            finally { _updating = false; }
            UpdateButtons();
        }

        private static decimal Clamp(decimal v, decimal lo, decimal hi) => v < lo ? lo : v > hi ? hi : v;

        private bool GridMode => _inv != null && _box != null && !_equipView;

        private void UpdateButtons()
        {
            bool has = _inv != null;
            int n = has ? Selected().Count : 0;
            if (has && _equipView)
            {
                var rows = SelectedPos();
                _btnAdd.Text = rows.Count == 1 && rows[0].Item != null ? "＋ 换一个…" : "＋ 装上…";
                _btnAdd.Enabled = rows.Count == 1 && !rows[0].Def.Legacy;
                _btnUnequip.Visible = true;
                _btnUnequip.Enabled = n > 0;
                _btnCopy.Enabled = false;
                _btnDel.Enabled = n > 0;
            }
            else
            {
                _btnAdd.Text = "＋ 添加物品…";
                _btnAdd.Enabled = GridMode;
                _btnUnequip.Visible = false;
                _btnCopy.Enabled = GridMode && n == 1;
                _btnDel.Enabled = GridMode && n > 0;
            }
            _btnRepair.Enabled = n > 0;
            _btnFill.Enabled = n > 0;
            _btnAll.Enabled = has && _list.Items.Count > 0;
            _grid.CanDrag = GridMode;
        }

        /// <summary>截图自查用：打开装备栏，选中第 row 行；dlgPng 不为空时把第 dlgRow 行的「装上」对话框画成图片。</summary>
        internal void PreviewEquip(int row, int dlgRow, string dlgPng)
        {
            var eq = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag as string == "equip");
            if (eq == null) return;
            _tree.SelectedNode = eq;
            if (dlgPng != null && dlgRow >= 0 && dlgRow < _list.Items.Count && _list.Items[dlgRow].Tag is EquipPos dp)
                EquipDialog.Snapshot(dlgPng, _inv, dp);
            if (row >= 0 && row < _list.Items.Count && _list.Items[row].Tag is EquipPos p) SelectSlot(p);
        }

        /// <summary>截图自查用：选中列表里第 n 个物品。</summary>
        internal void PreviewSelect(int n)
        {
            if (n >= 0 && n < _list.Items.Count && ItemOf(_list.Items[n]) is ItemView it) SelectItems(new[] { it.Msg });
        }

        private void SelectAll()
        {
            _updating = true;
            foreach (ListViewItem li in _list.Items) li.Selected = true;
            _updating = false;
            OnItemSelect();
        }

        private void SelectSlot(EquipPos pos)
        {
            _updating = true;
            foreach (ListViewItem li in _list.Items)
            {
                var p = li.Tag as EquipPos;
                li.Selected = p != null && p.Def == pos.Def && p.Index == pos.Index;
                if (li.Selected) li.EnsureVisible();
            }
            _updating = false;
            OnItemSelect();
        }

        private void OnListDoubleClick()
        {
            if (_equipView)
            {
                var rows = SelectedPos();
                if (rows.Count != 1) return;
                if (rows[0].Item?.Inventory != null) OpenInner(rows[0].Item);
                else DoEquip(rows[0]);
                return;
            }
            var sel = Selected();
            if (sel.Count == 1) OpenInner(sel[0]);
        }

        private void SelectItems(ICollection<PMsg> msgs)
        {
            _updating = true;
            foreach (ListViewItem li in _list.Items)
            {
                var it = ItemOf(li);
                li.Selected = it != null && msgs.Contains(it.Msg);
                if (li.Selected) li.EnsureVisible();
            }
            _updating = false;
            OnItemSelect();
        }

        // ================= 右键菜单 =================

        private void ShowMenu(Control on, Point pt, ItemView hit, Point? cell)
        {
            if (_inv == null) return;
            _menu.Items.Clear();
            void Add(string text, bool enabled, Action click)
            {
                var mi = new ToolStripMenuItem(text) { Enabled = enabled };
                mi.Click += (s, e) => click();
                _menu.Items.Add(mi);
            }
            var sel = Selected();
            if (_equipView)
            {
                var rows = SelectedPos();
                if (rows.Count == 1 && !rows[0].Def.Legacy) Add(rows[0].Item == null ? "＋ 装上…" : "＋ 换一个…", true, () => DoEquip(rows[0]));
                if (rows.Count == 1 && rows[0].Item?.Inventory != null) Add("打开它的格子", true, () => OpenInner(rows[0].Item));
                if (sel.Count > 0)
                {
                    _menu.Items.Add(new ToolStripSeparator());
                    Add("卸下到背包", true, DoUnequip);
                    Add("修好", true, DoRepair);
                    Add("补满", true, DoFill);
                    Add(sel.Count > 1 ? $"删除这 {sel.Count} 件" : "删除", true, DoDelete);
                }
                if (_menu.Items.Count > 0) _menu.Show(on, pt);
                return;
            }
            if (GridMode)
            {
                if (hit == null && cell != null) Add("＋ 在这里添加物品…", true, () => DoAdd(cell));
                else Add("＋ 添加物品…", true, () => DoAdd(null));
            }
            if (hit != null)
            {
                if (hit.Inventory != null) Add("打开这个背包", true, () => OpenInner(hit));
                if (GridMode && hit.Info.W != hit.Info.H) Add(hit.Rotated ? "转成竖放" : "转成横放", true, () => RotateItem(hit));
            }
            if (sel.Count > 0)
            {
                _menu.Items.Add(new ToolStripSeparator());
                Add("复制一个", GridMode && sel.Count == 1, DoCopy);
                Add("修好", true, DoRepair);
                Add("补满", true, DoFill);
                Add(sel.Count > 1 ? $"删除这 {sel.Count} 件" : "删除", GridMode, DoDelete);
            }
            if (_menu.Items.Count > 0) _menu.Show(on, pt);
        }

        // ================= 修改 =================

        private void MarkChanged(string status)
        {
            Dirty = true;
            Changed?.Invoke();
            if (status != null) Status?.Invoke(status + " 还没保存，点「保存修改」才会写进存档。");
        }

        private void ApplyEdit(Action<ItemView> apply)
        {
            if (_updating) return;
            var sel = Selected();
            if (sel.Count != 1) return;
            apply(sel[0]);
            RefreshRow(sel[0]);
            MarkChanged(null);
        }

        private void RefreshRow(ItemView it)
        {
            foreach (ListViewItem li in _list.Items)
            {
                if (ItemOf(li)?.Msg != it.Msg) continue;
                int o = _equipColumns ? 1 : 0;
                li.SubItems[1 + o].Text = it.CountText;
                li.SubItems[2 + o].Text = it.DurabilityText;
                li.SubItems[3 + o].Text = it.StateText;
            }
            _grid.Invalidate();
        }

        private void Info(string text, string title)
        {
            if (Quiet) Status?.Invoke("[" + title + "] " + text);
            else MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoAdd(Point? at)
        {
            if (_inv != null && _equipView)
            {
                var rows = SelectedPos();
                if (rows.Count == 1) DoEquip(rows[0]);
                return;
            }
            if (!GridMode) return;
            using (var dlg = new AddItemDialog(_box.Title))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Chosen == null) return;
                AddItems(dlg.Chosen, dlg.Amount, at);
            }
        }

        /// <summary>往当前格子区放 total 个某物品（能叠的按堆叠上限分格）。at = 第一份想放的格子。返回放进去了几个。</summary>
        internal int AddItems(ItemInfo info, int total, Point? at = null)
        {
            if (!GridMode) return 0;
            var added = new List<PMsg>();
            int left = total;
            while (left > 0)
            {
                int n = info.Stackable ? Math.Min(left, info.Stack) : 1;
                var item = ItemFactory.Create(info, n, _box.WorldTime);
                if (_box.Place(item, added.Count == 0 ? at : null) == null) break;
                added.Add(item);
                left -= n;
            }
            if (added.Count == 0)
            {
                Info($"「{_box.Title}」里放不下 {info.Name}（要 {info.W}×{info.H} 格的空地）。\n可以先删掉点东西、拖动物品腾出地方，或者放到别的背包里。", "放不下");
                return 0;
            }
            AfterStructureChange(added);
            string msg = left > 0
                ? $"空间不够，只放进了 {total - left} 个 {info.Name}（{added.Count} 格）。"
                : $"已添加 {total} 个 {info.Name}" + (added.Count > 1 ? $"（分成 {added.Count} 份）。" : "。");
            MarkChanged(msg);
            return total - left;
        }

        private void DoCopy()
        {
            var sel = Selected();
            if (!GridMode || sel.Count != 1) return;
            var clone = ItemFactory.CloneEmpty(sel[0].Msg);
            if (_box.Place(clone) == null)
            {
                Info("这个格子区里没有能放下它的空地了。", "放不下");
                return;
            }
            AfterStructureChange(new[] { clone });
            MarkChanged($"已复制一个 {sel[0].Name}。");
        }

        private void DoDelete()
        {
            if (_inv != null && _equipView)
            {
                DeleteEquipped(SelectedPos().Where(p => p.Item != null).ToList());
                return;
            }
            var sel = Selected();
            if (!GridMode || sel.Count == 0) return;
            int inner = 0;
            foreach (var it in sel)
            {
                var c = _inv.InnerOf(it);
                if (c != null) inner += CountDeep(c);
            }
            string what = sel.Count == 1 ? $"「{sel[0].Name}」" : $"选中的 {sel.Count} 件东西";
            string text = $"确定删除{what}吗？" + (inner > 0 ? $"\n\n里面还装着 {inner} 件东西，会一起删掉。" : "") +
                          "\n\n（保存之前点「撤销改动」可以全部恢复。）";
            if (!Quiet && MessageBox.Show(this, text, "删除物品", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var it in sel) _box.Remove(it);
            AfterStructureChange(null);
            MarkChanged($"已删除{what}。");
        }

        private int CountDeep(ContainerView c)
        {
            int n = 0;
            foreach (var it in c.Items)
            {
                n++;
                var inner = _inv.InnerOf(it);
                if (inner != null) n += CountDeep(inner);
            }
            return n;
        }

        private void DoRepair()
        {
            var sel = Selected();
            int n = sel.Count(it => it.Repair());
            FillList(sel.Select(x => x.Msg).ToList());
            if (n > 0) MarkChanged($"修好了 {n} 件。");
            else Status?.Invoke("选中的东西本来就是好的，没有要修的。");
        }

        private void DoFill()
        {
            var sel = Selected();
            int n = sel.Count(it => it.Fill());
            FillList(sel.Select(x => x.Msg).ToList());
            if (n > 0) MarkChanged($"补满了 {n} 件。");
            else Status?.Invoke("选中的东西没有能补的（不能堆叠、已经是满的）。");
        }

        // ---------- 挪动 ----------

        /// <summary>在当前格子区里把物品挪到 (x,y)，rot = 横放。</summary>
        internal bool MoveItemTo(ItemView it, int x, int y, bool rot)
        {
            if (!GridMode || it.Owner == null || it.Owner.Inv != _box.Inv) return false;
            if (x == it.X && y == it.Y && rot == it.Rotated) return true;
            if (!_box.MoveWithin(it, x, y, rot))
            {
                Status?.Invoke("那里放不下（超出格子或者压到别的东西了）。");
                return false;
            }
            AfterStructureChange(new[] { it.Msg });
            MarkChanged($"已把 {it.Name} 挪到 ({x},{y}){(rot ? "，横放" : "")}。");
            return true;
        }

        internal bool RotateItem(ItemView it)
        {
            if (!GridMode || it.Info.W == it.Info.H) return false;
            if (_box.MoveWithin(it, it.X, it.Y, !it.Rotated))
            {
                AfterStructureChange(new[] { it.Msg });
                MarkChanged($"已把 {it.Name} {(it.Rotated ? "转成横放" : "转成竖放")}。");
                return true;
            }
            Info("原地转不过来（转过来会压到别的东西或者超出格子）。可以先把它拖到空一点的地方再转。", "转不了");
            return false;
        }

        /// <summary>把物品放进格子图里的另一个背包。</summary>
        internal bool MoveIntoBag(ItemView it, ItemView bag)
        {
            var target = _inv?.InnerOf(bag);
            if (target == null) return false;
            return MoveToContainer(new List<ItemView> { it }, target) > 0;
        }

        /// <summary>把几件物品搬到另一个格子区（自动找空位）。返回搬过去几件。</summary>
        internal int MoveToContainer(List<ItemView> items, ContainerView target)
        {
            if (_inv == null || target == null || items.Count == 0) return 0;
            int moved = 0;
            var notFit = new List<string>();
            var self = new List<string>();
            foreach (var it in items)
            {
                if (it.Owner == null || it.Owner.Inv == target.Inv) continue;
                if (CharacterInventory.Holds(it.Msg, target.Inv)) { self.Add(it.Name); continue; }
                if (target.MoveIn(it) != null) moved++;
                else notFit.Add(it.Name);
            }
            if (moved > 0)
            {
                AfterStructureChange(null);
                MarkChanged($"已把 {moved} 件东西放进 {target.Title}。");
            }
            var msg = new List<string>();
            if (self.Count > 0) msg.Add($"不能把背包放进它自己里面：{string.Join("、", self.Distinct())}");
            if (notFit.Count > 0) msg.Add($"{target.Title} 里放不下：{string.Join("、", notFit.Distinct())}");
            if (msg.Count > 0) Info(string.Join("\n", msg), moved > 0 ? "有的没放进去" : "放不进去");
            return moved;
        }

        // ---------- 拖到左边树上 ----------

        private void StartDrag(List<ItemView> items)
        {
            if (_inv == null || items.Count == 0 || Quiet) return;
            if (_equipView) { _dragFromEquip = true; _dragSource = null; }
            else if (GridMode) _dragSource = _box;
            else return;
            var data = new DataObject(DragFormat, items.Select(x => x.Msg).ToList());
            try { DoDragDrop(data, DragDropEffects.Move); }
            finally
            {
                _dragSource = null;
                _dragFromEquip = false;
                HighlightDrop(null);
            }
        }

        private List<ItemView> DragItems(IDataObject data)
        {
            if (_inv == null || data == null || !data.GetDataPresent(DragFormat) || !(data.GetData(DragFormat) is List<PMsg> msgs)) return null;
            if (_dragFromEquip) return _inv.Equipped.Where(x => msgs.Contains(x.Msg)).ToList();
            if (_dragSource == null) return null;
            return msgs.Select(m => new ItemView(m, _dragSource, null, _dragSource.WorldTime)).ToList();
        }

        private void TreeDragOver(DragEventArgs e)
        {
            var items = DragItems(e.Data);
            var node = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            bool ok = false;
            if (items != null && items.Count > 0 && node != null)
            {
                if (node.Tag is ContainerView c)
                    ok = (_dragFromEquip || c.Inv != _dragSource.Inv) && items.All(it => !CharacterInventory.Holds(it.Msg, c.Inv));
                else if (node.Tag as string == "equip")
                    ok = !_dragFromEquip && items.Any(it => _inv.Slots.Any(p => p.Def.Accepts(it.Info)));
            }
            e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
            HighlightDrop(ok ? node : null);
        }

        private void HighlightDrop(TreeNode node)
        {
            if (_dropHighlight == node) return;
            if (_dropHighlight != null) { _dropHighlight.BackColor = Color.Empty; _dropHighlight.ForeColor = Color.Empty; }
            _dropHighlight = node;
            if (node != null) { node.BackColor = Color.FromArgb(70, 130, 200); node.ForeColor = Color.White; }
        }

        private void TreeDragDrop(DragEventArgs e)
        {
            var items = DragItems(e.Data);
            var node = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            HighlightDrop(null);
            if (items == null || items.Count == 0 || node == null) return;
            // 等拖放结束再改，免得树在拖的过程中被重建（那时 _dragSource 已经清掉了，先记下来）
            var src = _dragSource;
            if (_dragFromEquip && node.Tag is ContainerView to)
            {
                var rows = items.Select(it => _inv.Slots.FirstOrDefault(p => p.Item?.Msg == it.Msg)).Where(p => p != null).ToList();
                BeginInvoke(new Action(() => UnequipTo(rows, to)));
            }
            else if (!_dragFromEquip && node.Tag as string == "equip")
                BeginInvoke(new Action(() => AutoEquip(items, src)));
            else if (!_dragFromEquip && node.Tag is ContainerView target)
                BeginInvoke(new Action(() => MoveToContainer(items, target)));
        }

        // ---------- 装备栏：装上 / 卸下 / 删除 ----------

        private void DoEquip(EquipPos pos)
        {
            if (_inv == null || pos == null || pos.Def.Legacy) return;
            using (var dlg = new EquipDialog(_inv, pos))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.ChosenItem != null) EquipItem(pos, dlg.ChosenItem, dlg.ChosenFrom);
                else if (dlg.ChosenNew != null) EquipNew(pos, dlg.ChosenNew, dlg.Amount);
            }
        }

        private string Swapped(EquipPos pos, string oldName) =>
            oldName == null ? "" : pos.Def.Field == CharacterInventory.EqBackpack ? $"，原来的 {oldName}（连同里面的东西）放进了新背包" : $"，换下来的 {oldName} 放回了背包";

        /// <summary>把身上已有的一件东西（在 from 格子区里）装到 pos。</summary>
        internal bool EquipItem(EquipPos pos, ItemView it, ContainerView from)
        {
            string old = pos.Item?.Name;
            var err = _inv.Equip(pos, it.Msg, from);
            if (err != null) { Info(err, "装不上"); return false; }
            AfterStructureChange(new[] { it.Msg });
            MarkChanged($"已把 {it.Name} 装到「{pos.Name}」{Swapped(pos, old)}。");
            return true;
        }

        /// <summary>新造一件装到 pos。</summary>
        internal bool EquipNew(EquipPos pos, ItemInfo info, int count)
        {
            string old = pos.Item?.Name;
            var item = ItemFactory.Create(info, Math.Max(1, Math.Min(count, info.Stack)), _inv.WorldTime);
            var err = _inv.Equip(pos, item, null);
            if (err != null) { Info(err, "装不上"); return false; }
            AfterStructureChange(new[] { item });
            MarkChanged($"已把新的 {info.Name} 装到「{pos.Name}」{Swapped(pos, old)}。");
            return true;
        }

        /// <summary>拖到「装备栏」上：每件自动挑能放的槽（先挑空的）。返回装上了几件。</summary>
        internal int AutoEquip(List<ItemView> items, ContainerView from)
        {
            int ok = 0;
            var errs = new List<string>();
            var done = new List<PMsg>();
            foreach (var it in items)
            {
                var pos = _inv.AutoSlot(it.Info);
                if (pos == null)
                {
                    errs.Add(_inv.Slots.Any(p => p.Def.Accepts(it.Info)) ? $"{it.Name}：能放它的槽已经满了，先卸下一件。" : $"{it.Name} 不是能装备的东西。");
                    continue;
                }
                var e = _inv.Equip(pos, it.Msg, from);
                if (e == null) { ok++; done.Add(it.Msg); }
                else errs.Add(e);
            }
            if (ok > 0)
            {
                AfterStructureChange(done);
                MarkChanged($"已装上 {ok} 件。");
            }
            if (errs.Count > 0) Info(string.Join("\n", errs.Distinct()), ok > 0 ? "有的没装上" : "装不上");
            return ok;
        }

        private void DoUnequip()
        {
            if (_inv == null || !_equipView) return;
            UnequipTo(SelectedPos().Where(p => p.Item != null).ToList(), _inv.Main);
        }

        /// <summary>把装备卸下来放进 target 格子区。返回卸下了几件。</summary>
        internal int UnequipTo(List<EquipPos> rows, ContainerView target)
        {
            if (_inv == null || rows.Count == 0) return 0;
            int ok = 0;
            var errs = new List<string>();
            string title = target?.Title;
            foreach (var p in rows)
            {
                if (p.Item == null) continue;
                if (p.Def.Field == CharacterInventory.EqBackpack && target != null &&
                    (target.Inv == p.Item.Inventory || CharacterInventory.Holds(p.Item.Msg, target.Inv)))
                {
                    errs.Add("身上背的背包没地方卸（修改器里没有\"地上\"）。想换背包：选中「背包」槽点「＋ 换一个…」，原来的背包会放进新背包里；不要了就点「删除」。");
                    continue;
                }
                var e = _inv.Unequip(p, target);
                if (e == null) ok++;
                else errs.Add(e);
            }
            if (ok > 0)
            {
                AfterStructureChange(null);
                MarkChanged($"已卸下 {ok} 件，放进了 {title}。");
            }
            if (errs.Count > 0) Info(string.Join("\n", errs.Distinct()), ok > 0 ? "有的没卸下来" : "卸不下来");
            return ok;
        }

        internal void DeleteEquipped(List<EquipPos> rows)
        {
            if (_inv == null || rows.Count == 0) return;
            int inner = 0;
            foreach (var p in rows)
            {
                var c = _inv.InnerOf(p.Item);
                if (c != null) inner += CountDeep(c);
            }
            bool bag = rows.Any(p => p.Def.Field == CharacterInventory.EqBackpack);
            string what = rows.Count == 1 ? $"「{rows[0].Item.Name}」" : $"选中的 {rows.Count} 件装备";
            string text = $"确定删除{what}吗？" + (inner > 0 ? $"\n\n里面还装着 {inner} 件东西，会一起删掉。" : "") +
                          (bag ? "\n\n删掉身上背的背包后，随身物品栏会变成不背背包时的 6×8 空格子。" : "") +
                          "\n\n（保存之前点「撤销改动」可以全部恢复。）";
            if (!Quiet && MessageBox.Show(this, text, "删除装备", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var p in rows) _inv.Delete(p);
            AfterStructureChange(null);
            MarkChanged($"已删除{what}。");
        }

        /// <summary>增删、挪动物品后：背包可能多了/少了，左边的树重建，列表刷新。</summary>
        private void AfterStructureChange(ICollection<PMsg> select)
        {
            RebuildTree(_equipView ? null : _box?.Inv);
            FillList(select);
        }

        // ================= 界面自测 =================

        /// <summary>
        /// 界面自测：按用户的操作顺序把按钮、输入框和拖动走一遍。返回 (检查项, 是否通过) 列表。
        /// </summary>
        internal List<(string what, bool ok)> RunUiTest()
        {
            var r = new List<(string, bool)>();
            Quiet = true;
            r.Add(("背包页有格子区", _box != null && _list.Items.Count > 0));
            r.Add(("物品图标读进来了", ItemIcons.Has(0) && ItemIcons.Has(503) && ItemIcons.Has(10001)));
            r.Add(("「添加物品」按钮可以点", _btnAdd.Enabled && _btnAdd.Visible));
            int before = _list.Items.Count;

            // 1) 选一件不能叠的东西，改耐久
            int idx = -1;
            for (int i = 0; i < _list.Items.Count; i++)
                if (!((ItemView)_list.Items[i].Tag).Info.Stackable) { idx = i; break; }
            PreviewSelect(idx);
            var it = Selected().FirstOrDefault();
            _numDur.Value = 33.3m;
            r.Add(("耐久框改成 33.3 后物品跟着变", it != null && Math.Abs(it.Durability - 33.3f) < 0.01));
            r.Add(("列表那一行也刷新了", _list.SelectedItems.Count == 1 && _list.SelectedItems[0].SubItems[2].Text == "33.3"));
            r.Add(("标记为有改动", Dirty));

            // 2) 选一件能叠的东西，改数量
            for (int i = 0; i < _list.Items.Count; i++)
                if (((ItemView)_list.Items[i].Tag).Info.Stackable) { PreviewSelect(i); break; }
            var st = Selected().FirstOrDefault();
            if (st != null)
            {
                _numCount.Value = Math.Min(_numCount.Maximum, 2);
                r.Add(("数量框改成 2 后物品跟着变", Math.Abs(st.Count - (float)Math.Min(_numCount.Maximum, 2)) < 0.01));
            }

            // 3) 全选 → 修好 → 补满
            SelectAll();
            r.Add(("全选", _list.SelectedItems.Count == _list.Items.Count));
            DoRepair();
            r.Add(("修好后耐久都是 100", CurrentItems().All(x => x.Durability >= 100)));
            DoFill();
            r.Add(("补满后能叠的都满了", CurrentItems().All(x => !x.Info.Stackable || x.Count >= x.Info.Stack)));

            // 4) 复制一件小东西，再删掉占地最大的一件（腾出地方）
            var small = CurrentItems().First(x => x.Inventory == null);
            SelectItems(new[] { small.Msg });
            DoCopy();
            r.Add(("复制后列表多一件", _list.Items.Count == before + 1));
            var big = CurrentItems().OrderByDescending(x => x.W * x.H).First();
            SelectItems(new[] { big.Msg });
            DoDelete();
            r.Add(("删除后列表少一件", _list.Items.Count == before));

            // 5) 添加：一个新背包（树上应该多一个节点）+ 150 个木材，第一份放到指定的空格
            int nodesBefore = CountNodes(_tree.Nodes);
            var occBag = _box.Occupied();
            Point? bagSpot = null;
            var bagInfo = ItemDb.Get(501);
            for (int y = 0; y < _box.H && bagSpot == null; y++)
                for (int x = 0; x < _box.W && bagSpot == null; x++)
                    if (ContainerView.Fits(occBag, x, y, bagInfo.W, bagInfo.H)) bagSpot = new Point(x, y);
            int put = AddItems(bagInfo, 1, bagSpot);
            r.Add(("添加探险背包", put == 1 && CountNodes(_tree.Nodes) == nodesBefore + 1));
            var newBagItem = CurrentItems().FirstOrDefault(x => x.Id == 501 && x.Inventory != null && x.Inventory.GetMsgs(IF.InvItems).Count == 0 && bagSpot != null && x.X == bagSpot.Value.X && x.Y == bagSpot.Value.Y);
            r.Add(("新背包放在指定的格子上", newBagItem != null));
            r.Add(("添加后还停在原来的格子区", _box != null && _box.Inv == _inv.Main.Inv));
            put = AddItems(ItemDb.Get(0), 150);
            r.Add(("添加 150 个木材", put == 150 || put == 100 || put == 0));

            // 6) 用鼠标在格子图里拖一件 1 格宽的东西到空地
            var occ = _box.Occupied();
            var mover = CurrentItems().FirstOrDefault(x => x.Inventory == null && x.W * x.H <= 2);
            Point? spot = null;
            if (mover != null)
            {
                var occ2 = _box.Occupied(mover.Msg);
                for (int y = 0; y < _box.H && spot == null; y++)
                    for (int x = 0; x < _box.W && spot == null; x++)
                        if ((x != mover.X || y != mover.Y) && ContainerView.Fits(occ2, x, y, mover.W, mover.H)) spot = new Point(x, y);
            }
            r.Add(("找到能拖过去的空地", spot != null));
            if (spot != null)
            {
                var msg = mover.Msg;
                bool done = _grid.SimulateDrag(mover, spot.Value.X, spot.Value.Y, false);
                var now = CurrentItems().First(x => x.Msg == msg);
                r.Add(("鼠标拖动后物品到了新位置", done && now.X == spot.Value.X && now.Y == spot.Value.Y));
                // 拖到压住别的东西的地方：不应该动
                var other = CurrentItems().First(x => x.Msg != msg && x.Inventory == null);
                bool bad = _grid.SimulateDrag(now, other.X, other.Y, false);
                var still = CurrentItems().First(x => x.Msg == msg);
                r.Add(("拖到压住别的东西的地方不会动", !bad && still.X == spot.Value.X && still.Y == spot.Value.Y));
            }
            // 拖动时转向
            var longOne = CurrentItems().FirstOrDefault(x => x.Inventory == null && x.Info.W != x.Info.H);
            if (longOne != null)
            {
                var occ3 = _box.Occupied(longOne.Msg);
                bool rot = !longOne.Rotated;
                int w = rot ? longOne.Info.H : longOne.Info.W, h = rot ? longOne.Info.W : longOne.Info.H;
                Point? p = null;
                for (int y = 0; y < _box.H && p == null; y++)
                    for (int x = 0; x < _box.W && p == null; x++)
                        if (ContainerView.Fits(occ3, x, y, w, h)) p = new Point(x, y);
                if (p != null)
                {
                    var msg = longOne.Msg;
                    bool done = _grid.SimulateDrag(longOne, p.Value.X, p.Value.Y, true);
                    var now = CurrentItems().First(x => x.Msg == msg);
                    r.Add(("拖动时按右键转向", done && now.Rotated == rot && now.X == p.Value.X && now.Y == p.Value.Y));
                }
            }

            // 7) 在格子图上把一件东西拖到新背包上 = 放进去
            if (newBagItem != null)
            {
                var thing = CurrentItems().Where(x => x.Inventory == null).OrderBy(x => x.W * x.H).FirstOrDefault();
                var thingMsg = thing.Msg;
                bool done = _grid.SimulateDrag(thing, newBagItem.X, newBagItem.Y, false);
                r.Add(("拖到背包上就放进了背包", done && !CurrentItems().Any(x => x.Msg == thingMsg) &&
                                              newBagItem.Inventory.GetMsgs(IF.InvItems).Contains(thingMsg)));
                // 背包不能放进它自己里面
                var outer = CurrentItems().First(x => x.Msg == newBagItem.Msg);
                var self = _inv.InnerOf(outer);
                r.Add(("背包不能放进自己里面", MoveToContainer(new List<ItemView> { outer }, self) == 0 && CurrentItems().Any(x => x.Msg == outer.Msg)));
            }

            // 8) 拖到左边树上的别的背包（这里直接调拖放用的同一个函数）
            TreeNode newBag = AllNodes(_tree.Nodes).FirstOrDefault(n => n.Tag is ContainerView c && newBagItem != null && c.Inv == newBagItem.Inventory);
            r.Add(("树上找到新背包", newBag != null));
            if (newBag != null)
            {
                var two = CurrentItems().Where(x => x.Inventory == null).OrderBy(x => x.W * x.H).Take(2).ToList();
                int moved = MoveToContainer(two, (ContainerView)newBag.Tag);
                r.Add(("两件东西拖进了左边的背包", moved == two.Count && newBagItem.Inventory.GetMsgs(IF.InvItems).Count == 1 + two.Count));
                _tree.SelectedNode = AllNodes(_tree.Nodes).First(n => n.Tag is ContainerView c && c.Inv == newBagItem.Inventory);
                put = AddItems(ItemDb.Get(420), 1);
                r.Add(("往新背包里放了一把枪", put == 1 && _list.Items.Count == 2 + two.Count));
            }

            // 9) 装备栏：全部槽位都列出来（空的也列），能修、装上、卸下、换背包
            var eqNode = _tree.Nodes.Cast<TreeNode>().First(n => n.Tag as string == "equip");
            _tree.SelectedNode = eqNode;
            r.Add(("装备栏列出全部槽位（空的也列）", _equipView && _list.Items.Count == _inv.Slots.Count && _inv.Slots.Count >= 20));
            SelectAll();
            r.Add(("装备栏全选后能修、能卸下、能删除，不能复制", _btnRepair.Enabled && _btnUnequip.Visible && _btnUnequip.Enabled && _btnDel.Enabled && !_btnCopy.Enabled));
            DoRepair();
            r.Add(("装备修好", _inv.Equipped.All(x => x.Durability >= 100)));
            EquipPos Slot(int field, int index = 0) => _inv.Slots.First(p => p.Def.Field == field && p.Index == index);
            r.Add(("武器槽规矩：长枪进武器 1 不进武器 3，手枪进武器 3，近战进武器 1 不进武器 3，匕首进处决工具",
                Slot(57, 0).Def.Accepts(ItemDb.Get(420)) && !Slot(57, 2).Def.Accepts(ItemDb.Get(420)) && Slot(57, 2).Def.Accepts(ItemDb.Get(406)) &&
                Slot(57, 0).Def.Accepts(ItemDb.Get(309)) && !Slot(57, 2).Def.Accepts(ItemDb.Get(309)) && Slot(57, 3).Def.Accepts(ItemDb.Get(70)) &&
                !Slot(57, 3).Def.Accepts(ItemDb.Get(420))));
            r.Add(("其他槽规矩：背包槽不收弹壳收集器，照明槽收手电筒，头饰槽收头饰，外骨骼槽只收外骨骼",
                Slot(54).Def.Accepts(ItemDb.Get(515)) && !Slot(54).Def.Accepts(ItemDb.Get(520)) && Slot(65).Def.Accepts(ItemDb.Get(90)) &&
                Slot(63).Def.Accepts(ItemDb.Get(1617)) && Slot(68).Def.Accepts(ItemDb.Get(1083)) && !Slot(62).Def.Accepts(ItemDb.Get(1083))));

            // 选中空的处决工具槽 → 「装上」按钮；装一把新匕首（武器数组从 3 格长到 4 格）
            SelectSlot(Slot(57, 3));
            r.Add(("选中空槽，「装上」可以点", _btnAdd.Enabled && _btnAdd.Text.Contains("装上") && !_btnUnequip.Enabled));
            r.Add(("新匕首装进处决工具槽", EquipNew(Slot(57, 3), ItemDb.Get(70), 1) && Slot(57, 3).Item?.Id == 70 &&
                                          _inv.Cd.GetMsg(CharacterInventory.CdEquipment).GetMsgs(57).Count == 4));
            r.Add(("枪装不进处决工具槽", !EquipNew(Slot(57, 3), ItemDb.Get(420), 1) && Slot(57, 3).Item?.Id == 70));

            // 卸下武器 2 → 放进一个有空地的背包，武器数组长度不变（空格是占位）
            var w2Msg = Slot(57, 1).Item?.Msg;
            var room = w2Msg == null ? null : _inv.AllContainers().Select(x => x.box).FirstOrDefault(x => x.CanPlace(Slot(57, 1).Item.Info, null));
            r.Add(("卸下武器 2 放进背包", w2Msg != null && room != null && UnequipTo(new List<EquipPos> { Slot(57, 1) }, room) == 1 && Slot(57, 1).Empty &&
                                          room.Items.Any(x => x.Msg == w2Msg) &&
                                          _inv.Cd.GetMsg(CharacterInventory.CdEquipment).GetMsgs(57).Count == 4 &&
                                          _inv.Cd.GetMsg(CharacterInventory.CdEquipment).GetMsgs(57)[1].GetInt(IF.ItemId) == -1));
            // 拖到「装备栏」上（自动挑空槽）→ 回到武器 2
            var back = room?.Items.FirstOrDefault(x => x.Msg == w2Msg);
            r.Add(("拖到装备栏上自动装回武器 2", back != null && AutoEquip(new List<ItemView> { back }, room) == 1 && Slot(57, 1).Item?.Msg == w2Msg &&
                                                !room.Items.Any(x => x.Msg == w2Msg)));

            // 衣服：头部 1 件；上衣最多 3 件，满了再拖就装不上
            var head = ItemDb.Addable().First(i => i.Kind == ItemKind.Clothing && i.ClothPart == 0 && i.Known);
            var torso = ItemDb.Addable().First(i => i.Kind == ItemKind.Clothing && i.ClothPart == 1 && i.Known);
            r.Add(("戴上头部", EquipNew(Slot(50), head, 1) && Slot(50).Item?.Id == head.Id));
            int emptyTorso = _inv.Slots.Count(p => p.Def.Field == 60 && p.Empty);
            int put3 = 0;
            for (int k = 0; k < emptyTorso; k++)
            {
                var p = _inv.Slots.First(x => x.Def.Field == 60 && x.Empty);
                if (EquipNew(p, torso, 1)) put3++;
            }
            r.Add(("上衣穿满 3 件", put3 == emptyTorso && _inv.Slots.Count(p => p.Def.Field == 60 && !p.Empty) == 3 &&
                                     _inv.Slots.Count(p => p.Def.Field == 60) == 3 && _inv.AutoSlot(torso) == null));

            // 换背包：新的巨蜥皮背包装到背包槽，原来的背包连同里面的东西放进新背包
            var oldBag = _inv.WornBackpack;
            int countBefore = _inv.CountAll();
            int oldInner = CountDeep(_inv.Main);
            var bigInfo = ItemDb.Get(515);
            r.Add(("换上新背包", oldBag != null && EquipNew(Slot(54), bigInfo, 1) && _inv.WornBackpack?.Id == 515));
            r.Add(("原来的背包放进了新背包，里面东西一件没少",
                _inv.Main != null && _inv.Main.Inv == _inv.WornBackpack.Inventory && _inv.Main.Items.Count == 1 && _inv.Main.Items[0].Msg == oldBag.Msg &&
                CountDeep(_inv.InnerOf(_inv.Main.Items[0])) == oldInner && _inv.CountAll() == countBefore + 1));
            r.Add(("树上的第一个节点变成新背包", _tree.Nodes[0].Tag is ContainerView c0 && c0.Inv == _inv.Main.Inv));

            // 不背背包、再背上：在一份拷贝上做（不影响要保存的数据），连同保存前自检
            r.AddRange(NoBackpackTest());
            return r;
        }

        /// <summary>在人物数据的拷贝上试：删掉背包 → 随身 6×8 → 放东西 → 背上新背包（东西挪进去）→ 从背包里拿一个背包换上。</summary>
        private List<(string, bool)> NoBackpackTest()
        {
            var r = new List<(string, bool)>();
            var orig = _inv.Cd.Clone();
            var cd = _inv.Cd.Clone();
            var inv = new CharacterInventory(cd, _inv.WorldTime);
            var bagPos = inv.Slots.First(p => p.Def.Field == CharacterInventory.EqBackpack);
            inv.Delete(bagPos);
            r.Add(("删掉背包后随身物品栏是 6×8 空格子", inv.WornBackpack == null && inv.Main != null && inv.Main.W == 6 && inv.Main.H == 8 && inv.Main.Items.Count == 0));
            string Check(PMsg c, PMsg o)
            {
                try { GameFiles.ValidateItems(c, o); return null; }
                catch (Exception ex) { return ex.Message; }
            }
            inv.SyncBeforeSave();
            r.Add(("不背背包的数据能通过保存前自检", Check(cd, orig) == null));
            int n = 0;
            foreach (var id in new[] { 0, 101, 210, 224 })
                if (inv.Main.Place(ItemFactory.Create(ItemDb.Get(id), 1, inv.WorldTime)) != null) n++;
            var bag = ItemFactory.Create(ItemDb.Get(500), 1, inv.WorldTime);
            var err = inv.Equip(inv.Slots.First(p => p.Def.Field == CharacterInventory.EqBackpack), bag, null);
            r.Add(("背上新背包，随身格子里的东西挪进去了", err == null && n == 4 && inv.WornBackpack?.Msg == bag && inv.Main.Items.Count == 4));
            var bag2 = ItemFactory.Create(ItemDb.Get(515), 1, inv.WorldTime);
            var placed = inv.Main.Place(bag2);
            err = placed == null ? "放不下" : inv.Equip(inv.Slots.First(p => p.Def.Field == CharacterInventory.EqBackpack), bag2, inv.Main);
            r.Add(("从背包里拿一个背包换上，原来的背包进了新背包", err == null && inv.WornBackpack?.Msg == bag2 &&
                                                                    inv.Main.Items.Count == 1 && inv.Main.Items[0].Msg == bag &&
                                                                    bag.GetMsg(IF.Inventory).GetMsgs(IF.InvItems).Count == 4));
            inv.SyncBeforeSave();
            r.Add(("换背包后两份背包内容一致、能通过自检",
                Check(cd, orig) == null && cd.GetBytes(CharacterInventory.CdInventory).SequenceEqual(bag2.GetMsg(IF.Inventory).ToBytes())));
            var re = new CharacterInventory(PMsg.Parse(cd.ToBytes()), _inv.WorldTime);
            r.Add(("重新读一遍还是新背包", re.WornBackpack?.Id == 515 && re.Main.Items.Count == 1 && re.Main.Items[0].Id == 500));
            return r;
        }

        private static int CountNodes(TreeNodeCollection nodes)
        {
            int n = 0;
            foreach (TreeNode x in nodes) n += 1 + CountNodes(x.Nodes);
            return n;
        }

        // ================= 格子图 =================

        /// <summary>把格子区画出来（带物品图标），选中的高亮；能拖动换位置、转向、拖进背包。</summary>
        private sealed class GridView : Control
        {
            public event Action<ItemView> ItemClicked;
            public event Action<ItemView> ItemDoubleClicked;
            public event Func<ItemView, int, int, bool, bool> MoveRequested;
            public event Func<ItemView, ItemView, bool> DropIntoBag;
            public event Action<ItemView> DragOut;
            public event Action<ItemView, Point?, Point> ContextRequested;
            public bool CanDrag;
            public string EmptyText;

            private ContainerView _box;
            private List<ItemView> _items = new List<ItemView>();
            private ICollection<PMsg> _sel = new List<PMsg>();
            private int _cell;
            private Point _origin;

            // 拖动中的状态
            private ItemView _press;
            private Point _pressPt;
            private Point _grab;            // 按下的地方在物品里的第几格
            private bool _dragging, _rot, _valid, _rightRotated;
            private Point _ghost;
            private bool[,] _occ;
            private ItemView _bagUnder;

            public GridView()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                BackColor = Color.FromArgb(40, 42, 46);
            }

            public void SetBox(ContainerView box, ICollection<PMsg> sel)
            {
                CancelDrag();
                _box = box;
                _items = box?.Items ?? new List<ItemView>();
                _sel = sel ?? new List<PMsg>();
                Invalidate();
            }

            public void SetSelection(ICollection<PMsg> sel)
            {
                _sel = sel ?? new List<PMsg>();
                Invalidate();
            }

            private static Color KindColor(ItemKind k)
            {
                switch (k)
                {
                    case ItemKind.Food: return Color.FromArgb(96, 140, 72);
                    case ItemKind.MeleeWeapon: return Color.FromArgb(150, 90, 70);
                    case ItemKind.RangedWeapon: return Color.FromArgb(160, 70, 70);
                    case ItemKind.Ammo:
                    case ItemKind.Magazine:
                    case ItemKind.Arrow: return Color.FromArgb(170, 130, 60);
                    case ItemKind.GunPart: return Color.FromArgb(130, 100, 150);
                    case ItemKind.Backpack: return Color.FromArgb(70, 110, 150);
                    case ItemKind.Clothing:
                    case ItemKind.Cloak:
                    case ItemKind.FaceMask: return Color.FromArgb(80, 130, 130);
                    case ItemKind.Container:
                    case ItemKind.Liquid: return Color.FromArgb(60, 120, 170);
                    default: return Color.FromArgb(110, 110, 115);
                }
            }

            private void Layout2()
            {
                if (_box == null || _box.W <= 0 || _box.H <= 0) { _cell = 0; return; }
                _cell = Math.Max(6, Math.Min(Math.Min((ClientSize.Width - 8) / _box.W, (ClientSize.Height - 8) / _box.H), 40));
                _origin = new Point(Math.Max(4, (ClientSize.Width - _cell * _box.W) / 2), 4);
            }

            private Rectangle CellRect(int x, int y, int w, int h) =>
                new Rectangle(_origin.X + x * _cell + 1, _origin.Y + y * _cell + 1, w * _cell - 2, h * _cell - 2);

            private Rectangle Rect(ItemView it) => CellRect(it.X, it.Y, it.W, it.H);

            private Point CellAt(Point p) =>
                new Point((int)Math.Floor((p.X - _origin.X) / (double)_cell), (int)Math.Floor((p.Y - _origin.Y) / (double)_cell));

            private ItemView HitItem(Point p)
            {
                for (int i = _items.Count - 1; i >= 0; i--)     // 后画的在上面，所以倒着找
                    if (Rect(_items[i]).Contains(p)) return _items[i];
                return null;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                Layout2();
                if (_cell == 0)
                {
                    TextRenderer.DrawText(g, _box == null ? (EmptyText ?? "（没有格子图）") : "（这个格子区是空的）", Font,
                        Rectangle.Inflate(ClientRectangle, -10, -10), Color.Silver,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                int w = _box.W, h = _box.H;
                using (var pen = new Pen(Color.FromArgb(70, 72, 78)))
                {
                    for (int x = 0; x <= w; x++) g.DrawLine(pen, _origin.X + x * _cell, _origin.Y, _origin.X + x * _cell, _origin.Y + h * _cell);
                    for (int y = 0; y <= h; y++) g.DrawLine(pen, _origin.X, _origin.Y + y * _cell, _origin.X + w * _cell, _origin.Y + y * _cell);
                }
                using (var small = new Font(Font.FontFamily, Math.Max(6f, Math.Min(9f, _cell * 0.4f))))
                {
                    foreach (var it in _items)
                    {
                        bool moving = _dragging && it.Msg == _press?.Msg;
                        DrawItem(g, it, Rect(it), it.Rotated, small, _sel.Contains(it.Msg), moving ? 0.3f : 1f);
                    }
                    if (_bagUnder != null)
                    {
                        var br = Rect(_bagUnder);
                        using (var b = new SolidBrush(Color.FromArgb(110, 70, 140, 230))) g.FillRectangle(b, br);
                        using (var p = new Pen(Color.DeepSkyBlue, 2.5f)) g.DrawRectangle(p, br);
                        TextRenderer.DrawText(g, "放进" + _bagUnder.Name, small, br, Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    }
                    if (_dragging && _press != null)
                    {
                        var info = _press.Info;
                        int gw = _rot ? info.H : info.W, gh = _rot ? info.W : info.H;
                        var gr = CellRect(_ghost.X, _ghost.Y, gw, gh);
                        var col = _bagUnder != null ? Color.DeepSkyBlue : _valid ? Color.LimeGreen : Color.OrangeRed;
                        using (var b = new SolidBrush(Color.FromArgb(70, col))) g.FillRectangle(b, gr);
                        ItemIcons.Draw(g, _press.Id, gr, _rot, 0.85f);
                        using (var p = new Pen(col, 2.5f)) g.DrawRectangle(p, gr);
                    }
                }
            }

            private static void DrawItem(Graphics g, ItemView it, Rectangle r, bool rotated, Font small, bool sel, float alpha)
            {
                using (var b = new SolidBrush(Color.FromArgb((int)((sel ? 150 : 90) * alpha), KindColor(it.Info.Kind))))
                    g.FillRectangle(b, r);
                if (ItemIcons.Has(it.Id)) ItemIcons.Draw(g, it.Id, Rectangle.Inflate(r, -1, -1), rotated, alpha);
                else if (r.Width >= 18 && r.Height >= 12)
                    TextRenderer.DrawText(g, it.Name, small, Rectangle.Inflate(r, -2, -1), Color.White,
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (it.Info.Stackable && it.Count > 1 && r.Width >= 14)
                    TextRenderer.DrawText(g, ((int)Math.Round(it.Count)).ToString(), small, r, Color.White,
                        TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
                using (var p = new Pen(sel ? Color.Gold : Color.FromArgb((int)(200 * alpha), 20, 20, 24), sel ? 2.5f : 1f))
                    g.DrawRectangle(p, r);
            }

            // ---------- 鼠标 ----------

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                if (e.Button == MouseButtons.Left) Press(e.Location);
                else if (e.Button == MouseButtons.Right && _dragging) { ToggleRotate(); _rightRotated = true; }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if ((e.Button & MouseButtons.Left) != 0) Drag(e.Location);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left) Release(e.Location);
                else if (e.Button == MouseButtons.Right)
                {
                    if (_rightRotated) { _rightRotated = false; return; }
                    if (_dragging || _box == null) return;
                    Layout2();
                    var hit = _cell > 0 ? HitItem(e.Location) : null;
                    Point? cell = null;
                    if (_cell > 0)
                    {
                        var c = CellAt(e.Location);
                        if (c.X >= 0 && c.Y >= 0 && c.X < _box.W && c.Y < _box.H) cell = c;
                    }
                    ContextRequested?.Invoke(hit, cell, e.Location);
                }
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                if (e.Button != MouseButtons.Left || _cell == 0) return;
                var hit = HitItem(e.Location);
                if (hit != null) ItemDoubleClicked?.Invoke(hit);
            }

            protected override bool IsInputKey(Keys keyData) => keyData == Keys.R || keyData == Keys.Escape || base.IsInputKey(keyData);

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (!_dragging) return;
                if (e.KeyCode == Keys.R) ToggleRotate();
                if (e.KeyCode == Keys.Escape) CancelDrag();
            }

            private void Press(Point p)
            {
                Layout2();
                if (_cell == 0) return;
                var hit = HitItem(p);
                if (hit == null) return;
                ItemClicked?.Invoke(hit);
                if (!CanDrag) return;
                _press = hit;
                _pressPt = p;
                _rot = hit.Rotated;
                var c = CellAt(p);
                _grab = new Point(Math.Max(0, Math.Min(hit.W - 1, c.X - hit.X)), Math.Max(0, Math.Min(hit.H - 1, c.Y - hit.Y)));
            }

            private void Drag(Point p)
            {
                if (_press == null) return;
                if (!_dragging)
                {
                    if (Math.Abs(p.X - _pressPt.X) + Math.Abs(p.Y - _pressPt.Y) < 5) return;
                    _dragging = true;
                    _occ = _box.Occupied(_press.Msg);
                    Cursor = Cursors.SizeAll;
                }
                if (!ClientRectangle.Contains(p))
                {
                    // 拖出格子图：改成系统拖放，可以放到左边树上的背包里
                    var it = _press;
                    CancelDrag();
                    DragOut?.Invoke(it);
                    return;
                }
                UpdateGhost(p);
            }

            private void Release(Point p)
            {
                if (_press == null) return;
                if (!_dragging) { _press = null; return; }
                UpdateGhost(p);
                var it = _press;
                var bag = _bagUnder;
                bool valid = _valid;
                var ghost = _ghost;
                bool rot = _rot;
                CancelDrag();
                if (bag != null) DropIntoBag?.Invoke(it, bag);
                else if (valid) MoveRequested?.Invoke(it, ghost.X, ghost.Y, rot);
            }

            private void ToggleRotate()
            {
                if (_press == null || _press.Info.W == _press.Info.H) return;
                _rot = !_rot;
                _grab = new Point(_grab.Y, _grab.X);
                UpdateGhost(PointToClient(Cursor.Position));
            }

            private void UpdateGhost(Point p)
            {
                var info = _press.Info;
                int w = _rot ? info.H : info.W, h = _rot ? info.W : info.H;
                _grab = new Point(Math.Min(_grab.X, w - 1), Math.Min(_grab.Y, h - 1));
                var c = CellAt(p);
                _ghost = new Point(c.X - _grab.X, c.Y - _grab.Y);
                _valid = ContainerView.Fits(_occ, _ghost.X, _ghost.Y, w, h);
                _bagUnder = null;
                if (!_valid)
                {
                    // 鼠标底下是个背包（不是自己、也不是自己里面的）→ 放进去
                    var under = HitItem(p);
                    if (under != null && under.Msg != _press.Msg && under.Inventory != null &&
                        !CharacterInventory.Holds(_press.Msg, under.Inventory))
                        _bagUnder = under;
                }
                Invalidate();
            }

            private void CancelDrag()
            {
                _press = null;
                _dragging = false;
                _bagUnder = null;
                _occ = null;
                Cursor = Cursors.Default;
                Invalidate();
            }

            /// <summary>界面自测：模拟鼠标按住物品中间一格，(可选)右键转向，拖到 (x,y) 松开。返回是否真的动了。</summary>
            internal bool SimulateDrag(ItemView it, int x, int y, bool rotate)
            {
                Layout2();
                if (_cell == 0) return false;
                var target = _items.FirstOrDefault(i => i.Msg == it.Msg);
                if (target == null) return false;
                var start = CellCenter(target.X, target.Y);
                Press(start);
                if (_press == null) return false;
                _press = target;     // 叠在一起的东西，按下去点到的可能是上面那件
                _rot = target.Rotated;
                _grab = Point.Empty;
                Drag(new Point(start.X + 6, start.Y + 6));
                if (rotate) { _rot = !_rot; _grab = new Point(_grab.Y, _grab.X); }
                // 按的是物品左上那一格，所以松开在目标左上那一格
                var end = CellCenter(x, y);
                Drag(end);
                bool moved = false;
                Func<ItemView, int, int, bool, bool> h1 = (a, b, c, d) => moved = true;
                Func<ItemView, ItemView, bool> h2 = (a, b) => moved = true;
                MoveRequested += h1;
                DropIntoBag += h2;
                try { Release(end); }
                finally { MoveRequested -= h1; DropIntoBag -= h2; }
                return moved;
            }

            private Point CellCenter(int x, int y) => new Point(_origin.X + x * _cell + _cell / 2, _origin.Y + y * _cell + _cell / 2);
        }
    }
}
