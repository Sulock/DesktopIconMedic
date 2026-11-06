using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("桌面图标修复助手")]
[assembly: AssemblyDescription("Windows 11 桌面快捷方式检查与图标缓存修复")]
[assembly: AssemblyCompany("Desktop Icon Medic")]
[assembly: AssemblyProduct("桌面图标修复助手")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using (var mutex = new Mutex(true, @"Local\DesktopIconMedic.UI", out first))
            {
                if (!first) { MessageBox.Show("图标修复助手已经打开，请查看任务栏。", "桌面图标修复助手"); return; }
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
                    MessageBox.Show("操作未完成：" + e.Exception.Message, "桌面图标修复助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                Application.Run(new MainForm(StateRoot, false));
            }
        }
    }
}
