using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

internal sealed class ToolkitBackupData
{
    public readonly List<ToolkitProfile> IpProfiles = new List<ToolkitProfile>();
    public readonly List<RdpSiteProfile> RdpSites = new List<RdpSiteProfile>();
    public int SkippedDhcp;
}

internal static class ToolkitBackup
{
    private const string Format = "TEC-FieldToolkit-Profiles-1";
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 2097152 };

    internal static string FileName(string username, string hostname, DateTime created)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Func<string, string> safe = value => new string((value ?? "").Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray());
        return safe(username) + " - " + safe(hostname) + " - " + created.ToString("yyyyMMdd-HHmmss") + ".json";
    }

    internal static string Export(IEnumerable<ToolkitProfile> ipProfiles, IEnumerable<RdpSiteProfile> rdpSites, string version)
    {
        return Serializer.Serialize(new Dictionary<string, object> {
            { "BackupFormat", Format }, { "ToolkitVersion", version }, { "CreatedUtc", DateTime.UtcNow.ToString("u") },
            { "Computer", Environment.MachineName }, { "WindowsUser", Environment.UserName },
            { "IpProfiles", ipProfiles.Select(p => new Dictionary<string, object> {
                { "Name", p.Name }, { "Adapter", p.Adapter }, { "IPAddress", p.IPAddress }, { "SubnetMask", p.SubnetMask },
                { "Gateway", p.Gateway }, { "Dns1", p.Dns1 }, { "Dns2", p.Dns2 }
            }).ToArray() },
            { "RdpSites", rdpSites.Select(p => new Dictionary<string, object> {
                { "Name", p.Name }, { "Group", p.Group }, { "Host", p.Host }, { "User", p.User },
                { "Domain", p.Domain }, { "Resolution", p.Resolution }, { "Favorite", p.Favorite }
            }).ToArray() }
        });
    }

    internal static ToolkitBackupData Read(string path)
    {
        if (new FileInfo(path).Length > 2097152) throw new InvalidOperationException("The backup file is too large (limit: 2 MB).");
        if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) return ReadXml(path);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a TEC toolkit JSON backup or an AllProfiles.xml file.");
        return ReadJson(File.ReadAllText(path, Encoding.UTF8));
    }

    private static IEnumerable<IDictionary<string, object>> Rows(IDictionary<string, object> root, string key)
    {
        object value;
        if (!root.TryGetValue(key, out value) || !(value is IEnumerable) || value is string)
            throw new InvalidOperationException("The backup has no valid " + key + " list.");
        foreach (object item in (IEnumerable)value)
        {
            IDictionary<string, object> row = item as IDictionary<string, object>;
            if (row == null) throw new InvalidOperationException("The backup has an invalid " + key + " entry.");
            yield return row;
        }
    }

    private static string Value(IDictionary<string, object> row, string key)
    {
        object value;
        return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value).Trim() : "";
    }

    private static string SafeText(string value, string field)
    {
        if (value.Length > 200 || value.Any(Char.IsControl)) throw new InvalidOperationException("The backup has an invalid " + field + ".");
        return value;
    }

    private static string Address(string value, string field, bool optional)
    {
        if (optional && value.Length == 0) return "";
        IPAddress parsed;
        if (!Regex.IsMatch(value, @"^\d{1,3}(\.\d{1,3}){3}$") || !IPAddress.TryParse(value, out parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The backup has an invalid " + field + ": " + value);
        return value;
    }

    private static ToolkitProfile IpProfile(IDictionary<string, object> row)
    {
        string name = SafeText(Value(row, "Name"), "IP profile name");
        if (name.Length == 0) throw new InvalidOperationException("An IP profile needs a name.");
        return new ToolkitProfile { Name = name, Adapter = SafeText(Value(row, "Adapter"), "adapter name"),
            IPAddress = Address(Value(row, "IPAddress"), "IP address", true),
            SubnetMask = Address(Value(row, "SubnetMask"), "subnet mask", true),
            Gateway = Address(Value(row, "Gateway"), "gateway", true),
            Dns1 = Address(Value(row, "Dns1"), "DNS 1", true), Dns2 = Address(Value(row, "Dns2"), "DNS 2", true) };
    }

    private static ToolkitBackupData ReadJson(string content)
    {
        IDictionary<string, object> root = Serializer.Deserialize<Dictionary<string, object>>(content);
        if (root == null || Value(root, "BackupFormat") != Format)
            throw new InvalidOperationException("This is not a supported TEC toolkit profile backup.");
        ToolkitBackupData data = new ToolkitBackupData();
        foreach (IDictionary<string, object> row in Rows(root, "IpProfiles"))
        {
            if (data.IpProfiles.Count >= 500) throw new InvalidOperationException("The backup contains too many IP profiles.");
            data.IpProfiles.Add(IpProfile(row));
        }
        foreach (IDictionary<string, object> row in Rows(root, "RdpSites"))
        {
            if (data.RdpSites.Count >= 500) throw new InvalidOperationException("The backup contains too many RDP sites.");
            string name = SafeText(Value(row, "Name"), "RDP site name"), host = Value(row, "Host");
            if (name.Length == 0 || !Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$"))
                throw new InvalidOperationException("The backup has an invalid RDP site name or server.");
            bool favorite;
            Boolean.TryParse(Value(row, "Favorite"), out favorite);
            data.RdpSites.Add(new RdpSiteProfile { Name = name, Host = host, Group = SafeText(Value(row, "Group"), "RDP folder"),
                User = SafeText(Value(row, "User"), "RDP username"), Domain = SafeText(Value(row, "Domain"), "RDP domain"),
                Resolution = SafeText(Value(row, "Resolution"), "RDP resolution"), Favorite = favorite, ProtectedPassword = "" });
        }
        return data;
    }

    private static string Child(XmlNode node, string name)
    {
        XmlNode child = node.SelectSingleNode(name);
        return child == null ? "" : child.InnerText.Trim();
    }

    private static ToolkitBackupData ReadXml(string path)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        XmlDocument document = new XmlDocument { XmlResolver = null };
        using (XmlReader reader = XmlReader.Create(path, settings)) document.Load(reader);
        if (document.DocumentElement == null || document.DocumentElement.Name != "AllProfiles")
            throw new InvalidOperationException("This is not an AllProfiles.xml IP profile export.");
        ToolkitBackupData data = new ToolkitBackupData();
        foreach (XmlNode node in document.DocumentElement.SelectNodes("Profile"))
        {
            if (data.IpProfiles.Count + data.SkippedDhcp >= 500) throw new InvalidOperationException("The XML contains too many profiles.");
            if (Child(node, "autoIP") == "1") { data.SkippedDhcp++; continue; }
            Dictionary<string, object> row = new Dictionary<string, object> {
                { "Name", node.Attributes == null || node.Attributes["title"] == null ? "" : node.Attributes["title"].Value },
                { "Adapter", Child(node, "infName") }, { "IPAddress", Child(node, "IP") },
                { "SubnetMask", Child(node, "mask") }, { "Gateway", Child(node, "gateway") },
                { "Dns1", Child(node, "preDns") }, { "Dns2", Child(node, "altDns") }
            };
            data.IpProfiles.Add(IpProfile(row));
        }
        return data;
    }

    internal static void SelfTest()
    {
        if (FileName("technician", "FIELD-LAPTOP", new DateTime(2026, 10, 1, 12, 30, 0)) !=
            "technician - FIELD-LAPTOP - 20261001-123000.json")
            throw new InvalidOperationException("Profile backup filename is missing the username or hostname.");
        ToolkitProfile ip = new ToolkitProfile { Name = "Test site", Adapter = "Ethernet", IPAddress = "192.0.2.10", SubnetMask = "255.255.255.0" };
        RdpSiteProfile rdp = new RdpSiteProfile { Name = "Test RDP", Host = "192.0.2.20", User = "tech", ProtectedPassword = "SECRET" };
        string exported = Export(new[] { ip }, new[] { rdp }, "0.0.0");
        ToolkitBackupData restored = ReadJson(exported);
        if (exported.Contains("SECRET") || exported.Contains("ProtectedPassword") || restored.IpProfiles.Single().IPAddress != ip.IPAddress ||
            restored.RdpSites.Single().ProtectedPassword.Length != 0 || restored.RdpSites.Single().Host != rdp.Host)
            throw new InvalidOperationException("Profile backup lost data or included a saved password.");
        string path = Path.Combine(Path.GetTempPath(), "TEC-Backup-Test-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<AllProfiles><Profile title='MSK'><infName>Ethernet</infName><autoIP>0</autoIP><IP>192.168.0.1</IP><mask>255.255.255.0</mask><gateway/><preDns/><altDns/></Profile></AllProfiles>");
            if (Read(path).IpProfiles.Single().Name != "MSK") throw new InvalidOperationException("AllProfiles XML import failed.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
