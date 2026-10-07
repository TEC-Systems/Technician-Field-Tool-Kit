using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class RdpManager : UserControl
{
    private static readonly string[] Resolutions = { "Full screen", "Fit to screen", "1024 x 768", "1280 x 720", "1366 x 768", "1600 x 900", "1920 x 1080", "2560 x 1440" };
    private readonly List<RdpSiteProfile> sites;
    private readonly Action save;
    private readonly Action<string> activity;
    private readonly string folder;
    private readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox name = new TextBox(), group = new TextBox(), host = new TextBox(), user = new TextBox(), domain = new TextBox();
    private readonly TextBox password = new TextBox { UseSystemPasswordChar = true };
    private readonly CheckBox remember = new CheckBox { Text = "Save password for this Windows user", AutoSize = true };
    private readonly ComboBox resolution = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label notice = new Label { AutoSize = true, MaximumSize = new Size(480, 0) };
    private RdpSiteProfile selected;

    public RdpManager(List<RdpSiteProfile> profiles, Action persist, string dataFolder, Action<string> recordActivity = null)
    {
        sites = profiles; save = persist; folder = dataFolder; activity = recordActivity;
        Dock = DockStyle.Fill;
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        Controls.Add(layout);
        layout.Controls.Add(tree, 0, 0);
        Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        layout.Controls.Add(scroll, 1, 0);
        TableLayoutPanel editor = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12, 0, 0, 12) };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(editor);
        AddRow(editor, "Site name", name);
        AddRow(editor, "Folder", group);
        AddRow(editor, "Server / IP", host);
        AddRow(editor, "Username", user);
        AddRow(editor, "Domain", domain);
        AddRow(editor, "", new Label { AutoSize = true, MaximumSize = new Size(480, 0), Text = "For BMS sites using a local login, leave Domain blank. Enter a domain only if the site uses a domain account." });
        AddRow(editor, "Password", password);
        AddRow(editor, "", remember);
        resolution.Items.AddRange(Resolutions); resolution.SelectedIndex = 0;
        AddRow(editor, "Resolution", resolution);
        FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        AddButton(buttons, "New", NewSite);
        AddButton(buttons, "Save Site", SaveSite);
        AddButton(buttons, "Delete", DeleteSite);
        AddButton(buttons, "Connect", Connect);
        AddRow(editor, "", buttons);
        AddRow(editor, "", notice);
        notice.Text = "Connections open in Windows Remote Desktop. Saved passwords are encrypted for your Windows account on this laptop. Leave password blank to keep an existing saved password; uncheck Save password to remove it. Server policies may still require a prompt.";
        tree.AfterSelect += delegate { LoadSite(tree.SelectedNode == null ? null : tree.SelectedNode.Tag as RdpSiteProfile); };
        tree.NodeMouseDoubleClick += delegate(object sender, TreeNodeMouseClickEventArgs e) { if (e.Node.Tag is RdpSiteProfile) Run(Connect); };
        Reload();
    }

    private static void AddRow(TableLayoutPanel panel, string caption, Control control)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 8, 4, 8) }, 0, row);
        control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 4, 0, 8);
        panel.Controls.Add(control, 1, row);
    }

    private void AddButton(Control panel, string caption, Action action)
    {
        Button button = new Button { Text = caption, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, Tag = caption == "Connect" ? Color.FromArgb(0, 67, 230) : caption == "Save Site" ? Color.FromArgb(31, 128, 78) : Color.FromArgb(75, 94, 116), Margin = new Padding(0, 0, 8, 8) };
        button.Click += delegate { Run(action); }; panel.Controls.Add(button);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "RDP", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public void Reload()
    {
        RdpSiteProfile current = selected;
        tree.BeginUpdate(); tree.Nodes.Clear();
        foreach (IGrouping<string, RdpSiteProfile> folderSites in sites.OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).GroupBy(s => String.IsNullOrWhiteSpace(s.Group) ? "Sites" : s.Group, StringComparer.OrdinalIgnoreCase))
        {
            TreeNode parent = tree.Nodes.Add(folderSites.Key);
            foreach (RdpSiteProfile site in folderSites)
            {
                TreeNode node = parent.Nodes.Add(site.Name); node.Tag = site;
                if (Object.ReferenceEquals(site, current)) tree.SelectedNode = node;
            }
            parent.Expand();
        }
        tree.EndUpdate();
        if (current != null && !sites.Contains(current)) NewSite();
    }

    private void LoadSite(RdpSiteProfile site)
    {
        selected = site;
        if (site == null) { ClearFields(); return; }
        name.Text = site.Name; group.Text = site.Group; host.Text = site.Host; user.Text = site.User; domain.Text = site.Domain;
        password.Clear(); remember.Checked = !String.IsNullOrEmpty(site.ProtectedPassword);
        resolution.SelectedItem = Resolutions.Contains(site.Resolution) ? site.Resolution : Resolutions[0];
    }

    private void ClearFields()
    {
        name.Clear(); group.Clear(); host.Clear(); user.Clear(); domain.Clear(); password.Clear(); remember.Checked = false; resolution.SelectedIndex = 0;
    }

    private void NewSite() { selected = null; tree.SelectedNode = null; ClearFields(); name.Focus(); }

    private RdpSiteProfile ReadFields()
    {
        string server = host.Text.Trim();
        if (!Regex.IsMatch(server, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")) throw new InvalidOperationException("Enter a server hostname or IPv4 address without a port or path.");
        foreach (string value in new[] { user.Text, domain.Text })
            if (value.Any(c => Char.IsControl(c))) throw new InvalidOperationException("Username and domain cannot contain control characters.");
        if (remember.Checked && String.IsNullOrWhiteSpace(user.Text)) throw new InvalidOperationException("Enter a username before saving a password.");
        bool sameIdentity = selected != null && selected.Host.Equals(server, StringComparison.OrdinalIgnoreCase) && selected.User == user.Text.Trim() && selected.Domain == domain.Text.Trim();
        string encrypted = "";
        if (remember.Checked)
        {
            if (password.Text.Length > 0) encrypted = ProtectPassword(password.Text);
            else if (sameIdentity) encrypted = selected.ProtectedPassword;
        }
        return new RdpSiteProfile { Id = selected == null ? Guid.NewGuid().ToString("N") : selected.Id, Name = name.Text.Trim(), Group = group.Text.Trim(), Host = server,
            User = user.Text.Trim(), Domain = domain.Text.Trim(), Resolution = Convert.ToString(resolution.SelectedItem), ProtectedPassword = encrypted };
    }

    private void SaveSite()
    {
        RdpSiteProfile edited = ReadFields();
        if (edited.Name.Length == 0) throw new InvalidOperationException("Enter a site name.");
        if (sites.Any(s => !Object.ReferenceEquals(s, selected) && s.Name.Equals(edited.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That site name already exists. Choose another name or select the existing site to edit it.");
        RdpSiteProfile previous = selected;
        int index = previous == null ? -1 : sites.IndexOf(previous);
        if (index < 0) sites.Add(edited); else sites[index] = edited;
        selected = edited;
        try { save(); }
        catch { if (index < 0) sites.Remove(edited); else sites[index] = previous; selected = previous; throw; }
        if (!String.IsNullOrEmpty(folder)) WriteConnectionFile(edited, Screen.FromControl(this).WorkingArea.Size);
        if (activity != null) activity((index < 0 ? "Added site: " : "Updated site: ") + edited.Name);
        password.Clear(); Reload();
    }

    private void DeleteSite()
    {
        if (selected == null) throw new InvalidOperationException("Select a saved site first.");
        if (MessageBox.Show(this, "Delete saved site '" + selected.Name + "'?", "RDP", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        RdpSiteProfile previous = selected; int index = sites.IndexOf(previous); sites.Remove(previous);
        try { save(); } catch { sites.Insert(index, previous); throw; }
        if (!String.IsNullOrEmpty(folder)) DeleteConnectionFile(folder, previous.Id);
        if (activity != null) activity("Deleted site: " + previous.Name);
        NewSite(); Reload();
    }

    private void Connect()
    {
        RdpSiteProfile connection = ReadFields();
        // An entered password can be used for this connection without saving it in the profile.
        if (!remember.Checked && password.Text.Length > 0) connection.ProtectedPassword = ProtectPassword(password.Text);
        // A saved site uses the same path; one-off credentials use a disposable file.
        bool persistent = selected != null && password.Text.Length == 0;
        string path;
        Size screen = Screen.FromControl(this).WorkingArea.Size;
        if (persistent) path = WriteConnectionFile(connection, screen);
        else
        {
            string connections = Path.Combine(folder, "rdp-connections"); Directory.CreateDirectory(connections);
            path = Path.Combine(connections, "temporary-" + Guid.NewGuid().ToString("N") + ".rdp");
            File.WriteAllText(path, BuildRdpFile(connection, screen), Encoding.Unicode);
        }
        try
        {
            Process session = Process.Start(new ProcessStartInfo("mstsc.exe", "\"" + path + "\"") { UseShellExecute = true });
            if (persistent) { if (session != null) session.Dispose(); }
            else ThreadPool.QueueUserWorkItem(delegate {
                try { if (session != null) { session.WaitForExit(); session.Dispose(); } File.Delete(path); }
                catch { /* Windows may still be using this encrypted connection file. */ }
            });
        }
        catch { if (!persistent) File.Delete(path); throw; }
        if (activity != null) activity("Opened connection: " + connection.Name);
        password.Clear();
    }

    public void OpenSavedSite(string id)
    {
        RdpSiteProfile site = sites.FirstOrDefault(item => item.Id == id);
        if (site == null) throw new InvalidOperationException("The linked RDP connection was removed.");
        LoadSite(site);
        Connect();
    }

    internal static string ValidProfileId(string value)
    {
        Guid id; return Guid.TryParse(value, out id) ? id.ToString("N") : Guid.NewGuid().ToString("N");
    }

    private static string ConnectionPath(string dataFolder, string id)
    {
        Guid parsed;
        if (!Guid.TryParse(id, out parsed)) throw new InvalidOperationException("The saved site identifier is invalid. Save the site again.");
        return Path.Combine(dataFolder, "rdp-connections", parsed.ToString("N") + ".rdp");
    }

    internal static void DeleteConnectionFile(string dataFolder, string id)
    {
        string path = ConnectionPath(dataFolder, id);
        if (File.Exists(path)) File.Delete(path);
    }

    private string WriteConnectionFile(RdpSiteProfile site, Size screen)
    {
        string path = ConnectionPath(folder, site.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string previous = File.Exists(path) ? File.ReadAllText(path, Encoding.Unicode) : "";
        File.WriteAllText(path, MergeConnectionPreferences(previous, BuildRdpFile(site, screen)), Encoding.Unicode);
        return path;
    }

    internal static string MergeConnectionPreferences(string previous, string generated)
    {
        // Retain ordinary MSTSC display/resource preferences, never silently suppress server authentication.
        string[] preserved = { "redirectclipboard", "redirectprinters", "redirectcomports", "redirectsmartcards", "redirectposdevices", "drivestoredirect", "devicestoredirect", "audiomode", "audiocapturemode", "keyboardhook", "smart sizing", "use multimon", "selectedmonitors", "winposstr", "connection type", "networkautodetect", "bandwidthautodetect", "displayconnectionbar", "disable wallpaper", "disable full window drag", "disable menu anims", "disable themes", "bitmapcachepersistenable" };
        List<string> lines = generated.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string line in previous.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator < 0) continue;
            string key = line.Substring(0, separator);
            if (!preserved.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            lines.RemoveAll(item => item.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)); lines.Add(line);
        }
        return String.Join("\r\n", lines.ToArray()) + "\r\n";
    }

    internal static string ProtectPassword(string value)
    {
        byte[] plain = Encoding.Unicode.GetBytes(value);
        try { return BitConverter.ToString(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)).Replace("-", ""); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    internal static string BuildRdpFile(RdpSiteProfile site, Size screen)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("full address:s:" + site.Host);
        string login = String.IsNullOrEmpty(site.Domain) || site.User.Contains("\\") || site.User.Contains("@") ? site.User : site.Domain + "\\" + site.User;
        text.AppendLine("username:s:" + login);
        text.AppendLine("screen mode id:i:" + (site.Resolution == "Full screen" ? "2" : "1"));
        Size size = screen;
        Match match = Regex.Match(site.Resolution ?? "", @"^(\d+) x (\d+)$");
        if (match.Success) size = new Size(Int32.Parse(match.Groups[1].Value), Int32.Parse(match.Groups[2].Value));
        else if (site.Resolution != "Full screen") size = new Size(Math.Max(640, screen.Width - 80), Math.Max(480, screen.Height - 120));
        text.AppendLine("desktopwidth:i:" + size.Width); text.AppendLine("desktopheight:i:" + size.Height);
        text.AppendLine("authentication level:i:2"); text.AppendLine("enablecredsspsupport:i:1");
        text.AppendLine("redirectclipboard:i:1"); text.AppendLine("redirectprinters:i:0");
        if (!String.IsNullOrEmpty(site.ProtectedPassword))
        {
            if (!Regex.IsMatch(site.ProtectedPassword, @"\A(?:[0-9A-F]{2})+\z")) throw new InvalidOperationException("The saved password is invalid. Enter it again and save the site.");
            text.AppendLine("password 51:b:" + site.ProtectedPassword);
        }
        return text.ToString();
    }

    internal static void SelfTest()
    {
        List<RdpSiteProfile> data = new List<RdpSiteProfile>(); int saves = 0;
        using (RdpManager manager = new RdpManager(data, delegate { saves++; }, ""))
        {
            manager.name.Text = "Office server"; manager.group.Text = "Office"; manager.host.Text = "192.0.2.10";
            manager.user.Text = "technician"; manager.domain.Text = "TEC"; manager.resolution.SelectedItem = "1280 x 720";
            manager.remember.Checked = true; manager.password.Text = "self-test-password"; manager.SaveSite();
            if (saves != 1 || data.Count != 1 || manager.tree.Nodes[0].Text != "Office" || manager.password.Text.Length != 0)
                throw new InvalidOperationException("RDP site saving or folder display failed.");
            RdpSiteProfile site = data[0];
            string temporaryFolder = Path.Combine(Path.GetTempPath(), "TEC-Rdp-Test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (RdpManager fileManager = new RdpManager(data, delegate { }, temporaryFolder))
                {
                    string path = fileManager.WriteConnectionFile(site, new Size(1920, 1080));
                    File.AppendAllText(path, "redirectprinters:i:1\r\n", Encoding.Unicode);
                    string secondPath = fileManager.WriteConnectionFile(site, new Size(1920, 1080));
                    if (path != secondPath || !File.ReadAllText(path).Contains("redirectprinters:i:1")) throw new InvalidOperationException("Saved RDP file or Windows preferences were not retained.");
                    RdpSiteProfile cleared = new RdpSiteProfile { Id = site.Id, Host = site.Host, User = site.User, Resolution = site.Resolution };
                    fileManager.WriteConnectionFile(cleared, new Size(1920, 1080));
                    if (File.ReadAllText(path).Contains("password 51:")) throw new InvalidOperationException("Removed RDP password remained in the connection file.");
                    DeleteConnectionFile(temporaryFolder, site.Id);
                    if (File.Exists(path)) throw new InvalidOperationException("Deleted RDP site retained its connection file.");
                }
            }
            finally { if (Directory.Exists(temporaryFolder)) Directory.Delete(temporaryFolder, true); }
            string retained = MergeConnectionPreferences("redirectprinters:i:1\r\nauthentication level:i:0\r\npassword 51:b:DEAD\r\n", BuildRdpFile(site, new Size(1920, 1080)));
            if (!retained.Contains("redirectprinters:i:1") || !retained.Contains("authentication level:i:2") || retained.Contains("password 51:b:DEAD")) throw new InvalidOperationException("RDP preferences or authentication retention failed.");
            byte[] encrypted = Enumerable.Range(0, site.ProtectedPassword.Length / 2).Select(i => Convert.ToByte(site.ProtectedPassword.Substring(i * 2, 2), 16)).ToArray();
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { if (Encoding.Unicode.GetString(plain) != "self-test-password") throw new InvalidOperationException("RDP password encryption round-trip failed."); }
            finally { Array.Clear(plain, 0, plain.Length); }
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            RdpSiteProfile restored = serializer.Deserialize<RdpSiteProfile>(serializer.Serialize(site));
            if (restored.Id != site.Id || restored.Host != site.Host || restored.Group != "Office" || restored.Resolution != "1280 x 720" || restored.ProtectedPassword != site.ProtectedPassword)
                throw new InvalidOperationException("RDP profile persistence failed.");
            string file = BuildRdpFile(restored, new Size(1920, 1080));
            if (!file.Contains("username:s:TEC\\technician") || !file.Contains("desktopwidth:i:1280") || !file.Contains("desktopheight:i:720") || file.Contains("self-test-password"))
                throw new InvalidOperationException("RDP connection settings failed.");
            manager.SaveSite(); if (data[0].Id != site.Id || data[0].ProtectedPassword != site.ProtectedPassword) throw new InvalidOperationException("RDP saved password was lost on edit.");
            manager.host.Text = "192.0.2.11"; manager.SaveSite();
            if (data[0].ProtectedPassword.Length != 0) throw new InvalidOperationException("RDP credentials carried over to a different server.");
            manager.password.Text = "self-test-password"; manager.SaveSite();
            manager.remember.Checked = false; manager.SaveSite();
            if (data[0].ProtectedPassword.Length != 0) throw new InvalidOperationException("RDP password removal failed.");
            manager.resolution.SelectedItem = "Full screen"; manager.SaveSite();
            if (!BuildRdpFile(data[0], new Size(1920, 1080)).Contains("screen mode id:i:2")) throw new InvalidOperationException("RDP full screen selection failed.");
            manager.NewSite(); if (manager.host.Text.Length != 0 || manager.selected != null) throw new InvalidOperationException("RDP new-site fields failed.");
        }
    }
}
