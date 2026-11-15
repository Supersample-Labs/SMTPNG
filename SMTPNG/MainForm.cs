using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;

namespace DummySmtp;

public sealed class MainForm : Form
{
    private SmtpServer server = new();
    private readonly BindingList<CapturedMail> messages = new();
    private readonly TextBox bind = new() { Text = "0.0.0.0", Width = 110 };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 25, Width = 75 };
    private readonly Button start = new() { Text = "Start listening", AutoSize = true };
    private readonly Button stop = new() { Text = "Stop", Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(72, 32) };
    private readonly Label status = new() { Text = "Stopped", AutoSize = true, Padding = new Padding(8) };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly TextBox activity = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox forwarding = new() { Text = "Enable forwarding", Appearance = Appearance.Button, AutoSize = true };
    private readonly CheckBox direct = new() { Text = "Deliver directly to recipient MX", AutoSize = true };
    private readonly TextBox helo = new() { Width = 270 };
    private readonly CheckBox dkim = new() { Text = "Enable DKIM signing", AutoSize = true };
    private readonly TextBox dkimDomain = new() { Width = 270 };
    private readonly TextBox dkimSelector = new() { Width = 140, Text = "smtp" };
    private readonly TextBox dkimDns = new() { Width = 700, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both };
    private readonly TextBox dmarcDomain = new() { Width = 300 };
    private readonly ComboBox dmarcPolicy = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox dmarcReport = new() { Width = 350 };
    private readonly ComboBox dmarcDkimAlignment = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox dmarcSpfAlignment = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private string protectedDkimKey = "";
    private readonly TextBox host = new() { Width = 270 };
    private readonly NumericUpDown upstreamPort = new() { Minimum = 1, Maximum = 65535, Value = 587, Width = 85 };
    private readonly CheckBox tls = new() { Text = "Require STARTTLS", Checked = true, AutoSize = true };
    private readonly TextBox username = new() { Width = 270 };
    private readonly TextBox password = new() { Width = 270, UseSystemPasswordChar = true };
    private readonly TextBox allowed = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 11), Text = "127.0.0.1\r\n::1" };
    private readonly NotifyIcon tray = new();
    private readonly CheckBox saveEml = new() { Text = "Enable .eml saving", Appearance = Appearance.Button, AutoSize = true };
    private readonly CheckBox incomingTls = new() { Text = "Enable incoming STARTTLS", AutoSize = true };
    private readonly CheckBox requireTls = new() { Text = "Require TLS before accepting mail", AutoSize = true };
    private readonly ComboBox certStore = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox certName = new() { Width = 360, Text = "SMTPNG" };
    private readonly Label serviceStatus = new() { AutoSize = true, Padding = new Padding(5) };
    private readonly CheckBox viewService = new() { Text = "View service captures", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private string indexStamp = "";
    private string SelectedStorage => viewService.Checked ? Path.Combine(AppSettings.ServiceRoot, "Messages") : server.StoragePath;

    public MainForm()
    {
        Text = "SMTPNG — SMTP capture and relay";
        Size = new Size(1150, 780); MinimumSize = new Size(800, 550);
        StartPosition = FormStartPosition.CenterScreen;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true };
        var export = new Button { Text = "Export CSV", AutoSize = true };
        var folder = new Button { Text = "Open logs", AutoSize = true };
        var emails = new Button { Text = "Open saved emails", AutoSize = true };
        var clear = new Button { Text = "Clear view", AutoSize = true };
        var help = new Button { Text = "README", AutoSize = true };
        toolbar.Controls.AddRange(new Control[] { new Label { Text = "Bind IP", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, bind, new Label { Text = "Port", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, port, start, stop, export, folder, emails, clear, help, status });
        foreach (var button in toolbar.Controls.OfType<Button>())
        { button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink; button.MinimumSize = new Size(72, 32); button.Padding = new Padding(6, 3, 6, 3); }
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var rawTab = new TabPage("Selected message / raw source"); rawTab.Controls.Add(detail);
        var activityTab = new TabPage("Server activity"); activityTab.Controls.Add(activity);
        var settingsTab = new TabPage("Forwarding and Windows service");
        var settingsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };
        settingsTab.Controls.Add(settingsPanel);
        void Row(string label, params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            row.Controls.Add(new Label { Text = label, AutoSize = true, MinimumSize = new Size(200, 0), Padding = new Padding(0, 5, 0, 0) });
            row.Controls.AddRange(controls); settingsPanel.Controls.Add(row);
        }
        var save = new Button { Text = "Save GUI settings", AutoSize = true };
        Row("Mode", forwarding, direct, save);
        Row("Public EHLO hostname", helo);
        Row("DKIM", dkim);
        Row("DKIM domain / selector", dkimDomain, dkimSelector);
        var generateDkim = new Button { Text = "Generate DKIM key", AutoSize = true };
        Row("Signing key", generateDkim);
        Row("DNS record to publish", dkimDns);
        void RefreshDkimDns()
        {
            try { dkimDns.Text = protectedDkimKey.Length == 0 ? "Generate a key to see the DNS TXT record." : DkimSigning.DnsRecord(protectedDkimKey, dkimDomain.Text.Trim(), dkimSelector.Text.Trim()); }
            catch (Exception ex) { dkimDns.Text = ex.Message; }
        }
        generateDkim.Click += (_, _) =>
        {
            if (protectedDkimKey.Length > 0 && MessageBox.Show(this, "Replace the existing signing key? Publish the new DNS record before enabling signing.", "Rotate DKIM key", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            protectedDkimKey = Secret.Protect(rsa.ExportRSAPrivateKeyPem());
            RefreshDkimDns();
        };
        dkimDomain.TextChanged += (_, _) => RefreshDkimDns();
        dkimSelector.TextChanged += (_, _) => RefreshDkimDns();
        direct.CheckedChanged += (_, _) => { if (direct.Checked) forwarding.Checked = false; };
        forwarding.CheckedChanged += (_, _) => { if (forwarding.Checked) direct.Checked = false; };
        settingsPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "Publish the DKIM TXT record before enabling signing. Save/apply settings to retain the generated key. Direct delivery uses outbound TCP 25 and recipient MX records; no upstream credentials are used." });
        Row("Message files", saveEml, new Label { AutoSize = true, Text = "Daily logs retained for 30 days", Padding = new Padding(0, 5, 0, 0) });
        Row("GUI emails folder", new TextBox { ReadOnly = true, Width = 520, Text = AppSettings.EmailDirectory });
        Row("Service emails folder", new TextBox { ReadOnly = true, Width = 520, Text = Path.Combine(AppSettings.InstallDirectory, "Emails") });
        saveEml.CheckedChanged += (_, _) => saveEml.Text = saveEml.Checked ? ".eml saving enabled" : "Enable .eml saving";
        Row("Upstream SMTP", host, upstreamPort, tls);
        Row("Upstream username", username);
        Row("Upstream password", password);
        settingsPanel.Controls.Add(new Label { Text = "Manage allowed IP addresses and CIDR subnets on the Relay access tab. Choose upstream forwarding or direct delivery.", AutoSize = true });
        var install = new Button { Text = "Install service", AutoSize = true };
        var apply = new Button { Text = "Apply settings to service", AutoSize = true };
        var load = new Button { Text = "Load service settings", AutoSize = true };
        Row("Windows service", install, apply, load);
        var startService = new Button { Text = "Start service", AutoSize = true };
        var stopService = new Button { Text = "Stop service", AutoSize = true };
        var remove = new Button { Text = "Uninstall service", AutoSize = true };
        Row("Service controls", startService, stopService, remove, serviceStatus);
        Row("Logging", viewService);
        Row("GUI settings file", new TextBox { ReadOnly = true, Width = 520, Text = AppSettings.UserConfig });
        Row("Service settings file", new TextBox { ReadOnly = true, Width = 520, Text = AppSettings.ServiceConfig });
        settingsPanel.Controls.Add(new Label { AutoSize = true, Text = "Service controls request Windows administrator approval. Stop the listener/service before switching or applying settings.\r\nInstalled service starts automatically with Windows. Closing this window leaves the service running." });
        var accessTab = new TabPage("Relay access");
        var accessLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        accessLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); accessLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); accessLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accessLayout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Allowed relay clients — enter one IP address or CIDR subnet per line.\r\nExamples: 192.168.1.50   •   10.20.0.0/16   •   2001:db8::/32\r\nRules apply to upstream and direct relay. Stop the listener before editing; save/apply in Forwarding and Windows service.", Padding = new Padding(0, 0, 0, 10) }, 0, 0);
        var accessSplit = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(900, 350), SplitterDistance = 350 };
        accessSplit.Panel1.Controls.Add(allowed);
        var preview = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window };
        preview.Columns.Add("Network", "Effective IP / network"); preview.Columns.Add("Type", "Rule type"); accessSplit.Panel2.Controls.Add(preview);
        accessLayout.Controls.Add(accessSplit, 0, 1);
        var validation = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        accessLayout.Controls.Add(validation, 0, 2); accessTab.Controls.Add(accessLayout);
        void PreviewRules()
        {
            preview.Rows.Clear(); int errors = 0;
            foreach (var entry in RelayAccess.Entries(allowed.Text))
            {
                try { var rule = RelayNetwork.Parse(entry); preview.Rows.Add(rule.Network, rule.Type); }
                catch (ArgumentException) { int row = preview.Rows.Add(entry, "Invalid rule"); preview.Rows[row].DefaultCellStyle.ForeColor = Color.Firebrick; errors++; }
            }
            validation.Text = errors > 0 ? $"{errors} invalid rule(s). Correct the highlighted entries before saving." : $"{preview.Rows.Count} valid rule(s). Subnets are normalized to their network address in the preview.";
            validation.ForeColor = errors > 0 ? Color.Firebrick : SystemColors.ControlText;
        }
        allowed.TextChanged += (_, _) => PreviewRules(); PreviewRules();
        var tlsTab = new TabPage("Incoming TLS");
        var tlsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
        tlsTab.Controls.Add(tlsPanel);
        certStore.Items.AddRange(new object[] { "My", "WebHosting" }); certStore.SelectedIndex = 0;
        tlsPanel.Controls.Add(incomingTls); tlsPanel.Controls.Add(requireTls);
        tlsPanel.Controls.Add(new Label { AutoSize = true, Text = "Local Computer certificate store (My = Personal)", Padding = new Padding(0, 12, 0, 0) }); tlsPanel.Controls.Add(certStore);
        tlsPanel.Controls.Add(new Label { AutoSize = true, Text = "Certificate friendly name — exact match, case-insensitive", Padding = new Padding(0, 10, 0, 0) }); tlsPanel.Controls.Add(certName);
        var certButtons = new FlowLayoutPanel { AutoSize = true };
        var refreshCerts = new Button { Text = "Refresh friendly names", AutoSize = true };
        var checkCert = new Button { Text = "Check certificate", AutoSize = true };
        certButtons.Controls.AddRange(new Control[] { refreshCerts, checkCert }); tlsPanel.Controls.Add(certButtons);
        var certResult = new TextBox { Width = 760, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        tlsPanel.Controls.Add(certResult);
        tlsPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "SMTPNG selects the newest valid matching certificate with an accessible private key for every STARTTLS handshake. Renewed certificates are used without restarting the listener.\r\nThe sending system must connect using a DNS name on the certificate. The service account needs permission to read its private key.\r\nSave GUI settings or Apply settings to service in the Forwarding and Windows service tab. See README for win-acme renewal setup.", Padding = new Padding(0, 8, 0, 0) });
        refreshCerts.Click += (_, _) =>
        {
            try
            {
                var text = certName.Text;
                using var store = new X509Store(certStore.Text, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var certificates = store.Certificates;
                try
                {
                    certName.Items.Clear(); certName.Items.AddRange(certificates.Cast<X509Certificate2>().Where(c => c.HasPrivateKey && c.FriendlyName.Length > 0).Select(c => c.FriendlyName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).Cast<object>().ToArray());
                    certName.Text = text; certResult.Text = $"Found {certName.Items.Count} friendly name(s) in Local Computer\\{certStore.Text}.";
                }
                finally { foreach (var cert in certificates) cert.Dispose(); }
            }
            catch (Exception ex) { certResult.Text = ex.Message; }
        };
        checkCert.Click += (_, _) =>
        {
            try
            {
                using var cert = CertificateSelector.Select(new AppSettings { CertificateStore = certStore.Text, CertificateFriendlyName = certName.Text.Trim() });
                certResult.Text = $"Usable as current GUI user: {cert.Subject}\r\nFriendly name: {cert.FriendlyName}\r\nExpires: {cert.NotAfter:yyyy-MM-dd HH:mm:ss}\r\nThumbprint: {cert.Thumbprint}\r\nService private-key permissions are checked separately when the service starts.";
            }
            catch (Exception ex) { certResult.Text = ex.Message; }
        };
        var dmarcTab = new TabPage("DMARC setup");
        var dmarcPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        dmarcTab.Controls.Add(dmarcPanel);
        void DmarcRow(string label, params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(850, 0) };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 200, Padding = new Padding(0, 5, 0, 0) });
            row.Controls.AddRange(controls); dmarcPanel.Controls.Add(row);
        }
        dmarcPolicy.Items.AddRange(new object[] { "none", "quarantine", "reject" }); dmarcPolicy.SelectedIndex = 0;
        foreach (var combo in new[] { dmarcDkimAlignment, dmarcSpfAlignment }) { combo.Items.AddRange(new object[] { "r", "s" }); combo.SelectedIndex = 0; }
        dmarcPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "Publish DMARC in your domain's DNS. Start with none (monitoring); review reports and all authorized senders before using quarantine or reject. This policy affects all mail using the domain, including mail sent outside SMTPNG." });
        DmarcRow("Visible From domain", dmarcDomain);
        DmarcRow("Failure policy", dmarcPolicy);
        DmarcRow("Aggregate report email (optional)", dmarcReport);
        DmarcRow("DKIM / SPF alignment", dmarcDkimAlignment, dmarcSpfAlignment);
        dmarcPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "r = relaxed; s = strict. Use an existing mailbox or report service for reports; SMTPNG does not receive or analyze them. External reporting destinations may require a DNS authorization record from that provider." });
        var dmarcRecord = new TextBox { Width = 800, Height = 90, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both };
        DmarcRow("DNS TXT record to publish", dmarcRecord);
        void RefreshDmarc()
        {
            try { dmarcRecord.Text = "Name: _dmarc." + DmarcSetup.Domain(dmarcDomain.Text) + "\r\nType: TXT\r\nValue: " + DmarcSetup.Record(dmarcDomain.Text, dmarcPolicy.Text, dmarcReport.Text.Trim(), dmarcDkimAlignment.Text, dmarcSpfAlignment.Text); }
            catch (ArgumentException ex) { dmarcRecord.Text = ex.Message; }
        }
        foreach (var control in new Control[] { dmarcDomain, dmarcPolicy, dmarcReport, dmarcDkimAlignment, dmarcSpfAlignment }) control.TextChanged += (_, _) => RefreshDmarc();
        var checkDmarc = new Button { Text = "Check published DNS and DKIM alignment", AutoSize = true };
        var saveDmarc = new Button { Text = "Save GUI settings", AutoSize = true };
        DmarcRow("", checkDmarc, saveDmarc);
        var dmarcResult = new TextBox { Width = 800, Height = 190, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
        DmarcRow("Check results", dmarcResult);
        saveDmarc.Click += async (_, _) => { try { if (await SaveGuiSettings(ReadSettings())) dmarcResult.Text = "GUI settings saved. DNS must be published separately."; } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        checkDmarc.Click += async (_, _) =>
        {
            checkDmarc.Enabled = false; dmarcResult.Text = "Checking DNS...";
            try
            {
                var snapshot = new AppSettings { DmarcDomain = dmarcDomain.Text.Trim(), DmarcPolicy = dmarcPolicy.Text, DmarcReportAddress = dmarcReport.Text.Trim(), DmarcDkimAlignment = dmarcDkimAlignment.Text, DmarcSpfAlignment = dmarcSpfAlignment.Text, DkimEnabled = dkim.Checked, DkimDomain = dkimDomain.Text.Trim(), DkimSelector = dkimSelector.Text.Trim(), ProtectedDkimKey = protectedDkimKey };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var result = await DmarcSetup.CheckAsync(snapshot, timeout.Token);
                if (!IsDisposed) dmarcResult.Text = result;
            }
            catch (Exception ex) { if (!IsDisposed) dmarcResult.Text = ex is OperationCanceledException ? "DNS check timed out. Try again." : ex.Message; }
            finally { if (!IsDisposed) checkDmarc.Enabled = true; }
        };
        RefreshDmarc();
        tabs.TabPages.AddRange(new[] { rawTab, activityTab, settingsTab, dmarcTab, accessTab, tlsTab });
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(grid); split.Panel2.Controls.Add(tabs);
        tabs.SelectedIndexChanged += (_, _) => split.Panel1Collapsed = tabs.SelectedTab == settingsTab || tabs.SelectedTab == accessTab || tabs.SelectedTab == tlsTab;
        var note = new Label { Dock = DockStyle.Bottom, Height = 44, Text = "Capture only by default • Optional incoming STARTTLS • Optional upstream or direct delivery and DKIM with relay IP/subnet rules", Padding = new Padding(8) };
        Controls.Add(split); Controls.Add(toolbar); Controls.Add(note);
        foreach (var (property, label, weight) in new[] { ("Received", "Received", 18f), ("From", "From (envelope)", 22f), ("To", "To (envelope)", 28f), ("Subject", "Subject", 35f), ("Remote", "Client", 18f), ("Delivery", "Delivery", 16f) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = label, FillWeight = weight });
        grid.Columns[0].DefaultCellStyle.Format = "yyyy-MM-dd HH:mm:ss";
        grid.DataSource = messages;
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.CurrentRow?.DataBoundItem is CapturedMail mail)
            {
                string raw = mail.Raw;
                try { if (raw.Length == 0 && File.Exists(mail.File)) raw = File.ReadAllText(mail.File); }
                catch (Exception ex) { raw = ex.Message; }
                if (raw.Length == 0 && mail.File.Length == 0) raw = "Message content was not saved. Enable .eml saving to retain future message bodies.";
                detail.Text = $"Saved: {(mail.File.Length == 0 ? "Not saved (.eml saving disabled)" : mail.File)}\r\nStatus: {mail.Delivery}\r\nEnvelope from: <{mail.From}>\r\nEnvelope to: {mail.To}\r\n\r\n{raw}";
            }
            else detail.Clear();
        };
        start.Click += async (_, _) =>
        {
            if (!IPAddress.TryParse(bind.Text.Trim(), out var ip)) { MessageBox.Show(this, "Enter a valid bind IP address."); return; }
            try
            {
                var settings = ReadSettings(); if (!await SaveGuiSettings(settings)) return;
                if (WindowsService.State() is "Running" or "Starting") throw new InvalidOperationException("Stop the Windows service before starting the GUI listener.");
                server = new SmtpServer(settings);
                server.Log += value => Ui(() => { if (!viewService.Checked) Log(value); });
                server.Received += mail => Ui(() =>
                {
                    if (viewService.Checked) return;
                    messages.Insert(0, mail);
                    if (messages.Count > 1000) messages.RemoveAt(messages.Count - 1);
                    status.Text = $"Listening • {messages.Count} in view";
                });
                viewService.Checked = false;
                server.Start(ip, (int)port.Value); start.Enabled = false; stop.Enabled = true; bind.Enabled = port.Enabled = false; settingsPanel.Enabled = false; allowed.Enabled = false; tlsPanel.Enabled = false; status.Text = settings.RelayEnabled ? "Listening / relay" : "Listening / capture";
            }
            catch (Exception ex) { MessageBox.Show(this, $"Cannot start listener: {ex.Message}\n\nCheck whether another program is using this port.", "SMTP listener", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        stop.Click += (_, _) => { server.Stop(); start.Enabled = true; stop.Enabled = false; bind.Enabled = port.Enabled = true; settingsPanel.Enabled = true; allowed.Enabled = true; tlsPanel.Enabled = true; status.Text = "Stopped"; Log("Listener stopped."); };
        help.Click += (_, _) => { try { var path = Path.Combine(AppContext.BaseDirectory, "README.md"); if (!File.Exists(path)) InstallationFiles.Write(AppContext.BaseDirectory); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        clear.Click += (_, _) => { messages.Clear(); detail.Clear(); };
        folder.Click += (_, _) => { try { Directory.CreateDirectory(SelectedStorage); Process.Start(new ProcessStartInfo(SelectedStorage) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        emails.Click += (_, _) =>
        {
            try
            {
                var path = viewService.Checked ? Path.Combine(AppSettings.InstallDirectory, "Emails") : AppSettings.EmailDirectory;
                if (!Directory.Exists(path)) { MessageBox.Show(this, "No saved email folder yet. Enable .eml saving and start the listener or install the service."); return; }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        };
        export.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = "smtp-captures.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
            try
            {
                var lines = new List<string> { "Received,From,To,Subject,Client,MessageFile,Delivery" };
                lines.AddRange(messages.Select(m => string.Join(",", new[] { m.Received.ToString("O"), m.From, m.To, m.Subject, m.Remote, m.File, m.Delivery }.Select(Csv))));
                File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
        };
        forwarding.CheckedChanged += (_, _) => forwarding.Text = forwarding.Checked ? "Forwarding enabled" : "Enable forwarding";
        save.Click += async (_, _) => { try { if (await SaveGuiSettings(ReadSettings())) Log("GUI settings saved. Service settings require Apply settings to service."); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        load.Click += async (_, _) => await ServiceAction("load");
        install.Click += async (_, _) => await ServiceAction("install");
        apply.Click += async (_, _) => await ServiceAction("configure");
        startService.Click += async (_, _) => { if (!stop.Enabled && await ServiceAction("start")) viewService.Checked = true; };
        stopService.Click += async (_, _) => await ServiceAction("stop");
        remove.Click += async (_, _) => await ServiceAction("uninstall");
        viewService.CheckedChanged += (_, _) => { messages.Clear(); activity.Clear(); indexStamp = ""; PollService(); };
        timer.Tick += (_, _) => PollService(); timer.Start();
        try { LoadSettings(AppSettings.LoadGui()); }
        catch (UnauthorizedAccessException) { Shown += async (_, _) => await ServiceAction("load-gui"); }
        catch (Exception ex) { Log($"Settings could not be loaded: {ex.Message}"); }
        PollService();
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("SMTPNG.ico"))
        {
            if (iconStream != null) { using var loaded = new Icon(iconStream); Icon = (Icon)loaded.Clone(); }
        }
        tray.Icon = Icon; tray.Text = "SMTPNG — SMTP capture and relay";
        var trayMenu = new ContextMenuStrip();
        void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        trayMenu.Items.Add("Open SMTPNG", null, (_, _) => RestoreWindow());
        var trayStart = trayMenu.Items.Add("Start GUI listener", null, (_, _) => { RestoreWindow(); start.PerformClick(); });
        var trayStop = trayMenu.Items.Add("Stop GUI listener", null, (_, _) => { RestoreWindow(); stop.PerformClick(); });
        trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add("Exit SMTPNG", null, (_, _) => Close());
        trayMenu.Opening += (_, _) => { trayStart.Enabled = start.Enabled; trayStop.Enabled = stop.Enabled; };
        tray.ContextMenuStrip = trayMenu; tray.DoubleClick += (_, _) => RestoreWindow(); tray.Visible = true;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, _) => { tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); timer.Stop(); timer.Dispose(); server.Stop(); };
    }

    private AppSettings ReadSettings()
    {
        var settings = new AppSettings { BindAddress = bind.Text.Trim(), Port = (int)port.Value, Forwarding = forwarding.Checked, DirectDelivery = direct.Checked, HeloHostname = helo.Text.Trim(), DkimEnabled = dkim.Checked, DkimDomain = dkimDomain.Text.Trim(), DkimSelector = dkimSelector.Text.Trim(), ProtectedDkimKey = protectedDkimKey, UpstreamHost = host.Text.Trim(), UpstreamPort = (int)upstreamPort.Value, StartTls = tls.Checked, Username = username.Text, ProtectedPassword = Secret.Protect(password.Text), AllowedClients = string.Join(";", RelayAccess.Entries(allowed.Text)), IncomingStartTls = incomingTls.Checked, RequireIncomingTls = requireTls.Checked, CertificateStore = certStore.Text, CertificateFriendlyName = certName.Text.Trim(), SaveEml = saveEml.Checked, DmarcDomain = dmarcDomain.Text.Trim(), DmarcPolicy = dmarcPolicy.Text, DmarcReportAddress = dmarcReport.Text.Trim(), DmarcDkimAlignment = dmarcDkimAlignment.Text, DmarcSpfAlignment = dmarcSpfAlignment.Text };
        settings.Validate(); return settings;
    }

    private void LoadSettings(AppSettings settings)
    {
        bind.Text = settings.BindAddress; port.Value = settings.Port; forwarding.Checked = settings.Forwarding;
        host.Text = settings.UpstreamHost; upstreamPort.Value = settings.UpstreamPort; tls.Checked = settings.StartTls;
        username.Text = settings.Username; password.Text = Secret.Unprotect(settings.ProtectedPassword); allowed.Text = string.Join(Environment.NewLine, RelayAccess.Entries(settings.AllowedClients));
        incomingTls.Checked = settings.IncomingStartTls; requireTls.Checked = settings.RequireIncomingTls; certStore.SelectedItem = settings.CertificateStore; certName.Text = settings.CertificateFriendlyName;
        saveEml.Checked = settings.SaveEml;
        dmarcDomain.Text = settings.DmarcDomain; dmarcPolicy.SelectedItem = settings.DmarcPolicy; dmarcReport.Text = settings.DmarcReportAddress;
        dmarcDkimAlignment.SelectedItem = settings.DmarcDkimAlignment; dmarcSpfAlignment.SelectedItem = settings.DmarcSpfAlignment;
        direct.Checked = settings.DirectDelivery; helo.Text = settings.HeloHostname;
        protectedDkimKey = settings.ProtectedDkimKey; dkim.Checked = settings.DkimEnabled;
        dkimDomain.Text = settings.DkimDomain; dkimSelector.Text = settings.DkimSelector;
        dkimDns.Text = protectedDkimKey.Length == 0 ? "Generate a key to see the DNS TXT record." : DkimSigning.DnsRecord(protectedDkimKey, settings.DkimDomain, settings.DkimSelector);
    }

    private async Task<bool> SaveGuiSettings(AppSettings settings)
    {
        try { settings.Save(AppSettings.UserConfig); return true; }
        catch (UnauthorizedAccessException) { return await ServiceAction("save-gui"); }
    }

    private async Task<bool> ServiceAction(string action)
    {
        var config = Path.Combine(Path.GetTempPath(), $"DummySmtp-{Guid.NewGuid():N}.json");
        var result = config + ".result";
        try
        {
            if (stop.Enabled) throw new InvalidOperationException("Stop the GUI listener first.");
            if (action is "install" or "configure" or "save-gui") ReadSettings().Save(config);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.Arguments = $"--service-action {action} \"{config}\" \"{result}\"";
            Enabled = false;
            using var process = Process.Start(info) ?? throw new IOException("Could not start service management.");
            await process.WaitForExitAsync();
            var outcome = File.Exists(result) ? File.ReadAllText(result) : "Service management did not return a result.";
            if (outcome != "OK") throw new InvalidOperationException(outcome);
            if (action is "load" or "load-gui") LoadSettings(AppSettings.Load(config));
            Log($"Service action completed: {action}." );
            if (action is "install" or "configure") MessageBox.Show(this, "Service settings saved. Click Start service to begin listening.", "Windows service");
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Windows service", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }
        finally
        {
            Enabled = true;
            try { File.Delete(config); File.Delete(result); } catch (IOException) { }
            PollService();
        }
    }

    private void PollService()
    {
        serviceStatus.Text = "Service: " + WindowsService.State();
        if (!viewService.Checked) return;
        try
        {
            var log = DailyLogs.Files(SelectedStorage, "activity").FirstOrDefault();
            var text = log == null ? "" : DailyLogs.Tail(log);
            if (activity.Text != text) activity.Text = text;
            var indexes = DailyLogs.Files(SelectedStorage, "messages");
            var stamp = string.Join("|", indexes.Select(p => p + ":" + new FileInfo(p).Length));
            if (stamp == indexStamp) return;
            // Service appends may race with reads; retry incomplete JSON lines on the next tick.
            var rows = new List<CapturedMail>();
            foreach (var index in indexes)
            {
                if (rows.Count >= 1000) break;
                using var stream = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bool tail = stream.Length > 4 * 1024 * 1024;
                if (tail) stream.Seek(-4 * 1024 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                if (tail) reader.ReadLine();
                var dayRows = new List<CapturedMail>();
                while (reader.ReadLine() is { } line)
                {
                    var mail = JsonSerializer.Deserialize<CapturedMail>(line);
                    if (mail != null) dayRows.Add(mail);
                    if (dayRows.Count > 1000) dayRows.RemoveAt(0);
                }
                rows.AddRange(dayRows.OrderByDescending(m => m.Received).Take(1000 - rows.Count));
            }
            messages.RaiseListChangedEvents = false;
            messages.Clear(); foreach (var mail in rows.OrderByDescending(m => m.Received)) messages.Add(mail);
            messages.RaiseListChangedEvents = true; messages.ResetBindings(); indexStamp = stamp;
            status.Text = $"Service captures • {messages.Count} in view";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* Retry during next poll. */ }
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }
    private void Log(string text)
    {
        if (activity.TextLength > 200000) activity.Clear();
        activity.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
    }
}
