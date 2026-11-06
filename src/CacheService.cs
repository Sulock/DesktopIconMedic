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
                log("未发现可重建的图标缓存文件，已发送图标刷新请求。");
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
                log("正在暂时关闭当前登录会话的资源管理器……");
                desktop.PauseExplorer();
                for (int i = 0; i < candidates.Count; i++)
                {
                    string path = candidates[i];
                    try
                    {
                        // Revalidate the exact filename and all path components immediately before deletion.
                        if (!IsAllowedCache(path)) throw new IOException("路径不在允许的缓存范围内。");
                        Paths.RejectReparsePoints(path);
                        if (!File.Exists(path)) { log("缓存已由系统更新：" + Path.GetFileName(path)); continue; }
                        string saved = Path.Combine(folder, i.ToString("D3") + "_" + Path.GetFileName(path));
                        File.Copy(path, saved, false);
                        if (Paths.Hash(path) != Paths.Hash(saved)) throw new IOException("缓存备份校验失败。");
                        File.AppendAllText(Path.Combine(folder, "files.txt"), saved + "\t" + path + Environment.NewLine);
                        File.Delete(path);
                        result.Succeeded++;
                        log("已移除缓存，等待系统重建：" + Path.GetFileName(path));
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        log("保留 " + Path.GetFileName(path) + "：" + ex.Message);
                    }
                }
            }
            finally
            {
                bool desktopReady = false;
                if (watchdogStarted)
                {
                    try { desktop.ResumeExplorer(); desktopReady = true; log("资源管理器已运行；图标会逐步重新加载。"); }
                    catch (Exception ex) { throw new IOException("自动恢复桌面失败：" + ex.Message + "。恢复保护会继续尝试。也可用 Ctrl + Shift + Esc 打开任务管理器，运行 explorer.exe。", ex); }
                }
                if (!watchdogStarted || desktopReady)
                {
                    try { File.Delete(marker); } catch { }
                }
            }
            desktop.RefreshIcons();
            if (result.Failed > 0) log("部分缓存仍被系统占用。如图标未恢复，请重启电脑后再试。");
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
            throw new IOException("尚未检测到资源管理器进程。");
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
                    if (recovery == null || recovery.WaitForExit(250)) throw new IOException("桌面恢复保护未能启动，已取消重建。");
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
                            if (!process.WaitForExit(5000)) throw new IOException("资源管理器未及时退出。");
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
