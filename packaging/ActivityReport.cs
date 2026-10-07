using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal sealed class ActivityEntry
{
    internal DateTime When;
    internal string Category;
    internal string Message;
}

internal sealed class ActivitySnapshot
{
    private static readonly Regex LogLine = new Regex(@"^\[(\d{2}:\d{2}:\d{2})\] \[([^]]+)\] \[([^]]+)\] (.*)$", RegexOptions.Compiled);
    internal readonly List<ActivityEntry> Entries = new List<ActivityEntry>();
    internal readonly int[] ByDay = new int[7];
    internal readonly Dictionary<string, int> ByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    internal DateTime WeekStart;
    internal int SavedRdpSites;
    internal int SavedIpProfiles;
    internal int NewRdpSites;
    internal int Sessions;

    private static string Category(string area, string message)
    {
        if (area == "RDP" && (message.StartsWith("Added site:") || message.StartsWith("Updated site:") ||
            message.StartsWith("Deleted site:") || message.StartsWith("Opened connection:"))) return "RDP";
        if (area == "IP Shifter" && (message.StartsWith("Saved profile:") || message.StartsWith("Added profile:") ||
            message.StartsWith("Updated profile:") || message.StartsWith("Deleted profile:") || message.StartsWith("Loaded profile:") ||
            message.StartsWith("Requested ") || message.StartsWith("Restored previous"))) return "IP Shifter";
        if (area == "IP Scanner" && (message.StartsWith("Scanning ") || message.StartsWith("Exported "))) return "IP Scanner";
        if (area == "Capture" && (message.StartsWith("Analyzed ") || message.StartsWith("Saved packet report"))) return "Packet Reports";
        if (area == "BMS" && message.StartsWith("Opened YABE")) return "BMS";
        if (area == "Windows" && message.StartsWith("Opened ")) return "Windows";
        if (area == "Ping" || area == "DNS" || area == "Telnet" || area == "Web" || area == "Support") return "Network & Support";
        return null;
    }

    internal static ActivitySnapshot Load(string folder, DateTime today, int rdpCount, int ipCount)
    {
        ActivitySnapshot snapshot = new ActivitySnapshot();
        snapshot.WeekStart = today.Date.AddDays(-((int)today.DayOfWeek + 6) % 7);
        snapshot.SavedRdpSites = rdpCount;
        snapshot.SavedIpProfiles = ipCount;
        if (!Directory.Exists(folder)) return snapshot;
        foreach (string path in Directory.GetFiles(folder, "TEC_FieldToolkit_*.log"))
        {
            string filename = Path.GetFileNameWithoutExtension(path);
            if (filename.Length < 25) continue;
            DateTime fileDate;
            if (!DateTime.TryParseExact(filename.Substring(17, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out fileDate) || fileDate < snapshot.WeekStart || fileDate.Date > today.Date) continue;
            try
            {
                foreach (string line in File.ReadLines(path)) snapshot.AddLine(fileDate, line, today);
            }
            catch (IOException) { /* A log being written can be read on the next refresh. */ }
            catch (UnauthorizedAccessException) { }
        }
        snapshot.Entries.Sort((a, b) => b.When.CompareTo(a.When));
        return snapshot;
    }

    private void AddLine(DateTime date, string line, DateTime today)
    {
        Match match = LogLine.Match(line);
        if (!match.Success) return;
        TimeSpan time;
        if (!TimeSpan.TryParseExact(match.Groups[1].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out time)) return;
        DateTime when = date.Date.Add(time);
        if (when < WeekStart || when > today) return;
        string area = match.Groups[3].Value, message = match.Groups[4].Value;
        if (area == "Startup" && match.Groups[2].Value == "OK") Sessions++;
        string category = Category(area, message);
        if (category == null) return;
        Entries.Add(new ActivityEntry { When = when, Category = category, Message = message });
        int day = (int)(when.Date - WeekStart).TotalDays;
        if (day >= 0 && day < ByDay.Length) ByDay[day]++;
        int count; ByCategory.TryGetValue(category, out count); ByCategory[category] = count + 1;
        if (area == "RDP" && message.StartsWith("Added site:")) NewRdpSites++;
    }

    internal string ToReport()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("TEC SYSTEMS FIELD TOOLKIT — WEEKLY ACTIVITY");
        report.AppendLine("Week starting: " + WeekStart.ToString("MMMM d, yyyy"));
        report.AppendLine("Saved RDP sites now: " + SavedRdpSites);
        report.AppendLine("Saved IP profiles now: " + SavedIpProfiles);
        report.AppendLine("RDP sites added this week: " + NewRdpSites);
        report.AppendLine("Actions recorded this week: " + Entries.Count);
        report.AppendLine("Toolkit starts this week: " + Sessions);
        report.AppendLine().AppendLine("ACTIONS BY DAY");
        for (int i = 0; i < 7; i++) report.AppendLine("  " + WeekStart.AddDays(i).ToString("ddd MMM d") + ": " + ByDay[i]);
        report.AppendLine().AppendLine("ACTIONS BY TOOL");
        foreach (KeyValuePair<string, int> entry in ByCategory.OrderByDescending(item => item.Value))
            report.AppendLine("  " + entry.Key + ": " + entry.Value);
        report.AppendLine().AppendLine("RECENT ACTIONS");
        foreach (ActivityEntry entry in Entries.Take(100))
            report.AppendLine("  " + entry.When.ToString("MMM d HH:mm") + "  " + entry.Category + "  " + entry.Message.Replace('\r', ' ').Replace('\n', ' '));
        report.AppendLine().AppendLine("This report uses local toolkit logs. Actions not recorded by older versions may be absent.");
        return report.ToString();
    }

    internal static void SelfTest()
    {
        ActivitySnapshot sample = new ActivitySnapshot { WeekStart = new DateTime(2026, 10, 5) };
        sample.AddLine(new DateTime(2026, 10, 7), "[09:30:00] [OK] [RDP] Added site: Example", new DateTime(2026, 10, 7, 18, 0, 0));
        sample.AddLine(new DateTime(2026, 10, 7), "[10:00:00] [INFO] [IP Scanner] Scanning 192.0.2.1 - 192.0.2.10", new DateTime(2026, 10, 7, 18, 0, 0));
        if (sample.NewRdpSites != 1 || sample.Entries.Count != 2 || sample.ByDay[2] != 2)
            throw new InvalidOperationException("Weekly activity parsing failed.");
    }
}

internal sealed class ActivityChart : Panel
{
    internal ActivitySnapshot Snapshot;
    internal bool DarkTheme;
    private static readonly Color[] Palette = {
        Color.FromArgb(0, 104, 220), Color.FromArgb(22, 139, 109), Color.FromArgb(216, 130, 45),
        Color.FromArgb(147, 96, 201), Color.FromArgb(30, 150, 180), Color.FromArgb(190, 80, 96)
    };

    internal ActivityChart() { DoubleBuffered = true; }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        Graphics graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color ink = DarkTheme ? Color.FromArgb(235, 242, 246) : Color.FromArgb(28, 43, 55);
        Color grid = DarkTheme ? Color.FromArgb(80, 98, 110) : Color.FromArgb(215, 225, 234);
        using (Font title = new Font("Segoe UI", 10f, FontStyle.Bold))
        using (Font label = new Font("Segoe UI", 8f))
        using (Brush text = new SolidBrush(ink))
        using (Pen baseline = new Pen(grid))
        {
            graphics.DrawString("Actions this week", title, text, 16, 12);
            graphics.DrawString("By tool", title, text, Width / 2 + 12, 12);
            if (Snapshot == null) return;
            int chartTop = 56, chartBottom = Height - 28, chartHeight = Math.Max(45, chartBottom - chartTop);
            int leftWidth = Math.Max(210, Width / 2 - 32);
            graphics.DrawLine(baseline, 18, chartBottom, leftWidth, chartBottom);
            int maxDay = Math.Max(1, Snapshot.ByDay.Max());
            int gap = Math.Max(24, (leftWidth - 25) / 7);
            for (int i = 0; i < 7; i++)
            {
                int x = 24 + i * gap;
                int barHeight = (int)((chartHeight - 18) * (double)Snapshot.ByDay[i] / maxDay);
                using (Brush bar = new SolidBrush(Palette[i % Palette.Length]))
                    graphics.FillRectangle(bar, x, chartBottom - barHeight, Math.Max(12, gap - 11), barHeight);
                graphics.DrawString(Snapshot.WeekStart.AddDays(i).ToString("ddd"), label, text, x, chartBottom + 3);
                if (Snapshot.ByDay[i] > 0) graphics.DrawString(Snapshot.ByDay[i].ToString(), label, text, x, chartBottom - barHeight - 16);
            }
            int rightX = Width / 2 + 12, rightWidth = Math.Max(80, Width - rightX - 18);
            KeyValuePair<string, int>[] categories = Snapshot.ByCategory.OrderByDescending(item => item.Value).Take(6).ToArray();
            int maxCategory = categories.Length == 0 ? 1 : Math.Max(1, categories.Max(item => item.Value));
            for (int i = 0; i < categories.Length; i++)
            {
                int y = 50 + i * 30;
                graphics.DrawString(categories[i].Key, label, text, rightX, y);
                using (Brush bar = new SolidBrush(Palette[i % Palette.Length]))
                    graphics.FillRectangle(bar, rightX + 115, y + 3, Math.Max(3, (int)((rightWidth - 155) * (double)categories[i].Value / maxCategory)), 13);
                graphics.DrawString(categories[i].Value.ToString(), label, text, rightX + rightWidth - 28, y);
            }
        }
    }
}

internal sealed class ActivityWindow : Form
{
    private readonly string folder;
    private readonly Func<int> rdpCount;
    private readonly Func<int> ipCount;
    private readonly bool dark;
    private readonly Label[] values = new Label[4];
    private readonly Panel[] cards = new Panel[4];
    private readonly Label subtitle = new Label();
    private readonly ActivityChart chart = new ActivityChart();
    private readonly ListView recent = new ListView();
    private readonly Button refresh = new Button();
    private readonly Button export = new Button();
    private ActivitySnapshot snapshot;

    internal ActivityWindow(string dataFolder, Func<int> getRdpCount, Func<int> getIpCount, bool darkTheme)
    {
        folder = dataFolder; rdpCount = getRdpCount; ipCount = getIpCount; dark = darkTheme;
        Text = "TEC Systems — Activity Report"; ClientSize = new Size(930, 700); MinimumSize = new Size(840, 630);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9f);
        Color canvas = dark ? Color.FromArgb(27, 36, 43) : Color.FromArgb(240, 244, 248);
        Color surface = dark ? Color.FromArgb(38, 50, 59) : Color.White;
        Color ink = dark ? Color.FromArgb(235, 242, 246) : Color.FromArgb(25, 43, 56);
        BackColor = canvas; ForeColor = ink;
        Controls.Add(new Label { Text = "Your toolkit activity", Left = 22, Top = 18, Width = 490, Height = 40,
            Font = new Font("Segoe UI", 17f, FontStyle.Bold), ForeColor = ink });
        subtitle.Left = 24; subtitle.Top = 61; subtitle.Width = 560; subtitle.Height = 24; subtitle.ForeColor = ink;
        subtitle.Text = "Loading this week's local activity..."; Controls.Add(subtitle);
        refresh.Text = "Refresh"; refresh.SetBounds(635, 29, 120, 34); refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        refresh.Click += delegate { RefreshActivity(); }; Controls.Add(refresh);
        export.Text = "Export Report"; export.SetBounds(765, 29, 140, 34); export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        export.Enabled = false; export.Click += delegate { ExportReport(); }; Controls.Add(export);
        foreach (Button button in new[] { refresh, export })
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
            button.BackColor = button == refresh ? Color.FromArgb(0, 104, 220) : Color.FromArgb(22, 139, 109); button.ForeColor = Color.White;
        }
        string[] titles = { "Saved RDP sites", "Saved IP profiles", "Sites added this week", "Actions this week" };
        for (int i = 0; i < 4; i++)
        {
            Panel card = new Panel { Left = 22 + i * 222, Top = 100, Width = 210, Height = 92, BackColor = surface };
            cards[i] = card;
            card.Controls.Add(new Label { Text = titles[i], Left = 12, Top = 10, Width = 185, Height = 22, ForeColor = ink });
            values[i] = new Label { Text = "—", Left = 12, Top = 35, Width = 185, Height = 47,
                Font = new Font("Segoe UI", 21f, FontStyle.Bold), ForeColor = ActivityChartColor(i) };
            card.Controls.Add(values[i]); Controls.Add(card);
        }
        chart.SetBounds(22, 210, 884, 240); chart.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        chart.BackColor = surface; chart.DarkTheme = dark; Controls.Add(chart);
        Controls.Add(new Label { Text = "Recent actions", Left = 22, Top = 464, Width = 230, Height = 25,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = ink });
        recent.SetBounds(22, 492, 884, 172); recent.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        recent.View = View.Details; recent.FullRowSelect = true; recent.GridLines = false;
        recent.Columns.Add("When", 125); recent.Columns.Add("Tool", 145); recent.Columns.Add("What happened", 590);
        recent.BackColor = surface; recent.ForeColor = ink; Controls.Add(recent);
        if (dark)
        {
            recent.OwnerDraw = true;
            recent.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs args) {
                using (Brush fill = new SolidBrush(Color.FromArgb(48, 63, 74))) args.Graphics.FillRectangle(fill, args.Bounds);
                TextRenderer.DrawText(args.Graphics, args.Header.Text, recent.Font, args.Bounds, ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            recent.DrawItem += delegate(object sender, DrawListViewItemEventArgs args) { if (recent.View != View.Details) args.DrawDefault = true; };
            recent.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs args) {
                using (Brush fill = new SolidBrush(args.Item.Selected ? Color.FromArgb(42, 91, 91) : surface))
                    args.Graphics.FillRectangle(fill, args.Bounds);
                TextRenderer.DrawText(args.Graphics, args.SubItem.Text, recent.Font, args.Bounds, ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
        }
        Resize += delegate {
            int width = Math.Max(170, (ClientSize.Width - 80) / 4);
            for (int i = 0; i < cards.Length; i++) { cards[i].Left = 22 + i * (width + 12); cards[i].Width = width; }
        };
        Shown += delegate { RefreshActivity(); };
    }

    private static Color ActivityChartColor(int index)
    {
        return new[] { Color.FromArgb(0, 104, 220), Color.FromArgb(22, 139, 109),
            Color.FromArgb(216, 130, 45), Color.FromArgb(147, 96, 201) }[index];
    }

    private void RefreshActivity()
    {
        if (!refresh.Enabled) return;
        refresh.Enabled = false; subtitle.Text = "Reading local activity logs...";
        int sites = rdpCount(), profiles = ipCount();
        ThreadPool.QueueUserWorkItem(delegate {
            ActivitySnapshot loaded = null; string error = null;
            try { loaded = ActivitySnapshot.Load(folder, DateTime.Now, sites, profiles); }
            catch (Exception failure) { error = failure.Message; }
            if (!IsDisposed && IsHandleCreated)
                try { BeginInvoke(new Action(delegate {
                    if (IsDisposed) return;
                    refresh.Enabled = true;
                    if (loaded == null) { subtitle.Text = "Could not read activity: " + error; return; }
                    snapshot = loaded; export.Enabled = true;
                    subtitle.Text = "Week of " + loaded.WeekStart.ToString("MMMM d, yyyy") + "  •  " + loaded.Sessions + " toolkit starts recorded";
                    int[] totals = { loaded.SavedRdpSites, loaded.SavedIpProfiles, loaded.NewRdpSites, loaded.Entries.Count };
                    for (int i = 0; i < values.Length; i++) values[i].Text = totals[i].ToString("N0");
                    chart.Snapshot = loaded; chart.Invalidate();
                    recent.BeginUpdate(); recent.Items.Clear();
                    foreach (ActivityEntry entry in loaded.Entries.Take(100))
                    {
                        ListViewItem row = new ListViewItem(entry.When.ToString("MMM d HH:mm"));
                        row.SubItems.Add(entry.Category); row.SubItems.Add(entry.Message); recent.Items.Add(row);
                    }
                    recent.EndUpdate();
                })); }
                catch (InvalidOperationException) { }
        });
    }

    private void ExportReport()
    {
        if (snapshot == null) return;
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt",
            FileName = "TEC_Weekly_Activity_" + snapshot.WeekStart.ToString("yyyyMMdd") + ".txt" })
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                try { File.WriteAllText(dialog.FileName, snapshot.ToReport(), Encoding.UTF8); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Export Activity", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
    }
}
