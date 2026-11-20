using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("通用版考试模拟器")]
[assembly: AssemblyProduct("通用版考试模拟器")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ExamMachine
{
    internal static class FastProgram
    {
        internal const string AppName = "通用版考试模拟器";
        internal const string RuntimeFolder = "运行组件";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string directory = Path.GetDirectoryName(Application.ExecutablePath);
            bool relaunch = false;
            string key;
            using (var hash = SHA256.Create())
                key = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace('/', '_');
            using (var gate = new Mutex(false, "Local\\UniversalExamSimulator-" + key))
            {
                bool owned;
                try { owned = gate.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    MessageBox.Show("程序已经打开。请切回已有窗口。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    string runtime = Path.Combine(directory, RuntimeFolder);
                    relaunch = !RuntimeSetup.EnsureFramework(runtime);
                    if (!relaunch)
                    {
                        RuntimeSetup.EnsureWebView(runtime);
                        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => ResolveRuntime(runtime, args.Name);
                        RunWindow(directory);
                    }
                }
                catch (Exception error)
                {
                    MessageBox.Show("无法打开" + AppName + "。\n\n" + error.Message,
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { gate.ReleaseMutex(); }
            }
            if (relaunch) Process.Start(Application.ExecutablePath);
        }

        private static Assembly ResolveRuntime(string runtime, string assemblyName)
        {
            string name = new AssemblyName(assemblyName).Name;
            if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
            return Assembly.LoadFrom(Path.Combine(runtime, name + ".dll"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunWindow(string directory)
        {
            Application.Run(new FastWindow(directory));
        }
    }
}
