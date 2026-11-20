using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ExamMachine
{
    internal sealed class FastWindow : Form
    {
        private const string Root = "https://exam-machine.invalid";
        private const string Entry = Root + "/index.html";
        private static readonly Dictionary<string, string> Media = new Dictionary<string, string>
        {
            { "/index.html", "text/html; charset=utf-8" }, { "/app.css", "text/css; charset=utf-8" },
            { "/app.js", "text/javascript; charset=utf-8" }, { "/exam.ico", "image/x-icon" }
        };
        private readonly WebView2 browser;
        private readonly string cacheFolder;
        private readonly FastBridge bridge;
        private readonly SemaphoreSlim serial = new SemaphoreSlim(1);
        private Task warmup;
        private bool ready;
        private bool confirmingClose;
        private bool allowClose;

        internal FastWindow(string directory)
        {
            Text = FastProgram.AppName;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(237, 242, 252);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            using (Graphics graphics = Graphics.FromHwnd(Handle))
            {
                float scale = graphics.DpiX / 96F;
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min((int)(760 * scale), area.Width), Math.Min((int)(480 * scale), area.Height));
                Size = new Size(Math.Min((int)(1160 * scale), area.Width), Math.Min((int)(765 * scale), area.Height));
            }
            cacheFolder = Path.Combine(directory, FastProgram.RuntimeFolder, "渲染缓存");
            bridge = new FastBridge(directory);
            browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            var loading = new Label
            {
                Text = "正在打开" + FastProgram.AppName + "…", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(53, 80, 151), Font = new Font("Microsoft YaHei UI", 13F)
            };
            Controls.Add(browser);
            Controls.Add(loading);
            loading.BringToFront();
            Shown += async (s, e) => await Initialize(loading);
            FormClosing += ConfirmClose;
        }

        private async Task Initialize(Label loading)
        {
            try
            {
                warmup = Task.Run(() => bridge.WarmUp());
                var environment = await CoreWebView2Environment.CreateAsync(null, cacheFolder);
                if (IsDisposed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (IsDisposed) return;
                CoreWebView2 core = browser.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.NavigationStarting += (s, args) => { if (args.Uri != Entry) args.Cancel = true; };
                core.NewWindowRequested += (s, args) => args.Handled = true;
                core.AddWebResourceRequestedFilter(Root + "/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += Serve;
                core.WebMessageReceived += MessageReceived;
                core.NavigationCompleted += (s, args) =>
                {
                    if (args.IsSuccess) { ready = true; loading.Dispose(); }
                    else loading.Text = "无法载入界面，请重新打开程序。";
                };
                core.Navigate(Entry);
            }
            catch (Exception error)
            {
                if (!IsDisposed) loading.Text = "界面初始化失败\n\n" + error.GetBaseException().Message;
            }
        }

        private void Serve(object sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var uri = new Uri(args.Request.Uri);
            string type;
            if (!Media.TryGetValue(uri.AbsolutePath, out type))
            {
                args.Response = browser.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(), 404, "Not Found", "");
                return;
            }
            Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(uri.AbsolutePath.TrimStart('/'));
            args.Response = browser.CoreWebView2.Environment.CreateWebResourceResponse(resource, 200, "OK",
                "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        }

        private async void MessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (args.Source != Entry) return;
            string json = args.WebMessageAsJson;
            await serial.WaitAsync();
            try
            {
                var codec = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                BrowserRequest request = codec.Deserialize<BrowserRequest>(json);
                string path = request.Action == "import-bank" ? ChooseBank() : null;
                string response = await Task.Run(() => Process(request, path, codec));
                if (!IsDisposed && browser.CoreWebView2 != null) browser.CoreWebView2.PostWebMessageAsJson(response);
            }
            finally { serial.Release(); }
        }

        private string ChooseBank()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择题库文件", Filter = "题库文件 (*.xlsx;*.docx;*.csv;*.tsv)|*.xlsx;*.docx;*.csv;*.tsv",
                CheckFileExists = true, Multiselect = false, RestoreDirectory = true
            }) return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }

        private string Process(BrowserRequest request, string path, JavaScriptSerializer codec)
        {
            try
            {
                warmup.GetAwaiter().GetResult();
                object result = bridge.Dispatch(request, path);
                return codec.Serialize(new { request.Id, Data = result });
            }
            catch (Exception error)
            {
                string info = error is InvalidDataException && path != null ? "无法读取该表格或文档，请确认文件完整且未加密。" :
                    error is IOException || error is UnauthorizedAccessException ? "文件读取或保存失败。请检查访问权限和磁盘空间。" : error.Message;
                return codec.Serialize(new { request.Id, Error = info });
            }
        }

        private async void ConfirmClose(object sender, FormClosingEventArgs args)
        {
            if (allowClose || !ready || args.CloseReason == CloseReason.WindowsShutDown) return;
            args.Cancel = true;
            if (confirmingClose) return;
            confirmingClose = true;
            try
            {
                string result;
                try { result = await browser.CoreWebView2.ExecuteScriptAsync("!window.canCloseExamMachine || window.canCloseExamMachine()"); }
                catch (InvalidOperationException) { result = "true"; }
                catch (System.Runtime.InteropServices.COMException) { result = "true"; }
                if (result == "false") return;
                await serial.WaitAsync();
                try
                {
                    if (warmup.Status == TaskStatus.RanToCompletion)
                        await Task.Run(() => bridge.CheckpointDraft());
                }
                catch (IOException) { /* The standalone draft is still recoverable. */ }
                catch (UnauthorizedAccessException) { }
                finally { serial.Release(); }
                allowClose = true;
                Close();
            }
            finally { confirmingClose = false; }
        }
    }
}
