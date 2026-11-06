using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;

namespace DesktopIconMedic
{
    public static class PackageIcons
    {
        public static string AppIdFromParsingPath(string parsingPath)
        {
            if (String.IsNullOrEmpty(parsingPath)) return null;
            string id = parsingPath.Substring(parsingPath.LastIndexOf('\\') + 1);
            return Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]*_[A-Za-z0-9]+![A-Za-z0-9._-]+$") ? id : null;
        }

        public static string FindInstalledLogo(string appId)
        {
            if (AppIdFromParsingPath(appId) != appId) return null;
            object shell = null, folder = null, item = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
                if (folder == null) return null;
                item = ((dynamic)folder).ParseName(appId);
                if (item == null || !String.Equals((string)((dynamic)item).Path, appId, StringComparison.OrdinalIgnoreCase)) return null;
                string installed = (string)((dynamic)item).ExtendedProperty("System.AppUserModel.PackageInstallPath");
                string smallLogo = (string)((dynamic)item).ExtendedProperty("System.Tile.SmallLogoPath");
                if (String.IsNullOrWhiteSpace(installed) || !Path.IsPathRooted(installed) || Paths.IsRemote(installed)) return null;
                return FindLogoInPackage(installed, appId.Substring(appId.IndexOf('!') + 1), smallLogo);
            }
            catch { return null; }
            finally
            {
                foreach (object obj in new[] { item, folder, shell })
                    if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
            }
        }

        public static string FindLogoInPackage(string installed, string applicationId, string smallLogo)
        {
            Paths.RejectReparsePoints(installed);
            var logos = new List<string>();
            if (!String.IsNullOrWhiteSpace(smallLogo)) logos.Add(smallLogo);
            string manifest = Path.Combine(installed, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                foreach (XmlNode app in doc.SelectNodes("//*[local-name()='Applications']/*[local-name()='Application']"))
                {
                    if (app.Attributes["Id"] == null || app.Attributes["Id"].Value != applicationId) continue;
                    XmlNode visual = app.SelectSingleNode("*[local-name()='VisualElements']");
                    if (visual == null) continue;
                    foreach (string attr in new[] { "Square44x44Logo", "Square150x150Logo" })
                        if (visual.Attributes[attr] != null) logos.Add(visual.Attributes[attr].Value);
                }
            }
            var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string logo in logos.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(logo) || logo.Contains(":") || logo.StartsWith("/")) continue;
                string path = Path.GetFullPath(Path.Combine(installed, logo.Replace('/', '\\')));
                string root = Path.GetFullPath(installed).TrimEnd('\\') + "\\";
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                string parent = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path);
                if (!Directory.Exists(parent)) continue;
                Paths.RejectReparsePoints(parent);
                foreach (string file in Directory.GetFiles(parent, stem + "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name != stem && !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    // Avoid white-only and high-contrast variants that disappear on a light desktop.
                    if (name.IndexOf("contrast-", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("lightunplated", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    Paths.RejectReparsePoints(file);
                    try
                    {
                        using (Image image = Image.FromFile(file))
                        {
                            int score = Math.Min(image.Width, image.Height);
                            if (name.IndexOf("targetsize-256_altform-unplated", StringComparison.OrdinalIgnoreCase) >= 0) score += 10000;
                            candidates[file] = score;
                        }
                    }
                    catch { }
                }
            }
            return candidates.OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault();
        }

        public static string SaveStableIcon(string appId, string pngPath, string stateRoot)
        {
            if (AppIdFromParsingPath(appId) != appId) throw new IOException("商店应用标识无效。");
            if (Paths.IsRemote(pngPath)) throw new IOException("不从网络路径读取图标。");
            Paths.RejectReparsePoints(pngPath);
            string folder = Path.Combine(stateRoot, "Icons");
            Paths.RejectReparsePoints(folder);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, appId.Replace('!', '_') + "_" + Paths.Hash(pngPath).Substring(0, 16) + ".ico");
            Paths.RejectReparsePoints(path);
            if (File.Exists(path) && Shell.HasIcon(path)) return path;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteIco(pngPath, temp);
            if (!Shell.HasIcon(temp)) { File.Delete(temp); throw new IOException("生成的图标未通过 Windows 读取校验。"); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            return path;
        }

        public static void WriteIco(string pngPath, string output)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
            var frames = new List<byte[]>();
            using (Image source = Image.FromFile(pngPath))
            {
                foreach (int size in sizes)
                {
                    using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    using (var stream = new MemoryStream())
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        double scale = Math.Min((double)size / source.Width, (double)size / source.Height);
                        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
                        using (var attributes = new ImageAttributes())
                        {
                            attributes.SetWrapMode(WrapMode.TileFlipXY);
                            graphics.DrawImage(source, new Rectangle((size - width) / 2, (size - height) / 2, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                        }
                        bitmap.Save(stream, ImageFormat.Png);
                        frames.Add(stream.ToArray());
                    }
                }
            }
            using (var writer = new BinaryWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
                }
                foreach (byte[] frame in frames) writer.Write(frame);
            }
        }
    }
}
