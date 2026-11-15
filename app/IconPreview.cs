using System;
using System.Drawing;
using System.Windows.Forms;

namespace RobloxProductManager
{
    // A larger, higher-quality version of an icon, shown next to the cursor while hovering an Icon cell.
    // The big image is only downloaded on hover (nothing is preloaded), and only the last one is kept.
    class IconPreview : Form
    {
        const int LogicalSize = 300, HoverDelayMs = 350;
        static IconPreview shared;

        readonly PictureBox pic;
        readonly Label note;
        readonly Timer timer;
        RobloxClient api;
        Kind pendingKind;
        string pendingId, cachedKey, shownKey;
        Image cached;
        int seq;

        // target returns the thumbnail to show for a row (kind + id), or null when the row has no icon.
        public static void Attach(DataGridView grid, Func<RobloxClient> api, Func<DataGridViewRow, Tuple<Kind, string>> target)
        {
            if (shared == null || shared.IsDisposed) shared = new IconPreview();
            var p = shared;
            grid.CellMouseEnter += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "icon") return;
                var row = grid.Rows[e.RowIndex];
                if (row.Cells[e.ColumnIndex].Value == null) return; // no icon yet
                var t = target(row);
                if (t == null || string.IsNullOrEmpty(t.Item2) || t.Item2 == "0") return;
                var client = api();
                if (client != null) p.Arm(client, t.Item1, t.Item2);
            };
            grid.CellMouseLeave += (s, e) => p.Disarm();
            grid.MouseLeave += (s, e) => p.Disarm();
            grid.MouseDown += (s, e) => p.Disarm();
            grid.Scroll += (s, e) => p.Disarm();
            grid.Disposed += (s, e) => p.Disarm();
        }

        IconPreview()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = SystemColors.Window;
            Padding = new Padding(1);
            int px;
            using (var g = CreateGraphics()) px = (int)Math.Round(LogicalSize * g.DpiX / 96f);
            ClientSize = new Size(px + 2, px + 2);
            pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Window };
            note = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText, Text = "Loading..." };
            Controls.Add(pic);
            Controls.Add(note);
            timer = new Timer { Interval = HoverDelayMs };
            timer.Tick += async (s, e) => { timer.Stop(); await Open(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never steals focus
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, Width - 1, Height - 1);
        }

        void Arm(RobloxClient client, Kind kind, string id)
        {
            api = client;
            pendingKind = kind;
            pendingId = id;
            timer.Stop();
            timer.Start();
        }

        void Disarm()
        {
            timer.Stop();
            pendingId = null;
            shownKey = null;
            seq++;
            if (Visible) Hide();
        }

        async System.Threading.Tasks.Task Open()
        {
            if (pendingId == null || api == null) return;
            string key = pendingKind + ":" + pendingId;
            Kind kind = pendingKind;
            string id = pendingId;
            int my = ++seq;
            shownKey = key;
            PlaceNearCursor();
            if (cachedKey == key && cached != null) { ShowImage(cached); }
            else
            {
                pic.Image = null;
                pic.Visible = false;
                note.Text = "Loading...";
                note.Visible = true;
            }
            if (!Visible) Show();
            if (cachedKey == key && cached != null) return;

            Image img = null;
            try
            {
                string url = await api.LargeIconUrl(kind, id);
                if (url != "") img = await api.DownloadImage(url, ClientSize.Width - 2);
            }
            catch (ApiException) { }
            if (img == null)
            {
                if (my == seq) note.Text = "No larger image available.";
                return;
            }
            // Keep only the newest big image in memory.
            var old = cached;
            cached = img;
            cachedKey = key;
            if (my == seq && shownKey == key) ShowImage(img);
            if (old != null && !ReferenceEquals(old, pic.Image)) old.Dispose();
        }

        void ShowImage(Image img)
        {
            pic.Image = img;
            pic.Visible = true;
            note.Visible = false;
        }

        void PlaceNearCursor()
        {
            var c = Cursor.Position;
            var area = Screen.FromPoint(c).WorkingArea;
            int x = c.X + 18, y = c.Y + 18;
            if (x + Width > area.Right) x = c.X - Width - 8;
            if (y + Height > area.Bottom) y = area.Bottom - Height;
            Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (cached != null) cached.Dispose();
                cached = null;
            }
            base.Dispose(disposing);
        }
    }
}
