using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class ClaudeMonitor : Form
{
    private Panel contentPanel;
    private System.Windows.Forms.Timer timer;
    private System.Windows.Forms.Timer animationTimer;
    private System.Windows.Forms.Timer heartbeatTimer;
    private List<SessionControl> sessionControls = new List<SessionControl>();
    private Button pinBtn;
    private int errorCount = 0;
    private const int MaxErrors = 5;
    private string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "config.json");
    private string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "monitor.log");
    private Process serverProcess = null;  // Server 子进程
    private string hookDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "E:\\Code\\claude-monitor";

    private void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
        }
        catch { }
    }

    private void StartServer()
    {
        try
        {
            // 检查服务是否已运行
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/health");
                request.Proxy = null;
                request.Timeout = 1000;
                using (var response = request.GetResponse()) { }
                Log("Server already running");
                return;  // 服务已运行，不需要启动
            }
            catch { }

            // 启动 server.js
            string serverPath = Path.Combine(hookDir, "server.js");
            if (!File.Exists(serverPath))
            {
                Log("server.js not found at: " + serverPath);
                return;
            }

            Log("Starting server.js...");
            serverProcess = new Process();
            serverProcess.StartInfo.FileName = "node";
            serverProcess.StartInfo.Arguments = "\"" + serverPath + "\"";
            serverProcess.StartInfo.WorkingDirectory = hookDir;
            serverProcess.StartInfo.CreateNoWindow = true;
            serverProcess.StartInfo.UseShellExecute = false;
            serverProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            serverProcess.Start();

            // 等待服务就绪（最多5秒）
            for (int i = 0; i < 50; i++)
            {
                System.Threading.Thread.Sleep(100);
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/health");
                    request.Proxy = null;
                    request.Timeout = 500;
                    using (var response = request.GetResponse()) { }
                    Log("Server started successfully");
                    return;
                }
                catch { }
            }
            Log("Server startup timeout");
        }
        catch (Exception ex) { Log("StartServer error: " + ex.Message); }
    }

    private void StopServer()
    {
        // 先尝试杀死自己启动的子进程
        if (serverProcess != null)
        {
            try
            {
                Log("Stopping server process (pid=" + serverProcess.Id + ")...");
                serverProcess.Kill();
                serverProcess.WaitForExit(1000);
                serverProcess.Close();
                serverProcess = null;
                Log("Server stopped");
                return;  // 成功停止
            }
            catch (Exception ex) { Log("StopServer error: " + ex.Message); }
        }

        // 如果 serverProcess 为空（server 由其他方式启动），通过端口杀死进程
        try
        {
            Log("Finding server process on port 18989...");
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c netstat -ano | findstr :18989 | findstr LISTENING",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using (var proc = Process.Start(startInfo))
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                // 输出格式: "  TCP    0.0.0.0:18989    ...    LISTENING    12345"
                var parts = output.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    int pid = int.Parse(parts[parts.Length - 1]);
                    Log("Killing server process (pid=" + pid + ")...");
                    Process.GetProcessById(pid).Kill();
                    Log("Server stopped");
                }
            }
        }
        catch (Exception ex) { Log("StopServer cleanup error: " + ex.Message); }
    }

    public ClaudeMonitor()
    {
        try
        {
            Log("ClaudeMonitor constructor started");

            // 启动 server 子进程
            StartServer();

            this.Text = "Claude Monitor";
        this.Size = new Size(320, 150);
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(26, 26, 46);

        LoadWindowPosition();

        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        this.Load += (s, e) => {
            try
            {
                Log("Form Load event");
                ApplyRoundedCorners();
                this.TopMost = false;
                this.TopMost = true;
                Log("Form Load completed, Location: " + this.Location.X + "," + this.Location.Y);
            }
            catch (Exception ex) { Log("Form Load error: " + ex.Message); }
        };
        this.Resize += (s, e) => ApplyRoundedCorners();
        this.FormClosing += (s, e) => {
            Log("FormClosing event, CloseReason: " + e.CloseReason);
            SaveWindowPosition();
            // 不再杀死 server，因为 server 是父进程且可能服务其他会话
            // StopServer();
        };
        this.FormClosed += (s, e) => {
            Log("FormClosed event");
        };

        Panel header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 24;
        header.BackColor = Color.FromArgb(102, 126, 234);
        header.Cursor = Cursors.SizeAll;
        header.MouseDown += FormMouseDown;

        Label title = new Label();
        title.Text = " Claude";
        title.Dock = DockStyle.Fill;
        title.ForeColor = Color.White;
        title.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Cursor = Cursors.SizeAll;
        title.MouseDown += FormMouseDown;

        pinBtn = new Button();
        pinBtn.Text = "P";
        pinBtn.Width = 24;
        pinBtn.Height = 24;
        pinBtn.Dock = DockStyle.Right;
        pinBtn.FlatStyle = FlatStyle.Flat;
        pinBtn.BackColor = Color.FromArgb(46, 204, 113);
        pinBtn.ForeColor = Color.White;
        pinBtn.Font = new Font("Arial", 9, FontStyle.Bold);
        pinBtn.Cursor = Cursors.Hand;
        pinBtn.FlatAppearance.BorderSize = 0;
        pinBtn.Click += (s, e) => {
            this.TopMost = !this.TopMost;
            pinBtn.BackColor = this.TopMost ? Color.FromArgb(46, 204, 113) : Color.FromArgb(100, 100, 100);
        };

        Button closeBtn = new Button();
        closeBtn.Text = "X";
        closeBtn.Width = 24;
        closeBtn.Height = 24;
        closeBtn.Dock = DockStyle.Right;
        closeBtn.FlatStyle = FlatStyle.Flat;
        closeBtn.BackColor = Color.FromArgb(231, 76, 60);
        closeBtn.ForeColor = Color.White;
        closeBtn.Font = new Font("Arial", 10);
        closeBtn.Cursor = Cursors.Hand;
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Click += (s, e) => { timer.Stop(); animationTimer.Stop(); heartbeatTimer.Stop(); SaveWindowPosition(); this.Close(); };

        header.Controls.AddRange(new Control[] { closeBtn, pinBtn, title });

        contentPanel = new Panel();
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.BackColor = Color.FromArgb(26, 26, 46);
        contentPanel.MouseDown += FormMouseDown;

        ContextMenuStrip contextMenu = new ContextMenuStrip();
        ToolStripMenuItem resetItem = new ToolStripMenuItem("Reset Status");
        resetItem.Click += (s, e) => ResetStatus();
        ToolStripMenuItem copyItem = new ToolStripMenuItem("Copy Task");
        copyItem.Click += (s, e) => CopyCurrentTask();
        ToolStripMenuItem linkItem = new ToolStripMenuItem("Link to Active Window");
        linkItem.Click += (s, e) => LinkActiveWindow();
        ToolStripMenuItem flashItem = new ToolStripMenuItem("Flash Linked Window");
        flashItem.Click += (s, e) => FlashLinkedWindow();
        contextMenu.Items.AddRange(new ToolStripItem[] { resetItem, copyItem, linkItem, flashItem });
        this.ContextMenuStrip = contextMenu;

        this.Controls.AddRange(new Control[] { contentPanel, header });
        this.MouseDown += FormMouseDown;

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 500;
        timer.Tick += UpdateStatus;
        timer.Start();

        animationTimer = new System.Windows.Forms.Timer();
        animationTimer.Interval = 250;  // Slower animation (250ms)
        animationTimer.Tick += AnimationTick;
        animationTimer.Start();

        // 心跳定时器，每3秒发送一次心跳（从5秒缩短）
        heartbeatTimer = new System.Windows.Forms.Timer();
        heartbeatTimer.Interval = 3000;
        heartbeatTimer.Tick += SendHeartbeat;
        heartbeatTimer.Start();

        // 立即发送启动通知
        SendStarted();
        Log("Constructor completed successfully");
        }
        catch (Exception ex)
        {
            Log("Constructor error: " + ex.Message + "\n" + ex.StackTrace);
            throw;
        }
    }

    private void SendHeartbeat(object sender, EventArgs e)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/monitor-heartbeat");
            request.Proxy = null;
            request.Timeout = 2000;
            request.Method = "POST";
            request.ContentType = "application/json";
            string body = "{\"pid\":" + Process.GetCurrentProcess().Id + "}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            request.ContentLength = data.Length;
            using (var stream = request.GetRequestStream()) {
                stream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse()) { }
        }
        catch { }
    }

    private void SendStarted() {
        try {
            var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/monitor-started");
            request.Proxy = null;
            request.Timeout = 2000;
            request.Method = "POST";
            request.ContentType = "application/json";
            string body = "{\"pid\":" + Process.GetCurrentProcess().Id + "}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            request.ContentLength = data.Length;
            using (var stream = request.GetRequestStream()) {
                stream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse()) { }
            Log("Sent started with pid: " + Process.GetCurrentProcess().Id);
        } catch (Exception ex) { Log("SendStarted error: " + ex.Message); }
    }

    private void LoadWindowPosition()
    {
        try
        {
            if (File.Exists(configFile))
            {
                var json = File.ReadAllText(configFile);
                // 支持 JSON 格式: {"x":123,"y":456} 或简单格式: "123,456"
                int x = 0, y = 0;
                if (json.Contains("{"))
                {
                    // JSON 格式
                    x = GetJsonInt(json, "x");
                    y = GetJsonInt(json, "y");
                }
                else
                {
                    // 简单格式
                    var parts = json.Split(',');
                    if (parts.Length == 2)
                    {
                        x = int.Parse(parts[0]);
                        y = int.Parse(parts[1]);
                    }
                }

                Log("LoadWindowPosition: config x=" + x + ", y=" + y);

                // 检查坐标是否在任何屏幕内
                foreach (Screen screen in Screen.AllScreens)
                {
                    Log("Screen " + screen.DeviceName + " bounds: " + screen.Bounds);
                    if (screen.Bounds.Contains(x, y))
                    {
                        this.Location = new Point(x, y);
                        Log("Window positioned at " + x + "," + y + " on " + screen.DeviceName);
                        return;
                    }
                }
                Log("Config position not in any screen, using default");
            }
        }
        catch (Exception ex) { Log("LoadWindowPosition error: " + ex.Message); }

        // 默认位置：主显示器右下角
        var workingArea = Screen.PrimaryScreen.WorkingArea;
        this.Location = new Point(workingArea.Right - 340, workingArea.Bottom - 170);
        Log("Window positioned at default: " + this.Location.X + "," + this.Location.Y);
    }

    private int GetJsonInt(string json, string key)
    {
        string search = "\"" + key + "\":";
        int start = json.IndexOf(search);
        if (start < 0) return 0;
        start += search.Length;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        int end = start;
        while (end < json.Length && (json[end] >= '0' && json[end] <= '9' || json[end] == '-')) end++;
        if (end > start) return int.Parse(json.Substring(start, end - start));
        return 0;
    }

    private void SaveWindowPosition()
    {
        try
        {
            var dir = Path.GetDirectoryName(configFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            int x = this.Location.X;
            int y = this.Location.Y;

            // 验证坐标在某个屏幕内（支持多显示器，包括负坐标）
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Bounds.Contains(x, y))
                {
                    File.WriteAllText(configFile, x + "," + y);
                    Log("SaveWindowPosition: saved " + x + "," + y);
                    return;
                }
            }
            Log("SaveWindowPosition: position " + x + "," + y + " not in any screen, not saving");
        }
        catch (Exception ex) { Log("SaveWindowPosition error: " + ex.Message); }
    }

    private void ResetStatus()
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/reset");
            request.Proxy = null;
            request.Timeout = 1000;
            request.Method = "POST";
            request.ContentLength = 0;
            using (var response = request.GetResponse()) { }
        }
        catch { }
    }

    private void CopyCurrentTask()
    {
        var sb = new StringBuilder();
        foreach (var ctrl in sessionControls)
        {
            if (!string.IsNullOrEmpty(ctrl.CurrentTask))
            {
                sb.AppendLine(ctrl.CurrentTask);
            }
        }
        if (sb.Length > 0)
        {
            Clipboard.SetText(sb.ToString().TrimEnd());
        }
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            int cornerPreference = 3;
            NativeMethods.DwmSetWindowAttribute(this.Handle, 33, ref cornerPreference, 4);
        }
        catch (Exception ex) { Log("ApplyRoundedCorners error: " + ex.Message); }
    }

    private void FormMouseDown(object s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(this.Handle, 0xA1, 0x2, 0);
        }
    }

    private void AnimationTick(object sender, EventArgs e)
    {
        foreach (var ctrl in sessionControls)
        {
            ctrl.Animate();
        }
    }

    private void UpdateStatus(object sender, EventArgs e)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/status");
            request.Proxy = null;
            request.Timeout = 1000;
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);

                // Check showWindow flag
                bool showWindow = GetJsonBool(json, "showWindow");
                if (showWindow)
                {
                    this.Invoke((MethodInvoker)delegate { this.Show(); this.TopMost = true; });
                }

                var sessions = ParseSessions(json);
                errorCount = 0;
                this.Invoke((MethodInvoker)delegate { UpdateSessionUI(sessions); });
            }
        }
        catch
        {
            errorCount++;
            if (errorCount >= MaxErrors)
            {
                this.Invoke((MethodInvoker)delegate {
                    if (sessionControls.Count == 0)
                    {
                        UpdateSessionUI(new List<SessionData> {
                            new SessionData { state = "error", task = "Server disconnected", project = "" }
                        });
                    }
                });
            }
        }
    }

    private bool GetJsonBool(string json, string key)
    {
        string search = "\"" + key + "\":";
        int start = json.IndexOf(search);
        if (start < 0) return false;
        start += search.Length;
        // Skip whitespace
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        if (start >= json.Length) return false;
        return json[start] == 't' || json[start] == 'T';
    }

    private List<SessionData> ParseSessions(string json)
    {
        var list = new List<SessionData>();
        int sessionsStart = json.IndexOf("\"sessions\":[");
        if (sessionsStart < 0)
        {
            var s = new SessionData();
            s.state = GetJsonString(json, "state");
            s.task = GetJsonString(json, "task");
            s.project = "";
            list.Add(s);
            return list;
        }

        sessionsStart += "\"sessions\":[".Length;
        int depth = 1;
        int objStart = -1;

        for (int i = sessionsStart; i < json.Length && depth > 0; i++)
        {
            if (json[i] == '{') { if (depth == 1) objStart = i; depth++; }
            else if (json[i] == '}')
            {
                depth--;
                if (depth == 1 && objStart >= 0)
                {
                    string obj = json.Substring(objStart, i - objStart + 1);
                    var s = new SessionData();
                    s.id = GetJsonString(obj, "id");
                    s.project = GetJsonString(obj, "project");
                    s.state = GetJsonString(obj, "state");
                    s.task = GetJsonString(obj, "task");
                    s.windowHandle = GetJsonString(obj, "windowHandle");
                    s.model = GetJsonString(obj, "model");
                    s.context = GetJsonString(obj, "context");
                    s.branch = GetJsonString(obj, "branch");
                    list.Add(s);
                    objStart = -1;
                }
            }
            else if (json[i] == ']') depth = 0;
        }
        return list;
    }

    private void UpdateSessionUI(List<SessionData> sessions)
    {
        Log("UpdateSessionUI: sessions=" + sessions.Count + ", controls=" + sessionControls.Count);
        // Remove extra controls
        while (sessionControls.Count > sessions.Count)
        {
            var ctrl = sessionControls[sessionControls.Count - 1];
            ctrl.MouseClick -= SessionControlMouseClick;
            contentPanel.Controls.Remove(ctrl);
            ctrl.Dispose();
            sessionControls.RemoveAt(sessionControls.Count - 1);
        }

        // Add new controls if needed
        try
        {
            while (sessionControls.Count < sessions.Count)
            {
                Log("Creating SessionControl, count=" + sessionControls.Count);
                var ctrl = new SessionControl();
                ctrl.Left = 5;
                ctrl.Width = 305;
                ctrl.MouseClick += SessionControlMouseClick;
                contentPanel.Controls.Add(ctrl);
                sessionControls.Add(ctrl);
                Log("Added SessionControl, count=" + sessionControls.Count);
            }
        }
        catch (Exception ex)
        {
            Log("Error creating SessionControl: " + ex.Message + "\n" + ex.StackTrace);
        }

        // Update data and calculate positions
        int totalHeight = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            sessionControls[i].UpdateData(sessions[i]);
            sessionControls[i].Top = totalHeight;
            totalHeight += sessionControls[i].RequiredHeight + 5; // 5px gap
            Log("Session " + i + ": height=" + sessionControls[i].RequiredHeight + ", top=" + totalHeight);
        }

        // Adjust window height (header 24 + content + padding)
        int windowHeight = 24 + totalHeight + 10;
        this.Height = Math.Min(windowHeight, 500);
        Log("Window height=" + windowHeight);
    }

    private void SessionControlMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var ctrl = sender as SessionControl;
            if (ctrl != null && !string.IsNullOrEmpty(ctrl.SessionId))
            {
                _lastClickedSession = ctrl;
                Log("Click on session: " + ctrl.SessionId + ", windowHandle: " + ctrl.WindowHandle);
                ActivateClaudeWindow(ctrl.SessionId, ctrl.WindowHandle);
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            var ctrl = sender as SessionControl;
            if (ctrl != null)
            {
                _lastClickedSession = ctrl;
            }
        }
    }

    private SessionControl _lastClickedSession = null;

    private void LinkActiveWindow()
    {
        if (_lastClickedSession == null || string.IsNullOrEmpty(_lastClickedSession.SessionId))
        {
            Log("No session selected for linking");
            return;
        }

        // 获取当前前台窗口
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            Log("Failed to get foreground window");
            return;
        }

        string handleStr = hwnd.ToInt64().ToString();
        Log("Linking session " + _lastClickedSession.SessionId + " to window " + handleStr);

        // 更新服务器中的 windowHandle
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/session");
            request.Proxy = null;
            request.Timeout = 2000;
            request.Method = "POST";
            request.ContentType = "application/json";
            string body = "{\"sessionId\":\"" + _lastClickedSession.SessionId + "\",\"windowHandle\":\"" + handleStr + "\"}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            request.ContentLength = data.Length;
            using (var stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse()) { }
            Log("Window handle updated successfully");
        }
        catch (Exception ex)
        {
            Log("Failed to update window handle: " + ex.Message);
        }
    }

    private void FlashLinkedWindow()
    {
        if (_lastClickedSession == null || string.IsNullOrEmpty(_lastClickedSession.WindowHandle))
        {
            Log("No window handle to flash");
            return;
        }

        try
        {
            IntPtr hwnd = new IntPtr(long.Parse(_lastClickedSession.WindowHandle));
            if (!NativeMethods.IsWindow(hwnd))
            {
                Log("Window handle is invalid");
                return;
            }

            // Flash the window 5 times
            for (int i = 0; i < 5; i++)
            {
                NativeMethods.ShowWindow(hwnd, 6); // SW_MINIMIZE
                System.Threading.Thread.Sleep(100);
                NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
                System.Threading.Thread.Sleep(100);
            }
            Log("Flashed window " + _lastClickedSession.WindowHandle);
        }
        catch (Exception ex)
        {
            Log("Flash error: " + ex.Message);
        }
    }

    private void ActivateClaudeWindow(string sessionId, string windowHandle)
    {
        // If we have a window handle, use it directly
        if (!string.IsNullOrEmpty(windowHandle))
        {
            try
            {
                IntPtr hwnd = new IntPtr(long.Parse(windowHandle));
                if (NativeMethods.IsWindow(hwnd))
                {
                    // Get window position before activating
                    NativeMethods.RECT rect;
                    if (NativeMethods.GetWindowRect(hwnd, out rect))
                    {
                        Log("Activating window " + windowHandle + " at position (" + rect.Left + "," + rect.Top + ")");
                    }
                    // Force bring window to foreground
                    ForceForegroundWindow(hwnd);
                    return;
                }
                else
                {
                    Log("Window handle " + windowHandle + " is not valid");
                }
            }
            catch (Exception ex)
            {
                Log("Error activating window: " + ex.Message);
            }
        }
        else
        {
            Log("No window handle for session " + sessionId);
        }
        // 不再 fallback 到其他窗口，避免激活错误的窗口
    }

    private void ForceForegroundWindow(IntPtr hwnd) {
        // 方法1: Alt 键技巧（解决 Windows 前台窗口限制）
        NativeMethods.keybd_event(0x12, 0, 0, 0);  // Alt down
        NativeMethods.keybd_event(0x12, 0, 2, 0);  // Alt up

        // 如果最小化，先恢复
        if (NativeMethods.IsIconic(hwnd)) {
            NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
        }

        // 尝试直接切换
        if (NativeMethods.SetForegroundWindow(hwnd)) {
            NativeMethods.SetFocus(hwnd);
            return;
        }

        // 备用: 线程附加方式
        IntPtr foregroundHwnd = NativeMethods.GetForegroundWindow();
        int currentThread = NativeMethods.GetCurrentThreadId();
        int foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
        int targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, IntPtr.Zero);

        if (currentThread != targetThread) {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        }

        NativeMethods.ShowWindow(hwnd, 5); // SW_SHOW
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.SetFocus(hwnd);

        if (currentThread != targetThread) {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private string GetJsonString(string json, string key)
    {
        string search = "\"" + key + "\":\"";
        int start = json.IndexOf(search);
        if (start < 0) return "";
        start += search.Length;
        int end = json.IndexOf("\"", start);
        if (end < 0) return "";
        string value = json.Substring(start, end - start);
        return DecodeJsonString(value);
    }

    private string DecodeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                if (next == 'u' && i + 5 < s.Length)
                {
                    // \uXXXX
                    string hex = s.Substring(i + 2, 4);
                    try
                    {
                        int code = Convert.ToInt32(hex, 16);
                        sb.Append((char)code);
                        i += 5;
                    }
                    catch
                    {
                        sb.Append(s[i]);
                    }
                }
                else if (next == 'n')
                {
                    sb.Append('\n');
                    i++;
                }
                else if (next == 'r')
                {
                    sb.Append('\r');
                    i++;
                }
                else if (next == 't')
                {
                    sb.Append('\t');
                    i++;
                }
                else if (next == '\\')
                {
                    sb.Append('\\');
                    i++;
                }
                else if (next == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    // 静态 Mutex 确保不会被 GC 回收
    private static System.Threading.Mutex appMutex = null;

    static void Main()
    {
        // 全局异常处理
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => {
            LogStatic("ThreadException: " + e.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => {
            LogStatic("UnhandledException: " + ((Exception)e.ExceptionObject).Message);
        };

        // 单实例检测 - 先检查服务器上注册的进程
        bool createdNew = true;

        // 第一步：通过服务器检查是否有 Monitor 正在运行
        try
        {
            var statusRequest = (HttpWebRequest)WebRequest.Create("http://localhost:18989/monitor-status");
            statusRequest.Proxy = null;
            statusRequest.Timeout = 2000;
            using (var response = statusRequest.GetResponse())
            using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
            {
                string json = reader.ReadToEnd();
                // 简单解析 JSON
                if (json.Contains("\"running\":true") && json.Contains("\"pid\""))
                {
                    int pidStart = json.IndexOf("\"pid\":") + 6;
                    int pidEnd = json.IndexOf(",", pidStart);
                    if (pidEnd < 0) pidEnd = json.IndexOf("}", pidStart);
                    if (pidStart > 5 && pidEnd > pidStart)
                    {
                        string pidStr = json.Substring(pidStart, pidEnd - pidStart).Trim();
                        int existingPid;
                        if (int.TryParse(pidStr, out existingPid))
                        {
                            try
                            {
                                var existingProcess = Process.GetProcessById(existingPid);
                                if (existingProcess != null && !existingProcess.HasExited)
                                {
                                    LogStatic("Main: Monitor already running with pid " + existingPid + ", notifying show-window");
                                    // 通知现有实例显示窗口
                                    try
                                    {
                                        var showRequest = (HttpWebRequest)WebRequest.Create("http://localhost:18989/show-window");
                                        showRequest.Proxy = null;
                                        showRequest.Timeout = 1000;
                                        showRequest.Method = "POST";
                                        showRequest.ContentLength = 0;
                                        using (var resp = showRequest.GetResponse()) { }
                                    }
                                    catch { }
                                    return;
                                }
                            }
                            catch { /* 进程不存在 */ }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogStatic("Main: Status check failed: " + ex.Message);
        }

        // 第二步：使用 Mutex 作为额外保护
        try
        {
            appMutex = new System.Threading.Mutex(true, "ClaudeMonitor_SingleInstance_v3", out createdNew);
            if (!createdNew)
            {
                LogStatic("Main: Another instance running (Mutex), notifying show-window");
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/show-window");
                    request.Proxy = null;
                    request.Timeout = 1000;
                    request.Method = "POST";
                    request.ContentLength = 0;
                    using (var resp = request.GetResponse()) { }
                }
                catch { }
                return;
            }
        }
        catch (Exception ex)
        {
            LogStatic("Main: Mutex creation failed: " + ex.Message);
        }

        LogStatic("Main: Starting new instance, createdNew=" + createdNew);

        // 添加退出事件处理
        Application.ApplicationExit += (s, e) => {
            LogStatic("Main: ApplicationExit event triggered");
        };

        try
        {
            var form = new ClaudeMonitor();
            LogStatic("Main: Form created, calling Application.Run");
            Application.Run(form);
            LogStatic("Main: Application.Run ended normally");
        }
        catch (Exception ex)
        {
            LogStatic("Main: Application.Run exception: " + ex.Message + "\n" + ex.StackTrace);
        }

        LogStatic("Main: Exiting, releasing mutex");

        // 释放 Mutex
        try { appMutex.ReleaseMutex(); }
        catch { }
    }

    static void LogStatic(string msg)
    {
        try
        {
            var logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "monitor.log");
            var dir = Path.GetDirectoryName(logFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
        }
        catch { }
    }
}

class SessionData
{
    public string id, project, state, task, windowHandle;
    public string model;
    public string context;
    public string branch;
}

class SessionControl : Panel
{
    private PixelHorse horse;
    private Label statusLbl, projectLbl, infoLbl, taskLbl;
    private System.Windows.Forms.Timer flashTimer;
    private string lastState = "";
    private int flashCount = 0;
    private ToolTip toolTip;
    private string _currentTask = "";
    private string _sessionId = "";
    private string _windowHandle = "";
    private int _requiredHeight = 65;
    private bool _flashBorder = false;
    private Color _borderColor = Color.FromArgb(243, 156, 18); // #f39c12
    private int _animTick = 0;

    public string CurrentTask { get { return _currentTask; } private set { _currentTask = value; } }
    public string SessionId { get { return _sessionId; } }
    public string WindowHandle { get { return _windowHandle; } }
    public int RequiredHeight { get { return _requiredHeight; } }

    public SessionControl()
    {
        this.Height = 65;
        this.BackColor = Color.FromArgb(36, 36, 56);
        this.Cursor = Cursors.Hand;

        // Pixel horse indicator (24x20, 2px pixels - smaller)
        // Horse vertically centered relative to status + info lines (about 28px total)
        horse = new PixelHorse();
        horse.Location = new Point(5, 5);  // Centered: (28-20)/2 + 4 = 8, but slightly up looks better
        horse.Size = new Size(24, 20);

        statusLbl = new Label() { Location = new Point(32, 4), Size = new Size(55, 14), ForeColor = Color.FromArgb(180, 180, 180), BackColor = Color.Transparent, Font = new Font("Segoe UI", 8) };
        projectLbl = new Label() { Location = new Point(88, 4), Size = new Size(207, 14), ForeColor = Color.FromArgb(102, 126, 234), BackColor = Color.Transparent, Font = new Font("Segoe UI", 8, FontStyle.Bold) };

        // Info label for model/context/branch (right of horse, second row)
        infoLbl = new Label();
        infoLbl.Location = new Point(32, 18);
        infoLbl.Size = new Size(263, 14);
        infoLbl.ForeColor = Color.FromArgb(140, 140, 140);
        infoLbl.BackColor = Color.Transparent;
        infoLbl.Font = new Font("Segoe UI", 7.5f);

        taskLbl = new Label();
        taskLbl.Location = new Point(5, 34);
        taskLbl.Size = new Size(290, 40);
        taskLbl.ForeColor = Color.White;
        taskLbl.BackColor = Color.Transparent;
        taskLbl.Font = new Font("Segoe UI", 9);
        taskLbl.AutoSize = false;

        toolTip = new ToolTip();
        toolTip.InitialDelay = 500;
        toolTip.ShowAlways = true;

        flashTimer = new System.Windows.Forms.Timer();
        flashTimer.Interval = 150;
        flashTimer.Tick += FlashTick;

        // Make all child controls also trigger click on parent
        foreach (Control c in new Control[] { horse, statusLbl, projectLbl, infoLbl, taskLbl })
        {
            c.MouseClick += (s, e) => { this.OnMouseClick(e); };
            c.Cursor = Cursors.Hand;
        }

        this.Controls.AddRange(new Control[] { horse, statusLbl, projectLbl, infoLbl, taskLbl });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_flashBorder && _animTick % 2 == 0)
        {
            // 绘制橙色边框（2px）
            using (Pen pen = new Pen(_borderColor, 2))
            {
                e.Graphics.DrawRectangle(pen, 1, 1, this.Width - 2, this.Height - 2);
            }
        }
    }

    private void FlashTick(object sender, EventArgs e)
    {
        flashCount++;
        if (flashCount % 2 == 0)
            this.BackColor = Color.FromArgb(46, 204, 113);
        else
            this.BackColor = Color.FromArgb(36, 36, 56);

        if (flashCount >= 6)
        {
            flashTimer.Stop();
            this.BackColor = Color.FromArgb(36, 36, 56);
        }
    }

    public void Animate()
    {
        _animTick++;
        horse.Animate();
        if (_flashBorder) this.Invalidate(); // 触发重绘
    }

    public void UpdateData(SessionData data)
    {
        _sessionId = data.id ?? "";
        _windowHandle = data.windowHandle ?? "";
        // Debug log
        try {
            var logFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "monitor.log");
            var dir = System.IO.Path.GetDirectoryName(logFile);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] UpdateData: sessionId=" + _sessionId + ", windowHandle=" + _windowHandle + ", project=" + data.project + "\n");
        } catch { }
        projectLbl.Text = data.project;

        // DEBUG: Log update
        System.Diagnostics.Debug.WriteLine("UpdateData: state=" + data.state + ", project=" + data.project);

        // Build info line: model | context ctx | branch
        var infoParts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(data.model))
            infoParts.Add(data.model);
        if (!string.IsNullOrEmpty(data.context))
            infoParts.Add(data.context + " ctx");
        if (!string.IsNullOrEmpty(data.branch))
            infoParts.Add(data.branch);
        infoLbl.Text = string.Join(" | ", infoParts);

        // Adjust infoLbl visibility
        bool hasInfo = infoParts.Count > 0;
        infoLbl.Visible = hasInfo;

        CurrentTask = data.task;
        string taskText = string.IsNullOrEmpty(data.task) ? "..." : data.task;

        // Task label is below the info line
        taskLbl.Top = 34;

        // Calculate required height for task text (max 100px for task area)
        int taskHeight = CalculateTextHeight(taskText, 290);
        int maxTaskHeight = 100;
        if (taskHeight > maxTaskHeight) {
            taskHeight = maxTaskHeight;
            // Show full text in tooltip
            toolTip.SetToolTip(taskLbl, taskText);
        } else if (taskText.Length > 50) {
            toolTip.SetToolTip(taskLbl, taskText);
        } else {
            toolTip.RemoveAll();
        }

        // Calculate total height: header(34) + taskHeight + padding(10)
        _requiredHeight = 34 + taskHeight + 10;
        _requiredHeight = Math.Max(_requiredHeight, 50); // minimum height
        _requiredHeight = Math.Min(_requiredHeight, 140); // maximum height per session

        this.Height = _requiredHeight;
        taskLbl.Height = taskHeight;
        taskLbl.Text = taskText;

        if (data.state == "complete" && lastState != "complete")
        {
            flashCount = 0;
            flashTimer.Start();
        }

        if (data.state != lastState)
        {
            horse.SetState(data.state);
        }

        lastState = data.state;

        if (data.state == "idle") {
            statusLbl.Text = "Idle";
            _flashBorder = false;
            statusLbl.ForeColor = Color.FromArgb(180, 180, 180);
        }
        else if (data.state == "waiting") {
            statusLbl.Text = "Waiting";
            _flashBorder = true;
            statusLbl.ForeColor = Color.FromArgb(243, 156, 18); // 橙色
            taskLbl.Text = " " + taskText;
        }
        else if (data.state == "thinking") { statusLbl.Text = "Thinking"; }
        else if (data.state == "working") { statusLbl.Text = "Working"; }
        else if (data.state == "complete") {
            statusLbl.Text = "Done";
            // Show completion message with checkmark
            if (!string.IsNullOrEmpty(data.task)) {
                taskLbl.Text = "✓ " + taskText;
                taskLbl.ForeColor = Color.FromArgb(46, 204, 113);
            }
        }
        else if (data.state == "error") { statusLbl.Text = "Error"; }
        else { statusLbl.Text = data.state; }

        // Reset task label color for non-complete states
        if (data.state != "complete") {
            taskLbl.ForeColor = Color.White;
        }
    }

    private int CalculateTextHeight(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return 20;

        // Use TextRenderer to measure text
        using (Graphics g = this.CreateGraphics())
        {
            SizeF size = g.MeasureString(text, taskLbl.Font, width);
            return (int)Math.Ceiling(size.Height);
        }
    }
}

// Pixel art horse control with 6 states and animations
class PixelHorse : Control
{
    private string _state = "idle";
    private int _animTick = 0;
    private SolidBrush _brush;
    private const int PixelSize = 2;

    // Color palette (low saturation warm tones)
    private static readonly Color CMain = Color.FromArgb(212, 165, 116);    // #d4a574
    private static readonly Color CDark = Color.FromArgb(160, 128, 96);     // #a08060
    private static readonly Color CMane = Color.FromArgb(138, 122, 90);     // #8a7a5a
    private static readonly Color CNose = Color.FromArgb(192, 128, 64);     // #c08040
    private static readonly Color CWhite = Color.White;

    // State colors
    private static readonly Color CIdle = Color.FromArgb(122, 122, 122);    // #7a7a7a
    private static readonly Color CWaiting = Color.FromArgb(196, 163, 90); // #c4a35a
    private static readonly Color CThinking = Color.FromArgb(212, 165, 116); // #d4a574
    private static readonly Color CWorking = Color.FromArgb(184, 149, 106); // #b8956a
    private static readonly Color CComplete = Color.FromArgb(90, 154, 106); // #5a9a6a
    private static readonly Color CError = Color.FromArgb(138, 74, 74);     // #8a4a4a

    public PixelHorse()
    {
        this.Size = new Size(24, 20);
        _brush = new SolidBrush(Color.White);

        // Enable proper painting and transparent background
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        this.BackColor = Color.Transparent;
    }

    public void SetState(string state)
    {
        _state = state ?? "idle";
        _animTick = 0;
        this.Invalidate();
    }

    public void Animate()
    {
        _animTick++;
        this.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;

        // Get state colors
        Color main, dark, mane, nose;
        GetStateColors(out main, out dark, out mane, out nose);

        int p = PixelSize;
        int phase = _animTick % 4;

        switch (_state)
        {
            case "idle":
                DrawHorseIdle(g, main, dark, mane, p);
                break;
            case "waiting":
                DrawHorseWaiting(g, main, dark, mane, nose, p, phase);
                break;
            case "thinking":
                DrawHorseThinking(g, main, dark, mane, nose, p);
                break;
            case "working":
                DrawHorseWorking(g, main, dark, mane, nose, p, phase);
                break;
            case "complete":
                DrawHorseComplete(g, main, dark, mane, nose, p);
                break;
            case "error":
                DrawHorseError(g, main, dark, mane, p);
                break;
            default:
                DrawHorseIdle(g, main, dark, mane, p);
                break;
        }
    }

    private void GetStateColors(out Color main, out Color dark, out Color mane, out Color nose)
    {
        switch (_state)
        {
            case "idle":
                main = CIdle; dark = Color.FromArgb(90, 90, 90); mane = Color.FromArgb(106, 106, 106); nose = Color.FromArgb(106, 106, 106);
                break;
            case "waiting":
                main = CWaiting; dark = Color.FromArgb(138, 122, 74); mane = Color.FromArgb(160, 128, 64); nose = Color.FromArgb(160, 128, 64);
                break;
            case "thinking":
                main = CThinking; dark = CDark; mane = CMane; nose = CNose;
                break;
            case "working":
                main = CWorking; dark = Color.FromArgb(122, 96, 64); mane = Color.FromArgb(138, 112, 64); nose = Color.FromArgb(160, 128, 64);
                break;
            case "complete":
                main = CComplete; dark = Color.FromArgb(58, 112, 74); mane = Color.FromArgb(74, 128, 90); nose = Color.FromArgb(74, 128, 90);
                break;
            case "error":
                main = CError; dark = Color.FromArgb(90, 42, 42); mane = Color.FromArgb(106, 58, 58); nose = Color.FromArgb(106, 58, 58);
                break;
            default:
                main = CIdle; dark = Color.FromArgb(90, 90, 90); mane = Color.FromArgb(106, 106, 106); nose = Color.FromArgb(106, 106, 106);
                break;
        }
    }

    private void DrawPixel(Graphics g, int x, int y, int w, int h, Color c)
    {
        _brush.Color = c;
        g.FillRectangle(_brush, x * PixelSize, y * PixelSize, w * PixelSize, h * PixelSize);
    }

    // IDLE: Standing still, eyes closed, tail down
    private void DrawHorseIdle(Graphics g, Color main, Color dark, Color mane, int p)
    {
        // Ears
        DrawPixel(g, 2, 0, 1, 1, main);
        DrawPixel(g, 3, 0, 1, 2, dark);
        DrawPixel(g, 7, 0, 1, 2, dark);
        DrawPixel(g, 8, 0, 1, 1, main);

        // Mane
        DrawPixel(g, 4, 0, 3, 2, mane);

        // Head
        DrawPixel(g, 1, 2, 8, 3, main);

        // Eyes closed (horizontal line)
        DrawPixel(g, 2, 3, 2, 1, dark);
        DrawPixel(g, 5, 3, 2, 1, dark);

        // Nose
        DrawPixel(g, 2, 4, 3, 1, mane);

        // Neck
        DrawPixel(g, 5, 5, 4, 1, main);

        // Body
        DrawPixel(g, 1, 5, 10, 3, main);

        // Legs standing
        DrawPixel(g, 2, 8, 2, 3, dark);
        DrawPixel(g, 4, 8, 2, 3, dark);
        DrawPixel(g, 7, 8, 2, 3, dark);
        DrawPixel(g, 9, 8, 2, 3, dark);

        // Tail down
        DrawPixel(g, 10, 5, 1, 3, mane);
        DrawPixel(g, 11, 6, 1, 4, mane);
    }

    // WAITING: Alert, ears twitching, front hoof tapping
    private void DrawHorseWaiting(Graphics g, Color main, Color dark, Color mane, Color nose, int p, int phase)
    {
        // Ears twitching
        Color earColor = phase < 2 ? dark : mane;
        DrawPixel(g, 2, 0, 1, 1, main);
        DrawPixel(g, 3, 0, 1, 2, earColor);
        DrawPixel(g, 7, 0, 1, 2, earColor);
        DrawPixel(g, 8, 0, 1, 1, main);

        // Mane
        DrawPixel(g, 4, 0, 3, 2, mane);

        // Head alert
        DrawPixel(g, 1, 2, 8, 3, main);

        // Eyes open
        DrawPixel(g, 2, 3, 2, 1, CWhite);
        DrawPixel(g, 5, 3, 2, 1, CWhite);

        // Nose
        DrawPixel(g, 2, 4, 3, 1, nose);

        // Body
        DrawPixel(g, 1, 5, 10, 3, main);

        // Legs - front hoof tapping
        int leg1Y = phase == 0 ? 7 : 8;
        int leg2Y = phase == 2 ? 7 : 8;
        DrawPixel(g, 2, leg1Y, 2, 11 - leg1Y, dark);
        DrawPixel(g, 4, 8, 2, 3, dark);
        DrawPixel(g, 7, 8, 2, 3, dark);
        DrawPixel(g, 9, leg2Y, 2, 11 - leg2Y, dark);

        // Tail swishing
        int tailY = phase < 2 ? 4 : 6;
        DrawPixel(g, 10, 5, 1, 2, mane);
        DrawPixel(g, 11, tailY, 1, 2, mane);
    }

    // THINKING: Eyes blinking
    private void DrawHorseThinking(Graphics g, Color main, Color dark, Color mane, Color nose, int p)
    {
        // Ears
        DrawPixel(g, 2, 0, 1, 1, main);
        DrawPixel(g, 3, 0, 1, 2, dark);
        DrawPixel(g, 7, 0, 1, 2, dark);
        DrawPixel(g, 8, 0, 1, 1, main);

        // Mane
        DrawPixel(g, 4, 0, 3, 2, mane);

        // Head
        DrawPixel(g, 1, 2, 8, 3, main);

        // Eyes blinking
        Color eyeColor = _animTick % 2 == 0 ? CWhite : main;
        DrawPixel(g, 2, 3, 2, 1, eyeColor);
        DrawPixel(g, 5, 3, 2, 1, eyeColor);

        // Nose
        DrawPixel(g, 2, 4, 3, 1, nose);

        // Body
        DrawPixel(g, 1, 5, 10, 3, main);

        // Legs standing
        DrawPixel(g, 2, 8, 2, 3, dark);
        DrawPixel(g, 4, 8, 2, 3, dark);
        DrawPixel(g, 7, 8, 2, 3, dark);
        DrawPixel(g, 9, 8, 2, 3, dark);

        // Tail
        DrawPixel(g, 10, 5, 1, 3, mane);
        DrawPixel(g, 11, 6, 1, 3, mane);
    }

    // WORKING: Running, legs alternating
    private void DrawHorseWorking(Graphics g, Color main, Color dark, Color mane, Color nose, int p, int phase)
    {
        // Ears back
        DrawPixel(g, 3, 0, 1, 1, main);
        DrawPixel(g, 4, 0, 1, 2, dark);
        DrawPixel(g, 6, 0, 1, 2, dark);
        DrawPixel(g, 7, 0, 1, 1, main);

        // Mane flowing
        DrawPixel(g, 5, 0, 1, 2, mane);
        DrawPixel(g, 2, 2, 1, 2, dark);

        // Head forward
        DrawPixel(g, 0, 3, 8, 2, main);

        // Eyes focused
        DrawPixel(g, 1, 3, 2, 1, CWhite);
        DrawPixel(g, 4, 3, 2, 1, CWhite);

        // Nose
        DrawPixel(g, 0, 4, 3, 1, nose);

        // Body stretched
        DrawPixel(g, 2, 5, 9, 3, main);

        // Legs running
        int[] legY = new int[4];
        int[] legH = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int legPhase = (phase + i) % 4;
            if (legPhase == 0 || legPhase == 1) { legY[i] = 6; legH[i] = 4; }
            else { legY[i] = 8; legH[i] = 3; }
        }
        DrawPixel(g, 1, legY[0], 2, legH[0], dark);
        DrawPixel(g, 4, legY[1], 2, legH[1], dark);
        DrawPixel(g, 7, legY[2], 2, legH[2], dark);
        DrawPixel(g, 9, legY[3], 2, legH[3], dark);

        // Tail flowing
        DrawPixel(g, 10, 4, 1, 2, mane);
        DrawPixel(g, 11, 3, 1, 3, dark);
    }

    // COMPLETE: Happy, jumping, tail up
    private void DrawHorseComplete(Graphics g, Color main, Color dark, Color mane, Color nose, int p)
    {
        // Flashing effect
        Color bodyMain = _animTick % 2 == 0 ? main : Color.FromArgb(90, 186, 130);
        Color bodyDark = _animTick % 2 == 0 ? dark : main;

        // Ears
        DrawPixel(g, 2, 0, 1, 1, bodyMain);
        DrawPixel(g, 3, 0, 1, 2, bodyDark);
        DrawPixel(g, 7, 0, 1, 2, bodyDark);
        DrawPixel(g, 8, 0, 1, 1, bodyMain);

        // Mane
        DrawPixel(g, 4, 0, 3, 2, mane);

        // Head happy
        DrawPixel(g, 1, 2, 8, 3, bodyMain);

        // Eyes happy (^^)
        DrawPixel(g, 2, 3, 1, 1, bodyDark);
        DrawPixel(g, 3, 3, 1, 1, bodyMain);
        DrawPixel(g, 5, 3, 1, 1, bodyMain);
        DrawPixel(g, 6, 3, 1, 1, bodyDark);

        // Nose smile
        DrawPixel(g, 2, 4, 3, 1, nose);

        // Body
        DrawPixel(g, 1, 5, 10, 3, bodyMain);

        // Legs jumping
        DrawPixel(g, 2, 7, 2, 4, bodyDark);
        DrawPixel(g, 4, 8, 2, 3, bodyDark);
        DrawPixel(g, 7, 7, 2, 4, bodyDark);
        DrawPixel(g, 9, 8, 2, 3, bodyDark);

        // Tail up
        DrawPixel(g, 10, 4, 1, 2, mane);
        DrawPixel(g, 11, 3, 1, 2, mane);
    }

    // ERROR: Head down, X eyes, ears drooping
    private void DrawHorseError(Graphics g, Color main, Color dark, Color mane, int p)
    {
        // Ears drooping
        DrawPixel(g, 2, 2, 2, 1, main);
        DrawPixel(g, 7, 2, 2, 1, main);

        // Mane messy
        DrawPixel(g, 4, 2, 3, 2, mane);

        // Head down
        DrawPixel(g, 1, 3, 8, 3, main);

        // X eyes
        DrawPixel(g, 2, 3, 1, 1, dark);
        DrawPixel(g, 3, 4, 1, 1, dark);
        DrawPixel(g, 3, 3, 1, 1, main);
        DrawPixel(g, 2, 4, 1, 1, main);
        DrawPixel(g, 5, 3, 1, 1, dark);
        DrawPixel(g, 6, 4, 1, 1, dark);
        DrawPixel(g, 6, 3, 1, 1, main);
        DrawPixel(g, 5, 4, 1, 1, main);

        // Nose
        DrawPixel(g, 2, 5, 3, 1, mane);

        // Body
        DrawPixel(g, 1, 6, 10, 2, main);

        // Legs slumped
        DrawPixel(g, 2, 8, 2, 3, dark);
        DrawPixel(g, 4, 9, 2, 2, dark);
        DrawPixel(g, 7, 9, 2, 2, dark);
        DrawPixel(g, 9, 8, 2, 3, dark);

        // Tail down
        DrawPixel(g, 10, 6, 1, 2, mane);
        DrawPixel(g, 11, 7, 1, 3, dark);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _brush != null)
            _brush.Dispose();
        base.Dispose(disposing);
    }
}

class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}