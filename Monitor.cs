using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// 全局常量类，供所有类使用
static class Constants
{
    public const string SERVER_URL = "http://localhost:18989";
    public const string HEALTH_ENDPOINT = "/health";
    public const string STATUS_ENDPOINT = "/monitor-status";
    public const string HEARTBEAT_ENDPOINT = "/monitor-heartbeat";
    public const string STARTED_ENDPOINT = "/monitor-started";
    public const string STOPPING_ENDPOINT = "/monitor-stopping";  // 新增：优雅退出通知
    public const string SHOW_WINDOW_ENDPOINT = "/show-window";
    public const string SESSIONS_ENDPOINT = "/status";
    public const string SESSION_DELETE_ENDPOINT = "/session/";
    public const int HEALTH_TIMEOUT = 1000;
    public const int WAIT_TIMEOUT = 500;
    public const int MAX_WAIT_ITERATIONS = 100;  // 10 seconds
}

class ClaudeHub : Form
{
    // Win32 常量
    private const int GW_HWNDPREV = 3;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int DWMWA_CLOAKED = 14;
    private const int MaxWindowTraversal = 50;
    private const int MinWindowArea = 100;

    // Helper for window handle validation
    private static bool IsValidWindowHandle(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);
    }

    // 排除的系统窗口类名
    private static readonly string[] ExcludedWindowClasses = {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Start", "Windows.UI.Core.CoreWindow",
        "IslandWindow", "PerryShadowWnd", "WindowsTerminalShadowWnd", "ApplicationFrame_DropShadow"
    };

    private Panel contentPanel;
    private System.Windows.Forms.Timer timer;
    private System.Windows.Forms.Timer animationTimer;
    private System.Windows.Forms.Timer heartbeatTimer;
    private System.Windows.Forms.Timer positionSaveTimer;
    private List<SessionControl> sessionControls = new List<SessionControl>();
    private Button pinBtn;
    private Label waitingLabel;
    private bool hasWaitingSession = false;
    private bool suppressPositionSave = false;
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
        // Main() 已经调用 EnsureServerRunning() 启动了 server
        // 这里只需要检查是否运行，记录状态
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + Constants.HEALTH_ENDPOINT);
            request.Proxy = null;
            request.Timeout = Constants.HEALTH_TIMEOUT;
            using (var response = request.GetResponse()) { }
            Log("Server already running");
        }
        catch
        {
            Log("Server not reachable after Main() startup");
        }
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

    public ClaudeHub()
    {
        try
        {
            Log("ClaudeHub constructor started");

            // 启动 server 子进程
            StartServer();

            this.Text = "Claude Hub";
        this.Size = new Size(320, 148);
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(22, 22, 21);

        suppressPositionSave = true;
        LoadWindowPosition();
        suppressPositionSave = false;

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
        this.LocationChanged += (s, e) => ScheduleWindowPositionSave();
        this.FormClosing += (s, e) => {
            Log("FormClosing event, CloseReason: " + e.CloseReason);
            SaveWindowPosition();
            // 退出前通知 Server（优雅退出）
            NotifyStopping();
        };
        this.FormClosed += (s, e) => {
            Log("FormClosed event");
        };

        Panel header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 24;
        header.BackColor = Color.FromArgb(31, 30, 28);
        header.Cursor = Cursors.SizeAll;
        header.MouseDown += FormMouseDown;

        Label title = new Label();
        title.Text = " Claude Hub";
        title.Dock = DockStyle.Fill;
        title.ForeColor = Color.FromArgb(236, 239, 246);
        title.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Cursor = Cursors.SizeAll;
        title.MouseDown += FormMouseDown;

        pinBtn = new Button();
        pinBtn.Text = "\uE718";
        pinBtn.Width = 24;
        pinBtn.Height = 24;
        pinBtn.Dock = DockStyle.Right;
        pinBtn.FlatStyle = FlatStyle.Flat;
        pinBtn.BackColor = Color.FromArgb(43, 42, 38);
        pinBtn.ForeColor = Color.FromArgb(218, 225, 236);
        pinBtn.Font = new Font("Segoe MDL2 Assets", 10f);
        pinBtn.Cursor = Cursors.Hand;
        pinBtn.FlatAppearance.BorderSize = 0;
        pinBtn.Margin = new Padding(2);
        pinBtn.TextAlign = ContentAlignment.MiddleCenter;
        ToolTip pinTip = new ToolTip();
        pinTip.SetToolTip(pinBtn, "钉在顶层");
        pinBtn.Click += (s, e) => {
            this.TopMost = !this.TopMost;
            pinBtn.BackColor = this.TopMost ? Color.FromArgb(80, 153, 106) : Color.FromArgb(43, 42, 38);
        };
        pinBtn.MouseEnter += (s, e) => {
            pinBtn.BackColor = Color.FromArgb(55, 53, 48);
        };
        pinBtn.MouseLeave += (s, e) => {
            pinBtn.BackColor = this.TopMost ? Color.FromArgb(80, 153, 106) : Color.FromArgb(43, 42, 38);
        };

        Button closeBtn = new Button();
        closeBtn.Text = "\uE711";
        closeBtn.Width = 24;
        closeBtn.Height = 24;
        closeBtn.Dock = DockStyle.Right;
        closeBtn.FlatStyle = FlatStyle.Flat;
        closeBtn.BackColor = Color.FromArgb(43, 42, 38);
        closeBtn.ForeColor = Color.FromArgb(218, 225, 236);
        closeBtn.Font = new Font("Segoe MDL2 Assets", 8f);
        closeBtn.Cursor = Cursors.Hand;
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Margin = new Padding(2);
        closeBtn.TextAlign = ContentAlignment.MiddleCenter;
        ToolTip closeTip = new ToolTip();
        closeTip.SetToolTip(closeBtn, "关闭会话");
        closeBtn.Click += (s, e) => { timer.Stop(); animationTimer.Stop(); heartbeatTimer.Stop(); SaveWindowPosition(); this.Close(); };
        closeBtn.MouseEnter += (s, e) => {
            closeBtn.BackColor = Color.FromArgb(231, 76, 60);
        };
        closeBtn.MouseLeave += (s, e) => {
            closeBtn.BackColor = Color.FromArgb(43, 42, 38);
        };

        // Waiting 提示标签（初始隐藏）
        waitingLabel = new Label();
        waitingLabel.Text = " ⏳ Waiting for Input";
        waitingLabel.Text = " Waiting";
        waitingLabel.Width = 78;
        waitingLabel.Height = 24;
        waitingLabel.Dock = DockStyle.Left;
        waitingLabel.BackColor = Color.FromArgb(181, 127, 52);
        waitingLabel.ForeColor = Color.FromArgb(255, 248, 229);
        waitingLabel.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        waitingLabel.Visible = false;

        header.Controls.AddRange(new Control[] { waitingLabel, closeBtn, pinBtn, title });

        contentPanel = new Panel();
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.BackColor = Color.FromArgb(22, 22, 21);
        contentPanel.MouseDown += FormMouseDown;

        // Session 右键菜单（在 SessionControl 上显示）
        // 主窗口无右键菜单
        this.ContextMenuStrip = null;

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

        positionSaveTimer = new System.Windows.Forms.Timer();
        positionSaveTimer.Interval = 600;
        positionSaveTimer.Tick += (s, e) => {
            positionSaveTimer.Stop();
            SaveWindowPosition();
        };

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
        PostPidToServer(Constants.HEARTBEAT_ENDPOINT, 2000);
    }

    private void SendStarted() {
        PostPidToServer(Constants.STARTED_ENDPOINT, 2000);
        Log("Sent started with pid: " + Process.GetCurrentProcess().Id);
    }

    private void NotifyStopping() {
        PostPidToServer(Constants.STOPPING_ENDPOINT, 1000);
        Log("NotifyStopping sent for pid: " + Process.GetCurrentProcess().Id);
    }

    // Helper: POST PID to server endpoint
    private void PostPidToServer(string endpoint, int timeoutMs) {
        try {
            var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + endpoint);
            request.Proxy = null;
            request.Timeout = timeoutMs;
            request.Method = "POST";
            request.ContentType = "application/json";
            string body = "{\"pid\":" + Process.GetCurrentProcess().Id + "}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            request.ContentLength = data.Length;
            using (var stream = request.GetRequestStream()) {
                stream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse()) { }
        } catch (Exception ex) { Log("PostPidToServer error (" + endpoint + "): " + ex.Message); }
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
        this.Location = new Point(workingArea.Right - 340, workingArea.Bottom - 168);
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

    private long GetJsonLong(string json, string key)
    {
        string search = "\"" + key + "\":";
        int start = json.IndexOf(search);
        if (start < 0) return 0;
        start += search.Length;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        int end = start;
        while (end < json.Length && (json[end] >= '0' && json[end] <= '9')) end++;
        if (end > start) return long.Parse(json.Substring(start, end - start));
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

    private void ScheduleWindowPositionSave()
    {
        if (suppressPositionSave || positionSaveTimer == null) return;
        positionSaveTimer.Stop();
        positionSaveTimer.Start();
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
        RememberExternalForegroundWindow();
        foreach (var ctrl in sessionControls)
        {
            ctrl.Animate();
        }
    }

    private void UpdateStatus(object sender, EventArgs e)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + Constants.SESSIONS_ENDPOINT);
            request.Proxy = null;
            request.Timeout = Constants.HEALTH_TIMEOUT;
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
        bool inString = false;  // Track if we're inside a string (FIX: was missing)
        int braceBalance = 0;   // Track actual brace balance within sessions array

        for (int i = sessionsStart; i < json.Length && depth > 0; i++)
        {
            char c = json[i];

            // Skip characters inside strings (except escaped quotes)
            if (inString)
            {
                if (c == '\\' && i + 1 < json.Length) i++;  // Skip escaped char
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{')
            {
                braceBalance++;
                if (braceBalance == 1) objStart = i;
            }
            else if (c == '}')
            {
                braceBalance--;
                if (braceBalance == 0 && objStart >= 0)
                {
                    string obj = json.Substring(objStart, i - objStart + 1);
                    var s = new SessionData();
                    s.id = GetJsonString(obj, "id");
                    s.project = GetJsonString(obj, "project");
                    s.state = GetJsonString(obj, "state");
                    s.task = GetJsonString(obj, "task");
                    s.windowHandle = GetJsonString(obj, "windowHandle");
                    s.model = GetJsonString(obj, "model");
                    s.effort = GetJsonString(obj, "effort");
                    s.source = GetJsonString(obj, "source");
                    s.context = GetJsonString(obj, "context");
                    s.branch = GetJsonString(obj, "branch");
                    s.userMessage = GetJsonString(obj, "userMessage");
                    s.lastUpdate = GetJsonLong(obj, "lastUpdate");
                    s.needsHandleRecapture = GetJsonBool(obj, "needsHandleRecapture");
                    list.Add(s);
                    objStart = -1;
                }
            }
            else if (c == ']') depth = 0;
        }
        Log("ParseSessions: parsed " + list.Count + " sessions");
        return list;
    }

    private void UpdateSessionUI(List<SessionData> sessions)
    {
        Log("UpdateSessionUI: sessions=" + sessions.Count + ", controls=" + sessionControls.Count);

        // 使用 SuspendLayout 减少重绘，避免闪烁
        contentPanel.SuspendLayout();
        this.SuspendLayout();

        try
        {
            // Remove extra controls
            while (sessionControls.Count > sessions.Count)
            {
                var ctrl = sessionControls[sessionControls.Count - 1];
                ctrl.OnToggleWindow = null;
                contentPanel.Controls.Remove(ctrl);
                ctrl.Dispose();
                sessionControls.RemoveAt(sessionControls.Count - 1);
            }

            // Add new controls if needed
            while (sessionControls.Count < sessions.Count)
            {
                Log("Creating SessionControl, count=" + sessionControls.Count);
                var ctrl = new SessionControl();
                ctrl.Left = 5;
                ctrl.Width = this.ClientSize.Width - 10;
                ctrl.OnToggleWindow = (sc) => {
                    Log("Toggle window: " + sc.SessionId);
                    ToggleClaudeWindow(sc.SessionId);
                };
                contentPanel.Controls.Add(ctrl);
                sessionControls.Add(ctrl);
                Log("Added SessionControl, count=" + sessionControls.Count);
            }

            // Update data and calculate positions
            int totalHeight = 0;
            for (int i = 0; i < sessions.Count; i++)
            {
                int controlWidth = this.ClientSize.Width - 10;
                int controlTop = totalHeight + 5;
                if (sessionControls[i].Left != 5) sessionControls[i].Left = 5;
                if (sessionControls[i].Width != controlWidth) sessionControls[i].Width = controlWidth;
                if (sessionControls[i].Top != controlTop) sessionControls[i].Top = controlTop;
                sessionControls[i].UpdateData(sessions[i]);
                totalHeight += sessionControls[i].RequiredHeight + 4; // 4px gap
                Log("Session " + i + ": height=" + sessionControls[i].RequiredHeight + ", top=" + totalHeight);
            }

            // Adjust window height (header + content + padding)
            int windowHeight = 24 + totalHeight + 8;
            this.Height = Math.Min(windowHeight, 500);
            Log("Window height=" + windowHeight);

            // 检测是否有 waiting 状态的会话
            hasWaitingSession = sessions.Any(s => s.state == "waiting");
            waitingLabel.Visible = hasWaitingSession;

            // 清理已不存在的会话的 lastActivatedHandle 条目
            var activeSessionIds = new HashSet<string>(sessions.Select(s => s.id));
            var keysToRemove = lastActivatedHandle.Keys.Where(k => !activeSessionIds.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                lastActivatedHandle.Remove(key);
            }
        }
        finally
        {
            contentPanel.ResumeLayout();
            this.ResumeLayout();
        }
    }

    /// 检测两个矩形是否重叠
    private bool RectsOverlap(NativeMethods.RECT a, NativeMethods.RECT b)
    {
        return !(a.Right < b.Left || a.Left > b.Right ||
                 a.Bottom < b.Top || a.Top > b.Bottom);
    }

    /// 检测窗口是否被其他窗口遮挡
    private bool IsWindowObscured(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return true;

        NativeMethods.RECT targetRect;
        if (!NativeMethods.GetWindowRect(hwnd, out targetRect)) return true;

        Log("IsWindowObscured: target hwnd=" + hwnd + " rect=" + targetRect.Left + "," + targetRect.Top + "-" + targetRect.Right + "," + targetRect.Bottom);

        IntPtr prevWnd = NativeMethods.GetWindow(hwnd, GW_HWNDPREV);
        int checkedCount = 0;
        while (prevWnd != IntPtr.Zero && checkedCount < MaxWindowTraversal)
        {
            checkedCount++;

            if (prevWnd == this.Handle)
            {
                prevWnd = NativeMethods.GetWindow(prevWnd, GW_HWNDPREV);
                continue;
            }

            if (NativeMethods.IsWindowVisible(prevWnd) && !NativeMethods.IsIconic(prevWnd))
            {
                NativeMethods.RECT prevRect;
                if (NativeMethods.GetWindowRect(prevWnd, out prevRect))
                {
                    if (RectsOverlap(targetRect, prevRect))
                    {
                        int prevArea = (prevRect.Right - prevRect.Left) * (prevRect.Bottom - prevRect.Top);
                        if (prevArea > MinWindowArea)
                        {
                            StringBuilder classBuf = new StringBuilder(256);
                            NativeMethods.GetClassName(prevWnd, classBuf, classBuf.Capacity);
                            string className = classBuf.ToString();

                            bool isExcluded = false;
                            foreach (string excluded in ExcludedWindowClasses)
                            {
                                if (className.StartsWith(excluded))
                                {
                                    isExcluded = true;
                                    break;
                                }
                            }

                            if (!isExcluded)
                            {
                                Log("IsWindowObscured: obscured by hwnd=" + prevWnd + " class=" + className);
                                return true;
                            }
                        }
                    }
                }
            }
            prevWnd = NativeMethods.GetWindow(prevWnd, GW_HWNDPREV);
        }
        Log("IsWindowObscured: not obscured, checked=" + checkedCount);
        return false;
    }

    /// 检测窗口是否被系统隐藏（DWM cloaked）
    private bool IsWindowCloaked(IntPtr hwnd)
    {
        int cloaked = 0;
        try
        {
            NativeMethods.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, 4);
        }
        catch { }  // DWM API 在某些旧系统可能不可用
        return cloaked != 0;
    }

    /// 综合判断窗口激活状态
    private WindowActivationState GetWindowActivationState(IntPtr hwnd)
    {
        // 1. 窗口句柄无效
        if (!NativeMethods.IsWindow(hwnd))
            return WindowActivationState.Invalid;

        // 2. 窗口最小化
        if (NativeMethods.IsIconic(hwnd))
            return WindowActivationState.Minimized;

        // 3. 窗口隐藏
        if (!NativeMethods.IsWindowVisible(hwnd))
            return WindowActivationState.Hidden;

        // 4. 系统隐藏（cloaked）
        if (IsWindowCloaked(hwnd))
            return WindowActivationState.Cloaked;

        // 5. 检查是否被遮挡（先检查，因为遮挡状态优先于前台判断）
        if (IsWindowObscured(hwnd))
            return WindowActivationState.Obscured;

        // 6. 检查是否是前台窗口
        IntPtr foreground = GetComparableTopLevelWindow(NativeMethods.GetForegroundWindow());
        IntPtr target = GetComparableTopLevelWindow(hwnd);
        IntPtr monitorRoot = GetComparableTopLevelWindow(this.Handle);
        Log("Foreground check: hwnd=" + hwnd + " target=" + target + " foreground=" + foreground + " monitor=" + monitorRoot);

        // 如果前台窗口是 Monitor 自己，返回特殊状态
        if (foreground == monitorRoot)
        {
            return WindowActivationState.ForegroundIsMonitor;  // 特殊状态
        }

        if (target != IntPtr.Zero && target == foreground)
            return WindowActivationState.Foreground;

        // 7. 可见但不是前台
        return WindowActivationState.VisibleButNotForeground;
    }

    private IntPtr GetComparableTopLevelWindow(IntPtr hwnd)
    {
        if (!IsValidWindowHandle(hwnd))
            return IntPtr.Zero;

        IntPtr rootOwner = NativeMethods.GetAncestor(hwnd, GA_ROOTOWNER);
        if (IsValidWindowHandle(rootOwner))
            return rootOwner;

        IntPtr root = NativeMethods.GetAncestor(hwnd, GA_ROOT);
        if (IsValidWindowHandle(root))
            return root;

        return hwnd;
    }

    private void ToggleClaudeWindow(string sessionId)
    {
        Log("Toggle: sessionId=" + sessionId);
        IntPtr hwnd = IntPtr.Zero;
        bool hasSessionHandle = false;

        try
        {
            string handleFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "window-handles.json");
            if (File.Exists(handleFile))
            {
                string json = File.ReadAllText(handleFile);
                string pattern = "\"" + System.Text.RegularExpressions.Regex.Escape(sessionId) + "\"\\s*:\\s*\\{[^}]*\"handle\"\\s*:\\s*\"?([0-9]+)";
                var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
                if (match.Success)
                {
                    string handleStr = match.Groups[1].Value;
                    hwnd = new IntPtr(long.Parse(handleStr));
                    hasSessionHandle = true;
                    Log("Read handle from file: " + hwnd);
                }
            }
        }
        catch (Exception ex) { Log("Error reading handle file: " + ex.Message); }

        if (hwnd != IntPtr.Zero && !NativeMethods.IsWindow(hwnd))
        {
            Log("Handle " + hwnd + " is invalid");
            hwnd = IntPtr.Zero;
            hasSessionHandle = false;
        }

        if (hwnd == IntPtr.Zero)
        {
            hwnd = FindTerminalWindow();
            if (hwnd == IntPtr.Zero && !hasSessionHandle)
            {
                Log("No session handle and terminal fallback is ambiguous; requesting recapture for " + sessionId);
                RequestHandleRecapture(sessionId);
                return;
            }
        }

        if (hwnd != IntPtr.Zero && hwnd != this.Handle)
        {
            try
            {
                IntPtr normalizedHwnd = GetComparableTopLevelWindow(hwnd);
                if (normalizedHwnd != IntPtr.Zero)
                {
                    hwnd = normalizedHwnd;
                }

                WindowActivationState state = GetWindowActivationState(hwnd);
                Log("Terminal window state: " + state + " for hwnd=" + hwnd);

                if (state == WindowActivationState.Minimized ||
                    state == WindowActivationState.Cloaked ||
                    state == WindowActivationState.Hidden ||
                    state == WindowActivationState.Obscured ||
                    state == WindowActivationState.VisibleButNotForeground)
                {
                    ActivateWindow(hwnd, sessionId);
                }
                else if (state == WindowActivationState.Foreground)
                {
                    MinimizeWindow(hwnd, sessionId);
                }
                else if (state == WindowActivationState.ForegroundIsMonitor)
                {
                    IntPtr lastHwnd;
                    if ((lastActivatedHandle.TryGetValue(sessionId, out lastHwnd) && lastHwnd == hwnd) ||
                        WasRecentlyForegroundBeforeMonitor(hwnd))
                        MinimizeWindow(hwnd, sessionId);
                    else
                        ActivateWindow(hwnd, sessionId);
                }
            }
            catch (Exception ex) { Log("Toggle error: " + ex.Message); }
        }
        else
        {
            Log("No valid terminal window found");
        }
    }

    private void RequestHandleRecapture(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + "/session/" + sessionId + "/recapture-handle");
            request.Proxy = null;
            request.Timeout = Constants.HEALTH_TIMEOUT;
            request.Method = "POST";
            request.ContentType = "application/json";
            byte[] data = Encoding.UTF8.GetBytes("{}");
            request.ContentLength = data.Length;
            using (var stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse()) { }
        }
        catch (Exception ex)
        {
            Log("RequestHandleRecapture error: " + ex.Message);
        }
    }

    private void ActivateWindow(IntPtr hwnd, string sessionId)
    {
        Log("Activating hwnd=" + hwnd);
        ForceForegroundWindow(hwnd);
        lastActivatedHandle[sessionId] = hwnd;
    }

    private void MinimizeWindow(IntPtr hwnd, string sessionId)
    {
        Log("Minimizing hwnd=" + hwnd);
        NativeMethods.ShowWindow(hwnd, SW_MINIMIZE);
        lastActivatedHandle.Remove(sessionId);
    }

    // All terminal windows, indexed by order of discovery
    private List<IntPtr> allTerminalWindows = new List<IntPtr>();

    // 记住每个会话上次激活的窗口句柄，用于 toggle 逻辑
    private Dictionary<string, IntPtr> lastActivatedHandle = new Dictionary<string, IntPtr>();
    private IntPtr lastExternalForegroundWindow = IntPtr.Zero;
    private DateTime lastExternalForegroundAt = DateTime.MinValue;

    private void RememberExternalForegroundWindow()
    {
        try
        {
            IntPtr foreground = GetComparableTopLevelWindow(NativeMethods.GetForegroundWindow());
            IntPtr monitor = GetComparableTopLevelWindow(this.Handle);
            if (foreground != IntPtr.Zero && foreground != monitor && NativeMethods.IsWindow(foreground))
            {
                lastExternalForegroundWindow = foreground;
                lastExternalForegroundAt = DateTime.UtcNow;
            }
        }
        catch { }
    }

    private bool WasRecentlyForegroundBeforeMonitor(IntPtr hwnd)
    {
        IntPtr target = GetComparableTopLevelWindow(hwnd);
        if (target == IntPtr.Zero || target != lastExternalForegroundWindow)
            return false;

        return (DateTime.UtcNow - lastExternalForegroundAt).TotalMilliseconds <= 2000;
    }

    // 查找终端窗口（Windows Terminal）
    private IntPtr FindTerminalWindow()
    {
        IntPtr selfHwnd = this.Handle;

        // 枚举所有 CASCADIA_HOSTING_WINDOW_CLASS 窗口
        allTerminalWindows.Clear();
        NativeMethods.EnumWindows((hwnd, lParam) => {
            if (hwnd == selfHwnd) return true;
            try
            {
                StringBuilder classBuf = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, classBuf, classBuf.Capacity);
                if (classBuf.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS" && NativeMethods.IsWindow(hwnd))
                {
                    allTerminalWindows.Add(hwnd);
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        if (allTerminalWindows.Count == 0) return IntPtr.Zero;

        // 只有一个窗口：直接返回
        if (allTerminalWindows.Count == 1)
        {
            return allTerminalWindows[0];
        }

        Log("FindTerminalWindow: " + allTerminalWindows.Count + " terminal windows found, refusing to guess");
        return IntPtr.Zero;
    }

    private void ForceForegroundWindow(IntPtr hwnd) {
        // Validate handle before any operation
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) {
            Log("ForceForegroundWindow: invalid handle");
            return;
        }

        // 方法1: SendInput Alt 键技巧（替代已弃用的 keybd_event，解决 Windows 前台窗口限制）
        try {
            NativeMethods.SendAltKey();
        } catch (Exception ex) {
            Log("SendAltKey error: " + ex.Message);
        }

        // 如果最小化，先恢复
        try {
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.IsIconic(hwnd)) {
                NativeMethods.ShowWindow(hwnd, SW_RESTORE);
            }
        } catch (Exception ex) {
            Log("Restore window error: " + ex.Message);
            return;
        }

        // 尝试直接切换
        try {
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.SetForegroundWindow(hwnd)) {
                NativeMethods.SetFocus(hwnd);
                return;
            }
        } catch (Exception ex) {
            Log("SetForegroundWindow direct error: " + ex.Message);
        }

        // 备用: 线程附加方式（增加安全检查）
        try {
            IntPtr foregroundHwnd = NativeMethods.GetForegroundWindow();
            int foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
            int targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, IntPtr.Zero);

            // 安全检查：确保线程 ID 有效且不相同
            if (foregroundThread > 0 && targetThread > 0 && foregroundThread != targetThread) {
                // 检查是否已经是附加状态（避免递归附加导致死锁）
                bool attached1 = NativeMethods.AttachThreadInput(foregroundThread, targetThread, true);
                if (attached1) {
                    try {
                        if (NativeMethods.IsWindow(hwnd)) {
                            NativeMethods.ShowWindow(hwnd, SW_SHOW);
                            NativeMethods.SetForegroundWindow(hwnd);
                            NativeMethods.SetFocus(hwnd);
                        }
                    } finally {
                        NativeMethods.AttachThreadInput(foregroundThread, targetThread, false);
                    }
                }
            } else if (NativeMethods.IsWindow(hwnd)) {
                // 同一线程或无效线程，直接操作
                NativeMethods.ShowWindow(hwnd, SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.SetFocus(hwnd);
            }
        } catch (Exception ex) {
            Log("Thread attach error: " + ex.Message);
        }
    }

    private string GetJsonString(string json, string key)
    {
        string search = "\"" + key + "\":\"";
        int start = json.IndexOf(search);
        if (start < 0) {
            // 也尝试查找 "key":value 格式（非字符串值如 null）
            string searchNull = "\"" + key + "\":null";
            if (json.IndexOf(searchNull) >= 0) return "";
            return "";
        }
        start += search.Length;
        int end = start;
        bool escaped = false;
        while (end < json.Length)
        {
            char c = json[end];
            if (escaped)
            {
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                break;
            }
            end++;
        }
        if (end >= json.Length) return "";
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

    // 尝试启动 server（静态方法，在 Main 中使用）
    static bool EnsureServerRunning()
    {
        // 检查是否已运行
        if (CheckHealth()) return true;

        string hookDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "E:\\Code\\claude-monitor";
        string serverPath = Path.Combine(hookDir, "server.js");
        if (!File.Exists(serverPath))
        {
            LogStatic("EnsureServerRunning: server.js not found at: " + serverPath);
            return false;
        }

        // 尝试启动 server（主方案用 cmd.exe，备用用 node）
        if (TryStartServer("cmd.exe", "/c start /b node \"" + serverPath + "\"", hookDir) ||
            TryStartServer("node", "\"" + serverPath + "\"", hookDir))
        {
            return WaitForServerReady();
        }
        return false;
    }

    // 检查 server 健康状态
    static bool CheckHealth()
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + Constants.HEALTH_ENDPOINT);
            request.Proxy = null;
            request.Timeout = Constants.HEALTH_TIMEOUT;
            using (var response = request.GetResponse()) { }
            return true;
        }
        catch { return false; }
    }

    // 启动 server 进程
    static bool TryStartServer(string fileName, string arguments, string workingDir)
    {
        try
        {
            var proc = new Process();
            proc.StartInfo.FileName = fileName;
            proc.StartInfo.Arguments = arguments;
            proc.StartInfo.WorkingDirectory = workingDir;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.Start();
            LogStatic("Server process started: " + fileName + ", pid=" + proc.Id);
            return true;
        }
        catch (Exception ex)
        {
            LogStatic("Failed to start with " + fileName + ": " + ex.Message);
            return false;
        }
    }

    // 等待 server 就绪
    static bool WaitForServerReady()
    {
        for (int i = 0; i < Constants.MAX_WAIT_ITERATIONS; i++)
        {
            System.Threading.Thread.Sleep(100);
            if (CheckHealth())
            {
                LogStatic("Server ready after " + (i * 100) + "ms");
                return true;
            }
        }
        LogStatic("Server startup timeout after " + (Constants.MAX_WAIT_ITERATIONS * 100) + "ms");
        return false;
    }

    // 通过 Server 注册自己，如果已有其他实例则返回 false
    static bool RegisterWithServer()
    {
        try
        {
            var statusRequest = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + Constants.STATUS_ENDPOINT);
            statusRequest.Proxy = null;
            statusRequest.Timeout = 2000;
            using (var response = statusRequest.GetResponse())
            using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
            {
                string json = reader.ReadToEnd();
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
                            // 如果PID是自己的PID，说明Server刚启动我们，继续运行
                            int myPid = Process.GetCurrentProcess().Id;
                            if (existingPid == myPid)
                            {
                                LogStatic("RegisterWithServer: PID is my own pid " + myPid + ", continuing");
                                return true;
                            }
                            try
                            {
                                var existingProcess = Process.GetProcessById(existingPid);
                                if (existingProcess != null && !existingProcess.HasExited)
                                {
                                    LogStatic("RegisterWithServer: Monitor already running with pid " + existingPid);
                                    return false;
                                }
                            }
                            catch { /* 进程不存在 */ }
                        }
                    }
                }
            }
            return true;  // 可以启动
        }
        catch (Exception ex)
        {
            LogStatic("RegisterWithServer error: " + ex.Message);
            return true;  // Server 可能刚启动，允许继续
        }
    }

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

        // 第一步：确保 Server 运行（这是关键改进）
        if (!EnsureServerRunning())
        {
            LogStatic("Main: Failed to start server, exiting");
            return;
        }

        // 第二步：通过 Server 检查是否已有 Monitor 运行
        if (!RegisterWithServer())
        {
            LogStatic("Main: Monitor already running, notifying show-window");
            try
            {
                var showRequest = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + Constants.SHOW_WINDOW_ENDPOINT);
                showRequest.Proxy = null;
                showRequest.Timeout = Constants.HEALTH_TIMEOUT;
                showRequest.Method = "POST";
                showRequest.ContentLength = 0;
                using (var resp = showRequest.GetResponse()) { }
            }
            catch { }
            return;
        }

        // 第三步：使用 Mutex 作为本地额外保护（防止极端情况）
        bool createdNew = true;
        try
        {
            appMutex = new System.Threading.Mutex(true, "ClaudeHub_SingleInstance_v4", out createdNew);
            if (!createdNew)
            {
                LogStatic("Main: Mutex indicates another instance, exiting");
                return;
            }
        }
        catch (Exception ex)
        {
            LogStatic("Main: Mutex creation failed: " + ex.Message);
        }

        LogStatic("Main: Starting new instance");

        // 添加退出事件处理
        Application.ApplicationExit += (s, e) => {
            LogStatic("Main: ApplicationExit event triggered");
        };

        try
        {
            var form = new ClaudeHub();
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
    public string effort;
    public string source;
    public string context;
    public string branch;
    public string userMessage;
    public long lastUpdate;
    public bool needsHandleRecapture;  // Flag for window rebind
}

class SessionControl : Panel
{
    private PixelHorse horse;
    private Label statusLbl, projectLbl, sepLbl, branchLbl, modelLbl, ctxSepLbl, contextLbl, taskLbl, userMsgLbl, expandBtn, deleteBtn, clickLayerLbl;
    private System.Windows.Forms.Timer flashTimer;
    private string lastState = "";
    private int flashCount = 0;
    private ToolTip toolTip;
    private string _currentTask = "";
    private string _sessionId = "";
    private string _windowHandle = "";
    private long _lastUpdate = 0;
    private int _requiredHeight = 65;
    private bool _flashBorder = false;
    private bool _flashStoppedByClick = false;
    private bool _isCodexSession = false;
    private Color _borderColor = Color.FromArgb(243, 156, 18);
    private int _animTick = 0;
    private bool _expanded = false;
    private Color _accentColor = Color.FromArgb(126, 148, 226);
    private Color _cardBack = CardBack;
    private Color _cardBackAlt = CardBackAlt;
    private const int CardPadding = 5;
    private const int ActionSize = 18;
    private static readonly Color CardBack = Color.FromArgb(30, 32, 38);
    private static readonly Color CardBackAlt = Color.FromArgb(38, 41, 48);
    private static readonly Color CodexCardBack = Color.FromArgb(30, 32, 38);
    private static readonly Color CodexCardBackAlt = Color.FromArgb(38, 41, 48);
    private static readonly Color ClaudeBrand = Color.FromArgb(209, 135, 79);
    private static readonly Color CodexBrand = Color.FromArgb(116, 185, 158);
    private static readonly Color TextPrimary = Color.FromArgb(235, 238, 245);
    private static readonly Color TextMuted = Color.FromArgb(152, 161, 178);

    private string _fullTaskText = "";
    private ContextMenuStrip sessionContextMenu;  // Right-click menu for window rebind
    private System.Windows.Forms.Timer _recaptureFeedbackTimer;  // Timer for recapture visual feedback

    private void Log(string msg)
    {
        try
        {
            var logFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "monitor.log");
            var dir = System.IO.Path.GetDirectoryName(logFile);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
        }
        catch { }
    }

    public Action<SessionControl> OnToggleWindow;

    public string CurrentTask { get { return _currentTask; } private set { _currentTask = value; } }
    public string SessionId { get { return _sessionId; } }
    public string WindowHandle { get { return _windowHandle; } }
    public long LastUpdate { get { return _lastUpdate; } }
    public int RequiredHeight { get { return _requiredHeight; } }

    public SessionControl()
    {
        this.Height = 54;
        this.BackColor = CardBack;
        this.Cursor = Cursors.Hand;
        this.Padding = new Padding(0);
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        // Pixel horse indicator (24x20, 2px pixels - smaller)
        // Horse vertically centered relative to status + info lines (about 28px total)
        horse = new PixelHorse();
        horse.Location = new Point(6, 5);  // Centered in the compact card header.
        horse.Size = new Size(24, 24);

        projectLbl = new Label() { Location = new Point(33, 3), Size = new Size(132, 14), ForeColor = Color.FromArgb(126, 148, 226), BackColor = Color.Transparent, Font = new Font("Segoe UI", 8, FontStyle.Bold), AutoSize = false, AutoEllipsis = true };
        sepLbl = new Label() { Location = new Point(165, 3), Size = new Size(14, 14), ForeColor = Color.FromArgb(91, 99, 116), BackColor = Color.Transparent, Font = new Font("Segoe UI", 8), Text = "|", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        branchLbl = new Label() { Location = new Point(181, 3), Size = new Size(76, 14), ForeColor = Color.FromArgb(102, 176, 128), BackColor = Color.Transparent, Font = new Font("Segoe UI", 8), AutoSize = false, AutoEllipsis = true };

        // 折叠按钮 - 放在消息右边
        expandBtn = new Label();
        expandBtn.Text = "\uE70D";
        expandBtn.Size = new Size(ActionSize, ActionSize);
        expandBtn.Location = new Point(283, 34);  // 初始位置，会在 UpdateLayout 中更新
        expandBtn.ForeColor = Color.FromArgb(178, 187, 202);
        expandBtn.BackColor = CardBackAlt;
        expandBtn.Font = new Font("Segoe MDL2 Assets", 8.5f);
        expandBtn.Cursor = Cursors.Hand;
        expandBtn.TextAlign = ContentAlignment.MiddleCenter;
        expandBtn.MouseClick += (s, e) => { ToggleExpand(); };

        // 删除按钮 - 右上角
        deleteBtn = new Label();
        deleteBtn.Text = "\uE74D";
        deleteBtn.Size = new Size(ActionSize, ActionSize);
        deleteBtn.Location = new Point(283, 4);  // 右上角
        deleteBtn.ForeColor = Color.FromArgb(219, 116, 103);
        deleteBtn.BackColor = CardBackAlt;
        deleteBtn.Font = new Font("Segoe MDL2 Assets", 8.5f);
        deleteBtn.Cursor = Cursors.Hand;
        deleteBtn.TextAlign = ContentAlignment.MiddleCenter;
        deleteBtn.MouseClick += (s, e) => { DeleteSession(); };

        // 第二行：状态 + 模型 + 上下文信息（拆分为多个 Label）
        statusLbl = new Label() { Location = new Point(33, 17), Size = new Size(66, 14), ForeColor = TextMuted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8, FontStyle.Bold) };

        // 模型 Label
        modelLbl = new Label();
        modelLbl.Location = new Point(99, 17);
        modelLbl.Size = new Size(78, 14);
        modelLbl.ForeColor = TextMuted;
        modelLbl.BackColor = Color.Transparent;
        modelLbl.Font = new Font("Segoe UI", 8);

        // 分隔符 " | "
        ctxSepLbl = new Label();
        ctxSepLbl.Location = new Point(177, 17);
        ctxSepLbl.Size = new Size(14, 14);
        ctxSepLbl.ForeColor = Color.FromArgb(91, 99, 116);
        ctxSepLbl.BackColor = Color.Transparent;
        ctxSepLbl.Font = new Font("Segoe UI", 8);
        ctxSepLbl.Text = "|";

        // 上下文 Label（仅显示，无点击事件）
        contextLbl = new Label();
        contextLbl.Location = new Point(191, 17);
        contextLbl.Size = new Size(72, 14);
        contextLbl.ForeColor = TextMuted;
        contextLbl.BackColor = Color.Transparent;
        contextLbl.Font = new Font("Segoe UI", 8);

        // 用户消息 Label（浅蓝色）- 固定一行，超出显示省略号
        userMsgLbl = new Label();
        userMsgLbl.Name = "userMsgLbl";
        userMsgLbl.Location = new Point(CardPadding, 32);
        userMsgLbl.Size = new Size(270, 16);
        userMsgLbl.ForeColor = Color.FromArgb(116, 182, 224);
        userMsgLbl.BackColor = Color.Transparent;
        userMsgLbl.Font = new Font("Segoe UI", 8);
        userMsgLbl.AutoSize = false;
        userMsgLbl.AutoEllipsis = true;
        userMsgLbl.Text = "";

        taskLbl = new Label();
        taskLbl.Name = "taskLbl";
        taskLbl.Location = new Point(CardPadding, 52);
        taskLbl.Size = new Size(270, 24);
        taskLbl.ForeColor = TextPrimary;
        taskLbl.BackColor = Color.Transparent;
        taskLbl.Font = new Font("Segoe UI", 8);
        taskLbl.AutoSize = false;

        toolTip = new ToolTip();
        toolTip.InitialDelay = 500;
        toolTip.ShowAlways = true;

        flashTimer = new System.Windows.Forms.Timer();
        flashTimer.Interval = 150;
        flashTimer.Tick += FlashTick;

        // 消息区域点击：最小化时激活，否则最小化
        MouseEventHandler toggleHandler = (s, e) => {
            if (e.Button == MouseButtons.Left) {
                Log("Toggle click on SessionControl, sessionId=" + _sessionId);
                StopFlashing();
                if (OnToggleWindow != null) OnToggleWindow(this);
            }
        };

        // 消息区域中间空白部分的点击层（填补 userMsgLbl 和 taskLbl 之间的空隙）
        // userMsgLbl: y=34, height=18 -> 结束于 y=52
        // taskLbl: y=62, height=28 -> 开始于 y=62
        // 中间空隙: y=52 到 y=62
        clickLayerLbl = new Label();
        clickLayerLbl.Name = "clickLayerLbl";
        clickLayerLbl.Location = new Point(CardPadding, 47);
        clickLayerLbl.Size = new Size(270, 5);  // Clickable spacer between message rows.
        clickLayerLbl.BackColor = Color.Transparent;
        clickLayerLbl.Cursor = Cursors.Hand;
        clickLayerLbl.MouseDown += toggleHandler;

        // 用户消息和AI消息区域可点击切换窗口
        userMsgLbl.MouseDown += toggleHandler;
        userMsgLbl.Cursor = Cursors.Hand;
        taskLbl.MouseDown += toggleHandler;
        taskLbl.Cursor = Cursors.Hand;

        // 折叠按钮和删除按钮不触发 SessionClick
        expandBtn.MouseDown += (s, e) => { };

        this.Controls.AddRange(new Control[] { horse, statusLbl, projectLbl, sepLbl, branchLbl, modelLbl, ctxSepLbl, contextLbl, taskLbl, userMsgLbl, expandBtn, deleteBtn, clickLayerLbl });

        // Create right-click context menu for window rebind
        sessionContextMenu = new ContextMenuStrip();
        sessionContextMenu.BackColor = CardBack;
        sessionContextMenu.ForeColor = TextPrimary;
        sessionContextMenu.Font = new Font("Segoe UI", 9);
        sessionContextMenu.ShowImageMargin = false;  // Hide icon column

        ToolStripMenuItem rebindWindowItem = new ToolStripMenuItem("重新绑定窗口");
        rebindWindowItem.BackColor = CardBack;
        rebindWindowItem.ForeColor = Color.FromArgb(217, 157, 82);  // Orange to indicate action
        rebindWindowItem.MouseHover += (s, e) => {
            rebindWindowItem.BackColor = CardBackAlt;
        };
        rebindWindowItem.MouseLeave += (s, e) => {
            rebindWindowItem.BackColor = CardBack;
        };
        rebindWindowItem.Click += (s, e) => {
            MarkSessionForRecapture();
        };

        sessionContextMenu.Items.Add(rebindWindowItem);

        // Attach context menu to clickable controls
        this.ContextMenuStrip = sessionContextMenu;
        userMsgLbl.ContextMenuStrip = sessionContextMenu;
        taskLbl.ContextMenuStrip = sessionContextMenu;
        clickLayerLbl.ContextMenuStrip = sessionContextMenu;
        horse.ContextMenuStrip = sessionContextMenu;
        statusLbl.ContextMenuStrip = sessionContextMenu;
    }

    private void ToggleExpand()
    {
        _expanded = !_expanded;
        expandBtn.Text = _expanded ? "\uE70E" : "\uE70D";
        UpdateLayout();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (taskLbl != null) UpdateLayout();
    }

    private void ApplySessionPalette()
    {
        _cardBack = _isCodexSession ? CodexCardBack : CardBack;
        _cardBackAlt = _isCodexSession ? CodexCardBackAlt : CardBackAlt;

        if (this.BackColor != _cardBack) this.BackColor = _cardBack;
        if (expandBtn != null && expandBtn.BackColor != _cardBackAlt) expandBtn.BackColor = _cardBackAlt;
        if (deleteBtn != null && deleteBtn.BackColor != _cardBackAlt) deleteBtn.BackColor = _cardBackAlt;
        if (sessionContextMenu != null && sessionContextMenu.BackColor != _cardBack) sessionContextMenu.BackColor = _cardBack;
    }

    private void UpdateLayout()
    {
        // 使用 SuspendLayout 减少重绘，避免卡顿
        this.SuspendLayout();

        int actionLeft = Math.Max(CardPadding, this.Width - CardPadding - ActionSize);
        int textRight = Math.Max(120, actionLeft - CardPadding);
        int textWidth = Math.Max(120, textRight - CardPadding);

        horse.Location = new Point(CardPadding + 1, 5);
        projectLbl.Left = 33;
        projectLbl.Top = 3;
        int metaRight = Math.Max(projectLbl.Left + 80, actionLeft - 4);
        projectLbl.Width = Math.Max(72, Math.Min(132, metaRight - projectLbl.Left));
        sepLbl.Left = projectLbl.Right + 2;
        branchLbl.Left = sepLbl.Right + 2;
        branchLbl.Width = Math.Max(34, metaRight - branchLbl.Left);

        statusLbl.Left = 33;
        statusLbl.Top = 17;
        modelLbl.Left = statusLbl.Right + 4;
        modelLbl.Width = Math.Max(78, Math.Min(128, metaRight - modelLbl.Left - 58));
        ctxSepLbl.Left = modelLbl.Right + 2;
        contextLbl.Left = ctxSepLbl.Right + 2;
        contextLbl.Width = Math.Max(42, metaRight - contextLbl.Left);

        int y = 33;

        // 用户消息 - 固定一行，超出用省略号
        bool hasUserMsg = !string.IsNullOrEmpty(userMsgLbl.Text);
        userMsgLbl.Visible = hasUserMsg;
        if (hasUserMsg)
        {
            userMsgLbl.Top = y;
            userMsgLbl.Left = CardPadding;
            userMsgLbl.Width = textWidth;
            y += 20;  // 固定间距
        }

        // AI 消息 - 默认一行，超过一行显示展开按钮
        taskLbl.Top = y;
        taskLbl.Left = CardPadding;
        taskLbl.Width = textWidth;
        int fullHeight = CalculateTextHeight(_fullTaskText, textWidth);
        bool needExpand = fullHeight > 18;

        // 设置文本和高度
        taskLbl.Text = _fullTaskText;
        if (_expanded && needExpand)
        {
            taskLbl.Height = fullHeight;
        }
        else
        {
            taskLbl.Height = 16;
        }

        // 删除按钮 - 固定在右上角
        deleteBtn.Location = new Point(actionLeft, 4);
        deleteBtn.BringToFront();

        // 展开按钮 - 在消息右边
        expandBtn.Visible = needExpand;
        if (needExpand)
        {
            expandBtn.Left = actionLeft;  // Same action column as delete.
            expandBtn.Top = y + 2;
            expandBtn.BringToFront();
        }

        clickLayerLbl.Left = CardPadding;
        clickLayerLbl.Top = Math.Max(33, y - 5);
        clickLayerLbl.Width = textWidth;

        y += taskLbl.Height + 5;

        _requiredHeight = y;
        _requiredHeight = Math.Max(_requiredHeight, 50);
        _requiredHeight = Math.Min(_requiredHeight, 200);
        if (this.Height != _requiredHeight)
        {
            this.Height = _requiredHeight;
        }

        this.ResumeLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using (SolidBrush accentBrush = new SolidBrush(_accentColor))
        {
            e.Graphics.FillRectangle(accentBrush, 0, 0, 3, this.Height);
        }
        using (Pen borderPen = new Pen(Color.FromArgb(52, 58, 74), 1))
        {
            e.Graphics.DrawRectangle(borderPen, 0, 0, this.Width - 1, this.Height - 1);
        }

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
            this.BackColor = Color.FromArgb(42, 61, 49);
        else
            this.BackColor = _cardBack;

        if (flashCount >= 6)
        {
            flashTimer.Stop();
            this.BackColor = _cardBack;
        }
    }

    public void StopFlashing()
    {
        _flashStoppedByClick = true;
        _flashBorder = false;
        this.Invalidate();
    }

    public void Animate()
    {
        _animTick++;
        horse.Animate();
        if (_flashBorder) this.Invalidate(); // 触发重绘
    }

    public void DeleteSession()
    {
        if (!string.IsNullOrEmpty(_sessionId))
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + Constants.SESSION_DELETE_ENDPOINT + _sessionId);
                request.Proxy = null;
                request.Timeout = Constants.HEALTH_TIMEOUT;
                request.Method = "DELETE";
                using (var response = request.GetResponse()) { }
            }
            catch { }
        }
    }

    public void MarkSessionForRecapture()
    {
        if (!string.IsNullOrEmpty(_sessionId))
        {
            Log("MarkSessionForRecapture: sessionId=" + _sessionId);
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(Constants.SERVER_URL + "/session/" + _sessionId + "/recapture-handle");
                request.Proxy = null;
                request.Timeout = Constants.HEALTH_TIMEOUT;
                request.Method = "POST";
                request.ContentType = "application/json";
                string body = "{}";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(body);
                request.ContentLength = data.Length;
                using (var stream = request.GetRequestStream()) {
                    stream.Write(data, 0, data.Length);
                }
                using (var response = request.GetResponse()) {
                    Log("MarkSessionForRecapture: success for " + _sessionId);
                }

                // Visual feedback: flash border orange briefly
                _flashBorder = true;
                _borderColor = Color.FromArgb(243, 156, 18);
                this.Invalidate();

                // Stop any existing feedback timer before creating new one
                if (_recaptureFeedbackTimer != null) {
                    _recaptureFeedbackTimer.Stop();
                    _recaptureFeedbackTimer.Dispose();
                }

                // Stop flashing after 2 seconds
                _recaptureFeedbackTimer = new System.Windows.Forms.Timer();
                _recaptureFeedbackTimer.Interval = 2000;
                _recaptureFeedbackTimer.Tick += (s, e) => {
                    _flashBorder = false;
                    this.Invalidate();
                    _recaptureFeedbackTimer.Stop();
                    _recaptureFeedbackTimer.Dispose();
                    _recaptureFeedbackTimer = null;
                };
                _recaptureFeedbackTimer.Start();
            }
            catch (Exception ex) {
                Log("MarkSessionForRecapture error: " + ex.Message);
            }
        }
    }

    public void UpdateData(SessionData data)
    {
        _sessionId = data.id ?? "";
        _windowHandle = data.windowHandle ?? "";
        _lastUpdate = data.lastUpdate;
        _isCodexSession = (data.source == "codex") || _sessionId.StartsWith("codex:");
        ApplySessionPalette();
        // Debug log
        try {
            var logFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-monitor", "monitor.log");
            var dir = System.IO.Path.GetDirectoryName(logFile);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] UpdateData: sessionId=" + _sessionId + ", windowHandle=" + _windowHandle + ", project=" + data.project + ", branch=" + (data.branch ?? "null") + "\n");
        } catch { }

        // 项目名
        projectLbl.Text = data.project;

        if (_isCodexSession) {
            projectLbl.ForeColor = CodexBrand;
        } else {
            projectLbl.ForeColor = ClaudeBrand;
        }

        // 分支：竖线灰色，分支名绿色
        if (!string.IsNullOrEmpty(data.branch)) {
            sepLbl.Visible = true;
            branchLbl.Text = data.branch;
            branchLbl.Visible = true;
            // 定位在项目名后面
            sepLbl.Left = projectLbl.Right + 2;
            branchLbl.Left = sepLbl.Right + 2;
        } else {
            sepLbl.Visible = false;
            branchLbl.Visible = false;
        }

        // 模型和上下文分开显示
        bool hasModel = !string.IsNullOrEmpty(data.model);
        bool hasContext = !string.IsNullOrEmpty(data.context);

        if (hasModel)
        {
            string modelText = data.model;
            if (!string.IsNullOrEmpty(data.effort)) modelText = modelText + " " + data.effort;
            if (modelLbl.Text != modelText) modelLbl.Text = modelText;
            if (!modelLbl.Visible) modelLbl.Visible = true;
        }
        else
        {
            if (modelLbl.Visible) modelLbl.Visible = false;
        }

        if (hasModel && hasContext)
        {
            if (!ctxSepLbl.Visible) ctxSepLbl.Visible = true;
        }
        else
        {
            if (ctxSepLbl.Visible) ctxSepLbl.Visible = false;
        }

        if (hasContext)
        {
            string contextText = data.context + " ctx";
            if (contextLbl.Text != contextText) contextLbl.Text = contextText;
            if (!contextLbl.Visible) contextLbl.Visible = true;
        }
        else
        {
            if (contextLbl.Visible) contextLbl.Visible = false;
        }

        // 用户消息
        if (!string.IsNullOrEmpty(data.userMessage))
        {
            userMsgLbl.Text = "> " + data.userMessage;
            userMsgLbl.Visible = true;
        }
        else
        {
            userMsgLbl.Text = "";
            userMsgLbl.Visible = false;
        }

        CurrentTask = data.task;
        _fullTaskText = string.IsNullOrEmpty(data.task) ? "..." : data.task;

        // 更新布局
        UpdateLayout();

        if (data.state == "complete" && lastState != "complete")
        {
            flashCount = 0;
            flashTimer.Start();
        }

        if (data.state != lastState)
        {
            _flashStoppedByClick = false;
            horse.SetState(data.state);
        }

        lastState = data.state;

        if (data.state == "idle") {
            statusLbl.Text = "Idle";
            _accentColor = Color.FromArgb(116, 124, 140);
            if (_flashBorder) {
                _flashBorder = false;
                this.Invalidate();
            }
            statusLbl.ForeColor = TextMuted;
        }
        else if (data.state == "waiting") {
            statusLbl.Text = "Waiting";
            _accentColor = Color.FromArgb(217, 157, 82);
            _borderColor = Color.FromArgb(217, 157, 82);
            if (!_flashBorder && !_flashStoppedByClick) {
                _flashBorder = true;
                this.Invalidate();
            }
            statusLbl.ForeColor = Color.FromArgb(227, 175, 98);
        }
        else if (data.state == "thinking") {
            statusLbl.Text = "Thinking";
            _accentColor = Color.FromArgb(195, 151, 103);
            // 从 waiting 变为 thinking 时关闭闪烁并恢复颜色
            if (_flashBorder) {
                _flashBorder = false;
                this.Invalidate();
            }
            statusLbl.ForeColor = Color.FromArgb(202, 171, 134);
        }
        else if (data.state == "working") {
            statusLbl.Text = "Working";
            _accentColor = Color.FromArgb(126, 148, 226);
            // 从 waiting 变为 working 时关闭闪烁并恢复颜色
            if (_flashBorder) {
                _flashBorder = false;
                this.Invalidate();
            }
            statusLbl.ForeColor = Color.FromArgb(158, 176, 232);
        }
        else if (data.state == "complete") {
            statusLbl.Text = "Done";
            _accentColor = Color.FromArgb(102, 176, 128);
            // 从 waiting 变为 complete 时关闭闪烁并恢复颜色
            if (_flashBorder) {
                _flashBorder = false;
                this.Invalidate();
            }
            statusLbl.ForeColor = Color.FromArgb(113, 190, 138);  // 绿色表示完成
            // Show completion message with checkmark
            if (!string.IsNullOrEmpty(data.task)) {
                taskLbl.Text = "Done: " + _fullTaskText;
                taskLbl.ForeColor = Color.FromArgb(127, 201, 151);
            }
        }
        else if (data.state == "error") {
            statusLbl.Text = "Error";
            _accentColor = Color.FromArgb(201, 88, 79);
            // 从 waiting 变为 error 时关闭闪烁
            if (_flashBorder) {
                _flashBorder = false;
                this.Invalidate();
            }
            statusLbl.ForeColor = Color.FromArgb(224, 111, 100);  // 红色表示错误
        }
        else {
            statusLbl.Text = data.state;
            _accentColor = Color.FromArgb(116, 124, 140);
            // 未知状态也关闭闪烁
            if (_flashBorder) {
                _flashBorder = false;
                this.Invalidate();
            }
            statusLbl.ForeColor = TextMuted;
        }

        statusLbl.BringToFront();

        // Reset task label color for non-complete states
        if (data.state != "complete") {
            taskLbl.ForeColor = TextPrimary;
        }
        this.Invalidate();
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
        this.Size = new Size(24, 24);
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

        int phase = _animTick % 4;

        switch (_state)
        {
            case "idle":
                DrawUnifiedHorse(g, main, dark, mane, nose, "idle", phase);
                break;
            case "waiting":
                DrawUnifiedHorse(g, main, dark, mane, nose, "waiting", phase);
                break;
            case "thinking":
                DrawUnifiedHorse(g, main, dark, mane, nose, "thinking", phase);
                break;
            case "working":
                DrawUnifiedHorse(g, main, dark, mane, nose, "working", phase);
                break;
            case "complete":
                DrawUnifiedHorse(g, main, dark, mane, nose, "complete", phase);
                break;
            case "error":
                DrawUnifiedHorse(g, main, dark, mane, nose, "error", phase);
                break;
            default:
                DrawUnifiedHorse(g, main, dark, mane, nose, "idle", phase);
                break;
        }
    }

    private void GetStateColors(out Color main, out Color dark, out Color mane, out Color nose)
    {
        switch (_state)
        {
            case "idle":
                main = CIdle; dark = Color.FromArgb(88, 92, 102); mane = Color.FromArgb(106, 110, 120); nose = Color.FromArgb(106, 110, 120);
                break;
            case "waiting":
                main = CWaiting; dark = Color.FromArgb(138, 112, 72); mane = Color.FromArgb(165, 124, 70); nose = Color.FromArgb(176, 132, 76);
                break;
            case "thinking":
                main = CThinking; dark = Color.FromArgb(153, 116, 82); mane = CMane; nose = CNose;
                break;
            case "working":
                main = CWorking; dark = Color.FromArgb(116, 92, 67); mane = Color.FromArgb(134, 105, 70); nose = Color.FromArgb(167, 119, 72);
                break;
            case "complete":
                main = CComplete; dark = Color.FromArgb(57, 111, 74); mane = Color.FromArgb(78, 132, 91); nose = Color.FromArgb(78, 132, 91);
                break;
            case "error":
                main = CError; dark = Color.FromArgb(94, 48, 47); mane = Color.FromArgb(112, 64, 61); nose = Color.FromArgb(112, 64, 61);
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

    private void DrawUnifiedHorse(Graphics g, Color main, Color dark, Color mane, Color nose, string pose, int phase)
    {
        bool waiting = pose == "waiting";
        bool working = pose == "working";
        bool complete = pose == "complete";
        bool error = pose == "error";

        int lift = complete && phase < 2 ? -1 : 0;
        int headY = 2 + lift;
        int bodyY = 5 + lift;
        Color bodyMain = complete && phase % 2 == 1 ? Color.FromArgb(105, 190, 130) : main;
        Color bodyDark = complete && phase % 2 == 1 ? main : dark;

        // Same silhouette for every state: ears, mane, head, neck, body, four legs, tail.
        if (error)
        {
            DrawPixel(g, 2, headY, 2, 1, bodyMain);
            DrawPixel(g, 7, headY, 2, 1, bodyMain);
        }
        else if (working)
        {
            DrawPixel(g, 3, headY - 2, 1, 1, bodyMain);
            DrawPixel(g, 4, headY - 2, 1, 2, bodyDark);
            DrawPixel(g, 6, headY - 2, 1, 2, bodyDark);
            DrawPixel(g, 7, headY - 2, 1, 1, bodyMain);
        }
        else
        {
            Color ear = waiting && phase % 2 == 1 ? mane : bodyDark;
            DrawPixel(g, 2, headY - 2, 1, 1, bodyMain);
            DrawPixel(g, 3, headY - 2, 1, 2, ear);
            DrawPixel(g, 7, headY - 2, 1, 2, ear);
            DrawPixel(g, 8, headY - 2, 1, 1, bodyMain);
        }

        DrawPixel(g, 4, headY - 2, 3, 2, mane);
        DrawPixel(g, 1, headY, 8, 3, bodyMain);
        DrawEyes(g, pose, bodyDark, bodyMain, phase, headY);
        DrawPixel(g, 2, headY + 2, 3, 1, nose);
        DrawPixel(g, 5, bodyY - 1, 4, 1, bodyMain);
        DrawPixel(g, 1, bodyY, 10, 3, bodyMain);
        DrawLegs(g, pose, bodyDark, phase, bodyY);
        DrawTail(g, pose, mane, bodyDark, phase, bodyY);
    }

    private void DrawEyes(Graphics g, string pose, Color dark, Color main, int phase, int headY)
    {
        if (pose == "idle")
        {
            DrawPixel(g, 2, headY + 1, 2, 1, dark);
            DrawPixel(g, 5, headY + 1, 2, 1, dark);
        }
        else if (pose == "thinking" && phase == 1)
        {
            DrawPixel(g, 2, headY + 1, 2, 1, dark);
            DrawPixel(g, 5, headY + 1, 2, 1, dark);
        }
        else if (pose == "complete")
        {
            DrawPixel(g, 2, headY + 1, 1, 1, dark);
            DrawPixel(g, 6, headY + 1, 1, 1, dark);
        }
        else if (pose == "error")
        {
            DrawPixel(g, 2, headY, 1, 1, dark);
            DrawPixel(g, 3, headY + 1, 1, 1, dark);
            DrawPixel(g, 5, headY, 1, 1, dark);
            DrawPixel(g, 6, headY + 1, 1, 1, dark);
        }
        else
        {
            DrawPixel(g, 2, headY + 1, 2, 1, CWhite);
            DrawPixel(g, 5, headY + 1, 2, 1, CWhite);
        }
    }

    private void DrawLegs(Graphics g, string pose, Color dark, int phase, int bodyY)
    {
        if (pose == "working")
        {
            int yA = phase % 2 == 0 ? bodyY + 1 : bodyY + 3;
            int yB = phase % 2 == 0 ? bodyY + 3 : bodyY + 1;
            DrawPixel(g, 2, yA, 2, 11 - yA, dark);
            DrawPixel(g, 4, yB, 2, 11 - yB, dark);
            DrawPixel(g, 7, yB, 2, 11 - yB, dark);
            DrawPixel(g, 9, yA, 2, 11 - yA, dark);
        }
        else if (pose == "waiting")
        {
            int tap = phase == 0 ? bodyY + 2 : bodyY + 3;
            DrawPixel(g, 2, tap, 2, 11 - tap, dark);
            DrawPixel(g, 4, bodyY + 3, 2, 3, dark);
            DrawPixel(g, 7, bodyY + 3, 2, 3, dark);
            DrawPixel(g, 9, bodyY + 3, 2, 3, dark);
        }
        else if (pose == "complete")
        {
            DrawPixel(g, 2, bodyY + 2, 2, 3, dark);
            DrawPixel(g, 4, bodyY + 3, 2, 2, dark);
            DrawPixel(g, 7, bodyY + 2, 2, 3, dark);
            DrawPixel(g, 9, bodyY + 3, 2, 2, dark);
        }
        else if (pose == "error")
        {
            DrawPixel(g, 2, bodyY + 3, 2, 3, dark);
            DrawPixel(g, 4, bodyY + 4, 2, 2, dark);
            DrawPixel(g, 7, bodyY + 4, 2, 2, dark);
            DrawPixel(g, 9, bodyY + 3, 2, 3, dark);
        }
        else
        {
            DrawPixel(g, 2, bodyY + 3, 2, 3, dark);
            DrawPixel(g, 4, bodyY + 3, 2, 3, dark);
            DrawPixel(g, 7, bodyY + 3, 2, 3, dark);
            DrawPixel(g, 9, bodyY + 3, 2, 3, dark);
        }
    }

    private void DrawTail(Graphics g, string pose, Color mane, Color dark, int phase, int bodyY)
    {
        if (pose == "working")
        {
            DrawPixel(g, 10, bodyY - 1, 1, 2, mane);
            DrawPixel(g, 11, bodyY - 2, 1, 3, dark);
        }
        else if (pose == "complete")
        {
            DrawPixel(g, 10, bodyY - 1, 1, 2, mane);
            DrawPixel(g, 11, bodyY - 2, 1, 2, mane);
        }
        else if (pose == "waiting")
        {
            int tailY = phase < 2 ? bodyY - 1 : bodyY + 1;
            DrawPixel(g, 10, bodyY, 1, 2, mane);
            DrawPixel(g, 11, tailY, 1, 2, mane);
        }
        else if (pose == "error")
        {
            DrawPixel(g, 10, bodyY + 1, 1, 2, mane);
            DrawPixel(g, 11, bodyY + 2, 1, 3, dark);
        }
        else
        {
            DrawPixel(g, 10, bodyY, 1, 3, mane);
            DrawPixel(g, 11, bodyY + 1, 1, 3, mane);
        }
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

/// 窗口激活状态枚举
enum WindowActivationState
{
    Invalid,           // 窗口句柄无效
    Minimized,         // 最小化
    Hidden,            // 隐藏
    Cloaked,           // 系统隐藏（如被其他窗口完全遮挡或后台预加载）
    Foreground,        // 前台激活窗口
    Obscured,          // 被其他窗口遮挡（部分或全部）
    VisibleButNotForeground,  // 可见但不是前台（未被遮挡）
    ForegroundIsMonitor       // 前台窗口是 Monitor 自己
}

class NativeMethods
{
    // Win32 API declarations...
    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);  // GW_HWNDNEXT=2, GW_HWNDPREV=3, GW_OWNER=4
    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();
    [DllImport("user32.dll")]
    public static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // SendAltKey: 使用 SendInput 替代已弃用的 keybd_event
    public static void SendAltKey() {
        try {
            var inputs = new INPUT[2];
            int structSize = Marshal.SizeOf(typeof(INPUT));

            // Alt down
            inputs[0] = new INPUT();
            inputs[0].type = 1; // INPUT_KEYBOARD
            inputs[0].U.ki.wVk = 0x12; // VK_MENU (Alt)
            inputs[0].U.ki.wScan = 0;
            inputs[0].U.ki.dwFlags = 0; // KEYEVENTF_KEYDOWN
            inputs[0].U.ki.time = 0;
            inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;

            // Alt up
            inputs[1] = new INPUT();
            inputs[1].type = 1;
            inputs[1].U.ki.wVk = 0x12;
            inputs[1].U.ki.wScan = 0;
            inputs[1].U.ki.dwFlags = 0x0002; // KEYEVENTF_KEYUP
            inputs[1].U.ki.time = 0;
            inputs[1].U.ki.dwExtraInfo = IntPtr.Zero;

            SendInput(2, inputs, structSize);
        } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // SendInput 结构 - 使用正确的大小
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;  // 1 = KEYBOARD
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
