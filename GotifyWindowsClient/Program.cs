using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GotifyWindowsClient
{
    static class Program
    {
        private static NotifyIcon _trayIcon;
        private static ClientWebSocket _webSocket;
        private static bool _isConnected;
        private static readonly Mutex _mutex = new Mutex(true, "{8F6F0AC4-BC3B-4895-BC8F-5BBC8632465C}");
        private static readonly string SoftwareName = "GotifyTray";

        [STAThread]
        static void Main()
        {
            if (!_mutex.WaitOne(TimeSpan.Zero, true)) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ToastNotificationManagerCompat.OnActivated += OnToastActivated;

            var mainForm = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            InitializeTray();

            mainForm.Load += async (s, e) =>
            {
                mainForm.Visible = false;
                await ConnectToGotify();
            };

            Application.Run(mainForm);
            
            ToastNotificationManagerCompat.Uninstall();
            _mutex.ReleaseMutex();
        }

        private static void InitializeTray()
        {
            var exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
            var appIcon = Icon.ExtractAssociatedIcon(exePath);
            _trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Visible = true,
                Text = "Gotify Client",
                ContextMenuStrip = new ContextMenuStrip()
            };

            UpdateAutoStartMenu();

            _trayIcon.ContextMenuStrip.Items.Add("退出", null, (s, e) =>
            {
                _trayIcon.Visible = false;
                Application.Exit();
            });
        }

        private static void UpdateAutoStartMenu()
        {
            const string separatorTag = "AutoStartSeparator";
            var currentText = IsAutoStartEnabled() ? "禁用开机自启" : "启用开机自启";

            var itemsToRemove = new List<ToolStripItem>();
            foreach (ToolStripItem item in _trayIcon.ContextMenuStrip.Items)
            {
                if (item.Tag?.ToString() == "AutoStart" || item.Tag?.ToString() == separatorTag)
                {
                    itemsToRemove.Add(item);
                }
            }

            foreach (var item in itemsToRemove)
            {
                _trayIcon.ContextMenuStrip.Items.Remove(item);
            }

            var autoStartItem = new ToolStripMenuItem(currentText)
            {
                Tag = "AutoStart"
            };
            autoStartItem.Click += ToggleAutoStart;

            var separator = new ToolStripSeparator { Tag = separatorTag };

            _trayIcon.ContextMenuStrip.Items.Insert(0, autoStartItem);
            _trayIcon.ContextMenuStrip.Items.Insert(1, separator);
        }

        private static async Task ConnectToGotify()
        {
            var config = ConfigurationManager.AppSettings;
            var serverUrl = config["ServerUrl"] ?? "http://localhost:3000";
            var clientToken = config["ClientToken"];

            var wsUrl = serverUrl
                .Replace("http://", "ws://")
                .Replace("https://", "wss://")
                + $"/stream?token={clientToken}";

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            while (true)
            {
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

                        var buffer = new byte[4096];
                        while (ws.State == WebSocketState.Open)
                        {
                            var receivedBuffer = new List<byte>();
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                                receivedBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                            } while (!result.EndOfMessage);

                            var jsonBytes = receivedBuffer.ToArray();
                            Debug.WriteLine($"收到原始数据({jsonBytes.Length}字节)");

                            try
                            {
                                var message = JsonSerializer.Deserialize<GotifyMessage>(jsonBytes, options);
                                var title = string.IsNullOrEmpty(message.Title) ? "无标题" : message.Title;
                                var content = string.IsNullOrEmpty(message.Content) ? "空内容" : message.Content;

                                ShowNotification(title, content);
                            }
                            catch (JsonException ex)
                            {
                                var rawJson = Encoding.UTF8.GetString(jsonBytes);
                                File.WriteAllText("error.json", rawJson);
                                Debug.WriteLine($"JSON解析失败: {ex.Message}\n{rawJson}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"连接错误: {ex.Message}");
                    await Task.Delay(5000);
                }
            }
        }

        private static void ToggleAutoStart(object sender, EventArgs e)
        {
            var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            var appPath = $"\"{Application.ExecutablePath}\"";

            if (IsAutoStartEnabled())
            {
                key.DeleteValue(SoftwareName);
            }
            else
            {
                key.SetValue(SoftwareName, appPath);
            }

            UpdateAutoStartMenu();
        }

        private static bool IsAutoStartEnabled()
        {
            var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(SoftwareName) != null;
        }

        private static string? ExtractVerificationCode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var match = Regex.Match(text, @"\d{4,8}");
            return match.Success ? match.Value : null;
        }

        private static void ShowNotification(string title, string message)
        {
            var verificationCode = ExtractVerificationCode(message);

            if (verificationCode != null)
            {
                try
                {
                    new ToastContentBuilder()
                        .AddText(title)
                        .AddText(message)
                        .AddButton(new ToastButton()
                            .SetContent($"复制验证码: {verificationCode}")
                            .AddArgument("action", "copy")
                            .AddArgument("code", verificationCode)
                            .SetBackgroundActivation())
                        .Show();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Toast 失败: {ex.Message}");
                    _trayIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
                }
            }
            else
            {
                _trayIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
            }
        }

        private static void CopyToClipboard(string text)
        {
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        Clipboard.SetDataObject(text, true);
                        Debug.WriteLine($"剪贴板复制成功: {text}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"剪贴板复制失败: {ex.Message}");
                    }
                });
                
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建剪贴板线程失败: {ex.Message}");
            }
        }

        private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
        {
            var args = ToastArguments.Parse(e.Argument);

            if (args.Contains("action") && args["action"] == "copy")
            {
                if (args.Contains("code"))
                {
                    var code = args["code"];
                    CopyToClipboard(code);
                }
            }
        }
    }
}