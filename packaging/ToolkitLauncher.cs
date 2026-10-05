using System;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Windows.Forms;

internal static class ToolkitLauncher
{
    [STAThread]
    private static int Main()
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        string script = Path.Combine(folder, "TEC-Systems-FieldToolkit.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show("The toolkit script is missing. Reinstall TEC Systems Field Toolkit.",
                "TEC Systems Field Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        try
        {
            Environment.CurrentDirectory = folder;
            using (Runspace runspace = RunspaceFactory.CreateRunspace())
            {
                runspace.ApartmentState = ApartmentState.STA;
                runspace.ThreadOptions = PSThreadOptions.UseCurrentThread;
                runspace.Open();
                using (PowerShell powershell = PowerShell.Create())
                {
                    powershell.Runspace = runspace;
                    powershell.AddCommand(script);
                    powershell.Invoke();
                    if (powershell.HadErrors)
                    {
                        string message = powershell.Streams.Error.Count > 0
                            ? powershell.Streams.Error[0].ToString()
                            : "Unknown PowerShell error.";
                        throw new InvalidOperationException(message);
                    }
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show("The toolkit could not start.\r\n\r\n" + error.Message,
                "TEC Systems Field Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
