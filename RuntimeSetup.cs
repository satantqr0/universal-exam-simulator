using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ExamMachine
{
    internal static class RuntimeSetup
    {
        internal const string Core = "Microsoft.Web.WebView2.Core.dll";
        internal const string WinForms = "Microsoft.Web.WebView2.WinForms.dll";
        internal const string Loader = "WebView2Loader.dll";
        private const string InstallerFolder = "离线安装包";
        private const string WebViewInstaller = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
        private const string FrameworkInstaller = "NetFramework462-x64-x86.exe";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fullPath);

        [DllImport(Loader, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetAvailableCoreWebView2BrowserVersionString(string browserFolder, out IntPtr version);

        private static bool HasFramework()
        {
            object release = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null);
            return release is int && (int)release >= 394802;
        }

        // Relaunch after installing the framework so the program starts under the upgraded CLR.
        internal static bool EnsureFramework(string runtime)
        {
            if (HasFramework()) return true;
            int exit = RunOfflineInstaller(runtime, FrameworkInstaller, ".NET Framework 4.6.2",
                "/q /norestart /ChainingPackage ExamMachine", true);
            if (exit == 3010 || exit == 1641)
                throw new InvalidOperationException(".NET Framework 已安装。请重启 Windows 后再打开程序。");
            if (!HasFramework()) throw InstallationFailed(".NET Framework", exit);
            return false;
        }

        internal static void EnsureWebView(string runtime)
        {
            Directory.CreateDirectory(runtime);
            RestoreLibrary(runtime, Core);
            RestoreLibrary(runtime, WinForms);
            RestoreLibrary(runtime, Loader);

            if (LoadLibrary(Path.Combine(runtime, Loader)) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 WebView2 启动组件。");
            if (HasWebView()) return;

            int exit = RunOfflineInstaller(runtime, WebViewInstaller, "Microsoft Edge WebView2", "/silent /install", false);
            if (!HasWebView())
            {
                if (exit == 3010 || exit == 1641)
                    throw new InvalidOperationException("WebView2 已安装。请重启 Windows 后再打开程序。");
                throw InstallationFailed("WebView2", exit);
            }
        }

        private static bool HasWebView()
        {
            IntPtr version;
            int result = GetAvailableCoreWebView2BrowserVersionString(null, out version);
            try { return result >= 0 && version != IntPtr.Zero; }
            finally { Marshal.FreeCoTaskMem(version); }
        }

        private static void RestoreLibrary(string runtime, string name)
        {
            string target = Path.Combine(runtime, name);
            using (Stream source = ReadResource("library." + name))
            {
                if (File.Exists(target) && new FileInfo(target).Length == source.Length)
                {
                    string expected = Hash(source);
                    using (var installed = File.OpenRead(target))
                        if (Hash(installed) == expected) return;
                    source.Position = 0;
                }
                string temporary = target + ".restore.tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                        source.CopyTo(output);
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                finally { File.Delete(temporary); }
            }
        }

        private static Stream ReadResource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        private static string Hash(Stream file)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }

        private static string VerifyPackage(string runtime, string name)
        {
            string path = Path.Combine(runtime, InstallerFolder, name);
            if (!File.Exists(path))
                throw new FileNotFoundException("缺少离线安装包。请从完整副本一起复制「运行组件\\离线安装包」文件夹。", path);
            using (var checksums = new StreamReader(ReadResource("installer.checksums")))
            using (var package = File.OpenRead(path))
            {
                string prefix = name + " ";
                for (string line = checksums.ReadLine(); line != null; line = checksums.ReadLine())
                    if (line.StartsWith(prefix, StringComparison.Ordinal) && line.Substring(prefix.Length) == Hash(package))
                        return path;
            }
            throw new InvalidDataException("离线安装包校验失败。请从完整副本重新复制 " + name + "。");
        }

        private static int RunOfflineInstaller(string runtime, string name, string label, string arguments, bool elevate)
        {
            var start = new ProcessStartInfo
            {
                UseShellExecute = elevate,
                Verb = elevate ? "runas" : "",
                CreateNoWindow = !elevate,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = arguments
            };
            using (var progress = new Form())
            {
                progress.Text = FastProgram.AppName;
                progress.StartPosition = FormStartPosition.CenterScreen;
                progress.FormBorderStyle = FormBorderStyle.FixedDialog;
                progress.ControlBox = false;
                progress.ClientSize = new Size(440, 116);
                progress.Controls.Add(new Label
                {
                    Text = "首次使用正在补齐 " + label + "…",
                    AutoSize = false, Bounds = new Rectangle(22, 14, 396, 49),
                    TextAlign = ContentAlignment.MiddleLeft
                });
                progress.Controls.Add(new ProgressBar { Bounds = new Rectangle(22, 70, 396, 14), Style = ProgressBarStyle.Marquee });
                progress.Show();
                progress.Update();
                try
                {
                    start.FileName = VerifyPackage(runtime, name);
                    using (Process installer = Process.Start(start))
                    {
                        if (installer == null) throw new InvalidOperationException("无法启动 " + label + " 安装程序。");
                        while (!installer.WaitForExit(100)) Application.DoEvents();
                        return installer.ExitCode;
                    }
                }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode == 1223)
                        throw new InvalidOperationException("已取消 " + label + " 安装。重新打开程序即可重试。", error);
                    throw;
                }
            }
        }

        private static Exception InstallationFailed(string component, int exit)
        {
            return new InvalidOperationException(component + " 安装完成后仍无法使用。安装程序返回 " + exit + "（0x" + exit.ToString("X8") + "）。");
        }
    }
}
