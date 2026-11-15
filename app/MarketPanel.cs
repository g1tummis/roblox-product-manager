using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RobloxProductManager
{
    // "Clothing & Accessories" tab: list and edit your (or a group's) Marketplace avatar items with a Roblox API key.
    // Creating new clothing/accessories isn't possible through Roblox's public APIs; that stays in the Creator Hub.
    class MarketPanel : UserControl
    {
        static readonly Color ChangedColor = Color.FromArgb(255, 243, 176);
        const int IconSize = 40;
        static readonly string[] FilterNames = { "Classic clothing (T-shirts, shirts, pants)", "Accessories", "Layered clothing", "Everything" };
        static readonly int[][] FilterTypes =
        {
            new[] { 2, 11, 12 },
            new[] { 8, 41, 42, 43, 44, 45, 46, 47 },
            new[] { 64, 65, 66, 67, 68, 69, 70, 71, 72 },
            null, // everything = all of the above
        };
        static readonly Dictionary<int, string> TypeNames = new Dictionary<int, string>
        {
            { 2, "T-Shirt" }, { 11, "Shirt" }, { 12, "Pants" }, { 8, "Hat" }, { 41, "Hair" }, { 42, "Face" }, { 43, "Neck" },
            { 44, "Shoulder" }, { 45, "Front" }, { 46, "Back" }, { 47, "Waist" }, { 64, "T-Shirt (layered)" }, { 65, "Shirt (layered)" },
            { 66, "Pants (layered)" }, { 67, "Jacket" }, { 68, "Sweater" }, { 69, "Shorts" }, { 70, "Left shoe" }, { 71, "Right shoe" }, { 72, "Dress/skirt" },
        };
        static readonly Dictionary<string, Image> Icons = new Dictionary<string, Image>();

        readonly MainForm main;
        readonly Settings settings;
        readonly DataGridView grid;
        readonly FlowLayoutPanel bar;
        readonly ComboBox filterBox;
        readonly TextBox groupBox, searchBox;
        readonly Button loadBtn, saveBtn;
        readonly Label keyLabel, countLabel;
        bool suppress, loadedOnce;
        int loadSeq;

        public MarketPanel(MainForm main, Settings settings)
        {
            this.main = main;
            this.settings = settings;
            Dock = DockStyle.Fill;

            bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 2) };
            bar.Controls.Add(BarLabel("Show:"));
            filterBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
            filterBox.Items.AddRange(FilterNames);
            filterBox.SelectedIndex = Math.Max(0, Math.Min(FilterNames.Length - 1, settings.MarketFilter));
            bar.Controls.Add(filterBox);
            bar.Controls.Add(BarLabel("Group ID:"));
            groupBox = new TextBox { Name = "groupId", Width = 110, Text = settings.MarketGroup };
            bar.Controls.Add(groupBox);
            bar.Controls.Add(BarLabel("(empty = your own items)"));
            loadBtn = AddBtn("Load", async (s, e) => await LoadItems());
            saveBtn = AddBtn("Save changes", async (s, e) => await SaveAll());
            AddBtn("Put on sale", async (s, e) => await SetSaleSelected(true));
            AddBtn("Take off sale", async (s, e) => await SetSaleSelected(false));
            AddBtn("Set API key", (s, e) => SetKey());
            bar.Controls.Add(BarLabel("Filter:"));
            searchBox = new TextBox { Name = "search", Width = 150 };
            searchBox.TextChanged += (s, e) => ApplySearch();
            bar.Controls.Add(searchBox);
            countLabel = BarLabel("");
            bar.Controls.Add(countLabel);

            keyLabel = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 2, 0, 4),
                Text = "Uses a Roblox API key with the \"Marketplace items\" permission (Read to list, Write to edit). " +
                       "Edit cells, then click Save changes. New clothing and accessories still have to be created in the Creator Hub.",
            };

            grid = new DataGridView
            {
                Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText, EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, BackgroundColor = SystemColors.Window,
                AllowUserToResizeRows = false, RowHeadersWidth = 30,
            };
            grid.RowTemplate.Height = IconSize + 6;
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.Columns.Add(new DataGridViewImageColumn
            {
                Name = "icon", HeaderText = "Icon", Width = IconSize + 12, AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true, DefaultCellStyle = { NullValue = null },
            });
            IconPreview.Attach(grid, () => main.Api, row => { var it = row.Tag as MarketItem; return it == null ? null : Tuple.Create(Kind.Product, it.Id); });
            grid.Columns.Add(Col("id", "ID", 12, true));
            grid.Columns.Add(Col("type", "Type", 10, true));
            grid.Columns.Add(Col("name", "Name", 24, false));
            grid.Columns.Add(Col("description", "Description", 30, false));
            grid.Columns.Add(Col("price", "Price (Robux)", 9, false));
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "onSale", HeaderText = "On sale", FillWeight = 7, MinimumWidth = 50 });
            grid.Columns.Add(Col("moderation", "Moderation", 9, true));
            grid.Columns.Add(Col("status", "Status", 24, true));
            grid.CellValueChanged += (s, e) =>
            {
                if (suppress || e.RowIndex < 0) return;
                string col = grid.Columns[e.ColumnIndex].Name;
                if (col == "status" || col == "icon") return;
                MarkChanged(grid.Rows[e.RowIndex]);
            };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "id")
                    Browser.Open("https://www.roblox.com/catalog/" + grid.Rows[e.RowIndex].Cells["id"].Value);
            };

            Controls.Add(grid);
            Controls.Add(keyLabel);
            Controls.Add(bar);
            UpdateSaveText();
        }

        Button AddBtn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            bar.Controls.Add(b);
            return b;
        }

        static Label BarLabel(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(8, 8, 2, 0) }; }

        static DataGridViewTextBoxColumn Col(string name, string header, float weight, bool readOnly)
        {
            return new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };
        }

        static string S(DataGridViewRow r, string col)
        {
            var v = r.Cells[col].Value;
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
        }

        static bool B(DataGridViewRow r, string col) { var v = r.Cells[col].Value; return v is bool && (bool)v; }

        static void SetStatus(DataGridViewRow r, string text, int tone)
        {
            var c = r.Cells["status"];
            c.Value = text;
            c.ToolTipText = text;
            c.Style.ForeColor = tone == 1 ? Color.ForestGreen : tone == 2 ? Color.Firebrick : Color.Empty;
        }

        public bool HasUnsaved { get { return grid.Rows.Cast<DataGridViewRow>().Any(r => r.DefaultCellStyle.BackColor == ChangedColor); } }

        void UpdateSaveText()
        {
            int n = grid.Rows.Cast<DataGridViewRow>().Count(r => r.DefaultCellStyle.BackColor == ChangedColor);
            saveBtn.Text = n > 0 ? "Save changes (" + n + ")" : "Save changes";
        }

        public void SetBusy(bool on)
        {
            bar.Enabled = !on;
            grid.ReadOnly = on;
            if (!on) ApplyReadOnly();
        }

        // Price editing is limited to classic clothing (T-shirt, shirt, pants).
        static bool IsClassic(MarketItem it) { return it.AssetType == 2 || it.AssetType == 11 || it.AssetType == 12; }

        void ApplyReadOnly()
        {
            foreach (var name in new[] { "icon", "id", "type", "moderation", "status" }) grid.Columns[name].ReadOnly = true;
            foreach (DataGridViewRow r in grid.Rows)
            {
                var it = r.Tag as MarketItem;
                if (it == null) continue;
                if (it.CollectibleItemId == "") { r.Cells["price"].ReadOnly = true; r.Cells["onSale"].ReadOnly = true; }
                else if (!IsClassic(it))
                {
                    var c = r.Cells["price"];
                    c.ReadOnly = true;
                    c.Style.ForeColor = SystemColors.GrayText;
                    c.ToolTipText = "Prices can only be changed here for classic clothing (T-shirts, shirts, pants).";
                }
            }
        }

        // Called when the tab is shown: loads once automatically if a key is set.
        public async Task ShowTab()
        {
            if (loadedOnce || main.Busy) return;
            if (!main.Api.HasApiKey) { countLabel.Text = "Click Set API key to get started."; return; }
            await LoadItems();
        }

        void SetKey()
        {
            using (var f = new ApiKeyForm(settings.ApiKey != ""))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                settings.ApiKey = f.Key;
                try { settings.Save(); } catch (Exception) { }
                main.SetStatus(f.Key == "" ? "API key removed." : "API key saved (encrypted for your Windows account).");
                if (f.Key != "") { var t = LoadItems(); }
                else { grid.Rows.Clear(); loadedOnce = false; countLabel.Text = "Click Set API key to get started."; }
            }
        }

        IEnumerable<int> SelectedTypes()
        {
            int i = filterBox.SelectedIndex;
            return FilterTypes[i] ?? FilterTypes.Where(t => t != null).SelectMany(t => t);
        }

        string GroupId()
        {
            return new string(groupBox.Text.Where(char.IsDigit).ToArray());
        }

        async Task LoadItems()
        {
            if (main.Busy) return;
            if (HasUnsaved && !main.Ask("Throw away unsaved edits and reload?")) return;
            if (!main.Api.HasApiKey) { SetKey(); if (!main.Api.HasApiKey) return; }
            settings.MarketGroup = GroupId();
            settings.MarketFilter = filterBox.SelectedIndex;
            try { settings.Save(); } catch (Exception) { }

            int seq = ++loadSeq;
            loadedOnce = true;
            suppress = true;
            grid.Rows.Clear();
            suppress = false;
            countLabel.ForeColor = SystemColors.ControlText;
            countLabel.Text = "Loading...";
            UpdateSaveText();
            List<MarketItem> items;
            loadBtn.Enabled = false;
            try { items = await main.Api.ListMarketItems(SelectedTypes(), settings.MarketGroup); }
            catch (ApiException ex)
            {
                if (seq != loadSeq) return;
                countLabel.ForeColor = Color.Firebrick;
                countLabel.Text = "Could not load.";
                main.SetStatus("Could not load clothing and accessories: " + ex.Message, true);
                return;
            }
            finally { loadBtn.Enabled = true; }
            if (seq != loadSeq) return;

            suppress = true;
            foreach (var it in items)
            {
                var r = grid.Rows[grid.Rows.Add()];
                r.Tag = it;
                r.Cells["id"].Value = it.Id;
                string tn;
                r.Cells["type"].Value = TypeNames.TryGetValue(it.AssetType, out tn) ? tn : "Type " + it.AssetType;
                r.Cells["name"].Value = it.Name;
                r.Cells["description"].Value = it.Description;
                r.Cells["price"].Value = it.Free ? "0" : it.Price.HasValue ? it.Price.Value.ToString(CultureInfo.InvariantCulture) : "";
                r.Cells["onSale"].Value = it.OnSale;
                r.Cells["moderation"].Value = it.Moderation;
                if (it.CollectibleItemId == "") SetStatus(r, "Price and sale can't be changed here for this item", 0);
            }
            suppress = false;
            ApplyReadOnly();
            ApplySearch();
            countLabel.Text = items.Count + " found.";
            main.SetStatus(items.Count + " clothing and accessory item(s) loaded" + (settings.MarketGroup != "" ? " for group " + settings.MarketGroup : "") + ".");
            var icons = LoadIcons(items, seq);
        }

        async Task LoadIcons(List<MarketItem> items, int seq)
        {
            var missing = items.Where(i => !Icons.ContainsKey(i.Id)).Select(i => i.Id).Distinct().ToList();
            ShowIcons();
            if (missing.Count == 0) return;
            Dictionary<string, string> urls;
            try { urls = await main.Api.IconUrls(Kind.Product, missing); } // asset thumbnails
            catch (ApiException) { return; }
            var gate = new SemaphoreSlim(4);
            await Task.WhenAll(missing.Select(async id =>
            {
                string url;
                if (!urls.TryGetValue(id, out url)) return;
                await gate.WaitAsync();
                try
                {
                    if (seq != loadSeq || Icons.ContainsKey(id)) return;
                    var img = await main.Api.DownloadImage(url, IconSize);
                    if (img == null) return;
                    if (Icons.ContainsKey(id)) { img.Dispose(); return; }
                    Icons[id] = img;
                    ShowIcons();
                }
                finally { gate.Release(); }
            }));
            gate.Dispose();
        }

        void ShowIcons()
        {
            foreach (DataGridViewRow r in grid.Rows)
            {
                var it = r.Tag as MarketItem;
                Image img;
                if (it != null && r.Cells["icon"].Value == null && Icons.TryGetValue(it.Id, out img)) r.Cells["icon"].Value = img;
            }
        }

        void ApplySearch()
        {
            string q = searchBox.Text.Trim().ToLowerInvariant();
            grid.CurrentCell = null;
            foreach (DataGridViewRow r in grid.Rows)
            {
                var it = r.Tag as MarketItem;
                r.Visible = q == "" || it == null || it.Name.ToLowerInvariant().Contains(q) || it.Id.Contains(q);
            }
        }

        void MarkChanged(DataGridViewRow r)
        {
            var it = r.Tag as MarketItem;
            if (it == null) return;
            bool changed = S(r, "name") != it.Name.Trim() || S(r, "description") != it.Description.Trim() || PriceChanged(r, it) || B(r, "onSale") != it.OnSale;
            r.DefaultCellStyle.BackColor = changed ? ChangedColor : Color.Empty;
            UpdateSaveText();
        }

        static bool PriceChanged(DataGridViewRow r, MarketItem it)
        {
            string old = it.Free ? "0" : it.Price.HasValue ? it.Price.Value.ToString(CultureInfo.InvariantCulture) : "";
            return S(r, "price") != old;
        }

        // Ticks/unticks On sale for the selected rows and saves right away.
        async Task SetSaleSelected(bool on)
        {
            if (main.Busy) return;
            grid.EndEdit();
            var picked = grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).Concat(grid.SelectedRows.Cast<DataGridViewRow>())
                .Where(r => r.Visible && r.Tag is MarketItem).Distinct().ToList();
            if (picked.Count == 0) { main.SetStatus("Select one or more rows first (click a row; hold Ctrl or Shift for several).", true); return; }
            var usable = picked.Where(r => ((MarketItem)r.Tag).CollectibleItemId != "").ToList();
            int skipped = picked.Count - usable.Count;
            if (usable.Count == 0) { main.SetStatus("Roblox doesn't allow changing the sale status of the selected item(s) here.", true); return; }
            var changing = usable.Where(r => B(r, "onSale") != on).ToList();
            if (changing.Count == 0) { main.SetStatus("The selected item(s) are already " + (on ? "on sale." : "off sale.")); return; }
            if (on && !main.Ask("Put " + changing.Count + " item(s) on sale? Players will be able to buy them at the price shown.")) return;
            foreach (var r in changing) r.Cells["onSale"].Value = on;
            if (skipped > 0) main.SetStatus(skipped + " selected item(s) can't change sale status here and were skipped.");
            await SaveAll();
        }

        async Task SaveAll()
        {
            if (main.Busy) return;
            grid.EndEdit();
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => r.DefaultCellStyle.BackColor == ChangedColor).ToList();
            if (rows.Count == 0) { main.SetStatus("Nothing to save. Edit a row first."); return; }
            int bad = 0;
            foreach (var r in rows)
            {
                string v = "";
                long p;
                if (S(r, "name") == "") v = "Name is required.";
                else if (PriceChanged(r, (MarketItem)r.Tag) && !IsClassic((MarketItem)r.Tag)) v = "Prices can only be changed for classic clothing.";
                else if (B(r, "onSale") && !(long.TryParse(S(r, "price"), NumberStyles.None, CultureInfo.InvariantCulture, out p))) v = "Enter a whole-number price (0 = free), or untick On sale.";
                SetStatus(r, v, v != "" ? 2 : 0);
                if (v != "") bad++;
            }
            if (bad > 0) { main.SetStatus("Fix the " + bad + " row(s) marked in red.", true); return; }

            int ok = 0, fail = 0;
            main.SetBusy(true);
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var it = (MarketItem)r.Tag;
                    main.SetStatus("Saving " + (i + 1) + " of " + rows.Count + "...");
                    SetStatus(r, "Saving...", 0);
                    var done = new List<string>();
                    string step = "";
                    try
                    {
                        string name = S(r, "name"), desc = S(r, "description");
                        bool nameChanged = name != it.Name.Trim(), descChanged = desc != it.Description.Trim();
                        if (nameChanged || descChanged)
                        {
                            step = "Name/description";
                            await main.Api.UpdateMarketDisplay(it.Id, name, desc);
                            it.Name = name; it.Description = desc;
                            done.Add("name/description");
                        }
                        bool sale = B(r, "onSale");
                        if (PriceChanged(r, it) || sale != it.OnSale)
                        {
                            long p;
                            long? price = long.TryParse(S(r, "price"), NumberStyles.None, CultureInfo.InvariantCulture, out p) ? p : (long?)null;
                            step = "Price/sale";
                            if (!price.HasValue) price = it.Free ? 0 : it.Price;
                            await Task.Delay(1100); // this endpoint allows 1 request per second
                            await main.Api.UpdateMarketSale(it.CollectibleItemId, sale, price, it.Resellable);
                            it.OnSale = sale; it.Price = price; it.Free = sale && price == 0;
                            done.Add("price/sale");
                        }
                        r.DefaultCellStyle.BackColor = Color.Empty;
                        SetStatus(r, "Saved " + string.Join(" and ", done), 1);
                        ok++;
                    }
                    catch (ApiException ex)
                    {
                        SetStatus(r, (done.Count > 0 ? "Saved " + string.Join(" and ", done) + ". " : "") + step + " failed: " + ex.Message, 2);
                        fail++;
                    }
                    catch (Exception ex) { SetStatus(r, "Unexpected error: " + ex.Message, 2); fail++; }
                }
            }
            finally { main.SetBusy(false); UpdateSaveText(); }
            if (fail > 0) { main.SetStatus(ok + " saved, " + fail + " failed. See the red rows.", true); return; }
            main.SetStatus("Saved " + ok + " item(s). Reloading to show what Roblox has...");
            suppress = true; foreach (var r in rows) r.DefaultCellStyle.BackColor = Color.Empty; suppress = false;
            await LoadItems();
        }
    }

    // Small window for entering (or removing) the Roblox API key.
    class ApiKeyForm : Form
    {
        readonly TextBox keyBox;
        public string Key { get; private set; }

        public ApiKeyForm(bool hasKey)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "Roblox API key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            t.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 8),
                Text = "Create a key in the Creator Dashboard under API Keys, using your own account (not the group). " +
                       "Add the \"Marketplace items\" permission with Read (to list) and Write (to edit). Don't add Publish. " +
                       "For group items, your group role must be allowed to manage the group's items. " +
                       "The key is stored encrypted, readable only by your Windows account.",
            });
            var link = new LinkLabel { Text = "Open the Roblox API Keys page", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            link.LinkClicked += (o, e) => Browser.Open("https://create.roblox.com/dashboard/credentials?activeTab=ApiKeysTab");
            t.Controls.Add(link);
            keyBox = new TextBox { Width = 500, UseSystemPasswordChar = true };
            t.Controls.Add(keyBox);
            if (hasKey) t.Controls.Add(new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "A key is already saved. Paste a new one to replace it, or click Remove key." });

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += (o, e) =>
            {
                string k = keyBox.Text.Trim();
                if (k == "") { MessageBox.Show(this, "Paste the API key first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (k.Any(char.IsWhiteSpace)) { MessageBox.Show(this, "An API key has no spaces. Paste it again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Key = k;
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            if (hasKey)
            {
                var remove = new Button { Text = "Remove key", AutoSize = true };
                remove.Click += (o, e) => { Key = ""; DialogResult = DialogResult.OK; };
                buttons.Controls.Add(remove);
            }
            t.Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;
            Controls.Add(t);
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
