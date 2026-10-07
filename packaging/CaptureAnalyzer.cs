using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class CaptureSummary
{
    private readonly Dictionary<string, long> protocols = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> endpoints = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> conversations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> dnsQueries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> dnsErrors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> httpHosts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> httpStatuses = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> tlsNames = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> bacnetErrors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> deviceSources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> arpSources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private long packets;
    private long bytes;
    private long retransmissions;
    private long resets;
    private long icmpPackets;
    private long bacnetPackets;
    private long whoIsPackets;
    private long iAmPackets;
    private long readRequests;
    private long writeRequests;
    private long bacnetFailurePackets;
    private long bacnetBroadcasts;
    private double first = Double.MaxValue;
    private double last;

    private static void Count(Dictionary<string, long> values, string value)
    {
        if (String.IsNullOrWhiteSpace(value)) return;
        long count;
        values.TryGetValue(value, out count);
        values[value] = count + 1;
    }

    private static string Field(string[] values, int index)
    {
        return index < values.Length ? values[index].Trim() : "";
    }

    private static int Code(string value)
    {
        int result;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Int32.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result) ? result : -1;
        return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : -1;
    }

    internal void AddPacket(string line)
    {
        string[] fields = line.Split('|');
        if (fields.Length < 3) return;
        packets++;
        double time;
        if (Double.TryParse(Field(fields, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out time))
        {
            first = Math.Min(first, time);
            last = Math.Max(last, time);
        }
        long length;
        if (Int64.TryParse(Field(fields, 1), out length)) bytes += length;
        Count(protocols, Field(fields, 2));
        string source = Field(fields, 3), destination = Field(fields, 4);
        if (source.Length == 0) source = Field(fields, 5);
        if (destination.Length == 0) destination = Field(fields, 6);
        Count(endpoints, source);
        Count(endpoints, destination);
        string sourcePort = Field(fields, 7), destinationPort = Field(fields, 8);
        string transport = "TCP";
        if (sourcePort.Length == 0 && destinationPort.Length == 0)
        {
            sourcePort = Field(fields, 9); destinationPort = Field(fields, 10); transport = "UDP";
        }
        if (source.Length > 0 && destination.Length > 0)
        {
            string a = source + (sourcePort.Length == 0 ? "" : ":" + sourcePort);
            string b = destination + (destinationPort.Length == 0 ? "" : ":" + destinationPort);
            Count(conversations, transport + " " + (String.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + " ↔ " + b : b + " ↔ " + a));
        }
        Count(dnsQueries, Field(fields, 11));
        string dnsCode = Field(fields, 12);
        if (dnsCode.Length > 0 && dnsCode != "0") Count(dnsErrors, dnsCode);
        Count(httpHosts, Field(fields, 13));
        Count(httpStatuses, Field(fields, 14));
        Count(tlsNames, Field(fields, 15));
        if (Field(fields, 16).Length > 0) retransmissions++;
        if (Field(fields, 17) == "1") resets++;
        if (Field(fields, 18).Length > 0 || Field(fields, 19).Length > 0) icmpPackets++;
        string arpIp = Field(fields, 27), arpMac = Field(fields, 28);
        if (arpIp.Length > 0 && arpMac.Length > 0)
        {
            HashSet<string> macs;
            if (!arpSources.TryGetValue(arpIp, out macs)) arpSources[arpIp] = macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            macs.Add(arpMac);
        }
        string apdu = Field(fields, 20), bvlc = Field(fields, 26);
        if (apdu.Length > 0 || bvlc.Length > 0)
        {
            bacnetPackets++;
            int type = Code(apdu), confirmed = Code(Field(fields, 21)), unconfirmed = Code(Field(fields, 22));
            if (type == 1 && unconfirmed == 8) whoIsPackets++;
            if (type == 1 && unconfirmed == 0)
            {
                iAmPackets++;
                string device = Field(fields, 25);
                if (device.Length > 0 && source.Length > 0)
                {
                    HashSet<string> sources;
                    if (!deviceSources.TryGetValue(device, out sources)) deviceSources[device] = sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    sources.Add(source);
                }
            }
            if (type == 0 && (confirmed == 12 || confirmed == 14)) readRequests++;
            if (type == 0 && (confirmed == 15 || confirmed == 16)) writeRequests++;
            if (type == 5 || type == 6 || type == 7) bacnetFailurePackets++;
            string errorClass = Field(fields, 23), errorCode = Field(fields, 24);
            if (errorClass.Length > 0 || errorCode.Length > 0) Count(bacnetErrors, "class " + errorClass + ", code " + errorCode);
            if (Code(bvlc) == 11) bacnetBroadcasts++;
        }
    }

    private static void Section(StringBuilder report, string title, Dictionary<string, long> values, int limit)
    {
        report.AppendLine().AppendLine(title);
        if (values.Count == 0) { report.AppendLine("  None observed"); return; }
        foreach (KeyValuePair<string, long> entry in values.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(limit))
            report.Append("  ").Append(entry.Value.ToString("N0")).Append("  ").AppendLine(entry.Key);
        if (values.Count > limit) report.AppendLine("  ... and " + (values.Count - limit) + " more");
    }

    private void AppendFindings(StringBuilder report)
    {
        report.AppendLine().AppendLine("AT A GLANCE");
        if (packets == 0) { report.AppendLine("  No packets were found in this file."); return; }
        report.AppendLine("  This file contains " + packets.ToString("N0") + " packets involving " +
            endpoints.Count.ToString("N0") + " IP addresses.");
        KeyValuePair<string, long> busiestProtocol = protocols.OrderByDescending(item => item.Value).FirstOrDefault();
        if (!String.IsNullOrEmpty(busiestProtocol.Key))
            report.AppendLine("  Most common protocol: " + busiestProtocol.Key + " (" + busiestProtocol.Value.ToString("N0") + " packets).");
        KeyValuePair<string, long> busiestConversation = conversations.OrderByDescending(item => item.Value).FirstOrDefault();
        if (!String.IsNullOrEmpty(busiestConversation.Key))
            report.AppendLine("  Busiest conversation: " + busiestConversation.Key + " (" + busiestConversation.Value.ToString("N0") + " packets).");

        report.AppendLine().AppendLine("POSSIBLE TROUBLE SPOTS");
        bool found = false;
        long dnsFailures = dnsErrors.Values.Sum();
        if (dnsFailures > 0)
        {
            report.AppendLine("  DNS replies reported an error " + dnsFailures.ToString("N0") + " times. Check the queried names and DNS server responses below.");
            found = true;
        }
        long httpFailures = httpStatuses.Where(item => item.Key.StartsWith("4") || item.Key.StartsWith("5")).Sum(item => item.Value);
        if (httpFailures > 0)
        {
            report.AppendLine("  HTTP reported " + httpFailures.ToString("N0") + " client or server error responses (4xx/5xx). Review the status codes below.");
            found = true;
        }
        if (retransmissions > 0)
        {
            report.AppendLine("  TCP retransmissions appeared " + retransmissions.ToString("N0") + " times. They can indicate lost or delayed packets; compare both sides of the connection.");
            found = true;
        }
        if (resets > 0)
        {
            report.AppendLine("  TCP connections were reset " + resets.ToString("N0") + " times. A reset can be normal, but repeated resets may explain interrupted sessions.");
            found = true;
        }
        foreach (KeyValuePair<string, HashSet<string>> ip in arpSources.Where(item => item.Value.Count > 1).Take(5))
        {
            report.AppendLine("  Check IP " + ip.Key + ": ARP shows more than one MAC address (" + String.Join(", ", ip.Value.ToArray()) + "). This may be an IP conflict or a legitimate failover/proxy setup.");
            found = true;
        }
        if (!found) report.AppendLine("  No DNS errors, HTTP error codes, TCP retransmissions, or TCP resets were identified in these basic checks.");
        report.AppendLine("  These are clues from the recorded traffic, not a diagnosis. The capture may show only one side of a connection.");
    }

    private void AppendScore(StringBuilder report)
    {
        report.AppendLine().AppendLine("NETWORK SCORE");
        if (packets < 20)
        {
            report.AppendLine("  Not enough traffic to score (fewer than 20 packets).");
            return;
        }
        int score = 100;
        List<string> reasons = new List<string>();
        int duplicateDevices = deviceSources.Count(item => item.Value.Count > 1);
        int duplicateIps = arpSources.Count(item => item.Value.Count > 1);
        if (duplicateDevices > 0) { score -= 25; reasons.Add("-25: BACnet device ID announced from multiple IPs"); }
        if (duplicateIps > 0) { score -= 20; reasons.Add("-20: IP address advertised by multiple MACs in ARP"); }
        if (bacnetFailurePackets > 0)
        {
            int deduction = Math.Min(25, 10 + (int)Math.Ceiling(15.0 * bacnetFailurePackets / Math.Max(1, bacnetPackets)));
            score -= deduction; reasons.Add("-" + deduction + ": BACnet Error, Reject, or Abort responses");
        }
        if (whoIsPackets > 0 && iAmPackets == 0) { score -= 10; reasons.Add("-10: Who-Is discovery without I-Am in this capture"); }
        if (retransmissions > 0)
        {
            int deduction = Math.Min(20, 5 + (int)Math.Ceiling(100.0 * retransmissions / packets));
            score -= deduction; reasons.Add("-" + deduction + ": TCP retransmissions");
        }
        if (resets > 3) { score -= 5; reasons.Add("-5: repeated TCP resets"); }
        score = Math.Max(0, score);
        report.AppendLine("  " + score + "/100 — " + (score >= 80 ? "no major flags in these checks" : score >= 60 ? "review flagged traffic" : "investigate flagged traffic"));
        if (reasons.Count == 0) report.AppendLine("  No score deductions from the basic checks.");
        else foreach (string reason in reasons) report.AppendLine("  " + reason);
        report.AppendLine("  This is a transparent triage score for this file, not Optigo's score or a complete network health measurement.");
    }

    private void AppendBmsFindings(StringBuilder report)
    {
        report.AppendLine().AppendLine("BACNET / BMS HEALTH CHECK");
        if (bacnetPackets == 0)
        {
            report.AppendLine("  No BACnet traffic was decoded. This file may not include BMS packets, or the protocol may use a port TShark did not recognize.");
            return;
        }
        report.AppendLine("  BACnet packets: " + bacnetPackets.ToString("N0"));
        report.AppendLine("  Device discovery: " + whoIsPackets.ToString("N0") + " Who-Is requests, " + iAmPackets.ToString("N0") + " I-Am announcements.");
        report.AppendLine("  Property traffic: " + readRequests.ToString("N0") + " read requests, " + writeRequests.ToString("N0") + " write requests.");
        report.AppendLine("  BACnet error/reject/abort packets: " + bacnetFailurePackets.ToString("N0"));
        report.AppendLine("  BACnet/IP broadcast packets: " + bacnetBroadcasts.ToString("N0"));
        bool issue = false;
        if (whoIsPackets > 0 && iAmPackets == 0)
        {
            report.AppendLine("  Check discovery: Who-Is requests appear without I-Am replies in this capture. Verify the device, route, and capture location.");
            issue = true;
        }
        if (bacnetFailurePackets > 0)
        {
            report.AppendLine("  Check BACnet responses: errors, rejects, or aborts can explain failed BMS reads or writes. Review the error details below.");
            issue = true;
        }
        foreach (KeyValuePair<string, HashSet<string>> device in deviceSources.Where(item => item.Value.Count > 1).Take(5))
        {
            report.AppendLine("  Check device ID " + device.Key + ": I-Am announcements came from multiple IPs (" + String.Join(", ", device.Value.ToArray()) + "). Confirm routing before treating this as a duplicate ID.");
            issue = true;
        }
        if (!issue) report.AppendLine("  No obvious BACnet discovery failures or error responses were identified by these basic checks.");
        report.AppendLine("  A short or one-sided capture cannot prove that every device responded.");
        Section(report, "BACNET ERROR DETAILS", bacnetErrors, 15);
    }

    internal string Report(string capturePath)
    {
        FileInfo file = new FileInfo(capturePath);
        StringBuilder report = new StringBuilder();
        report.AppendLine("WIRESHARK CAPTURE REPORT");
        report.AppendLine("File: " + file.Name);
        report.AppendLine("Size: " + file.Length.ToString("N0") + " bytes");
        report.AppendLine("Packets: " + packets.ToString("N0"));
        report.AppendLine("Traffic: " + bytes.ToString("N0") + " bytes");
        if (first != Double.MaxValue)
        {
            report.AppendLine("First packet: " + DateTimeOffset.FromUnixTimeMilliseconds((long)(first * 1000)).UtcDateTime.ToString("u"));
            report.AppendLine("Last packet:  " + DateTimeOffset.FromUnixTimeMilliseconds((long)(last * 1000)).UtcDateTime.ToString("u"));
            report.AppendLine("Duration: " + Math.Max(0, last - first).ToString("N2", CultureInfo.InvariantCulture) + " seconds");
        }
        AppendScore(report);
        AppendFindings(report);
        AppendBmsFindings(report);
        Section(report, "PROTOCOLS", protocols, 20);
        Section(report, "TOP IP ENDPOINTS (packet appearances)", endpoints, 20);
        Section(report, "TOP CONVERSATIONS", conversations, 20);
        Section(report, "DNS QUERIES", dnsQueries, 20);
        Section(report, "DNS ERROR CODES (3 = NXDOMAIN)", dnsErrors, 10);
        Section(report, "HTTP HOSTS", httpHosts, 20);
        Section(report, "HTTP RESPONSE CODES", httpStatuses, 15);
        Section(report, "TLS SERVER NAMES", tlsNames, 20);
        report.AppendLine().AppendLine("CONNECTION INDICATORS");
        report.AppendLine("  TCP retransmissions: " + retransmissions.ToString("N0"));
        report.AppendLine("  TCP resets: " + resets.ToString("N0"));
        report.AppendLine("  ICMP packets: " + icmpPackets.ToString("N0"));
        report.AppendLine().AppendLine("This is a traffic summary, not a verdict about security or root cause. Review packets in Wireshark for details.");
        report.AppendLine("The capture stays on this computer; the toolkit does not upload it.");
        return report.ToString();
    }

    internal static void SelfTest()
    {
        CaptureSummary sample = new CaptureSummary();
        sample.AddPacket("1700000000.25|100|DNS|192.0.2.1|192.0.2.53|||12345|53|||example.test|3||||||");
        if (sample.packets != 1 || sample.bytes != 100 || !sample.dnsQueries.ContainsKey("example.test") || !sample.dnsErrors.ContainsKey("3"))
            throw new InvalidOperationException("Packet summary did not count DNS traffic.");
        StringBuilder findings = new StringBuilder();
        sample.AppendFindings(findings);
        if (!findings.ToString().Contains("DNS replies reported an error"))
            throw new InvalidOperationException("Plain-language DNS finding is missing.");
        string[] bacnet = new string[29];
        bacnet[0] = "1700000001.00"; bacnet[1] = "100"; bacnet[2] = "BACnet";
        bacnet[3] = "192.0.2.1"; bacnet[4] = "192.0.2.255"; bacnet[20] = "1"; bacnet[22] = "8"; bacnet[26] = "11";
        sample.AddPacket(String.Join("|", bacnet));
        StringBuilder bms = new StringBuilder(); sample.AppendBmsFindings(bms);
        if (!bms.ToString().Contains("Who-Is requests appear without I-Am replies"))
            throw new InvalidOperationException("BACnet discovery finding is missing.");
        bacnet[2] = "ARP"; bacnet[20] = ""; bacnet[22] = ""; bacnet[26] = "";
        bacnet[27] = "192.0.2.1"; bacnet[28] = "00:11:22:33:44:55"; sample.AddPacket(String.Join("|", bacnet));
        bacnet[28] = "00:11:22:33:44:66"; sample.AddPacket(String.Join("|", bacnet));
        if (sample.arpSources["192.0.2.1"].Count != 2)
            throw new InvalidOperationException("ARP duplicate-IP evidence is missing.");
    }
}

internal static class CaptureAnalyzer
{
    private static string FindTshark()
    {
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string path = Path.Combine(root, "Wireshark", "tshark.exe");
            if (File.Exists(path)) return path;
        }
        string environmentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string root in environmentPath.Split(Path.PathSeparator))
        {
            try { string path = Path.Combine(root.Trim(), "tshark.exe"); if (File.Exists(path)) return path; }
            catch (ArgumentException) { }
        }
        throw new FileNotFoundException("TShark was not found. Install Wireshark with its TShark command-line component, then try again.");
    }

    internal static string Analyze(string capturePath, CancellationToken cancellation)
    {
        if (!File.Exists(capturePath)) throw new FileNotFoundException("Capture file not found.", capturePath);
        string extension = Path.GetExtension(capturePath);
        if (!String.Equals(extension, ".pcap", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(extension, ".pcapng", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(extension, ".cap", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a .pcap, .pcapng, or .cap capture file.");
        string executable = FindTshark();
        string[] fields = { "frame.time_epoch", "frame.len", "_ws.col.Protocol", "ip.src", "ip.dst", "ipv6.src", "ipv6.dst",
            "tcp.srcport", "tcp.dstport", "udp.srcport", "udp.dstport", "dns.qry.name", "dns.flags.rcode",
            "http.host", "http.response.code", "tls.handshake.extensions_server_name", "tcp.analysis.retransmission",
            "tcp.flags.reset", "icmp.type", "icmpv6.type", "bacapp.type", "bacapp.confirmed_service",
            "bacapp.unconfirmed_service", "bacapp.error_class", "bacapp.error_code", "bacapp.deviceIdentifier", "bvlc.function",
            "arp.src.proto_ipv4", "arp.src.hw_mac" };
        string arguments = "-n -r \"" + capturePath + "\" -T fields -E separator=| -E occurrence=f " +
            String.Join(" ", fields.Select(field => "-e " + field).ToArray());
        ProcessStartInfo start = new ProcessStartInfo(executable, arguments) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        CaptureSummary summary = new CaptureSummary();
        using (Process process = Process.Start(start))
        using (cancellation.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
        {
            Task<string> errors = Task<string>.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
            string line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                cancellation.ThrowIfCancellationRequested();
                summary.AddPacket(line);
            }
            process.WaitForExit();
            cancellation.ThrowIfCancellationRequested();
            string error = errors.Result.Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("TShark could not read this capture: " + (error.Length > 500 ? error.Substring(0, 500) : error));
        }
        return summary.Report(capturePath);
    }
}
