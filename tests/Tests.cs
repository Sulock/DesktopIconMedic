using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DesktopIconMedic;

internal static class Tests
{
    private static int passed, failed;
    private static string root, exe;
    private static bool visiblePreview;

    [STAThread]
    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetFullPath(args[0]), "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        visiblePreview = args.Contains("--visible-preview");
        Directory.CreateDirectory(root);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Use our own bundled executable as the icon source. Never mutate a real desktop shortcut.
        exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopIconMedic.exe");
        Check("Executable contains an icon", delegate { Assert(Shell.HasIcon(exe), "Embedded icon missing"); });
        Check("Missing icon detection, repair, metadata preservation, exact restore", RoundTrip);
        Check("Changed shortcut is not overwritten during repair", ChangedAfterScan);
        Check("Changed shortcut is not overwritten during restore", RestoreConflict);
        Check("Altered backup is not restored", CorruptBackup);
        Check("Missing targets and relative icon paths are not auto-repaired", Unsupported);
        Check("Network target is not queried", NetworkTarget);
        Check("Corrupt shortcut produces a readable finding", CorruptShortcut);
        Check("Default icon is accepted", DefaultIcon);
        Check("Read-only shortcut failure preserves the original", ReadOnlyShortcut);
        Check("Cache allowlist excludes thumbnails, directories, and traversal", CacheAllowlist);
        Check("Cache discovery only returns approved icon databases", CacheDiscovery);
        Check("Cache rebuild backs up exact bytes and preserves thumbnails", CacheRebuild);
        Check("Explorer resume runs even when pausing fails", PauseFailure);
        Check("Recovery startup failure prevents cache deletion", RecoveryFailure);
        Check("Failed cache backup preserves the cache and resumes desktop", BackupFailure);
        Check("Explorer resume failure keeps the recovery marker", ResumeFailure);
        Check("Backup traversal is rejected", BackupTraversal);
        Check("Installer shortcuts are kept intact", InstallerLink);
        Check("Packaged application identity parsing rejects invalid names", PackageIdentity);
        Check("Packaged icons generate a reusable multi-resolution ICO", PackageIconFile);
        Check("Packaged logo lookup confines assets and chooses the high-resolution logo", PackageLogoSelection);
        Check("Checkbox multi-selection, select-all and deselection preserve eligibility", MultiSelection);
        Check("Batch repair and restore operate on all selected shortcuts", BatchRoundTrip);
        Check("First-run language follows Windows, including regional Chinese variants", SystemLanguage);
        Check("Language preferences survive restart and invalid settings fall back to Windows", SavedLanguage);
        Check("All translations preserve their formatting arguments", TranslationFormats);
        Check("Language switching preserves selection, filtering, focused row and logs", LiveLanguageSwitch);
        Check("Language cannot change during an active operation", BusyLanguageSwitch);
        Check("Scan details and repair diagnostics use the selected language", LocalizedDiagnostics);
        Check("All three language layouts render with the language selector", LanguagePreviews);
        Check(visiblePreview ? "Visible UI preview renders using demo data" : "UI layout initializes using demo data", Preview);
        string summary = passed + " passed, " + failed + " failed";
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(root, "result.txt"), summary);
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }

    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Ignore(string value) { }
    private static string Folder(string name) { string p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }

    private static string Broken(string folder)
    {
        string path = Path.Combine(folder, "测试 应用 & 中文.lnk");
        Shell.CreateLink(path, new LinkData { Target = exe, IconPath = Path.Combine(folder, "missing.ico"),
            Arguments = "--sample \"中文 path\" & literal", WorkingDirectory = folder, Description = "测试说明与参数保留" });
        return path;
    }

    private static void RoundTrip()
    {
        string folder = Folder("roundtrip"), path = Broken(folder);
        var service = new ShortcutService(Path.Combine(folder, "state"));
        string before = Paths.Hash(path);
        LinkData original = Shell.ReadLink(path);
        ShortcutItem item = service.Inspect(path, "测试");
        Assert(item.CanRepair, item.Detail);
        var repaired = service.Repair(new[] { item }, Ignore);
        Assert(repaired.Succeeded == 1 && repaired.Failed == 0, "Repair failed");
        LinkData after = Shell.ReadLink(path);
        Assert(Paths.Same(after.IconPath, exe), "Incorrect icon");
        Assert(after.Arguments == original.Arguments && after.Target == original.Target && after.Description == original.Description && after.WorkingDirectory == original.WorkingDirectory, "Shortcut metadata changed");
        Assert(service.Inspect(path, "测试").Finding == Finding.Healthy, "Not healthy after repair");
        Assert(service.ListRestorableBackups().Count == 1, "Backup not listed");
        var restored = service.Restore(repaired.BackupDirectory, Ignore);
        Assert(restored.Succeeded == 1 && Paths.Hash(path) == before, "Restore not byte-identical");
        Assert(service.ListRestorableBackups().Count == 0, "Restored backup still listed");
    }

    private static void ChangedAfterScan()
    {
        string folder = Folder("changed"), path = Broken(folder);
        var service = new ShortcutService(Path.Combine(folder, "state"));
        ShortcutItem scanned = service.Inspect(path, "测试");
        Shell.ChangeIcon(path, exe);
        string hash = Paths.Hash(path);
        var result = service.Repair(new[] { scanned }, Ignore);
        Assert(result.Succeeded == 0 && result.Failed == 1 && Paths.Hash(path) == hash, "Changed file overwritten");
    }

    private static void RestoreConflict()
    {
        string folder = Folder("restore-conflict"), path = Broken(folder);
        var service = new ShortcutService(Path.Combine(folder, "state"));
        var result = service.Repair(new[] { service.Inspect(path, "测试") }, Ignore);
        Shell.ChangeIcon(path, Path.Combine(folder, "user-choice.ico"));
        string hash = Paths.Hash(path);
        var restored = service.Restore(result.BackupDirectory, Ignore);
        Assert(restored.Succeeded == 0 && restored.Failed == 1 && Paths.Hash(path) == hash, "User edit overwritten");
        File.Delete(path);
        Assert(service.Restore(result.BackupDirectory, Ignore).Failed == 1 && !File.Exists(path), "Deleted shortcut resurrected");
    }

    private static void CorruptBackup()
    {
        string folder = Folder("backup-corrupt"), path = Broken(folder);
        var service = new ShortcutService(Path.Combine(folder, "state"));
        var result = service.Repair(new[] { service.Inspect(path, "测试") }, Ignore);
        string hash = Paths.Hash(path);
        File.AppendAllText(Path.Combine(result.BackupDirectory, "0000.lnk"), "tampered");
        Assert(service.Restore(result.BackupDirectory, Ignore).Failed == 1 && Paths.Hash(path) == hash, "Altered backup accepted");
    }

    private static void Unsupported()
    {
        string folder = Folder("unsupported"), path = Path.Combine(folder, "missing-target.lnk");
        var service = new ShortcutService(Path.Combine(folder, "state"));
        Shell.CreateLink(path, new LinkData { Target = Path.Combine(folder, "missing.exe"), IconPath = Path.Combine(folder, "missing.ico") });
        Assert(service.Inspect(path, "测试").Finding == Finding.MissingTarget, "Missing target classification");
        Shell.CreateLink(path, new LinkData { Target = exe, IconPath = "relative-missing.ico" });
        Assert(!service.Inspect(path, "测试").CanRepair, "Relative path auto-repaired");
    }

    private static void NetworkTarget()
    {
        string folder = Folder("network");
        var service = new ShortcutService(Path.Combine(folder, "state"));
        Assert(Paths.IsRemote(@"\\example.invalid\share\app.exe"), "UNC target not remote");
        // Classify the shortcut itself without invoking COM or a network filesystem.
        Assert(service.Inspect(@"\\example.invalid\share\app.lnk", "测试").Finding == Finding.Unavailable, "Network path queried");
    }

    private static void CorruptShortcut()
    {
        string folder = Folder("corrupt"), path = Path.Combine(folder, "invalid.lnk");
        File.WriteAllText(path, "Not a shortcut");
        Assert(new ShortcutService(folder).Inspect(path, "测试").Finding == Finding.Error, "Corrupt link accepted");
    }

    private static void DefaultIcon()
    {
        string folder = Folder("default"), path = Path.Combine(folder, "default.lnk");
        Shell.CreateLink(path, new LinkData { Target = exe });
        Assert(new ShortcutService(folder).Inspect(path, "测试").Finding == Finding.Healthy, "Default icon incorrectly flagged");
    }

    private static void ReadOnlyShortcut()
    {
        string folder = Folder("readonly"), path = Broken(folder);
        var service = new ShortcutService(Path.Combine(folder, "state"));
        var item = service.Inspect(path, "测试");
        string before = Paths.Hash(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var result = service.Repair(new[] { item }, Ignore);
            Assert(result.Failed == 1 && Paths.Hash(path) == before, "Read-only original changed");
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    private static void CacheAllowlist()
    {
        string folder = Folder("cache-rules");
        var service = new CacheService(folder, Path.Combine(folder, "state"));
        string explorer = Path.Combine(folder, "Microsoft", "Windows", "Explorer");
        Assert(service.IsAllowedCache(Path.Combine(folder, "IconCache.db")), "Legacy rejected");
        Assert(service.IsAllowedCache(Path.Combine(explorer, "iconcache_256.db")), "Modern rejected");
        Assert(service.IsAllowedCache(Path.Combine(explorer, "iconcache_custom_stream.db")), "Custom stream rejected");
        Assert(!service.IsAllowedCache(Path.Combine(explorer, "thumbcache_256.db")), "Thumbnail accepted");
        Assert(!service.IsAllowedCache(Path.Combine(explorer, "iconcache_256.db.bak")), "Backup accepted");
        Assert(!service.IsAllowedCache(Path.Combine(explorer, "..", "iconcache_256.db")), "Parent traversal accepted");
        Assert(!service.IsAllowedCache(Path.Combine(explorer, "nested", "iconcache_256.db")), "Nested file accepted");
        Assert(!service.IsAllowedCache(Path.Combine(folder + "-other", "IconCache.db")), "Sibling prefix accepted");
        Assert(!service.IsAllowedCache(Path.Combine(explorer, "iconcache*.db")), "Wildcard accepted");
    }

    private static void CacheDiscovery()
    {
        string folder = Folder("cache-discovery");
        string explorer = Path.Combine(folder, "Microsoft", "Windows", "Explorer"); Directory.CreateDirectory(explorer);
        File.WriteAllText(Path.Combine(folder, "IconCache.db"), "sample");
        File.WriteAllText(Path.Combine(explorer, "iconcache_32.db"), "sample");
        File.WriteAllText(Path.Combine(explorer, "thumbcache_32.db"), "sample");
        File.WriteAllText(Path.Combine(explorer, "iconcache-not-a-cache.db"), "sample");
        var service = new CacheService(folder, Path.Combine(folder, "state"));
        Assert(service.GetCandidates().Count == 2, "Unexpected cache candidates");
        Assert(Directory.GetFiles(explorer).Length == 3, "Discovery changed files");
    }

    private static void BackupTraversal()
    {
        string folder = Folder("traversal");
        bool rejected = false;
        try { new ShortcutService(Path.Combine(folder, "state")).Restore(folder, Ignore); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "Outside backup directory accepted");
    }

    private sealed class FakeDesktop : IDesktopSession
    {
        public readonly List<string> Calls = new List<string>();
        public bool FailPause, FailStart, FailResume;
        public string Marker;
        public void StartRecovery(string marker) { Calls.Add("protect"); Marker = marker; if (FailStart) throw new IOException("Simulated recovery failure"); }
        public void PauseExplorer() { Calls.Add("pause"); if (FailPause) throw new IOException("Simulated pause failure"); }
        public void ResumeExplorer() { Calls.Add("resume"); if (FailResume) throw new IOException("Simulated resume failure"); }
        public void RefreshIcons() { Calls.Add("refresh"); }
    }

    private static void CacheRebuild()
    {
        string folder = Folder("rebuild"), path = Path.Combine(folder, "IconCache.db");
        File.WriteAllText(path, "icon cache sample");
        string explorer = Path.Combine(folder, "Microsoft", "Windows", "Explorer"); Directory.CreateDirectory(explorer);
        string thumbnail = Path.Combine(explorer, "thumbcache_32.db"); File.WriteAllText(thumbnail, "thumbnail sample");
        string hash = Paths.Hash(path), thumbnailHash = Paths.Hash(thumbnail);
        var session = new FakeDesktop();
        var result = new CacheService(folder, Path.Combine(folder, "state"), session).Rebuild(Ignore);
        Assert(result.Succeeded == 1 && result.Failed == 0 && !File.Exists(path), "Cache not rebuilt");
        Assert(Paths.Hash(Path.Combine(result.BackupDirectory, "000_IconCache.db")) == hash, "Cache backup not identical");
        Assert(Paths.Hash(thumbnail) == thumbnailHash, "Thumbnail changed");
        Assert(String.Join(",", session.Calls) == "protect,pause,resume,refresh", "Incorrect lifecycle order");
        Assert(!File.Exists(session.Marker), "Recovery marker not cleared");
    }

    private static void PauseFailure()
    {
        string folder = Folder("pause-failure"), path = Path.Combine(folder, "IconCache.db"); File.WriteAllText(path, "sample");
        var session = new FakeDesktop { FailPause = true };
        bool failedAsExpected = false;
        try { new CacheService(folder, Path.Combine(folder, "state"), session).Rebuild(Ignore); }
        catch (IOException) { failedAsExpected = true; }
        Assert(failedAsExpected && session.Calls.Contains("resume") && File.Exists(path), "Desktop not resumed after failure");
    }

    private static void RecoveryFailure()
    {
        string folder = Folder("protect-failure"), path = Path.Combine(folder, "IconCache.db"); File.WriteAllText(path, "sample");
        var session = new FakeDesktop { FailStart = true };
        bool failedAsExpected = false;
        try { new CacheService(folder, Path.Combine(folder, "state"), session).Rebuild(Ignore); }
        catch (IOException) { failedAsExpected = true; }
        Assert(failedAsExpected && !session.Calls.Contains("pause") && File.Exists(path), "Destructive operation despite missing recovery");
    }

    private static void BackupFailure()
    {
        string folder = Folder("cache-locked"), path = Path.Combine(folder, "IconCache.db"); File.WriteAllText(path, "sample");
        var session = new FakeDesktop();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = new CacheService(folder, Path.Combine(folder, "state"), session).Rebuild(Ignore);
            Assert(result.Failed == 1 && result.Succeeded == 0 && File.Exists(path) && session.Calls.Contains("resume"), "Locked file deleted or desktop not resumed");
        }
    }

    private static void ResumeFailure()
    {
        string folder = Folder("resume-failure"), path = Path.Combine(folder, "IconCache.db"); File.WriteAllText(path, "sample");
        var session = new FakeDesktop { FailResume = true };
        bool failedAsExpected = false;
        try { new CacheService(folder, Path.Combine(folder, "state"), session).Rebuild(Ignore); }
        catch (IOException) { failedAsExpected = true; }
        Assert(failedAsExpected && File.Exists(session.Marker), "Recovery protection cleared after failed resume");
    }

    private static void InstallerLink()
    {
        string folder = Folder("installer"), path = Broken(folder);
        byte[] bytes = File.ReadAllBytes(path);
        uint flags = BitConverter.ToUInt32(bytes, 20) | 0x1000;
        Array.Copy(BitConverter.GetBytes(flags), 0, bytes, 20, 4);
        File.WriteAllBytes(path, bytes);
        Assert(new ShortcutService(folder).Inspect(path, "测试").Finding == Finding.Special, "Installer shortcut altered");
    }

    private static void Preview()
    {
        using (var form = new MainForm(Path.Combine(root, "preview-state"), true))
        {
            PrepareControls(form);
            form.PerformLayout();
            if (visiblePreview) { form.Show(); Application.DoEvents(); }
            var grid = (DataGridView)GetField(form, "grid");
            grid.Rows[1].Cells[0].Value = true;
            grid.Rows[2].Cells[0].Value = true;
            if (visiblePreview) Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(root, "preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                bitmap.Save(Path.Combine(Path.GetDirectoryName(root), visiblePreview ? "preview.png" : "preview-headless.png"), System.Drawing.Imaging.ImageFormat.Png);
                Assert(bitmap.GetPixel(40, 240).B < 150, "Sidebar was not rendered");
                if (visiblePreview) Assert(bitmap.GetPixel(1000, 580).R == 255, "Grid was not rendered");
            }
        }
        Assert(!Directory.Exists(Path.Combine(root, "preview-state")), "Preview wrote user state");
    }

    private static void PackageIdentity()
    {
        Assert(PackageIcons.AppIdFromParsingPath("OpenAI.Codex_2p2nqsd0c76g0!App") == "OpenAI.Codex_2p2nqsd0c76g0!App", "Valid identity rejected");
        Assert(PackageIcons.AppIdFromParsingPath(@"::{4234d49b-0245-4df3-b780-3893943456e1}\Claude_pzs8sxrjxfjjc!Claude") == "Claude_pzs8sxrjxfjjc!Claude", "Qualified shell identity rejected");
        Assert(PackageIcons.AppIdFromParsingPath("https://example.com") == null, "URL accepted");
        Assert(PackageIcons.AppIdFromParsingPath("shell:RecycleBinFolder") == null, "Virtual system folder accepted");
        Assert(PackageIcons.AppIdFromParsingPath("App_pkg!../../evil") == null, "Invalid app identity accepted");
    }

    private static void PackageIconFile()
    {
        string folder = Folder("package-icon"), png = Path.Combine(folder, "source.png");
        using (var bitmap = new Bitmap(128, 128))
        using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.Blue); bitmap.Save(png); }
        string icon = PackageIcons.SaveStableIcon("Example_pkg!App", png, folder);
        Assert(Shell.HasIcon(icon), "Windows cannot extract generated icon");
        using (var reader = new BinaryReader(File.OpenRead(icon)))
        {
            Assert(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 10, "Missing ICO resolutions");
        }
        string hash = Paths.Hash(icon);
        Assert(PackageIcons.SaveStableIcon("Example_pkg!App", png, folder) == icon && Paths.Hash(icon) == hash, "Stable icon not reused");
    }

    private static void PackageLogoSelection()
    {
        string folder = Folder("package-assets"), assets = Path.Combine(folder, "Assets"); Directory.CreateDirectory(assets);
        using (var bitmap = new Bitmap(44, 44)) bitmap.Save(Path.Combine(assets, "Logo.png"));
        using (var bitmap = new Bitmap(256, 256)) bitmap.Save(Path.Combine(assets, "Logo.targetsize-256_altform-unplated.png"));
        using (var bitmap = new Bitmap(512, 512)) bitmap.Save(Path.Combine(assets, "Logo.targetsize-512_altform-lightunplated.png"));
        string selected = PackageIcons.FindLogoInPackage(folder, "App", @"Assets\Logo.png");
        Assert(Path.GetFileName(selected) == "Logo.targetsize-256_altform-unplated.png", "Wrong logo variant selected");
        Assert(PackageIcons.FindLogoInPackage(folder, "App", @"..\outside.png") == null, "Parent traversal accepted");
    }

    private static void PrepareControls(Control control)
    {
        IntPtr handle = control.Handle;
        control.PerformLayout();
        foreach (Control child in control.Controls) PrepareControls(child);
    }

    private static object GetField(object obj, string name)
    {
        return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    }

    private static void MultiSelection()
    {
        using (var form = new MainForm(Path.Combine(root, "selection-state"), true))
        {
            PrepareControls(form);
            var grid = (DataGridView)GetField(form, "grid");
            var all = (CheckBox)GetField(form, "selectAll");
            var button = (Button)GetField(form, "repairButton");
            Assert(grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[0] is DataGridViewCheckBoxCell), "Some rows have no checkbox");
            grid.Rows[0].Cells[0].Value = true;
            grid.Rows[1].Cells[0].Value = true;
            Assert((bool)grid.Rows[0].Cells[0].Value && (bool)grid.Rows[1].Cells[0].Value && button.Text.Contains("(2)"), "Checking the second row lost the first selection");
            Assert(all.CheckState == CheckState.Indeterminate, "Partial selection not reflected");
            var onClick = typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
            onClick.Invoke(all, new object[] { EventArgs.Empty });
            Assert(button.Text.Contains("(3)") && all.CheckState == CheckState.Checked, "Select-all from mixed state failed");
            foreach (DataGridViewRow row in grid.Rows)
                Assert((bool)row.Cells[0].Value == ((ShortcutItem)row.Tag).CanRepair, "Unavailable item selected by select-all");
            onClick.Invoke(all, new object[] { EventArgs.Empty });
            Assert(all.CheckState == CheckState.Unchecked && grid.Rows.Cast<DataGridViewRow>().All(r => !(bool)r.Cells[0].Value), "Clear all failed");
            Assert(grid.Rows.Cast<DataGridViewRow>().Where(r => !((ShortcutItem)r.Tag).CanRepair).All(r => r.Cells[0].ReadOnly), "Unavailable checkbox can be edited");
        }
    }

    private static void BatchRoundTrip()
    {
        string folder = Folder("batch");
        var service = new ShortcutService(Path.Combine(folder, "state"));
        var selected = new List<ShortcutItem>();
        var before = new Dictionary<string, string>();
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(folder, "App" + i + ".lnk");
            Shell.CreateLink(path, new LinkData { Target = exe, IconPath = Path.Combine(folder, "missing.ico"), Arguments = "--app " + i });
            before[path] = Paths.Hash(path);
            selected.Add(service.Inspect(path, "Batch test"));
        }
        var repaired = service.Repair(selected, Ignore);
        Assert(repaired.Succeeded == 3 && repaired.Failed == 0, "Batch did not repair all selections");
        var restored = service.Restore(repaired.BackupDirectory, Ignore);
        Assert(restored.Succeeded == 3 && restored.Failed == 0 && before.All(x => Paths.Hash(x.Key) == x.Value), "Batch restore not byte-identical");
    }

    private static void SystemLanguage()
    {
        foreach (string name in new[] { "zh-CN", "zh-SG", "zh-Hans" })
            Assert(UiText.ForCulture(CultureInfo.GetCultureInfo(name)) == AppLanguage.SimplifiedChinese, name);
        foreach (string name in new[] { "zh-TW", "zh-HK", "zh-MO", "zh-Hant" })
            Assert(UiText.ForCulture(CultureInfo.GetCultureInfo(name)) == AppLanguage.TraditionalChinese, name);
        foreach (string name in new[] { "en-US", "en-GB", "de-DE", "ja-JP" })
            Assert(UiText.ForCulture(CultureInfo.GetCultureInfo(name)) == AppLanguage.English, name);
    }

    private static void SavedLanguage()
    {
        AppLanguage original = UiText.Language;
        string folder = Path.Combine(Folder("language-settings"), "state");
        try
        {
            Assert(LanguageSettings.Load(folder, CultureInfo.GetCultureInfo("zh-TW")) == AppLanguage.TraditionalChinese && !Directory.Exists(folder), "First run wrote state or ignored Windows language");
            UiText.Language = AppLanguage.SimplifiedChinese;
            using (var form = new MainForm(folder, false))
            {
                PrepareControls(form);
                ((ComboBox)GetField(form, "languageSelector")).SelectedIndex = (int)AppLanguage.English;
                Assert(LanguageSettings.Load(folder, CultureInfo.GetCultureInfo("zh-CN")) == AppLanguage.English, "UI choice was not saved");
            }
            UiText.Language = LanguageSettings.Load(folder);
            using (var reopened = new MainForm(folder, false))
                Assert(((ComboBox)GetField(reopened, "languageSelector")).SelectedIndex == (int)AppLanguage.English, "Saved choice was not restored at startup");
            LanguageSettings.Save(folder, AppLanguage.TraditionalChinese);
            Assert(LanguageSettings.Load(folder, CultureInfo.GetCultureInfo("en-US")) == AppLanguage.TraditionalChinese, "Existing preference was not replaced");
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"language\":\"unknown\"}");
            Assert(LanguageSettings.Load(folder, CultureInfo.GetCultureInfo("zh-CN")) == AppLanguage.SimplifiedChinese, "Unknown preference did not fall back");
            File.WriteAllText(Path.Combine(folder, "settings.json"), "invalid JSON");
            Assert(LanguageSettings.Load(folder, CultureInfo.GetCultureInfo("en-US")) == AppLanguage.English, "Corrupt preference blocked startup");
        }
        finally { UiText.Language = original; }
    }

    private static void TranslationFormats()
    {
        AppLanguage original = UiText.Language;
        try
        {
            foreach (string key in UiText.Keys)
            {
                string expected = null;
                foreach (AppLanguage language in Enum.GetValues(typeof(AppLanguage)))
                {
                    UiText.Language = language;
                    string value = UiText.Get(key);
                    Assert(!String.IsNullOrWhiteSpace(value), "Missing translation: " + key);
                    string placeholders = String.Join(",", Regex.Matches(value, @"\{\d+\}").Cast<Match>().Select(m => m.Value).OrderBy(s => s));
                    if (expected == null) expected = placeholders;
                    Assert(placeholders == expected, "Formatting arguments differ: " + key + " / " + language);
                    UiText.Get(key, 0, 1, 2, 3);
                }
            }
        }
        finally { UiText.Language = original; }
    }

    private static void LiveLanguageSwitch()
    {
        AppLanguage original = UiText.Language;
        string state = Path.Combine(root, "language-preview-state");
        try
        {
            UiText.Language = AppLanguage.SimplifiedChinese;
            using (var form = new MainForm(state, true))
            {
                PrepareControls(form);
                var grid = (DataGridView)GetField(form, "grid");
                var filter = (CheckBox)GetField(form, "problemsOnly");
                var selector = (ComboBox)GetField(form, "languageSelector");
                filter.Checked = true;
                grid.Rows[0].Cells[0].Value = true;
                grid.Rows[1].Cells[0].Value = true;
                grid.CurrentCell = grid.Rows[1].Cells[1];
                string selectedPath = ((ShortcutItem)grid.Rows[1].Tag).Path;
                string log = ((TextBox)GetField(form, "logBox")).Text;
                int count = grid.Rows.Count;
                foreach (AppLanguage language in new[] { AppLanguage.English, AppLanguage.TraditionalChinese, AppLanguage.SimplifiedChinese })
                {
                    selector.SelectedIndex = (int)language;
                    Assert(UiText.Language == language && form.Text == UiText.Get("app.window"), "Window language did not change");
                    Assert(filter.Checked && grid.Rows.Count == count, "Filter changed during language switching");
                    Assert(grid.Rows.Cast<DataGridViewRow>().Count(r => (bool)r.Cells[0].Value) == 2, "Selection was lost");
                    Assert(((ShortcutItem)grid.CurrentRow.Tag).Path == selectedPath, "Focused shortcut changed");
                    Assert(((Button)GetField(form, "repairButton")).Text == UiText.Get("list.repairCount", 2), "Repair caption did not update");
                    Assert(grid.Columns[1].HeaderText == UiText.Get("column.name"), "Column caption did not update");
                    Assert((string)grid.Rows[0].Cells[2].Value == UiText.Get("status.missingIcon"), "Existing scan status did not update");
                    Assert((string)grid.Rows[0].Cells[3].Value == UiText.Get("location.personal"), "Existing scan location did not update");
                    Assert(grid.Rows[0].Cells[1].ToolTipText == UiText.Get("detail.missingIcon"), "Existing scan explanation did not update");
                    Assert(((TextBox)GetField(form, "logBox")).Text == log, "Historical log text changed");
                }
                Assert(!Directory.Exists(state), "Preview saved a real preference");
            }
        }
        finally { UiText.Language = original; }
    }

    private static void BusyLanguageSwitch()
    {
        AppLanguage original = UiText.Language;
        try
        {
            UiText.Language = AppLanguage.SimplifiedChinese;
            using (var form = new MainForm(Path.Combine(root, "language-busy-state"), true))
            {
                var selector = (ComboBox)GetField(form, "languageSelector");
                typeof(MainForm).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
                selector.SelectedIndex = (int)AppLanguage.English;
                Assert(UiText.Language == AppLanguage.SimplifiedChinese && selector.SelectedIndex == 0, "Language changed during an operation");
            }
        }
        finally { UiText.Language = original; }
    }

    private static void LocalizedDiagnostics()
    {
        AppLanguage original = UiText.Language;
        try
        {
            string folder = Folder("language-diagnostics"), path = Broken(folder);
            var service = new ShortcutService(Path.Combine(folder, "state"));
            UiText.Language = AppLanguage.SimplifiedChinese;
            var item = service.Inspect(path, "Test");
            string before = Paths.Hash(path);
            foreach (AppLanguage language in Enum.GetValues(typeof(AppLanguage)))
            {
                UiText.Language = language;
                Assert(item.Detail == UiText.Get("detail.missingIcon"), "Stored scan details kept the old language");
                Assert(Paths.Hash(path) == before, "Changing language edited a shortcut");
            }
            Shell.ChangeIcon(path, exe);
            var log = new List<string>();
            UiText.Language = AppLanguage.English;
            var result = service.Repair(new[] { item }, log.Add);
            Assert(result.Failed == 1 && log.Any(line => line.Contains(UiText.Get("error.changedAfterScan"))), "Repair error did not use English");
        }
        finally { UiText.Language = original; }
    }

    private static void LanguagePreviews()
    {
        AppLanguage original = UiText.Language;
        try
        {
            foreach (AppLanguage language in Enum.GetValues(typeof(AppLanguage)))
            {
                UiText.Language = language;
                using (var form = new MainForm(Path.Combine(root, "language-render-state"), true))
                {
                    PrepareControls(form);
                    if (visiblePreview) { form.Show(); Application.DoEvents(); }
                    var selector = (ComboBox)GetField(form, "languageSelector");
                    Assert(selector.Items.Count == 3 && selector.SelectedIndex == (int)language, "Language selector is incomplete");
                    Assert(selector.Width > 100 && selector.Height > 20, "Language selector is clipped");
                    var grid = (DataGridView)GetField(form, "grid");
                    grid.Rows[0].Cells[0].Value = true;
                    grid.Rows[1].Cells[0].Value = true;
                    SaveLanguagePreview(form, "preview-" + UiText.Code(language) + ".png");
                    if (language == AppLanguage.English)
                    {
                        form.Size = form.MinimumSize;
                        form.PerformLayout();
                        if (visiblePreview) Application.DoEvents();
                        SaveLanguagePreview(form, "preview-en-small.png");
                    }
                }
            }
        }
        finally { UiText.Language = original; }
    }

    private static void SaveLanguagePreview(Form form, string filename)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(root, filename), System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
