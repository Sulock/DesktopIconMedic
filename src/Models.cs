using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace DesktopIconMedic
{
    public enum Finding { Healthy, MissingIcon, MissingTarget, Special, Unavailable, Error, PackagedIcon }

    public sealed class ShortcutItem
    {
        public string Path { get; set; }
        public string Name { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }
        public string Location { get; set; }
        public string Target { get; set; }
        public string IconPath { get; set; }
        public int IconIndex { get; set; }
        public Finding Finding { get; set; }
        public string Detail { get; set; }
        public string SourceHash { get; set; }
        public string PackagedAppId { get; set; }
        public string PackagedIconSource { get; set; }
        public bool CanRepair { get { return Finding == Finding.MissingIcon || Finding == Finding.PackagedIcon; } }
        public string Status
        {
            get
            {
                switch (Finding)
                {
                    case Finding.Healthy: return "路径正常";
                    case Finding.MissingIcon: return "图标来源失效";
                    case Finding.PackagedIcon: return "可固定商店图标";
                    case Finding.MissingTarget: return "目标不可用";
                    case Finding.Special: return "特殊快捷方式";
                    case Finding.Unavailable: return "需联网检查";
                    default: return "无法读取";
                }
            }
        }
    }

    public sealed class OperationResult
    {
        public int Succeeded;
        public int Failed;
        public string BackupDirectory;
        public readonly List<string> Messages = new List<string>();
    }

    [DataContract]
    public sealed class BackupManifest
    {
        [DataMember] public string CreatedUtc;
        [DataMember] public List<BackupEntry> Entries = new List<BackupEntry>();
    }

    [DataContract]
    public sealed class BackupEntry
    {
        [DataMember] public string OriginalPath;
        [DataMember] public string BackupFile;
        [DataMember] public string BeforeHash;
        [DataMember] public string AfterHash;
        [DataMember] public bool Restored;
    }

    public static class Paths
    {
        public static string Expand(string value)
        {
            return Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"'));
        }

        public static bool IsRemote(string value)
        {
            value = Expand(value);
            if (value.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                if (System.IO.Path.IsPathRooted(value))
                    return new DriveInfo(System.IO.Path.GetPathRoot(value)).DriveType == DriveType.Network;
            }
            catch { return true; }
            return false;
        }

        public static bool Same(string a, string b)
        {
            return String.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'),
                System.IO.Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static void RejectReparsePoints(string path)
        {
            string current = System.IO.Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径含链接或重解析点，已跳过：" + current);
                current = System.IO.Path.GetDirectoryName(current);
            }
        }

        public static string Hash(string path)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
    }
}
