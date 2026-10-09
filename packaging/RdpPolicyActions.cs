using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

internal static partial class RdpPolicyDiagnostics
{
    private const string MarkerPath = @"SOFTWARE\TEC Systems\Field Toolkit\RdpCredentialRules";
    private static readonly string[] Allows = { "AllowSavedCredentials", "AllowSavedCredentialsWhenNTLMOnly" };
    private static readonly string[] Defaults = { "ConcatenateDefaults_AllowSaved", "ConcatenateDefaults_AllowSavedNTLMOnly" };

    internal static bool IsExactIpv4(string value)
    {
        IPAddress parsed;
        return value != null && Regex.IsMatch(value, @"\A(?:[0-9]{1,3}\.){3}[0-9]{1,3}\z") &&
            IPAddress.TryParse(value, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork &&
            parsed.ToString() == value;
    }

    internal static int RunElevated(string operation, string ip)
    {
        try
        {
            if (!IsExactIpv4(ip) || (operation != "apply" && operation != "undo"))
                throw new InvalidOperationException("Select a saved RDP site with a standard IPv4 address.");
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Administrator approval is required to change local computer policy.");
            string result = operation == "apply" ? Apply(ip) : Undo(ip);
            Audit(operation, ip, result);
            MessageBox.Show(result, "RDP Credential Policy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            Audit(operation, ip, "Failed: " + error.GetType().Name + ": " + error.Message);
            MessageBox.Show(error.Message, "RDP Credential Policy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static RegistryKey MachineRoot()
    {
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
    }

    private static bool ExactPresent(RegistryKey key, string target)
    {
        return key.GetValueNames().Any(name => String.Equals(Convert.ToString(key.GetValue(name)), target, StringComparison.OrdinalIgnoreCase));
    }

    private static string NextNumber(RegistryKey key)
    {
        for (int number = 1; number < 10000; number++)
            if (!key.GetValueNames().Contains(number.ToString())) return number.ToString();
        throw new InvalidOperationException("No free credential-delegation rule number was found.");
    }

    private static string Apply(string ip)
    {
        string target = "TERMSRV/" + ip;
        using (RegistryKey root = MachineRoot())
        {
            if (ReadFlag(root, TerminalServicesPath, "DisablePasswordSaving") == 1)
                throw new InvalidOperationException("Client policy blocks password saving. Ask company IT to review it.");
            foreach (string allow in Allows)
            {
                if (ReadFlag(root, DelegationPath, allow) == 0)
                    throw new InvalidOperationException(allow + " is explicitly disabled. Ask company IT to review it.");
                string deny = allow.Replace("Allow", "Deny");
                if (ReadFlag(root, DelegationPath, deny) == 1 &&
                    ReadTargets(root, DelegationPath + "\\" + deny).Any(rule => MatchesTarget(rule, target)))
                    throw new InvalidOperationException("A deny rule matches " + target + ". Ask company IT to review it.");
            }
            using (RegistryKey markers = root.CreateSubKey(MarkerPath))
            using (RegistryKey existing = markers.OpenSubKey(ip))
            {
                if (existing == null)
                {
                    using (RegistryKey policy = root.CreateSubKey(DelegationPath))
                    using (RegistryKey regular = policy.CreateSubKey(Allows[0]))
                    using (RegistryKey ntlm = policy.CreateSubKey(Allows[1]))
                    {
                        string first = ExactPresent(regular, target) ? "" : NextNumber(regular);
                        string second = ExactPresent(ntlm, target) ? "" : NextNumber(ntlm);
                        if (markers.GetSubKeyNames().Length == 0)
                            foreach (string name in Allows.Concat(Defaults))
                                markers.SetValue("Missing_" + name, policy.GetValue(name) == null ? 1 : 0, RegistryValueKind.DWord);
                        using (RegistryKey marker = markers.CreateSubKey(ip))
                        {
                            marker.SetValue("RegularNumber", first, RegistryValueKind.String);
                            marker.SetValue("NtlmNumber", second, RegistryValueKind.String);
                        }
                        try
                        {
                            if (first.Length != 0) regular.SetValue(first, target, RegistryValueKind.String);
                            if (second.Length != 0) ntlm.SetValue(second, target, RegistryValueKind.String);
                            foreach (string name in Allows.Concat(Defaults))
                                if (policy.GetValue(name) == null) policy.SetValue(name, 1, RegistryValueKind.DWord);
                        }
                        catch
                        {
                            throw new InvalidOperationException("The local rule was only partly written. Use Undo local rule to remove the toolkit changes.");
                        }
                    }
                }
            }
            string refresh = RefreshPolicy();
            bool present = Allows.All(name => ReadFlag(root, DelegationPath, name) == 1 &&
                ReadTargets(root, DelegationPath + "\\" + name).Any(rule => MatchesTarget(rule, target)));
            return (present ? "Exact credential delegation rules are present for " + target + "." :
                "The rule did not remain active after policy refresh. Company Group Policy may have replaced it; ask IT to review.") +
                "\r\n" + refresh + "\r\nReconnect to test. This check cannot verify remote sign-in or persistence after reboot.";
        }
    }

    private static string Undo(string ip)
    {
        string target = "TERMSRV/" + ip;
        using (RegistryKey root = MachineRoot())
        using (RegistryKey markers = root.OpenSubKey(MarkerPath, true))
        {
            if (markers == null) return "No toolkit-created local rule was recorded for " + target + ".";
            using (RegistryKey marker = markers.OpenSubKey(ip))
            {
                if (marker == null) return "No toolkit-created local rule was recorded for " + target + ".";
                bool lastRule = markers.GetSubKeyNames().Length == 1;
                using (RegistryKey policy = root.OpenSubKey(DelegationPath, true))
                {
                    if (policy == null) throw new InvalidOperationException("The policy key is missing. Ask IT to inspect it before clearing the toolkit record.");
                    for (int i = 0; i < Allows.Length; i++)
                    {
                        string name = Allows[i];
                        string number = Convert.ToString(marker.GetValue(i == 0 ? "RegularNumber" : "NtlmNumber", ""));
                        using (RegistryKey entries = policy.OpenSubKey(name, true))
                        {
                            if (entries != null && number.Length != 0 &&
                                String.Equals(Convert.ToString(entries.GetValue(number)), target, StringComparison.OrdinalIgnoreCase))
                                entries.DeleteValue(number);
                            if (lastRule && entries != null && entries.GetValueNames().Length == 0 &&
                                Convert.ToInt32(markers.GetValue("Missing_" + name, 0)) == 1 &&
                                Convert.ToInt32(policy.GetValue(name, 0)) == 1)
                                policy.DeleteValue(name);
                        }
                    }
                    foreach (string name in Defaults)
                        if (lastRule && Convert.ToInt32(markers.GetValue("Missing_" + name, 0)) == 1 &&
                            Convert.ToInt32(policy.GetValue(name, 0)) == 1)
                            policy.DeleteValue(name);
                }
            }
            markers.DeleteSubKey(ip);
            if (markers.GetSubKeyNames().Length == 0)
                foreach (string name in Allows.Concat(Defaults)) markers.DeleteValue("Missing_" + name, false);
            return "Toolkit-created exact rule removed for " + target + ".\r\n" + RefreshPolicy() +
                "\r\nA company policy may still allow this target.";
        }
    }

    private static string RefreshPolicy()
    {
        using (Process process = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "gpupdate.exe"), "/target:computer /force")
            { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (process == null) return "Policy refresh could not start.";
            if (!process.WaitForExit(120000))
            {
                try { process.Kill(); } catch { }
                return "Policy refresh timed out after two minutes. Ask IT to check gpupdate.";
            }
            return process.ExitCode == 0 ? "Computer policy refresh completed." :
                "Computer policy refresh returned exit code " + process.ExitCode + ". Ask IT to check gpupdate.";
        }
    }

    private static void Audit(string operation, string ip, string result)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TEC Systems", "Field Toolkit");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "rdp-policy.log"), DateTimeOffset.Now.ToString("o") + " " +
                operation + " " + ip + " " + result.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
        }
        catch { /* The result is still shown to the technician. */ }
    }
}
