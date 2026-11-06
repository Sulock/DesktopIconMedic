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

        public MainForm(string stateRoot, bool preview)
        {
            this.stateRoot = stateRoot;
            this.preview = preview;
            shortcuts = new ShortcutService(stateRoot);
            cache = new CacheService(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), stateRoot);
            Text = "桌面图标修复助手 1.2";
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
                    MessageBox.Show(this, "当前操作正在进行，请等待完成后再关闭。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            sidebar.Controls.Add(LabelAt("图标修复助手", 24, 92, 178, 30, 17, Color.White, true));
            sidebar.Controls.Add(LabelAt("DESKTOP ICON MEDIC", 25, 131, 175, 22, 8, Color.FromArgb(157, 177, 204), false));
            sidebar.Controls.Add(LabelAt("Windows 11  /  免安装", 25, 160, 180, 24, 9, Color.FromArgb(157, 177, 204), false));

            var sideNav = new Panel { Location = new Point(16, 220), Size = new Size(176, 46), BackColor = Color.FromArgb(39, 60, 91) };
            sideNav.Controls.Add(LabelAt("●   桌面图标修复", 13, 11, 155, 24, 10, Color.White, true));
            sidebar.Controls.Add(sideNav);
            sidebar.Controls.Add(LabelAt("建议按顺序尝试", 25, 299, 165, 26, 10, Color.FromArgb(193, 208, 229), true));
            sidebar.Controls.Add(LabelAt("01  检查快捷方式\n\n02  刷新图标\n\n03  按需重建缓存", 25, 337, 169, 140, 10, Color.FromArgb(157, 177, 204), false));

            var sideBottom = new Panel { Dock = DockStyle.Bottom, Height = 173, Padding = new Padding(16), BackColor = sidebar.BackColor };
            var restore = MakeButton("恢复快捷方式备份", false); restore.Dock = DockStyle.Top; restore.Height = 36;
            restore.Click += delegate { Restore(); };
            var backups = MakeButton("打开备份与日志", false); backups.Dock = DockStyle.Top; backups.Height = 36;
            backups.Click += delegate { OpenBackups(); };
            var help = new LinkLabel { Text = "使用说明  ↗", LinkColor = Color.FromArgb(174, 199, 239), ActiveLinkColor = Color.White, Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
            help.LinkClicked += delegate { ShowHelp(); };
            sideBottom.Controls.Add(restore);
            sideBottom.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 });
            sideBottom.Controls.Add(backups);
            sideBottom.Controls.Add(help);
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
            heading.Controls.Add(LabelAt("让桌面图标恢复清晰", 0, 0, 590, 39, 23, Ink, true));
            heading.Controls.Add(LabelAt("出现白纸图标？先检查路径，再让 Windows 重新加载图标。", 1, 49, 740, 28, 10, Muted, false));
            main.Controls.Add(heading, 0, 0);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            main.Controls.Add(cards, 0, 1);
            cards.Controls.Add(ActionCard("01  检查桌面", "查找失效的目标与图标路径", "重新扫描", false, Scan), 0, 0);
            cards.Controls.Add(ActionCard("02  刷新图标", "请求系统重新加载图标", "刷新图标", true, Refresh), 1, 0);
            cards.Controls.Add(ActionCard("03  深度修复", "重建缓存 · 会暂时关闭桌面", "重建图标缓存…", false, Rebuild), 2, 0);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
            countLabel = new Label { Text = "桌面快捷方式", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            problemsOnly = new CheckBox { Text = "只看待关注项目", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted };
            problemsOnly.CheckedChanged += delegate { BindItems(); };
            repairButton = MakeButton("修复勾选项", true); repairButton.Dock = DockStyle.Fill; repairButton.Margin = new Padding(0, 7, 1, 7); repairButton.Enabled = false;
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
                Margin = new Padding(0, 0, 0, 0), AccessibleName = "桌面快捷方式检查结果"
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(234, 239, 247), ForeColor = Muted, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 4, 0), SelectionBackColor = Color.FromArgb(234, 239, 247) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, BackColor = Color.White, SelectionBackColor = Color.FromArgb(232, 240, 255), SelectionForeColor = Ink, Padding = new Padding(4, 0, 4, 0) };
            grid.RowTemplate.Height = 39;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "勾选", Width = 56, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "快捷方式", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 48, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "检查结果", Width = 145, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = "位置", Width = 92, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "目标路径", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 65, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate { if (!updatingChecks) UpdateSelection(); };
            grid.CellPainting += PaintUnavailableCheckbox;
            grid.SelectionChanged += delegate { ShowDetail(); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex != 0) ShowSelectedDetails(); };
            main.Controls.Add(grid, 0, 3);

            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            selectionLabel = new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Muted, Text = "支持失效图标来源与商店应用图标修复。" };
            detailLabel = new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Muted, AutoEllipsis = true, Text = "选中一项查看原因，双击查看完整路径。" };
            var selectionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 63));
            selectAll = new CheckBox { Text = "全选可修复项", AutoCheck = false, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Blue, AccessibleName = "全选当前列表中的可修复项目" };
            selectAll.Click += delegate { if (!updatingChecks) SetVisibleChecks(selectAll.CheckState != CheckState.Checked); };
            actions.Add(selectAll);
            var clearSelection = new LinkLabel { Text = "取消全选", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight, Margin = Padding.Empty, LinkColor = Blue };
            clearSelection.LinkClicked += delegate { SetVisibleChecks(false); };
            actions.Add(clearSelection);
            selectionBar.Controls.Add(selectionLabel, 0, 0); selectionBar.Controls.Add(selectAll, 1, 0); selectionBar.Controls.Add(clearSelection, 2, 0);
            detail.Controls.Add(selectionBar, 0, 0); detail.Controls.Add(detailLabel, 0, 1);
            main.Controls.Add(detail, 0, 4);

            var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logPanel.Controls.Add(new Label { Text = "操作记录", ForeColor = Muted, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
            var export = new LinkLabel { Text = "导出日志", LinkColor = Blue, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.TopRight };
            export.LinkClicked += delegate { ExportLog(); };
            logPanel.Controls.Add(export, 1, 0);
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, ForeColor = Muted, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", 8.5F), AccessibleName = "操作日志" };
            logPanel.Controls.Add(logBox, 0, 1); logPanel.SetColumnSpan(logBox, 2);
            main.Controls.Add(logPanel, 0, 5);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            statusLabel = new Label { Text = "就绪", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, MarqueeAnimationSpeed = 30, Visible = false, Margin = new Padding(0, 5, 0, 5) };
            footer.Controls.Add(statusLabel, 0, 0); footer.Controls.Add(progress, 1, 0);
            main.Controls.Add(footer, 0, 6);
        }

        private Label LabelAt(string text, int x, int y, int w, int h, float size, Color color, bool bold)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = color, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button {
                Text = text, BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink,
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
            layout.Controls.Add(new Label { Text = title, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
            layout.Controls.Add(new Label { Text = description, ForeColor = Muted, Font = new Font("Microsoft YaHei UI", 8.5F), Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 1);
            Button button = MakeButton(buttonText, primary); button.Dock = DockStyle.Fill; button.Margin = Padding.Empty;
            button.Click += delegate { action(); };
            layout.Controls.Add(button, 0, 2); card.Controls.Add(layout); return card;
        }

        private void OnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) Invoke(action); else action();
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
                catch (Exception ex) { failure = ex; Log("操作未完成：" + ex.Message); }
                OnUi(delegate {
                    busy = false;
                    foreach (Control action in actions) action.Enabled = true;
                    grid.Enabled = true; problemsOnly.Enabled = true; progress.Visible = false;
                    statusLabel.Text = failure == null ? "操作完成 · 请查看结果与桌面图标" : "操作未完成 · 请查看操作记录";
                    UpdateSelection();
                    if (failure == null && done != null) done();
                    if (failure != null) MessageBox.Show(this, failure.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Run("正在检查桌面快捷方式…", delegate {
                Log("开始检查个人桌面和公共桌面的快捷方式。");
                List<ShortcutItem> scanned = shortcuts.ScanDesktop(Log);
                OnUi(delegate { items = scanned; BindItems(); });
                Log("检查完成：" + scanned.Count + " 项，" + scanned.Count(x => x.Finding == Finding.MissingIcon) + " 项图标来源失效，" + scanned.Count(x => x.Finding == Finding.PackagedIcon) + " 项可固定商店图标。请仅勾选实际显示异常的项目。");
            }, null);
        }

        private new void Refresh()
        {
            Run("正在请求 Windows 刷新图标…", delegate {
                Shell.RefreshIcons();
                Log("已发送图标刷新请求。请等待片刻，查看桌面；若仍显示白纸，可尝试重建图标缓存。");
            }, null);
        }

        private void Rebuild()
        {
            if (MessageBox.Show(this,
                "此操作将：\n\n• 暂时关闭当前登录会话的 Windows 资源管理器。\n• 先备份、再移除图标缓存，随后恢复桌面。\n\n任务栏和桌面会短暂消失，已打开的文件夹窗口可能关闭。\n请先等待文件复制 / 移动完成，并保存正在编辑的文件。\n\n现在重建图标缓存吗？", "重建图标缓存", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            OperationResult result = null;
            Run("正在重建图标缓存，请勿关闭程序…", delegate { result = cache.Rebuild(Log); }, delegate {
                string summary = result.Succeeded == 0 && result.Failed == 0 ? "未发现可移除的缓存，已请求刷新。" : "已移除 " + result.Succeeded + " 个缓存文件，" + result.Failed + " 个未处理。";
                MessageBox.Show(this, summary + "\n\nWindows 会自动生成新缓存。请检查桌面图标；\n若仍有白纸图标，可重启电脑，或按检查结果重新创建快捷方式。", "缓存处理完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (MessageBox.Show(this, "将为勾选的 " + selected.Count + " 个快捷方式备份原文件，\n再使用已安装程序的原图标。商店应用图标将另存为稳定的本地文件。\n\n启动目标、参数与商店应用启动标识将保留，可在左下角恢复备份。\n继续修复吗？", "修复图标来源", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            OperationResult result = null;
            Run("正在备份并修复快捷方式…", delegate {
                result = shortcuts.Repair(selected, Log);
                Shell.RefreshIcons();
                Log("快捷方式修复完成：成功 " + result.Succeeded + " 项，未修复 " + result.Failed + " 项。备份：" + result.BackupDirectory);
                List<ShortcutItem> scanned = shortcuts.ScanDesktop(Log);
                OnUi(delegate { items = scanned; BindItems(); });
            }, null);
        }

        private void Restore()
        {
            try
            {
                List<string> folders = shortcuts.ListRestorableBackups();
                if (folders.Count == 0) { MessageBox.Show(this, "没有待恢复的快捷方式备份。", Text); return; }
                string folder;
                using (var dialog = new BackupPicker(folders)) { if (dialog.ShowDialog(this) != DialogResult.OK) return; folder = dialog.SelectedFolder; }
                Run("正在恢复快捷方式备份…", delegate {
                    OperationResult result = shortcuts.Restore(folder, Log);
                    Shell.RefreshIcons();
                    Log("恢复完成：成功 " + result.Succeeded + " 项，跳过 " + result.Failed + " 项。");
                    List<ShortcutItem> scanned = shortcuts.ScanDesktop(Log);
                    OnUi(delegate { items = scanned; BindItems(); });
                }, null);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法读取备份", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
                row.Cells[0].ToolTipText = item.CanRepair ? "点击方框勾选，可同时勾选多项后批量修复。" : "此项暂不支持自动修复：" + item.Detail;
                row.Cells[4].ToolTipText = item.Target;
            }
            countLabel.Text = "桌面快捷方式  " + items.Count + " 项 · 可修复 " + items.Count(x => x.CanRepair) + " 项";
            UpdateSelection(); ShowDetail();
            if (grid.Rows.Count == 0) detailLabel.Text = items.Count == 0 ? "未发现普通桌面快捷方式，或桌面无法读取。仍可尝试刷新图标，请同时查看操作记录。" : "当前筛选下没有待关注项目。";
        }

        private void UpdateSelection()
        {
            if (grid == null || selectionLabel == null) return;
            int count = SelectedItems().Count;
            repairButton.Enabled = !busy && count > 0;
            repairButton.Text = count > 0 ? "修复勾选项 (" + count + ")" : "修复勾选项";
            selectionLabel.Text = count > 0 ? "已勾选 " + count + " 项 · 点击修复按钮批量处理" : "点击行首方框多选；灰色方框表示暂不支持自动修复。";
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
            detailLabel.Text = item == null ? "选中一项查看原因，双击查看完整路径。" : item.Detail;
        }

        private void ShowSelectedDetails()
        {
            ShortcutItem item = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as ShortcutItem;
            if (item == null) return;
            MessageBox.Show(this, "快捷方式：" + item.Path + "\n\n目标：" + (item.Target ?? "未提供普通文件路径") + "\n\n图标来源：" + (String.IsNullOrEmpty(item.IconPath) ? "使用默认图标" : item.IconPath + "，索引 " + item.IconIndex) + "\n\n" + item.Detail, item.Name + " · " + item.Status, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenBackups()
        {
            try { Paths.RejectReparsePoints(stateRoot); Directory.CreateDirectory(stateRoot); Process.Start(new ProcessStartInfo { FileName = stateRoot, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开目录"); }
        }

        private void ExportLog()
        {
            using (var dialog = new SaveFileDialog { Filter = "文本日志 (*.txt)|*.txt", FileName = "图标修复记录_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, fullLog.ToString(), Encoding.UTF8); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "日志导出失败"); }
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "推荐顺序\n\n1. 检查桌面：查看目标文件和图标来源是否可用。\n2. 刷新图标：通常适合路径正常但图标显示异常的情况。\n3. 重建缓存：刷新无效时使用；会暂时关闭资源管理器。\n4. 点击行首方框，可同时勾选多项后批量修复。\n5. 全选可修复项：选择当前列表中支持修复的项目。\n\n灰色方框表示暂不支持自动修复，可选中该行查看原因。\n商店图标从已安装应用中提取，原启动方式保留。\n请保留数据目录中的 Icons 文件夹，它保存修复后的图标。\n公共桌面可能需要管理员权限。\n\n备份与日志保存于：\n" + stateRoot + "\n\n版本 1.2.0 · 无需安装 · 无联网或自动启动功能", "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LoadPreview()
        {
            items = new List<ShortcutItem> {
                new ShortcutItem { Path = @"C:\Demo\图像编辑器.lnk", Location = "个人桌面", Target = @"C:\Apps\PhotoEditor\PhotoEditor.exe", Finding = Finding.MissingIcon, Detail = "原图标文件不存在，可改用启动程序自身的图标。修复前会备份快捷方式。" },
                new ShortcutItem { Path = @"C:\Demo\ChatGPT.lnk", Location = "个人桌面", Target = "商店应用 · ChatGPT", Finding = Finding.PackagedIcon, Detail = "如图标显示白纸，可勾选固定原图标，并保留原启动方式。" },
                new ShortcutItem { Path = @"C:\Demo\Claude.lnk", Location = "个人桌面", Target = "商店应用 · Claude", Finding = Finding.PackagedIcon, Detail = "如图标显示白纸，可与其他项目一起勾选，批量固定原图标。" },
                new ShortcutItem { Path = @"C:\Demo\笔记工具.lnk", Location = "个人桌面", Target = @"D:\Tools\Notes\Notes.exe", Finding = Finding.MissingTarget, Detail = "目标文件当前不可用，请确认软件仍已安装。" },
                new ShortcutItem { Path = @"C:\Demo\浏览器.lnk", Location = "公共桌面", Target = @"C:\Program Files\Browser\browser.exe", Finding = Finding.Healthy, Detail = "路径未发现异常；如仍显示白纸，可尝试刷新图标。" },
                new ShortcutItem { Path = @"C:\Demo\代码编辑器.lnk", Location = "个人桌面", Target = @"C:\Apps\Editor\Editor.exe", Finding = Finding.Healthy, Detail = "路径未发现异常；如仍显示白纸，可尝试刷新图标。" },
                new ShortcutItem { Path = @"C:\Demo\应用商店.lnk", Location = "个人桌面", Finding = Finding.Special, Detail = "系统或商店应用，可尝试刷新图标。" }
            };
            BindItems();
            Log("界面预览 · 以上为演示数据，未读取或修改真实桌面。");
            statusLabel.Text = "预览模式 · 演示数据";
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
            Text = "选择要恢复的快捷方式备份"; Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(610, 305);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            var label = new Label { Text = "选择一次修复记录。修复后又被改动或删除的快捷方式将跳过。", Dock = DockStyle.Top, Height = 49, Padding = new Padding(12), AutoSize = false };
            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (string folder in folders) list.Items.Add(Path.GetFileName(folder));
            list.SelectedIndex = 0;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(90, 32) };
            var ok = new Button { Text = "恢复此备份", DialogResult = DialogResult.OK, Size = new Size(115, 32) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            Controls.Add(list); Controls.Add(label); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }
    }
}
