using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class ToolkitSetup
{
    private const string ProductName = "TEC Systems Field Toolkit";
    private const string ExeName = "TEC-Systems-FieldToolkit.exe";
    private const string UninstallName = "ToolkitUninstall.exe";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TEC Systems Field Toolkit";
    private static readonly string InstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ProductName);
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TEC Systems", "Field Toolkit");
    private static readonly string[] PayloadFiles = {
        ExeName, "version.txt",
        "assets/TEC Systems Full Logo Cobalt RGB.png",
        "assets/TEC Systems Field Toolkit.ico",
        "assets/TEC Systems Field Toolkit Tray.ico"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "/verify") { VerifyPayload(); return 0; }
            if (args.Length > 0 && args[0] == "/self-test")
            {
                Application.EnableVisualStyles();
                using (Form form = CreateWindow(false))
                {
                    CheckBox launch = form.Controls.OfType<CheckBox>().FirstOrDefault(box => box.Text == "Launch after setup");
                    if (launch == null || launch.Checked) throw new InvalidOperationException("Setup must not launch the toolkit by default.");
                }
                return 0;
            }
            if (args.Length > 1 && args[0] == "/test-install")
            {
                ExtractPayload(Path.GetFullPath(args[1]));
                return 0;
            }
            if (args.Length > 0 && args[0] == "/remove")
            {
                BeginRemove();
                return 0;
            }
            if (args.Length > 0 && args[0] == "/remove-run")
            {
                if (args.Length != 2) throw new ArgumentException("Missing uninstall parent process ID.");
                int parentId;
                if (!Int32.TryParse(args[1], out parentId) || parentId <= 0) throw new ArgumentException("Invalid uninstall parent process ID.");
                WaitForProcessExit(parentId);
                RemoveInstalledFiles();
                MessageBox.Show("The toolkit, saved profiles, settings, and logs were removed.", ProductName);
                return 0;
            }
            bool automaticUpdate = args.Length == 2 && args[0] == "/wait-for-exit";
            if (automaticUpdate)
            {
                int previousProcessId;
                if (!Int32.TryParse(args[1], out previousProcessId) || previousProcessId <= 0)
                    throw new ArgumentException("Invalid toolkit process ID for update.");
                try
                {
                    using (Process previous = Process.GetProcessById(previousProcessId))
                    {
                        if (!previous.WaitForExit(15000))
                            throw new IOException("The toolkit did not exit in time. Use the TEC notification-area icon to exit it, then run Setup again.");
                    }
                }
                catch (ArgumentException) { /* The toolkit already exited. */ }
                InstallPayload(null);
                Process.Start(Path.Combine(InstallFolder, ExeName));
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(CreateWindow(automaticUpdate));
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0 && (args[0] == "/verify" || args[0] == "/self-test" || args[0] == "/test-install")) return 1;
            MessageBox.Show(error.Message, ProductName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static Form CreateWindow(bool automaticUpdate)
    {
        VerifyPayload();
        bool updating = File.Exists(Path.Combine(InstallFolder, ExeName));
        Form form = new Form {
            Text = ProductName + " Setup", Width = 550, Height = 370,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, BackColor = System.Drawing.Color.White
        };
        try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Label title = new Label {
            Text = updating ? "Update TEC Systems Field Toolkit" : "Install TEC Systems Field Toolkit",
            Left = 28, Top = 26, Width = 480, Height = 35,
            Font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold),
            ForeColor = System.Drawing.Color.FromArgb(16, 46, 92)
        };
        Label managed = new Label {
            Text = "Managed by TEC Systems IT", Left = 30, Top = 70,
            Width = 470, Height = 24, Font = new System.Drawing.Font("Segoe UI", 9)
        };
        Label version = new Label {
            Text = "Version " + PayloadVersion() + "  |  Per-user installation",
            Left = 30, Top = 106, Width = 470, Height = 24
        };
        Label destination = new Label {
            Text = InstallFolder, Left = 30, Top = 134, Width = 475, Height = 40,
            ForeColor = System.Drawing.Color.DimGray
        };
        Label exitSteps = new Label {
            Text = automaticUpdate ? "The toolkit has exited automatically. Continue with the update below; your saved profiles and logs will remain." : updating ? "Before updating: click the notification-area arrow near the clock, right-click the TEC icon, then choose Exit Toolkit. Closing the window only hides it." : "Closing the toolkit window keeps it in the notification area. To close it fully, right-click the TEC icon and choose Exit Toolkit.",
            Left = 30, Top = 181, Width = 475, Height = 50,
            ForeColor = System.Drawing.Color.FromArgb(60, 70, 83)
        };
        CheckBox desktop = new CheckBox { Text = "Create desktop shortcut", Left = 30, Top = 238, Width = 220, Checked = true };
        CheckBox launch = new CheckBox { Text = "Launch after setup", Left = 260, Top = 238, Width = 190, Checked = false };
        Button uninstall = new Button {
            Text = "Uninstall", Left = 190, Top = 280,
            Width = 145, Height = 34, BackColor = System.Drawing.Color.FromArgb(75, 94, 116),
            ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat
        };
        uninstall.Click += (sender, e) => {
            try { if (BeginRemove()) form.Close(); }
            catch (Exception error) { MessageBox.Show(error.Message, ProductName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        Button install = new Button {
            Text = updating ? "Update" : "Install", Left = 365, Top = 280,
            Width = 140, Height = 34, BackColor = System.Drawing.Color.FromArgb(6, 72, 255),
            ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat
        };
        install.Click += (sender, e) => {
             install.Enabled = false;
             try {
                InstallPayload(desktop.Checked);
                MessageBox.Show("The toolkit is ready.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (launch.Checked) Process.Start(Path.Combine(InstallFolder, ExeName));
                form.Close();
            }
            catch (Exception error) {
                MessageBox.Show(error.Message, ProductName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                install.Enabled = true;
            }
        };
        form.Controls.AddRange(new Control[] { title, managed, version, destination, exitSteps, desktop, launch, install });
        bool cleanupAvailable = updating || Directory.Exists(InstallFolder) || Directory.Exists(DataFolder);
        using (RegistryKey registration = Registry.CurrentUser.OpenSubKey(RegistryPath))
            cleanupAvailable = cleanupAvailable || registration != null;
        if (cleanupAvailable) form.Controls.Add(uninstall);
        return form;
    }

    private static bool BeginRemove()
    {
        if (MessageBox.Show("Uninstall the toolkit and permanently delete this Windows user's saved profiles, passwords, settings, and logs? Any running toolkit will close.",
            ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        string copy = Path.Combine(Path.GetTempPath(), "TEC-FieldToolkit-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Application.ExecutablePath, copy);
        Process.Start(new ProcessStartInfo(copy, "/remove-run " + Process.GetCurrentProcess().Id) {
            UseShellExecute = true, WorkingDirectory = Path.GetTempPath()
        });
        return true;
    }

    private static void WaitForProcessExit(int id)
    {
        try { using (Process previous = Process.GetProcessById(id)) if (!previous.WaitForExit(15000)) throw new IOException("The setup window did not close in time. Close it and try uninstall again."); }
        catch (ArgumentException) { /* The parent already exited. */ }
    }

    private static void InstallPayload(bool? desktopShortcut)
    {
        if (IsInstalledToolkitRunning())
            throw new IOException("The installed toolkit is still running on this laptop. Click the notification-area arrow near the clock, right-click the TEC icon, choose Exit Toolkit, then try again. Closing the window alone is not enough.");
        ExtractPayload(InstallFolder);
        string legacyScript = Path.Combine(InstallFolder, "TEC-Systems-FieldToolkit.ps1");
        if (File.Exists(legacyScript)) File.Delete(legacyScript);
        File.Copy(Application.ExecutablePath, Path.Combine(InstallFolder, UninstallName), true);
        CreateShortcut(StartShortcut, Path.Combine(InstallFolder, ExeName));
        if (desktopShortcut == true) CreateShortcut(DesktopShortcut, Path.Combine(InstallFolder, ExeName));
        else if (desktopShortcut == false && File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
        RegisterUninstall(PayloadVersion());
        // Enable sign-in startup on first adoption; preserve the user's later opt-out.
        using (RegistryKey preference = Registry.CurrentUser.CreateSubKey(@"Software\TEC Systems\Field Toolkit"))
        using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (preference.GetValue("StartupConfigured") == null || run.GetValue(ProductName) != null)
                run.SetValue(ProductName, "\"" + Path.Combine(InstallFolder, ExeName) + "\" /startup");
            preference.SetValue("StartupConfigured", 1);
        }
    }

    private static bool IsInstalledToolkitRunning()
    {
        string installedExe = Path.Combine(InstallFolder, ExeName);
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule.FileName, installedExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (System.ComponentModel.Win32Exception) { /* An inaccessible process cannot be this user's install. */ }
                catch (InvalidOperationException) { /* The process exited during the check. */ }
            }
        }
        return false;
    }

    private static string StartShortcut {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TEC Systems", ProductName + ".lnk"); }
    }
    private static string DesktopShortcut {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"); }
    }

    private static ZipArchive OpenPayload()
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ToolkitPayload");
        if (stream == null) throw new InvalidDataException("The installer payload is missing.");
        return new ZipArchive(stream, ZipArchiveMode.Read, false);
    }

    private static void VerifyPayload()
    {
        using (ZipArchive archive = OpenPayload())
        {
            foreach (string name in PayloadFiles)
                if (FindEntry(archive, name) == null) throw new InvalidDataException("Missing payload file: " + name);
        }
    }

    private static ZipArchiveEntry FindEntry(ZipArchive archive, string name)
    {
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), name, StringComparison.Ordinal));
    }

    private static string PayloadVersion()
    {
        using (ZipArchive archive = OpenPayload())
        using (StreamReader reader = new StreamReader(FindEntry(archive, "version.txt").Open()))
            return reader.ReadToEnd().Trim();
    }

    private static void ExtractPayload(string destination)
    {
        VerifyPayload();
        Directory.CreateDirectory(destination);
        using (ZipArchive archive = OpenPayload())
        {
            foreach (string name in PayloadFiles)
            {
                string path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (Stream input = FindEntry(archive, name).Open())
                using (Stream output = File.Create(path)) input.CopyTo(output);
            }
        }
    }

    private static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            try
            {
                Type type = shortcut.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { InstallFolder });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void RegisterUninstall(string version)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName", ProductName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "TEC Systems IT");
            key.SetValue("DisplayIcon", Path.Combine(InstallFolder, ExeName));
            key.SetValue("InstallLocation", InstallFolder);
            key.SetValue("UninstallString", "\"" + Path.Combine(InstallFolder, UninstallName) + "\" /remove");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static void RemoveInstalledFiles()
    {
        string installedExe = Path.Combine(InstallFolder, ExeName);
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                string path;
                try { path = process.MainModule.FileName; }
                catch (InvalidOperationException) { continue; }
                catch (System.ComponentModel.Win32Exception) { continue; }
                if (!String.Equals(path, installedExe, StringComparison.OrdinalIgnoreCase)) continue;
                try { process.Kill(); if (!process.WaitForExit(10000)) throw new IOException("The running toolkit did not close. Exit it from the TEC tray icon and try uninstall again."); }
                catch (InvalidOperationException) { /* The toolkit already exited. */ }
            }
        }
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            if (run != null) run.DeleteValue(ProductName, false);
        if (File.Exists(StartShortcut)) File.Delete(StartShortcut);
        if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
        string startFolder = Path.GetDirectoryName(StartShortcut);
        if (Directory.Exists(startFolder) && !Directory.EnumerateFileSystemEntries(startFolder).Any()) Directory.Delete(startFolder);
        if (Directory.Exists(InstallFolder)) Directory.Delete(InstallFolder, true);
        if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, true);
        Registry.CurrentUser.DeleteSubKey(RegistryPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\TEC Systems\Field Toolkit", false);
    }
}
