// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Sulock

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DesktopIconMedic
{
    public enum AppLanguage { SimplifiedChinese, TraditionalChinese, English }

    public static partial class UiText
    {
        public static AppLanguage Language { get; set; }

        public static string Get(string key, params object[] args)
        {
            string value = Messages[key][(int)Language];
            return args.Length == 0 ? value : String.Format(CultureInfo.InvariantCulture, value, args);
        }

        public static IEnumerable<string> Keys { get { return Messages.Keys; } }

        public static AppLanguage ForCulture(CultureInfo culture)
        {
            if (culture.TwoLetterISOLanguageName != "zh") return AppLanguage.English;
            string name = culture.Name;
            return name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.TraditionalChinese : AppLanguage.SimplifiedChinese;
        }

        public static string Code(AppLanguage language)
        {
            switch (language)
            {
                case AppLanguage.SimplifiedChinese: return "zh-Hans";
                case AppLanguage.TraditionalChinese: return "zh-Hant";
                case AppLanguage.English: return "en";
                default: throw new ArgumentOutOfRangeException("language");
            }
        }
    }

    public static class LanguageSettings
    {
        [DataContract]
        private sealed class Settings
        {
            [DataMember(Name = "language")] public string Language;
        }

        public static AppLanguage Load(string stateRoot)
        {
            return Load(stateRoot, CultureInfo.CurrentUICulture);
        }

        public static AppLanguage Load(string stateRoot, CultureInfo systemCulture)
        {
            try
            {
                string path = Path.Combine(stateRoot, "settings.json");
                Paths.RejectReparsePoints(path);
                using (var stream = File.OpenRead(path))
                {
                    var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    if (settings != null)
                        foreach (AppLanguage language in Enum.GetValues(typeof(AppLanguage)))
                            if (settings.Language == UiText.Code(language)) return language;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SerializationException) { }
            return UiText.ForCulture(systemCulture);
        }

        public static void Save(string stateRoot, AppLanguage language)
        {
            string code = UiText.Code(language);
            Paths.RejectReparsePoints(stateRoot);
            Directory.CreateDirectory(stateRoot);
            string path = Path.Combine(stateRoot, "settings.json");
            Paths.RejectReparsePoints(path);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, new Settings { Language = code });
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
