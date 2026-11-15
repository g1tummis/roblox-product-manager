using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RobloxProductManager
{
    // One tab: "Create new" grid on top, "Existing" grid below, for game passes, developer products or badges.
    class KindPanel : UserControl
    {
        static readonly Color ChangedColor = Color.FromArgb(255, 243, 176);
        const int IconSize = 40;
        // Downloaded icons, shrunk to IconSize, kept for this session. Key: kind + id (product icons: asset id).
        static readonly Dictionary<string, Image> IconCache = new Dictionary<string, Image>();
        const string ImageFilter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";
        const string Done = "done";

        readonly MainForm main;
        readonly Kind kind;
        readonly bool priced;
        readonly DataGridView newGrid, oldGrid;
        readonly FlowLayoutPanel createBar, editBar;
        readonly Button createBtn, saveBtn;
        readonly TextBox sharedImage, filterBox;
        readonly ComboBox managedBox;
        readonly Label quotaLabel, countLabel;
        readonly SplitContainer split;
        bool suppress, splitPlaced;
        int loadSeq;
        public string LoadedFor;

        public static string PluralOf(Kind k) { return k == Kind.Pass ? "Game Passes" : k == Kind.Product ? "Developer Products" : "Badges"; }
        string Lower { get { return PluralOf(kind).ToLowerInvariant(); } }
        int GapMs { get { return kind == Kind.Pass ? 250 : kind == Kind.Product ? 400 : 700; } } // stays under Roblox rate limits

        public KindPanel(MainForm main, Kind kind)
        {
            this.main = main;
            this.kind = kind;
            priced = kind != Kind.Badge;
            Dock = DockStyle.Fill;

            split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 8 };
            split.SizeChanged += (s, e) =>
            {
                if (!splitPlaced && split.Height > 300) { split.SplitterDistance = split.Height * 45 / 100; splitPlaced = true; }
            };
            Controls.Add(split);

            // ---------- create ----------
            var createBox = new GroupBox { Text = "Create new " + Lower, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
            newGrid = MakeGrid();
            newGrid.Columns.Add(TextCol("name", "Name *", 26, false));
            if (priced) newGrid.Columns.Add(TextCol("price", "Price (Robux)", 10, false));
            newGrid.Columns.Add(TextCol("description", "Description", 36, false));
            newGrid.Columns.Add(priced ? CheckCol("forSale", "For sale") : CheckCol("active", "Active"));
            if (priced) newGrid.Columns.Add(CheckCol("giftable", "Giftable")); // also creates a "[GIFT]" developer product
            newGrid.Columns.Add(TextCol("image", "Image file", 22, false));
            newGrid.Columns.Add(ButtonCol());
            newGrid.Columns.Add(TextCol("status", "Status", 24, true));
            newGrid.DefaultValuesNeeded += (s, e) => { if (priced) { e.Row.Cells["price"].Value = "1"; e.Row.Cells["forSale"].Value = true; } else e.Row.Cells["active"].Value = false; }; // new badges start inactive
            newGrid.CellContentClick += (s, e) => { if (e.RowIndex >= 0 && newGrid.Columns[e.ColumnIndex].Name == "browse") BrowseImage(newGrid, e.RowIndex); };
            newGrid.CurrentCellDirtyStateChanged += (s, e) => { if (newGrid.CurrentCell is DataGridViewCheckBoxCell) newGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            newGrid.KeyDown += (s, e) => GridKeyDown(newGrid, e, true);
            newGrid.RowsAdded += (s, e) => UpdateCreateText();
            newGrid.RowsRemoved += (s, e) => UpdateCreateText();
            newGrid.CellValueChanged += (s, e) => UpdateCreateText();
            newGrid.AllowDrop = true;
            newGrid.DragEnter += OnDragEnter;
            newGrid.DragDrop += (s, e) => OnDrop(newGrid, e, true);

            createBar = Bar();
            createBtn = AddButton(createBar, "Create all", async (s, e) => await CreateAll());
            if (kind == Kind.Badge) AddButton(createBar, "Create free badges", async (s, e) => await CreateAll(true));
            AddButton(createBar, "Paste rows", (s, e) => PasteRows());
            AddButton(createBar, "Remove created rows", (s, e) => RemoveDone());
            createBar.Controls.Add(BarLabel("Image for rows without one:"));
            sharedImage = new TextBox { Width = 200 };
            createBar.Controls.Add(sharedImage);
            AddButton(createBar, "...", (s, e) => { var p = PickImage(); if (p != null) sharedImage.Text = p; });
            if (priced)
            {
                createBar.Controls.Add(BarLabel("Managed pricing:"));
                managedBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
                managedBox.Items.AddRange(new object[] { "Roblox default", "On", "Off" });
                managedBox.SelectedIndex = 0;
                createBar.Controls.Add(managedBox);
            }
            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Padding = new Padding(0, 2, 0, 4),
                Text = "Type in the grid, paste rows copied from Excel or Google Sheets (" + (priced ? "Name, Price, Description" : "Name, Description") +
                       ") with Ctrl+V, or drop image files on it to get one row per image.",
            };
            createBox.Controls.Add(newGrid);
            createBox.Controls.Add(hint);
            createBox.Controls.Add(createBar);
            if (kind == Kind.Badge)
            {
                quotaLabel = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 2, 0, 4) };
                createBox.Controls.Add(quotaLabel);
            }
            split.Panel1.Controls.Add(createBox);

            // ---------- existing ----------
            var editBox = new GroupBox { Text = "Existing " + Lower + ": edit the cells, then click Save changes", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
            oldGrid = MakeGrid();
            oldGrid.AllowUserToAddRows = false;
            oldGrid.AllowUserToDeleteRows = false;
            oldGrid.RowTemplate.Height = IconSize + 6;
            oldGrid.Columns.Add(new DataGridViewImageColumn
            {
                Name = "icon", HeaderText = "Icon", Width = IconSize + 12, AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true, DefaultCellStyle = { NullValue = null },
            });
            // Hover an icon for a big preview: the icon image asset (420x420) when known, else the pass/badge thumbnail.
            IconPreview.Attach(oldGrid, () => main.Api, row =>
            {
                var it = row.Tag as Item;
                if (it == null) return null;
                if (it.IconAssetId != "" && it.IconAssetId != "0") return Tuple.Create(Kind.Product, it.IconAssetId);
                return kind == Kind.Product ? null : Tuple.Create(kind, it.Id);
            });
            oldGrid.Columns.Add(TextCol("id", "ID", 12, true));
            oldGrid.Columns.Add(TextCol("name", "Name", 26, false));
            if (priced) oldGrid.Columns.Add(TextCol("price", "Price (Robux)", 10, false));
            oldGrid.Columns.Add(TextCol("description", "Description", 36, false));
            if (priced)
            {
                oldGrid.Columns.Add(CheckCol("forSale", "For sale"));
                { var mc = CheckCol("managed", "Managed pricing"); mc.MinimumWidth = 72; oldGrid.Columns.Add(mc); }
                oldGrid.Columns.Add(TextCol("image", "New image", 18, false));
                oldGrid.Columns.Add(ButtonCol());
            }
            else oldGrid.Columns.Add(CheckCol("enabled", "Enabled"));
            oldGrid.Columns.Add(TextCol("status", "Status", 22, true));
            oldGrid.CellValueChanged += (s, e) =>
            {
                if (!suppress && e.RowIndex >= 0 && oldGrid.Columns[e.ColumnIndex].Name != "status" && oldGrid.Columns[e.ColumnIndex].Name != "icon") MarkChanged(oldGrid.Rows[e.RowIndex]);
            };
            oldGrid.CurrentCellDirtyStateChanged += (s, e) => { if (oldGrid.CurrentCell is DataGridViewCheckBoxCell) oldGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            oldGrid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && oldGrid.Columns[e.ColumnIndex].Name == "browse" && !oldGrid.Rows[e.RowIndex].ReadOnly) BrowseImage(oldGrid, e.RowIndex);
            };
            oldGrid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && oldGrid.Columns[e.ColumnIndex].Name == "id") OpenOnRoblox(oldGrid.Rows[e.RowIndex]); };
            oldGrid.KeyDown += (s, e) => GridKeyDown(oldGrid, e, false);
            if (priced)
            {
                oldGrid.AllowDrop = true;
                oldGrid.DragEnter += OnDragEnter;
                oldGrid.DragDrop += (s, e) => OnDrop(oldGrid, e, false);
            }

            editBar = Bar();
            saveBtn = AddButton(editBar, "Save changes", async (s, e) => await SaveAll());
            AddButton(editBar, "Reload", async (s, e) => { if (!HasUnsaved || main.Ask("Throw away unsaved edits and reload?")) await LoadList(); });
            if (kind != Kind.Product)
                AddButton(editBar, "Open on Roblox", (s, e) => { if (oldGrid.CurrentRow != null) OpenOnRoblox(oldGrid.CurrentRow); });
            if (kind == Kind.Badge)
                AddButton(editBar, "Add badge by ID", async (s, e) => await AddBadgeById());
            if (priced)
                AddButton(editBar, "Make gift", async (s, e) => await MakeGifts());
            editBar.Controls.Add(BarLabel("Filter:"));
            filterBox = new TextBox { Width = 180 };
            filterBox.TextChanged += (s, e) => ApplyFilter();
            editBar.Controls.Add(filterBox);
            countLabel = BarLabel("");
            editBar.Controls.Add(countLabel);
            editBox.Controls.Add(oldGrid);
            editBox.Controls.Add(editBar);
            split.Panel2.Controls.Add(editBox);
        }

        // ---------- small UI builders ----------
        static DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                BackgroundColor = SystemColors.Window,
                AllowUserToResizeRows = false,
                RowHeadersWidth = 30,
            };
            g.DataError += (s, e) => { e.ThrowException = false; };
            return g;
        }

        static DataGridViewTextBoxColumn TextCol(string name, string header, float weight, bool readOnly)
        {
            return new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };
        }

        static DataGridViewCheckBoxColumn CheckCol(string name, string header)
        {
            return new DataGridViewCheckBoxColumn { Name = name, HeaderText = header, FillWeight = 8, MinimumWidth = 50 };
        }

        static DataGridViewButtonColumn ButtonCol()
        {
            return new DataGridViewButtonColumn { Name = "browse", HeaderText = "", Text = "...", UseColumnTextForButtonValue = true, FillWeight = 4, MinimumWidth = 32, ToolTipText = "Choose an image" };
        }

        static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0, 2, 0, 2) };
        }

        static Button AddButton(FlowLayoutPanel bar, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            bar.Controls.Add(b);
            return b;
        }

        static Label BarLabel(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 8, 2, 0) }; }

        // ---------- cell helpers ----------
        static string S(DataGridViewRow r, string col)
        {
            if (!r.DataGridView.Columns.Contains(col)) return "";
            var v = r.Cells[col].Value;
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
        }

        static bool B(DataGridViewRow r, string col)
        {
            if (!r.DataGridView.Columns.Contains(col)) return false;
            var v = r.Cells[col].Value;
            return v is bool && (bool)v;
        }

        static bool TryWhole(string s, out long v)
        {
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        }

        static void SetRowStatus(DataGridViewRow r, string text, int tone) // tone: 0 normal, 1 good, 2 error
        {
            var c = r.Cells["status"];
            c.Value = text;
            c.ToolTipText = text;
            c.Style.ForeColor = tone == 1 ? Color.ForestGreen : tone == 2 ? Color.Firebrick : Color.Empty;
        }

        static bool IsImage(string f)
        {
            string x = Path.GetExtension(f).ToLowerInvariant();
            return x == ".png" || x == ".jpg" || x == ".jpeg" || x == ".bmp";
        }

        bool IsBlank(DataGridViewRow r) { return S(r, "name") == "" && S(r, "description") == "" && S(r, "image") == ""; } // the default price alone does not count

        IEnumerable<DataGridViewRow> PendingRows()
        {
            return newGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && !Done.Equals(r.Tag) && !IsBlank(r));
        }

        bool addingRows; // set while many rows are added at once, so the button count updates only at the end

        void UpdateCreateText()
        {
            if (addingRows) return;
            int n = PendingRows().Count();
            createBtn.Text = n > 0 ? "Create all (" + n + ")" : "Create all";
        }

        void UpdateSaveText()
        {
            int n = oldGrid.Rows.Cast<DataGridViewRow>().Count(r => r.DefaultCellStyle.BackColor == ChangedColor);
            saveBtn.Text = n > 0 ? "Save changes (" + n + ")" : "Save changes";
        }

        public bool HasUnsaved { get { return oldGrid.Rows.Cast<DataGridViewRow>().Any(r => r.DefaultCellStyle.BackColor == ChangedColor); } }
        public bool HasPendingCreates { get { return PendingRows().Any(); } }

        // ---------- adding rows ----------
        DataGridViewRow AddNewRow(string name, string price, string description, string image, bool forSaleWithoutPrice)
        {
            newGrid.EndEdit();
            var r = newGrid.Rows[newGrid.Rows.Add()];
            r.Cells["name"].Value = name;
            r.Cells["description"].Value = description;
            r.Cells["image"].Value = image;
            if (priced)
            {
                string digits = new string((price ?? "").Where(char.IsDigit).ToArray());
                long v;
                bool has = digits.Length > 0 && digits.Length < 13 && TryWhole(digits, out v) && v > 0;
                r.Cells["price"].Value = has ? digits.TrimStart('0') : "1"; // 1 Robux is the lowest allowed price
                r.Cells["forSale"].Value = true;
            }
            else r.Cells["active"].Value = false; // new badges start inactive; tick Active to publish them right away
            return r;
        }

        static string ClipboardText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch (Exception) { return ""; }
        }

        void GridKeyDown(DataGridView g, KeyEventArgs e, bool isNew)
        {
            if (main.Busy) return;
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                string text = ClipboardText();
                if (isNew && (text.Contains("\n") || text.Contains("\t") || text.Contains("|"))) PasteRows();
                else PasteCell(g, text);
            }
            else if (e.KeyCode == Keys.Delete && g.SelectedRows.Count == 0)
            {
                e.Handled = true;
                foreach (DataGridViewCell c in g.SelectedCells)
                    if (!c.ReadOnly && !c.OwningRow.IsNewRow && c is DataGridViewTextBoxCell) c.Value = "";
            }
        }

        void PasteCell(DataGridView g, string text)
        {
            text = text.Replace("\r\n", "\n").Trim('\n');
            var c = g.CurrentCell;
            if (text == "" || c == null || c.ReadOnly || !(c is DataGridViewTextBoxCell)) return;
            var row = c.OwningRow;
            if (row.IsNewRow) row = AddNewRow("", "", "", "", true);
            row.Cells[c.ColumnIndex].Value = text.Split('\n')[0].Split('\t')[0].Trim();
        }

        void PasteRows()
        {
            if (main.Busy) return;
            int added = 0;
            addingRows = true;
            try
            {
            foreach (var raw in ClipboardText().Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim() == "") continue;
                var p = (raw.Contains("\t") ? raw.Split('\t') : raw.Split('|')).Select(x => x.Trim()).ToArray();
                if (added == 0 && p[0].Equals("name", StringComparison.OrdinalIgnoreCase)) continue; // header row
                if (priced) AddNewRow(p[0], p.Length > 1 ? p[1] : "", string.Join(" ", p.Skip(2)), "", false);
                else AddNewRow(p[0], "", string.Join(" ", p.Skip(1)), "", false);
                added++;
            }
            }
            finally { addingRows = false; UpdateCreateText(); }
            main.SetStatus(added == 0
                ? "There are no rows on the clipboard. Copy rows from Excel or Sheets (or lines like Name | Price | Description) first."
                : "Added " + added + " row(s). Check them, then click Create all.", added == 0);
        }

        void RemoveDone()
        {
            foreach (var r in newGrid.Rows.Cast<DataGridViewRow>().Where(r => Done.Equals(r.Tag)).ToList()) newGrid.Rows.Remove(r);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !main.Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDrop(DataGridView g, DragEventArgs e, bool isNew)
        {
            if (main.Busy) return;
            var files = ((e.Data.GetData(DataFormats.FileDrop) as string[]) ?? new string[0]).Where(IsImage).ToArray();
            if (files.Length == 0) { main.SetStatus("Only PNG, JPG or BMP images can be dropped here.", true); return; }
            var pt = g.PointToClient(new Point(e.X, e.Y));
            var hit = g.HitTest(pt.X, pt.Y);
            bool onRow = hit.RowIndex >= 0 && !g.Rows[hit.RowIndex].IsNewRow && !g.Rows[hit.RowIndex].ReadOnly;
            if (files.Length == 1 && onRow) { g.Rows[hit.RowIndex].Cells["image"].Value = files[0]; return; }
            if (!isNew) { main.SetStatus("Drop one image onto the row you want to change.", true); return; }
            addingRows = true;
            try { foreach (var f in files) AddNewRow(Path.GetFileNameWithoutExtension(f), "", "", f, true); }
            finally { addingRows = false; UpdateCreateText(); }
            main.SetStatus("Added " + files.Length + " row(s) from images. Fill in the details, then click Create all.");
        }

        string PickImage()
        {
            using (var d = new OpenFileDialog { Filter = ImageFilter, Title = "Choose an image" })
                return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
        }

        void BrowseImage(DataGridView g, int rowIndex)
        {
            if (main.Busy) return;
            string path = PickImage();
            if (path == null) return;
            var r = g.Rows[rowIndex];
            if (r.IsNewRow) AddNewRow("", "", "", path, true);
            else r.Cells["image"].Value = path;
        }

        void OpenOnRoblox(DataGridViewRow r)
        {
            var it = r.Tag as Item;
            if (it == null) return;
            if (kind == Kind.Pass) Browser.Open("https://www.roblox.com/game-pass/" + it.Id);
            else if (kind == Kind.Badge) Browser.Open("https://www.roblox.com/badges/" + it.Id);
        }

        // ---------- busy / reset ----------
        public void SetBusy(bool on)
        {
            createBar.Enabled = !on;
            editBar.Enabled = !on;
            newGrid.ReadOnly = on;
            oldGrid.ReadOnly = on;
            if (!on) ApplyReadOnly();
        }

        void ApplyReadOnly()
        {
            newGrid.Columns["status"].ReadOnly = true;
            oldGrid.Columns["id"].ReadOnly = true;
            oldGrid.Columns["status"].ReadOnly = true;
            foreach (DataGridViewRow r in newGrid.Rows) if (Done.Equals(r.Tag)) r.ReadOnly = true;
            foreach (DataGridViewRow r in oldGrid.Rows) { var it = r.Tag as Item; if (it != null && it.Locked) r.ReadOnly = true; }
        }

        public void Reset()
        {
            loadSeq++;
            LoadedFor = null;
            suppress = true;
            oldGrid.Rows.Clear();
            suppress = false;
            countLabel.Text = "";
            if (quotaLabel != null) quotaLabel.Text = "";
            UpdateSaveText();
        }

        // ---------- validation ----------
        string ValidateRow(DataGridViewRow r, string sharedImg)
        {
            if (S(r, "name") == "") return "Name is required.";
            if (priced)
            {
                string p = S(r, "price");
                long v;
                bool hasPrice = TryWhole(p, out v);
                if (p != "" && !hasPrice) return "Price must be a whole number.";
                if (B(r, "forSale") && !(hasPrice && v >= 1)) return "Enter a price of 1 or more, or untick For sale.";
            }
            string img = S(r, "image");
            if (img == "") img = sharedImg;
            if (img != "" && !File.Exists(img)) return "Image file not found: " + img;
            return "";
        }

        // ---------- create ----------
        // freeOnly (badges): create only as many as today's free quota allows and never spend Robux.
        async Task CreateAll(bool freeOnly = false)
        {
            var u = main.Game;
            if (u == null || main.Busy) return;
            newGrid.EndEdit();
            string shared = sharedImage.Text.Trim();
            if (shared != "" && !File.Exists(shared)) { main.SetStatus("The image for rows without one was not found: " + shared, true); return; }
            var rows = PendingRows().ToList();
            int free = int.MaxValue;

            if (freeOnly)
            {
                main.SetStatus("Checking free badges...");
                try { free = await main.Api.FreeBadgeQuota(u.Id); }
                catch (ApiException ex) { main.HandleApiError(ex, "Could not check free badges, so nothing was created: "); return; }
                if (free == 0) { main.SetStatus("No free badges left today for this game. The free count resets at 00:00 UTC.", true); return; }
                if (rows.Count < free)
                {
                    int extra = free - rows.Count;
                    string q = rows.Count == 0
                        ? "\"" + u.Name + "\" can create " + free + " free badge(s) today.\n\nAdd " + extra + " row(s) named \"Badge 1\", \"Badge 2\"... and create them? You can rename them afterwards."
                        : "\"" + u.Name + "\" can create " + free + " free badge(s) today, but only " + rows.Count + " row(s) are ready.\n\nYes: add " + extra +
                          " more row(s) named \"Badge 1\", \"Badge 2\"... so all free badges are used.\nNo: create just your " + rows.Count + " row(s).";
                    var answer = MessageBox.Show(this, q, "Create free badges", rows.Count == 0 ? MessageBoxButtons.OKCancel : MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) { main.SetStatus("Cancelled. Nothing was created."); return; }
                    if (answer == DialogResult.OK || answer == DialogResult.Yes)
                    {
                        var used = new HashSet<string>(newGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => S(r, "name")), StringComparer.OrdinalIgnoreCase);
                        int n = 1;
                        addingRows = true;
                        for (int i = 0; i < extra; i++)
                        {
                            while (used.Contains("Badge " + n)) n++;
                            used.Add("Badge " + n);
                            rows.Add(AddNewRow("Badge " + n, "", "", "", false));
                        }
                        addingRows = false; UpdateCreateText();
                    }
                }
                if (rows.Count > free)
                {
                    foreach (var r in rows.Skip(free)) SetRowStatus(r, "Waiting: no free badges left today", 0);
                    rows = rows.Take(free).ToList();
                }
            }

            if (rows.Count == 0) { main.SetStatus("Add at least one row first.", true); return; }
            int bad = 0;
            foreach (var r in rows) { string v = ValidateRow(r, shared); SetRowStatus(r, v, v != "" ? 2 : 0); if (v != "") bad++; }
            if (bad > 0) { main.SetStatus("Fix the " + bad + " row(s) marked in red.", true); return; }

            if (kind == Kind.Badge && !freeOnly)
            {
                main.SetStatus("Checking free badges...");
                try { free = await main.Api.FreeBadgeQuota(u.Id); }
                catch (ApiException ex) { main.HandleApiError(ex, "Could not check free badges, so nothing was created: "); return; }
                int paid = Math.Max(0, rows.Count - free);
                if (paid > 0 && MessageBox.Show(this,
                        "\"" + u.Name + "\" has " + free + " free badge(s) left today.\n\n" + paid + " badge(s) will cost 100 Robux each (" + (paid * 100) +
                        " Robux total), paid from " + Payer(u) + ".\n\nCreate them?",
                        "Robux will be spent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    main.SetStatus("Cancelled. Nothing was created.");
                    return;
                }
            }

            string managed = managedBox == null || managedBox.SelectedIndex == 0 ? "" : managedBox.SelectedIndex == 1 ? "true" : "false";
            if (managed == "true" && !ConfirmManagedPricing(rows.Count)) { main.SetStatus("Cancelled. Nothing was created."); return; }
            int ok = 0, fail = 0, unknown = 0, giftFail = 0;
            bool stopped = false, giftsMade = false;
            main.SetBusy(true);
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    main.SetStatus("Creating " + (i + 1) + " of " + rows.Count + "...");
                    SetRowStatus(r, "Creating...", 0);
                    long price = 0;
                    var n = new NewItem
                    {
                        Name = S(r, "name"),
                        Description = S(r, "description"),
                        ImagePath = S(r, "image") != "" ? S(r, "image") : shared,
                        Managed = managed,
                        ForSale = B(r, "forSale"),
                        Active = B(r, "active"),
                    };
                    if (priced && TryWhole(S(r, "price"), out price)) n.Price = price;
                    try
                    {
                        string id = await main.Api.CreateItem(kind, u, n, freeOnly || free > 0 ? 0 : 100);
                        r.Tag = Done;
                        r.ReadOnly = true;
                        r.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                        SetRowStatus(r, "Created" + (id != "" ? ", ID " + id : ""), 1);
                        ok++;
                        if (kind == Kind.Badge) free--;
                        if (priced && B(r, "giftable"))
                        {
                            // Matching developer product for gifting: same price and icon, never regional pricing.
                            await Task.Delay(400); // developer product creation is limited to 3 per second
                            var gift = new NewItem
                            {
                                Name = "[GIFT] " + n.Name,
                                Description = "Gift the " + n.Name + " " + (kind == Kind.Pass ? "pass" : "product") + " to a player",
                                ImagePath = n.ImagePath,
                                Price = n.Price,
                                ForSale = n.ForSale,
                                Regional = "false",
                                Managed = "false", // Managed pricing would turn regional pricing back on
                            };
                            try
                            {
                                string giftId = await main.Api.CreateItem(Kind.Product, u, gift, 0);
                                SetRowStatus(r, "Created, ID " + id + "; gift product ID " + giftId, 1);
                                giftsMade = true;
                            }
                            catch (ApiException gex)
                            {
                                if (gex.SignedOut) { stopped = true; main.HandleApiError(gex, ""); break; }
                                SetRowStatus(r, "Created, ID " + id + ", but the gift product " + (gex.Uncertain ? "may not have been created (no answer from Roblox). Reload Developer Products to check." : "failed: " + gex.Message) +
                                    " To retry, select this item in Existing and click Make gift.", 2);
                                giftFail++;
                            }
                        }
                    }
                    catch (ApiException ex)
                    {
                        if (ex.SignedOut) { stopped = true; main.HandleApiError(ex, ""); break; }
                        if (ex.Uncertain)
                        {
                            // Roblox may have created it: never retry this row automatically.
                            r.Tag = Done;
                            r.ReadOnly = true;
                            SetRowStatus(r, ex.Message + " This row won't be created again; remove it if it's missing.", 2);
                            if (kind == Kind.Badge) free--; // assume the free badge was used, so later costs are never under-estimated
                            unknown++;
                        }
                        else
                        {
                            SetRowStatus(r, FriendlyError(ex), 2);
                            fail++;
                        }
                    }
                    catch (IOException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    catch (UnauthorizedAccessException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    catch (Exception ex) { SetRowStatus(r, "Unexpected error: " + ex.Message, 2); if (!Done.Equals(r.Tag)) fail++; } // one bad row must not stop the rest
                    await Task.Delay(GapMs);
                }
            }
            finally
            {
                main.SetBusy(false);
                UpdateCreateText();
            }
            if (stopped) return;
            if (needsManagedOptIn) OfferManagedOptIn(u);
            main.SetStatus("Done. " + ok + " created" +
                (fail > 0 ? ", " + fail + " failed (fix those rows and click Create all again)" : "") +
                (unknown > 0 ? ", " + unknown + " unknown (no answer from Roblox; check after the reload)" : "") +
                (giftFail > 0 ? ", " + giftFail + " gift product(s) failed (see the red rows)" : "") + ".", fail + unknown + giftFail > 0);
            if (giftsMade && kind == Kind.Pass) main.MarkStale(Kind.Product); // gift products appear in the Developer Products tab
            if (ok + unknown > 0) await LoadList();
        }

        // Set when Roblox says Managed pricing hasn't been accepted for the game yet (a one-time step in the Creator Hub).
        bool needsManagedOptIn;

        string FriendlyError(ApiException ex)
        {
            if (ex.Message.IndexOf("Managed pricing must be accepted", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                needsManagedOptIn = true;
                return "Managed pricing isn't accepted for this game yet. Accept it once in the Creator Hub (Monetization > Managed Pricing), then try again.";
            }
            return ex.Message;
        }

        void OfferManagedOptIn(Universe u)
        {
            needsManagedOptIn = false;
            if (MessageBox.Show(this,
                    "Roblox won't turn on Managed pricing until it has been accepted for this game. That is a one-time step on Roblox's website and can't be done from this app.\n\n" +
                    "Open this game's Managed Pricing page in the Creator Hub now? Accept it there, then save again here.",
                    "Accept Managed pricing first", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Browser.Open("https://create.roblox.com/dashboard/creations/experiences/" + u.Id + "/monetization/managed-pricing");
        }

        bool ConfirmManagedPricing(int count)
        {
            return MessageBox.Show(this,
                "Turn on Managed pricing for " + count + " item(s)?\n\n" +
                "Managed pricing turns on regional pricing and lets Roblox run automatic price tests " +
                "(price optimization) on these items. You can review or reschedule tests in the Creator Hub under Monetization > Managed Pricing.",
                "Managed pricing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        static string Payer(Universe u) { return u.IsGroup ? "the group's funds" : "your account"; }

        // ---------- edit ----------
        ItemChanges Diff(DataGridViewRow r)
        {
            var it = (Item)r.Tag;
            var c = new ItemChanges();
            if (S(r, "name") != it.Name.Trim()) c.Name = S(r, "name");
            if (S(r, "description") != it.Description.Trim()) c.Description = S(r, "description");
            if (priced)
            {
                string p = S(r, "price"), op = it.Price.HasValue ? it.Price.Value.ToString(CultureInfo.InvariantCulture) : "";
                bool fs = B(r, "forSale");
                if (p != op || fs != it.ForSale)
                {
                    c.PriceOrSaleChanged = true;
                    c.ForSale = fs;
                    long v;
                    if (TryWhole(p, out v)) c.Price = v;
                }
                bool mg = B(r, "managed");
                if (mg != it.Managed) c.Managed = mg;
                if (S(r, "image") != "") c.ImagePath = S(r, "image");
            }
            else
            {
                bool en = B(r, "enabled");
                if (en != it.Enabled) c.Enabled = en;
            }
            return c;
        }

        void MarkChanged(DataGridViewRow r)
        {
            if (!(r.Tag is Item)) return;
            r.DefaultCellStyle.BackColor = Diff(r).Any ? ChangedColor : Color.Empty;
            UpdateSaveText();
        }

        async Task SaveAll()
        {
            var u = main.Game;
            if (u == null || main.Busy) return;
            oldGrid.EndEdit();
            var rows = oldGrid.Rows.Cast<DataGridViewRow>().Where(r => r.DefaultCellStyle.BackColor == ChangedColor).ToList();
            if (rows.Count == 0) { main.SetStatus("Nothing to save. Edit a row first."); return; }
            int bad = 0;
            foreach (var r in rows) { string v = ValidateRow(r, ""); SetRowStatus(r, v, v != "" ? 2 : 0); if (v != "") bad++; }
            if (bad > 0) { main.SetStatus("Fix the " + bad + " row(s) marked in red.", true); return; }
            int turningOn = priced ? rows.Count(r => Diff(r).Managed == true) : 0;
            if (turningOn > 0 && !ConfirmManagedPricing(turningOn)) { main.SetStatus("Cancelled. Nothing was saved."); return; }

            int ok = 0, fail = 0, regionalIgnored = 0;
            bool stopped = false;
            main.SetBusy(true);
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var it = (Item)r.Tag;
                    main.SetStatus("Saving " + (i + 1) + " of " + rows.Count + "...");
                    SetRowStatus(r, "Saving...", 0);
                    try
                    {
                        bool newImage = priced && S(r, "image") != "";
                        var changes = Diff(r);
                        await main.Api.UpdateItem(kind, u.Id, it.Id, changes);
                        bool pricingMismatch = false;
                        if (changes.Managed.HasValue)
                        {
                            // Roblox can report success without applying pricing settings, so read them back.
                            Item actual;
                            try { actual = await main.Api.ReadPricing(kind, u.Id, it.Id); }
                            catch (ApiException rex)
                            {
                                if (rex.SignedOut) throw;
                                actual = null; // saved, but couldn't double-check
                            }
                            if (actual == null) actual = new Item { Managed = changes.Managed.Value };
                            var problems = new List<string>();
                            if (changes.Managed.HasValue && actual.Managed != changes.Managed.Value)
                                problems.Add("Managed pricing " + (changes.Managed.Value ? "on" : "off") + ". The game may need the one-time Managed Pricing opt-in in the Creator Hub (Monetization > Managed Pricing)");
                            if (problems.Count > 0)
                            {
                                SetRowStatus(r, "Other changes saved, but Roblox did not turn " + string.Join(" or ", problems), 2);
                                regionalIgnored++;
                                pricingMismatch = true;
                            }
                            suppress = true;
                            r.Cells["managed"].Value = actual.Managed;
                            suppress = false;
                        }
                        if (newImage) IconRefreshed.Remove(IconKey(it)); // fetch the new icon on the next load and overwrite the cached file
                        // Remember what was saved, so the row is no longer "changed".
                        suppress = true;
                        it.Name = S(r, "name");
                        it.Description = S(r, "description");
                        if (priced)
                        {
                            long v;
                            it.Price = TryWhole(S(r, "price"), out v) ? v : (long?)null;
                            it.ForSale = B(r, "forSale");
                            it.Managed = B(r, "managed");
                            r.Cells["image"].Value = "";
                        }
                        else it.Enabled = B(r, "enabled");
                        suppress = false;
                        r.DefaultCellStyle.BackColor = Color.Empty;
                        if (!pricingMismatch) SetRowStatus(r, "Saved", 1);
                        ok++;
                    }
                    catch (ApiException ex)
                    {
                        if (ex.SignedOut) { stopped = true; main.HandleApiError(ex, ""); break; }
                        SetRowStatus(r, FriendlyError(ex), 2);
                        fail++;
                    }
                    catch (IOException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    catch (UnauthorizedAccessException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    catch (Exception ex) { SetRowStatus(r, "Unexpected error: " + ex.Message, 2); fail++; } // one bad row must not stop the rest
                    await Task.Delay(GapMs);
                }
            }
            finally
            {
                suppress = false;
                main.SetBusy(false);
                UpdateSaveText();
            }
            if (stopped) return;
            if (needsManagedOptIn) OfferManagedOptIn(u);
            if (fail > 0) { main.SetStatus(ok + " saved, " + fail + " failed. Fix the red rows and click Save changes again.", true); return; }
            if (regionalIgnored > 0) { main.SetStatus("Saved " + ok + " change(s), but Roblox ignored a pricing setting on " + regionalIgnored + " item(s). See the red rows.", true); return; }
            await LoadList(); // re-read from Roblox so the grid shows what was really saved
            main.SetStatus("Saved " + ok + " change(s).");
        }

        // ---------- list ----------
        public async Task LoadList()
        {
            var u = main.Game;
            if (u == null) return;
            int seq = ++loadSeq;
            LoadedFor = u.Id;
            suppress = true;
            oldGrid.Rows.Clear();
            suppress = false;
            countLabel.ForeColor = SystemColors.ControlText;
            countLabel.Text = "Loading...";
            UpdateSaveText();
            if (kind == Kind.Badge) { var quota = LoadQuota(u); }
            try
            {
                var items = await main.Api.ListItems(kind, u.Id);
                if (seq != loadSeq) return;
                Render(items);
                countLabel.Text = items.Count + " found.";
                var icons = LoadIcons(items, seq);
            }
            catch (ApiException ex)
            {
                if (seq != loadSeq) return;
                LoadedFor = null;
                countLabel.ForeColor = Color.Firebrick;
                countLabel.Text = "Could not load.";
                main.HandleApiError(ex, "Could not load " + Lower + ": ");
            }
        }

        string IconKey(Item it) { return kind + ":" + it.Id; }

        // Icons downloaded fresh from Roblox in this session (the rest came from the disk cache and get refreshed once).
        static readonly HashSet<string> IconRefreshed = new HashSet<string>();

        // On-disk cache file, named after the item's own ID (game pass ID, developer product ID or badge ID).
        string IconFile(Item it)
        {
            string sub = kind == Kind.Pass ? "passes" : kind == Kind.Product ? "products" : "badges";
            return Path.Combine(Settings.IconFolder, sub, it.Id + ".png");
        }

        static Image ReadImage(string path)
        {
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(path))) // read fully so the file isn't kept locked
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch (Exception) { return null; }
        }

        // Fills the Icon column: memory and disk cache first (instant), then a fresh copy from Roblox once per session,
        // which overwrites the cached file with the same name.
        async Task LoadIcons(List<Item> items, int seq)
        {
            // 1) Disk cache, read off the UI thread so big lists don't freeze the window.
            var fromDisk = items.Where(it => !IconCache.ContainsKey(IconKey(it))).Select(it => new { Key = IconKey(it), Path = IconFile(it) }).ToList();
            if (fromDisk.Count > 0)
            {
                var loaded = await Task.Run(() => fromDisk.Select(x => new { x.Key, Img = File.Exists(x.Path) ? ReadImage(x.Path) : null }).ToList());
                foreach (var x in loaded)
                {
                    if (x.Img == null) continue;
                    if (IconCache.ContainsKey(x.Key)) x.Img.Dispose(); // another load got there first
                    else IconCache[x.Key] = x.Img;
                }
            }
            if (seq != loadSeq) return;
            ShowIcons(seq);

            // 2) A fresh copy from Roblox once per session, 4 downloads at a time.
            var stale = items.Where(it => !IconRefreshed.Contains(IconKey(it)) && (kind != Kind.Product || it.IconAssetId != "" && it.IconAssetId != "0")).ToList();
            if (stale.Count == 0) return;
            Dictionary<string, string> urls;
            try { urls = await main.Api.IconUrls(kind, stale.Select(it => kind == Kind.Product ? it.IconAssetId : it.Id).Distinct().ToList()); }
            catch (ApiException) { return; } // icons are optional; cached ones stay
            var gate = new SemaphoreSlim(4);
            await Task.WhenAll(stale.Select(async it =>
            {
                string url;
                if (!urls.TryGetValue(kind == Kind.Product ? it.IconAssetId : it.Id, out url)) return;
                await gate.WaitAsync();
                try
                {
                    if (seq != loadSeq) return;
                    var img = await main.Api.DownloadImage(url, IconSize);
                    if (img == null) return;
                    SetIcon(IconKey(it), img);
                    IconRefreshed.Add(IconKey(it));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(IconFile(it)));
                        img.Save(IconFile(it), System.Drawing.Imaging.ImageFormat.Png); // overwrites any icon with the same name
                    }
                    catch (Exception) { /* cache is optional */ }
                }
                finally { gate.Release(); }
            }));
            gate.Dispose();
        }

        // Puts a new icon in the cache, points any grid cells at it, then frees the old bitmap.
        void SetIcon(string key, Image img)
        {
            Image old;
            IconCache.TryGetValue(key, out old);
            IconCache[key] = img;
            foreach (DataGridViewRow r in oldGrid.Rows)
            {
                var it = r.Tag as Item;
                if (it != null && IconKey(it) == key) r.Cells["icon"].Value = img;
                else if (old != null && ReferenceEquals(r.Cells["icon"].Value, old)) r.Cells["icon"].Value = img;
            }
            if (old != null && !ReferenceEquals(old, img)) old.Dispose();
        }

        void ShowIcons(int seq)
        {
            if (seq != loadSeq) return;
            foreach (DataGridViewRow r in oldGrid.Rows)
            {
                var it = r.Tag as Item;
                Image img;
                if (it != null && IconCache.TryGetValue(IconKey(it), out img) && !ReferenceEquals(r.Cells["icon"].Value, img)) r.Cells["icon"].Value = img;
            }
        }

        void Render(List<Item> items)
        {
            suppress = true;
            foreach (var it in items)
            {
                var r = oldGrid.Rows[oldGrid.Rows.Add()];
                r.Tag = it;
                r.Cells["id"].Value = it.Id;
                r.Cells["name"].Value = it.Name;
                r.Cells["description"].Value = it.Description;
                if (priced)
                {
                    r.Cells["price"].Value = it.Price.HasValue ? it.Price.Value.ToString(CultureInfo.InvariantCulture) : "";
                    r.Cells["forSale"].Value = it.ForSale;
                    r.Cells["managed"].Value = it.Managed;
                    r.Cells["image"].Value = "";
                }
                else r.Cells["enabled"].Value = it.Enabled;
                if (it.Locked)
                {
                    r.ReadOnly = true;
                    r.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                    SetRowStatus(r, "Read-only (managed by Roblox)", 0);
                }
            }
            suppress = false;
            ApplyFilter();
            UpdateSaveText();
        }

        void ApplyFilter()
        {
            string q = filterBox.Text.Trim().ToLowerInvariant();
            oldGrid.CurrentCell = null;
            foreach (DataGridViewRow r in oldGrid.Rows)
            {
                var it = r.Tag as Item;
                r.Visible = q == "" || it == null || it.Name.ToLowerInvariant().Contains(q) || it.Id.Contains(q);
            }
        }

        // Creates a "[GIFT] <name>" developer product for each selected existing pass/product:
        // same price, the item's current icon, regional pricing always off. Skips items that already have one.
        async Task MakeGifts()
        {
            var u = main.Game;
            if (u == null || main.Busy) return;
            var rows = oldGrid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow)
                .Concat(oldGrid.SelectedRows.Cast<DataGridViewRow>()).Distinct()
                .Where(r => r.Tag is Item).OrderBy(r => r.Index).ToList();
            if (rows.Count == 0) { main.SetStatus("Select the rows you want gift products for (click a cell, or Ctrl+click several), then click Make gift.", true); return; }
            string what = kind == Kind.Pass ? "pass" : "product";
            if (MessageBox.Show(this, "Create a \"[GIFT]\" developer product for " + rows.Count + " selected " + what + (rows.Count == 1 ? "" : "es").Replace("productes", "products") +
                    "?\n\nEach gift gets the same price and icon, and regional pricing off.", "Make gift", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            main.SetBusy(true);
            int ok = 0, skipped = 0, fail = 0;
            bool stopped = false;
            try
            {
                main.SetStatus("Checking existing gift products...");
                var existing = await main.Api.ListItems(Kind.Product, u.Id);
                var giftNames = new HashSet<string>(existing.Select(p => p.Name.Trim()), StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var it = (Item)r.Tag;
                    string giftName = "[GIFT] " + it.Name.Trim();
                    if (it.Name.StartsWith("[GIFT]", StringComparison.OrdinalIgnoreCase)) { SetRowStatus(r, "Skipped: this is already a gift product", 0); skipped++; continue; }
                    if (giftNames.Contains(giftName)) { SetRowStatus(r, "Skipped: \"" + giftName + "\" already exists", 0); skipped++; continue; }
                    main.SetStatus("Making gift " + (i + 1) + " of " + rows.Count + "...");
                    SetRowStatus(r, "Making gift...", 0);
                    string icon = null;
                    try
                    {
                        string iconId = kind == Kind.Product ? it.IconAssetId : it.Id;
                        if (iconId != "" && iconId != "0")
                        {
                            var urls = await main.Api.IconUrls(kind, new List<string> { iconId });
                            string url;
                            if (urls.TryGetValue(iconId, out url)) icon = await main.Api.DownloadToTemp(url, "gift-" + it.Id);
                        }
                        var gift = new NewItem
                        {
                            Name = giftName,
                            Description = "Gift the " + it.Name.Trim() + " " + what + " to a player",
                            ImagePath = icon ?? "",
                            Price = it.Price ?? 1,
                            ForSale = true,
                            Regional = "false", // gifts never use regional pricing
                            Managed = "false",  // Managed pricing would turn regional pricing back on
                        };
                        string giftId = await main.Api.CreateItem(Kind.Product, u, gift, 0);
                        giftNames.Add(giftName);
                        SetRowStatus(r, "Gift product created, ID " + giftId + (icon == null ? " (no icon found to copy)" : ""), 1);
                        ok++;
                    }
                    catch (ApiException ex)
                    {
                        if (ex.SignedOut) { stopped = true; main.HandleApiError(ex, ""); break; }
                        SetRowStatus(r, ex.Uncertain ? "No answer from Roblox, so the gift may or may not exist. Reload Developer Products to check before trying again." : "Gift failed: " + ex.Message, 2);
                        if (ex.Uncertain) giftNames.Add(giftName); // never make a second one in this run
                        fail++;
                    }
                    catch (Exception ex) { SetRowStatus(r, "Gift failed (unexpected error): " + ex.Message, 2); fail++; }
                    finally { if (icon != null) try { File.Delete(icon); } catch (Exception) { } }
                    await Task.Delay(400); // developer product creation is limited to 3 per second
                }
            }
            catch (ApiException ex) { stopped = true; main.HandleApiError(ex, "Could not check existing products, so no gifts were made: "); }
            finally { main.SetBusy(false); }
            if (stopped) return;
            main.SetStatus("Gifts: " + ok + " created" + (skipped > 0 ? ", " + skipped + " skipped" : "") + (fail > 0 ? ", " + fail + " failed" : "") + ".", fail > 0);
            if (ok > 0)
            {
                if (kind == Kind.Pass) main.MarkStale(Kind.Product);
                else await LoadList();
            }
        }

        // Disabled badges don't appear in Roblox's public list. Add one by ID (from the Creator Dashboard) so it can be edited here.
        async Task AddBadgeById()
        {
            var u = main.Game;
            if (u == null || main.Busy) return;
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Disabled badges don't show up in Roblox's public badge list. Enter the badge ID (from the Creator Dashboard or the badge's web address). " +
                "You can enter several, separated by commas or spaces.", "Add badge by ID", "").Trim();
            if (input == "") return;
            var ids = input.Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => new string(x.Where(char.IsDigit).ToArray())).Where(x => x != "").Distinct().ToList();
            int added = 0;
            var problems = new List<string>();
            main.SetBusy(true); // nothing else may change the lists meanwhile
            try
            {
                foreach (var id in ids)
                {
                    try { await main.Api.GetBadge(u.Id, id); main.Api.RememberBadge(u.Id, id); added++; }
                    catch (ApiException ex) { if (ex.SignedOut) { main.HandleApiError(ex, ""); return; } problems.Add(id + ": " + (ex.Status == 404 ? "not found" : ex.Message)); }
                }
            }
            finally { main.SetBusy(false); }
            if (added > 0) await LoadList();
            main.SetStatus((added > 0 ? "Added " + added + " badge(s). " : "") + (problems.Count > 0 ? "Not added: " + string.Join("; ", problems) : ""), problems.Count > 0);
        }

        async Task LoadQuota(Universe u)
        {
            quotaLabel.Text = "Checking free badges...";
            try
            {
                int n = await main.Api.FreeBadgeQuota(u.Id);
                if (main.Game == null || main.Game.Id != u.Id) return;
                quotaLabel.Text = "Free badges left today for this game: " + n + ". After that, each new badge costs 100 Robux, paid from " +
                                  Payer(u) + ". You will be asked before anything is charged.";
            }
            catch (ApiException ex) { quotaLabel.Text = "Could not check free badges: " + ex.Message; }
        }
    }
}
