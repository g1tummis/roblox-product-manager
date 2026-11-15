using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RobloxProductManager
{
    // One tab: "Create new" grid on top, "Existing" grid below, for game passes, developer products or badges.
    class KindPanel : UserControl
    {
        static readonly Color ChangedColor = Color.FromArgb(255, 243, 176);
        const string ImageFilter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";
        const string Done = "done";

        readonly MainForm main;
        readonly Kind kind;
        readonly bool priced;
        readonly DataGridView newGrid, oldGrid;
        readonly FlowLayoutPanel createBar, editBar;
        readonly Button createBtn, saveBtn;
        readonly TextBox sharedImage, filterBox;
        readonly ComboBox regionalBox;
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
            newGrid.Columns.Add(TextCol("image", "Image file", 22, false));
            newGrid.Columns.Add(ButtonCol());
            newGrid.Columns.Add(TextCol("status", "Status", 24, true));
            newGrid.DefaultValuesNeeded += (s, e) => { e.Row.Cells[priced ? "forSale" : "active"].Value = true; };
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
            AddButton(createBar, "Paste rows", (s, e) => PasteRows());
            AddButton(createBar, "Remove created rows", (s, e) => RemoveDone());
            createBar.Controls.Add(BarLabel("Image for rows without one:"));
            sharedImage = new TextBox { Width = 200 };
            createBar.Controls.Add(sharedImage);
            AddButton(createBar, "...", (s, e) => { var p = PickImage(); if (p != null) sharedImage.Text = p; });
            if (priced)
            {
                createBar.Controls.Add(BarLabel("Regional pricing:"));
                regionalBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
                regionalBox.Items.AddRange(new object[] { "Roblox default", "On", "Off" });
                regionalBox.SelectedIndex = 0;
                createBar.Controls.Add(regionalBox);
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
            oldGrid.Columns.Add(TextCol("id", "ID", 12, true));
            oldGrid.Columns.Add(TextCol("name", "Name", 26, false));
            if (priced) oldGrid.Columns.Add(TextCol("price", "Price (Robux)", 10, false));
            oldGrid.Columns.Add(TextCol("description", "Description", 36, false));
            if (priced)
            {
                oldGrid.Columns.Add(CheckCol("forSale", "For sale"));
                oldGrid.Columns.Add(CheckCol("regional", "Regional pricing"));
                oldGrid.Columns.Add(TextCol("image", "New image", 18, false));
                oldGrid.Columns.Add(ButtonCol());
            }
            else oldGrid.Columns.Add(CheckCol("enabled", "Enabled"));
            oldGrid.Columns.Add(TextCol("status", "Status", 22, true));
            oldGrid.CellValueChanged += (s, e) =>
            {
                if (!suppress && e.RowIndex >= 0 && oldGrid.Columns[e.ColumnIndex].Name != "status") MarkChanged(oldGrid.Rows[e.RowIndex]);
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

        bool IsBlank(DataGridViewRow r) { return S(r, "name") == "" && S(r, "description") == "" && S(r, "image") == "" && S(r, "price") == ""; }

        IEnumerable<DataGridViewRow> PendingRows()
        {
            return newGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && !Done.Equals(r.Tag) && !IsBlank(r));
        }

        void UpdateCreateText()
        {
            int n = PendingRows().Count();
            createBtn.Text = n > 0 ? "Create all (" + n + ")" : "Create all";
        }

        void UpdateSaveText()
        {
            int n = oldGrid.Rows.Cast<DataGridViewRow>().Count(r => r.DefaultCellStyle.BackColor == ChangedColor);
            saveBtn.Text = n > 0 ? "Save changes (" + n + ")" : "Save changes";
        }

        public bool HasUnsaved { get { return oldGrid.Rows.Cast<DataGridViewRow>().Any(r => r.DefaultCellStyle.BackColor == ChangedColor); } }

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
                r.Cells["price"].Value = has ? digits.TrimStart('0') : "";
                r.Cells["forSale"].Value = has || forSaleWithoutPrice;
            }
            else r.Cells["active"].Value = true;
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
            foreach (var raw in ClipboardText().Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim() == "") continue;
                var p = (raw.Contains("\t") ? raw.Split('\t') : raw.Split('|')).Select(x => x.Trim()).ToArray();
                if (added == 0 && p[0].Equals("name", StringComparison.OrdinalIgnoreCase)) continue; // header row
                if (priced) AddNewRow(p[0], p.Length > 1 ? p[1] : "", string.Join(" ", p.Skip(2)), "", false);
                else AddNewRow(p[0], "", string.Join(" ", p.Skip(1)), "", false);
                added++;
            }
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
            foreach (var f in files) AddNewRow(Path.GetFileNameWithoutExtension(f), "", "", f, true);
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
        async Task CreateAll()
        {
            var u = main.Game;
            if (u == null || main.Busy) return;
            newGrid.EndEdit();
            string shared = sharedImage.Text.Trim();
            if (shared != "" && !File.Exists(shared)) { main.SetStatus("The image for rows without one was not found: " + shared, true); return; }
            var rows = PendingRows().ToList();
            if (rows.Count == 0) { main.SetStatus("Add at least one row first.", true); return; }
            int bad = 0;
            foreach (var r in rows) { string v = ValidateRow(r, shared); SetRowStatus(r, v, v != "" ? 2 : 0); if (v != "") bad++; }
            if (bad > 0) { main.SetStatus("Fix the " + bad + " row(s) marked in red.", true); return; }

            int free = int.MaxValue;
            if (kind == Kind.Badge)
            {
                main.SetStatus("Checking free badges...");
                try { free = await main.Api.FreeBadgeQuota(u.Id); }
                catch (ApiException ex) { main.HandleApiError(ex, "Could not check free badges, so nothing was created: "); return; }
                int paid = Math.Max(0, rows.Count - free);
                if (paid > 0 && MessageBox.Show(this,
                        "This game has " + free + " free badge(s) left today.\n\n" + paid + " badge(s) will cost 100 Robux each (" + (paid * 100) +
                        " Robux total), paid from " + Payer(u) + ".\n\nCreate them?",
                        "Robux will be spent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    main.SetStatus("Cancelled. Nothing was created.");
                    return;
                }
            }

            string regional = regionalBox == null || regionalBox.SelectedIndex == 0 ? "" : regionalBox.SelectedIndex == 1 ? "true" : "false";
            int ok = 0, fail = 0;
            bool stopped = false;
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
                        Regional = regional,
                        ForSale = B(r, "forSale"),
                        Active = B(r, "active"),
                    };
                    if (priced && TryWhole(S(r, "price"), out price)) n.Price = price;
                    try
                    {
                        string id = await main.Api.CreateItem(kind, u, n, free > 0 ? 0 : 100);
                        r.Tag = Done;
                        r.ReadOnly = true;
                        r.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                        SetRowStatus(r, "Created" + (id != "" ? ", ID " + id : ""), 1);
                        ok++;
                        if (kind == Kind.Badge) free--;
                    }
                    catch (ApiException ex)
                    {
                        if (ex.SignedOut) { stopped = true; main.HandleApiError(ex, ""); break; }
                        SetRowStatus(r, ex.Message, 2);
                        fail++;
                    }
                    catch (IOException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    catch (UnauthorizedAccessException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    await Task.Delay(GapMs);
                }
            }
            finally
            {
                main.SetBusy(false);
                UpdateCreateText();
            }
            if (stopped) return;
            main.SetStatus("Done. " + ok + " created" + (fail > 0 ? ", " + fail + " failed. Fix those rows and click Create all again." : "."), fail > 0);
            if (ok > 0) await LoadList();
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
                bool rg = B(r, "regional");
                if (rg != it.Regional) c.Regional = rg;
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

            int ok = 0, fail = 0;
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
                        await main.Api.UpdateItem(kind, u.Id, it.Id, Diff(r));
                        // Remember what was saved, so the row is no longer "changed".
                        suppress = true;
                        it.Name = S(r, "name");
                        it.Description = S(r, "description");
                        if (priced)
                        {
                            long v;
                            it.Price = TryWhole(S(r, "price"), out v) ? v : (long?)null;
                            it.ForSale = B(r, "forSale");
                            it.Regional = B(r, "regional");
                            r.Cells["image"].Value = "";
                        }
                        else it.Enabled = B(r, "enabled");
                        suppress = false;
                        r.DefaultCellStyle.BackColor = Color.Empty;
                        SetRowStatus(r, "Saved", 1);
                        ok++;
                    }
                    catch (ApiException ex)
                    {
                        if (ex.SignedOut) { stopped = true; main.HandleApiError(ex, ""); break; }
                        SetRowStatus(r, ex.Message, 2);
                        fail++;
                    }
                    catch (IOException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
                    catch (UnauthorizedAccessException ex) { SetRowStatus(r, "Could not read the image: " + ex.Message, 2); fail++; }
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
            if (fail > 0) { main.SetStatus(ok + " saved, " + fail + " failed. Fix the red rows and click Save changes again.", true); return; }
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
                    r.Cells["regional"].Value = it.Regional;
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
