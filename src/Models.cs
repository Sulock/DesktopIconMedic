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
        private string location, detail;
        public string LocationKey { get; set; }
        public string DetailKey { get; set; }
        public string Location
        {
            get { return LocationKey == null ? location : UiText.Get(LocationKey); }
            set { location = value; LocationKey = null; }
        }
        public string Target { get; set; }
        public string IconPath { get; set; }
        public int IconIndex { get; set; }
        public Finding Finding { get; set; }
        public string Detail
        {
            get { return DetailKey == null ? detail : UiText.Get(DetailKey); }
            set { detail = value; DetailKey = null; }
        }
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
                    case Finding.Healthy: return UiText.Get("status.healthy");
                    case Finding.MissingIcon: return UiText.Get("status.missingIcon");
                    case Finding.PackagedIcon: return UiText.Get("status.packaged");
                    case Finding.MissingTarget: return UiText.Get("status.missingTarget");
                    case Finding.Special: return UiText.Get("status.special");
                    case Finding.Unavailable: return UiText.Get("status.remote");
                    default: return UiText.Get("status.error");
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
                    throw new IOException(UiText.Get("error.reparse", current));
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
