using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

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

            var mainForm = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            InitializeTray();

            // 启动时最小化到托盘
            mainForm.Load += async (s, e) =>
            {
                mainForm.Visible = false;
                await ConnectToGotify();
            };

            Application.Run(mainForm);
            _mutex.ReleaseMutex();
        }

        private static void InitializeTray()
        {
            // 从当前程序文件提取图标
            var exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
            var appIcon = Icon.ExtractAssociatedIcon(exePath);
            _trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Visible = true,
                Text = "Gotify Client",
                ContextMenuStrip = new ContextMenuStrip()
            };

            // 动态更新自启动菜单文本
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

            // 清理旧菜单项
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

            // 创建新菜单项
            var autoStartItem = new ToolStripMenuItem(currentText)
            {
                Tag = "AutoStart"
            };
            autoStartItem.Click += ToggleAutoStart;

            // 创建带标识的分隔线
            var separator = new ToolStripSeparator { Tag = separatorTag };

            // 插入到菜单顶部（保留退出按钮在底部）
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

        /// <summary>
        /// 设置开机自启
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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

        /// <summary>
        /// 判断是否已开机自启
        /// </summary>
        /// <returns></returns>
        private static bool IsAutoStartEnabled()
        {
            var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(SoftwareName) != null;
        }


        /// <summary>
        /// 弹窗提醒
        /// </summary>
        /// <param name="title"></param>
        /// <param name="message"></param>
        private static void ShowNotification(string title, string message)
        {
            _trayIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
    }
}