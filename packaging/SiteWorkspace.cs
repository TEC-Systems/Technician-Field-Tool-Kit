using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class SiteWorkspaceProfile
{
    public string Id = Guid.NewGuid().ToString("N");
    public string Name = "";
    public string IpProfileName = "";
    public string Notes = "";
    public List<SiteDevice> Devices = new List<SiteDevice>();
    public List<string> RdpIds = new List<string>();
    public override string ToString() { return Name; }
}

internal sealed class SiteDevice
{
    public string Name = "";
    public string Host = "";
}

internal sealed class IpChangeRecord
{
    public string TimeUtc = "";
    public string AdapterGuid = "";
    public string AdapterName = "";
    public string Action = "";
    public string Before = "";
    public string After = "";
    public string Result = "";
    public string Details = "";
}

internal sealed class SiteCheckResult
{
    public string Name = "";
    public string Host = "";
    public string Ping = "";
    public string Dns = "";
    public string Rdp = "";
    public string Web = "";
}

internal sealed partial class ToolkitWindow
{
    private readonly List<SiteWorkspaceProfile> workspaces = new List<SiteWorkspaceProfile>();
    private readonly List<IpChangeRecord> ipHistory = new List<IpChangeRecord>();
    private readonly List<SiteCheckResult> siteResults = new List<SiteCheckResult>();
    private SiteWorkspaceProfile selectedWorkspace;
    private ListBox workspaceList;
    private TextBox workspaceName, workspaceNotes, workspaceDevices;
    private ComboBox workspaceIpProfile;
    private CheckedListBox workspaceRdp;
    private ListView workspaceChecks;
    private Label workspaceStatus;
    private Button workspaceCheckButton;
    private bool loadingWorkspace;
    private string siteCheckedUtc = "";
    private string siteCheckedId = "";

    private static IEnumerable<IDictionary<string, object>> Rows(object value)
    {
        IEnumerable entries = value as IEnumerable;
        if (entries == null || value is string) yield break;
        foreach (object entry in entries)
        {
            IDictionary<string, object> row = entry as IDictionary<string, object>;
            if (row != null) yield return row;
        }
    }

    private static List<string> Strings(object value)
    {
        IEnumerable entries = value as IEnumerable;
        if (entries == null || value is string) return new List<string>();
        return entries.Cast<object>().Select(Convert.ToString).Where(x => !String.IsNullOrWhiteSpace(x)).ToList();
    }

    private static SiteWorkspaceProfile ParseWorkspace(IDictionary<string, object> row)
    {
        SiteWorkspaceProfile site = new SiteWorkspaceProfile { Id = RdpManager.ValidProfileId(Value(row, "Id")),
            Name = Value(row, "Name").Trim(), IpProfileName = Value(row, "IpProfileName"), Notes = Value(row, "Notes") };
        object value;
        if (row.TryGetValue("Devices", out value)) foreach (IDictionary<string, object> device in Rows(value))
        {
            string host = Value(device, "Host").Trim();
            if (ValidTarget(host) && site.Devices.Count < 50)
                site.Devices.Add(new SiteDevice { Name = Value(device, "Name").Trim(), Host = host });
        }
        if (row.TryGetValue("RdpIds", out value)) site.RdpIds = Strings(value).Take(50).ToList();
        return site;
    }

    private static Dictionary<string, object> WorkspaceData(SiteWorkspaceProfile site)
    {
        return new Dictionary<string, object> { { "Id", site.Id }, { "Name", site.Name }, { "IpProfileName", site.IpProfileName },
            { "Notes", site.Notes }, { "Devices", site.Devices.Select(d => new Dictionary<string, object> { { "Name", d.Name }, { "Host", d.Host } }).ToArray() },
            { "RdpIds", site.RdpIds.ToArray() } };
    }

    private void LoadWorkspaceSettings()
    {
        object saved;
        if (settings.TryGetValue("Workspaces", out saved)) foreach (IDictionary<string, object> row in Rows(saved))
        {
            SiteWorkspaceProfile site = ParseWorkspace(row);
            if (site.Name.Length > 0 && !workspaces.Any(s => s.Name.Equals(site.Name, StringComparison.OrdinalIgnoreCase))) workspaces.Add(site);
        }
        if (settings.TryGetValue("IpChangeHistory", out saved)) foreach (IDictionary<string, object> row in Rows(saved))
        {
            ipHistory.Add(new IpChangeRecord { TimeUtc = Value(row, "TimeUtc"), AdapterGuid = Value(row, "AdapterGuid"),
                AdapterName = Value(row, "AdapterName"), Action = Value(row, "Action"), Before = Value(row, "Before"),
                After = Value(row, "After"), Result = Value(row, "Result"), Details = Value(row, "Details") });
        }
        if (ipHistory.Count > 100) ipHistory.RemoveRange(0, ipHistory.Count - 100);
    }

    private void SaveWorkspaceSettings()
    {
        settings["Workspaces"] = workspaces.Select(WorkspaceData).ToArray();
        settings["IpChangeHistory"] = ipHistory.Select(h => new Dictionary<string, object> { { "TimeUtc", h.TimeUtc },
            { "AdapterGuid", h.AdapterGuid }, { "AdapterName", h.AdapterName }, { "Action", h.Action },
            { "Before", h.Before }, { "After", h.After }, { "Result", h.Result }, { "Details", h.Details } }).ToArray();
    }

    private void BuildSiteWorkspace(TabPage page)
    {
        SplitContainer layout = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(800, 600), SplitterDistance = 180, SplitterWidth = 2, Panel1MinSize = 145, Panel2MinSize = 360 };
        page.Controls.Add(layout);
        layout.Panel1.Padding = new Padding(10);
        workspaceList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        workspaceList.SelectedIndexChanged += delegate { if (!loadingWorkspace) SelectWorkspace(workspaceList.SelectedItem as SiteWorkspaceProfile); };
        layout.Panel1.Controls.Add(workspaceList);
        Label heading = new Label { Text = "Sites", Dock = DockStyle.Top, Height = 27, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        layout.Panel1.Controls.Add(heading);
        FlowLayoutPanel leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 112 };
        layout.Panel1.Controls.Add(leftButtons);
        B(leftButtons, "New", 0, 0, 74, NewWorkspace, "Create a site workspace.", Cobalt);
        B(leftButtons, "Delete", 0, 0, 74, DeleteWorkspace, "Delete this workspace only.", Slate);
        B(leftButtons, "Refresh Links", 0, 0, 130, ReloadWorkspaceChoices, "Update the IP and RDP profile choices after changes in other tabs.", Slate);
        Panel editor = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        layout.Panel2.Controls.Add(editor);
        TableLayoutPanel fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 10, 10) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.Controls.Add(fields);
        workspaceName = new TextBox(); AddWorkspaceRow(fields, "Site name", workspaceName);
        workspaceIpProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; AddWorkspaceRow(fields, "IP profile", workspaceIpProfile);
        workspaceRdp = new CheckedListBox { Height = 85, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle }; AddWorkspaceRow(fields, "RDP sites", workspaceRdp);
        workspaceDevices = new TextBox { Multiline = true, Height = 78, ScrollBars = ScrollBars.Vertical };
        AddWorkspaceRow(fields, "Devices", workspaceDevices);
        AddWorkspaceRow(fields, "", new Label { Text = "One device per line: Name | hostname or IP. RDP sites are checked separately.", AutoSize = true, MaximumSize = new Size(380, 0) });
        workspaceNotes = new TextBox { Multiline = true, Height = 76, ScrollBars = ScrollBars.Vertical };
        AddWorkspaceRow(fields, "Notes", workspaceNotes);
        FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        B(buttons, "Save Site", 0, 0, 95, SaveWorkspace, "Save site links, devices, and notes.", Green);
        B(buttons, "Load IP", 0, 0, 82, OpenWorkspaceIpProfile, "Load the linked IP profile in IP Shifter without applying it.", Slate);
        B(buttons, "Open RDP", 0, 0, 90, OpenWorkspaceRdp, "Open the selected linked RDP connection.", Slate);
        B(buttons, "Check Connections", 0, 0, 132, CheckWorkspace, "Test ping, DNS, RDP, and web ports for saved targets.", Cobalt);
        workspaceCheckButton = buttons.Controls.OfType<Button>().Last();
        B(buttons, "Export Site", 0, 0, 105, ExportWorkspace, "Export this site without saved passwords.", Slate);
        B(buttons, "Import Site", 0, 0, 105, ImportWorkspace, "Import a password-free site file.", Slate);
        B(buttons, "Export Report", 0, 0, 115, ExportWorkspaceReport, "Save a diagnostic text report.", Green);
        AddWorkspaceRow(fields, "", buttons);
        workspaceStatus = new Label { Text = "Select a site or create one.", AutoSize = true, MaximumSize = new Size(390, 0) };
        AddWorkspaceRow(fields, "", workspaceStatus);
        workspaceChecks = new ListView { Height = 150, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.FixedSingle };
        workspaceChecks.Columns.Add("Target", 150); workspaceChecks.Columns.Add("Ping", 70); workspaceChecks.Columns.Add("DNS", 70);
        workspaceChecks.Columns.Add("RDP", 70); workspaceChecks.Columns.Add("Web", 90);
        AddWorkspaceRow(fields, "Results", workspaceChecks);
        ReloadWorkspaceChoices();
    }

    private static void AddWorkspaceRow(TableLayoutPanel panel, string caption, Control control)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 8, 4, 8) }, 0, row);
        control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 4, 0, 8);
        panel.Controls.Add(control, 1, row);
    }

    private void ReloadWorkspaceChoices()
    {
        if (workspaceList == null) return;
        loadingWorkspace = true;
        workspaceList.Items.Clear();
        foreach (SiteWorkspaceProfile site in workspaces.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) workspaceList.Items.Add(site);
        workspaceList.SelectedItem = selectedWorkspace;
        workspaceIpProfile.Items.Clear(); workspaceIpProfile.Items.Add("(none)");
        foreach (ToolkitProfile profile in profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) workspaceIpProfile.Items.Add(profile.Name);
        workspaceRdp.Items.Clear();
        foreach (RdpSiteProfile site in rdpSites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) workspaceRdp.Items.Add(site);
        loadingWorkspace = false;
        LoadWorkspaceEditor(selectedWorkspace);
    }

    private void SelectWorkspace(SiteWorkspaceProfile site)
    {
        selectedWorkspace = site;
        siteResults.Clear(); siteCheckedUtc = ""; siteCheckedId = ""; workspaceChecks.Items.Clear();
        LoadWorkspaceEditor(site);
    }

    private void LoadWorkspaceEditor(SiteWorkspaceProfile site)
    {
        workspaceName.Text = site == null ? "" : site.Name;
        workspaceNotes.Text = site == null ? "" : site.Notes;
        workspaceDevices.Text = site == null ? "" : String.Join(Environment.NewLine, site.Devices.Select(d => d.Name.Length == 0 ? d.Host : d.Name + " | " + d.Host).ToArray());
        workspaceIpProfile.SelectedItem = site != null && workspaceIpProfile.Items.Contains(site.IpProfileName) ? site.IpProfileName : "(none)";
        for (int i = 0; i < workspaceRdp.Items.Count; i++)
        {
            RdpSiteProfile rdp = (RdpSiteProfile)workspaceRdp.Items[i];
            workspaceRdp.SetItemChecked(i, site != null && site.RdpIds.Contains(rdp.Id));
        }
        workspaceStatus.Text = site == null ? "Enter a site name, then save." : "Saved site: " + site.Name;
    }

    private void NewWorkspace()
    {
        loadingWorkspace = true; workspaceList.ClearSelected(); loadingWorkspace = false;
        SelectWorkspace(null); workspaceName.Focus();
    }

    private SiteWorkspaceProfile ReadWorkspaceEditor()
    {
        string name = workspaceName.Text.Trim();
        if (name.Length == 0 || name.Length > 100 || name.Any(Char.IsControl)) throw new InvalidOperationException("Enter a site name of up to 100 characters.");
        if (workspaces.Any(s => !Object.ReferenceEquals(s, selectedWorkspace) && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A site workspace with that name already exists.");
        if (workspaceNotes.Text.Length > 10000) throw new InvalidOperationException("Site notes are limited to 10,000 characters.");
        SiteWorkspaceProfile site = new SiteWorkspaceProfile { Id = selectedWorkspace == null ? Guid.NewGuid().ToString("N") : selectedWorkspace.Id,
            Name = name, IpProfileName = Convert.ToString(workspaceIpProfile.SelectedItem) == "(none)" ? "" : Convert.ToString(workspaceIpProfile.SelectedItem),
            Notes = workspaceNotes.Text };
        foreach (string raw in workspaceDevices.Lines.Where(x => !String.IsNullOrWhiteSpace(x)))
        {
            string[] parts = raw.Split(new[] { '|' }, 2);
            string host = parts[parts.Length - 1].Trim();
            if (!ValidTarget(host)) throw new InvalidOperationException("Invalid device host: " + host);
            if (site.Devices.Count >= 50) throw new InvalidOperationException("A site can have up to 50 devices.");
            site.Devices.Add(new SiteDevice { Name = parts.Length == 2 ? parts[0].Trim() : "", Host = host });
        }
        foreach (object item in workspaceRdp.CheckedItems) site.RdpIds.Add(((RdpSiteProfile)item).Id);
        return site;
    }

    private void SaveWorkspace()
    {
        SiteWorkspaceProfile edited = ReadWorkspaceEditor();
        int index = selectedWorkspace == null ? -1 : workspaces.IndexOf(selectedWorkspace);
        SiteWorkspaceProfile previous = selectedWorkspace;
        if (index < 0) workspaces.Add(edited); else workspaces[index] = edited;
        selectedWorkspace = edited;
        try { SaveSettings(); }
        catch { if (index < 0) workspaces.Remove(edited); else workspaces[index] = previous; selectedWorkspace = previous; throw; }
        ReloadWorkspaceChoices(); workspaceStatus.Text = "Saved site: " + edited.Name;
        Log("Sites", "OK", "Saved workspace: " + edited.Name);
    }

    private void DeleteWorkspace()
    {
        if (selectedWorkspace == null) throw new InvalidOperationException("Select a saved site first.");
        if (MessageBox.Show(this, "Delete workspace '" + selectedWorkspace.Name + "'? IP and RDP profiles will remain saved.", "Delete Site", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        workspaces.Remove(selectedWorkspace); selectedWorkspace = null; SaveSettings(); ReloadWorkspaceChoices();
        siteResults.Clear(); workspaceChecks.Items.Clear(); siteCheckedUtc = ""; siteCheckedId = "";
    }

    private void OpenWorkspaceIpProfile()
    {
        if (selectedWorkspace == null || selectedWorkspace.IpProfileName.Length == 0) throw new InvalidOperationException("Link an IP profile to this site first.");
        ListViewItem item = profileView.Items.Cast<ListViewItem>().FirstOrDefault(row => ((ToolkitProfile)row.Tag).Name.Equals(selectedWorkspace.IpProfileName, StringComparison.OrdinalIgnoreCase));
        if (item == null) throw new InvalidOperationException("The linked IP profile was removed. Refresh the site links and choose another profile.");
        foreach (ListViewItem row in profileView.SelectedItems.Cast<ListViewItem>().ToArray()) row.Selected = false;
        item.Selected = true;
        LoadProfile(); tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().First(page => page.Text == "IP Shifter");
    }

    private void OpenWorkspaceRdp()
    {
        if (selectedWorkspace == null) throw new InvalidOperationException("Select a saved site first.");
        RdpSiteProfile rdp = workspaceRdp.SelectedItem as RdpSiteProfile;
        if (rdp == null || !selectedWorkspace.RdpIds.Contains(rdp.Id)) throw new InvalidOperationException("Select a checked RDP connection linked to this site.");
        rdpManager.OpenSavedSite(rdp.Id);
    }

    private static SiteCheckResult TestSiteTarget(string name, string host)
    {
        SiteCheckResult result = new SiteCheckResult { Name = name, Host = host, Ping = "No reply", Dns = "Unresolved", Rdp = "Closed/filtered", Web = "Closed/filtered" };
        try { result.Dns = Dns.GetHostAddresses(host).Any() ? "Resolved" : "Unresolved"; } catch { }
        try { using (Ping ping = new Ping()) { PingReply reply = ping.Send(host, 800); if (reply.Status == IPStatus.Success) result.Ping = reply.RoundtripTime + " ms"; } } catch { }
        if (ProbePort(host, 3389, 700)) result.Rdp = "Open";
        bool https = ProbePort(host, 443, 700), http = ProbePort(host, 80, 700);
        if (https || http) result.Web = https && http ? "80, 443" : https ? "443" : "80";
        return result;
    }

    private void CheckWorkspace()
    {
        if (selectedWorkspace == null) throw new InvalidOperationException("Save and select a site first.");
        SiteWorkspaceProfile site = selectedWorkspace;
        List<SiteDevice> targets = new List<SiteDevice>(site.Devices);
        foreach (RdpSiteProfile rdp in rdpSites.Where(r => site.RdpIds.Contains(r.Id)))
            if (!targets.Any(d => d.Host.Equals(rdp.Host, StringComparison.OrdinalIgnoreCase)))
                targets.Add(new SiteDevice { Name = rdp.Name, Host = rdp.Host });
        if (targets.Count == 0) throw new InvalidOperationException("Add a device or link an RDP site before checking connections.");
        workspaceCheckButton.Enabled = false; workspaceStatus.Text = "Checking " + targets.Count + " target(s)...";
        siteResults.Clear(); workspaceChecks.Items.Clear(); siteCheckedUtc = ""; siteCheckedId = "";
        ThreadPool.QueueUserWorkItem(delegate {
            List<SiteCheckResult> results = targets.Select(t => TestSiteTarget(t.Name.Length == 0 ? t.Host : t.Name, t.Host)).ToList();
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate {
                if (IsDisposed) return;
                workspaceCheckButton.Enabled = true;
                if (selectedWorkspace != site) { workspaceStatus.Text = "Selected site changed; check it again."; return; }
                siteResults.AddRange(results); siteCheckedUtc = DateTime.UtcNow.ToString("u"); siteCheckedId = site.Id;
                foreach (SiteCheckResult result in results)
                {
                    ListViewItem item = new ListViewItem(result.Name + " (" + result.Host + ")");
                    item.SubItems.Add(result.Ping); item.SubItems.Add(result.Dns); item.SubItems.Add(result.Rdp); item.SubItems.Add(result.Web);
                    workspaceChecks.Items.Add(item);
                }
                workspaceStatus.Text = "Checked " + results.Count + " target(s) at " + siteCheckedUtc + ". Closed/filtered ports may be blocked by a firewall.";
                Log("Sites", "OK", "Checked connections for " + site.Name + ".");
            }));
        });
    }

    private static Dictionary<string, object> IpProfileData(ToolkitProfile p)
    {
        return new Dictionary<string, object> { { "Name", p.Name }, { "Adapter", p.Adapter }, { "IPAddress", p.IPAddress },
            { "SubnetMask", p.SubnetMask }, { "Gateway", p.Gateway }, { "Dns1", p.Dns1 }, { "Dns2", p.Dns2 } };
    }

    private static Dictionary<string, object> RdpExportData(RdpSiteProfile p)
    {
        return new Dictionary<string, object> { { "Name", p.Name }, { "Host", p.Host }, { "Group", p.Group },
            { "User", p.User }, { "Domain", p.Domain }, { "Resolution", p.Resolution } };
    }

    private static Dictionary<string, object> BuildSiteExport(SiteWorkspaceProfile site, IEnumerable<ToolkitProfile> ipProfiles, IEnumerable<RdpSiteProfile> rdpProfiles)
    {
        ToolkitProfile ip = ipProfiles.FirstOrDefault(p => p.Name.Equals(site.IpProfileName, StringComparison.OrdinalIgnoreCase));
        return new Dictionary<string, object> { { "Format", "TEC-FieldToolkit-Site" }, { "Version", 1 },
            { "Name", site.Name }, { "Notes", site.Notes },
            { "Devices", site.Devices.Select(d => new Dictionary<string, object> { { "Name", d.Name }, { "Host", d.Host } }).ToArray() },
            { "IpProfile", ip == null ? null : IpProfileData(ip) },
            { "RdpSites", rdpProfiles.Where(p => site.RdpIds.Contains(p.Id)).Select(RdpExportData).ToArray() } };
    }

    private void ExportWorkspace()
    {
        if (selectedWorkspace == null) throw new InvalidOperationException("Select a saved site first.");
        string content = json.Serialize(BuildSiteExport(selectedWorkspace, profiles, rdpSites));
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Site files (*.json)|*.json", FileName = "TEC-Site-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" })
            if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(dialog.FileName, content, Encoding.UTF8); Log("Sites", "OK", "Exported password-free site: " + selectedWorkspace.Name); }
    }

    private static string UniqueName(string requested, IEnumerable<string> existing)
    {
        HashSet<string> names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested)) return requested;
        int number = 2; while (names.Contains(requested + " (" + number + ")")) number++;
        return requested + " (" + number + ")";
    }

    private void ImportWorkspace()
    {
        using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Site files (*.json)|*.json", CheckFileExists = true })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidOperationException("Site file exceeds the 1 MB limit.");
            IDictionary<string, object> data = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(dialog.FileName));
            if (data == null || Value(data, "Format") != "TEC-FieldToolkit-Site" || Value(data, "Version") != "1")
                throw new InvalidOperationException("This is not a supported TEC site export.");
            string name = Value(data, "Name").Trim();
            if (name.Length == 0 || name.Length > 100 || name.Any(Char.IsControl)) throw new InvalidOperationException("The site name is invalid.");
            SiteWorkspaceProfile site = new SiteWorkspaceProfile { Name = UniqueName(name, workspaces.Select(s => s.Name)), Notes = Value(data, "Notes") };
            if (site.Notes.Length > 10000) throw new InvalidOperationException("Site notes exceed the limit.");
            object value;
            if (data.TryGetValue("Devices", out value)) foreach (IDictionary<string, object> row in Rows(value))
            {
                string host = Value(row, "Host").Trim();
                if (!ValidTarget(host) || site.Devices.Count >= 50) throw new InvalidOperationException("The site file has an invalid device address or too many devices.");
                site.Devices.Add(new SiteDevice { Name = Value(row, "Name").Trim(), Host = host });
            }
            ToolkitProfile ip = null;
            if (data.TryGetValue("IpProfile", out value) && value is IDictionary<string, object>)
            {
                IDictionary<string, object> row = (IDictionary<string, object>)value;
                string ipName = Value(row, "Name").Trim();
                if (ipName.Length == 0 || ipName.Length > 100) throw new InvalidOperationException("The IP profile name is invalid.");
                ip = new ToolkitProfile { Name = UniqueName(ipName, profiles.Select(p => p.Name)), Adapter = Value(row, "Adapter"),
                    IPAddress = ValidateIpv4(Value(row, "IPAddress"), "Imported IP", true), SubnetMask = ValidateIpv4(Value(row, "SubnetMask"), "Imported mask", true),
                    Gateway = ValidateIpv4(Value(row, "Gateway"), "Imported gateway", true), Dns1 = ValidateIpv4(Value(row, "Dns1"), "Imported DNS 1", true),
                    Dns2 = ValidateIpv4(Value(row, "Dns2"), "Imported DNS 2", true) };
                site.IpProfileName = ip.Name;
            }
            List<RdpSiteProfile> importedRdp = new List<RdpSiteProfile>();
            if (data.TryGetValue("RdpSites", out value)) foreach (IDictionary<string, object> row in Rows(value))
            {
                string host = Value(row, "Host").Trim(), rdpName = Value(row, "Name").Trim();
                if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$") || rdpName.Length == 0 || rdpName.Length > 100 || importedRdp.Count >= 50)
                    throw new InvalidOperationException("The site file has an invalid RDP connection or too many connections.");
                if (new[] { rdpName, Value(row, "Group"), Value(row, "User"), Value(row, "Domain") }.Any(x => x.Any(Char.IsControl)))
                    throw new InvalidOperationException("The site file contains invalid RDP text.");
                string unique = UniqueName(rdpName, rdpSites.Select(p => p.Name).Concat(importedRdp.Select(p => p.Name)));
                string resolution = Value(row, "Resolution");
                if (resolution != "Full screen" && resolution != "Fit to screen" &&
                    !new[] { "1024 x 768", "1280 x 720", "1366 x 768", "1600 x 900", "1920 x 1080", "2560 x 1440" }.Contains(resolution))
                    resolution = "Full screen";
                RdpSiteProfile rdp = new RdpSiteProfile { Name = unique, Host = host, Group = Value(row, "Group"),
                    User = Value(row, "User"), Domain = Value(row, "Domain"), Resolution = resolution };
                importedRdp.Add(rdp); site.RdpIds.Add(rdp.Id);
            }
            string summary = "Import '" + site.Name + "' with " + site.Devices.Count + " devices, " + importedRdp.Count + " RDP connections, and " + (ip == null ? "no" : "one") + " IP profile? Saved passwords are not included.";
            if (MessageBox.Show(this, summary, "Import Site", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            workspaces.Add(site); if (ip != null) profiles.Add(ip); rdpSites.AddRange(importedRdp);
            try { SaveSettings(); }
            catch { workspaces.Remove(site); if (ip != null) profiles.Remove(ip); foreach (RdpSiteProfile rdp in importedRdp) rdpSites.Remove(rdp); throw; }
            selectedWorkspace = site; ReloadWorkspaceChoices(); RefreshProfiles(); rdpManager.Reload();
            Log("Sites", "OK", "Imported site without passwords: " + site.Name);
        }
    }

    private string WorkspaceReport(SiteWorkspaceProfile site)
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("TEC Systems Field Toolkit - Site Diagnostic Report");
        report.AppendLine("Created (UTC): " + DateTime.UtcNow.ToString("u"));
        report.AppendLine("Site: " + site.Name);
        report.AppendLine("Computer: " + Environment.MachineName);
        report.AppendLine("IP profile: " + (site.IpProfileName.Length == 0 ? "None" : site.IpProfileName));
        report.AppendLine("Notes: " + site.Notes);
        report.AppendLine(); report.AppendLine("Saved devices:");
        foreach (SiteDevice device in site.Devices) report.AppendLine("- " + device.Name + " | " + device.Host);
        report.AppendLine(); report.AppendLine("Linked RDP connections (no credentials):");
        foreach (RdpSiteProfile rdp in rdpSites.Where(r => site.RdpIds.Contains(r.Id))) report.AppendLine("- " + rdp.Name + " | " + rdp.Host);
        report.AppendLine(); report.AppendLine("Current adapters:");
        foreach (ToolkitAdapter adapter in adapters) report.AppendLine("- " + adapter.Name + " | " + adapter.Status + " | " + adapter.IP + " / " + adapter.Mask +
            " | Gateway " + adapter.Gateway + " | DNS " + adapter.Dns + " | MAC " + adapter.Mac);
        report.AppendLine(); report.AppendLine("Connection checks: " + (siteCheckedId == site.Id ? siteCheckedUtc : "Not run for this site"));
        if (siteCheckedId == site.Id) foreach (SiteCheckResult result in siteResults)
            report.AppendLine("- " + result.Name + " (" + result.Host + "): ping " + result.Ping + ", DNS " + result.Dns + ", RDP " + result.Rdp + ", web " + result.Web);
        report.AppendLine(); report.AppendLine("Recent IP changes:");
        foreach (IpChangeRecord entry in ipHistory.TakeLastCompat(10))
            report.AppendLine("- " + entry.TimeUtc + " | " + entry.AdapterName + " | " + entry.Action + " | " + entry.Result + " | " + entry.Before + " -> " + entry.After);
        report.AppendLine(); report.AppendLine("Port checks indicate reachability only; closed or filtered ports can be blocked by a firewall.");
        return report.ToString();
    }

    private void ExportWorkspaceReport()
    {
        if (selectedWorkspace == null) throw new InvalidOperationException("Select a saved site first.");
        RefreshAdapters();
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Text reports (*.txt)|*.txt", FileName = "TEC-Site-Report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
            if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(dialog.FileName, WorkspaceReport(selectedWorkspace), Encoding.UTF8); Log("Sites", "OK", "Exported diagnostic report for " + selectedWorkspace.Name); }
    }

    private void RecordIpChange(ToolkitAdapter adapter, string action, string before, string after, string result, string details)
    {
        ipHistory.Add(new IpChangeRecord { TimeUtc = DateTime.UtcNow.ToString("u"), AdapterGuid = adapter.Guid, AdapterName = adapter.Name,
            Action = action, Before = before, After = after, Result = result, Details = details });
        if (ipHistory.Count > 100) ipHistory.RemoveAt(0);
        try { SaveSettings(); } catch (Exception error) { Log("IP History", "WARN", "Could not save IP history: " + error.Message); }
    }

    private void ShowIpHistory()
    {
        using (Form dialog = new Form { Text = "IP Change History", StartPosition = FormStartPosition.CenterParent, Size = new Size(960, 460), MinimumSize = new Size(700, 300) })
        {
            ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            list.Columns.Add("UTC", 145); list.Columns.Add("Adapter", 130); list.Columns.Add("Action", 105);
            list.Columns.Add("Before", 165); list.Columns.Add("After", 165); list.Columns.Add("Result", 85); list.Columns.Add("Details", 200);
            foreach (IpChangeRecord h in ipHistory.AsEnumerable().Reverse())
            {
                ListViewItem item = new ListViewItem(h.TimeUtc); item.SubItems.Add(h.AdapterName); item.SubItems.Add(h.Action);
                item.SubItems.Add(h.Before); item.SubItems.Add(h.After); item.SubItems.Add(h.Result); item.SubItems.Add(h.Details); list.Items.Add(item);
            }
            dialog.Controls.Add(list);
            dialog.BackColor = dark ? Color.FromArgb(27, 36, 43) : Color.White;
            list.BackColor = dark ? Color.FromArgb(34, 44, 53) : Color.White;
            list.ForeColor = dark ? Color.White : Color.FromArgb(20, 36, 57);
            dialog.ShowDialog(this);
        }
    }

    private void SelfTestSiteWorkspace()
    {
        SiteWorkspaceProfile site = new SiteWorkspaceProfile { Name = "Sample site", IpProfileName = "Sample IP", Notes = "Panel room" };
        site.Devices.Add(new SiteDevice { Name = "Controller", Host = "192.0.2.10" });
        RdpSiteProfile rdp = new RdpSiteProfile { Name = "BMS server", Host = "192.0.2.20", ProtectedPassword = "SENSITIVE-TEST-VALUE" };
        site.RdpIds.Add(rdp.Id);
        ToolkitProfile ip = new ToolkitProfile { Name = "Sample IP", IPAddress = "192.0.2.30", SubnetMask = "255.255.255.0" };
        string exported = json.Serialize(BuildSiteExport(site, new[] { ip }, new[] { rdp }));
        if (exported.Contains("ProtectedPassword") || exported.Contains("SENSITIVE-TEST-VALUE") || !exported.Contains("192.0.2.30") || !exported.Contains("192.0.2.20"))
            throw new InvalidOperationException("Site export omitted settings or exposed a saved password.");
        IDictionary<string, object> source = json.Deserialize<Dictionary<string, object>>(json.Serialize(WorkspaceData(site)));
        SiteWorkspaceProfile loaded = ParseWorkspace(source);
        if (loaded.Name != site.Name || loaded.Devices.Count != 1 || loaded.Devices[0].Host != "192.0.2.10" || loaded.RdpIds.Single() != rdp.Id)
            throw new InvalidOperationException("Site workspace serialization failed.");
        if (UniqueName("Sample site", new[] { "Sample site" }) != "Sample site (2)")
            throw new InvalidOperationException("Imported site name collision handling failed.");
        if (workspaceList != null || tabs.TabPages.Cast<TabPage>().Any(p => p.Text == "Sites") ||
            !tabs.TabPages.Cast<TabPage>().First(p => p.Text == "IP Shifter").Controls.OfType<Button>().Any(b => b.Text == "IP History"))
            throw new InvalidOperationException("Site workspaces must be hidden while IP History remains available.");
    }
}

internal static class SiteEnumerableExtensions
{
    public static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> items, int count)
    {
        Queue<T> tail = new Queue<T>();
        foreach (T item in items) { if (tail.Count == count) tail.Dequeue(); tail.Enqueue(item); }
        return tail;
    }
}
