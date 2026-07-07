using System.Text;

namespace ListenererConfig;

/// <summary>
/// Listenerer configuration editor. Loads and saves the .env file the bot reads
/// (DotNetEnv, AzureSettings__* convention). Doubles as the standalone editor for
/// any Listenerer .env via File > Open.
/// </summary>
public class MainForm : Form
{
    private sealed record Field(string Key, string Label, string Default = "", bool Secret = false, bool IsBool = false, string Help = "");

    private static readonly (string Section, Field[] Fields)[] Sections =
    [
        ("Bot identity (from the Entra app registration)",
        [
            new("AzureSettings__BotName", "Bot name"),
            new("AzureSettings__AadAppId", "Entra app (client) id"),
            new("AzureSettings__AadAppSecret", "Client secret", Secret: true),
        ]),
        ("Public endpoint",
        [
            new("AzureSettings__ServiceDnsName", "Service DNS name", Help: "e.g. bot.example.com — must match the certificate"),
            new("AzureSettings__CertificateThumbprint", "Certificate thumbprint", Help: "cert must be in LocalMachine store"),
        ]),
        ("Ports",
        [
            new("AzureSettings__CallSignalingPort", "Call signaling port", "9441"),
            new("AzureSettings__CallSignalingPublicPort", "Signaling public port", "443", Help: "public port in the Teams callback URL; leave 443 unless the edge maps signaling elsewhere"),
            new("AzureSettings__InstanceInternalPort", "Media port (internal)", "8445", Help: "one media port serves ALL concurrent calls"),
            new("AzureSettings__InstancePublicPort", "Media port (public)", Help: "public port NAT-mapped to the internal media port"),
        ]),
        ("Recording output",
        [
            new("AzureSettings__RecordingRootFolder", "Recording folder", Help: @"local dir or UNC share, e.g. D:\recordings — blank = OS temp"),
        ]),
        ("Audio",
        [
            new("AzureSettings__IsStereo", "Stereo output", "false", IsBool: true),
            new("AzureSettings__WAVSampleRate", "WAV sample rate", Help: "blank = native 16000"),
            new("AzureSettings__WAVQuality", "WAV quality", "100"),
        ]),
        ("Advanced",
        [
            new("AzureSettings__PlaceCallEndpointUrl", "Graph endpoint", "https://graph.microsoft.com/v1.0"),
            new("AzureSettings__ServiceCname", "Service CNAME", Help: "media platform FQDN; blank = service DNS name"),
            new("AzureSettings__ServicePath", "Service path prefix", Help: "webhook URL path prefix; blank = /"),
            new("AzureSettings__CaptureEvents", "Capture diagnostic events", "false", IsBool: true),
            new("AzureSettings__PodName", "Pod name", "bot-0"),
            new("AzureSettings__MediaFolder", "Media folder", "archive"),
            new("AzureSettings__EventsFolder", "Events folder", "events"),
        ]),
        ("Event Grid diagnostics (optional — inactive unless key is set)",
        [
            new("AzureSettings__TopicName", "Topic name", "recordingbotevents"),
            new("AzureSettings__RegionName", "Topic region", "australiaeast"),
            new("AzureSettings__TopicKey", "Topic key", Secret: true),
        ]),
    ];

    private readonly Dictionary<string, Control> _inputs = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private string _envPath;

    public MainForm()
    {
        Text = "Listenerer Config";
        MinimumSize = new Size(680, 640);
        Size = new Size(720, 760);
        StartPosition = FormStartPosition.CenterScreen;

        _envPath = Path.Combine(Application.StartupPath, ".env");

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (section, fields) in Sections)
        {
            var header = new Label
            {
                Text = section,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 14, 0, 6),
            };
            table.Controls.Add(header);
            table.SetColumnSpan(header, 2);

            foreach (var f in fields)
            {
                var label = new Label
                {
                    Text = f.Label,
                    AutoSize = true,
                    Margin = new Padding(0, 8, 8, 0),
                };
                table.Controls.Add(label);

                Control input;
                if (f.IsBool)
                {
                    input = new CheckBox { Checked = f.Default == "true", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
                }
                else
                {
                    var tb = new TextBox { Dock = DockStyle.Fill, Text = f.Default };
                    if (f.Secret)
                    {
                        tb.UseSystemPasswordChar = true;
                    }
                    input = tb;
                }
                if (f.Help.Length > 0)
                {
                    new ToolTip().SetToolTip(input, f.Help);
                    new ToolTip().SetToolTip(label, f.Help);
                }
                _inputs[f.Key] = input;
                table.Controls.Add(input);
            }
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var save = new Button { Text = "Save", Width = 100 };
        var open = new Button { Text = "Open…", Width = 100 };
        var browse = new Button { Text = "Recording folder…", Width = 130 };
        var showSecrets = new CheckBox { Text = "Show secrets", AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
        save.Click += (_, _) => SaveEnv();
        open.Click += (_, _) => OpenEnv();
        browse.Click += (_, _) => BrowseRecordingFolder();
        showSecrets.CheckedChanged += (_, _) =>
        {
            foreach (var (key, ctl) in _inputs)
            {
                if (ctl is TextBox tb && Sections.SelectMany(s => s.Fields).First(f => f.Key == key).Secret)
                {
                    tb.UseSystemPasswordChar = !showSecrets.Checked;
                }
            }
        };
        buttons.Controls.AddRange([save, open, browse, showSecrets]);

        var statusStrip = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 2, 12, 2) };
        statusStrip.Controls.Add(_status);

        Controls.Add(table);
        Controls.Add(buttons);
        Controls.Add(statusStrip);

        LoadEnv(_envPath);
    }

    private void LoadEnv(string path)
    {
        _envPath = path;
        if (!File.Exists(path))
        {
            _status.Text = $"New file: {path} (defaults shown — Save to create)";
            return;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var idx = trimmed.IndexOf('=');
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            if (!_inputs.TryGetValue(key, out var ctl)) continue;
            if (ctl is CheckBox cb) cb.Checked = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            else ((TextBox)ctl).Text = value;
        }
        _status.Text = $"Loaded: {path}";
    }

    private void SaveEnv()
    {
        // Light validation: ports numeric when set
        foreach (var portKey in new[] { "AzureSettings__CallSignalingPort", "AzureSettings__InstanceInternalPort", "AzureSettings__InstancePublicPort" })
        {
            var text = ((TextBox)_inputs[portKey]).Text.Trim();
            if (text.Length > 0 && !ushort.TryParse(text, out _))
            {
                MessageBox.Show($"{portKey.Replace("AzureSettings__", "")} must be a port number (1-65535) or blank.", "Listenerer Config", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Listenerer configuration — generated by Listenerer Config");
        sb.AppendLine("# Edit with the Listenerer Config tool, or by hand (KEY=VALUE).");
        foreach (var (section, fields) in Sections)
        {
            sb.AppendLine();
            sb.AppendLine($"# --- {section} ---");
            foreach (var f in fields)
            {
                var value = _inputs[f.Key] switch
                {
                    CheckBox cb => cb.Checked ? "true" : "false",
                    TextBox tb => tb.Text.Trim(),
                    _ => "",
                };
                sb.AppendLine($"{f.Key}={value}");
            }
        }

        File.WriteAllText(_envPath, sb.ToString());
        _status.Text = $"Saved: {_envPath}";
    }

    private void OpenEnv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open Listenerer .env",
            Filter = "env files (*.env;.env)|*.env;.env|All files (*.*)|*.*",
            FileName = ".env",
            InitialDirectory = Path.GetDirectoryName(_envPath),
            CheckFileExists = false,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadEnv(dlg.FileName);
        }
    }

    private void BrowseRecordingFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Recording output folder" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            ((TextBox)_inputs["AzureSettings__RecordingRootFolder"]).Text = dlg.SelectedPath;
        }
    }
}
