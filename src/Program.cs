// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Sulock

using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Desktop Icon Medic")]
[assembly: AssemblyDescription("Windows desktop shortcut and icon cache repair")]
[assembly: AssemblyCompany("Desktop Icon Medic")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Sulock")]
[assembly: AssemblyProduct("Desktop Icon Medic")]
[assembly: AssemblyVersion("1.3.1.0")]
[assembly: AssemblyFileVersion("1.3.1.0")]

namespace DesktopIconMedic
{
    internal static class Program
    {
        public static string StateRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIconMedic"); } }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--recover-explorer")
            {
                int parent;
                try { if (Int32.TryParse(args[1], out parent)) CacheService.RunRecovery(parent, args[2], StateRoot); }
                catch { }
                return;
            }
            UiText.Language = LanguageSettings.Load(StateRoot);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using (var mutex = new Mutex(true, @"Local\DesktopIconMedic.UI", out first))
            {
                if (!first) { MessageBox.Show(UiText.Get("app.alreadyOpen"), UiText.Get("app.title")); return; }
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
                    MessageBox.Show(UiText.Get("app.error", e.Exception.Message), UiText.Get("app.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                Application.Run(new MainForm(StateRoot, false));
            }
        }
    }
}
