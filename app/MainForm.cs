using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RobloxProductManager
{
    class MainForm : Form
    {
        readonly Settings settings;
        readonly RobloxClient api;
        readonly List<KindPanel> panels = new List<KindPanel>();
        MarketPanel market; // "Clothing & Accessories" tab (last tab; uses an API key, not the selected game)
        Panel signedOutPanel, mainPanel, loadingPanel;
        Label loadingLabel;
        Button signInBtn;
        Label signedOutMsg, waitingLabel, whoLabel, noGamesLabel;
        LinkLabel settingsLink;
        ComboBox gameBox;
        PictureBox headshot;
        TableLayoutPanel topBar;
        TabControl tabs;
        ToolStripStatusLabel statusLabel;
        ToolStripProgressBar progress;
        ToolStripButton cancelSignIn;
        CancellationTokenSource signInCts;
        string currentGameId;
        bool suppressGameChange;

        public bool Busy { get; private set; }
        public RobloxClient Api { get { return api; } }
        public Universe Game { get { return gameBox.SelectedItem as Universe; } }

        public MainForm()
        {
            settings = Settings.Load();
            api = new RobloxClient(settings);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "Roblox Product Manager";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 820);
            MinimumSize = new Size(900, 600);
#if TESTHOOKS
            // Test builds open off-screen so automated UI tests don't get in the way.
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-5000, 0);
            ShowInTaskbar = false;
            Text = "Roblox Product Manager (TEST BUILD)";
#endif

            BuildSignedOutPanel();
            BuildLoadingPanel();
            BuildMainPanel();
            var strip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false };
            cancelSignIn = new ToolStripButton { Text = "Cancel sign-in", Visible = false };
            cancelSignIn.Click += (s, e) => { if (signInCts != null) signInCts.Cancel(); };
            strip.Items.AddRange(new ToolStripItem[] { statusLabel, progress, cancelSignIn });

            Controls.Add(mainPanel);
            Controls.Add(signedOutPanel);
            Controls.Add(loadingPanel);
            Controls.Add(strip);
            ResumeLayout(false);
            PerformLayout();

            ShowLoading("Starting...");
            Shown += async (s, e) => await Startup();
            FormClosing += OnFormClosing;
        }

        // ---------- layout ----------
        void BuildSignedOutPanel()
        {
            signedOutPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 20, 28, 20) };
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };

            var title = new Label { Text = "Roblox Product Manager", AutoSize = true, Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
            var intro = Para("Create and edit game passes, developer products and badges for your Roblox experiences, many at a time.");
            signedOutMsg = Para("");
            signedOutMsg.ForeColor = Color.Firebrick;
            signInBtn = new Button
            {
                Text = "Sign in with Roblox",
                AutoSize = true,
                Padding = new Padding(14, 6, 14, 6),
                Font = new Font(Font.FontFamily, 11F),
                Margin = new Padding(0, 12, 0, 6),
            };
            signInBtn.Click += async (s, e) => await SignIn(false);
            waitingLabel = Para("");
            waitingLabel.Visible = false;
            var howTitle = new Label { Text = "How signing in works", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 18, 0, 4) };
            var steps = Para(string.Join("\n", new[]
            {
                "1. Click Sign in with Roblox. Your web browser opens Roblox's own page (apis.roblox.com). This app never sees your password.",
                "2. Log in to Roblox there if it asks. You need a 13+ account.",
                "3. Roblox shows what this app wants to do: view and edit game passes and developer products, and create and edit badges. " +
                    "Creating a badge can spend Robux once a game's free daily badges are used up. This app always asks you first.",
                "4. Choose the experiences this app may manage: your own, or group experiences your role is allowed to manage.",
                "5. Confirm, then come back to this window. You'll be signed in.",
                "",
                "You stay signed in next time you open the app. Sign out removes this app's access on Roblox.",
            }));
            settingsLink = new LinkLabel { Text = "Advanced settings (only needed if you run your own Roblox OAuth app)", AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
            settingsLink.LinkClicked += (s, e) => OpenSettings();

            flow.Controls.AddRange(new Control[] { title, intro, signedOutMsg, signInBtn, waitingLabel, howTitle, steps, settingsLink });
            signedOutPanel.Controls.Add(flow);
        }

        Label Para(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 2, 0, 2) };
        }

        void BuildMainPanel()
        {
            mainPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 4) };

            topBar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1 };
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            gameBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470, Margin = new Padding(3, 4, 3, 3) };
            gameBox.SelectedIndexChanged += (s, e) => OnGameChanged(false);
            left.Controls.Add(new Label { Text = "Game:", AutoSize = true, Margin = new Padding(0, 8, 2, 0) });
            left.Controls.Add(gameBox);
            left.Controls.Add(MakeButton("Refresh list", async (s, e) => await ReloadGames()));
            left.Controls.Add(MakeButton("Add or remove games", async (s, e) => await SignIn(true)));
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(0) };
            headshot = new PictureBox { Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 6, 0) };
            right.Controls.Add(headshot);
            whoLabel = new Label { AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
            right.Controls.Add(whoLabel);
            right.Controls.Add(MakeButton("Settings", (s, e) => OpenSettings()));
            right.Controls.Add(MakeButton("Sign out", async (s, e) => await SignOut()));
            topBar.Controls.Add(left, 0, 0);
            topBar.Controls.Add(right, 1, 0);

            noGamesLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(4, 12, 0, 0),
                Visible = false,
                Text = "No games are shared with this app yet. Click \"Add or remove games\" and pick experiences on the Roblox page.",
            };

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            foreach (var k in new[] { Kind.Pass, Kind.Product, Kind.Badge })
            {
                var page = new TabPage(KindPanel.PluralOf(k)) { Padding = new Padding(4), UseVisualStyleBackColor = true };
                var p = new KindPanel(this, k);
                page.Controls.Add(p);
                panels.Add(p);
                tabs.TabPages.Add(page);
            }
            var marketPage = new TabPage("Clothing and Accessories") /* "&" throws off the tab's padding */ { Padding = new Padding(4), UseVisualStyleBackColor = true };
            market = new MarketPanel(this, settings);
            marketPage.Controls.Add(market);
            tabs.TabPages.Add(marketPage);
            tabs.SelectedIndexChanged += async (s, e) => await EnsureLoaded();

            mainPanel.Controls.Add(tabs);
            mainPanel.Controls.Add(noGamesLabel);
            mainPanel.Controls.Add(topBar);
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
            b.Click += click;
            return b;
        }

        // ---------- status ----------
        public void SetStatus(string text, bool error = false)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        public void SetBusy(bool on)
        {
            Busy = on;
            topBar.Enabled = !on;
            settingsLink.Enabled = !on;
            foreach (var p in panels) p.SetBusy(on);
            market.SetBusy(on);
            progress.Visible = on;
        }

        public bool Ask(string question)
        {
            return MessageBox.Show(this, question, "Roblox Product Manager", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        public void HandleApiError(ApiException ex, string prefix)
        {
            if (ex.SignedOut) ShowSignedOut(ex.Message);
            else SetStatus(prefix + ex.Message, true);
        }

        bool HasUnsaved() { return panels.Any(p => p.HasUnsaved) || market.HasUnsaved; }

        // Makes a tab reload its list the next time it is shown (panels are in Kind order).
        public void MarkStale(Kind k) { panels[(int)k].LoadedFor = null; }

        // ---------- views ----------
        void BuildLoadingPanel()
        {
            loadingPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            var title = new Label
            {
                Text = "Roblox Product Manager", Dock = DockStyle.Top, Height = 120, TextAlign = ContentAlignment.BottomCenter,
                Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            };
            loadingLabel = new Label { Text = "Loading...", Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 240, Height = 14 };
            var barHolder = new Panel { Dock = DockStyle.Top, Height = 30 };
            barHolder.Controls.Add(bar);
            barHolder.Resize += (s, e) => bar.Left = Math.Max(0, (barHolder.Width - bar.Width) / 2);
            loadingPanel.Controls.Add(barHolder);
            loadingPanel.Controls.Add(loadingLabel);
            loadingPanel.Controls.Add(title);
        }

        // Shown at startup until the app knows whether the user is signed in.
        void ShowLoading(string text)
        {
            loadingLabel.Text = text;
            signedOutPanel.Visible = false;
            mainPanel.Visible = false;
            loadingPanel.Visible = true;
            SetStatus(text);
        }

        void ShowSignedOut(string message)
        {
            loadingPanel.Visible = false;
            mainPanel.Visible = false;
            signedOutPanel.Visible = true;
            signedOutMsg.Text = message;
            signedOutMsg.Visible = message != "";
            SetStatus(message != "" ? message : (settings.Complete ? "Not signed in." : "Enter your app settings, then sign in."), message != "");
        }

        void ShowMain()
        {
            loadingPanel.Visible = false;
            signedOutPanel.Visible = false;
            mainPanel.Visible = true;
            whoLabel.Text = api.User != null ? "Signed in as " + api.User.Display : "";
            var load = LoadHeadshot();
        }

        string headshotFor;

        async Task LoadHeadshot()
        {
            if (api.User == null || api.User.Id == "" || headshotFor == api.User.Id) return;
            string userId = api.User.Id;
            headshotFor = userId;
            var old = headshot.Image; headshot.Image = null; if (old != null) old.Dispose();
            try
            {
                string url = await api.HeadshotUrl(userId);
                if (url == "") return;
                var img = await api.DownloadImage(url, 32);
                if (img == null) return;
                if (api.User != null && api.User.Id == userId) { var prev = headshot.Image; headshot.Image = img; if (prev != null) prev.Dispose(); }
                else img.Dispose(); // user changed while downloading
            }
            catch (ApiException) { headshotFor = null; /* the picture is optional */ }
        }

        void SetSigningIn(bool on, string text)
        {
            signInBtn.Enabled = !on;
            settingsLink.Enabled = !on;
            topBar.Enabled = !on;
            foreach (var p in panels) p.Enabled = !on;
            market.Enabled = !on;
            waitingLabel.Text = text;
            waitingLabel.Visible = on && text != "";
            cancelSignIn.Visible = on && signInCts != null;
            progress.Visible = on || Busy;
            if (on) SetStatus(text);
        }

        void BringToFrontNow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;
            TopMost = false;
            Activate();
        }

        // ---------- flows ----------
        async Task Startup()
        {
            if (!settings.Complete)
            {
                ShowSignedOut("");
                if (OpenSetup(true)) SetStatus("Settings saved. Click Sign in with Roblox.");
                return;
            }
            if (settings.RefreshToken == "") { ShowSignedOut(""); return; } // nothing saved: sign-in is needed
            ShowLoading("Signing you back in...");
            try
            {
                if (await api.TryRestore()) { ShowMain(); await ReloadGames(); }
                else ShowSignedOut("");
            }
            catch (ApiException ex) { ShowSignedOut(ex.Message + " Click Sign in with Roblox to try again."); }
            finally { progress.Visible = Busy; }
        }

        async Task SignIn(bool chooseGames)
        {
            if (Busy || signInCts != null) return;
            if (!settings.Complete && !OpenSetup(true)) return;
            if (chooseGames && panels.Any(p => p.HasUnsaved) && !Ask("You have unsaved edits. Continue and throw them away?")) return;
            bool fromMain = mainPanel.Visible;
            signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            SetSigningIn(true, "Waiting for you to approve on Roblox in your web browser. Come back here when you're done.\n" +
                "If Roblox says \"Redirect URI is invalid\", the app's owner must add " + settings.RedirectUri + " to the Roblox OAuth app.");
            try
            {
                await api.SignIn(chooseGames, signInCts.Token);
                BringToFrontNow();
                ShowMain();
                await ReloadGames();
            }
            catch (OperationCanceledException)
            {
                if (fromMain) SetStatus("Cancelled or timed out. Nothing changed.", true);
                else ShowSignedOut("Sign-in was cancelled or timed out. Click Sign in with Roblox to try again.");
            }
            catch (ApiException ex)
            {
                if (fromMain && !ex.SignedOut) SetStatus(ex.Message, true);
                else ShowSignedOut(ex.Message);
            }
            finally
            {
                var cts = signInCts;
                signInCts = null;
                if (cts != null) cts.Dispose();
                SetSigningIn(false, "");
            }
        }

        async Task ReloadGames()
        {
            SetStatus("Loading your games...");
            List<Universe> list;
            try { list = await api.LoadUniverses(); }
            catch (ApiException ex) { HandleApiError(ex, "Could not load your games: "); return; }

            string keep = currentGameId ?? settings.LastUniverse;
            suppressGameChange = true;
            gameBox.BeginUpdate();
            gameBox.Items.Clear();
            foreach (var u in list) gameBox.Items.Add(u);
            gameBox.EndUpdate();
            int idx = list.FindIndex(u => u.Id == keep);
            gameBox.SelectedIndex = idx >= 0 ? idx : (list.Count > 0 ? 0 : -1);
            suppressGameChange = false;

            noGamesLabel.Visible = list.Count == 0;
            tabs.Visible = true; // the Clothing & Accessories tab works without any game
            OnGameChanged(true);
            SetStatus(list.Count == 0 ? "No games are shared with this app yet." : list.Count + (list.Count == 1 ? " game" : " games") + " available.");
        }

        void OnGameChanged(bool fromReload)
        {
            if (suppressGameChange) return;
            var g = Game;
            string id = g != null ? g.Id : null;
            if (id == currentGameId) return;
            if (!fromReload && panels.Any(p => p.HasUnsaved) && !Ask("You have unsaved edits. Switch game and throw them away?"))
            {
                suppressGameChange = true;
                gameBox.SelectedIndex = gameBox.Items.Cast<Universe>().ToList().FindIndex(u => u.Id == currentGameId);
                suppressGameChange = false;
                return;
            }
            currentGameId = id;
            if (g != null)
            {
                settings.LastUniverse = g.Id;
                try { settings.Save(); } catch (Exception) { }
            }
            foreach (var p in panels) p.Reset();
            var load = EnsureLoaded();
        }

        async Task EnsureLoaded()
        {
            if (tabs.SelectedIndex >= panels.Count) { await market.ShowTab(); return; }
            var g = Game;
            if (g == null || tabs.SelectedIndex < 0) return;
            var p = panels[tabs.SelectedIndex];
            if (p.LoadedFor != g.Id) await p.LoadList();
        }

        async Task SignOut()
        {
            if (Busy) return;
            if (HasUnsaved() && !Ask("You have unsaved edits. Sign out and throw them away?")) return;
            SetStatus("Signing out...");
            await api.SignOut();
            ClearGames();
            ShowSignedOut("");
            SetStatus("Signed out. This app's access on Roblox was removed.");
        }

        void ClearGames()
        {
            suppressGameChange = true;
            gameBox.Items.Clear();
            suppressGameChange = false;
            currentGameId = null;
            foreach (var p in panels) p.Reset();
        }

        void OpenSettings()
        {
            if (Busy || signInCts != null) return;
            string id = settings.ClientId, secret = settings.ClientSecret, scopes = settings.Scopes;
            if (!OpenSetup(false)) return;
            bool appChanged = settings.ClientId != id || settings.ClientSecret != secret || settings.Scopes != scopes;
            if (appChanged && (api.User != null || settings.RefreshToken != ""))
            {
                api.Forget();
                ClearGames();
                ShowSignedOut("Settings changed. Sign in again.");
            }
            else SetStatus("Settings saved.");
        }

        bool OpenSetup(bool firstRun)
        {
            using (var f = new SetupForm(settings, firstRun)) return f.ShowDialog(this) == DialogResult.OK;
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (Busy)
            {
                if (!Ask("Work is still running. Quit anyway? Rows that haven't been processed yet won't be created or saved.")) e.Cancel = true;
                return;
            }
            if (HasUnsaved() && !Ask("You have unsaved edits. Quit and throw them away?")) { e.Cancel = true; return; }
            if (panels.Any(p => p.HasPendingCreates) && !Ask("You have rows that haven't been created yet. Quit anyway?")) e.Cancel = true;
        }
    }
}
