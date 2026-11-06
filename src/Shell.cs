// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Sulock

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DesktopIconMedic
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkObject { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    public sealed class LinkData
    {
        public string Target, IconPath, Arguments, WorkingDirectory, Description;
        public string ParsingPath, IdList;
        public int IconIndex;
    }

    public static class Shell
    {
        [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr pidl);
        [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint name, out IntPtr text);
        [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint change, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr large, IntPtr small, uint count);

        public static void RefreshIcons()
        {
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }

        public static void NotifyFile(string file)
        {
            IntPtr ptr = Marshal.StringToHGlobalUni(file);
            try { SHChangeNotify(0x00002000, 0x0005, ptr, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public static bool HasIcon(string path)
        {
            if (Paths.IsRemote(path)) return false;
            uint count = ExtractIconEx(path, -1, IntPtr.Zero, IntPtr.Zero, 0);
            return count > 0 && count != UInt32.MaxValue;
        }

        // Never call Resolve: inspection must not launch installers or search the network.
        public static LinkData ReadLink(string path)
        {
            object obj = new ShellLinkObject();
            try
            {
                ((IPersistFile)obj).Load(path, 0);
                IShellLinkW link = (IShellLinkW)obj;
                var buffer = new StringBuilder(32768);
                var data = new LinkData();
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
                data.Target = buffer.ToString();
                IntPtr pidl;
                link.GetIDList(out pidl);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        uint size = ILGetSize(pidl);
                        if (size > 1024 * 1024) throw new IOException(UiText.Get("error.linkIdentity"));
                        byte[] bytes = new byte[size];
                        Marshal.Copy(pidl, bytes, 0, bytes.Length);
                        data.IdList = Convert.ToBase64String(bytes);
                        // Read the existing shell identity without resolving or launching the target.
                        IntPtr name;
                        if (SHGetNameFromIDList(pidl, 0x80028000, out name) >= 0 && name != IntPtr.Zero)
                        {
                            try { data.ParsingPath = Marshal.PtrToStringUni(name); }
                            finally { Marshal.FreeCoTaskMem(name); }
                        }
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                buffer.Clear();
                link.GetIconLocation(buffer, buffer.Capacity, out data.IconIndex);
                data.IconPath = buffer.ToString();
                buffer.Clear(); link.GetArguments(buffer, buffer.Capacity); data.Arguments = buffer.ToString();
                buffer.Clear(); link.GetWorkingDirectory(buffer, buffer.Capacity); data.WorkingDirectory = buffer.ToString();
                buffer.Clear(); link.GetDescription(buffer, buffer.Capacity); data.Description = buffer.ToString();
                return data;
            }
            finally { Marshal.FinalReleaseComObject(obj); }
        }

        public static void ChangeIcon(string path, string iconPath)
        {
            object obj = new ShellLinkObject();
            try
            {
                ((IPersistFile)obj).Load(path, 2);
                ((IShellLinkW)obj).SetIconLocation(iconPath, 0);
                ((IPersistFile)obj).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(obj); }
        }

        public static void CreateLink(string path, LinkData data)
        {
            object obj = new ShellLinkObject();
            try
            {
                IShellLinkW link = (IShellLinkW)obj;
                link.SetPath(data.Target);
                link.SetIconLocation(data.IconPath ?? "", data.IconIndex);
                link.SetArguments(data.Arguments ?? "");
                link.SetWorkingDirectory(data.WorkingDirectory ?? "");
                link.SetDescription(data.Description ?? "");
                ((IPersistFile)obj).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(obj); }
        }
    }
}
