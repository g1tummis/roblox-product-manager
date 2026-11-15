using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RobloxProductManager
{
    // First-run setup and "App settings": Client ID, Client Secret, port, scopes.
    class SetupForm : Form
    {
        readonly Settings s;
        readonly bool hadSecret;
        readonly TextBox idBox, secretBox, redirectBox, scopesBox;
        readonly NumericUpDown portBox;

        public SetupForm(Settings settings, bool firstRun)
        {
            s = settings;
            hadSecret = s.ClientSecret != "";
            Settings old = s.Complete ? null : Settings.FromOldVersion();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = firstRun ? "Set up Roblox Product Manager" : "App settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };

            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                Margin = new Padding(0, 0, 0, 8),
                Text = "This app signs in through your own Roblox OAuth 2.0 app. On the Creator Dashboard (it needs an ID-verified account): " +
                       "create an app, tick the scopes listed below, add the Redirect URL below, then paste the app's Client ID and Secret here. " +
                       "README.md (Part 1) has the full steps.",
            };
            t.Controls.Add(intro, 0, 0);
            t.SetColumnSpan(intro, 3);

            var link = new LinkLabel { Text = "Open the Roblox OAuth 2.0 Apps page", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            link.LinkClicked += (o, e) => Browser.Open("https://create.roblox.com/dashboard/credentials?activeTab=OAuthTab");
            t.Controls.Add(link, 0, 1);
            t.SetColumnSpan(link, 3);

            idBox = new TextBox { Width = 380, Text = s.ClientId != "" ? s.ClientId : (old != null ? old.ClientId : "") };
            AddRow(t, 2, "Client ID:", idBox, null);

            secretBox = new TextBox { Width = 380, UseSystemPasswordChar = true };
            if (!hadSecret && old != null) secretBox.Text = old.ClientSecret;
            AddRow(t, 3, "Client Secret:", secretBox, hadSecret ? Hint("Leave empty to keep the saved one") : null);

            portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 90, Value = old != null ? old.Port : s.Port };
            AddRow(t, 4, "Port:", portBox, Hint("Keep 3000 unless something else uses it"));

            redirectBox = new TextBox { Width = 380, ReadOnly = true };
            AddRow(t, 5, "Redirect URL:", redirectBox, CopyButton(() => redirectBox.Text));
            portBox.ValueChanged += (o, e) => UpdateRedirect();
            UpdateRedirect();

            scopesBox = new TextBox
            {
                Width = 380,
                Height = 120,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Text = string.Join("\r\n", Split(s.Scopes)),
            };
            AddRow(t, 6, "Scopes:", scopesBox, CopyButton(() => string.Join(" ", Split(scopesBox.Text))));

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0),
                Text = "Tick exactly these scopes on your Roblox app. Your Client Secret and sign-in are stored encrypted, readable only by your Windows account.",
            };
            t.Controls.Add(note, 0, 7);
            t.SetColumnSpan(note, 3);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += (o, e) => SaveAndClose();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            t.Controls.Add(buttons, 0, 8);
            t.SetColumnSpan(buttons, 3);

            AcceptButton = save;
            CancelButton = cancel;
            Controls.Add(t);
            ResumeLayout(false);
            PerformLayout();
        }

        void UpdateRedirect() { redirectBox.Text = "http://localhost:" + portBox.Value + "/callback"; }

        static string[] Split(string scopes)
        {
            return (scopes ?? "").Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static void AddRow(TableLayoutPanel t, int row, string label, Control box, Control extra)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, row);
            t.Controls.Add(box, 1, row);
            if (extra != null) t.Controls.Add(extra, 2, row);
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 6, 0, 0) };
        }

        static Button CopyButton(Func<string> text)
        {
            var b = new Button { Text = "Copy", AutoSize = true };
            b.Click += (o, e) => { try { Clipboard.SetText(text()); } catch (Exception) { } };
            return b;
        }

        void Warn(string text) { MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        void SaveAndClose()
        {
            string id = idBox.Text.Trim(), secret = secretBox.Text.Trim();
            var scopes = Split(scopesBox.Text);
            if (id == "" || id.Any(char.IsWhiteSpace)) { Warn("Paste the Client ID from your Roblox OAuth app (no spaces)."); idBox.Focus(); return; }
            if (secret == "" && !hadSecret) { Warn("Paste the Client Secret from your Roblox OAuth app."); secretBox.Focus(); return; }
            if (secret.Any(char.IsWhiteSpace)) { Warn("The Client Secret has no spaces. Paste it again."); secretBox.Focus(); return; }
            if (!scopes.Contains("openid")) { Warn("The scopes must include openid."); scopesBox.Focus(); return; }
            if (secret != "" && !secret.StartsWith("RBX-") &&
                MessageBox.Show(this, "Roblox client secrets usually start with \"RBX-\". Save this one anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            s.ClientId = id;
            if (secret != "") s.ClientSecret = secret;
            s.Port = (int)portBox.Value;
            s.Scopes = string.Join(" ", scopes);
            try { s.Save(); }
            catch (Exception ex) { Warn("Could not save the settings: " + ex.Message); return; }
            DialogResult = DialogResult.OK;
        }
    }
}
