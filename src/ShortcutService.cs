// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Sulock

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
                new { Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LabelKey = "location.personal" },
                new { Path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), LabelKey = "location.public" }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (String.IsNullOrEmpty(root.Path) || !seen.Add(root.Path)) continue;
                if (Paths.IsRemote(root.Path)) { log(UiText.Get("scan.remoteDesktop", UiText.Get(root.LabelKey))); continue; }
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root.Path))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) { var item = Inspect(file, UiText.Get(root.LabelKey)); item.LocationKey = root.LabelKey; result.Add(item); }
                        else if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                            result.Add(new ShortcutItem { Path = file, LocationKey = root.LabelKey, Finding = Finding.Special,
                                DetailKey = "detail.web" });
                    }
                }
                catch (Exception ex) { log(UiText.Get("scan.failedDesktop", UiText.Get(root.LabelKey), ex.Message)); }
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
                    item.DetailKey = "detail.remoteShortcut";
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
                            item.DetailKey = "detail.installer";
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
                            item.DetailKey = "detail.pinned";
                        }
                        else
                        {
                            item.PackagedIconSource = PackageIcons.FindInstalledLogo(item.PackagedAppId);
                            item.Finding = item.PackagedIconSource == null ? Finding.Special : Finding.PackagedIcon;
                            item.DetailKey = item.PackagedIconSource == null ? "detail.noPackageIcon" : "detail.packageIcon";
                        }
                    }
                    else
                    {
                        item.Finding = Finding.Special;
                        item.DetailKey = "detail.special";
                    }
                }
                else if (Paths.IsRemote(item.Target) || (!String.IsNullOrEmpty(item.IconPath) && Paths.IsRemote(item.IconPath)))
                {
                    item.Finding = Finding.Unavailable;
                    item.DetailKey = "detail.remoteTarget";
                }
                else if (!File.Exists(item.Target) && !Directory.Exists(item.Target))
                {
                    item.Finding = Finding.MissingTarget;
                    item.DetailKey = "detail.missingTarget";
                }
                else if (!String.IsNullOrEmpty(item.IconPath) && !Path.IsPathRooted(item.IconPath))
                {
                    item.Finding = Finding.Special;
                    item.DetailKey = "detail.relativeIcon";
                }
                else if (!String.IsNullOrEmpty(item.IconPath) && !File.Exists(item.IconPath))
                {
                    if (Path.GetExtension(item.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase) && Shell.HasIcon(item.Target))
                    {
                        item.Finding = Finding.MissingIcon;
                        item.DetailKey = "detail.missingIcon";
                    }
                    else
                    {
                        item.Finding = Finding.Special;
                        item.DetailKey = "detail.noIcon";
                    }
                }
                else
                {
                    item.Finding = Finding.Healthy;
                    item.DetailKey = "detail.healthy";
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
                    if (!item.CanRepair) throw new IOException(UiText.Get("error.unsupportedRepair"));
                    ShortcutItem current = Inspect(item.Path, item.Location);
                    if (!current.CanRepair || current.SourceHash != item.SourceHash)
                        throw new IOException(UiText.Get("error.changedAfterScan"));
                    string backupName = manifest.Entries.Count.ToString("D4") + ".lnk";
                    string backupPath = Path.Combine(folder, backupName);
                    File.Copy(item.Path, backupPath, false);
                    if (Paths.Hash(backupPath) != item.SourceHash) throw new IOException(UiText.Get("error.backupHash"));
                    entry = new BackupEntry { OriginalPath = Path.GetFullPath(item.Path), BackupFile = backupName, BeforeHash = item.SourceHash };
                    manifest.Entries.Add(entry);
                    SaveManifest(folder, manifest);
                    if (Paths.Hash(item.Path) != item.SourceHash) throw new IOException(UiText.Get("error.changedAfterBackup"));
                    LinkData before = Shell.ReadLink(item.Path);
                    string iconPath = current.Finding == Finding.PackagedIcon
                        ? PackageIcons.SaveStableIcon(current.PackagedAppId, current.PackagedIconSource, stateRoot)
                        : current.Target;
                    if (Paths.Hash(item.Path) != item.SourceHash) throw new IOException(UiText.Get("error.changedDuringIcon"));
                    attemptedWrite = true;
                    Shell.ChangeIcon(item.Path, iconPath);
                    LinkData after = Shell.ReadLink(item.Path);
                    if (!Paths.Same(Paths.Expand(after.IconPath), iconPath) || after.IconIndex != 0)
                        throw new IOException(UiText.Get("error.writtenIcon"));
                    if (before.Target != after.Target || before.Arguments != after.Arguments ||
                        before.WorkingDirectory != after.WorkingDirectory || before.Description != after.Description ||
                        before.IdList != after.IdList || before.ParsingPath != after.ParsingPath)
                        throw new IOException(UiText.Get("error.metadataChanged"));
                    entry.AfterHash = Paths.Hash(item.Path);
                    SaveManifest(folder, manifest);
                    result.Succeeded++;
                    Shell.NotifyFile(item.Path);
                    log(UiText.Get("repair.itemDone", item.Name));
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
                            log(UiText.Get("repair.rolledBack", item.Name));
                        }
                        catch (Exception rollback) { log(UiText.Get("repair.rollbackFailed", folder, rollback.Message)); }
                    }
                    log(UiText.Get("repair.itemFailed", item.Name, ex.Message));
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
                throw new IOException(UiText.Get("error.backupOutside"));
            Paths.RejectReparsePoints(folder);
            BackupManifest manifest = ReadManifest(folder);
            var result = new OperationResult { BackupDirectory = folder };
            foreach (BackupEntry entry in manifest.Entries.Where(e => !e.Restored && !String.IsNullOrEmpty(e.AfterHash)))
            {
                try
                {
                    if (Path.GetFileName(entry.BackupFile) != entry.BackupFile || !entry.BackupFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        throw new IOException(UiText.Get("error.backupFilename"));
                    if (!entry.OriginalPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || Paths.IsRemote(entry.OriginalPath))
                        throw new IOException(UiText.Get("error.originalPath"));
                    Paths.RejectReparsePoints(entry.OriginalPath);
                    string source = Path.Combine(folder, entry.BackupFile);
                    Paths.RejectReparsePoints(source);
                    if (Paths.Hash(source) != entry.BeforeHash) throw new IOException(UiText.Get("error.backupChanged"));
                    if (!File.Exists(entry.OriginalPath) || Paths.Hash(entry.OriginalPath) != entry.AfterHash)
                        throw new IOException(UiText.Get("error.restoreConflict"));
                    File.Copy(source, entry.OriginalPath, true);
                    entry.Restored = true;
                    SaveManifest(folder, manifest);
                    result.Succeeded++;
                    Shell.NotifyFile(entry.OriginalPath);
                    log(UiText.Get("restore.itemDone", Path.GetFileNameWithoutExtension(entry.OriginalPath)));
                }
                catch (Exception ex) { result.Failed++; log(UiText.Get("restore.itemFailed", Path.GetFileName(entry.OriginalPath), ex.Message)); }
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
