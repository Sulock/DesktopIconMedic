using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DesktopIconMedic
{
    public interface IDesktopSession
    {
        void StartRecovery(string marker);
        void PauseExplorer();
        void ResumeExplorer();
        void RefreshIcons();
    }

    public sealed class CacheService
    {
        private readonly string localRoot;
        private readonly ShortcutService backups;
        private readonly IDesktopSession desktop;
        private static readonly Regex CacheName = new Regex(@"^iconcache(?:_[a-zA-Z0-9_-]+)?\.db$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public CacheService(string localAppData, string stateRoot) : this(localAppData, stateRoot, new WindowsDesktopSession()) { }

        public CacheService(string localAppData, string stateRoot, IDesktopSession desktop)
        {
            localRoot = Path.GetFullPath(localAppData);
            backups = new ShortcutService(stateRoot);
            this.desktop = desktop;
        }

        public bool IsAllowedCache(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (Paths.Same(full, Path.Combine(localRoot, "IconCache.db"))) return true;
                return Paths.Same(Path.GetDirectoryName(full), Path.Combine(localRoot, "Microsoft", "Windows", "Explorer")) &&
                    CacheName.IsMatch(Path.GetFileName(full));
            }
            catch { return false; }
        }

        public List<string> GetCandidates()
        {
            var result = new List<string>();
            Paths.RejectReparsePoints(localRoot);
            string legacy = Path.Combine(localRoot, "IconCache.db");
            if (File.Exists(legacy)) result.Add(legacy);
            string explorer = Path.Combine(localRoot, "Microsoft", "Windows", "Explorer");
            Paths.RejectReparsePoints(explorer);
            if (Directory.Exists(explorer)) result.AddRange(Directory.GetFiles(explorer, "iconcache*.db").Where(IsAllowedCache));
            return result;
        }

        public OperationResult Rebuild(Action<string> log)
        {
            var result = new OperationResult();
            List<string> candidates = GetCandidates();
            if (candidates.Count == 0)
            {
                log(UiText.Get("cache.none"));
                desktop.RefreshIcons();
                return result;
            }
            string folder = backups.NewBackupFolder("cache");
            result.BackupDirectory = folder;
            string marker = Path.Combine(folder, "recovery.pending");
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            bool watchdogStarted = false;
            try
            {
                // Launch recovery before stopping Explorer so an unexpected app exit cannot strand the desktop.
                desktop.StartRecovery(marker);
                watchdogStarted = true;
                log(UiText.Get("cache.pause"));
                desktop.PauseExplorer();
                for (int i = 0; i < candidates.Count; i++)
                {
                    string path = candidates[i];
                    try
                    {
                        // Revalidate the exact filename and all path components immediately before deletion.
                        if (!IsAllowedCache(path)) throw new IOException(UiText.Get("error.cacheOutside"));
                        Paths.RejectReparsePoints(path);
                        if (!File.Exists(path)) { log(UiText.Get("cache.alreadyChanged", Path.GetFileName(path))); continue; }
                        string saved = Path.Combine(folder, i.ToString("D3") + "_" + Path.GetFileName(path));
                        File.Copy(path, saved, false);
                        if (Paths.Hash(path) != Paths.Hash(saved)) throw new IOException(UiText.Get("error.cacheBackup"));
                        File.AppendAllText(Path.Combine(folder, "files.txt"), saved + "\t" + path + Environment.NewLine);
                        File.Delete(path);
                        result.Succeeded++;
                        log(UiText.Get("cache.removed", Path.GetFileName(path)));
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        log(UiText.Get("cache.kept", Path.GetFileName(path), ex.Message));
                    }
                }
            }
            finally
            {
                bool desktopReady = false;
                if (watchdogStarted)
                {
                    try { desktop.ResumeExplorer(); desktopReady = true; log(UiText.Get("cache.resumed")); }
                    catch (Exception ex) { throw new IOException(UiText.Get("cache.resumeFailed", ex.Message), ex); }
                }
                if (!watchdogStarted || desktopReady)
                {
                    try { File.Delete(marker); } catch { }
                }
            }
            desktop.RefreshIcons();
            if (result.Failed > 0) log(UiText.Get("cache.partial"));
            return result;
        }

        private static List<Process> ExplorerProcesses()
        {
            int session = Process.GetCurrentProcess().SessionId;
            var result = new List<Process>();
            foreach (Process process in Process.GetProcessesByName("explorer"))
            {
                try { if (process.SessionId == session) { result.Add(process); continue; } }
                catch { }
                process.Dispose();
            }
            return result;
        }

        public static void EnsureExplorer()
        {
            List<Process> running = ExplorerProcesses();
            bool exists = running.Count > 0;
            foreach (Process p in running) p.Dispose();
            if (exists) return;
            Process.Start(new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = true
            });
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                running = ExplorerProcesses();
                exists = running.Count > 0;
                foreach (Process p in running) p.Dispose();
                if (exists) return;
            }
            throw new IOException(UiText.Get("error.explorerMissing"));
        }

        public static void RunRecovery(int parentId, string marker, string stateRoot)
        {
            string parentDir = Path.GetDirectoryName(Path.GetFullPath(marker));
            if (Path.GetFileName(marker) != "recovery.pending" || !Path.GetFileName(parentDir).StartsWith("cache_", StringComparison.Ordinal) ||
                !Paths.Same(Path.GetDirectoryName(parentDir), Path.Combine(stateRoot, "Backups"))) return;
            Paths.RejectReparsePoints(marker);
            for (int i = 0; i < 60 && File.Exists(marker); i++)
            {
                try { using (Process parent = Process.GetProcessById(parentId)) { if (parent.HasExited) break; } }
                catch (ArgumentException) { break; }
                Thread.Sleep(1000);
            }
            if (!File.Exists(marker)) return;
            for (int i = 0; i < 3; i++)
            {
                try { EnsureExplorer(); File.Delete(marker); return; }
                catch { Thread.Sleep(1000); }
            }
        }

        private sealed class WindowsDesktopSession : IDesktopSession
        {
            public void StartRecovery(string marker)
            {
                using (Process recovery = Process.Start(new ProcessStartInfo {
                    FileName = System.Windows.Forms.Application.ExecutablePath,
                    Arguments = "--recover-explorer " + Process.GetCurrentProcess().Id + " \"" + marker + "\"",
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (recovery == null || recovery.WaitForExit(250)) throw new IOException(UiText.Get("error.recoveryStart"));
                }
            }

            public void PauseExplorer()
            {
                foreach (Process process in ExplorerProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            process.Kill();
                            if (!process.WaitForExit(5000)) throw new IOException(UiText.Get("error.explorerExit"));
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }

            public void ResumeExplorer() { EnsureExplorer(); }
            public void RefreshIcons() { Shell.RefreshIcons(); }
        }
    }
}
