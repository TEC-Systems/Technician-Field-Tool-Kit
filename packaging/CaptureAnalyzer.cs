using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    private long packets;
    private long bytes;
    private long retransmissions;
    private long resets;
    private long icmpPackets;
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
    }

    private static void Section(StringBuilder report, string title, Dictionary<string, long> values, int limit)
    {
        report.AppendLine().AppendLine(title);
        if (values.Count == 0) { report.AppendLine("  None observed"); return; }
        foreach (KeyValuePair<string, long> entry in values.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(limit))
            report.Append("  ").Append(entry.Value.ToString("N0")).Append("  ").AppendLine(entry.Key);
        if (values.Count > limit) report.AppendLine("  ... and " + (values.Count - limit) + " more");
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
    }
}

internal static class CaptureAnalyzer
{
    internal sealed class CaptureInterface
    {
        internal int Number;
        internal string Description;
        public override string ToString() { return Description; }
    }

    internal static string FindTshark()
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

    internal static List<CaptureInterface> ListInterfaces()
    {
        using (Process process = Process.Start(new ProcessStartInfo(FindTshark(), "-D") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Wireshark interface discovery timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not list capture interfaces: " + error.Trim());
            List<CaptureInterface> interfaces = new List<CaptureInterface>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = Regex.Match(line, @"^\s*(\d+)\.\s+(.+)$");
                int number;
                if (match.Success && Int32.TryParse(match.Groups[1].Value, out number))
                    interfaces.Add(new CaptureInterface { Number = number, Description = match.Groups[2].Value.Trim() });
            }
            return interfaces;
        }
    }

    internal static void Capture(int interfaceNumber, int durationSeconds, string outputPath, CancellationToken cancellation)
    {
        if (interfaceNumber <= 0 || durationSeconds < 5 || durationSeconds > 3600)
            throw new ArgumentException("Choose an interface and a capture duration.");
        string arguments = "-n -q -i " + interfaceNumber + " -a duration:" + durationSeconds + " -w \"" + outputPath + "\"";
        using (Process process = Process.Start(new ProcessStartInfo(FindTshark(), arguments) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }))
        using (cancellation.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
        {
            Task<string> output = Task<string>.Factory.StartNew(delegate { return process.StandardOutput.ReadToEnd(); });
            Task<string> errors = Task<string>.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
            if (!process.WaitForExit((durationSeconds + 30) * 1000))
            {
                process.Kill();
                throw new TimeoutException("Capture did not finish in time.");
            }
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                string error = errors.Result.Trim();
                throw new InvalidOperationException("Wireshark could not capture packets: " +
                    (error.Length > 500 ? error.Substring(0, 500) : error));
            }
            output.Wait();
            errors.Wait();
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("The capture did not produce a file. Check that Npcap is installed and the interface is active.");
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
            "tcp.flags.reset", "icmp.type", "icmpv6.type" };
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
