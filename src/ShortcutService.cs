using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace DesktopIconMedic
{
    public sealed class ShortcutService
    {
        private readonly string backupRoot;
        private readonly string stateRoot;
        public ShortcutService(string stateRoot) { this.stateRoot = stateRoot; backupRoot = Path.Combine(stateRoot, "Backups"); }

        public List<ShortcutItem> ScanDesktop(Action<string> log)
        {
            var result = new List<ShortcutItem>();
            var roots = new[] {
                new { Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Label = "个人桌面" },
                new { Path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Label = "公共桌面" }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (String.IsNullOrEmpty(root.Path) || !seen.Add(root.Path)) continue;
                if (Paths.IsRemote(root.Path)) { log(root.Label + "位于网络位置，已跳过。"); continue; }
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root.Path))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) result.Add(Inspect(file, root.Label));
                        else if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                            result.Add(new ShortcutItem { Path = file, Location = root.Label, Finding = Finding.Special,
                                Detail = "网页或游戏启动链接。可尝试刷新图标；本工具不会改写此类链接。" });
                    }
                }
                catch (Exception ex) { log(root.Label + "扫描未完成：" + ex.Message); }
            }
            return result.OrderByDescending(x => x.CanRepair).ThenBy(x => x.Finding == Finding.Healthy).ThenBy(x => x.Name).ToList();
        }

        public ShortcutItem Inspect(string file, string location)
        {
            var item = new ShortcutItem { Path = file, Location = location };
            try
            {
                if (Paths.IsRemote(file))
                {
                    item.Finding = Finding.Unavailable;
                    item.Detail = "快捷方式位于网络位置，未访问。";
                    return item;
                }
                // A reparse point may be cloud-only or redirect elsewhere. Do not hydrate or edit it.
                Paths.RejectReparsePoints(file);
                using (var stream = File.OpenRead(file))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length >= 24)
                    {
                        stream.Position = 20;
                        if ((reader.ReadUInt32() & 0x1000) != 0)
                        {
                            item.Finding = Finding.Special;
                            item.Detail = "Windows Installer 管理的快捷方式，保留其安装器信息；可尝试刷新图标。";
                            return item;
                        }
                    }
                }
                LinkData data = Shell.ReadLink(file);
                item.Target = Paths.Expand(data.Target);
                item.IconPath = Paths.Expand(data.IconPath);
                item.IconIndex = data.IconIndex;
                item.SourceHash = Paths.Hash(file);
                if (String.IsNullOrWhiteSpace(item.Target) || !Path.IsPathRooted(item.Target))
                {
                    item.PackagedAppId = PackageIcons.AppIdFromParsingPath(data.ParsingPath);
                    if (item.PackagedAppId != null)
                    {
                        item.Target = item.PackagedAppId;
                        bool validLocalIcon = !String.IsNullOrWhiteSpace(item.IconPath) && Path.IsPathRooted(item.IconPath) && !Paths.IsRemote(item.IconPath) && File.Exists(item.IconPath);
                        if (validLocalIcon)
                        {
                            item.Finding = Finding.Healthy;
                            item.Detail = "商店应用使用独立的本地图标。原应用启动标识保留；如显示异常，可先刷新图标。";
                        }
                        else
                        {
                            item.PackagedIconSource = PackageIcons.FindInstalledLogo(item.PackagedAppId);
                            item.Finding = item.PackagedIconSource == null ? Finding.Special : Finding.PackagedIcon;
                            item.Detail = item.PackagedIconSource == null ? "已识别商店应用，但未找到可读取的已安装图标。请确认应用仍安装在当前账户。" :
                                "商店应用使用系统提供的图标。如仍显示白纸，可勾选修复：提取已安装应用的原图标并固定为本地文件，保留原启动方式。此状态不表示一定存在故障。";
                        }
                    }
                    else
                    {
                        item.Finding = Finding.Special;
                        item.Detail = "系统或其他特殊链接；无法按普通文件路径判断，可尝试刷新图标。";
                    }
                }
                else if (Paths.IsRemote(item.Target) || (!String.IsNullOrEmpty(item.IconPath) && Paths.IsRemote(item.IconPath)))
                {
                    item.Finding = Finding.Unavailable;
                    item.Detail = "目标或图标位于网络位置，请连接网络后确认。本工具未访问该位置。";
                }
                else if (!File.Exists(item.Target) && !Directory.Exists(item.Target))
                {
                    item.Finding = Finding.MissingTarget;
                    item.Detail = "目标文件当前不可用。请确认软件未被移动或卸载，或对应磁盘 / 云文件已就绪，再重新创建快捷方式。";
                }
                else if (!String.IsNullOrEmpty(item.IconPath) && !Path.IsPathRooted(item.IconPath))
                {
                    item.Finding = Finding.Special;
                    item.Detail = "图标使用相对路径，需人工确认；可先尝试刷新图标。";
                }
                else if (!String.IsNullOrEmpty(item.IconPath) && !File.Exists(item.IconPath))
                {
                    if (Path.GetExtension(item.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase) && Shell.HasIcon(item.Target))
                    {
                        item.Finding = Finding.MissingIcon;
                        item.Detail = "原图标文件不存在，可改用启动程序自身的图标。修复前会备份快捷方式。";
                    }
                    else
                    {
                        item.Finding = Finding.Special;
                        item.Detail = "原图标文件不存在，且无法确认可用的程序图标。请重新创建快捷方式或手动选择图标。";
                    }
                }
                else
                {
                    item.Finding = Finding.Healthy;
                    item.Detail = "目标和图标文件路径未发现异常。如仍显示白纸，可尝试刷新图标或重建缓存；路径正常不代表图标已正确显示。";
                }
            }
            catch (Exception ex) { item.Finding = Finding.Error; item.Detail = ex.Message; }
            return item;
        }

        public OperationResult Repair(IEnumerable<ShortcutItem> selected, Action<string> log)
        {
            var result = new OperationResult();
            string folder = NewBackupFolder("shortcuts");
            result.BackupDirectory = folder;
            var manifest = new BackupManifest { CreatedUtc = DateTime.UtcNow.ToString("o") };
            SaveManifest(folder, manifest);
            foreach (ShortcutItem item in selected)
            {
                BackupEntry entry = null;
                bool attemptedWrite = false;
                try
                {
                    if (!item.CanRepair) throw new IOException("该项目不适合自动修复。");
                    ShortcutItem current = Inspect(item.Path, item.Location);
                    if (!current.CanRepair || current.SourceHash != item.SourceHash)
                        throw new IOException("快捷方式在扫描后发生变化，请重新扫描。");
                    string backupName = manifest.Entries.Count.ToString("D4") + ".lnk";
                    string backupPath = Path.Combine(folder, backupName);
                    File.Copy(item.Path, backupPath, false);
                    if (Paths.Hash(backupPath) != item.SourceHash) throw new IOException("备份校验未通过。");
                    entry = new BackupEntry { OriginalPath = Path.GetFullPath(item.Path), BackupFile = backupName, BeforeHash = item.SourceHash };
                    manifest.Entries.Add(entry);
                    SaveManifest(folder, manifest);
                    if (Paths.Hash(item.Path) != item.SourceHash) throw new IOException("快捷方式在备份后发生变化，请重新扫描。");
                    LinkData before = Shell.ReadLink(item.Path);
                    string iconPath = current.Finding == Finding.PackagedIcon
                        ? PackageIcons.SaveStableIcon(current.PackagedAppId, current.PackagedIconSource, stateRoot)
                        : current.Target;
                    if (Paths.Hash(item.Path) != item.SourceHash) throw new IOException("准备图标期间快捷方式发生变化，请重新扫描。");
                    attemptedWrite = true;
                    Shell.ChangeIcon(item.Path, iconPath);
                    LinkData after = Shell.ReadLink(item.Path);
                    if (!Paths.Same(Paths.Expand(after.IconPath), iconPath) || after.IconIndex != 0)
                        throw new IOException("写入后的图标路径校验未通过。");
                    if (before.Target != after.Target || before.Arguments != after.Arguments ||
                        before.WorkingDirectory != after.WorkingDirectory || before.Description != after.Description ||
                        before.IdList != after.IdList || before.ParsingPath != after.ParsingPath)
                        throw new IOException("快捷方式的其他信息发生变化，已取消修复。");
                    entry.AfterHash = Paths.Hash(item.Path);
                    SaveManifest(folder, manifest);
                    result.Succeeded++;
                    Shell.NotifyFile(item.Path);
                    log("已修复：" + item.Name);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    if (attemptedWrite && entry != null)
                    {
                        try
                        {
                            File.Copy(Path.Combine(folder, entry.BackupFile), item.Path, true);
                            entry.Restored = true;
                            SaveManifest(folder, manifest);
                            log("已回退未完成的修改：" + item.Name);
                        }
                        catch (Exception rollback) { log("自动回退失败，原文件保存在 " + folder + "；" + rollback.Message); }
                    }
                    log("未修复 " + item.Name + "：" + ex.Message);
                }
            }
            return result;
        }

        public List<string> ListRestorableBackups()
        {
            if (!Directory.Exists(backupRoot)) return new List<string>();
            var folders = new List<string>();
            foreach (string folder in Directory.GetDirectories(backupRoot, "shortcuts_*"))
            {
                try { if (ReadManifest(folder).Entries.Any(e => !e.Restored && !String.IsNullOrEmpty(e.AfterHash))) folders.Add(folder); }
                catch { }
            }
            return folders.OrderByDescending(x => x).ToList();
        }

        public OperationResult Restore(string folder, Action<string> log)
        {
            if (!Paths.Same(Path.GetDirectoryName(Path.GetFullPath(folder)), backupRoot))
                throw new IOException("备份目录不属于本工具。");
            Paths.RejectReparsePoints(folder);
            BackupManifest manifest = ReadManifest(folder);
            var result = new OperationResult { BackupDirectory = folder };
            foreach (BackupEntry entry in manifest.Entries.Where(e => !e.Restored && !String.IsNullOrEmpty(e.AfterHash)))
            {
                try
                {
                    if (Path.GetFileName(entry.BackupFile) != entry.BackupFile || !entry.BackupFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("备份文件名无效。");
                    if (!entry.OriginalPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || Paths.IsRemote(entry.OriginalPath))
                        throw new IOException("原快捷方式路径无效。");
                    Paths.RejectReparsePoints(entry.OriginalPath);
                    string source = Path.Combine(folder, entry.BackupFile);
                    Paths.RejectReparsePoints(source);
                    if (Paths.Hash(source) != entry.BeforeHash) throw new IOException("备份内容已改变，未恢复。");
                    if (!File.Exists(entry.OriginalPath) || Paths.Hash(entry.OriginalPath) != entry.AfterHash)
                        throw new IOException("当前快捷方式已被修改或删除，为避免覆盖你的更改，已跳过。");
                    File.Copy(source, entry.OriginalPath, true);
                    entry.Restored = true;
                    SaveManifest(folder, manifest);
                    result.Succeeded++;
                    Shell.NotifyFile(entry.OriginalPath);
                    log("已恢复：" + Path.GetFileNameWithoutExtension(entry.OriginalPath));
                }
                catch (Exception ex) { result.Failed++; log("未恢复 " + Path.GetFileName(entry.OriginalPath) + "：" + ex.Message); }
            }
            return result;
        }

        public string NewBackupFolder(string prefix)
        {
            Paths.RejectReparsePoints(backupRoot);
            string folder = Path.Combine(backupRoot, prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void SaveManifest(string folder, BackupManifest manifest)
        {
            string path = Path.Combine(folder, "manifest.json");
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(BackupManifest)).WriteObject(stream, manifest);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static BackupManifest ReadManifest(string folder)
        {
            using (var stream = File.OpenRead(Path.Combine(folder, "manifest.json")))
                return (BackupManifest)new DataContractJsonSerializer(typeof(BackupManifest)).ReadObject(stream);
        }
    }
}
