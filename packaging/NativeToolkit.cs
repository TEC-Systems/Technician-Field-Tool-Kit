using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class NativeToolkit
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (ToolkitWindow window = new ToolkitWindow())
            {
                if (args.Length > 0 && args[0] == "/self-test") return window.SelfTest();
                Application.Run(window);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0 && args[0] == "/self-test")
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt"), error.ToString());
            else
                MessageBox.Show(error.Message, "TEC Systems Field Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class ToolkitProfile
{
    public string Name = "";
    public string Adapter = "";
    public string IPAddress = "";
    public string SubnetMask = "255.255.255.0";
    public string Gateway = "";
    public string Dns1 = "";
    public string Dns2 = "";
}

internal sealed class BmsServerProfile
{
    public string Name = "";
    public string Host = "";
    public string User = "";
}

internal sealed class ToolkitAdapter
{
    public string Name = "";
    public string Guid = "";
    public string Status = "Unknown";
    public string IP = "No IP";
    public string Mask = "";
    public string Gateway = "";
    public string Dns = "";
    public string Mac = "";
}

internal sealed class ScanHost
{
    public string IP = "";
    public string Hostname = "";
    public string Ping = "";
    public string Mac = "";
    public string Ports = "";
}

internal sealed class ToolkitTabs : TabControl
{
    public bool DarkTheme;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != 0x000F || TabCount == 0 || !IsHandleCreated) return;
        Rectangle last = GetTabRect(TabCount - 1);
        Rectangle rest = new Rectangle(last.Right, 0, Math.Max(0, Width - last.Right), last.Bottom);
        using (Graphics graphics = Graphics.FromHwnd(Handle))
        using (SolidBrush brush = new SolidBrush(DarkTheme ? Color.FromArgb(26, 34, 42) : Color.FromArgb(226, 234, 241)))
            graphics.FillRectangle(brush, rest);
    }
}

internal sealed class ToolkitWindow : Form
{
    private const string Product = "TEC Systems Field Toolkit";
    private const string Mailbox = "IT@tec-system.com";
    private static readonly Color Cobalt = Color.FromArgb(0, 67, 230);
    private static readonly Color Green = Color.FromArgb(31, 128, 78);
    private static readonly Color Slate = Color.FromArgb(75, 94, 116);
    private readonly string folder;
    private readonly string configPath;
    private readonly string logPath;
    private readonly string version;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly List<ToolkitProfile> profiles = new List<ToolkitProfile>();
    private readonly List<BmsServerProfile> bmsProfiles = new List<BmsServerProfile>();
    private readonly List<ToolkitAdapter> adapters = new List<ToolkitAdapter>();
    private readonly HashSet<ListView> themedLists = new HashSet<ListView>();
    private readonly HashSet<ComboBox> themedCombos = new HashSet<ComboBox>();
    private readonly ToolTip tips = new ToolTip();
    private Dictionary<string, object> settings = new Dictionary<string, object>();
    private bool dark;
    private bool exitRequested;
    private bool stopScan;
    private bool scanRunning;
    private string yabePath = "";
    private NotifyIcon tray;
    private bool testing;
    private ContextMenuStrip trayMenu;
    private System.Windows.Forms.Timer internetTimer;
    private SplitContainer split;
    private ToolkitTabs tabs;
    private Panel headerPanel;
    private Panel footerPanel;
    private PictureBox brandPicture;
    private Image lightBrand;
    private Image darkBrand;
    private ListView logView;
    private Label internetLabel;
    private Label statusLabel;
    private TextBox searchBox;
    private Button themeButton;
    private TextBox pingTarget;
    private CheckBox continuousPing;
    private TextBox webTarget;
    private TextBox rdpTarget;
    private TextBox networkTarget;
    private TextBox switchTarget;
    private ComboBox switchScheme;
    private TextBox telnetPort;
    private TextBox playbookText;
    private ComboBox bundleType;
    private TextBox ebiHost;
    private TextBox ebiUser;
    private TextBox ebiBackupPath;
    private TextBox bmsSiteName;
    private ListView bmsProfileView;
    private ListView profileView;
    private ComboBox adapterChoice;
    private TextBox profileName;
    private TextBox ipField;
    private TextBox maskField;
    private TextBox gatewayField;
    private TextBox dns1Field;
    private TextBox dns2Field;
    private ComboBox scanAdapter;
    private TextBox scanStart;
    private TextBox scanEnd;
    private Label scanScope;
    private Label scanStatus;
    private ListView scanView;
    private Button scanButton;

    public ToolkitWindow()
    {
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TEC Systems", "Field Toolkit");
        Directory.CreateDirectory(folder);
        configPath = Path.Combine(folder, "config.json");
        logPath = Path.Combine(folder, "TEC_FieldToolkit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        string versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
        version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "0.0.0";
        LoadSettings();
        Text = Product;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 780);
        MinimumSize = new Size(1120, 680);
        Font = new Font("Segoe UI", 9f);
        string iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "TEC Systems Field Toolkit.ico");
        if (File.Exists(iconFile)) Icon = new Icon(iconFile);
        BuildWindow();
        ConfigureTray();
        ApplyTheme();
        Shown += delegate { SetInitialSplit(); ApplyTitleBarTheme(); if (testing) return; RefreshAdapters(); RefreshProfiles(); RefreshScannerAdapters(); UpdateInternet(); internetTimer.Start(); CheckUpdates(true); Log("Startup", "OK", Product + " " + version); };
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += OnClosing;
    }

    private static string Value(IDictionary<string, object> row, string key)
    {
        object value;
        return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
    }

    private void LoadSettings()
    {
        if (!File.Exists(configPath)) return;
        try
        {
            settings = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            if (settings == null) settings = new Dictionary<string, object>();
            object mode;
            if (settings.TryGetValue("DarkMode", out mode)) dark = Convert.ToBoolean(mode);
            yabePath = Value(settings, "YabePath");
            object saved;
            if (settings.TryGetValue("SiteProfiles", out saved) && saved is IEnumerable)
            {
                foreach (object entry in (IEnumerable)saved)
                {
                    IDictionary<string, object> row = entry as IDictionary<string, object>;
                    if (row == null) continue;
                    profiles.Add(new ToolkitProfile {
                        Name = Value(row, "Name"), Adapter = Value(row, "Adapter"),
                        IPAddress = Value(row, "IPAddress"), SubnetMask = Value(row, "SubnetMask"),
                        Gateway = Value(row, "Gateway"), Dns1 = Value(row, "Dns1"), Dns2 = Value(row, "Dns2")
                    });
                }
            }
            if (settings.TryGetValue("BmsServerProfiles", out saved) && saved is IEnumerable)
            {
                foreach (object entry in (IEnumerable)saved)
                {
                    IDictionary<string, object> row = entry as IDictionary<string, object>;
                    if (row == null) continue;
                    string name = Value(row, "Name"), host = Value(row, "Host");
                    if (String.IsNullOrWhiteSpace(name) || !ValidTarget(host)) continue;
                    bmsProfiles.Add(new BmsServerProfile { Name = name, Host = host, User = Value(row, "User") });
                }
            }
            if (bmsProfiles.Count == 0 && ValidTarget(Value(settings, "EbiHost")))
                bmsProfiles.Add(new BmsServerProfile { Name = "Saved server", Host = Value(settings, "EbiHost"), User = Value(settings, "EbiUser") });
        }
        catch (Exception error) { File.AppendAllText(logPath, "Settings warning: " + error.Message + Environment.NewLine); }
    }

    private void SaveSettings()
    {
        settings["DarkMode"] = dark;
        settings["YabePath"] = yabePath;
        settings["SiteProfiles"] = profiles.Select(p => new Dictionary<string, object> {
            { "Name", p.Name }, { "Adapter", p.Adapter }, { "IPAddress", p.IPAddress },
            { "SubnetMask", p.SubnetMask }, { "Gateway", p.Gateway }, { "Dns1", p.Dns1 }, { "Dns2", p.Dns2 }
        }).ToArray();
        settings["BmsServerProfiles"] = bmsProfiles.Select(p => new Dictionary<string, object> {
            { "Name", p.Name }, { "Host", p.Host }, { "User", p.User }
        }).ToArray();
        settings.Remove("EbiHost");
        settings.Remove("EbiUser");
        File.WriteAllText(configPath, json.Serialize(settings), Encoding.UTF8);
    }

    private static Label L(string text, int x, int y, int width)
    {
        return new Label { Text = text, Left = x, Top = y, Width = width, Height = 22 };
    }

    private static TextBox T(int x, int y, int width, string text)
    {
        return new TextBox { Left = x, Top = y, Width = width, Text = text };
    }

    private Button B(Control parent, string text, int x, int y, int width, Action action, string hint, Color color)
    {
        Button button = new Button { Text = text, Left = x, Top = y, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Tag = color };
        button.FlatAppearance.BorderSize = 0;
        button.Click += delegate { try { action(); } catch (Exception error) { Fail(text, error); } };
        if (!String.IsNullOrEmpty(hint)) tips.SetToolTip(button, hint);
        parent.Controls.Add(button);
        return button;
    }

    private TabPage Page(string name)
    {
        TabPage tab = new TabPage(name) { AutoScroll = true, Padding = new Padding(0) };
        tabs.TabPages.Add(tab);
        return tab;
    }

    private void BuildWindow()
    {
        headerPanel = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Color.White };
        Panel header = headerPanel;
        Controls.Add(header);
        string logoFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "TEC Systems Full Logo Cobalt RGB.png");
        if (File.Exists(logoFile))
        {
            lightBrand = Image.FromFile(logoFile);
            darkBrand = CreateDarkBrand(lightBrand);
            brandPicture = new PictureBox { Left = 18, Top = 16, Width = 300, Height = 64, SizeMode = PictureBoxSizeMode.Zoom, Image = lightBrand };
            header.Controls.Add(brandPicture);
        }
        header.Controls.Add(new Label { Text = Product, Left = 340, Top = 18, Width = 320, Height = 32, Font = new Font("Segoe UI", 15f, FontStyle.Bold) });
        header.Controls.Add(L("Managed by TEC Systems IT", 342, 53, 250));
        header.Controls.Add(L("Search log", 670, 51, 120));
        searchBox = T(670, 75, 138, "");
        header.Controls.Add(searchBox);
        searchBox.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Enter) { SearchLog(); args.SuppressKeyPress = true; } };
        B(header, "Search", 818, 70, 72, SearchLog, "Find entries in the current technician log.", Green);
        themeButton = B(header, "Dark Mode", 900, 70, 98, ToggleTheme, "Switch between light and dark appearance.", Slate);
        header.Controls.Add(new Label { Text = "Hostname: " + Environment.MachineName, Left = 1050, Top = 18, Width = 190, Height = 22, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right });
        header.Controls.Add(new Label { Text = IsAdmin() ? "Mode: Administrator" : "Mode: Standard User", Left = 1050, Top = 69, Width = 190, Height = 22, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right });

        footerPanel = new Panel { Dock = DockStyle.Bottom, Height = 42 };
        Panel footer = footerPanel;
        Controls.Add(footer);
        statusLabel = L("Ready", 18, 11, 295);
        footer.Controls.Add(statusLabel);
        B(footer, "Check Updates", 338, 5, 130, delegate { CheckUpdates(false); }, "Check for a newer signed-off toolkit release.", Cobalt);
        internetLabel = new Label { Text = "Internet: Checking", Dock = DockStyle.Right, Width = 450, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 18, 0) };
        footer.Controls.Add(internetLabel);

        split = new SplitContainer { Dock = DockStyle.Fill };
        Controls.Add(split);
        split.BringToFront();
        tabs = new ToolkitTabs { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, Padding = new Point(17, 4) };
        tabs.DrawItem += DrawToolkitTab;
        split.Panel1.Padding = new Padding(15, 10, 6, 0);
        split.Panel1.Controls.Add(tabs);
        tabs.BringToFront();
        BuildWindowsPage(Page("Windows Troubleshooting"));
        BuildIpPage(Page("IP Shifter"));
        BuildScannerPage(Page("IP Scanner"));
        BuildNetworkPage(Page("Network Troubleshooting"));
        BuildBmsPage(Page("BMS Tools"));
        BuildFeedbackPage(Page("Feedback"));
        BuildLogPanel();
        internetTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        internetTimer.Tick += delegate { UpdateInternet(); };
    }

    private void SetInitialSplit()
    {
        split.Panel1MinSize = 650;
        split.Panel2MinSize = 260;
        split.SplitterDistance = Math.Min(870, split.Width - split.Panel2MinSize - split.SplitterWidth);
    }

    private void BuildLogPanel()
    {
        Panel panel = split.Panel2;
        panel.Padding = new Padding(8, 10, 15, 0);
        Label heading = new Label { Text = "Technician Log", Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        panel.Controls.Add(heading);
        FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, WrapContents = false };
        panel.Controls.Add(actions);
        B(actions, "Copy Path", 0, 0, 100, delegate { Clipboard.SetText(logPath); Log("Log", "OK", "Copied log path."); }, "Copy the current log file path.", Cobalt);
        B(actions, "Export Log", 0, 0, 100, ExportLog, "Save a copy of this session's log.", Green);
        B(actions, "Clear Log", 0, 0, 100, delegate { logView.Items.Clear(); }, "Clear visible rows; the saved log file stays intact.", Slate);
        logView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        logView.Columns.Add("Time", 65);
        logView.Columns.Add("Level", 60);
        logView.Columns.Add("Area", 95);
        logView.Columns.Add("Message", 520);
        panel.Controls.Add(logView);
        logView.BringToFront();
    }

    private void Log(string area, string level, string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string, string, string>(Log), area, level, message); return; }
        string time = DateTime.Now.ToString("HH:mm:ss");
        string line = String.Format("[{0}] [{1}] [{2}] {3}", time, level, area, message);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        if (logView != null)
        {
            ListViewItem row = new ListViewItem(time);
            row.SubItems.Add(level);
            row.SubItems.Add(area);
            row.SubItems.Add(message);
            logView.Items.Add(row);
            row.EnsureVisible();
        }
        if (statusLabel != null) { statusLabel.Text = level == "ERROR" ? "Error: check log" : message; statusLabel.ForeColor = level == "ERROR" ? (dark ? Color.FromArgb(255, 120, 120) : Color.Firebrick) : (dark ? Color.FromArgb(93, 214, 168) : Green); }
    }

    private void Fail(string area, Exception error)
    {
        Log(area, "ERROR", error.Message);
        MessageBox.Show(this, error.Message, area, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SearchLog()
    {
        string query = searchBox.Text.Trim();
        if (query.Length == 0) return;
        foreach (ListViewItem row in logView.Items)
        {
            if (row.SubItems.Cast<ListViewItem.ListViewSubItem>().Any(p => p.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                row.Selected = true;
                row.EnsureVisible();
                logView.Focus();
                return;
            }
        }
        MessageBox.Show(this, "No matching entry in this session's log.", "Search Log");
    }

    private void ExportLog()
    {
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt", FileName = Path.GetFileName(logPath) })
            if (dialog.ShowDialog(this) == DialogResult.OK) { File.Copy(logPath, dialog.FileName, true); Log("Log", "OK", "Exported " + dialog.FileName); }
    }

    private void ConfigureTray()
    {
        string trayFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "TEC Systems Field Toolkit Tray.ico");
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Toolkit", null, delegate { RestoreFromTray(); });
        trayMenu.Items.Add("Check Updates", null, delegate { RestoreFromTray(); CheckUpdates(false); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit Toolkit", null, delegate { ExitToolkit(); });
        tray = new NotifyIcon { Icon = File.Exists(trayFile) ? new Icon(trayFile) : Icon, Text = Product, ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += delegate { RestoreFromTray(); };
    }

    private void RestoreFromTray()
    {
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitToolkit() { exitRequested = true; Close(); }

    private void OnClosing(object sender, FormClosingEventArgs args)
    {
        if (!exitRequested && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; Hide(); return; }
        if (internetTimer != null) { internetTimer.Stop(); internetTimer.Dispose(); }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        if (trayMenu != null) trayMenu.Dispose();
        if (brandPicture != null) brandPicture.Image = null;
        if (darkBrand != null) darkBrand.Dispose();
        if (lightBrand != null) lightBrand.Dispose();
    }

    private void ToggleTheme() { dark = !dark; ApplyTheme(); SaveSettings(); }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated) return;
        try
        {
            int enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static Image CreateDarkBrand(Image source)
    {
        Bitmap tinted = new Bitmap(source.Width, source.Height);
        float[][] values = {
            new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 1, 0 },
            new float[] { 0.78f, 0.9f, 1f, 0, 1 }
        };
        using (Graphics graphics = Graphics.FromImage(tinted))
        using (System.Drawing.Imaging.ImageAttributes attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(values));
            graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return tinted;
    }

    private void DrawToolkitTab(object sender, DrawItemEventArgs args)
    {
        if (args.Index < 0 || args.Index >= tabs.TabPages.Count) return;
        bool selected = args.Index == tabs.SelectedIndex;
        Rectangle area = tabs.GetTabRect(args.Index);
        Color fill = dark ? (selected ? Color.FromArgb(39, 52, 62) : Color.FromArgb(26, 34, 42))
                          : (selected ? Color.White : Color.FromArgb(226, 234, 241));
        Color text = dark ? Color.FromArgb(235, 242, 247) : Color.FromArgb(28, 43, 55);
        using (SolidBrush brush = new SolidBrush(fill)) args.Graphics.FillRectangle(brush, area);
        TextRenderer.DrawText(args.Graphics, tabs.TabPages[args.Index].Text, tabs.Font,
            new Rectangle(area.X + 4, area.Y + 2, area.Width - 8, area.Height - 5), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (selected)
            using (SolidBrush accent = new SolidBrush(dark ? Color.FromArgb(77, 202, 162) : Cobalt))
                args.Graphics.FillRectangle(accent, area.X, area.Bottom - 3, area.Width, 3);
    }

    private void PrepareListView(ListView view)
    {
        if (!themedLists.Add(view)) return;
        view.GridLines = false;
        view.OwnerDraw = true;
        view.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs args)
        {
            Color fill = dark ? Color.FromArgb(48, 63, 74) : Color.FromArgb(232, 238, 243);
            using (SolidBrush brush = new SolidBrush(fill)) args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, args.Header.Text, view.Font,
                new Rectangle(args.Bounds.X + 7, args.Bounds.Y, args.Bounds.Width - 9, args.Bounds.Height),
                dark ? Color.FromArgb(231, 239, 244) : Color.FromArgb(34, 48, 59),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        view.DrawItem += delegate(object sender, DrawListViewItemEventArgs args)
        { if (view.View != View.Details) args.DrawDefault = true; };
        view.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs args)
        {
            Color fill = args.Item.Selected ? (dark ? Color.FromArgb(42, 91, 91) : Color.FromArgb(211, 231, 249))
                                            : (dark ? Color.FromArgb(34, 44, 53) : Color.White);
            using (SolidBrush brush = new SolidBrush(fill)) args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, args.SubItem.Text, view.Font,
                new Rectangle(args.Bounds.X + 6, args.Bounds.Y, args.Bounds.Width - 8, args.Bounds.Height),
                dark ? Color.FromArgb(233, 241, 246) : Color.FromArgb(29, 43, 56),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Pen line = new Pen(dark ? Color.FromArgb(48, 62, 72) : Color.FromArgb(233, 238, 242)))
                args.Graphics.DrawLine(line, args.Bounds.Left, args.Bounds.Bottom - 1, args.Bounds.Right, args.Bounds.Bottom - 1);
        };
    }

    private void PrepareComboBox(ComboBox box)
    {
        if (!themedCombos.Add(box)) return;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.DrawItem += delegate(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0 || args.Index >= box.Items.Count) return;
            Color fill = dark ? ((args.State & DrawItemState.Selected) != 0 ? Color.FromArgb(42, 91, 91) : Color.FromArgb(45, 59, 68))
                              : ((args.State & DrawItemState.Selected) != 0 ? Color.FromArgb(211, 231, 249) : Color.White);
            using (SolidBrush brush = new SolidBrush(fill)) args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, Convert.ToString(box.Items[args.Index]), box.Font,
                new Rectangle(args.Bounds.X + 6, args.Bounds.Y, args.Bounds.Width - 8, args.Bounds.Height),
                dark ? Color.FromArgb(235, 242, 246) : Color.FromArgb(20, 36, 57),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
    }

    private void ApplyTheme()
    {
        Color background = dark ? Color.FromArgb(21, 27, 32) : Color.FromArgb(240, 244, 248);
        Color canvas = dark ? Color.FromArgb(27, 36, 43) : Color.White;
        Color surface = dark ? Color.FromArgb(38, 50, 59) : Color.White;
        Color input = dark ? Color.FromArgb(45, 59, 68) : Color.White;
        Color text = dark ? Color.FromArgb(235, 242, 246) : Color.FromArgb(20, 36, 57);
        foreach (Control control in AllControls(this))
        {
            if (control == this || control is SplitContainer)
                control.BackColor = background;
            else if (control == headerPanel)
                control.BackColor = dark ? Color.FromArgb(34, 46, 55) : Color.White;
            else if (control == footerPanel)
                control.BackColor = dark ? Color.FromArgb(30, 40, 48) : Color.FromArgb(240, 244, 248);
            else if (control is GroupBox || control is FlowLayoutPanel)
                control.BackColor = surface;
            else if (control is SplitterPanel || control is Panel || control is TabControl || control is TabPage)
                control.BackColor = canvas;
            else if (control is TextBox || control is ComboBox || control is ListBox)
            {
                control.BackColor = input;
                if (control is TextBox) ((TextBox)control).BorderStyle = BorderStyle.FixedSingle;
                if (control is ComboBox) { PrepareComboBox((ComboBox)control); control.Invalidate(); }
            }
            else if (control is ListView)
            {
                PrepareListView((ListView)control);
                control.BackColor = dark ? Color.FromArgb(34, 44, 53) : Color.White;
                control.Invalidate();
            }
            else if (control is Button)
            {
                Button button = (Button)control;
                Color original = button.Tag is Color ? (Color)button.Tag : Slate;
                button.BackColor = !dark ? original : original == Green ? Color.FromArgb(22, 139, 109) :
                    original == Cobalt ? Color.FromArgb(32, 103, 211) : original == Slate ? Color.FromArgb(67, 91, 105) : Color.FromArgb(163, 92, 40);
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(button.BackColor, 0.12f);
                button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(button.BackColor, 0.12f);
            }
            control.ForeColor = control is Button ? Color.White : text;
        }
        if (brandPicture != null) brandPicture.Image = dark ? darkBrand : lightBrand;
        if (statusLabel != null) statusLabel.ForeColor = dark ? Color.FromArgb(93, 214, 168) : Green;
        if (internetLabel != null) internetLabel.ForeColor = dark ? Color.FromArgb(93, 214, 168) : Green;
        tips.BackColor = dark ? surface : Color.White;
        tips.ForeColor = text;
        themeButton.Text = dark ? "Light Mode" : "Dark Mode";
        if (tabs != null) { tabs.DarkTheme = dark; tabs.Invalidate(); }
        ApplyTitleBarTheme();
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls) foreach (Control descendant in AllControls(child)) yield return descendant;
    }

    private static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ValidTarget(string value)
    {
        return Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9.:-]*$") && value.Length <= 255;
    }

    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

    private void RunExternal(string area, string executable, string arguments)
    {
        ThreadPool.QueueUserWorkItem(delegate {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(30000);
                    string combined = (output + Environment.NewLine + error).Trim();
                    foreach (string line in combined.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(180)) Log(area, process.ExitCode == 0 ? "INFO" : "WARN", line);
                    Log(area, process.ExitCode == 0 ? "OK" : "WARN", "Exit code " + process.ExitCode);
                }
            }
            catch (Exception error) { Log(area, "ERROR", error.Message); }
        });
    }

    private void PingTerminal(string target, bool continuous)
    {
        target = target.Trim();
        if (!ValidTarget(target)) throw new InvalidOperationException("Enter a valid hostname or IP address.");
        Process.Start(new ProcessStartInfo("cmd.exe", "/k ping " + target + (continuous ? " -t" : " -n 4")) { UseShellExecute = true });
        Log("Ping", "INFO", "Opened live ping for " + target);
    }

    private void Lookup(string target)
    {
        target = target.Trim();
        if (!ValidTarget(target)) throw new InvalidOperationException("Enter a valid hostname or IP address.");
        ThreadPool.QueueUserWorkItem(delegate {
            try { foreach (IPAddress address in Dns.GetHostAddresses(target)) Log("DNS", "OK", target + " -> " + address); }
            catch (Exception error) { Log("DNS", "ERROR", error.Message); }
        });
    }

    private void OpenWeb(string target)
    {
        target = target.Trim();
        if (!target.Contains("://")) target = "http://" + target;
        Uri url;
        if (!Uri.TryCreate(target, UriKind.Absolute, out url) || (url.Scheme != "http" && url.Scheme != "https")) throw new InvalidOperationException("Enter an HTTP or HTTPS address.");
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        Log("Web", "OK", "Opened " + url.AbsoluteUri);
    }

    private void OpenRdp(string target)
    {
        target = target.Trim();
        if (!ValidTarget(target)) throw new InvalidOperationException("Enter a valid RDP hostname or IP address.");
        Process.Start("mstsc.exe", "/v:" + target);
        Log("RDP", "OK", "Opened Remote Desktop for " + target);
    }

    private void OpenTool(string name, string executable, string args)
    {
        Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = true });
        Log("Windows", "INFO", "Opened " + name);
    }

    private void BuildWindowsPage(TabPage page)
    {
        page.Controls.Add(L("Host / IP", 18, 18, 120));
        pingTarget = T(18, 42, 280, "8.8.8.8"); page.Controls.Add(pingTarget);
        B(page, "Ping Terminal", 314, 38, 132, delegate { PingTerminal(pingTarget.Text, continuousPing.Checked); }, "Open live replies in a terminal. Four pings by default or continuous when checked.", Cobalt);
        B(page, "DNS Lookup", 458, 38, 118, delegate { Lookup(pingTarget.Text); }, "Resolve the entered hostname using Windows DNS.", Cobalt);
        B(page, "ARP Cache", 588, 38, 118, delegate { RunExternal("ARP", "arp.exe", "-a"); }, "Show local IP-to-MAC neighbor mappings.", Cobalt);
        continuousPing = new CheckBox { Text = "Continuous ping", Left = 18, Top = 78, Width = 160 }; page.Controls.Add(continuousPing);
        page.Controls.Add(L("Windows checks", 18, 106, 200));
        string[] labels = { "System Summary", "Disk Summary", "Service Check", "Event Errors", "Problem Devices", "Update Status" };
        Action[] actions = { SystemSummary, DiskSummary, ServiceCheck, EventErrors, ProblemDevices, UpdateStatus };
        for (int i = 0; i < labels.Length; i++) B(page, labels[i], 18 + (i % 5) * 144, 130 + (i / 5) * 40, 132, actions[i], "Write " + labels[i].ToLowerInvariant() + " findings to the technician log.", Cobalt);
        page.Controls.Add(L("Open tools", 18, 252, 180));
        FlowLayoutPanel tools = new FlowLayoutPanel { Left = 18, Top = 280, Width = 708, Height = 126, AutoScroll = true, WrapContents = true };
        page.Controls.Add(tools);
        string[,] specs = {
            { "Services", "services.msc", "" }, { "Event Viewer", "eventvwr.msc", "" }, { "Device Manager", "devmgmt.msc", "" },
            { "Computer Mgmt", "compmgmt.msc", "" }, { "Local Users", "lusrmgr.msc", "" }, { "Programs", "appwiz.cpl", "" },
            { "System Props", "sysdm.cpl", "" }, { "Task Manager", "taskmgr.exe", "" }, { "Command Prompt", "cmd.exe", "" },
            { "Task Scheduler", "taskschd.msc", "" }, { "Firewall Console", "wf.msc", "" }, { "Credential Mgr", "control.exe", "/name Microsoft.CredentialManager" },
            { "Shared Folders", "fsmgmt.msc", "" }, { "Windows Update", "ms-settings:windowsupdate", "" }, { "Remote Desktop", "mstsc.exe", "" },
            { "Printers", "control.exe", "printers" }
        };
        for (int i = 0; i < specs.GetLength(0); i++) { string name = specs[i, 0], exe = specs[i, 1], arg = specs[i, 2]; B(tools, name, 0, 0, 126, delegate { OpenTool(name, exe, arg); }, "Open " + name + " for Windows troubleshooting.", Slate).Margin = new Padding(0, 0, 8, 8); }
        page.Controls.Add(L("Web URL", 18, 416, 120)); webTarget = T(18, 442, 420, "http://"); page.Controls.Add(webTarget);
        B(page, "Open Webpage", 456, 438, 150, delegate { OpenWeb(webTarget.Text); }, "Open a site or BMS web interface.", Cobalt);
        page.Controls.Add(L("RDP Target", 18, 478, 120)); rdpTarget = T(18, 504, 420, ""); page.Controls.Add(rdpTarget);
        B(page, "Open RDP", 456, 500, 150, delegate { OpenRdp(rdpTarget.Text); }, "Start Remote Desktop to the entered host.", Cobalt);
    }

    private void SystemSummary()
    {
        Log("System", "INFO", Environment.MachineName + " | " + Environment.OSVersion + " | User: " + Environment.UserName);
        foreach (ManagementObject item in new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem").Get()) Log("System", "INFO", Convert.ToString(item["Manufacturer"]) + " " + Convert.ToString(item["Model"]));
    }
    private void DiskSummary() { foreach (DriveInfo d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed)) Log("Disk", "INFO", d.Name + " free " + (d.AvailableFreeSpace / 1073741824) + " GB / " + (d.TotalSize / 1073741824) + " GB"); }
    private void ServiceCheck() { foreach (string name in new[] { "Dnscache", "Dhcp", "EventLog", "W32Time", "Winmgmt" }) { try { using (ServiceController service = new ServiceController(name)) Log("Service", "INFO", name + ": " + service.Status); } catch { Log("Service", "WARN", name + ": unavailable"); } } }
    private void EventErrors() { foreach (string name in new[] { "System", "Application" }) { using (EventLog log = new EventLog(name)) foreach (EventLogEntry entry in log.Entries.Cast<EventLogEntry>().Reverse().Where(e => e.EntryType == EventLogEntryType.Error).Take(12)) Log("Events", "WARN", name + " | " + entry.Source + " | " + entry.Message.Replace('\r', ' ').Replace('\n', ' ').Substring(0, Math.Min(180, entry.Message.Length))); } }
    private void ProblemDevices() { foreach (ManagementObject item in new ManagementObjectSearcher("SELECT Name,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0").Get()) Log("Devices", "WARN", Convert.ToString(item["Name"]) + " | Code " + item["ConfigManagerErrorCode"]); }
    private void UpdateStatus() { RunExternal("Updates", "sc.exe", "query wuauserv"); }

    private void BuildIpPage(TabPage page)
    {
        page.Controls.Add(L("Saved profiles", 18, 15, 200));
        profileView = new ListView { Left = 18, Top = 41, Width = 700, Height = 160, View = View.Details, FullRowSelect = true, GridLines = true };
        profileView.Columns.Add("Profile", 110); profileView.Columns.Add("Adapter", 120); profileView.Columns.Add("IP", 105);
        profileView.Columns.Add("Mask", 105); profileView.Columns.Add("Gateway", 92);
        profileView.DoubleClick += delegate { LoadProfile(); };
        page.Controls.Add(profileView);
        profileView.SizeChanged += delegate { UpdateProfileColumns(); };
        page.SizeChanged += delegate { profileView.Width = Math.Max(550, page.ClientSize.Width - 36); UpdateProfileColumns(); };
        B(page, "Load Profile", 18, 214, 130, LoadProfile, "Load the selected saved site profile.", Cobalt);
        B(page, "Save Profile", 164, 214, 130, SaveProfile, "Save this adapter and IP configuration for reuse.", Green);
        B(page, "Delete Profile", 310, 214, 130, DeleteProfile, "Delete the selected profile; this does not change Windows IP settings.", Slate);
        page.Controls.Add(L("Adapter", 18, 261, 140));
        adapterChoice = new ComboBox { Left = 18, Top = 285, Width = 330, DropDownStyle = ComboBoxStyle.DropDownList }; page.Controls.Add(adapterChoice);
        B(page, "Refresh", 366, 280, 100, RefreshAdapters, "Refresh connected and disconnected adapters with saved IPv4 settings.", Cobalt);
        B(page, "Details", 482, 280, 100, AdapterDetails, "Show selected adapter status, IP, mask, gateway, DNS, and MAC in the log.", Cobalt);
        page.Controls.Add(L("Profile name", 18, 327, 140)); profileName = T(18, 351, 330, ""); page.Controls.Add(profileName);
        page.Controls.Add(L("IP address", 18, 387, 120)); ipField = T(18, 411, 170, ""); page.Controls.Add(ipField);
        page.Controls.Add(L("Subnet mask", 218, 387, 120)); maskField = T(218, 411, 170, "255.255.255.0"); page.Controls.Add(maskField);
        page.Controls.Add(L("Gateway", 418, 387, 120)); gatewayField = T(418, 411, 170, ""); page.Controls.Add(gatewayField);
        page.Controls.Add(L("DNS 1", 18, 448, 120)); dns1Field = T(18, 472, 170, ""); page.Controls.Add(dns1Field);
        page.Controls.Add(L("DNS 2", 218, 448, 120)); dns2Field = T(218, 472, 170, ""); page.Controls.Add(dns2Field);
        B(page, "Apply Static IP", 18, 510, 150, ApplyStaticIp, "Confirm and apply a static IPv4 address. Administrator rights required.", Color.FromArgb(177, 93, 39));
        B(page, "Set DHCP", 184, 510, 120, ApplyDhcp, "Confirm and restore automatic IPv4 and DNS settings.", Slate);
        profileView.Width = Math.Max(550, page.ClientSize.Width - 36);
        UpdateProfileColumns();
    }

    private void UpdateProfileColumns()
    {
        if (profileView == null || profileView.Columns.Count != 5) return;
        int extra = Math.Max(0, profileView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4 - 532);
        profileView.Columns[0].Width = 110 + extra / 2;
        profileView.Columns[1].Width = 120 + extra - extra / 2;
        profileView.Columns[2].Width = 105;
        profileView.Columns[3].Width = 105;
        profileView.Columns[4].Width = 92;
    }

    private void RefreshAdapters()
    {
        string selected = adapterChoice.SelectedIndex >= 0 && adapterChoice.SelectedIndex < adapters.Count ? adapters[adapterChoice.SelectedIndex].Name : "";
        adapters.Clear();
        try
        {
            foreach (ManagementObject item in new ManagementObjectSearcher("SELECT DeviceID,NetConnectionID,GUID,NetConnectionStatus,MACAddress FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL").Get())
            {
                string name = Convert.ToString(item["NetConnectionID"]);
                if (String.IsNullOrWhiteSpace(name)) continue;
                int index = Convert.ToInt32(item["DeviceID"]);
                int status = item["NetConnectionStatus"] == null ? -1 : Convert.ToInt32(item["NetConnectionStatus"]);
                ToolkitAdapter adapter = new ToolkitAdapter { Name = name, Guid = Convert.ToString(item["GUID"]), Status = status == 2 ? "Connected" : status == 7 || status == 0 ? "Disconnected" : status == 5 ? "Disabled" : "Status " + status, Mac = Convert.ToString(item["MACAddress"]) };
                foreach (ManagementObject config in new ManagementObjectSearcher("SELECT IPAddress,IPSubnet,DefaultIPGateway,DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE Index=" + index).Get())
                {
                    adapter.IP = FirstIpv4(config["IPAddress"]);
                    adapter.Mask = FirstIpv4(config["IPSubnet"]);
                    adapter.Gateway = FirstIpv4(config["DefaultIPGateway"]);
                    adapter.Dns = JoinAddresses(config["DNSServerSearchOrder"]);
                }
                if (adapter.IP == "No IP" && !String.IsNullOrEmpty(adapter.Guid))
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapter.Guid))
                    {
                        if (key != null)
                        {
                            string stored = FirstIpv4(key.GetValue("IPAddress"));
                            if (stored != "No IP" && stored != "0.0.0.0") { adapter.IP = stored; adapter.Mask = FirstIpv4(key.GetValue("SubnetMask")); }
                            if (String.IsNullOrEmpty(adapter.Gateway)) adapter.Gateway = FirstIpv4(key.GetValue("DefaultGateway"));
                            if (String.IsNullOrEmpty(adapter.Dns)) adapter.Dns = Convert.ToString(key.GetValue("NameServer"));
                        }
                    }
                }
                adapters.Add(adapter);
            }
        }
        catch (Exception error) { Log("Adapter", "ERROR", error.Message); }
        adapters.Sort((a, b) => String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        adapterChoice.Items.Clear();
        foreach (ToolkitAdapter adapter in adapters) adapterChoice.Items.Add(adapter.Name + " [" + adapter.Status + "] - IP: " + adapter.IP + (String.IsNullOrEmpty(adapter.Mask) ? "" : " / " + adapter.Mask));
        if (adapters.Count > 0) adapterChoice.SelectedIndex = Math.Max(0, adapters.FindIndex(a => a.Name == selected));
        RefreshScannerAdapters();
        Log("Adapter", "INFO", adapters.Count + " adapters found.");
    }

    private static string JoinAddresses(object addresses)
    {
        string[] array = addresses as string[];
        return array == null ? "" : String.Join(", ", array);
    }

    private static string FirstIpv4(object addresses)
    {
        string[] array = addresses as string[];
        if (array == null) return "No IP";
        foreach (string candidate in array)
        {
            IPAddress parsed;
            if (IPAddress.TryParse(candidate, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork) return candidate;
        }
        return "No IP";
    }

    private ToolkitAdapter SelectedAdapter()
    {
        if (adapterChoice.SelectedIndex < 0 || adapterChoice.SelectedIndex >= adapters.Count) throw new InvalidOperationException("Select a network adapter.");
        return adapters[adapterChoice.SelectedIndex];
    }

    private void AdapterDetails()
    {
        ToolkitAdapter a = SelectedAdapter();
        Log("Adapter", "INFO", a.Name + " | " + a.Status + " | IP " + a.IP + " | Mask " + a.Mask + " | Gateway " + a.Gateway + " | DNS " + a.Dns + " | MAC " + a.Mac);
    }

    private void RefreshProfiles()
    {
        profileView.Items.Clear();
        foreach (ToolkitProfile p in profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            ListViewItem row = new ListViewItem(p.Name) { Tag = p };
            row.SubItems.Add(p.Adapter); row.SubItems.Add(p.IPAddress); row.SubItems.Add(p.SubnetMask); row.SubItems.Add(p.Gateway);
            profileView.Items.Add(row);
        }
    }

    private void LoadProfile()
    {
        if (profileView.SelectedItems.Count == 0) throw new InvalidOperationException("Select a saved profile.");
        ToolkitProfile p = (ToolkitProfile)profileView.SelectedItems[0].Tag;
        profileName.Text = p.Name; ipField.Text = p.IPAddress; maskField.Text = p.SubnetMask;
        gatewayField.Text = p.Gateway; dns1Field.Text = p.Dns1; dns2Field.Text = p.Dns2;
        int index = adapters.FindIndex(a => a.Name == p.Adapter);
        if (index >= 0) adapterChoice.SelectedIndex = index;
        Log("IP Shifter", "OK", "Loaded profile: " + p.Name);
    }

    private void SaveProfile()
    {
        string name = profileName.Text.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Enter a profile name.");
        ToolkitProfile p = profiles.FirstOrDefault(item => String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p == null) { p = new ToolkitProfile(); profiles.Add(p); }
        p.Name = name; p.Adapter = SelectedAdapter().Name; p.IPAddress = ipField.Text.Trim(); p.SubnetMask = maskField.Text.Trim();
        p.Gateway = gatewayField.Text.Trim(); p.Dns1 = dns1Field.Text.Trim(); p.Dns2 = dns2Field.Text.Trim();
        SaveSettings(); RefreshProfiles(); Log("IP Shifter", "OK", "Saved profile: " + name);
    }

    private void DeleteProfile()
    {
        if (profileView.SelectedItems.Count == 0) throw new InvalidOperationException("Select a saved profile.");
        ToolkitProfile p = (ToolkitProfile)profileView.SelectedItems[0].Tag;
        if (MessageBox.Show(this, "Delete saved profile '" + p.Name + "'?", "Delete Profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        profiles.Remove(p); SaveSettings(); RefreshProfiles(); Log("IP Shifter", "OK", "Deleted profile: " + p.Name);
    }

    private static string ValidateIpv4(string value, string field, bool optional)
    {
        value = value.Trim();
        if (optional && value.Length == 0) return "";
        IPAddress address;
        if (!Regex.IsMatch(value, @"^\d{1,3}(\.\d{1,3}){3}$") || !IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException(field + " must be a valid dotted IPv4 address.");
        return value;
    }

    private void RequireAdmin()
    {
        if (IsAdmin()) return;
        if (MessageBox.Show(this, "Changing adapter settings requires administrator rights. Restart the toolkit as administrator?", "Administrator Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" }); ExitToolkit(); }
            catch (Exception error) { throw new InvalidOperationException("Could not restart as administrator: " + error.Message); }
        }
        throw new InvalidOperationException("No IP settings were changed. Run the toolkit as administrator.");
    }

    private void Netsh(string args)
    {
        ProcessStartInfo start = new ProcessStartInfo("netsh.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (Process p = Process.Start(start))
        {
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("Windows rejected the IP change: " + output.Trim());
            if (!String.IsNullOrWhiteSpace(output)) Log("IP Shifter", "INFO", output.Trim());
        }
    }

    private void ApplyStaticIp()
    {
        ToolkitAdapter adapter = SelectedAdapter();
        string ip = ValidateIpv4(ipField.Text, "IP address", false), mask = ValidateIpv4(maskField.Text, "Subnet mask", false);
        string gateway = ValidateIpv4(gatewayField.Text, "Gateway", true), dns1 = ValidateIpv4(dns1Field.Text, "DNS 1", true), dns2 = ValidateIpv4(dns2Field.Text, "DNS 2", true);
        RequireAdmin();
        string summary = adapter.Name + "\r\nIP: " + ip + "\r\nMask: " + mask + "\r\nGateway: " + (gateway.Length == 0 ? "None" : gateway) + "\r\nDNS: " + (dns1.Length == 0 ? "Automatic" : dns1 + (dns2.Length == 0 ? "" : ", " + dns2));
        if (MessageBox.Show(this, "Apply this configuration?\r\n\r\n" + summary, "Confirm IP Change", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Netsh("interface ipv4 set address name=" + Quote(adapter.Name) + " source=static address=" + ip + " mask=" + mask + " gateway=" + (gateway.Length == 0 ? "none" : gateway));
        if (dns1.Length > 0) { Netsh("interface ipv4 set dnsservers name=" + Quote(adapter.Name) + " source=static address=" + dns1 + " validate=no"); if (dns2.Length > 0) Netsh("interface ipv4 add dnsservers name=" + Quote(adapter.Name) + " address=" + dns2 + " index=2 validate=no"); }
        else Netsh("interface ipv4 set dnsservers name=" + Quote(adapter.Name) + " source=dhcp");
        RefreshAdapters();
        ToolkitAdapter updated = adapters.FirstOrDefault(a => a.Name == adapter.Name);
        Log("IP Shifter", updated != null && updated.IP == ip ? "OK" : "WARN", "Requested " + ip + "; Windows reports " + (updated == null ? "adapter unavailable" : updated.IP));
    }

    private void ApplyDhcp()
    {
        ToolkitAdapter adapter = SelectedAdapter();
        RequireAdmin();
        if (MessageBox.Show(this, "Switch " + adapter.Name + " to automatic IP and DNS?", "Confirm DHCP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Netsh("interface ipv4 set address name=" + Quote(adapter.Name) + " source=dhcp");
        Netsh("interface ipv4 set dnsservers name=" + Quote(adapter.Name) + " source=dhcp");
        RefreshAdapters(); Log("IP Shifter", "OK", "Requested DHCP on " + adapter.Name);
    }

    private void BuildNetworkPage(TabPage page)
    {
        GroupBox device = new GroupBox { Text = "Device Checks", Left = 18, Top = 18, Width = 340, Height = 230 }; page.Controls.Add(device);
        device.Controls.Add(L("Device host / IP", 14, 28, 160)); networkTarget = T(14, 52, 300, "192.168.1.10"); device.Controls.Add(networkTarget);
        B(device, "Ping Device", 14, 106, 142, delegate { PingTerminal(networkTarget.Text, false); }, "Four live pings to the device.", Cobalt);
        B(device, "Ping Gateway", 172, 106, 142, delegate { PingTerminal(SelectedAdapter().Gateway, false); }, "Four live pings to the selected adapter's gateway.", Green);
        B(device, "Tracert", 14, 146, 142, delegate { if (!ValidTarget(networkTarget.Text)) throw new InvalidOperationException("Enter a valid target."); RunExternal("Trace", "tracert.exe", "-d " + networkTarget.Text.Trim()); }, "Trace the route to the device.", Slate);
        B(device, "PathPing", 172, 146, 142, delegate { if (!ValidTarget(networkTarget.Text)) throw new InvalidOperationException("Enter a valid target."); RunExternal("PathPing", "pathping.exe", "-n " + networkTarget.Text.Trim()); }, "Find route and packet loss along the path.", Slate);
        B(device, "DNS Lookup", 14, 186, 142, delegate { Lookup(networkTarget.Text); }, "Resolve the device hostname.", Cobalt);
        B(device, "Open Webpage", 172, 186, 142, delegate { OpenWeb(networkTarget.Text); }, "Open the device's web interface.", Cobalt);

        GroupBox access = new GroupBox { Text = "Device Access", Left = 376, Top = 18, Width = 340, Height = 230 }; page.Controls.Add(access);
        access.Controls.Add(L("Host / IP", 14, 28, 155)); switchTarget = T(14, 52, 220, "192.168.1.2"); access.Controls.Add(switchTarget);
        switchScheme = new ComboBox { Left = 246, Top = 52, Width = 70, DropDownStyle = ComboBoxStyle.DropDownList }; switchScheme.Items.AddRange(new object[] { "http://", "https://" }); switchScheme.SelectedIndex = 0; access.Controls.Add(switchScheme);
        access.Controls.Add(L("Telnet port", 14, 82, 165)); telnetPort = T(14, 106, 84, "23"); access.Controls.Add(telnetPort);
        B(access, "Ping", 14, 146, 94, delegate { PingTerminal(switchTarget.Text, false); }, "Four live pings to the selected host.", Cobalt);
        B(access, "Ports", 118, 146, 94, delegate { ProbeSwitchPorts(switchTarget.Text); }, "Check HTTP, HTTPS, SSH, and RDP ports without changing the device.", Green);
        B(access, "Test Telnet", 222, 146, 94, TestTelnet, "Check whether a TCP connection opens on the chosen port. This does not log in or prove the Telnet protocol works.", Cobalt);
        B(access, "Web UI", 14, 186, 94, delegate { OpenWeb(Convert.ToString(switchScheme.SelectedItem) + switchTarget.Text.Trim()); }, "Open the device management page.", Cobalt);
        B(access, "SSH", 118, 186, 94, delegate { if (!ValidTarget(switchTarget.Text)) throw new InvalidOperationException("Enter a valid host."); Process.Start("cmd.exe", "/k ssh " + switchTarget.Text.Trim()); }, "Open an SSH session if Windows OpenSSH is installed.", Slate);
        B(access, "Open Telnet", 222, 186, 94, OpenTelnet, "Launch Windows Telnet Client. Telnet traffic is unencrypted; use only on approved networks.", Slate);

        GroupBox capture = new GroupBox { Text = "Quick Capture", Left = 18, Top = 262, Width = 340, Height = 328 }; page.Controls.Add(capture);
        string[] labels = { "Baseline Snapshot", "Link Status", "Adapter Details", "Neighbors", "ARP Cache", "Route Table", "DNS Cache", "Netstat", "Firewall", "Network Summary", "IPConfig /all", "Network Settings", "Flush DNS" };
        Action[] actions = {
            delegate { RunExternal("Network", "ipconfig.exe", "/all"); }, delegate { foreach (ToolkitAdapter a in adapters) Log("Link", "INFO", a.Name + " | " + a.Status + " | " + a.IP); },
            AdapterDetails, delegate { RunExternal("Neighbors", "arp.exe", "-a"); }, delegate { RunExternal("ARP", "arp.exe", "-a"); },
            delegate { RunExternal("Route", "route.exe", "print"); }, delegate { RunExternal("DNS Cache", "ipconfig.exe", "/displaydns"); },
            delegate { RunExternal("Netstat", "netstat.exe", "-ano"); }, delegate { RunExternal("Firewall", "netsh.exe", "advfirewall show allprofiles"); },
            delegate { foreach (ToolkitAdapter a in adapters) Log("Network", "INFO", a.Name + " | " + a.Status + " | " + a.IP + " | GW " + a.Gateway); },
            delegate { RunExternal("IPConfig", "ipconfig.exe", "/all"); }, delegate { OpenTool("Network Connections", "ncpa.cpl", ""); },
            delegate { if (MessageBox.Show(this, "Clear the Windows DNS resolver cache?", "Flush DNS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) RunExternal("DNS", "ipconfig.exe", "/flushdns"); }
        };
        for (int i = 0; i < labels.Length; i++) B(capture, labels[i], 14 + (i % 2) * 160, 32 + (i / 2) * 40, 146, actions[i], "Run " + labels[i] + " and write results to the technician log.", labels[i] == "Flush DNS" ? Color.FromArgb(177, 93, 39) : Slate);

        GroupBox playbook = new GroupBox { Text = "Field Playbooks", Left = 376, Top = 262, Width = 340, Height = 248 }; page.Controls.Add(playbook);
        string[] topics = { "Managed Switch", "Unmanaged Switch", "MS/TP and IP", "No Link Light", "Wrong VLAN / IP", "Intermittent" };
        for (int i = 0; i < topics.Length; i++) { string topic = topics[i]; B(playbook, topic, 14 + (i % 2) * 160, 28 + (i / 2) * 38, 148, delegate { ShowPlaybook(topic); }, "Open a short field checklist for " + topic.ToLowerInvariant() + ".", i == 2 ? Green : Slate); }
        playbookText = new TextBox { Left = 14, Top = 144, Width = 308, Height = 88, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true }; playbook.Controls.Add(playbookText);
        ShowPlaybook("Managed Switch");

        page.Controls.Add(L("Support bundle", 376, 530, 220));
        bundleType = new ComboBox { Left = 376, Top = 560, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList }; bundleType.Items.AddRange(new object[] { "Network", "Windows", "Both" }); bundleType.SelectedIndex = 0; page.Controls.Add(bundleType);
        B(page, "Export ZIP", 574, 556, 130, ExportBundle, "Export selected logs and diagnostic snapshots as a ZIP for review before sharing.", Green);
    }

    private int SelectedTelnetPort()
    {
        string value = telnetPort.Text.Trim();
        int port;
        if (!Regex.IsMatch(value, @"^[0-9]{1,5}$") || !Int32.TryParse(value, out port) || port < 1 || port > 65535)
            throw new InvalidOperationException("Enter a Telnet TCP port from 1 to 65535.");
        return port;
    }

    private void TestTelnet()
    {
        string host = switchTarget.Text.Trim();
        if (!ValidTarget(host)) throw new InvalidOperationException("Enter a valid device hostname or IP address.");
        int port = SelectedTelnetPort();
        Log("Telnet", "INFO", "Checking TCP " + host + ":" + port + "...");
        ThreadPool.QueueUserWorkItem(delegate {
            bool reachable = ProbePort(host, port, 1800);
            Log("Telnet", reachable ? "OK" : "WARN", host + ":" + port + (reachable ? " accepts a TCP connection. Telnet login is not verified." : " did not accept a TCP connection."));
        });
    }

    private void OpenTelnet()
    {
        string host = switchTarget.Text.Trim();
        if (!ValidTarget(host)) throw new InvalidOperationException("Enter a valid device hostname or IP address.");
        int port = SelectedTelnetPort();
        string executable = Path.Combine(Environment.SystemDirectory, "telnet.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Windows Telnet Client is not installed. Ask IT to enable the optional Telnet Client feature, or use Test Telnet to check the TCP port.");
        Process.Start(new ProcessStartInfo(executable, host + " " + port) { UseShellExecute = true });
        Log("Telnet", "INFO", "Opened Windows Telnet Client for " + host + ":" + port + ". Telnet traffic is unencrypted.");
    }

    private void ShowPlaybook(string topic)
    {
        string guidance = topic == "Managed Switch" ? "Check link and port status. Confirm switch management IP, access VLAN, trunk path, MAC table, and upstream gateway. Record port number before changing switch settings." :
            topic == "Unmanaged Switch" ? "Confirm power, link lights, patch cable, uplink, and known-good port. Compare direct connection against the switch path." :
            topic == "MS/TP and IP" ? "Check controller power, MS/TP polarity and termination, MAC conflicts, baud rate, IP gateway, and the server or point-server path." :
            topic == "No Link Light" ? "Check cable, NIC, switch port, PoE/power, adapter enabled state, and a known-good patch lead. Escalate when physical link remains absent." :
            topic == "Wrong VLAN / IP" ? "Compare assigned IP and mask with site plan. Confirm switch access VLAN and DHCP scope on the switch; do not infer VLAN ID from subnet alone." :
            "Check packet loss over time, switch errors, spanning-tree changes, duplex, cable quality, power, and upstream instability.";
        if (playbookText != null) playbookText.Text = guidance;
    }

    private void BuildBmsPage(TabPage page)
    {
        page.AutoScroll = true;
        page.Controls.Add(new Label { Text = "BMS Tools", Left = 18, Top = 17, Width = 300, Height = 35, Font = new Font("Segoe UI", 16f, FontStyle.Bold) });
        B(page, "Launch YABE", 400, 18, 150, LaunchYabe, "Open installed YABE. Select Yabe.exe once if it is installed elsewhere.", Green);
        page.Controls.Add(L("Saved BMS servers", 18, 55, 250));
        bmsProfileView = new ListView { Left = 18, Top = 78, Width = 590, Height = 112, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false };
        bmsProfileView.Columns.Add("Site / profile", 190);
        bmsProfileView.Columns.Add("Server", 210);
        bmsProfileView.Columns.Add("Username", 170);
        bmsProfileView.SelectedIndexChanged += delegate { LoadSelectedBmsProfile(); };
        page.Controls.Add(bmsProfileView);
        page.SizeChanged += delegate { ResizeBmsProfileView(page); };
        ResizeBmsProfileView(page);
        RefreshBmsProfiles();
        page.Controls.Add(L("Profile name / site", 18, 195, 190));
        bmsSiteName = T(18, 219, 190, ""); page.Controls.Add(bmsSiteName);
        B(page, "New", 224, 215, 78, NewBmsProfile, "Clear the fields to create another site or server profile.", Slate);
        B(page, "Save Profile", 312, 215, 112, SaveEbiServer, "Save or update this site's server and username on this laptop. Passwords are never stored.", Cobalt);
        B(page, "Delete", 434, 215, 88, DeleteBmsProfile, "Delete the selected saved profile after confirmation. This does not change the server.", Slate);
        BuildEbiPanel(page);
        if (bmsProfileView.Items.Count > 0)
        {
            bmsProfileView.Items[0].Selected = true;
            BmsServerProfile first = bmsProfileView.Items[0].Tag as BmsServerProfile;
            if (first != null) { bmsSiteName.Text = first.Name; ebiHost.Text = first.Host; ebiUser.Text = first.User; }
        }
    }

    private void ResizeBmsProfileView(TabPage page)
    {
        if (bmsProfileView == null) return;
        bmsProfileView.Width = Math.Max(570, page.ClientSize.Width - 36);
        int usable = bmsProfileView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 5;
        bmsProfileView.Columns[0].Width = Math.Max(170, (usable - 170) / 2);
        bmsProfileView.Columns[1].Width = Math.Max(190, usable - bmsProfileView.Columns[0].Width - 170);
        bmsProfileView.Columns[2].Width = 170;
    }

    private void RefreshBmsProfiles()
    {
        bmsProfileView.Items.Clear();
        foreach (BmsServerProfile profile in bmsProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            ListViewItem item = new ListViewItem(profile.Name) { Tag = profile };
            item.SubItems.Add(profile.Host);
            item.SubItems.Add(profile.User);
            bmsProfileView.Items.Add(item);
        }
    }

    private void LoadSelectedBmsProfile()
    {
        if (bmsProfileView.SelectedItems.Count == 0) return;
        BmsServerProfile profile = bmsProfileView.SelectedItems[0].Tag as BmsServerProfile;
        if (profile == null) return;
        bmsSiteName.Text = profile.Name;
        ebiHost.Text = profile.Host;
        ebiUser.Text = profile.User;
        Log("BMS", "INFO", "Selected " + profile.Name + " (" + profile.Host + ").");
    }

    private void NewBmsProfile()
    {
        foreach (ListViewItem item in bmsProfileView.SelectedItems.Cast<ListViewItem>().ToArray()) item.Selected = false;
        bmsSiteName.Clear(); ebiHost.Clear(); ebiUser.Clear();
        bmsSiteName.Focus();
    }

    private void BuildEbiPanel(TabPage page)
    {
        GroupBox panel = new GroupBox { Text = "Connect to BMS Server (EBI)", Left = 18, Top = 265, Width = 590, Height = 258 };
        page.Controls.Add(panel);
        page.SizeChanged += delegate { panel.Width = Math.Max(570, page.ClientSize.Width - 36); };
        panel.Width = Math.Max(570, page.ClientSize.Width - 36);
        panel.Controls.Add(L("Server hostname / IP", 14, 26, 200));
        ebiHost = T(14, 50, 250, ""); panel.Controls.Add(ebiHost);
        panel.Controls.Add(L("Username (not password)", 280, 26, 220));
        ebiUser = T(280, 50, 270, ""); panel.Controls.Add(ebiUser);
        B(panel, "Ping", 14, 91, 92, delegate { PingTerminal(EbiTarget(), false); }, "Show four live ping replies from this laptop to the EBI server.", Slate);
        B(panel, "RDP", 118, 91, 92, delegate { OpenRdp(EbiTarget()); }, "Open Remote Desktop Connection; Windows asks for your login.", Slate);
        B(panel, "Remote Shell", 222, 91, 130, delegate { OpenEbiShell(null); }, "Open an interactive WinRM shell. Windows prompts for the password in the terminal.", Green);
        B(panel, "Check Ports", 364, 91, 118, CheckEbiPorts, "Check RDP 3389 and WinRM 5985/5986. Open ports do not prove login access.", Slate);
        panel.Controls.Add(L("Backup output path on server", 14, 143, 280));
        ebiBackupPath = T(14, 168, 250, "C:\\sitename.txt"); panel.Controls.Add(ebiBackupPath);
        B(panel, "Liclist", 280, 164, 110, delegate { OpenEbiShell("liclist"); }, "Run Liclist on the EBI server through WinRM.", Cobalt);
        B(panel, "bckbld -out", 402, 164, 160, RunEbiBackup, "Run bckbld with the chosen output path on the server after confirmation.", Cobalt);
        panel.Controls.Add(new Label { Text = "WinRM access must be approved and configured on the server. Use a hostname for domain authentication where possible.", Left = 14, Top = 213, Width = 550, Height = 30, AutoEllipsis = true });
    }

    private string EbiTarget()
    {
        string target = ebiHost.Text.Trim();
        if (!ValidTarget(target)) throw new InvalidOperationException("Enter a valid EBI server hostname or IP address.");
        return target;
    }

    private void SaveEbiServer()
    {
        string name = bmsSiteName.Text.Trim(), host = EbiTarget(), user = ebiUser.Text.Trim();
        if (name.Length == 0 || name.Length > 80) throw new InvalidOperationException("Enter a profile name or site (up to 80 characters).");
        BmsServerProfile profile = bmsProfileView.SelectedItems.Count > 0 ? bmsProfileView.SelectedItems[0].Tag as BmsServerProfile : null;
        BmsServerProfile duplicate = bmsProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (duplicate != null && !Object.ReferenceEquals(duplicate, profile))
            throw new InvalidOperationException("A BMS profile with that name already exists. Select it to update, or use another name.");
        if (profile == null) { profile = new BmsServerProfile(); bmsProfiles.Add(profile); }
        profile.Name = name; profile.Host = host; profile.User = user;
        SaveSettings();
        RefreshBmsProfiles();
        ListViewItem item = bmsProfileView.Items.Cast<ListViewItem>().FirstOrDefault(row => Object.ReferenceEquals(row.Tag, profile));
        if (item != null) item.Selected = true;
        Log("BMS", "OK", "Saved " + name + " (" + host + "); no password stored.");
    }

    private void DeleteBmsProfile()
    {
        if (bmsProfileView.SelectedItems.Count == 0) throw new InvalidOperationException("Select a saved BMS profile to delete.");
        BmsServerProfile profile = bmsProfileView.SelectedItems[0].Tag as BmsServerProfile;
        if (profile == null) return;
        if (MessageBox.Show(this, "Delete saved profile '" + profile.Name + "'? This only removes its local shortcut.", "Delete BMS Profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        bmsProfiles.Remove(profile);
        SaveSettings();
        RefreshBmsProfiles();
        bmsSiteName.Clear(); ebiHost.Clear(); ebiUser.Clear();
        Log("BMS", "OK", "Deleted profile " + profile.Name + ".");
    }

    private void OpenEbiShell(string command)
    {
        string host = EbiTarget();
        string user = ebiUser.Text.Trim();
        if (!Regex.IsMatch(user, @"^[A-Za-z0-9_.@\\-]+$")) throw new InvalidOperationException("Enter a valid Windows username, such as DOMAIN\\technician. Passwords are entered only in the terminal prompt.");
        string args = "/k winrs /r:" + host + " /u:" + user + " cmd.exe" + (String.IsNullOrEmpty(command) ? "" : " /c " + command);
        Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
        Log("EBI", "INFO", "Opened WinRM " + (String.IsNullOrEmpty(command) ? "shell" : command) + " on " + host + ". Enter password in the terminal; it is not stored.");
    }

    private void RunEbiBackup()
    {
        string path = ebiBackupPath.Text.Trim();
        if (!Regex.IsMatch(path, @"^[A-Za-z]:\\[A-Za-z0-9_.-]+\.txt$"))
            throw new InvalidOperationException("Enter a server-local output path such as C:\\sitename.txt (letters, numbers, underscore, hyphen, or period only).");
        if (MessageBox.Show(this, "Run bckbld -out " + path + " on " + EbiTarget() + "?\r\n\r\nThis writes a file on the EBI server. Confirm the site and path first.",
            "Run EBI Command", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        OpenEbiShell("bckbld -out " + path);
    }

    private void CheckEbiPorts()
    {
        string host = EbiTarget();
        ThreadPool.QueueUserWorkItem(delegate {
            foreach (int port in new[] { 3389, 5985, 5986 })
            {
                bool reachable = ProbePort(host, port, 650);
                Log("EBI", reachable ? "OK" : "INFO", host + ":" + port + (reachable ? " reachable" : " unavailable"));
            }
        });
    }

    private void LaunchYabe()
    {
        string[] candidates = {
            yabePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yabe", "Yabe.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Yabe", "Yabe.exe")
        };
        string executable = candidates.FirstOrDefault(path => !String.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (String.IsNullOrEmpty(executable))
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "Find Yabe.exe", Filter = "YABE executable (Yabe.exe)|Yabe.exe", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                executable = dialog.FileName;
            }
            if (!String.Equals(Path.GetFileName(executable), "Yabe.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select the installed Yabe.exe file.");
        }
        yabePath = executable;
        SaveSettings();
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable) });
        Log("BMS", "OK", "Opened YABE: " + executable);
    }

    private void ProbeSwitchPorts(string host)
    {
        if (!ValidTarget(host)) throw new InvalidOperationException("Enter a valid switch host.");
        ThreadPool.QueueUserWorkItem(delegate { foreach (int port in new[] { 22, 80, 443, 3389 }) { bool open = ProbePort(host, port, 350); Log("Switch", open ? "OK" : "INFO", host + ":" + port + (open ? " open" : " unavailable")); } });
    }

    private static string CaptureCommand(string executable, string args)
    {
        using (Process p = Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        { string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(20000); return output; }
    }

    private void ExportBundle()
    {
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "ZIP files (*.zip)|*.zip", FileName = "TEC_SupportBundle_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip" })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            using (FileStream file = File.Create(dialog.FileName))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                AddZipText(archive, "technician.log", File.Exists(logPath) ? File.ReadAllText(logPath) : "");
                string type = Convert.ToString(bundleType.SelectedItem);
                if (type != "Windows") { AddZipText(archive, "ipconfig.txt", CaptureCommand("ipconfig.exe", "/all")); AddZipText(archive, "routes.txt", CaptureCommand("route.exe", "print")); AddZipText(archive, "arp.txt", CaptureCommand("arp.exe", "-a")); }
                if (type != "Network") { AddZipText(archive, "system.txt", Environment.MachineName + Environment.NewLine + Environment.OSVersion); AddZipText(archive, "services.txt", String.Join(Environment.NewLine, ServiceController.GetServices().Select(s => s.ServiceName + " | " + s.Status).ToArray())); }
            }
            Log("Support", "OK", "Exported bundle. Review before sharing: " + dialog.FileName);
        }
    }

    private static void AddZipText(ZipArchive archive, string name, string content)
    {
        using (StreamWriter writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8)) writer.Write(content);
    }

    private void BuildScannerPage(TabPage page)
    {
        page.Controls.Add(L("Network adapter", 18, 16, 220));
        scanAdapter = new ComboBox { Left = 18, Top = 38, Width = 690, DropDownStyle = ComboBoxStyle.DropDownList }; scanAdapter.SelectedIndexChanged += delegate { SelectScanAdapter(); }; page.Controls.Add(scanAdapter);
        scanScope = L("Select an adapter to fill the local network range.", 18, 68, 690); page.Controls.Add(scanScope);
        page.Controls.Add(L("Start IP", 18, 98, 120)); scanStart = T(18, 122, 156, ""); page.Controls.Add(scanStart);
        page.Controls.Add(L("End IP", 190, 98, 120)); scanEnd = T(190, 122, 156, ""); page.Controls.Add(scanEnd);
        scanButton = B(page, "Scan", 362, 120, 88, StartScan, "Scan up to 1,024 IPs with ping and common TCP probes.", Cobalt);
        B(page, "Stop", 458, 120, 74, delegate { stopScan = true; }, "Stop the current scan.", Slate);
        B(page, "Export CSV", 540, 120, 110, ExportScan, "Save discovered IPs, hostnames, ping times, MAC addresses, and ports.", Green);
        scanStatus = L("Ready | Ping and common TCP services", 18, 164, 660); page.Controls.Add(scanStatus);
        scanView = new ListView { Left = 18, Top = 194, Width = 690, Height = 326, View = View.Details, FullRowSelect = true, GridLines = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        scanView.Columns.Add("IP address", 126); scanView.Columns.Add("Hostname", 198); scanView.Columns.Add("Ping", 70); scanView.Columns.Add("MAC address", 128); scanView.Columns.Add("Open TCP ports", 132);
        page.Controls.Add(scanView);
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Ping", null, delegate { if (scanView.SelectedItems.Count > 0) PingTerminal(scanView.SelectedItems[0].Text, true); });
        menu.Items.Add("Remote Desktop", null, delegate { if (scanView.SelectedItems.Count > 0) OpenRdp(scanView.SelectedItems[0].Text); });
        menu.Items.Add("Open HTTP", null, delegate { if (scanView.SelectedItems.Count > 0) OpenWeb("http://" + scanView.SelectedItems[0].Text); });
        menu.Items.Add("Open HTTPS", null, delegate { if (scanView.SelectedItems.Count > 0) OpenWeb("https://" + scanView.SelectedItems[0].Text); });
        scanView.ContextMenuStrip = menu;
    }

    private static uint IpNumber(string value) { byte[] bytes = IPAddress.Parse(ValidateIpv4(value, "Scan IP", false)).GetAddressBytes(); return (uint)((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]); }
    private static string IpText(uint number) { return String.Format("{0}.{1}.{2}.{3}", number >> 24, (number >> 16) & 255, (number >> 8) & 255, number & 255); }

    private void RefreshScannerAdapters()
    {
        if (scanAdapter == null) return;
        scanAdapter.Items.Clear();
        foreach (ToolkitAdapter a in adapters) scanAdapter.Items.Add(a.Name + " [" + a.Status + "] - " + a.IP);
        if (adapters.Count > 0) { int index = adapters.FindIndex(a => a.Status == "Connected" && a.IP != "No IP"); scanAdapter.SelectedIndex = index >= 0 ? index : 0; }
    }

    private void SelectScanAdapter()
    {
        if (scanAdapter.SelectedIndex < 0 || scanAdapter.SelectedIndex >= adapters.Count) return;
        ToolkitAdapter a = adapters[scanAdapter.SelectedIndex];
        if (a.Status != "Connected" || a.IP == "No IP") { scanScope.Text = "This adapter is disconnected. Enter a range manually or choose a connected adapter."; return; }
        try
        {
            uint ip = IpNumber(a.IP), mask = IpNumber(a.Mask), network = ip & mask, broadcast = network | ~mask;
            uint first = network + 1, last = broadcast - 1;
            if (last < first) { first = ip; last = ip; }
            if ((ulong)last - first + 1 > 254) { first = Math.Max(first, ip > 126 ? ip - 126 : 0); last = Math.Min(last, first + 253); scanScope.Text = a.Name + ": large subnet; showing 254 addresses near this laptop. Edit the range for another slice."; }
            else scanScope.Text = a.Name + ": connected | " + a.IP + " / " + a.Mask;
            scanStart.Text = IpText(first); scanEnd.Text = IpText(last);
        }
        catch (Exception error) { scanScope.Text = "Could not determine subnet: " + error.Message; }
    }

    private static bool ProbePort(string host, int port, int timeout)
    {
        using (TcpClient client = new TcpClient())
        {
            try { IAsyncResult task = client.BeginConnect(host, port, null, null); if (!task.AsyncWaitHandle.WaitOne(timeout)) return false; client.EndConnect(task); return client.Connected; }
            catch { return false; }
        }
    }

    private static ScanHost ProbeHost(string ip)
    {
        ScanHost host = new ScanHost { IP = ip };
        using (Ping ping = new Ping()) { try { PingReply reply = ping.Send(ip, 350); if (reply.Status == IPStatus.Success) host.Ping = reply.RoundtripTime + " ms"; } catch { } }
        List<int> ports = new List<int>();
        foreach (int port in new[] { 22, 80, 443, 445, 3389 }) if (ProbePort(ip, port, 150)) ports.Add(port);
        if (host.Ping.Length == 0 && ports.Count == 0) return null;
        host.Ports = String.Join(", ", ports.Select(p => p.ToString()).ToArray());
        try { IAsyncResult task = Dns.BeginGetHostEntry(ip, null, null); if (task.AsyncWaitHandle.WaitOne(300)) host.Hostname = Dns.EndGetHostEntry(task).HostName; } catch { }
        try {
            string arp = CaptureCommand("arp.exe", "-a " + ip);
            Match match = Regex.Match(arp, @"(?m)^\s*" + Regex.Escape(ip) + @"\s+([0-9a-fA-F-]{17})\s+");
            if (match.Success) host.Mac = match.Groups[1].Value;
        }
        catch { }
        return host;
    }

    private void StartScan()
    {
        if (scanRunning) throw new InvalidOperationException("A scan is already running.");
        uint start = IpNumber(scanStart.Text.Trim()), end = IpNumber(scanEnd.Text.Trim());
        if (end < start || (ulong)end - start + 1 > 1024) throw new InvalidOperationException("Enter an ascending range of no more than 1,024 IPs.");
        scanView.Items.Clear(); scanRunning = true; stopScan = false; scanButton.Enabled = false;
        int count = (int)(end - start + 1), processed = 0;
        scanStatus.Text = "Scanning 0/" + count;
        Log("IP Scanner", "INFO", "Scanning " + IpText(start) + " - " + IpText(end));
        Task.Factory.StartNew(delegate {
            try
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => {
                    if (stopScan) return;
                    ScanHost host = ProbeHost(IpText(start + (uint)i));
                    if (host != null) BeginInvoke(new Action<ScanHost>(AddScanHost), host);
                    int done = Interlocked.Increment(ref processed);
                    if (done % 16 == 0 || done == count) BeginInvoke(new Action<int, int>(ScanProgress), done, count);
                });
            }
            catch (Exception error) { Log("IP Scanner", "ERROR", error.Message); }
            finally { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { scanRunning = false; scanButton.Enabled = true; scanStatus.Text = stopScan ? "Scan stopped" : scanView.Items.Count + " devices found"; Log("IP Scanner", "OK", scanStatus.Text); })); }
        });
    }

    private void AddScanHost(ScanHost host)
    {
        if (IsDisposed) return;
        ListViewItem row = new ListViewItem(host.IP);
        row.SubItems.Add(host.Hostname); row.SubItems.Add(host.Ping); row.SubItems.Add(host.Mac); row.SubItems.Add(host.Ports);
        scanView.Items.Add(row);
    }
    private void ScanProgress(int done, int count) { if (!IsDisposed) scanStatus.Text = "Scanning " + done + "/" + count + " | " + scanView.Items.Count + " devices"; }

    private void ExportScan()
    {
        if (scanView.Items.Count == 0) throw new InvalidOperationException("There are no scan results to export.");
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "IP_Scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv" })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            using (StreamWriter writer = new StreamWriter(dialog.FileName, false, Encoding.UTF8))
            {
                writer.WriteLine("IPAddress,Hostname,Ping,MacAddress,OpenPorts");
                foreach (ListViewItem row in scanView.Items) writer.WriteLine(String.Join(",", row.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(c => "\"" + c.Text.Replace("\"", "\"\"") + "\"").ToArray()));
            }
            Log("IP Scanner", "OK", "Exported " + dialog.FileName);
        }
    }

    private void BuildFeedbackPage(TabPage page)
    {
        page.Controls.Add(new Label {
            Text = "Feedback for TEC Systems IT", Left = 24, Top = 28, Width = 560, Height = 38,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold)
        });
        page.Controls.Add(new Label {
            Text = "Send feedback by email to:", Left = 24, Top = 87, Width = 350, Height = 26,
            Font = new Font("Segoe UI", 10f)
        });
        page.Controls.Add(new Label {
            Text = Mailbox, Left = 24, Top = 119, Width = 460, Height = 38,
            Font = new Font("Segoe UI", 18f, FontStyle.Regular)
        });
        B(page, "Open Outlook Web", 24, 178, 164, OpenFeedbackEmail,
            "Open a message to TEC Systems IT in Microsoft 365 Outlook on the web. Send it from there.", Green);
        B(page, "Copy Address", 200, 178, 138, delegate {
            Clipboard.SetText(Mailbox);
            Log("Feedback", "OK", "Copied IT email address.");
        }, "Copy the IT email address.", Slate);
    }

    private void OpenFeedbackEmail()
    {
        string draft = "https://outlook.office.com/mail/deeplink/compose?to=" + Uri.EscapeDataString(Mailbox) + "&subject=" + Uri.EscapeDataString(Product + " Feedback");
        Process.Start(new ProcessStartInfo(draft) { UseShellExecute = true });
        Log("Feedback", "INFO", "Opened Outlook on the web; technician must review and send the message.");
    }

    private bool InternetOnline()
    {
        try { return Dns.GetHostAddresses("microsoft.com").Length > 0; }
        catch { return false; }
    }

    private void UpdateInternet()
    {
        bool online = InternetOnline();
        internetLabel.Text = online ? "Internet: Online" : "Internet: Offline";
        internetLabel.ForeColor = online ? (dark ? Color.FromArgb(93, 214, 168) : Green) : (dark ? Color.FromArgb(255, 120, 120) : Color.Firebrick);
        if (tray != null) tray.Text = Product + " - Internet: " + (online ? "Online" : "Offline");
    }

    private void CheckUpdates(bool silent)
    {
        ThreadPool.QueueUserWorkItem(delegate {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "TEC-Systems-Field-Toolkit";
                    string releaseJson = client.DownloadString("https://api.github.com/repos/TEC-Systems/Technician-Field-Tool-Kit/releases/latest");
                    Dictionary<string, object> release = json.Deserialize<Dictionary<string, object>>(releaseJson);
                    Version latest = new Version(Value(release, "tag_name").TrimStart('v'));
                    if (latest <= new Version(version)) { if (!silent) BeginInvoke(new Action(delegate { MessageBox.Show(this, "This toolkit is up to date.", "Toolkit Updates"); })); return; }
                    BeginInvoke(new Action(delegate { OfferUpdate(release, latest.ToString()); }));
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (!IsDisposed) BeginInvoke(new Action(delegate {
                        Log("Updates", "INFO", "No published toolkit release is available.");
                        if (!silent) MessageBox.Show(this,
                            "No toolkit update has been published yet. Installed version: " + version + ".\r\n\r\nTEC Systems IT must publish a GitHub Release before updates can be downloaded here.",
                            "Toolkit Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                    return;
                }
                if (!silent && !IsDisposed) BeginInvoke(new Action(delegate { Fail("Updates", error); }));
            }
            catch (Exception error) { if (!silent && !IsDisposed) BeginInvoke(new Action(delegate { Fail("Updates", error); })); }
        });
    }

    private void OfferUpdate(Dictionary<string, object> release, string latest)
    {
        if (IsDisposed) return;
        if (MessageBox.Show(this, "Version " + latest + " is available (installed: " + version + ").\r\n\r\nDownload the update now? The toolkit will exit fully, then Setup will open. Your saved profiles and logs will stay in place.", "Toolkit Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        ThreadPool.QueueUserWorkItem(delegate {
            try
            {
                string setupUrl = "", sumsUrl = "";
                object assetsValue;
                if (release.TryGetValue("assets", out assetsValue)) foreach (object item in (IEnumerable)assetsValue)
                {
                    Dictionary<string, object> asset = item as Dictionary<string, object>;
                    if (asset == null) continue;
                    if (Value(asset, "name") == "TEC-Systems-FieldToolkit-Setup.exe") setupUrl = Value(asset, "browser_download_url");
                    if (Value(asset, "name") == "SHA256SUMS.txt") sumsUrl = Value(asset, "browser_download_url");
                }
                if (setupUrl.Length == 0 || sumsUrl.Length == 0) throw new InvalidOperationException("The release lacks a verified installer.");
                string directory = Path.Combine(Path.GetTempPath(), "TEC-FieldToolkit-Update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                string installer = Path.Combine(directory, "TEC-Systems-FieldToolkit-Setup.exe"), sums = Path.Combine(directory, "SHA256SUMS.txt");
                using (WebClient client = new WebClient()) { client.Headers[HttpRequestHeader.UserAgent] = "TEC-Systems-Field-Toolkit"; client.DownloadFile(setupUrl, installer); client.DownloadFile(sumsUrl, sums); }
                string expected = ParseReleaseChecksum(File.ReadAllText(sums));
                using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(installer))
                { string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Installer checksum mismatch."); }
                BeginInvoke(new Action(delegate {
                    try
                    {
                        Process.Start(new ProcessStartInfo(installer, "/wait-for-exit " + Process.GetCurrentProcess().Id) { UseShellExecute = true });
                        ExitToolkit();
                    }
                    catch (Exception error) { Fail("Updates", error); }
                }));
            }
            catch (Exception error) { if (!IsDisposed) BeginInvoke(new Action(delegate { Fail("Updates", error); })); }
        });
    }

    private static string ParseReleaseChecksum(string content)
    {
        Match match = Regex.Match(content.TrimStart('\uFEFF'), @"(?im)^([a-f0-9]{64})[ \t]+TEC-Systems-FieldToolkit-Setup\.exe[ \t]*\r?$");
        if (!match.Success) throw new InvalidOperationException("Release checksum is missing.");
        return match.Groups[1].Value;
    }

    public int SelfTest()
    {
        try
        {
            testing = true;
            string checksumSample = new string('a', 64) + "  TEC-Systems-FieldToolkit-Setup.exe";
            if (ParseReleaseChecksum(checksumSample + "\n") != new string('a', 64) ||
                ParseReleaseChecksum(checksumSample + "\r\n") != new string('a', 64))
                throw new InvalidOperationException("Release checksum line ending handling failed.");
            if (tabs.TabPages.Count != 6) throw new InvalidOperationException("Expected six active tabs.");
            if (tabs.TabPages[1].Text != "IP Shifter" || tabs.TabPages[2].Text != "IP Scanner")
                throw new InvalidOperationException("IP Scanner must be next to IP Shifter.");
            TabPage networkPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "Network Troubleshooting");
            if (networkPage.Controls.Cast<Control>().Any(c => c.Text == "VLAN clues" || c.Text == "Inspect Selected Adapter")) throw new InvalidOperationException("VLAN clues controls remain.");
            GroupBox deviceAccess = networkPage.Controls.Cast<Control>().OfType<GroupBox>().First(c => c.Text == "Device Access");
            if (!deviceAccess.Controls.Cast<Control>().Any(c => c.Text == "Test Telnet") ||
                !deviceAccess.Controls.Cast<Control>().Any(c => c.Text == "Open Telnet"))
                throw new InvalidOperationException("Telnet controls are missing.");
            TabPage bmsPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "BMS Tools");
            if (!bmsPage.Controls.Cast<Control>().Any(c => c.Text == "Launch YABE") ||
                !bmsPage.Controls.Cast<Control>().Any(c => c.Text == "Connect to BMS Server (EBI)"))
                throw new InvalidOperationException("BMS server tools are missing.");
            if (bmsPage.Controls.Cast<Control>().Any(c => c.Text == "Choose the reported issue" || c.Text == "Steps taken" || c.Text == "Start"))
                throw new InvalidOperationException("BMS decision guide controls remain.");
            if (bmsProfileView == null || bmsSiteName == null ||
                !bmsPage.Controls.Cast<Control>().Any(c => c.Text == "Save Profile"))
                throw new InvalidOperationException("BMS server profile controls are missing.");
            string sample = json.Serialize(new[] { new Dictionary<string, object> { { "Name", "Test site" }, { "Host", "ebi.example.test" }, { "User", "DOMAIN\\tech" } } });
            object[] roundTrip = json.Deserialize<object[]>(sample);
            IDictionary<string, object> loaded = roundTrip[0] as IDictionary<string, object>;
            if (loaded == null || Value(loaded, "Host") != "ebi.example.test" || Value(loaded, "User") != "DOMAIN\\tech")
                throw new InvalidOperationException("BMS profile serialization failed.");
            TabPage feedbackPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "Feedback");
            if (!feedbackPage.Controls.Cast<Control>().Any(c => c.Text == Mailbox)) throw new InvalidOperationException("Feedback email address is missing.");
            if (feedbackPage.Controls.Cast<Control>().Any(c => c.Text.IndexOf("Screenshot", StringComparison.OrdinalIgnoreCase) >= 0)) throw new InvalidOperationException("Old screenshot controls remain.");
            if (IpText(IpNumber("192.168.10.42")) != "192.168.10.42") throw new InvalidOperationException("IPv4 conversion failed.");
            if (typeof(ToolkitWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name == "System.Management.Automation")) throw new InvalidOperationException("PowerShell runtime reference found.");
            Show(); Application.DoEvents();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().First(page => page.Text == "IP Shifter");
            split.SplitterDistance = 650;
            Application.DoEvents();
            if (profileView.Columns.Cast<ColumnHeader>().Sum(column => column.Width) > profileView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4)
                throw new InvalidOperationException("IP profile columns require horizontal scrolling.");
            tabs.SelectedTab = bmsPage;
            Application.DoEvents();
            if (bmsProfileView.Columns.Cast<ColumnHeader>().Sum(column => column.Width) > bmsProfileView.ClientSize.Width - 4)
                throw new InvalidOperationException("BMS profile columns require horizontal scrolling.");
            BmsServerProfile testProfile = new BmsServerProfile { Name = "Self-test site", Host = "ebi.example.test", User = "DOMAIN\\tech" };
            bmsProfiles.Add(testProfile);
            RefreshBmsProfiles();
            ListViewItem testRow = bmsProfileView.Items.Cast<ListViewItem>().First(row => Object.ReferenceEquals(row.Tag, testProfile));
            testRow.Selected = true;
            LoadSelectedBmsProfile();
            if (bmsSiteName.Text != testProfile.Name || ebiHost.Text != testProfile.Host || ebiUser.Text != testProfile.User)
                throw new InvalidOperationException("Selecting a BMS profile did not load its server fields.");
            NewBmsProfile();
            if (bmsSiteName.Text.Length != 0 || ebiHost.Text.Length != 0 || ebiUser.Text.Length != 0 || bmsProfileView.SelectedItems.Count != 0)
                throw new InvalidOperationException("New BMS profile did not clear the previous selection.");
            bmsProfiles.Remove(testProfile);
            RefreshBmsProfiles();
            if (!tray.Visible) throw new InvalidOperationException("Tray icon not visible.");
            Close(); Application.DoEvents();
            if (!tray.Visible || Visible) throw new InvalidOperationException("Close did not keep the app in the tray.");
            RestoreFromTray(); Application.DoEvents();
            if (!Visible) throw new InvalidOperationException("Tray restore failed.");
            ExitToolkit();
            if (tray.Visible) throw new InvalidOperationException("Tray icon remained after Exit.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt"), error.ToString());
            exitRequested = true;
            Close();
            return 1;
        }
    }

}
