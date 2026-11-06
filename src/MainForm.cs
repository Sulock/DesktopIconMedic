// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Sulock

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DesktopIconMedic
{
    public sealed class MainForm : Form
    {
        internal static readonly Color Ink = Color.FromArgb(25, 39, 58);
        internal static readonly Color Muted = Color.FromArgb(103, 117, 135);
        internal static readonly Color Blue = Color.FromArgb(38, 98, 224);
        internal static readonly Color Line = Color.FromArgb(226, 232, 241);
        private readonly string stateRoot;
        private readonly ShortcutService shortcuts;
        private readonly CacheService cache;
        private readonly bool preview;
        private bool busy;
        private readonly List<Control> actions = new List<Control>();
        private readonly StringBuilder fullLog = new StringBuilder();
        private List<ShortcutItem> items = new List<ShortcutItem>();
        private Label statusLabel, countLabel, selectionLabel, detailLabel;
        private DataGridView grid;
        private Button repairButton;
        private TextBox logBox;
        private CheckBox problemsOnly, selectAll;
        private bool updatingChecks;
        private ProgressBar progress;
        private ComboBox languageSelector;
        private bool changingLanguage;
        private string statusKey = "operation.ready";

        public MainForm(string stateRoot, bool preview)
        {
            this.stateRoot = stateRoot;
            this.preview = preview;
            shortcuts = new ShortcutService(stateRoot);
            cache = new CacheService(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), stateRoot);
            Text = UiText.Get("app.window");
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 248, 252);
            ForeColor = Ink;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 800);
            MinimumSize = new Size(980, 700);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildLayout();
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (busy)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, UiText.Get("app.waitClose"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            if (!preview) Shown += delegate { Scan(); };
            else LoadPreview();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle available = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            Location = new Point(available.Left + (available.Width - Width) / 2, available.Top + (available.Height - Height) / 2);
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(21, 37, 60), Margin = Padding.Empty };
            root.Controls.Add(sidebar, 0, 0);
            var brand = new Label { Text = "◈", Font = new Font("Segoe UI", 34), ForeColor = Color.FromArgb(131, 174, 255), Location = new Point(23, 20), Size = new Size(90, 65) };
            sidebar.Controls.Add(brand);
            sidebar.Controls.Add(LabelAt("sidebar.brand", 24, 92, 178, 30, 17, Color.White, true));
            sidebar.Controls.Add(LabelAt("sidebar.englishBrand", 25, 131, 175, 22, 8, Color.FromArgb(157, 177, 204), false));
            sidebar.Controls.Add(LabelAt("sidebar.portable", 25, 160, 180, 24, 9, Color.FromArgb(157, 177, 204), false));

            var sideNav = new Panel { Location = new Point(16, 220), Size = new Size(176, 46), BackColor = Color.FromArgb(39, 60, 91) };
            sideNav.Controls.Add(LabelAt("sidebar.nav", 13, 11, 155, 24, 10, Color.White, true));
            sidebar.Controls.Add(sideNav);
            sidebar.Controls.Add(LabelAt("sidebar.order", 25, 289, 165, 26, 10, Color.FromArgb(193, 208, 229), true));
            sidebar.Controls.Add(LabelAt("sidebar.steps", 25, 325, 169, 106, 10, Color.FromArgb(157, 177, 204), false));

            var sideBottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 232, Padding = new Padding(16), BackColor = sidebar.BackColor, ColumnCount = 1, RowCount = 8 };
            sideBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 23, 32, 14, 36, 10, 36, 10, 28 }) sideBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var languageLabel = new Label { Text = UiText.Get("language.label"), Tag = "language.label", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(193, 208, 229), Margin = Padding.Empty };
            languageSelector = new ComboBox { Name = "LanguageSelector", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = Padding.Empty, AccessibleName = UiText.Get("language.label") };
            languageSelector.Items.AddRange(new object[] { "简体中文", "繁體中文", "English" });
            languageSelector.SelectedIndex = (int)UiText.Language;
            languageSelector.SelectedIndexChanged += delegate { if (!changingLanguage && languageSelector.SelectedIndex >= 0) ChangeLanguage((AppLanguage)languageSelector.SelectedIndex); };
            actions.Add(languageSelector);
            sideBottom.Controls.Add(languageLabel, 0, 0);
            sideBottom.Controls.Add(languageSelector, 0, 1);
            var restore = MakeButton("sidebar.restore", false); restore.Dock = DockStyle.Fill; restore.Margin = Padding.Empty;
            restore.Click += delegate { Restore(); };
            var backups = MakeButton("sidebar.backups", false); backups.Dock = DockStyle.Fill; backups.Margin = Padding.Empty;
            backups.Click += delegate { OpenBackups(); };
            var help = new LinkLabel { Text = UiText.Get("sidebar.help"), Tag = "sidebar.help", LinkColor = Color.FromArgb(174, 199, 239), ActiveLinkColor = Color.White, Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
            help.LinkClicked += delegate { ShowHelp(); };
            sideBottom.Controls.Add(backups, 0, 3);
            sideBottom.Controls.Add(restore, 0, 5);
            sideBottom.Controls.Add(help, 0, 7);
            sidebar.Controls.Add(sideBottom);

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(26, 23, 26, 15), Margin = Padding.Empty };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 97));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 143));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 77));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 97));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            root.Controls.Add(main, 1, 0);

            var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            heading.Controls.Add(LabelAt("heading.title", 0, 0, 720, 39, 23, Ink, true));
            heading.Controls.Add(LabelAt("heading.subtitle", 1, 49, 740, 40, 10, Muted, false));
            heading.SizeChanged += delegate { foreach (Control label in heading.Controls) label.Width = Math.Max(1, heading.ClientSize.Width - label.Left); };
            main.Controls.Add(heading, 0, 0);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            main.Controls.Add(cards, 0, 1);
            cards.Controls.Add(ActionCard("card.scan", "card.scanDescription", "card.rescan", false, Scan), 0, 0);
            cards.Controls.Add(ActionCard("card.refresh", "card.refreshDescription", "card.refreshButton", true, Refresh), 1, 0);
            cards.Controls.Add(ActionCard("card.cache", "card.cacheDescription", "card.cacheButton", false, Rebuild), 2, 0);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            countLabel = new Label { Text = UiText.Get("list.title"), Tag = "list.title", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            problemsOnly = new CheckBox { Text = UiText.Get("list.filter"), Tag = "list.filter", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted };
            problemsOnly.CheckedChanged += delegate { BindItems(); };
            repairButton = MakeButton("list.repair", true); repairButton.Dock = DockStyle.Fill; repairButton.Margin = new Padding(0, 7, 1, 7); repairButton.Enabled = false;
            repairButton.Click += delegate { Repair(); };
            toolbar.Controls.Add(countLabel, 0, 0); toolbar.Controls.Add(problemsOnly, 1, 0); toolbar.Controls.Add(repairButton, 2, 0);
            main.Controls.Add(toolbar, 0, 2);

            grid = new DataGridView {
                Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, AutoGenerateColumns = false, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false,
                GridColor = Line, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, ColumnHeadersHeight = 35,
                Margin = new Padding(0, 0, 0, 0), AccessibleName = UiText.Get("list.accessible")
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(234, 239, 247), ForeColor = Muted, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 4, 0), SelectionBackColor = Color.FromArgb(234, 239, 247) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, BackColor = Color.White, SelectionBackColor = Color.FromArgb(232, 240, 255), SelectionForeColor = Ink, Padding = new Padding(4, 0, 4, 0) };
            grid.RowTemplate.Height = 39;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = UiText.Get("column.selected"), Tag = "column.selected", Width = 56, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = UiText.Get("column.name"), Tag = "column.name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 48, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = UiText.Get("column.status"), Tag = "column.status", Width = 183, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = UiText.Get("column.location"), Tag = "column.location", Width = 128, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = UiText.Get("column.target"), Tag = "column.target", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 65, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate { if (!updatingChecks) UpdateSelection(); };
            grid.CellPainting += PaintUnavailableCheckbox;
            grid.SelectionChanged += delegate { ShowDetail(); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex != 0) ShowSelectedDetails(); };
            main.Controls.Add(grid, 0, 3);

            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            selectionLabel = new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Muted, Text = UiText.Get("list.supported"), Tag = "list.supported" };
            detailLabel = new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Muted, AutoEllipsis = true, Text = UiText.Get("list.detailHint"), Tag = "list.detailHint" };
            var selectionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            selectAll = new CheckBox { Text = UiText.Get("list.selectAll"), Tag = "list.selectAll", AutoCheck = false, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Blue, AccessibleName = UiText.Get("list.selectAllAccessible") };
            selectAll.Click += delegate { if (!updatingChecks) SetVisibleChecks(selectAll.CheckState != CheckState.Checked); };
            actions.Add(selectAll);
            var clearSelection = new LinkLabel { Text = UiText.Get("list.clear"), Tag = "list.clear", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight, Margin = Padding.Empty, LinkColor = Blue };
            clearSelection.LinkClicked += delegate { SetVisibleChecks(false); };
            actions.Add(clearSelection);
            selectionBar.Controls.Add(selectionLabel, 0, 0); selectionBar.Controls.Add(selectAll, 1, 0); selectionBar.Controls.Add(clearSelection, 2, 0);
            detail.Controls.Add(selectionBar, 0, 0); detail.Controls.Add(detailLabel, 0, 1);
            main.Controls.Add(detail, 0, 4);

            var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logPanel.Controls.Add(new Label { Text = UiText.Get("log.title"), Tag = "log.title", ForeColor = Muted, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
            var export = new LinkLabel { Text = UiText.Get("log.export"), Tag = "log.export", LinkColor = Blue, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.TopRight };
            export.LinkClicked += delegate { ExportLog(); };
            logPanel.Controls.Add(export, 1, 0);
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, ForeColor = Muted, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", 8.5F), AccessibleName = UiText.Get("log.accessible") };
            logPanel.Controls.Add(logBox, 0, 1); logPanel.SetColumnSpan(logBox, 2);
            main.Controls.Add(logPanel, 0, 5);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            statusLabel = new Label { Text = UiText.Get("operation.ready"), Tag = "operation.ready", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, MarqueeAnimationSpeed = 30, Visible = false, Margin = new Padding(0, 5, 0, 5) };
            footer.Controls.Add(statusLabel, 0, 0); footer.Controls.Add(progress, 1, 0);
            main.Controls.Add(footer, 0, 6);
        }

        private Label LabelAt(string text, int x, int y, int w, int h, float size, Color color, bool bold)
        {
            return new Label { Text = UiText.Get(text), Tag = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = color, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button {
                Text = UiText.Get(text), Tag = text, BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
                Font = new Font("Microsoft YaHei UI", 9F), Height = 34
            };
            button.FlatAppearance.BorderColor = primary ? Blue : Line;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(26, 82, 202) : Color.FromArgb(239, 244, 252);
            actions.Add(button);
            return button;
        }

        private Panel ActionCard(string title, string description, string buttonText, bool primary, Action action)
        {
            var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 9, 9), BackColor = Color.White, Padding = new Padding(13, 12, 13, 12) };
            card.Paint += delegate(object sender, PaintEventArgs e) { using (var p = new Pen(Line)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = UiText.Get(title), Tag = title, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
            layout.Controls.Add(new Label { Text = UiText.Get(description), Tag = description, ForeColor = Muted, Font = new Font("Microsoft YaHei UI", 8.5F), Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 1);
            Button button = MakeButton(buttonText, primary); button.Dock = DockStyle.Fill; button.Margin = Padding.Empty;
            button.Click += delegate { action(); };
            layout.Controls.Add(button, 0, 2); card.Controls.Add(layout); return card;
        }

        private void OnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) Invoke(action); else action();
        }

        private static void TranslateControls(Control control)
        {
            string key = control.Tag as string;
            if (key != null) control.Text = UiText.Get(key);
            foreach (Control child in control.Controls) TranslateControls(child);
        }

        private void ChangeLanguage(AppLanguage language)
        {
            if (changingLanguage || language == UiText.Language) return;
            if (busy)
            {
                changingLanguage = true;
                try { languageSelector.SelectedIndex = (int)UiText.Language; }
                finally { changingLanguage = false; }
                return;
            }
            grid.EndEdit();
            var selected = new HashSet<string>(SelectedItems().Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            var current = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as ShortcutItem;
            int firstVisible = grid.FirstDisplayedScrollingRowIndex;
            UiText.Code(language);
            changingLanguage = true;
            SuspendLayout();
            try
            {
                UiText.Language = language;
                Text = UiText.Get("app.window");
                TranslateControls(this);
                foreach (DataGridViewColumn column in grid.Columns) column.HeaderText = UiText.Get((string)column.Tag);
                grid.AccessibleName = UiText.Get("list.accessible");
                selectAll.AccessibleName = UiText.Get("list.selectAllAccessible");
                logBox.AccessibleName = UiText.Get("log.accessible");
                languageSelector.AccessibleName = UiText.Get("language.label");
                languageSelector.SelectedIndex = (int)language;
                BindItems();
                updatingChecks = true;
                try
                {
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        var item = (ShortcutItem)row.Tag;
                        row.Cells[0].Value = item.CanRepair && selected.Contains(item.Path);
                        if (current != null && Paths.Same(item.Path, current.Path)) grid.CurrentCell = row.Cells[1];
                    }
                }
                finally { updatingChecks = false; }
                if (firstVisible >= 0 && firstVisible < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = firstVisible;
                UpdateSelection();
                if (grid.Rows.Count > 0) ShowDetail();
                statusLabel.Text = UiText.Get(statusKey);
            }
            finally { ResumeLayout(true); changingLanguage = false; }
            if (!preview)
            {
                try { LanguageSettings.Save(stateRoot, language); }
                catch (Exception ex) { Log(UiText.Get("language.unsaved", ex.Message)); }
            }
        }

        private void Run(string status, Action work, Action done)
        {
            if (busy || preview) return;
            busy = true;
            foreach (Control action in actions) action.Enabled = false;
            grid.Enabled = false; problemsOnly.Enabled = false;
            progress.Visible = true; statusLabel.Text = status;
            var thread = new Thread(delegate() {
                Exception failure = null;
                try { work(); }
                catch (Exception ex) { failure = ex; Log(UiText.Get("app.error", ex.Message)); }
                OnUi(delegate {
                    busy = false;
                    foreach (Control action in actions) action.Enabled = true;
                    grid.Enabled = true; problemsOnly.Enabled = true; progress.Visible = false;
                    statusKey = failure == null ? "operation.done" : "operation.failed";
                    statusLabel.Text = UiText.Get(statusKey);
                    UpdateSelection();
                    if (failure == null && done != null) done();
                    if (failure != null) MessageBox.Show(this, failure.Message, UiText.Get("app.incomplete"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine;
            OnUi(delegate { fullLog.Append(line); logBox.AppendText(line); });
            if (preview) return;
            try
            {
                string folder = Path.Combine(stateRoot, "Logs");
                Paths.RejectReparsePoints(folder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), line, Encoding.UTF8);
            }
            catch { }
        }

        private void Scan()
        {
            Run(UiText.Get("scan.progress"), delegate {
                Log(UiText.Get("scan.start"));
                List<ShortcutItem> scanned = shortcuts.ScanDesktop(Log);
                OnUi(delegate { items = scanned; BindItems(); });
                Log(UiText.Get("scan.done", scanned.Count, scanned.Count(x => x.Finding == Finding.MissingIcon), scanned.Count(x => x.Finding == Finding.PackagedIcon)));
            }, null);
        }

        private new void Refresh()
        {
            Run(UiText.Get("refresh.progress"), delegate {
                Shell.RefreshIcons();
                Log(UiText.Get("refresh.done"));
            }, null);
        }

        private void Rebuild()
        {
            if (MessageBox.Show(this,
                UiText.Get("cache.confirm"), UiText.Get("cache.title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            OperationResult result = null;
            Run(UiText.Get("cache.progress"), delegate { result = cache.Rebuild(Log); }, delegate {
                string summary = result.Succeeded == 0 && result.Failed == 0 ? UiText.Get("cache.noneSummary") : UiText.Get("cache.summary", result.Succeeded, result.Failed);
                MessageBox.Show(this, summary + UiText.Get("cache.after"), UiText.Get("cache.completed"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private List<ShortcutItem> SelectedItems()
        {
            return grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is ShortcutItem && ((ShortcutItem)r.Tag).CanRepair && r.Cells[0].Value is bool && (bool)r.Cells[0].Value).Select(r => (ShortcutItem)r.Tag).ToList();
        }

        private void SetVisibleChecks(bool check)
        {
            if (busy || grid == null) return;
            grid.EndEdit();
            updatingChecks = true;
            try
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    ShortcutItem item = row.Tag as ShortcutItem;
                    row.Cells[0].Value = check && item != null && item.CanRepair;
                }
            }
            finally { updatingChecks = false; }
            UpdateSelection();
        }

        private void PaintUnavailableCheckbox(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            ShortcutItem item = grid.Rows[e.RowIndex].Tag as ShortcutItem;
            if (item == null || item.CanRepair) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus);
            Size size = CheckBoxRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled);
            var point = new Point(e.CellBounds.Left + (e.CellBounds.Width - size.Width) / 2, e.CellBounds.Top + (e.CellBounds.Height - size.Height) / 2);
            CheckBoxRenderer.DrawCheckBox(e.Graphics, point, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled);
            e.Handled = true;
        }

        private void Repair()
        {
            grid.EndEdit();
            List<ShortcutItem> selected = SelectedItems();
            if (selected.Count == 0) return;
            if (MessageBox.Show(this, UiText.Get("repair.confirm", selected.Count), UiText.Get("repair.title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            OperationResult result = null;
            Run(UiText.Get("repair.progress"), delegate {
                result = shortcuts.Repair(selected, Log);
                Shell.RefreshIcons();
                Log(UiText.Get("repair.summary", result.Succeeded, result.Failed, result.BackupDirectory));
                List<ShortcutItem> scanned = shortcuts.ScanDesktop(Log);
                OnUi(delegate { items = scanned; BindItems(); });
            }, null);
        }

        private void Restore()
        {
            try
            {
                List<string> folders = shortcuts.ListRestorableBackups();
                if (folders.Count == 0) { MessageBox.Show(this, UiText.Get("restore.none"), Text); return; }
                string folder;
                using (var dialog = new BackupPicker(folders)) { if (dialog.ShowDialog(this) != DialogResult.OK) return; folder = dialog.SelectedFolder; }
                Run(UiText.Get("restore.progress"), delegate {
                    OperationResult result = shortcuts.Restore(folder, Log);
                    Shell.RefreshIcons();
                    Log(UiText.Get("restore.summary", result.Succeeded, result.Failed));
                    List<ShortcutItem> scanned = shortcuts.ScanDesktop(Log);
                    OnUi(delegate { items = scanned; BindItems(); });
                }, null);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText.Get("restore.readFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void BindItems()
        {
            grid.Rows.Clear();
            foreach (ShortcutItem item in items.Where(x => !problemsOnly.Checked || x.Finding != Finding.Healthy))
            {
                int index = grid.Rows.Add(false, item.Name, item.Status, item.Location, item.Target ?? "—");
                DataGridViewRow row = grid.Rows[index]; row.Tag = item;
                row.Cells[0].ReadOnly = !item.CanRepair;
                row.Cells[2].Style.ForeColor = item.CanRepair ? Color.FromArgb(190, 115, 10) : item.Finding == Finding.Healthy ? Color.FromArgb(34, 135, 112) : Muted;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = item.Detail;
                row.Cells[0].ToolTipText = item.CanRepair ? UiText.Get("list.checkHint") : UiText.Get("list.unsupported", item.Detail);
                row.Cells[4].ToolTipText = item.Target;
            }
            countLabel.Text = UiText.Get("list.count", items.Count, items.Count(x => x.CanRepair));
            UpdateSelection(); ShowDetail();
            if (grid.Rows.Count == 0) detailLabel.Text = items.Count == 0 ? UiText.Get("list.empty") : UiText.Get("list.filteredEmpty");
        }

        private void UpdateSelection()
        {
            if (grid == null || selectionLabel == null) return;
            int count = SelectedItems().Count;
            repairButton.Enabled = !busy && count > 0;
            repairButton.Text = count > 0 ? UiText.Get("list.repairCount", count) : UiText.Get("list.repair");
            selectionLabel.Text = count > 0 ? UiText.Get("list.selectedCount", count) : UiText.Get("list.selectionHint");
            if (selectAll != null)
            {
                int available = grid.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is ShortcutItem && ((ShortcutItem)r.Tag).CanRepair);
                updatingChecks = true;
                try
                {
                    selectAll.Enabled = !busy && available > 0;
                    selectAll.CheckState = count == 0 ? CheckState.Unchecked : count == available ? CheckState.Checked : CheckState.Indeterminate;
                }
                finally { updatingChecks = false; }
            }
        }

        private void ShowDetail()
        {
            if (detailLabel == null) return;
            ShortcutItem item = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as ShortcutItem;
            detailLabel.Text = item == null ? UiText.Get("list.detailHint") : item.Detail;
        }

        private void ShowSelectedDetails()
        {
            ShortcutItem item = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as ShortcutItem;
            if (item == null) return;
            MessageBox.Show(this, UiText.Get("detail.dialog", item.Path, item.Target ?? UiText.Get("detail.noTarget"), String.IsNullOrEmpty(item.IconPath) ? UiText.Get("detail.defaultIcon") : UiText.Get("detail.iconIndex", item.IconPath, item.IconIndex), item.Detail), item.Name + " · " + item.Status, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenBackups()
        {
            try { Paths.RejectReparsePoints(stateRoot); Directory.CreateDirectory(stateRoot); Process.Start(new ProcessStartInfo { FileName = stateRoot, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText.Get("backups.openFailed")); }
        }

        private void ExportLog()
        {
            using (var dialog = new SaveFileDialog { Filter = UiText.Get("log.filter"), FileName = UiText.Get("log.filePrefix") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, fullLog.ToString(), Encoding.UTF8); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText.Get("log.exportFailed")); }
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                UiText.Get("help.body", stateRoot), UiText.Get("help.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LoadPreview()
        {
            items = new List<ShortcutItem> {
                new ShortcutItem { Path = @"C:\Demo\Photo Editor.lnk", LocationKey = "location.personal", Target = @"C:\Apps\PhotoEditor\PhotoEditor.exe", Finding = Finding.MissingIcon, DetailKey = "detail.missingIcon" },
                new ShortcutItem { Path = @"C:\Demo\ChatGPT.lnk", LocationKey = "location.personal", Target = "ChatGPT", Finding = Finding.PackagedIcon, DetailKey = "preview.pin" },
                new ShortcutItem { Path = @"C:\Demo\Claude.lnk", LocationKey = "location.personal", Target = "Claude", Finding = Finding.PackagedIcon, DetailKey = "preview.batch" },
                new ShortcutItem { Path = @"C:\Demo\Notes.lnk", LocationKey = "location.personal", Target = @"D:\Tools\Notes\Notes.exe", Finding = Finding.MissingTarget, DetailKey = "preview.missing" },
                new ShortcutItem { Path = @"C:\Demo\Browser.lnk", LocationKey = "location.public", Target = @"C:\Program Files\Browser\browser.exe", Finding = Finding.Healthy, DetailKey = "preview.healthy" },
                new ShortcutItem { Path = @"C:\Demo\Code Editor.lnk", LocationKey = "location.personal", Target = @"C:\Apps\Editor\Editor.exe", Finding = Finding.Healthy, DetailKey = "preview.healthy" },
                new ShortcutItem { Path = @"C:\Demo\Store.lnk", LocationKey = "location.personal", Finding = Finding.Special, DetailKey = "preview.special" }
            };
            BindItems();
            Log(UiText.Get("preview.log"));
            statusKey = "preview.status";
            statusLabel.Text = UiText.Get(statusKey);
        }
    }

    internal sealed class BackupPicker : Form
    {
        private readonly ListBox list;
        private readonly List<string> folders;
        public string SelectedFolder { get { return folders[list.SelectedIndex]; } }
        public BackupPicker(List<string> folders)
        {
            this.folders = folders;
            Text = UiText.Get("picker.title"); Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(610, 305);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            var label = new Label { Text = UiText.Get("picker.description"), Tag = "picker.description", Dock = DockStyle.Top, Height = 49, Padding = new Padding(12), AutoSize = false };
            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (string folder in folders) list.Items.Add(Path.GetFileName(folder));
            list.SelectedIndex = 0;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = UiText.Get("picker.cancel"), Tag = "picker.cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 32) };
            var ok = new Button { Text = UiText.Get("picker.restore"), Tag = "picker.restore", DialogResult = DialogResult.OK, Size = new Size(115, 32) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            Controls.Add(list); Controls.Add(label); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }
    }
}
