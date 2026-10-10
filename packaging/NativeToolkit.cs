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
    internal const int RestoreMessage = 0x8000 + 175;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr filter);

    internal static bool RestoreExisting()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            IntPtr existing = FindWindow(null, "TEC Systems Field Toolkit Activation");
            if (existing == IntPtr.Zero) existing = FindWindow(null, "TEC Systems Field Toolkit");
            if (existing != IntPtr.Zero)
            {
                uint processId; GetWindowThreadProcessId(existing, out processId);
                AllowSetForegroundWindow(processId);
                if (PostMessage(existing, RestoreMessage, IntPtr.Zero, IntPtr.Zero)) return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Any(arg => arg == "/self-test" || arg == "/startup-self-test")) Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "/activity-preview")
            {
                using (ActivityWindow preview = new ActivityWindow(Path.GetTempPath(), delegate { return 0; },
                    delegate { return 0; }, true, true))
                {
                    preview.LoadSnapshot(ActivitySnapshot.Preview());
                    preview.Show(); Application.DoEvents();
                    using (Bitmap image = new Bitmap(preview.Width, preview.Height))
                    {
                        preview.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    preview.Close();
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "/main-preview")
            {
                using (ToolkitWindow preview = new ToolkitWindow()) preview.SavePreview(args[1]);
                return 0;
            }
            if (args.Length > 0 && args[0] == "/activate") return RestoreExisting() ? 0 : 1;
            if (args.Length == 3 && args[0] == "/rdp-policy")
                return RdpPolicyDiagnostics.RunElevated(args[1], args[2]);
            if (args.Length == 2 && args[0] == "/wait-for-exit")
            {
                int previousId;
                if (!Int32.TryParse(args[1], out previousId) || previousId <= 0) throw new ArgumentException("Invalid previous toolkit process.");
                try { using (Process previous = Process.GetProcessById(previousId)) if (!previous.WaitForExit(15000)) throw new InvalidOperationException("The previous toolkit did not exit. Exit it from its tray menu and try again."); }
                catch (ArgumentException) { }
            }
            bool startup = args.Any(arg => arg == "/startup");
            bool updated = args.Any(arg => arg == "/updated");
            bool selfTest = args.Length > 0 && args[0] == "/self-test";
            if (args.Length > 0 && args[0] == "/startup-self-test")
            {
                using (ToolkitWindow startupWindow = new ToolkitWindow(true)) return startupWindow.StartupSelfTest();
            }
            if (selfTest)
            {
                using (ToolkitWindow testWindow = new ToolkitWindow()) return testWindow.SelfTest();
            }
            bool firstInstance;
            using (Mutex instance = new Mutex(true, @"Local\TEC-Systems-FieldToolkit", out firstInstance))
            {
                if (!firstInstance)
                {
                    if (!startup && !RestoreExisting())
                        MessageBox.Show("The running toolkit could not be reached. Try its notification-area icon, or exit it there and reopen the toolkit.", "TEC Systems Field Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 0;
                }
                try
                {
                    using (ToolkitWindow window = new ToolkitWindow(startup, updated)) Application.Run(window);
                }
                finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0 && (args[0] == "/self-test" || args[0] == "/startup-self-test" || args[0] == "/activity-preview" || args[0] == "/main-preview"))
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt"), error.ToString());
            else
                MessageBox.Show(error.Message, "TEC Systems Field Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class ToolkitActivationWindow : NativeWindow, IDisposable
{
    private readonly Action restore;
    internal ToolkitActivationWindow(Action action)
    {
        restore = action;
        CreateHandle(new CreateParams { Caption = "TEC Systems Field Toolkit Activation" });
        // This message only requests that the window be shown; no command or data is accepted.
        NativeToolkit.ChangeWindowMessageFilterEx(Handle, NativeToolkit.RestoreMessage, 1, IntPtr.Zero);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeToolkit.RestoreMessage) { restore(); return; }
        base.WndProc(ref message);
    }
    public void Dispose() { DestroyHandle(); }
}

internal static class ToolkitStartup
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string PreferencePath = @"Software\TEC Systems\Field Toolkit";
    internal const string Entry = "TEC Systems Field Toolkit";
    internal static bool Enabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunPath))
            return key != null && key.GetValue(Entry) != null;
    }
    internal static string Command(string executable) { return "\"" + executable + "\" /startup"; }
    internal static void SetEnabled(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath))
        {
            if (enabled) key.SetValue(Entry, Command(Application.ExecutablePath));
            else key.DeleteValue(Entry, false);
        }
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencePath)) key.SetValue("StartupConfigured", 1);
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

internal sealed class IpRestorePoint
{
    public string AdapterGuid = "";
    public string AdapterName = "";
    public string CapturedUtc = "";
    public bool DhcpEnabled;
    public string IPAddress = "";
    public string SubnetMask = "";
    public string Gateway = "";
    public string Dns1 = "";
    public string Dns2 = "";
    public bool DnsAutomatic = true;
}

internal sealed class BmsServerProfile
{
    public string Name = "";
    public string Host = "";
    public string User = "";
}

internal sealed class RdpSiteProfile
{
    public string Id = System.Guid.NewGuid().ToString("N");
    public string Name = "";
    public string Host = "";
    public string Group = "";
    public string User = "";
    public string Domain = "";
    public string Resolution = "Full screen";
    public string ProtectedPassword = "";
    public bool Favorite;
    public override string ToString() { return Name + "  |  " + Host; }
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
    public string Manufacturer = "";
    public string Ports = "";
}

internal sealed class ScanResultsComparer : IComparer
{
    public readonly int Column;
    public readonly bool Descending;
    public ScanResultsComparer(int column, bool descending) { Column = column; Descending = descending; }

    private static int CompareIp(string left, string right)
    {
        IPAddress first, second;
        if (!IPAddress.TryParse(left, out first) || !IPAddress.TryParse(right, out second))
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        byte[] a = first.GetAddressBytes(), b = second.GetAddressBytes();
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    private static int ComparePing(string left, string right)
    {
        long a, b;
        if (!Int64.TryParse(left.Replace(" ms", "").Trim(), out a)) return Int64.TryParse(right.Replace(" ms", "").Trim(), out b) ? 1 : 0;
        if (!Int64.TryParse(right.Replace(" ms", "").Trim(), out b)) return -1;
        return a.CompareTo(b);
    }

    public int Compare(object first, object second)
    {
        ListViewItem a = (ListViewItem)first, b = (ListViewItem)second;
        string left = a.SubItems[Column].Text, right = b.SubItems[Column].Text;
        int order = Column == 0 ? CompareIp(left, right) : Column == 2 ? ComparePing(left, right) :
            StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        if (Descending) order = -order;
        return order != 0 ? order : CompareIp(a.Text, b.Text);
    }
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

internal sealed partial class ToolkitWindow : Form
{
    private const string Product = "TEC Systems Field Toolkit";
    private const string Mailbox = "IT@tec-system.com";
    private static readonly Color Cobalt = Color.FromArgb(0, 67, 230);
    private static readonly Color Green = Color.FromArgb(31, 128, 78);
    private static readonly Color Slate = Color.FromArgb(75, 94, 116);
    private static readonly Dictionary<string, string> MacVendors = LoadMacVendors();
    private readonly string folder;
    private readonly string configPath;
    private readonly string logPath;
    private readonly string version;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly List<ToolkitProfile> profiles = new List<ToolkitProfile>();
    private readonly List<IpRestorePoint> ipRestorePoints = new List<IpRestorePoint>();
    private readonly List<BmsServerProfile> bmsProfiles = new List<BmsServerProfile>();
    private readonly List<RdpSiteProfile> rdpSites = new List<RdpSiteProfile>();
    private readonly List<ToolkitAdapter> adapters = new List<ToolkitAdapter>();
    private readonly HashSet<ListView> themedLists = new HashSet<ListView>();
    private readonly HashSet<ComboBox> themedCombos = new HashSet<ComboBox>();
    private readonly ToolTip tips = new ToolTip();
    private Dictionary<string, object> settings = new Dictionary<string, object>();
    private bool dark;
    private bool followWindowsTheme = true;
    private ToolStripMenuItem followThemeItem;
    private bool exitRequested;
    private bool stopScan;
    private bool scanRunning;
    private string yabePath = "";
    private NotifyIcon tray;
    private ToolkitActivationWindow activation;
    private System.Windows.Forms.Timer updateTimer;
    private Button updateButton;
    private Dictionary<string, object> availableRelease;
    private string availableVersion = "";
    private int checkingUpdates;
    private int checkingInternet;
    private bool testing;
    private ContextMenuStrip trayMenu;
    private System.Windows.Forms.Timer internetTimer;
    private System.Windows.Forms.Timer usageTimer;
    private DateTime? usageStartedUtc;
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
    private RdpManager rdpManager;
    private TextBox networkTarget;
    private TextBox switchTarget;
    private ComboBox switchScheme;
    private TextBox telnetPort;
    private TextBox playbookText;
    private ComboBox bundleType;
    private ListView profileView;
    private ComboBox profileSort;
    private ComboBox adapterChoice;
    private TextBox profileName;
    private TextBox ipField;
    private TextBox maskField;
    private TextBox gatewayField;
    private TextBox dns1Field;
    private TextBox dns2Field;
    private ComboBox scanAdapter;
    private ComboBox networkGatewayChoice;
    private readonly List<ToolkitAdapter> gatewayAdapters = new List<ToolkitAdapter>();
    private string scanSelectedAdapterGuid = "";
    private string scanSelectedAdapterName = "";
    private TextBox scanStart;
    private TextBox scanEnd;
    private Label scanScope;
    private Label scanStatus;
    private ListView scanView;
    private int scanSortColumn = -1;
    private bool scanSortDescending;
    private Button scanButton;
    private TextBox capturePath;
    private TextBox captureReport;
    private ListView captureFindings;
    private Label captureStatus;
    private Button captureBrowse;
    private Button captureAnalyze;
    private Button captureCancel;
    private Button captureExport;
    private CancellationTokenSource captureCancellation;

    public ToolkitWindow(bool startup = false, bool updated = false)
    {
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TEC Systems", "Field Toolkit");
        Directory.CreateDirectory(folder);
        configPath = Path.Combine(folder, "config.json");
        logPath = Path.Combine(folder, "TEC_FieldToolkit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        string versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
        version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "0.0.0";
        dark = WindowsPrefersDark();
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
        activation = new ToolkitActivationWindow(RestoreFromTray);
        if (startup) ShowInTaskbar = false;
        ApplyTheme();
        Shown += delegate {
            SetInitialSplit(); ApplyTitleBarTheme();
            if (startup) Hide();
            if (testing) return;
            RefreshProfiles(); RefreshAdaptersAsync(); UpdateInternet(); internetTimer.Start(); updateTimer.Start(); usageTimer.Start(); CheckUpdates(true);
            Log("Startup", "OK", Product + " " + version);
            if (updated) BeginInvoke(new Action(delegate {
                Log("Updates", "OK", "Successfully updated to version " + version + ".");
                MessageBox.Show(this, "Successfully updated to version " + version + ".", "Toolkit Update Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
        };
        FormClosing += OnClosing;
        Activated += delegate { StartUsage(); };
        Deactivate += delegate { FlushUsage(); };
        VisibleChanged += delegate { if (!Visible) FlushUsage(); };
        SizeChanged += delegate { if (WindowState == FormWindowState.Minimized) FlushUsage(); };
        SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
    }

    private static string Value(IDictionary<string, object> row, string key)
    {
        object value;
        return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
    }

    private static bool WindowsPrefersDark()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                if (value == null && key != null) value = key.GetValue("SystemUsesLightTheme");
                return value != null && Convert.ToInt32(value) == 0;
            }
        }
        catch { return false; }
    }

    private void OnWindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (!followWindowsTheme || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(delegate { if (followWindowsTheme && !IsDisposed) { dark = WindowsPrefersDark(); ApplyTheme(); } })); }
        catch (InvalidOperationException) { }
    }

    private void LoadSettings()
    {
        if (!File.Exists(configPath)) return;
        try
        {
            settings = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            if (settings == null) settings = new Dictionary<string, object>();
            object mode;
            if (settings.TryGetValue("ThemeMode", out mode) && String.Equals(Convert.ToString(mode), "manual", StringComparison.OrdinalIgnoreCase))
            {
                followWindowsTheme = false;
                if (settings.TryGetValue("DarkMode", out mode)) dark = Convert.ToBoolean(mode);
            }
            yabePath = Value(settings, "YabePath");
            bool migratedRdpIds = false;
            object saved;
            if (settings.TryGetValue("IpRestorePoints", out saved) && saved is IEnumerable)
            {
                foreach (object entry in (IEnumerable)saved)
                {
                    IDictionary<string, object> row = entry as IDictionary<string, object>;
                    if (row == null || String.IsNullOrWhiteSpace(Value(row, "AdapterGuid"))) continue;
                    bool dhcp, automatic;
                    if (!Boolean.TryParse(Value(row, "DhcpEnabled"), out dhcp) ||
                        !Boolean.TryParse(Value(row, "DnsAutomatic"), out automatic)) continue;
                    ipRestorePoints.Add(new IpRestorePoint { AdapterGuid = Value(row, "AdapterGuid"), AdapterName = Value(row, "AdapterName"),
                        CapturedUtc = Value(row, "CapturedUtc"), DhcpEnabled = dhcp, IPAddress = Value(row, "IPAddress"),
                        SubnetMask = Value(row, "SubnetMask"), Gateway = Value(row, "Gateway"), Dns1 = Value(row, "Dns1"),
                        Dns2 = Value(row, "Dns2"), DnsAutomatic = automatic });
                }
            }
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
            if (settings.TryGetValue("RdpSites", out saved) && saved is IEnumerable)
            {
                foreach (object entry in (IEnumerable)saved)
                {
                    IDictionary<string, object> row = entry as IDictionary<string, object>;
                    if (row == null) continue;
                    string name = Value(row, "Name").Trim(), host = Value(row, "Host").Trim();
                    if (name.Length == 0 || !ValidTarget(host) || rdpSites.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    Guid savedId;
                    if (!Guid.TryParse(Value(row, "Id"), out savedId)) migratedRdpIds = true;
                    rdpSites.Add(new RdpSiteProfile { Id = RdpManager.ValidProfileId(Value(row, "Id")), Name = name, Host = host, Group = Value(row, "Group"),
                        User = Value(row, "User"), Domain = Value(row, "Domain"),
                        Resolution = String.IsNullOrEmpty(Value(row, "Resolution")) ? "Full screen" : Value(row, "Resolution"),
                        ProtectedPassword = Value(row, "ProtectedPassword"),
                        Favorite = String.Equals(Value(row, "Favorite"), "True", StringComparison.OrdinalIgnoreCase) });
                }
            }
            if (bmsProfiles.Count == 0 && ValidTarget(Value(settings, "EbiHost")))
                bmsProfiles.Add(new BmsServerProfile { Name = "Saved server", Host = Value(settings, "EbiHost"), User = Value(settings, "EbiUser") });
            LoadWorkspaceSettings();
            if (migratedRdpIds) SaveSettings();
        }
        catch (Exception error) { File.AppendAllText(logPath, "Settings warning: " + error.Message + Environment.NewLine); }
    }

    private void SaveSettings()
    {
        settings["DarkMode"] = dark;
        settings["ThemeMode"] = followWindowsTheme ? "system" : "manual";
        settings["YabePath"] = yabePath;
        settings["IpRestorePoints"] = ipRestorePoints.Select(p => new Dictionary<string, object> {
            { "AdapterGuid", p.AdapterGuid }, { "AdapterName", p.AdapterName }, { "CapturedUtc", p.CapturedUtc },
            { "DhcpEnabled", p.DhcpEnabled }, { "IPAddress", p.IPAddress }, { "SubnetMask", p.SubnetMask },
            { "Gateway", p.Gateway }, { "Dns1", p.Dns1 }, { "Dns2", p.Dns2 }, { "DnsAutomatic", p.DnsAutomatic }
        }).ToArray();
        settings["SiteProfiles"] = profiles.Select(p => new Dictionary<string, object> {
            { "Name", p.Name }, { "Adapter", p.Adapter }, { "IPAddress", p.IPAddress },
            { "SubnetMask", p.SubnetMask }, { "Gateway", p.Gateway }, { "Dns1", p.Dns1 }, { "Dns2", p.Dns2 }
        }).ToArray();
        settings["BmsServerProfiles"] = bmsProfiles.Select(p => new Dictionary<string, object> {
            { "Name", p.Name }, { "Host", p.Host }, { "User", p.User }
        }).ToArray();
        settings["RdpSites"] = rdpSites.Select(p => new Dictionary<string, object> {
            { "Id", p.Id }, { "Name", p.Name }, { "Host", p.Host }, { "Group", p.Group }, { "User", p.User },
            { "Domain", p.Domain }, { "Resolution", p.Resolution }, { "ProtectedPassword", p.ProtectedPassword },
            { "Favorite", p.Favorite }
        }).ToArray();
        SaveWorkspaceSettings();
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
        button.Click += delegate { try { action(); } catch (Exception error) { if (!IsDisposed) Fail(text, error); } };
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
        header.Controls.Add(L("Version " + version, 342, 76, 200));
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
        updateButton = B(footer, "Check Updates", 338, 5, 148, OpenUpdateOffer, "Check for a newer toolkit release or install an available update.", Cobalt);
        B(footer, "Activity Report", 500, 5, 148, ShowActivityReport, "View this week's saved sites and technician activity.", Green);
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
        TabPage rdpPage = Page("RDP");
        rdpManager = new RdpManager(rdpSites, SaveSettings, folder, delegate(string message) { Log("RDP", "OK", message); });
        rdpPage.Controls.Add(rdpManager);
        // Site workspaces are retained in settings but hidden for the field launch.
        BuildNetworkPage(Page("Network Troubleshooting"));
        BuildCapturePage(Page("Packet Analyzer"));
        BuildBmsPage(Page("BMS Tools"));
        BuildFeedbackPage(Page("Feedback"));
        BuildLogPanel();
        internetTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        internetTimer.Tick += delegate { UpdateInternet(); };
        updateTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
        updateTimer.Tick += delegate { CheckUpdates(true); };
        usageTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        usageTimer.Tick += delegate { FlushUsage(); if (ContainsFocus) StartUsage(); };
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

    private void ShowActivityReport()
    {
        using (ActivityWindow report = new ActivityWindow(folder, delegate { return rdpSites.Count; },
            delegate { return profiles.Count; }, dark)) report.ShowDialog(this);
    }

    private void StartUsage()
    {
        if (!testing && Visible && WindowState != FormWindowState.Minimized && !usageStartedUtc.HasValue)
            usageStartedUtc = DateTime.UtcNow;
    }

    private void FlushUsage()
    {
        if (!usageStartedUtc.HasValue) return;
        int seconds = Math.Max(0, (int)(DateTime.UtcNow - usageStartedUtc.Value).TotalSeconds);
        usageStartedUtc = null;
        if (seconds > 0 && !testing)
            try { File.AppendAllText(logPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] [INFO] [Usage] Foreground seconds: " + seconds + Environment.NewLine); }
            catch { /* Usage totals are best effort if the log is unavailable. */ }
    }

    private void ConfigureTray()
    {
        string trayFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "TEC Systems Field Toolkit Tray.ico");
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Toolkit", null, delegate { RestoreFromTray(); });
        trayMenu.Items.Add("Check Updates", null, delegate { RestoreFromTray(); OpenUpdateOffer(); });
        ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows") { Checked = ToolkitStartup.Enabled() };
        startupItem.Click += delegate {
            try { ToolkitStartup.SetEnabled(!startupItem.Checked); startupItem.Checked = ToolkitStartup.Enabled(); }
            catch (Exception error) { Fail("Windows Startup", error); }
        };
        trayMenu.Items.Add(startupItem);
        followThemeItem = new ToolStripMenuItem("Follow Windows Theme") { Checked = followWindowsTheme };
        followThemeItem.Click += delegate {
            followWindowsTheme = !followWindowsTheme;
            if (followWindowsTheme) { dark = WindowsPrefersDark(); ApplyTheme(); }
            followThemeItem.Checked = followWindowsTheme;
            SaveSettings();
        };
        trayMenu.Items.Add(followThemeItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit Toolkit", null, delegate { ExitToolkit(); });
        tray = new NotifyIcon { Icon = File.Exists(trayFile) ? new Icon(trayFile) : Icon, Text = Product, ContextMenuStrip = trayMenu, Visible = true };
        tray.MouseClick += OnTrayMouseClick;
        tray.BalloonTipClicked += delegate { RestoreFromTray(); OpenUpdateOffer(); };
    }

    private void OnTrayMouseClick(object sender, MouseEventArgs args)
    {
        if (args.Button == MouseButtons.Left) RestoreFromTray();
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        NativeToolkit.ChangeWindowMessageFilterEx(Handle, NativeToolkit.RestoreMessage, 1, IntPtr.Zero);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        NativeToolkit.SetForegroundWindow(Handle);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeToolkit.RestoreMessage)
        {
            RestoreFromTray();
            return;
        }
        base.WndProc(ref message);
    }

    private void ExitToolkit() { exitRequested = true; Close(); }

    private void OnClosing(object sender, FormClosingEventArgs args)
    {
        if (!exitRequested && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; Hide(); return; }
        FlushUsage();
        SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
        if (internetTimer != null) { internetTimer.Stop(); internetTimer.Dispose(); }
        if (updateTimer != null) { updateTimer.Stop(); updateTimer.Dispose(); }
        if (usageTimer != null) { usageTimer.Stop(); usageTimer.Dispose(); }
        if (activation != null) { activation.Dispose(); activation = null; }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        if (trayMenu != null) trayMenu.Dispose();
        if (brandPicture != null) brandPicture.Image = null;
        if (darkBrand != null) darkBrand.Dispose();
        if (lightBrand != null) lightBrand.Dispose();
    }

    private void ToggleTheme()
    {
        dark = !dark;
        followWindowsTheme = false;
        if (followThemeItem != null) followThemeItem.Checked = false;
        ApplyTheme(); SaveSettings();
    }

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
            else if (control is SplitterPanel || control is Panel || control is TableLayoutPanel || control is TabControl || control is TabPage)
                control.BackColor = canvas;
            else if (control is TreeView)
            {
                control.BackColor = input;
                ((TreeView)control).LineColor = text;
                control.Invalidate();
            }
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
        if (themeButton != null) themeButton.Text = dark ? "Light Mode" : "Dark Mode";
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

    private void BuildCapturePage(TabPage page)
    {
        page.Controls.Add(new Label { Text = "Packet Capture Analyzer", Left = 18, Top = 18, Width = 520, Height = 34,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold) });
        page.Controls.Add(L("Open a saved Wireshark file to get a plain-language traffic report. Nothing is uploaded.", 18, 58, 790));
        capturePath = T(18, 90, 510, ""); capturePath.ReadOnly = true; page.Controls.Add(capturePath);
        captureBrowse = B(page, "Open Packet File", 540, 86, 146, delegate {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "Choose a Wireshark capture", Filter = "Capture files (*.pcap;*.pcapng;*.cap)|*.pcap;*.pcapng;*.cap|All files (*.*)|*.*", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { capturePath.Text = dialog.FileName; captureReport.Clear(); captureFindings.Items.Clear(); captureExport.Enabled = false; captureStatus.Text = "Ready to analyze."; }
        }, "Open a saved Wireshark packet capture.", Cobalt);
        captureAnalyze = B(page, "Analyze", 18, 126, 120, AnalyzeCapture, "Analyze the selected capture without uploading it.", Green);
        captureCancel = B(page, "Stop Analysis", 150, 126, 120, delegate { if (captureCancellation != null) captureCancellation.Cancel(); }, "Stop reading the selected file.", Slate);
        captureCancel.Enabled = false;
        captureExport = B(page, "Export Report", 282, 126, 150, ExportCaptureReport, "Save the analysis as a text file.", Cobalt);
        captureExport.Enabled = false;
        captureStatus = L("Choose a capture to begin.", 448, 133, 390); page.Controls.Add(captureStatus);
        page.Controls.Add(L("Requires Wireshark's TShark component. The toolkit does not record packets.", 18, 172, 790));
        page.Controls.Add(L("Priority findings", 18, 197, 300));
        captureFindings = new ListView { Left = 18, Top = 222, Width = 790, Height = 165, View = View.Details,
            FullRowSelect = true, GridLines = true, ShowItemToolTips = true, HideSelection = false };
        captureFindings.Columns.Add("Priority", 72);
        captureFindings.Columns.Add("Finding", 188);
        captureFindings.Columns.Add("What it means", 250);
        captureFindings.Columns.Add("Next check", 270);
        page.Controls.Add(captureFindings);
        page.Controls.Add(L("Detailed report", 18, 399, 300));
        captureReport = new TextBox { Left = 18, Top = 424, Width = 790, Height = 170, ReadOnly = true,
            Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f) };
        page.Controls.Add(captureReport);
        Action fit = delegate {
            int width = Math.Max(620, page.ClientSize.Width - 36);
            capturePath.Width = Math.Max(180, width - 158);
            captureBrowse.Left = capturePath.Right + 12;
            captureFindings.Width = width;
            captureReport.Width = width;
            captureReport.Height = Math.Max(120, page.ClientSize.Height - captureReport.Top - 18);
            int detailsWidth = Math.Max(120, width - 72 - 180 - 245 - 8);
            captureFindings.Columns[0].Width = 72;
            captureFindings.Columns[1].Width = 180;
            captureFindings.Columns[2].Width = 245;
            captureFindings.Columns[3].Width = detailsWidth;
            captureStatus.Width = Math.Max(150, page.ClientSize.Width - captureStatus.Left - 18);
        };
        page.Resize += delegate { fit(); };
        fit();
    }

    private void AnalyzeCapture()
    {
        string path = capturePath.Text;
        if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose a capture file first.");
        if (captureCancellation != null) return;
        CancellationTokenSource source = new CancellationTokenSource();
        captureCancellation = source;
        captureBrowse.Enabled = false; captureAnalyze.Enabled = false; captureCancel.Enabled = true; captureExport.Enabled = false;
        captureStatus.Text = "Analyzing capture...";
        ThreadPool.QueueUserWorkItem(delegate {
            CaptureAnalysis analysis = null;
            string error = null;
            try { analysis = CaptureAnalyzer.Analyze(path, source.Token); }
            catch (OperationCanceledException) { error = "Analysis stopped."; }
            catch (Exception failure) { error = failure.Message; }
            if (!IsDisposed && IsHandleCreated)
                try { BeginInvoke(new Action(delegate {
                    if (IsDisposed) return;
                    captureFindings.BeginUpdate();
                    try
                    {
                        captureFindings.Items.Clear();
                        if (analysis != null)
                            foreach (CaptureFinding finding in analysis.Findings)
                            {
                                ListViewItem item = new ListViewItem(new[] { finding.Severity, finding.Title, finding.Explanation, finding.NextCheck });
                                item.ToolTipText = finding.Explanation + Environment.NewLine + "Next check: " + finding.NextCheck;
                                item.ForeColor = finding.Severity == "High" ? Color.FromArgb(232, 132, 76) :
                                    finding.Severity == "Medium" ? Color.FromArgb(232, 186, 87) : Color.FromArgb(84, 187, 221);
                                captureFindings.Items.Add(item);
                            }
                    }
                    finally { captureFindings.EndUpdate(); }
                    captureReport.Text = analysis == null ? error : analysis.Report;
                    captureStatus.Text = analysis == null ? error : "Analysis complete: " + analysis.Findings.Count + " findings.";
                    captureBrowse.Enabled = true; captureAnalyze.Enabled = true; captureCancel.Enabled = false;
                    captureExport.Enabled = analysis != null;
                    captureCancellation = null;
                    Log("Capture", analysis == null ? "ERROR" : "OK", analysis == null ? error : "Analyzed " + Path.GetFileName(path));
                    source.Dispose();
                })); }
                catch (InvalidOperationException) { source.Dispose(); }
            else source.Dispose();
        });
    }

    private void ExportCaptureReport()
    {
        if (String.IsNullOrWhiteSpace(captureReport.Text)) return;
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = "Packet_Report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" })
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(dialog.FileName, captureReport.Text, Encoding.UTF8);
                Log("Capture", "OK", "Saved packet report to " + dialog.FileName);
            }
    }

    private void BuildWindowsPage(TabPage page)
    {
        page.Controls.Add(L("Host / IP", 18, 18, 120));
        pingTarget = T(18, 42, 280, "8.8.8.8"); page.Controls.Add(pingTarget);
        B(page, "Ping Terminal", 314, 38, 132, delegate { PingTerminal(pingTarget.Text, continuousPing.Checked); }, "Open live replies in a terminal. Four pings by default or continuous when checked.", Cobalt);
        B(page, "DNS Lookup", 458, 38, 118, delegate { Lookup(pingTarget.Text); }, "Resolve the entered hostname using Windows DNS.", Cobalt);
        continuousPing = new CheckBox { Text = "Continuous ping", Left = 18, Top = 78, Width = 160 }; page.Controls.Add(continuousPing);
        page.Controls.Add(L("Windows checks", 18, 106, 200));
        string[] labels = { "System Summary", "Disk Summary", "Service Check", "Event Errors", "Problem Devices", "Update Status" };
        Action[] actions = { SystemSummary, DiskSummary, ServiceCheck, EventErrors, ProblemDevices, UpdateStatus };
        for (int i = 0; i < labels.Length; i++) B(page, labels[i], 18 + (i % 5) * 144, 130 + (i / 5) * 40, 132, actions[i], "Write " + labels[i].ToLowerInvariant() + " findings to the technician log.", Cobalt);
        page.Controls.Add(L("Open tools", 18, 220, 180));
        FlowLayoutPanel tools = new FlowLayoutPanel { Left = 18, Top = 244, Width = 708, AutoScroll = false, WrapContents = true };
        page.Controls.Add(tools);
        string[,] specs = {
            { "Services", "services.msc", "" }, { "Event Viewer", "eventvwr.msc", "" }, { "Device Manager", "devmgmt.msc", "" },
            { "Computer Mgmt", "compmgmt.msc", "" }, { "Local Users", "lusrmgr.msc", "" }, { "Programs", "appwiz.cpl", "" },
            { "System Props", "sysdm.cpl", "" }, { "Display Settings", "ms-settings:display", "" }, { "Task Manager", "taskmgr.exe", "" }, { "Command Prompt", "cmd.exe", "" },
            { "Task Scheduler", "taskschd.msc", "" }, { "Firewall Console", "wf.msc", "" }, { "Credential Mgr", "control.exe", "/name Microsoft.CredentialManager" },
            { "Shared Folders", "fsmgmt.msc", "" }, { "Windows Update", "ms-settings:windowsupdate", "" }, { "Remote Desktop", "mstsc.exe", "" },
            { "Printers", "control.exe", "printers" }, { "IPConfig /all", "cmd.exe", "/k ipconfig /all" }
        };
        for (int i = 0; i < specs.GetLength(0); i++) { string name = specs[i, 0], exe = specs[i, 1], arg = specs[i, 2]; B(tools, name, 0, 0, 126, delegate { OpenTool(name, exe, arg); }, "Open " + name + " for Windows troubleshooting.", Slate).Margin = new Padding(0, 0, 8, 8); }
        Action fitTools = delegate {
            tools.Width = Math.Max(134, page.ClientSize.Width - 36);
            int columns = Math.Max(1, tools.ClientSize.Width / 134);
            tools.Height = ((tools.Controls.Count + columns - 1) / columns) * 42 + 4;
        };
        page.SizeChanged += delegate { fitTools(); };
        fitTools();
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
        Label sortLabel = L("Sort", 555, 15, 45); page.Controls.Add(sortLabel);
        profileSort = new ComboBox { Left = 605, Top = 10, Width = 112, DropDownStyle = ComboBoxStyle.DropDownList };
        profileSort.Items.AddRange(new object[] { "A–Z", "Z–A" }); profileSort.SelectedIndex = 0;
        profileSort.SelectedIndexChanged += delegate { if (profileView != null) RefreshProfiles(); };
        page.Controls.Add(profileSort);
        profileView = new ListView { Left = 18, Top = 41, Width = 700, Height = 160, View = View.Details, FullRowSelect = true, GridLines = true };
        profileView.Columns.Add("Profile", 110); profileView.Columns.Add("Adapter", 120); profileView.Columns.Add("IP", 105);
        profileView.Columns.Add("Mask", 105); profileView.Columns.Add("Gateway", 92);
        profileView.DoubleClick += delegate { LoadProfile(); };
        page.Controls.Add(profileView);
        profileView.SizeChanged += delegate { UpdateProfileColumns(); };
        page.SizeChanged += delegate { profileView.Width = Math.Max(550, page.ClientSize.Width - 36); profileSort.Left = profileView.Right - profileSort.Width; sortLabel.Left = profileSort.Left - 50; UpdateProfileColumns(); };
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
        B(page, "Restore Previous IP", 320, 510, 168, RestorePreviousIp, "Restore this adapter's settings from before the last IP Shifter change.", Green);
        B(page, "IP History", 504, 510, 112, ShowIpHistory, "View verified and failed IP changes on this laptop.", Cobalt);
        profileView.Width = Math.Max(550, page.ClientSize.Width - 36);
        profileSort.Left = profileView.Right - profileSort.Width; sortLabel.Left = profileSort.Left - 50;
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
        ShowAdapters(ReadAdapters(), selected);
    }

    private void RefreshAdaptersAsync()
    {
        ThreadPool.QueueUserWorkItem(delegate {
            List<ToolkitAdapter> found = ReadAdapters();
            if (!IsDisposed && IsHandleCreated)
                try { BeginInvoke(new Action(delegate {
                    if (!IsDisposed) ShowAdapters(found, "");
                })); }
                catch (InvalidOperationException) { }
        });
    }

    private List<ToolkitAdapter> ReadAdapters()
    {
        List<ToolkitAdapter> found = new List<ToolkitAdapter>();
        try
        {
            foreach (ManagementObject item in new ManagementObjectSearcher("SELECT DeviceID,NetConnectionID,GUID,NetConnectionStatus,MACAddress,Description,PNPDeviceID,ServiceName FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL").Get())
            {
                string name = Convert.ToString(item["NetConnectionID"]);
                if (String.IsNullOrWhiteSpace(name) || IsBluetoothAdapter(name, Convert.ToString(item["Description"]), Convert.ToString(item["PNPDeviceID"]), Convert.ToString(item["ServiceName"]))) continue;
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
                if (!String.IsNullOrEmpty(adapter.Guid))
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapter.Guid))
                    {
                        if (key != null)
                        {
                            if (adapter.IP == "No IP")
                            {
                                string stored = FirstIpv4(key.GetValue("IPAddress"));
                                if (stored != "No IP" && stored != "0.0.0.0") { adapter.IP = stored; adapter.Mask = FirstIpv4(key.GetValue("SubnetMask")); }
                            }
                            if (!IsUsableIpv4(adapter.Gateway)) adapter.Gateway = FirstIpv4(key.GetValue("DefaultGateway"));
                            if (String.IsNullOrEmpty(adapter.Dns)) adapter.Dns = Convert.ToString(key.GetValue("NameServer"));
                        }
                    }
                }
                if (!IsUsableIpv4(adapter.Gateway)) adapter.Gateway = GatewayFromNetworkInterface(adapter.Guid);
                found.Add(adapter);
            }
        }
        catch (Exception error) { Log("Adapter", "ERROR", error.Message); }
        found.Sort((a, b) => String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private void ShowAdapters(List<ToolkitAdapter> found, string selected)
    {
        adapters.Clear();
        adapters.AddRange(found);
        adapterChoice.Items.Clear();
        foreach (ToolkitAdapter adapter in adapters) adapterChoice.Items.Add(adapter.Name + " [" + adapter.Status + "] - IP: " + adapter.IP + (String.IsNullOrEmpty(adapter.Mask) ? "" : " / " + adapter.Mask));
        if (adapters.Count > 0) adapterChoice.SelectedIndex = Math.Max(0, adapters.FindIndex(a => a.Name == selected));
        RefreshScannerAdapters();
        RefreshGatewayAdapters();
        Log("Adapter", "INFO", adapters.Count + " adapters found.");
    }

    private static bool IsBluetoothAdapter(string name, string description, string deviceId, string service)
    {
        return (name ?? "").IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (description ?? "").IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (deviceId ?? "").StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(service, "BthPan", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ToolkitAdapter> ReadIpAssignments()
    {
        // Inspect all adapters, including Bluetooth and disconnected devices hidden from the selector.
        Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT GUID,NetConnectionID,Description FROM Win32_NetworkAdapter"))
        using (ManagementObjectCollection rows = search.Get())
            foreach (ManagementObject row in rows)
            {
                string guid = Convert.ToString(row["GUID"]);
                if (guid.Length > 0) names[guid] = String.IsNullOrWhiteSpace(Convert.ToString(row["NetConnectionID"])) ? Convert.ToString(row["Description"]) : Convert.ToString(row["NetConnectionID"]);
            }
        List<ToolkitAdapter> result = new List<ToolkitAdapter>();
        using (ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT SettingID,Description,IPAddress FROM Win32_NetworkAdapterConfiguration"))
        using (ManagementObjectCollection rows = search.Get())
            foreach (ManagementObject row in rows)
            {
                string guid = Convert.ToString(row["SettingID"]), name;
                if (!names.TryGetValue(guid, out name)) name = Convert.ToString(row["Description"]);
                List<string> values = new List<string>((row["IPAddress"] as string[]) ?? new string[0]);
                if (guid.Length > 0)
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + guid))
                        if (key != null) values.AddRange((key.GetValue("IPAddress") as string[]) ?? new string[0]);
                foreach (string address in values.Distinct())
                {
                    IPAddress parsed;
                    if (address != "0.0.0.0" && IPAddress.TryParse(address, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(new ToolkitAdapter { Name = name, Guid = guid, IP = parsed.ToString() });
                }
            }
        return result;
    }

    private static string[] IpConflictOwners(IEnumerable<ToolkitAdapter> assignments, ToolkitAdapter selected, string ip)
    {
        return assignments.Where(a => a.IP == ip &&
            !(String.IsNullOrEmpty(selected.Guid) ? String.Equals(a.Name, selected.Name, StringComparison.OrdinalIgnoreCase) : String.Equals(a.Guid, selected.Guid, StringComparison.OrdinalIgnoreCase)))
            .Select(a => String.IsNullOrWhiteSpace(a.Name) ? a.Guid : a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

    private static bool IsUsableIpv4(string value)
    {
        IPAddress parsed;
        return !String.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork && !parsed.Equals(IPAddress.Any);
    }

    private static string GatewayFromNetworkInterface(string guid)
    {
        if (String.IsNullOrEmpty(guid)) return "No IP";
        try
        {
            NetworkInterface nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                String.Equals(item.Id.Trim('{', '}'), guid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
            if (nic != null)
                foreach (GatewayIPAddressInformation gateway in nic.GetIPProperties().GatewayAddresses)
                    if (IsUsableIpv4(gateway.Address.ToString())) return gateway.Address.ToString();
        }
        catch (NetworkInformationException) { }
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
        ToolkitProfile selectedProfile = profileView.SelectedItems.Count == 0 ? null : profileView.SelectedItems[0].Tag as ToolkitProfile;
        profileView.Items.Clear();
        IEnumerable<ToolkitProfile> ordered = profileSort != null && profileSort.SelectedIndex == 1 ?
            profiles.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase) :
            profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (ToolkitProfile p in ordered)
        {
            ListViewItem row = new ListViewItem(p.Name) { Tag = p };
            row.SubItems.Add(p.Adapter); row.SubItems.Add(p.IPAddress); row.SubItems.Add(p.SubnetMask); row.SubItems.Add(p.Gateway);
            profileView.Items.Add(row);
            if (Object.ReferenceEquals(p, selectedProfile)) row.Selected = true;
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
        bool created = p == null;
        if (created) { p = new ToolkitProfile(); profiles.Add(p); }
        p.Name = name; p.Adapter = SelectedAdapter().Name; p.IPAddress = ipField.Text.Trim(); p.SubnetMask = maskField.Text.Trim();
        p.Gateway = gatewayField.Text.Trim(); p.Dns1 = dns1Field.Text.Trim(); p.Dns2 = dns2Field.Text.Trim();
        SaveSettings(); RefreshProfiles(); Log("IP Shifter", "OK", (created ? "Added profile: " : "Updated profile: ") + name);
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
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/wait-for-exit " + Process.GetCurrentProcess().Id) { UseShellExecute = true, Verb = "runas" }); ExitToolkit(); }
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

    private static List<string> Ipv4Values(object value)
    {
        string[] raw = value as string[] ?? new string[0];
        return raw.Where(address => { IPAddress parsed; return IPAddress.TryParse(address, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && address != "0.0.0.0"; }).ToList();
    }

    private IpRestorePoint CaptureIpRestorePoint(ToolkitAdapter adapter)
    {
        if (String.IsNullOrWhiteSpace(adapter.Guid)) throw new InvalidOperationException("Windows did not provide an adapter ID, so its settings cannot be saved for restore. No settings were changed.");
        using (ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT SettingID,DHCPEnabled,IPAddress,IPSubnet,DefaultIPGateway FROM Win32_NetworkAdapterConfiguration"))
        using (ManagementObjectCollection rows = search.Get())
            foreach (ManagementObject row in rows)
            {
                if (!String.Equals(Convert.ToString(row["SettingID"]).Trim('{', '}'), adapter.Guid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)) continue;
                if (row["DHCPEnabled"] == null) break;
                IpRestorePoint point = new IpRestorePoint { AdapterGuid = adapter.Guid, AdapterName = adapter.Name,
                    CapturedUtc = DateTime.UtcNow.ToString("o"), DhcpEnabled = Convert.ToBoolean(row["DHCPEnabled"]) };
                if (!point.DhcpEnabled)
                {
                    string[] addresses = row["IPAddress"] as string[] ?? new string[0];
                    string[] masks = row["IPSubnet"] as string[] ?? new string[0];
                    List<int> ipv4Indexes = new List<int>();
                    for (int i = 0; i < addresses.Length; i++)
                    {
                        IPAddress parsed;
                        if (IPAddress.TryParse(addresses[i], out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && addresses[i] != "0.0.0.0") ipv4Indexes.Add(i);
                    }
                    if (ipv4Indexes.Count != 1 || ipv4Indexes[0] >= masks.Length)
                        throw new InvalidOperationException("This adapter has multiple or unavailable static IPv4 addresses. IP Shifter cannot safely save and restore that configuration, so no settings were changed.");
                    point.IPAddress = ValidateIpv4(addresses[ipv4Indexes[0]], "Previous IP address", false);
                    point.SubnetMask = ValidateIpv4(masks[ipv4Indexes[0]], "Previous subnet mask", false);
                }
                List<string> gateways = Ipv4Values(row["DefaultIPGateway"]);
                if (gateways.Count > 1) throw new InvalidOperationException("This adapter has multiple IPv4 gateways. IP Shifter cannot safely restore them, so no settings were changed.");
                point.Gateway = gateways.Count == 0 ? "" : ValidateIpv4(gateways[0], "Previous gateway", false);
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapter.Guid))
                {
                    if (key == null) throw new InvalidOperationException("Could not read the adapter's DNS settings for restore. No settings were changed.");
                    string nameServer = Convert.ToString(key.GetValue("NameServer")).Trim();
                    point.DnsAutomatic = nameServer.Length == 0;
                    if (!point.DnsAutomatic)
                    {
                        string[] dns = nameServer.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (dns.Length < 1 || dns.Length > 2) throw new InvalidOperationException("This adapter has more than two custom DNS servers. IP Shifter cannot safely restore them, so no settings were changed.");
                        point.Dns1 = ValidateIpv4(dns[0], "Previous DNS 1", false);
                        if (dns.Length == 2) point.Dns2 = ValidateIpv4(dns[1], "Previous DNS 2", false);
                    }
                }
                return point;
            }
        throw new InvalidOperationException("Could not read the selected adapter's Windows IP settings for restore. No settings were changed.");
    }

    private void SaveIpRestorePoint(ToolkitAdapter adapter)
    {
        IpRestorePoint point = CaptureIpRestorePoint(adapter);
        ipRestorePoints.RemoveAll(p => String.Equals(p.AdapterGuid, adapter.Guid, StringComparison.OrdinalIgnoreCase));
        ipRestorePoints.Add(point);
        SaveSettings();
        Log("IP Shifter", "INFO", "Saved previous IP settings for " + adapter.Name + ".");
    }

    private static string[] RestoreIpCommands(IpRestorePoint point, string adapterName)
    {
        string name = Quote(adapterName);
        List<string> commands = new List<string>();
        if (point.DhcpEnabled) commands.Add("interface ipv4 set address name=" + name + " source=dhcp");
        else commands.Add("interface ipv4 set address name=" + name + " source=static address=" + ValidateIpv4(point.IPAddress, "Previous IP address", false) +
            " mask=" + ValidateIpv4(point.SubnetMask, "Previous subnet mask", false) + " gateway=" + (String.IsNullOrEmpty(point.Gateway) ? "none" : ValidateIpv4(point.Gateway, "Previous gateway", false)));
        if (point.DnsAutomatic) commands.Add("interface ipv4 set dnsservers name=" + name + " source=dhcp");
        else
        {
            commands.Add("interface ipv4 set dnsservers name=" + name + " source=static address=" + ValidateIpv4(point.Dns1, "Previous DNS 1", false) + " validate=no");
            if (!String.IsNullOrEmpty(point.Dns2)) commands.Add("interface ipv4 add dnsservers name=" + name + " address=" + ValidateIpv4(point.Dns2, "Previous DNS 2", false) + " index=2 validate=no");
        }
        return commands.ToArray();
    }

    private void RestorePreviousIp()
    {
        ToolkitAdapter adapter = SelectedAdapter();
        IpRestorePoint point = ipRestorePoints.LastOrDefault(p => String.Equals(p.AdapterGuid, adapter.Guid, StringComparison.OrdinalIgnoreCase));
        if (point == null) throw new InvalidOperationException("No previous IP settings are saved for " + adapter.Name + ". Apply Static IP or Set DHCP once to create a restore point.");
        string[] commands = RestoreIpCommands(point, adapter.Name);
        string summary = point.DhcpEnabled ? "IP: Automatic (DHCP)" : "IP: " + point.IPAddress + " / " + point.SubnetMask + "\r\nGateway: " + (point.Gateway.Length == 0 ? "None" : point.Gateway);
        summary += "\r\nDNS: " + (point.DnsAutomatic ? "Automatic" : point.Dns1 + (point.Dns2.Length == 0 ? "" : ", " + point.Dns2));
        if (MessageBox.Show(this, "Restore the saved settings for " + adapter.Name + "?\r\nSaved: " + point.CapturedUtc + " UTC\r\n\r\n" + summary,
            "Confirm IP Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        RequireAdmin();
        string prior = adapter.IP + " / " + adapter.Mask;
        try
        {
            foreach (string command in commands) Netsh(command);
            RefreshAdapters();
            ToolkitAdapter updated = adapters.FirstOrDefault(a => String.Equals(a.Guid, adapter.Guid, StringComparison.OrdinalIgnoreCase));
            bool verified = false;
            if (updated != null)
            {
                IpRestorePoint current = CaptureIpRestorePoint(updated);
                verified = current.DhcpEnabled == point.DhcpEnabled &&
                    (point.DhcpEnabled || (current.IPAddress == point.IPAddress && current.SubnetMask == point.SubnetMask && current.Gateway == point.Gateway)) &&
                    current.DnsAutomatic == point.DnsAutomatic &&
                    (point.DnsAutomatic || (current.Dns1 == point.Dns1 && current.Dns2 == point.Dns2));
            }
            if (!verified) throw new InvalidOperationException("Windows accepted the restore commands, but the adapter settings could not be verified. The saved restore point is still available; click Refresh and Details before trying again.");
            ipRestorePoints.Remove(point);
            SaveSettings();
            RecordIpChange(adapter, "Restore", prior, summary.Replace("\r\n", "; "), "Verified", "Previous settings restored.");
            Log("IP Shifter", "OK", "Restored previous IP settings for " + adapter.Name + ".");
            MessageBox.Show(this, "Previous IP settings restored for " + adapter.Name + ".", "IP Restored", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error) { RecordIpChange(adapter, "Restore", prior, summary.Replace("\r\n", "; "), "Failed", error.Message); throw; }
    }

    private void ApplyStaticIp()
    {
        ToolkitAdapter adapter = SelectedAdapter();
        string ip = ValidateIpv4(ipField.Text, "IP address", false), mask = ValidateIpv4(maskField.Text, "Subnet mask", false);
        string gateway = ValidateIpv4(gatewayField.Text, "Gateway", true), dns1 = ValidateIpv4(dns1Field.Text, "DNS 1", true), dns2 = ValidateIpv4(dns2Field.Text, "DNS 2", true);
        List<ToolkitAdapter> assignments;
        try { assignments = ReadIpAssignments(); }
        catch (Exception error) { throw new InvalidOperationException("Could not check existing IP assignments. No settings were changed. Refresh adapters and try again. Details: " + error.Message); }
        string[] owners = IpConflictOwners(assignments, adapter, ip);
        if (owners.Length > 0)
            throw new InvalidOperationException("IP address " + ip + " is already assigned to: " + String.Join(", ", owners) +
                ". No settings were changed. Choose another IP, or review and remove the existing assignment from that adapter before trying again.");
        bool alreadyAssigned = assignments.Any(a => a.IP == ip && (String.IsNullOrEmpty(adapter.Guid) ? String.Equals(a.Name, adapter.Name, StringComparison.OrdinalIgnoreCase) : String.Equals(a.Guid, adapter.Guid, StringComparison.OrdinalIgnoreCase)));
        RequireAdmin();
        string summary = adapter.Name + "\r\nIP: " + ip + "\r\nMask: " + mask + "\r\nGateway: " + (gateway.Length == 0 ? "None" : gateway) + "\r\nDNS: " + (dns1.Length == 0 ? "Automatic" : dns1 + (dns2.Length == 0 ? "" : ", " + dns2));
        if (MessageBox.Show(this, (alreadyAssigned ? "This IP is already assigned to the selected adapter. Reapply these settings?" : "Apply this configuration?") + "\r\n\r\n" + summary, "Confirm IP Change", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SaveIpRestorePoint(adapter);
        string before = adapter.IP + " / " + adapter.Mask, after = ip + " / " + mask;
        try
        {
            Netsh("interface ipv4 set address name=" + Quote(adapter.Name) + " source=static address=" + ip + " mask=" + mask + " gateway=" + (gateway.Length == 0 ? "none" : gateway));
            if (dns1.Length > 0) { Netsh("interface ipv4 set dnsservers name=" + Quote(adapter.Name) + " source=static address=" + dns1 + " validate=no"); if (dns2.Length > 0) Netsh("interface ipv4 add dnsservers name=" + Quote(adapter.Name) + " address=" + dns2 + " index=2 validate=no"); }
            else Netsh("interface ipv4 set dnsservers name=" + Quote(adapter.Name) + " source=dhcp");
            RefreshAdapters();
            ToolkitAdapter updated = adapters.FirstOrDefault(a => a.Name == adapter.Name);
            bool verified = updated != null && updated.IP == ip && updated.Mask == mask;
            RecordIpChange(adapter, "Static IP", before, after, verified ? "Verified" : "Unverified", "Gateway: " + gateway + "; DNS: " + (dns1.Length == 0 ? "Automatic" : dns1 + ", " + dns2));
            Log("IP Shifter", verified ? "OK" : "WARN", "Requested " + ip + " / " + mask + "; Windows reports " + (updated == null ? "adapter unavailable" : updated.IP + " / " + updated.Mask));
            MessageBox.Show(this, verified ? "IP address assigned successfully.\r\n\r\nAdapter: " + adapter.Name + "\r\nIP: " + ip + "\r\nSubnet mask: " + mask :
                "Windows accepted the commands, but the requested IP address and subnet mask could not be verified.\r\n\r\nClick Refresh and Details to check the adapter before continuing.",
                verified ? "IP Assigned Successfully" : "Verify IP Settings", MessageBoxButtons.OK, verified ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) { RecordIpChange(adapter, "Static IP", before, after, "Failed", error.Message); throw; }
    }

    private void ApplyDhcp()
    {
        ToolkitAdapter adapter = SelectedAdapter();
        RequireAdmin();
        if (MessageBox.Show(this, "Switch " + adapter.Name + " to automatic IP and DNS?", "Confirm DHCP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SaveIpRestorePoint(adapter);
        string before = adapter.IP + " / " + adapter.Mask;
        try
        {
            Netsh("interface ipv4 set address name=" + Quote(adapter.Name) + " source=dhcp");
            Netsh("interface ipv4 set dnsservers name=" + Quote(adapter.Name) + " source=dhcp");
            RefreshAdapters();
            ToolkitAdapter updated = adapters.FirstOrDefault(a => String.Equals(a.Guid, adapter.Guid, StringComparison.OrdinalIgnoreCase));
            bool verified = updated != null && CaptureIpRestorePoint(updated).DhcpEnabled;
            RecordIpChange(adapter, "DHCP", before, "Automatic IP and DNS", verified ? "Verified" : "Unverified", "DHCP requested.");
            Log("IP Shifter", verified ? "OK" : "WARN", "Requested DHCP on " + adapter.Name);
            MessageBox.Show(this, verified ? "Automatic IP and DNS enabled successfully for " + adapter.Name + "." : "Windows accepted the DHCP commands, but DHCP could not be verified. Click Refresh and Details to check the adapter.",
                verified ? "DHCP Enabled" : "Verify DHCP", MessageBoxButtons.OK, verified ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) { RecordIpChange(adapter, "DHCP", before, "Automatic IP and DNS", "Failed", error.Message); throw; }
    }

    private void BuildNetworkPage(TabPage page)
    {
        GroupBox device = new GroupBox { Text = "Device Checks", Left = 18, Top = 18, Width = 340, Height = 230 }; page.Controls.Add(device);
        device.Controls.Add(L("Device host / IP", 14, 28, 160)); networkTarget = T(14, 52, 300, "192.168.1.10"); device.Controls.Add(networkTarget);
        device.Controls.Add(L("Gateway adapter", 14, 82, 105));
        networkGatewayChoice = new ComboBox { Left = 120, Top = 79, Width = 194, DropDownWidth = 310, DropDownStyle = ComboBoxStyle.DropDownList }; device.Controls.Add(networkGatewayChoice);
        B(device, "Ping Device", 14, 106, 142, delegate { PingTerminal(networkTarget.Text, false); }, "Four live pings to the device.", Cobalt);
        B(device, "Ping Gateway", 172, 106, 142, PingSelectedGateway, "Ping the gateway shown for the selected network adapter.", Green);
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

        page.Controls.Add(L("Support bundle", 376, 530, 185));
        B(page, "Export Summary", 574, 518, 130, ExportTroubleshootingSummary, "Save adapter, gateway, DNS, recent IP changes, and recent network checks as a readable text report.", Cobalt);
        bundleType = new ComboBox { Left = 376, Top = 560, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList }; bundleType.Items.AddRange(new object[] { "Network", "Windows", "Both" }); bundleType.SelectedIndex = 0; page.Controls.Add(bundleType);
        B(page, "Export ZIP", 574, 556, 130, ExportBundle, "Export selected logs and diagnostic snapshots as a ZIP for review before sharing.", Green);
    }

    private void RefreshGatewayAdapters()
    {
        if (networkGatewayChoice == null) return;
        string selectedGuid = networkGatewayChoice.SelectedIndex >= 0 && networkGatewayChoice.SelectedIndex < gatewayAdapters.Count ?
            gatewayAdapters[networkGatewayChoice.SelectedIndex].Guid : "";
        gatewayAdapters.Clear(); networkGatewayChoice.Items.Clear();
        foreach (ToolkitAdapter adapter in adapters.Where(a => a.Status == "Connected" && IsUsableIpv4(a.Gateway)))
        {
            gatewayAdapters.Add(adapter);
            networkGatewayChoice.Items.Add(adapter.Gateway + "  |  " + adapter.Name);
        }
        if (gatewayAdapters.Count > 0)
        {
            int previous = gatewayAdapters.FindIndex(a => a.Guid == selectedGuid && selectedGuid.Length > 0);
            int shifter = adapterChoice.SelectedIndex >= 0 && adapterChoice.SelectedIndex < adapters.Count ?
                gatewayAdapters.FindIndex(a => a.Guid == adapters[adapterChoice.SelectedIndex].Guid) : -1;
            networkGatewayChoice.SelectedIndex = previous >= 0 ? previous : shifter >= 0 ? shifter : 0;
        }
    }

    private void PingSelectedGateway()
    {
        if (networkGatewayChoice == null || networkGatewayChoice.SelectedIndex < 0 || networkGatewayChoice.SelectedIndex >= gatewayAdapters.Count)
            throw new InvalidOperationException("No connected adapter with an IPv4 gateway was found. Connect to a network with a gateway and click Refresh in IP Shifter.");
        ToolkitAdapter adapter = gatewayAdapters[networkGatewayChoice.SelectedIndex];
        if (!IsUsableIpv4(adapter.Gateway)) throw new InvalidOperationException("The selected adapter does not have a valid IPv4 gateway. Refresh the adapters and try again.");
        PingTerminal(adapter.Gateway, false);
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
        page.Controls.Add(new Label { Text = "BMS Tools", Left = 24, Top = 24, Width = 400, Height = 38, Font = new Font("Segoe UI", 16f, FontStyle.Bold) });
        B(page, "Launch YABE", 24, 82, 160, LaunchYabe, "Open installed YABE. Select Yabe.exe once if it is installed elsewhere.", Green);
        page.Controls.Add(L("More BMS tools — work in progress", 24, 142, 500));
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

    private string[] RecentNetworkChecks()
    {
        string[] areas = { "Ping", "DNS", "Telnet", "Switch", "Trace", "PathPing" };
        return logView.Items.Cast<ListViewItem>()
            .Where(row => row.SubItems.Count >= 4 && areas.Contains(row.SubItems[2].Text))
            .Reverse().Take(15).Reverse()
            .Select(row => row.SubItems[0].Text + " | " + row.SubItems[1].Text + " | " + row.SubItems[2].Text + " | " +
                row.SubItems[3].Text.Replace('\r', ' ').Replace('\n', ' ')).ToArray();
    }

    private static string BuildTroubleshootingSummary(IEnumerable<ToolkitAdapter> currentAdapters,
        IEnumerable<IpChangeRecord> recentChanges, IEnumerable<string> recentChecks, string computer, DateTime createdUtc)
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("TEC Systems Field Toolkit - Troubleshooting Summary");
        report.AppendLine("Created (UTC): " + createdUtc.ToString("u"));
        report.AppendLine("Computer: " + computer);
        report.AppendLine(); report.AppendLine("Current network adapters:");
        ToolkitAdapter[] snapshot = currentAdapters.ToArray();
        if (snapshot.Length == 0) report.AppendLine("- No adapters available. Refresh in IP Shifter and check the technician log.");
        foreach (ToolkitAdapter adapter in snapshot)
            report.AppendLine("- " + adapter.Name + " | " + adapter.Status + " | IP " + adapter.IP + " / " + adapter.Mask +
                " | Gateway " + adapter.Gateway + " | DNS " + adapter.Dns + " | MAC " + adapter.Mac);
        report.AppendLine(); report.AppendLine("Recent IP Shifter changes:");
        IpChangeRecord[] changes = recentChanges.Reverse().Take(10).Reverse().ToArray();
        if (changes.Length == 0) report.AppendLine("- None recorded.");
        foreach (IpChangeRecord change in changes)
            report.AppendLine("- " + change.TimeUtc + " | " + change.AdapterName + " | " + change.Action + " | " +
                change.Result + " | " + change.Before + " -> " + change.After);
        report.AppendLine(); report.AppendLine("Recent network checks in this toolkit session:");
        string[] checks = recentChecks.ToArray();
        if (checks.Length == 0) report.AppendLine("- None recorded.");
        foreach (string check in checks) report.AppendLine("- " + check);
        report.AppendLine(); report.AppendLine("Ping opens in a separate Command Prompt; its result is not captured here.");
        report.AppendLine("Review machine and network details before sharing this report.");
        return report.ToString();
    }

    private void ExportTroubleshootingSummary()
    {
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Text reports (*.txt)|*.txt",
            FileName = "TEC-Troubleshooting-Summary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            RefreshAdapters();
            string report = BuildTroubleshootingSummary(adapters, ipHistory, RecentNetworkChecks(), Environment.MachineName, DateTime.UtcNow);
            File.WriteAllText(dialog.FileName, report, Encoding.UTF8);
            Log("Support", "OK", "Exported troubleshooting summary. Review before sharing: " + dialog.FileName);
        }
    }

    private void ExportBundle()
    {
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "ZIP files (*.zip)|*.zip", FileName = "TEC_SupportBundle_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip" })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            RefreshAdapters();
            using (FileStream file = File.Create(dialog.FileName))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                AddZipText(archive, "troubleshooting-summary.txt", BuildTroubleshootingSummary(adapters, ipHistory,
                    RecentNetworkChecks(), Environment.MachineName, DateTime.UtcNow));
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
        scanAdapter = new ComboBox { Left = 18, Top = 38, Width = 558, DropDownStyle = ComboBoxStyle.DropDownList }; scanAdapter.SelectedIndexChanged += delegate { SelectScanAdapter(); }; page.Controls.Add(scanAdapter);
        B(page, "Refresh", 592, 35, 112, RefreshAdapters, "Read the current IP, mask, and gateway from Windows again.", Cobalt);
        scanScope = L("Select an adapter to fill the local network range.", 18, 68, 690); page.Controls.Add(scanScope);
        page.Controls.Add(L("Start IP", 18, 98, 120)); scanStart = T(18, 122, 156, ""); page.Controls.Add(scanStart);
        page.Controls.Add(L("End IP", 190, 98, 120)); scanEnd = T(190, 122, 156, ""); page.Controls.Add(scanEnd);
        scanButton = B(page, "Scan", 362, 120, 88, StartScan, "Scan up to 1,024 IPs with ping and common TCP probes.", Cobalt);
        B(page, "Stop", 458, 120, 74, delegate { stopScan = true; }, "Stop the current scan.", Slate);
        B(page, "Export CSV", 540, 120, 110, ExportScan, "Save discovered IPs, hostnames, ping times, MAC owners, and ports.", Green);
        scanStatus = L("Ready | Ping and common TCP services", 18, 164, 660); page.Controls.Add(scanStatus);
        scanView = new ListView { Left = 18, Top = 194, Width = 690, Height = 326, View = View.Details, FullRowSelect = true, GridLines = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        scanView.Columns.Add("IP address", 110); scanView.Columns.Add("Hostname", 145); scanView.Columns.Add("Ping", 58); scanView.Columns.Add("MAC address", 125); scanView.Columns.Add("Manufacturer", 155); scanView.Columns.Add("Open TCP ports", 95);
        scanView.SizeChanged += delegate { UpdateScanColumns(); };
        scanView.ColumnClick += delegate(object sender, ColumnClickEventArgs args) { SortScanResults(args.Column); };
        page.Controls.Add(scanView);
        tips.SetToolTip(scanView, "Manufacturer is the registered MAC-prefix owner, which may differ from the device brand. Some devices do not publish a hostname or expose a MAC to this laptop.");
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Ping", null, delegate { if (scanView.SelectedItems.Count > 0) PingTerminal(scanView.SelectedItems[0].Text, true); });
        menu.Items.Add("Remote Desktop", null, delegate { if (scanView.SelectedItems.Count > 0) OpenRdp(scanView.SelectedItems[0].Text); });
        menu.Items.Add("Open HTTP", null, delegate { if (scanView.SelectedItems.Count > 0) OpenWeb("http://" + scanView.SelectedItems[0].Text); });
        menu.Items.Add("Open HTTPS", null, delegate { if (scanView.SelectedItems.Count > 0) OpenWeb("https://" + scanView.SelectedItems[0].Text); });
        scanView.ContextMenuStrip = menu;
    }

    private void UpdateScanColumns()
    {
        if (scanView == null) return;
        int extra = Math.Max(0, scanView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 688);
        scanView.Columns[1].Width = 145 + extra / 2;
        scanView.Columns[4].Width = 155 + extra - extra / 2;
    }

    private static uint IpNumber(string value) { byte[] bytes = IPAddress.Parse(ValidateIpv4(value, "Scan IP", false)).GetAddressBytes(); return (uint)((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]); }
    private static string IpText(uint number) { return String.Format("{0}.{1}.{2}.{3}", number >> 24, (number >> 16) & 255, (number >> 8) & 255, number & 255); }

    private void RefreshScannerAdapters()
    {
        if (scanAdapter == null) return;
        scanAdapter.Items.Clear();
        foreach (ToolkitAdapter a in adapters) scanAdapter.Items.Add(a.Name + " [" + a.Status + "] - " + a.IP);
        if (adapters.Count == 0)
        {
            scanStart.Clear(); scanEnd.Clear(); scanScope.Text = "No network adapters found. Click Refresh in IP Shifter.";
            return;
        }
        int previous = adapters.FindIndex(a => scanSelectedAdapterGuid.Length > 0 && a.Guid == scanSelectedAdapterGuid);
        if (previous < 0) previous = adapters.FindIndex(a => a.Name == scanSelectedAdapterName && scanSelectedAdapterName.Length > 0);
        int preferred = adapters.FindIndex(a => a.Status == "Connected" && IsUsableIpv4(a.IP) && IsUsableIpv4(a.Mask));
        scanAdapter.SelectedIndex = previous >= 0 ? previous : preferred >= 0 ? preferred : 0;
    }

    private void SelectScanAdapter()
    {
        if (scanAdapter.SelectedIndex < 0 || scanAdapter.SelectedIndex >= adapters.Count) return;
        ToolkitAdapter a = adapters[scanAdapter.SelectedIndex];
        scanSelectedAdapterGuid = a.Guid; scanSelectedAdapterName = a.Name;
        scanStart.Clear(); scanEnd.Clear();
        if (a.Status != "Connected" || !IsUsableIpv4(a.IP) || !IsUsableIpv4(a.Mask))
        {
            scanScope.Text = a.Name + " has no usable connected IPv4 address and mask. Select a connected adapter or click Refresh in IP Shifter.";
            return;
        }
        try
        {
            uint ip = IpNumber(a.IP), mask = IpNumber(a.Mask), network = ip & mask, broadcast = network | ~mask;
            uint first = network + 1, last = broadcast - 1;
            if (last < first) { first = ip; last = ip; }
            if ((ulong)last - first + 1 > 254)
            {
                uint slice = ip > 126 ? ip - 126 : 0;
                first = Math.Min(Math.Max(first, slice), last - 253);
                last = first + 253;
                scanScope.Text = a.Name + ": large subnet; showing 254 addresses near this laptop. Edit the range for another slice.";
            }
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
        try { IAsyncResult task = Dns.BeginGetHostEntry(ip, null, null); if (task.AsyncWaitHandle.WaitOne(1200)) host.Hostname = Dns.EndGetHostEntry(task).HostName; } catch { }
        if (host.Hostname == ip || host.Hostname.Length == 0) host.Hostname = QueryNetBiosName(ip);
        try {
            string arp = CaptureCommand("arp.exe", "-a " + ip);
            Match match = Regex.Match(arp, @"(?m)^\s*" + Regex.Escape(ip) + @"\s+([0-9a-fA-F-]{17})\s+");
            if (match.Success) { host.Mac = match.Groups[1].Value; host.Manufacturer = LookupManufacturer(host.Mac); }
        }
        catch { }
        return host;
    }

    private static string QueryNetBiosName(string ip)
    {
        byte[] query = new byte[50];
        query[0] = 0x54; query[1] = 0x45; query[5] = 1;
        query[12] = 32; query[13] = (byte)'C'; query[14] = (byte)'K';
        for (int i = 15; i <= 44; i++) query[i] = (byte)'A';
        query[47] = 0x21; query[49] = 1;
        try
        {
            using (UdpClient client = new UdpClient())
            {
                client.Client.ReceiveTimeout = 700;
                client.Connect(ip, 137);
                client.Send(query, query.Length);
                IPEndPoint endpoint = null;
                return ParseNodeStatusName(client.Receive(ref endpoint), 0x5445);
            }
        }
        catch (SocketException) { return ""; }
        catch (ObjectDisposedException) { return ""; }
    }

    private static int SkipDnsName(byte[] response, int offset)
    {
        while (offset < response.Length)
        {
            int length = response[offset];
            if (length == 0) return offset + 1;
            if ((length & 0xC0) == 0xC0) return offset + 2 <= response.Length ? offset + 2 : -1;
            if (length > 63 || offset + 1 + length > response.Length) return -1;
            offset += 1 + length;
        }
        return -1;
    }

    private static int Word(byte[] bytes, int offset) { return (bytes[offset] << 8) | bytes[offset + 1]; }

    private static string ParseNodeStatusName(byte[] response, int requestId)
    {
        if (response.Length < 12 || Word(response, 0) != requestId || (response[2] & 0x80) == 0 || (response[3] & 0x0F) != 0) return "";
        int offset = 12;
        for (int i = 0; i < Word(response, 4); i++)
        {
            offset = SkipDnsName(response, offset);
            if (offset < 0 || offset + 4 > response.Length) return "";
            offset += 4;
        }
        string serverName = "";
        for (int i = 0; i < Word(response, 6); i++)
        {
            offset = SkipDnsName(response, offset);
            if (offset < 0 || offset + 10 > response.Length) return "";
            int type = Word(response, offset), size = Word(response, offset + 8), data = offset + 10;
            if (data + size > response.Length) return "";
            if (type == 0x21 && size > 0)
            {
                int count = response[data];
                if (1 + count * 18 > size) return "";
                for (int nameIndex = 0; nameIndex < count; nameIndex++)
                {
                    int name = data + 1 + nameIndex * 18;
                    if ((Word(response, name + 16) & 0x8000) != 0) continue;
                    string value = Encoding.ASCII.GetString(response, name, 15).TrimEnd(' ', '\0');
                    if (value.Length == 0 || value == "*") continue;
                    if (response[name + 15] == 0) return value;
                    if (response[name + 15] == 0x20 && serverName.Length == 0) serverName = value;
                }
            }
            offset = data + size;
        }
        return serverName;
    }

    private static Dictionary<string, string> LoadMacVendors()
    {
        Dictionary<string, string> vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (Stream stream = typeof(ToolkitWindow).Assembly.GetManifestResourceStream("MacVendors"))
        {
            if (stream == null) throw new InvalidDataException("The bundled manufacturer registry is missing.");
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int separator = line.IndexOf('\t');
                    if (separator > 0 && separator + 1 < line.Length) vendors[line.Substring(0, separator)] = line.Substring(separator + 1);
                }
            }
        }
        return vendors;
    }

    private static string LookupManufacturer(string mac)
    {
        string digits = Regex.Replace(mac, "[^0-9A-Fa-f]", "").ToUpperInvariant();
        if (digits.Length != 12) return "";
        int firstByte = Convert.ToInt32(digits.Substring(0, 2), 16);
        if ((firstByte & 1) != 0) return "Multicast";
        if ((firstByte & 2) != 0) return "Locally assigned";
        string vendor;
        foreach (int length in new[] { 9, 7, 6 })
            if (MacVendors.TryGetValue(digits.Substring(0, length), out vendor)) return vendor;
        return "Not listed";
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

    private void SortScanResults(int column)
    {
        if (column < 0 || column >= scanView.Columns.Count) return;
        scanSortDescending = scanSortColumn == column && !scanSortDescending;
        scanSortColumn = column;
        foreach (ColumnHeader header in scanView.Columns)
            header.Text = header.Text.TrimEnd(' ', '▲', '▼');
        scanView.Columns[column].Text += scanSortDescending ? " ▼" : " ▲";
        ApplyScanSort();
    }

    private void ApplyScanSort()
    {
        if (scanSortColumn < 0 || scanView.Items.Count < 2) return;
        List<ListViewItem> rows = scanView.Items.Cast<ListViewItem>().ToList();
        ScanResultsComparer comparer = new ScanResultsComparer(scanSortColumn, scanSortDescending);
        rows.Sort((left, right) => comparer.Compare(left, right));
        scanView.BeginUpdate();
        try { scanView.Items.Clear(); scanView.Items.AddRange(rows.ToArray()); }
        finally { scanView.EndUpdate(); }
    }

    private void AddScanHost(ScanHost host)
    {
        if (IsDisposed) return;
        ListViewItem row = new ListViewItem(host.IP);
        row.SubItems.Add(host.Hostname); row.SubItems.Add(host.Ping); row.SubItems.Add(host.Mac); row.SubItems.Add(host.Manufacturer); row.SubItems.Add(host.Ports);
        scanView.Items.Add(row);
        ApplyScanSort();
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
                writer.WriteLine("IPAddress,Hostname,Ping,MacAddress,Manufacturer,OpenPorts");
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
        if (Interlocked.CompareExchange(ref checkingInternet, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate {
            try
            {
                bool online = InternetOnline();
                if (!IsDisposed && IsHandleCreated)
                    try { BeginInvoke(new Action(delegate {
                        if (IsDisposed) return;
                        internetLabel.Text = online ? "Internet: Online" : "Internet: Offline";
                        internetLabel.ForeColor = online ? (dark ? Color.FromArgb(93, 214, 168) : Green) : (dark ? Color.FromArgb(255, 120, 120) : Color.Firebrick);
                        if (tray != null) tray.Text = Product + " - Internet: " + (online ? "Online" : "Offline");
                    })); }
                    catch (InvalidOperationException) { }
            }
            finally { Interlocked.Exchange(ref checkingInternet, 0); }
        });
    }

    private void OpenUpdateOffer()
    {
        if (availableRelease != null) OfferUpdate(availableRelease, availableVersion);
        else CheckUpdates(false);
    }

    private void AnnounceUpdate(Dictionary<string, object> release, string latest, bool silent)
    {
        bool newlyAvailable = availableVersion != latest;
        availableRelease = release; availableVersion = latest;
        updateButton.Text = "Update Available";
        updateButton.Tag = Color.FromArgb(177, 93, 39);
        updateButton.BackColor = Color.FromArgb(177, 93, 39);
        tips.SetToolTip(updateButton, "Version " + latest + " is available. Click to install.");
        if (newlyAvailable && !testing) tray.ShowBalloonTip(10000, "Toolkit update available", "Version " + latest + " is ready. Open the toolkit and click Update Available.", ToolTipIcon.Info);
        if (!silent) OfferUpdate(release, latest);
    }

    private void CheckUpdates(bool silent)
    {
        if (Interlocked.CompareExchange(ref checkingUpdates, 1, 0) != 0) return;
        if (availableRelease == null) updateButton.Text = "Checking...";
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
                    BeginInvoke(new Action(delegate { AnnounceUpdate(release, latest.ToString(), silent); }));
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
            finally
            {
                Interlocked.Exchange(ref checkingUpdates, 0);
                if (!IsDisposed && IsHandleCreated)
                    try { BeginInvoke(new Action(delegate { if (availableRelease == null) updateButton.Text = "Check Updates"; })); }
                    catch (InvalidOperationException) { }
            }
        });
    }

    private void OfferUpdate(Dictionary<string, object> release, string latest)
    {
        if (IsDisposed) return;
        if (MessageBox.Show(this, "Version " + latest + " is available (installed: " + version + ").\r\n\r\nUpdate now? The toolkit will exit, update in place, and reopen. Your saved profiles and logs will stay in place.", "Toolkit Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
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

    public int StartupSelfTest()
    {
        testing = true;
        Show(); Application.DoEvents();
        if (Visible || !tray.Visible) throw new InvalidOperationException("Windows sign-in startup did not remain in the tray.");
        RestoreFromTray(); Application.DoEvents();
        if (!Visible || !ShowInTaskbar) throw new InvalidOperationException("Startup instance did not restore to the taskbar.");
        ExitToolkit();
        return 0;
    }

    public void SavePreview(string path)
    {
        testing = true;
        dark = true;
        Text = Product + " (Preview)";
        ApplyTheme();
        Show(); Application.DoEvents();
        Size = new Size(1280, 780);
        Application.DoEvents();
        using (Bitmap image = new Bitmap(Width, Height))
        {
            DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        ExitToolkit();
    }

    public int SelfTest()
    {
        try
        {
            testing = true;
            RdpManager.SelfTest();
            ActivitySnapshot.SelfTest();
            if (!IsBluetoothAdapter("Renamed adapter", "Bluetooth Device (Personal Area Network)", "", "") ||
                !IsBluetoothAdapter("Renamed adapter", "", "BTH\\MS_BTHPAN", "") ||
                IsBluetoothAdapter("Ethernet 3", "USB Ethernet", "USB\\123", ""))
                throw new InvalidOperationException("Bluetooth adapter filtering failed.");
            ToolkitAdapter ipSelected = new ToolkitAdapter { Name = "Ethernet 3", Guid = "ethernet" };
            ToolkitAdapter[] assignments = { new ToolkitAdapter { Name = "Bluetooth Network Connection", Guid = "bluetooth", IP = "10.211.113.251" },
                new ToolkitAdapter { Name = "Ethernet 3", Guid = "ethernet", IP = "192.168.2.251" } };
            if (IpConflictOwners(assignments, ipSelected, "10.211.113.251").Single() != "Bluetooth Network Connection" ||
                IpConflictOwners(assignments, ipSelected, "192.168.2.251").Length != 0 ||
                IpConflictOwners(assignments, ipSelected, "10.211.113.252").Length != 0)
                throw new InvalidOperationException("Existing IP assignment detection failed.");
            IpRestorePoint restoreSample = new IpRestorePoint { AdapterGuid = "sample", AdapterName = "Ethernet 3", IPAddress = "192.0.2.10",
                SubnetMask = "255.255.255.0", Gateway = "192.0.2.1", DnsAutomatic = false, Dns1 = "1.1.1.1", Dns2 = "8.8.8.8" };
            string[] restoreCommands = RestoreIpCommands(restoreSample, "Ethernet 3");
            if (restoreCommands.Length != 3 || !restoreCommands[0].Contains("address=192.0.2.10") ||
                !restoreCommands[1].Contains("address=1.1.1.1") || !restoreCommands[2].Contains("address=8.8.8.8"))
                throw new InvalidOperationException("Static IP restore command planning failed.");
            restoreSample.DhcpEnabled = true;
            restoreSample.DnsAutomatic = true;
            restoreCommands = RestoreIpCommands(restoreSample, "Ethernet 3");
            if (restoreCommands.Length != 2 || !restoreCommands[0].Contains("source=dhcp") || !restoreCommands[1].Contains("source=dhcp"))
                throw new InvalidOperationException("DHCP restore command planning failed.");
            string restoreJson = json.Serialize(new Dictionary<string, object> { { "AdapterGuid", restoreSample.AdapterGuid },
                { "DhcpEnabled", restoreSample.DhcpEnabled }, { "DnsAutomatic", restoreSample.DnsAutomatic } });
            IDictionary<string, object> restoredRow = json.Deserialize<Dictionary<string, object>>(restoreJson);
            if (Value(restoredRow, "AdapterGuid") != "sample" || !Boolean.Parse(Value(restoredRow, "DhcpEnabled")) ||
                !Boolean.Parse(Value(restoredRow, "DnsAutomatic")))
                throw new InvalidOperationException("IP restore point serialization failed.");
            TabPage ipPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "IP Shifter");
            if (!ipPage.Controls.OfType<Button>().Any(c => c.Text == "Restore Previous IP"))
                throw new InvalidOperationException("IP restore action is missing.");
            string checksumSample = new string('a', 64) + "  TEC-Systems-FieldToolkit-Setup.exe";
            if (ParseReleaseChecksum(checksumSample + "\n") != new string('a', 64) ||
                ParseReleaseChecksum(checksumSample + "\r\n") != new string('a', 64))
                throw new InvalidOperationException("Release checksum line ending handling failed.");
            if (tabs.TabPages.Count != 8 || !tabs.TabPages.Cast<TabPage>().Any(p => p.Text == "Packet Analyzer"))
                throw new InvalidOperationException("Expected the packet analyzer tab.");
            CaptureSummary.SelfTest();
            if (!headerPanel.Controls.Cast<Control>().Any(c => c.Text == "Version " + version))
                throw new InvalidOperationException("Current toolkit version is not visible in the header.");
            if (tabs.TabPages[1].Text != "IP Shifter" || tabs.TabPages[2].Text != "IP Scanner" || tabs.TabPages[3].Text != "RDP" ||
                tabs.TabPages.Cast<TabPage>().Any(p => p.Text == "Sites"))
                throw new InvalidOperationException("IP Scanner/RDP tab order or hidden Sites tab is incorrect.");
            SelfTestSiteWorkspace();
            TabPage networkPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "Network Troubleshooting");
            if (!networkPage.Controls.OfType<Button>().Any(c => c.Text == "Export Summary"))
                throw new InvalidOperationException("Troubleshooting summary export is missing.");
            string summarySample = BuildTroubleshootingSummary(
                new[] { new ToolkitAdapter { Name = "Test Ethernet", Status = "Connected", IP = "192.0.2.5", Mask = "255.255.255.0", Gateway = "192.0.2.1", Dns = "1.1.1.1" } },
                new[] { new IpChangeRecord { AdapterName = "Test Ethernet", Action = "Restore", Result = "Verified" } },
                new[] { "12:00:00 | OK | DNS | example.test -> 192.0.2.8" }, "TEST-PC", DateTime.UtcNow);
            if (!summarySample.Contains("Gateway 192.0.2.1") || !summarySample.Contains("DNS 1.1.1.1") ||
                !summarySample.Contains("Restore | Verified") || !summarySample.Contains("example.test -> 192.0.2.8") ||
                !summarySample.Contains("Ping opens in a separate Command Prompt"))
                throw new InvalidOperationException("Troubleshooting summary omitted adapter settings or recent checks.");
            TabPage windowsPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "Windows Troubleshooting");
            if (windowsPage.Controls.Cast<Control>().Any(c => c.Text == "Open Webpage" || c.Text == "Web URL" ||
                c.Text == "Saved RDP sites" || c.Text == "Save Site" || c.Text == "Delete Site" || c.Text == "Site name"))
                throw new InvalidOperationException("Windows Troubleshooting still contains removed RDP site controls.");
            FlowLayoutPanel windowsTools = windowsPage.Controls.Cast<Control>().OfType<FlowLayoutPanel>().First();
            if (!windowsTools.Controls.Cast<Control>().Any(c => c.Text == "IPConfig /all") ||
                !windowsTools.Controls.Cast<Control>().Any(c => c.Text == "Display Settings"))
                throw new InvalidOperationException("Windows tool shortcuts are missing.");
            if (windowsTools.AutoScroll || windowsTools.Controls.Count != 18 ||
                windowsTools.Controls.Cast<Control>().Any(c => !windowsTools.ClientRectangle.Contains(c.Bounds)))
                throw new InvalidOperationException("Open tools shortcuts are clipped or require internal scrolling.");
            split.SplitterDistance = 650; Application.DoEvents();
            if (windowsTools.Controls.Cast<Control>().Any(c => !windowsTools.ClientRectangle.Contains(c.Bounds)))
                throw new InvalidOperationException("Open tools shortcuts are clipped at narrow width.");
            SetInitialSplit(); Application.DoEvents();
            string rdpSample = json.Serialize(new[] { new Dictionary<string, object> { { "Name", "Test site" }, { "Host", "192.0.2.10" } } });
            object[] rdpRoundTrip = json.Deserialize<object[]>(rdpSample);
            IDictionary<string, object> rdpLoaded = rdpRoundTrip[0] as IDictionary<string, object>;
            if (rdpLoaded == null || Value(rdpLoaded, "Name") != "Test site" || Value(rdpLoaded, "Host") != "192.0.2.10")
                throw new InvalidOperationException("RDP site serialization failed.");
            if (windowsPage.Controls.Cast<Control>().Any(c => c.Text == "ARP Cache") ||
                !networkPage.Controls.Cast<Control>().OfType<GroupBox>().First(c => c.Text == "Quick Capture")
                    .Controls.Cast<Control>().Any(c => c.Text == "ARP Cache"))
                throw new InvalidOperationException("ARP Cache belongs only in Network Troubleshooting.");
            if (networkPage.Controls.Cast<Control>().Any(c => c.Text == "VLAN clues" || c.Text == "Inspect Selected Adapter")) throw new InvalidOperationException("VLAN clues controls remain.");
            GroupBox deviceAccess = networkPage.Controls.Cast<Control>().OfType<GroupBox>().First(c => c.Text == "Device Access");
            if (!deviceAccess.Controls.Cast<Control>().Any(c => c.Text == "Test Telnet") ||
                !deviceAccess.Controls.Cast<Control>().Any(c => c.Text == "Open Telnet"))
                throw new InvalidOperationException("Telnet controls are missing.");
            TabPage bmsPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "BMS Tools");
            if (bmsPage.Controls.OfType<Button>().Count() != 1 || !bmsPage.Controls.Cast<Control>().Any(c => c.Text == "Launch YABE") ||
                !bmsPage.Controls.Cast<Control>().Any(c => c.Text == "More BMS tools — work in progress"))
                throw new InvalidOperationException("BMS page should contain only YABE and the work-in-progress note.");
            string sample = json.Serialize(new[] { new Dictionary<string, object> { { "Name", "Test site" }, { "Host", "ebi.example.test" }, { "User", "DOMAIN\\tech" } } });
            object[] roundTrip = json.Deserialize<object[]>(sample);
            IDictionary<string, object> loaded = roundTrip[0] as IDictionary<string, object>;
            if (loaded == null || Value(loaded, "Host") != "ebi.example.test" || Value(loaded, "User") != "DOMAIN\\tech")
                throw new InvalidOperationException("BMS profile serialization failed.");
            TabPage feedbackPage = tabs.TabPages.Cast<TabPage>().First(p => p.Text == "Feedback");
            if (!feedbackPage.Controls.Cast<Control>().Any(c => c.Text == Mailbox)) throw new InvalidOperationException("Feedback email address is missing.");
            if (feedbackPage.Controls.Cast<Control>().Any(c => c.Text.IndexOf("Screenshot", StringComparison.OrdinalIgnoreCase) >= 0)) throw new InvalidOperationException("Old screenshot controls remain.");
            if (IpText(IpNumber("192.168.10.42")) != "192.168.10.42") throw new InvalidOperationException("IPv4 conversion failed.");
            if (MacVendors.Count < 50000 || !LookupManufacturer("00-00-0C-00-00-01").StartsWith("Cisco", StringComparison.OrdinalIgnoreCase) ||
                LookupManufacturer("02-00-00-00-00-01") != "Locally assigned")
                throw new InvalidOperationException("Offline manufacturer lookup failed.");
            TabPage scannerPage = tabs.TabPages.Cast<TabPage>().First(page => page.Text == "IP Scanner");
            if (scanView.Columns.Count != 6 || scanView.Columns[4].Text != "Manufacturer")
                throw new InvalidOperationException("IP Scanner manufacturer column is missing.");
            List<ToolkitAdapter> originalAdapters = adapters.ToList();
            string originalScanGuid = scanSelectedAdapterGuid, originalScanName = scanSelectedAdapterName;
            try
            {
                adapters.Clear();
                adapters.Add(new ToolkitAdapter { Name = "Ethernet A", Guid = "adapter-a", Status = "Connected", IP = "192.168.10.20", Mask = "255.255.255.0", Gateway = "192.168.10.1" });
                adapters.Add(new ToolkitAdapter { Name = "Ethernet B", Guid = "adapter-b", Status = "Connected", IP = "10.20.30.40", Mask = "255.255.255.0", Gateway = "10.20.30.1" });
                adapters.Add(new ToolkitAdapter { Name = "Offline", Guid = "adapter-c", Status = "Disconnected", IP = "No IP", Mask = "No IP", Gateway = "No IP" });
                RefreshScannerAdapters();
                scanAdapter.SelectedIndex = 1;
                if (scanStart.Text != "10.20.30.1" || scanEnd.Text != "10.20.30.254")
                    throw new InvalidOperationException("Changing scanner adapters did not update the scan range.");
                RefreshScannerAdapters();
                if (scanAdapter.SelectedIndex != 1 || scanStart.Text != "10.20.30.1")
                    throw new InvalidOperationException("Refreshing adapters reset the scanner selection.");
                scanAdapter.SelectedIndex = 2;
                if (scanStart.Text.Length != 0 || scanEnd.Text.Length != 0)
                    throw new InvalidOperationException("An unavailable scanner adapter retained the previous range.");
                RefreshGatewayAdapters();
                if (gatewayAdapters.Count != 2 || networkGatewayChoice.Items.Count != 2 ||
                    !Convert.ToString(networkGatewayChoice.Items[0]).Contains("192.168.10.1"))
                    throw new InvalidOperationException("Gateway selection did not show connected adapters with valid gateways.");
            }
            finally
            {
                adapters.Clear(); adapters.AddRange(originalAdapters);
                scanSelectedAdapterGuid = originalScanGuid; scanSelectedAdapterName = originalScanName;
                RefreshScannerAdapters(); RefreshGatewayAdapters();
            }
            AddScanHost(new ScanHost { IP = "192.0.2.1", Hostname = "TEST-HOST", Mac = "00-00-0C-00-00-01", Manufacturer = "Cisco Systems, Inc" });
            if (scanView.Items[0].SubItems.Count != 6 || scanView.Items[0].SubItems[1].Text != "TEST-HOST" ||
                scanView.Items[0].SubItems[4].Text != "Cisco Systems, Inc")
                throw new InvalidOperationException("IP Scanner result columns are misaligned.");
            scanView.Items.Clear();
            tabs.SelectedTab = scannerPage; Application.DoEvents();
            AddScanHost(new ScanHost { IP = "192.168.1.10", Hostname = "zulu", Ping = "12 ms" });
            AddScanHost(new ScanHost { IP = "192.168.1.2", Hostname = "Alpha", Ping = "3 ms" });
            SortScanResults(0);
            if (scanView.Items[0].Text != "192.168.1.2") throw new InvalidOperationException("IP Scanner IP sort is not numeric.");
            SortScanResults(0);
            if (scanView.Items[0].Text != "192.168.1.10") throw new InvalidOperationException("IP Scanner reverse IP sort failed.");
            SortScanResults(1);
            if (scanView.Items[0].SubItems[1].Text != "Alpha") throw new InvalidOperationException("IP Scanner A-Z hostname sort failed.");
            SortScanResults(1);
            if (scanView.Items[0].SubItems[1].Text != "zulu") throw new InvalidOperationException("IP Scanner Z-A hostname sort failed.");
            SortScanResults(2);
            if (scanView.Items[0].SubItems[2].Text != "3 ms") throw new InvalidOperationException("IP Scanner ping sort is not numeric.");
            AddScanHost(new ScanHost { IP = "192.168.1.3", Hostname = "Bravo", Ping = "1 ms" });
            if (scanView.Items[0].SubItems[2].Text != "1 ms") throw new InvalidOperationException("New scan result ignored the selected sort.");
            scanSortColumn = -1; scanView.Items.Clear();
            byte[] nodeStatus = new byte[43];
            nodeStatus[0] = 0x54; nodeStatus[1] = 0x45; nodeStatus[2] = 0x80; nodeStatus[7] = 1;
            nodeStatus[12] = 0xC0; nodeStatus[13] = 0x0C; nodeStatus[15] = 0x21; nodeStatus[17] = 1;
            nodeStatus[23] = 19; nodeStatus[24] = 1;
            for (int i = 25; i < 40; i++) nodeStatus[i] = (byte)' ';
            Encoding.ASCII.GetBytes("EBISERV").CopyTo(nodeStatus, 25);
            if (ParseNodeStatusName(nodeStatus, 0x5445) != "EBISERV" || ParseNodeStatusName(new byte[3], 0x5445) != "")
                throw new InvalidOperationException("NetBIOS hostname parsing failed.");
            if (typeof(ToolkitWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name == "System.Management.Automation")) throw new InvalidOperationException("PowerShell runtime reference found.");
            List<ToolkitProfile> originalProfiles = profiles.ToList();
            try
            {
                profiles.Clear();
                profiles.Add(new ToolkitProfile { Name = "Zebra" });
                profiles.Add(new ToolkitProfile { Name = "Alpha" });
                RefreshProfiles();
                if (profileView.Items[0].Text != "Alpha") throw new InvalidOperationException("IP profile A-Z sorting failed.");
                profileSort.SelectedIndex = 1;
                if (profileView.Items[0].Text != "Zebra") throw new InvalidOperationException("IP profile Z-A sorting failed.");
            }
            finally
            {
                profiles.Clear(); profiles.AddRange(originalProfiles);
                profileSort.SelectedIndex = 0; RefreshProfiles();
            }
            Show(); Application.DoEvents();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().First(page => page.Text == "IP Shifter");
            split.SplitterDistance = 650;
            Application.DoEvents();
            if (profileView.Columns.Cast<ColumnHeader>().Sum(column => column.Width) > profileView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4)
                throw new InvalidOperationException("IP profile columns require horizontal scrolling.");
            if (!tray.Visible) throw new InvalidOperationException("Tray icon not visible.");
            WindowState = FormWindowState.Minimized; Application.DoEvents();
            if (!Visible || !ShowInTaskbar || WindowState != FormWindowState.Minimized)
                throw new InvalidOperationException("Minimize removed the toolkit from the taskbar.");
            WindowState = FormWindowState.Normal; Application.DoEvents();
            Close(); Application.DoEvents();
            if (!tray.Visible || Visible) throw new InvalidOperationException("Close did not keep the app in the tray.");
            using (Process secondLaunch = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/activate") { UseShellExecute = false }))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while ((!secondLaunch.HasExited || !Visible) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                if (!secondLaunch.HasExited || secondLaunch.ExitCode != 0 || !Visible) throw new InvalidOperationException("Second-process activation did not restore the hidden toolkit.");
            }
            Hide();
            OnTrayMouseClick(tray, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)); Application.DoEvents();
            if (!Visible) throw new InvalidOperationException("Single-click tray restore failed.");
            bool previousTheme = dark;
            dark = true; ApplyTheme();
            TreeView siteTree = AllControls(rdpManager).OfType<TreeView>().Single();
            if (siteTree.BackColor == Color.White || siteTree.ForeColor.GetBrightness() < 0.5f) throw new InvalidOperationException("RDP tree dark theme is unreadable.");
            dark = false; ApplyTheme();
            if (siteTree.BackColor != Color.White || siteTree.ForeColor.GetBrightness() > 0.5f) throw new InvalidOperationException("RDP tree light theme is unreadable.");
            dark = previousTheme; ApplyTheme();
            AnnounceUpdate(new Dictionary<string, object>(), "99.0.0", true);
            if (updateButton.Text != "Update Available" || availableVersion != "99.0.0") throw new InvalidOperationException("Persistent update indicator failed.");
            if (ToolkitStartup.Command(@"C:\Program Files\Toolkit.exe") != @"""C:\Program Files\Toolkit.exe"" /startup") throw new InvalidOperationException("Windows startup command quoting failed.");
            using (Process startupTest = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/startup-self-test") { UseShellExecute = false }))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!startupTest.HasExited && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                if (!startupTest.HasExited) { startupTest.Kill(); throw new InvalidOperationException("Windows startup self-test timed out."); }
                if (startupTest.ExitCode != 0) throw new InvalidOperationException("Windows startup self-test failed: " + (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt")) ? File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt")) : "No child error details."));
            }
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
