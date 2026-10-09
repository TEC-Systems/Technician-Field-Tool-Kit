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
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Web.Script.Serialization;

internal sealed class RdpManager : UserControl
{
    private static readonly string[] Resolutions = { "Full screen", "Fit to screen", "1024 x 768", "1280 x 720", "1366 x 768", "1600 x 900", "1920 x 1080", "2560 x 1440" };
    private readonly List<RdpSiteProfile> sites;
    private readonly Action save;
    private readonly Action<string> activity;
    private readonly string folder;
    private readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
    private readonly ComboBox sort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox name = new TextBox(), group = new TextBox(), host = new TextBox(), user = new TextBox();
    private readonly TextBox password = new TextBox { UseSystemPasswordChar = true };
    private readonly CheckBox remember = new CheckBox { Text = "Save password for this Windows user", AutoSize = true };
    private readonly ComboBox resolution = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label notice = new Label { AutoSize = true, MaximumSize = new Size(480, 0) };
    private Button favoriteButton;
    private RdpSiteProfile selected;
    private bool reloadingTree;

    public RdpManager(List<RdpSiteProfile> profiles, Action persist, string dataFolder, Action<string> recordActivity = null)
    {
        sites = profiles; save = persist; folder = dataFolder; activity = recordActivity;
        Dock = DockStyle.Fill;
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        Controls.Add(layout);
        Panel browser = new Panel { Dock = DockStyle.Fill };
        layout.Controls.Add(browser, 0, 0);
        browser.Controls.Add(tree);
        TableLayoutPanel browserTools = new TableLayoutPanel { Dock = DockStyle.Top, Height = 68, ColumnCount = 2, RowCount = 2 };
        browserTools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        browserTools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        browserTools.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        browserTools.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        browser.Controls.Add(browserTools);
        browserTools.Controls.Add(new Label { Text = "Search sites", Dock = DockStyle.Fill }, 0, 0);
        browserTools.Controls.Add(new Label { Text = "Sort", Dock = DockStyle.Fill }, 1, 0);
        browserTools.Controls.Add(search, 0, 1);
        sort.Items.AddRange(new object[] { "A–Z", "Z–A" }); sort.SelectedIndex = 0;
        browserTools.Controls.Add(sort, 1, 1);
        search.TextChanged += delegate { Reload(); };
        sort.SelectedIndexChanged += delegate { Reload(); };
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
        AddRow(editor, "", new Label { AutoSize = true, MaximumSize = new Size(480, 0), Text = @"For a site-local account, enter SERVERNAME\username. Use the account that works in Remote Desktop." });
        AddRow(editor, "Password", password);
        password.TextChanged += delegate { if (password.Text.Length > 0) remember.Checked = true; };
        AddRow(editor, "", remember);
        resolution.Items.AddRange(Resolutions); resolution.SelectedIndex = 0;
        AddRow(editor, "Resolution", resolution);
        FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        AddButton(buttons, "New", NewSite);
        AddButton(buttons, "Save Site", SaveSite);
        AddButton(buttons, "Delete", DeleteSite);
        AddButton(buttons, "Connect", Connect);
        favoriteButton = AddButton(buttons, "Add Favorite", ToggleFavorite);
        AddButton(buttons, "Troubleshoot Login", ShowCredentialHelp);
        favoriteButton.Enabled = false;
        AddRow(editor, "", buttons);
        AddRow(editor, "", notice);
        notice.Text = "Enter the site's username and password, then Save Site. Leave Password blank later to keep it.";
        tree.AfterSelect += delegate { if (!reloadingTree) LoadSite(tree.SelectedNode == null ? null : tree.SelectedNode.Tag as RdpSiteProfile); };
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

    private Button AddButton(Control panel, string caption, Action action)
    {
        Button button = new Button { Text = caption, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, Tag = caption == "Connect" ? Color.FromArgb(0, 67, 230) : caption == "Save Site" ? Color.FromArgb(31, 128, 78) : Color.FromArgb(75, 94, 116), Margin = new Padding(0, 0, 8, 8) };
        button.Click += delegate { Run(action); }; panel.Controls.Add(button);
        return button;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "RDP", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public void Reload()
    {
        RdpSiteProfile current = selected;
        IEnumerable<RdpSiteProfile> visible = sites.Where(site => MatchesSearch(site, search.Text));
        IOrderedEnumerable<RdpSiteProfile> ordered = sort.SelectedIndex == 1 ?
            visible.OrderByDescending(site => site.Name, StringComparer.OrdinalIgnoreCase) :
            visible.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase);
        reloadingTree = true;
        tree.BeginUpdate(); tree.Nodes.Clear();
        TreeNode selectedNode = null;
        RdpSiteProfile[] favorites = ordered.Where(site => site.Favorite).ToArray();
        if (favorites.Length > 0)
        {
            TreeNode parent = tree.Nodes.Add("★ Favorites");
            foreach (RdpSiteProfile site in favorites)
            {
                TreeNode node = parent.Nodes.Add(site.Name); node.Tag = site;
                if (Object.ReferenceEquals(site, current)) selectedNode = node;
            }
            parent.Expand();
        }
        IEnumerable<IGrouping<string, RdpSiteProfile>> groups = ordered.GroupBy(site => String.IsNullOrWhiteSpace(site.Group) ? "Sites" : site.Group, StringComparer.OrdinalIgnoreCase);
        groups = sort.SelectedIndex == 1 ? groups.OrderByDescending(item => item.Key, StringComparer.OrdinalIgnoreCase) : groups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, RdpSiteProfile> folderSites in groups)
        {
            TreeNode parent = tree.Nodes.Add(folderSites.Key);
            foreach (RdpSiteProfile site in folderSites)
            {
                TreeNode node = parent.Nodes.Add(site.Name); node.Tag = site;
                if (selectedNode == null && Object.ReferenceEquals(site, current)) selectedNode = node;
            }
            parent.Expand();
        }
        tree.SelectedNode = selectedNode;
        tree.EndUpdate();
        reloadingTree = false;
        if (current != null && !sites.Contains(current)) NewSite();
    }

    private static bool MatchesSearch(RdpSiteProfile site, string query)
    {
        query = (query ?? "").Trim();
        return query.Length == 0 || new[] { site.Name, site.Group, site.Host }.Any(value =>
            (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void ToggleFavorite()
    {
        if (selected == null) throw new InvalidOperationException("Select a saved site first.");
        selected.Favorite = !selected.Favorite;
        try { save(); }
        catch { selected.Favorite = !selected.Favorite; throw; }
        if (activity != null) activity((selected.Favorite ? "Favorited site: " : "Removed favorite: ") + selected.Name);
        Reload(); UpdateFavoriteButton();
    }

    private void UpdateFavoriteButton()
    {
        if (favoriteButton == null) return;
        favoriteButton.Enabled = selected != null;
        favoriteButton.Text = selected != null && selected.Favorite ? "Remove Favorite" : "Add Favorite";
    }

    private void LoadSite(RdpSiteProfile site)
    {
        selected = site;
        UpdateFavoriteButton();
        if (site == null) { ClearFields(); return; }
        name.Text = site.Name; group.Text = site.Group; host.Text = site.Host; user.Text = LoginName(site);
        password.Clear(); remember.Checked = !String.IsNullOrEmpty(site.ProtectedPassword);
        resolution.SelectedItem = Resolutions.Contains(site.Resolution) ? site.Resolution : Resolutions[0];
    }

    private void ClearFields()
    {
        name.Clear(); group.Clear(); host.Clear(); user.Clear(); password.Clear(); remember.Checked = false; resolution.SelectedIndex = 0;
    }

    private void NewSite() { selected = null; tree.SelectedNode = null; ClearFields(); UpdateFavoriteButton(); name.Focus(); }

    private RdpSiteProfile ReadFields()
    {
        string server = host.Text.Trim();
        if (!Regex.IsMatch(server, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")) throw new InvalidOperationException("Enter a server hostname or IPv4 address without a port or path.");
        if (user.Text.Any(c => Char.IsControl(c))) throw new InvalidOperationException("Windows login cannot contain control characters.");
        if (remember.Checked && String.IsNullOrWhiteSpace(user.Text)) throw new InvalidOperationException("Enter a Windows login before saving a password.");
        if (!String.IsNullOrWhiteSpace(user.Text) && !user.Text.Contains("\\") && !user.Text.Contains("@"))
            throw new InvalidOperationException(@"Enter the complete username, such as SERVERNAME\user or DOMAIN\user.");
        bool sameIdentity = selected != null && selected.Host.Equals(server, StringComparison.OrdinalIgnoreCase) &&
            String.Equals(LoginName(selected), user.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        string encrypted = "";
        if (remember.Checked)
        {
            if (password.Text.Length > 0) encrypted = ProtectPassword(password.Text);
            else if (sameIdentity) encrypted = selected.ProtectedPassword;
        }
        return new RdpSiteProfile { Id = selected == null ? Guid.NewGuid().ToString("N") : selected.Id, Name = name.Text.Trim(), Group = group.Text.Trim(), Host = server,
            User = user.Text.Trim(), Domain = "", Resolution = Convert.ToString(resolution.SelectedItem), ProtectedPassword = encrypted,
            Favorite = selected != null && selected.Favorite };
    }

    private void SaveSite()
    {
        RdpSiteProfile edited = ReadFields();
        if (edited.Name.Length == 0) throw new InvalidOperationException("Enter a site name.");
        if (sites.Any(s => !Object.ReferenceEquals(s, selected) && s.Name.Equals(edited.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That site name already exists. Choose another name or select the existing site to edit it.");
        RdpSiteProfile previous = selected;
        int index = previous == null ? -1 : sites.IndexOf(previous);
        if (previous != null && (previous.Host != edited.Host || previous.ProtectedPassword != edited.ProtectedPassword))
            RdpCredentials.RemoveIfOwned(previous);
        if (index < 0) sites.Add(edited); else sites[index] = edited;
        selected = edited;
        try { save(); }
        catch { if (index < 0) sites.Remove(edited); else sites[index] = previous; selected = previous; throw; }
        if (remember.Checked && password.Text.Length > 0) RdpCredentials.Store(edited, true);
        if (!String.IsNullOrEmpty(folder)) WriteConnectionFile(edited, Screen.FromControl(this).WorkingArea.Size);
        if (activity != null) activity((index < 0 ? "Added site: " : "Updated site: ") + edited.Name);
        user.Text = LoginName(edited);
        password.Clear(); remember.Checked = !String.IsNullOrEmpty(edited.ProtectedPassword);
        Reload(); UpdateFavoriteButton();
        notice.Text = "Saved " + edited.Name + " with username " + LoginName(edited) + ". " +
            (remember.Checked ? "Click Connect to test it." : "Enter and save a password to connect without a prompt.");
    }

    private void DeleteSite()
    {
        if (selected == null) throw new InvalidOperationException("Select a saved site first.");
        if (MessageBox.Show(this, "Delete saved site '" + selected.Name + "'?", "RDP", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        RdpSiteProfile previous = selected; int index = sites.IndexOf(previous);
        RdpCredentials.RemoveIfOwned(previous);
        sites.Remove(previous);
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
        if (persistent && !String.IsNullOrEmpty(connection.ProtectedPassword))
            RdpCredentials.Store(connection);
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

    private void ShowCredentialHelp()
    {
        if (selected == null) throw new InvalidOperationException("Select a saved RDP site first.");
        RdpSiteProfile site = selected;
        string report = RdpPolicyDiagnostics.Report(site);
        bool darkTheme = Parent != null && Parent.BackColor.GetBrightness() < 0.5f;
        Color background = darkTheme ? Color.FromArgb(27, 36, 43) : Color.White;
        Color foreground = darkTheme ? Color.FromArgb(235, 242, 246) : Color.FromArgb(20, 36, 57);
        using (Form dialog = new Form { Text = "RDP Login Help", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(900, 600), MinimumSize = new Size(760, 440), BackColor = background, ForeColor = foreground,
            Font = new Font("Segoe UI", 9f) })
        {
            TextBox text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, Text = report, BackColor = darkTheme ? Color.FromArgb(38, 50, 59) : Color.White,
                ForeColor = foreground, Font = new Font("Consolas", 9f), WordWrap = true };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 82, FlowDirection = FlowDirection.RightToLeft };
            Button close = new Button { Text = "Close", Width = 110, Height = 32, DialogResult = DialogResult.OK };
            Button copy = new Button { Text = "Copy for IT", Width = 120, Height = 32 };
            copy.Click += delegate {
                try { Clipboard.SetText(text.Text); }
                catch (Exception error) { MessageBox.Show(dialog, "Could not copy the report: " + error.Message, "RDP Login Help", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            Button refresh = new Button { Text = "Refresh report", Width = 120, Height = 32 };
            Button apply = new Button { Text = "Allow this IP", Width = 120, Height = 32 };
            Button undo = new Button { Text = "Undo local rule", Width = 120, Height = 32 };
            Button clear = new Button { Text = "Clear conflicting login", Width = 155, Height = 32 };
            refresh.Click += delegate { text.Text = RdpPolicyDiagnostics.Report(site); clear.Enabled = RdpCredentials.HasConflictingWindowsLogon(site); };
            bool supported = RdpPolicyDiagnostics.IsExactIpv4(site.Host);
            apply.Enabled = undo.Enabled = supported;
            clear.Enabled = RdpCredentials.HasConflictingWindowsLogon(site);
            apply.Click += delegate { StartPolicyHelper(dialog, "apply", site.Host); };
            undo.Click += delegate { StartPolicyHelper(dialog, "undo", site.Host); };
            clear.Click += delegate {
                if (MessageBox.Show(dialog, "Windows has a separate saved logon for TERMSRV/" + site.Host +
                    " that was not created by this toolkit. Remove that Windows logon entry for your account? " +
                    "The toolkit's saved password and other sites will remain. You can reconnect to register the toolkit credential again.",
                    "Clear conflicting RDP login", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    RdpCredentials.RemoveConflictingWindowsLogon(site);
                    if (activity != null) activity("Cleared conflicting RDP login: " + site.Name);
                    text.Text = RdpPolicyDiagnostics.Report(site);
                    clear.Enabled = RdpCredentials.HasConflictingWindowsLogon(site);
                    MessageBox.Show(dialog, "The conflicting Windows logon was removed. Close existing Remote Desktop windows, then connect to this site again.",
                        "RDP Login Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception error) { MessageBox.Show(dialog, error.Message, "RDP Login Help", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            actions.Controls.Add(close); actions.Controls.Add(copy); actions.Controls.Add(refresh);
            actions.Controls.Add(clear); actions.Controls.Add(undo); actions.Controls.Add(apply);
            dialog.Controls.Add(text); dialog.Controls.Add(actions);
            dialog.AcceptButton = close; dialog.ShowDialog(this);
        }
    }

    private static void StartPolicyHelper(Form owner, string operation, string ip)
    {
        string action = operation == "apply" ? "allow saved RDP credentials for TERMSRV/" + ip : "remove the local rule created for TERMSRV/" + ip;
        if (MessageBox.Show(owner, "This will " + action + " on this computer. Administrator approval is required. A company Group Policy may replace local settings. Continue?",
            "RDP Credential Policy", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/rdp-policy " + operation + " " + ip)
                { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            if (error.NativeErrorCode != 1223) MessageBox.Show(owner, error.Message, "RDP Credential Policy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
        File.WriteAllText(path, MergeConnectionPreferences(previous, BuildRdpFile(site, screen, false)), Encoding.Unicode);
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

    internal static byte[] UnprotectPassword(string value)
    {
        if (String.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"\A(?:[0-9A-F]{2})+\z"))
            throw new InvalidOperationException("The saved RDP password is invalid. Enter it again and save the site.");
        byte[] encrypted = Enumerable.Range(0, value.Length / 2).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray();
        try { return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidOperationException("Windows could not unlock this saved RDP password. Enter it again and save the site."); }
        finally { Array.Clear(encrypted, 0, encrypted.Length); }
    }

    internal static string BuildRdpFile(RdpSiteProfile site, Size screen, bool includePassword = true)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("full address:s:" + site.Host);
        text.AppendLine("username:s:" + LoginName(site));
        text.AppendLine("screen mode id:i:" + (site.Resolution == "Full screen" ? "2" : "1"));
        Size size = screen;
        Match match = Regex.Match(site.Resolution ?? "", @"^(\d+) x (\d+)$");
        if (match.Success) size = new Size(Int32.Parse(match.Groups[1].Value), Int32.Parse(match.Groups[2].Value));
        else if (site.Resolution != "Full screen") size = new Size(Math.Max(640, screen.Width - 80), Math.Max(480, screen.Height - 120));
        text.AppendLine("desktopwidth:i:" + size.Width); text.AppendLine("desktopheight:i:" + size.Height);
        text.AppendLine("authentication level:i:2"); text.AppendLine("enablecredsspsupport:i:1");
        text.AppendLine("prompt for credentials:i:0");
        text.AppendLine("redirectclipboard:i:1"); text.AppendLine("redirectprinters:i:0");
        if (includePassword && !String.IsNullOrEmpty(site.ProtectedPassword))
        {
            if (!Regex.IsMatch(site.ProtectedPassword, @"\A(?:[0-9A-F]{2})+\z")) throw new InvalidOperationException("The saved password is invalid. Enter it again and save the site.");
            text.AppendLine("password 51:b:" + site.ProtectedPassword);
        }
        return text.ToString();
    }

    internal static string LoginName(RdpSiteProfile site)
    {
        if (String.IsNullOrWhiteSpace(site.User) || site.User.Contains("\\") || site.User.Contains("@")) return site.User;
        return String.IsNullOrWhiteSpace(site.Domain) ? @".\" + site.User : site.Domain + "\\" + site.User;
    }

    internal static void SelfTest()
    {
        List<RdpSiteProfile> data = new List<RdpSiteProfile>(); int saves = 0;
        using (RdpManager manager = new RdpManager(data, delegate { saves++; }, ""))
        {
            manager.name.Text = "Office server"; manager.group.Text = "Office"; manager.host.Text = "192.0.2.10";
            manager.user.Text = @"TEC\technician"; manager.resolution.SelectedItem = "1280 x 720";
            manager.remember.Checked = true; manager.password.Text = "self-test-password"; manager.SaveSite();
            if (saves != 1 || data.Count != 1 || manager.tree.Nodes[0].Text != "Office" || manager.password.Text.Length != 0)
                throw new InvalidOperationException("RDP site saving or folder display failed.");
            RdpSiteProfile site = data[0];
            manager.search.Text = "192.0.2.10";
            if (manager.tree.Nodes.Count != 1 || manager.tree.Nodes[0].Nodes.Count != 1)
                throw new InvalidOperationException("RDP search did not match server address.");
            manager.search.Text = "no-matching-site";
            if (manager.tree.Nodes.Count != 0 || manager.selected != site)
                throw new InvalidOperationException("RDP search lost the selected site.");
            manager.search.Clear();
            manager.ToggleFavorite();
            if (!site.Favorite || manager.tree.Nodes[0].Text != "★ Favorites" || saves != 2)
                throw new InvalidOperationException("RDP favorite was not saved or shown first.");
            site.User = "technician"; site.Domain = "TEC";
            manager.LoadSite(site);
            if (manager.user.Text != @"TEC\technician") throw new InvalidOperationException("Legacy RDP domain login was not shown in full.");
            manager.SaveSite();
            if (data[0].ProtectedPassword != site.ProtectedPassword || data[0].User != @"TEC\technician")
                throw new InvalidOperationException("Legacy RDP login or password was lost when saved.");
            site = data[0];
            string temporaryFolder = Path.Combine(Path.GetTempPath(), "TEC-Rdp-Test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (RdpManager fileManager = new RdpManager(data, delegate { }, temporaryFolder))
                {
                    string path = fileManager.WriteConnectionFile(site, new Size(1920, 1080));
                    if (File.ReadAllText(path, Encoding.Unicode).Contains("password 51:"))
                        throw new InvalidOperationException("Saved RDP connection file retained a password blob instead of using Windows Credential Manager.");
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
            byte[] plain = UnprotectPassword(site.ProtectedPassword);
            try { if (Encoding.Unicode.GetString(plain) != "self-test-password") throw new InvalidOperationException("RDP password encryption round-trip failed."); }
            finally { Array.Clear(plain, 0, plain.Length); }
            RdpSiteProfile credentialTest = new RdpSiteProfile { Host = "selftest-" + Guid.NewGuid().ToString("N") + ".invalid",
                User = "selftest", ProtectedPassword = site.ProtectedPassword };
            try
            {
                RdpCredentials.Store(credentialTest);
                if (!RdpCredentials.Describe(credentialTest).Contains("Generic credential: toolkit entry matches the saved account and password."))
                    throw new InvalidOperationException("RDP credential did not survive read-back.");
            }
            finally { RdpCredentials.RemoveIfOwned(credentialTest); }
            RdpCredentials.SelfTestPreserveWindowsEntry();
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            RdpSiteProfile restored = serializer.Deserialize<RdpSiteProfile>(serializer.Serialize(site));
            if (restored.Id != site.Id || restored.Host != site.Host || restored.Group != "Office" || restored.Resolution != "1280 x 720" || restored.ProtectedPassword != site.ProtectedPassword || !restored.Favorite)
                throw new InvalidOperationException("RDP profile persistence failed.");
            string file = BuildRdpFile(restored, new Size(1920, 1080));
            if (!file.Contains("username:s:TEC\\technician") || !file.Contains("desktopwidth:i:1280") || !file.Contains("desktopheight:i:720") || file.Contains("self-test-password"))
                throw new InvalidOperationException("RDP connection settings failed.");
            if (LoginName(new RdpSiteProfile { User = "localuser" }) != @".\localuser" ||
                LoginName(new RdpSiteProfile { User = @"OTHER\admin" }) != @"OTHER\admin")
                throw new InvalidOperationException("RDP local and qualified account names failed.");
            string policyReport = RdpPolicyDiagnostics.Report(site);
            if (!policyReport.Contains("TERMSRV/192.0.2.10") ||
                policyReport.Contains("self-test-password") || policyReport.Contains(site.ProtectedPassword))
                throw new InvalidOperationException("RDP policy report omitted the target or exposed a password.");
            manager.SaveSite(); if (data[0].Id != site.Id || data[0].ProtectedPassword != site.ProtectedPassword) throw new InvalidOperationException("RDP saved password was lost on edit.");
            manager.host.Text = "192.0.2.11"; manager.SaveSite();
            if (data[0].ProtectedPassword.Length != 0) throw new InvalidOperationException("RDP credentials carried over to a different server.");
            manager.password.Text = "self-test-password"; manager.SaveSite();
            manager.remember.Checked = false; manager.SaveSite();
            if (data[0].ProtectedPassword.Length != 0) throw new InvalidOperationException("RDP password removal failed.");
            manager.resolution.SelectedItem = "Full screen"; manager.SaveSite();
            if (!BuildRdpFile(data[0], new Size(1920, 1080)).Contains("screen mode id:i:2")) throw new InvalidOperationException("RDP full screen selection failed.");
            manager.NewSite(); if (manager.host.Text.Length != 0 || manager.selected != null) throw new InvalidOperationException("RDP new-site fields failed.");
            manager.name.Text = "Zebra site"; manager.group.Text = "Office"; manager.host.Text = "192.0.2.20"; manager.SaveSite();
            manager.sort.SelectedIndex = 1;
            TreeNode office = manager.tree.Nodes.Cast<TreeNode>().First(node => node.Text == "Office");
            if (office.Nodes[0].Text != "Zebra site" || office.Nodes[1].Text != "Office server")
                throw new InvalidOperationException("RDP reverse alphabetical sorting failed.");
            manager.sort.SelectedIndex = 0;
            if (office == manager.tree.Nodes.Cast<TreeNode>().First(node => node.Text == "Office") ||
                manager.tree.Nodes.Cast<TreeNode>().First(node => node.Text == "Office").Nodes[0].Text != "Office server")
                throw new InvalidOperationException("RDP alphabetical sorting failed.");
        }
    }
}

internal static class RdpCredentials
{
    private const int GenericCredential = 1;
    private const int WindowsLogonCredential = 2;
    private const int LocalMachinePersistence = 2;
    private const int NotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        internal int Flags;
        internal int Type;
        [MarshalAs(UnmanagedType.LPWStr)] internal string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Comment;
        internal long LastWritten;
        internal int CredentialBlobSize;
        internal IntPtr CredentialBlob;
        internal int Persist;
        internal int AttributeCount;
        internal IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] internal string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] internal string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    private static string Target(RdpSiteProfile site) { return "TERMSRV/" + site.Host; }
    private static string Owner(RdpSiteProfile site) { return "TEC Systems Field Toolkit site " + site.Id; }

    internal static void SelfTestPreserveWindowsEntry()
    {
        RdpSiteProfile site = new RdpSiteProfile { Id = Guid.NewGuid().ToString("N"),
            Host = "selftest-" + Guid.NewGuid().ToString("N") + ".invalid", User = "selftest",
            ProtectedPassword = RdpManager.ProtectPassword("toolkit-password") };
        byte[] otherPassword = Encoding.Unicode.GetBytes("windows-password");
        IntPtr blob = Marshal.AllocHGlobal(otherPassword.Length);
        try
        {
            Marshal.Copy(otherPassword, 0, blob, otherPassword.Length);
            Credential other = new Credential { Type = GenericCredential, TargetName = Target(site),
                Comment = "Windows Remote Desktop", CredentialBlobSize = otherPassword.Length,
                CredentialBlob = blob, Persist = LocalMachinePersistence, UserName = RdpManager.LoginName(site) };
            if (!CredWrite(ref other, 0)) throw new InvalidOperationException("Could not create a temporary Windows credential for the RDP self-test.");
            Store(site);
            if (!HasOtherGenericCredential(site)) throw new InvalidOperationException("Connect overwrote a Windows-saved RDP credential.");
            Store(site, true);
            if (!Describe(site).Contains("Generic credential: toolkit entry matches the saved account and password."))
                throw new InvalidOperationException("Save Site did not replace the Windows RDP credential.");
        }
        finally
        {
            CredDelete(Target(site), GenericCredential, 0);
            Marshal.Copy(new byte[otherPassword.Length], 0, blob, otherPassword.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(otherPassword, 0, otherPassword.Length);
        }
    }

    internal static string Describe(RdpSiteProfile site)
    {
        if (String.IsNullOrEmpty(site.ProtectedPassword)) return "No password is saved for this site in the toolkit.";
        string logon = DescribeType(site, WindowsLogonCredential, "Windows logon");
        string legacy = DescribeType(site, GenericCredential, "Generic");
        return logon + " " + legacy;
    }

    private static bool HasOtherGenericCredential(RdpSiteProfile site)
    {
        IntPtr pointer;
        if (!CredRead(Target(site), GenericCredential, 0, out pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return false;
            throw new InvalidOperationException("Windows Credential Manager could not check this RDP entry (error " + error + ").");
        }
        try
        {
            Credential stored = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
            return !String.Equals(stored.Comment, Owner(site), StringComparison.Ordinal);
        }
        finally { CredFree(pointer); }
    }

    internal static bool HasConflictingWindowsLogon(RdpSiteProfile site)
    {
        IntPtr pointer;
        if (!CredRead(Target(site), WindowsLogonCredential, 0, out pointer)) return false;
        try
        {
            Credential stored = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
            return !String.Equals(stored.Comment, Owner(site), StringComparison.Ordinal);
        }
        finally { CredFree(pointer); }
    }

    internal static void RemoveConflictingWindowsLogon(RdpSiteProfile site)
    {
        if (!HasConflictingWindowsLogon(site)) return;
        if (!CredDelete(Target(site), WindowsLogonCredential, 0))
            throw new InvalidOperationException("Windows could not remove the conflicting credential (error " + Marshal.GetLastWin32Error() + ").");
    }

    private static string DescribeType(RdpSiteProfile site, int type, string label)
    {
        IntPtr pointer;
        if (!CredRead(Target(site), type, 0, out pointer))
        {
            int error = Marshal.GetLastWin32Error();
            return error == NotFound ? label + " credential: not found." : label + " credential: could not be checked (error " + error + ").";
        }
        try
        {
            Credential stored = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
            if (!String.Equals(stored.Comment, Owner(site), StringComparison.Ordinal))
                return label + " credential: another Windows entry exists for this IP (account: " +
                    (stored.UserName ?? "").Replace("\r", "").Replace("\n", "") + ").";
            bool accountMatches = String.Equals(stored.UserName, RdpManager.LoginName(site), StringComparison.OrdinalIgnoreCase);
            byte[] plain = RdpManager.UnprotectPassword(site.ProtectedPassword);
            try
            {
                bool passwordMatches = stored.CredentialBlobSize == plain.Length && stored.CredentialBlob != IntPtr.Zero;
                if (passwordMatches)
                {
                    byte[] storedPassword = new byte[plain.Length];
                    try
                    {
                        Marshal.Copy(stored.CredentialBlob, storedPassword, 0, storedPassword.Length);
                        passwordMatches = storedPassword.SequenceEqual(plain);
                    }
                    finally { Array.Clear(storedPassword, 0, storedPassword.Length); }
                }
                return label + " credential: toolkit entry " + (accountMatches && passwordMatches ?
                    "matches the saved account and password." : "does not match the currently saved account or password.");
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        finally { CredFree(pointer); }
    }

    internal static void Store(RdpSiteProfile site, bool replaceWindowsCredential = false)
    {
        // MSTSC may save the password entered after a failed logon. Keep that Windows entry
        // unless the technician explicitly enters a new password and saves this site.
        if (!replaceWindowsCredential && HasOtherGenericCredential(site)) return;
        byte[] plain = RdpManager.UnprotectPassword(site.ProtectedPassword);
        IntPtr blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocHGlobal(plain.Length);
            Marshal.Copy(plain, 0, blob, plain.Length);
            Credential credential = new Credential { Type = GenericCredential, TargetName = Target(site), Comment = Owner(site),
                CredentialBlobSize = plain.Length, CredentialBlob = blob, Persist = LocalMachinePersistence,
                UserName = RdpManager.LoginName(site) };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("Windows Credential Manager could not save this RDP password (error " + Marshal.GetLastWin32Error() + "). Enter the password again and save the site.");
            RemoveIfOwned(site, WindowsLogonCredential);
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                Marshal.Copy(new byte[plain.Length], 0, blob, plain.Length);
                Marshal.FreeHGlobal(blob);
            }
            Array.Clear(plain, 0, plain.Length);
        }
    }

    internal static void RemoveIfOwned(RdpSiteProfile site)
    {
        RemoveIfOwned(site, WindowsLogonCredential);
        RemoveIfOwned(site, GenericCredential);
    }

    private static void RemoveIfOwned(RdpSiteProfile site, int type)
    {
        IntPtr pointer;
        if (!CredRead(Target(site), type, 0, out pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return;
            throw new InvalidOperationException("Windows Credential Manager could not check the old RDP credential (error " + Marshal.GetLastWin32Error() + ").");
        }
        bool owned;
        try { owned = String.Equals(((Credential)Marshal.PtrToStructure(pointer, typeof(Credential))).Comment, Owner(site), StringComparison.Ordinal); }
        finally { CredFree(pointer); }
        if (owned && !CredDelete(Target(site), type, 0))
            throw new InvalidOperationException("Windows Credential Manager could not remove the old RDP credential (error " + Marshal.GetLastWin32Error() + ").");
    }
}

internal static partial class RdpPolicyDiagnostics
{
    private const string DelegationPath = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";
    private const string TerminalServicesPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    private static int? ReadFlag(RegistryKey root, string path, string name)
    {
        using (RegistryKey key = root.OpenSubKey(path))
        {
            object value = key == null ? null : key.GetValue(name);
            if (value == null) return null;
            try { return Convert.ToInt32(value); }
            catch (FormatException) { return null; }
        }
    }

    private static string[] ReadTargets(RegistryKey root, string path)
    {
        using (RegistryKey key = root.OpenSubKey(path))
            return key == null ? new string[0] : key.GetValueNames().Select(name => Convert.ToString(key.GetValue(name)))
                .Where(value => !String.IsNullOrWhiteSpace(value)).ToArray();
    }

    private static bool MatchesTarget(string rule, string target)
    {
        rule = (rule ?? "").Trim();
        if (rule.EndsWith("*", StringComparison.Ordinal))
            return target.StartsWith(rule.Substring(0, rule.Length - 1), StringComparison.OrdinalIgnoreCase);
        return String.Equals(rule, target, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Report(RdpSiteProfile site)
    {
        string target = "TERMSRV/" + site.Host;
        IPAddress address;
        bool byIp = IPAddress.TryParse(site.Host, out address) && address.AddressFamily == AddressFamily.InterNetwork;
        StringBuilder report = new StringBuilder();
        report.AppendLine("RDP SAVED-CREDENTIAL CHECK");
        report.AppendLine("Site: " + site.Name);
        report.AppendLine("Connection target: " + target);
        report.AppendLine("Account sent to Remote Desktop: " + RdpManager.LoginName(site));
        report.AppendLine("Signed-in Windows domain: " + Environment.UserDomainName);
        report.AppendLine("Connection by IP: " + (byIp ? "Yes; NTLM-only credential delegation may apply." : "No"));
        report.AppendLine("Saved credential: " + RdpCredentials.Describe(site));
        report.AppendLine("A matching saved credential only matches the toolkit profile; it does not prove the remote PC accepts that password.");
        report.AppendLine("If Windows saves a corrected password after a prompt, Connect keeps that Windows entry. Enter a new password and Save Site to replace it intentionally.");
        report.AppendLine("A missing NTLM-only policy entry alone does not explain a failed logon. Compare a working site.");
        report.AppendLine();
        report.AppendLine("APPLIED CLIENT POLICY (read-only)");
        try
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default))
            {
                int? standard = ReadFlag(root, DelegationPath, "AllowSavedCredentials");
                string[] standardTargets = ReadTargets(root, DelegationPath + @"\AllowSavedCredentials");
                int? standardDenied = ReadFlag(root, DelegationPath, "DenySavedCredentials");
                string[] standardDeniedTargets = ReadTargets(root, DelegationPath + @"\DenySavedCredentials");
                int? allowed = ReadFlag(root, DelegationPath, "AllowSavedCredentialsWhenNTLMOnly");
                string[] allowedTargets = ReadTargets(root, DelegationPath + @"\AllowSavedCredentialsWhenNTLMOnly");
                int? denied = ReadFlag(root, DelegationPath, "DenySavedCredentialsWhenNTLMOnly");
                string[] deniedTargets = ReadTargets(root, DelegationPath + @"\DenySavedCredentialsWhenNTLMOnly");
                int? blockedSaving = ReadFlag(root, TerminalServicesPath, "DisablePasswordSaving");
                report.AppendLine("Saved credential delegation: " + (standard == 1 ? "Enabled" : standard == 0 ? "Disabled" : "No explicit setting found"));
                report.AppendLine("Matching standard allow rule: " + (standard == 1 && standardTargets.Any(rule => MatchesTarget(rule, target)) ? "Yes" : "No"));
                report.AppendLine("Matching standard deny rule: " + (standardDenied == 1 && standardDeniedTargets.Any(rule => MatchesTarget(rule, target)) ? "Yes - ask IT to review" : "No explicit match found"));
                report.AppendLine("NTLM-only saved credential delegation: " + (allowed == 1 ? "Enabled" : allowed == 0 ? "Disabled" : "No explicit setting found"));
                report.AppendLine("Exact target listed: " + (allowedTargets.Any(rule => String.Equals(rule.Trim(), target, StringComparison.OrdinalIgnoreCase)) ? "Yes" : "No"));
                report.AppendLine("Matching allow rule: " + (allowed == 1 && allowedTargets.Any(rule => MatchesTarget(rule, target)) ? "Yes" : "No"));
                report.AppendLine("Matching deny rule: " + (denied == 1 && deniedTargets.Any(rule => MatchesTarget(rule, target)) ? "Yes - ask IT to review" : "No explicit match found"));
                report.AppendLine("Password saving blocked by client policy: " + (blockedSaving == 1 ? "Yes - ask IT to review" : "No explicit block found"));
                if (standardTargets.Concat(allowedTargets).Any(rule => String.Equals(rule.Trim(), site.Host, StringComparison.OrdinalIgnoreCase)))
                    report.AppendLine("Warning: a policy list contains the bare IP. Use " + target + " instead; the IP alone is not an RDP delegation target.");
            }
        }
        catch (Exception error) { report.AppendLine("Policy could not be read: " + error.Message); }
        report.AppendLine();
        report.AppendLine("REQUEST FOR COMPANY IT");
        report.AppendLine("If Windows blocks saved credentials, review the domain Group Policy at:");
        report.AppendLine("Computer Configuration > Administrative Templates > System > Credentials Delegation");
        report.AppendLine("  Allow delegating saved credentials");
        report.AppendLine("  Allow delegating saved credentials with NTLM-only server authentication");
        report.AppendLine("  Approved target: " + target);
        report.AppendLine("Also review 'Do not allow passwords to be saved' on the laptop and");
        report.AppendLine("'Always prompt for password upon connection' on the remote PC.");
        report.AppendLine("If Windows says 'Your credentials did not work', verify the saved site password");
        report.AppendLine("and remote local account before changing credential-delegation policy.");
        report.AppendLine("A domain policy can replace a local registry setting. The optional Allow this IP button changes only a local rule for this exact target.");
        report.AppendLine("No password or credential secret is included in this report.");
        return report.ToString();
    }
}
