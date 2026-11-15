# SMTPNG — SMTP capture and optional relay

SMTPNG accepts SMTP email and logs subjects, envelope senders and recipients. Capture-only is the default. Optional relay sends mail through an upstream SMTP server or directly to recipient MX servers, with optional DKIM signing. Incoming STARTTLS and upstream STARTTLS are configured separately.

## Installation and opening the GUI

Extract the whole ZIP and run `SMTPNG.exe`. The self-contained Windows x64 release includes the .NET runtime; no separate runtime installation is needed. Framework-dependent builds require the .NET 10 Windows Desktop Runtime. SMTPNG 1.5 targets `net10.0-windows`; building from source requires the .NET 10 SDK or newer.

To install: stop the GUI listener, configure settings, open **Forwarding and Windows service**, and click **Install service**. Approve Windows administrator elevation. Files are copied from the folder containing the executable you launched into:

```text
C:\Program Files\SMTPNG
```

Installation creates this README and `Set-SMTPNGCertificateName.ps1` from resources embedded in the application; it does not depend on the source folder having a README. Click **Start service** afterward. The service starts automatically with Windows and keeps running when the GUI is closed. It runs as LocalSystem, using internal service ID `DummySmtpCapture` (retained for compatibility) and display name **SMTPNG**.

To reopen the installed GUI, run `C:\Program Files\SMTPNG\SMTPNG.exe` without arguments. Select **View service captures** to see mail received by the service. Minimize hides the window to the system tray. Double-click the mail icon to reopen; right-click for Open, Start/Stop GUI listener, and Exit. Closing the GUI stops only its own listener.

## Settings are beside the installed executable

| File | Purpose |
| --- | --- |
| `C:\Program Files\SMTPNG\settings.json` | Service configuration, written by **Apply settings to service** |
| `C:\Program Files\SMTPNG\gui-settings.json` | GUI listener configuration, written by **Save GUI settings** or starting the GUI listener |
| `C:\Program Files\SMTPNG\README.md` | This operating guide; also available through the GUI **README** button |
| `C:\Program Files\SMTPNG\Set-SMTPNGCertificateName.ps1` | Optional win-acme certificate-renewal hook |

When run from an extracted/portable folder, GUI settings are saved as `gui-settings.json` beside that copy of `SMTPNG.exe`. The installed service always uses its installed `settings.json`. The two settings files are independent: changing the GUI does not change a running service.

Installed settings files are restricted to Administrators and SYSTEM. The GUI requests administrator approval to read or save protected installed settings. Upstream passwords use machine-scope Windows DPAPI encryption and are never printed in the README. Do not distribute settings containing credentials.

Stop the service, wait for **Stopped**, edit fields, click **Apply settings to service**, and then **Start service**. **Load service settings** imports the installed service configuration into the GUI fields. Certificate renewals do not require this cycle; changing TLS settings or the selected friendly name does.

Older `%LOCALAPPDATA%\DummySmtp\settings.json` settings are loaded as a fallback if no GUI settings file exists. **Load service settings** can read the legacy `%PROGRAMDATA%\DummySmtp\settings.json` if the new service file is missing. Old files are retained. Save/apply to create settings at the new location. To update an older installed service, stop/uninstall it, run the new extracted GUI, load or choose your settings, and install again.

## Listener and capture

Default bind IP is `0.0.0.0`, port **25**. This listens on every IPv4 interface; `127.0.0.1` allows only local senders. Remote senders use this computer's actual IP or DNS name. Windows Firewall may need an inbound TCP rule; SMTPNG does not change firewall settings.

The grid shows time, envelope From/To addresses, decoded subject, client and capture/forwarding status. Envelope addresses can differ from message headers. Select a message for available raw source. **Export CSV** exports the current view. **Clear view** does not delete files.

GUI logs: `%LOCALAPPDATA%\DummySmtp\Messages`.

Service logs: `%PROGRAMDATA%\DummySmtp\Messages`.

### Daily log rotation and 30-day retention

SMTPNG creates separate files for each local calendar day:

* `activity-YYYY-MM-DD.log` — listener, TLS and forwarding activity/errors.
* `messages-YYYY-MM-DD.jsonl` — message metadata, including subject, sender, recipients, client, status and optional saved `.eml` path. Message bodies are never written to this metadata log.
* `service-error-YYYY-MM-DD.log` — service startup failures, in the service log folder above.

Logs retain **30 calendar days: today and the previous 29 days**. New entries automatically use the new day's file. Cleanup runs at listener/service startup and checks every minute while running, removing expired logs after the date changes even when no email arrives. If stopped, cleanup resumes at the next start. Cleanup failures are reported in activity logs. There is no additional size limit within an individual day.

Old `activity.log`, `messages.jsonl` and `service-error.log` files are moved into dated `.legacy-...` archives on upgrade/startup and then follow the same retention rule. Their archive date is the old file's last-write date; mixed historical entries inside a legacy file are not split by entry date. Other files are not removed. The old root-level service-error.log is imported into the service log folder at service startup.

**Open logs** opens the currently selected GUI/service log folder. Local Windows users can read service logs for GUI monitoring. The GUI shows up to 1,000 metadata entries across the retained daily files and refreshes service monitoring every two seconds. Each day's metadata tail is limited to 4 MB. The Server activity tab shows the newest available activity log tail.

### Optional saved email files

Saving complete emails is **off by default**, including when upgrading settings without a `SaveEml` property. Open **Forwarding and Windows service** and click **Enable .eml saving**. The button changes to **.eml saving enabled**. Save GUI settings or apply settings to the stopped service, then start it. Click the button again to disable saving.

When enabled, new `.eml` files are stored in an **Emails subfolder beside the running application**:

```text
Installed GUI/service: C:\Program Files\SMTPNG\Emails
Portable GUI:         <folder containing SMTPNG.exe>\Emails
```

**Open saved emails** opens the appropriate GUI/service email folder. Installation/apply creates the installed Emails subfolder and gives the installing/configuring Windows user Modify access to that subfolder only; SYSTEM/Administrators retain their normal access. Other users may need permission to save there or may run the GUI as administrator. SMTPNG checks directory write access before starting when saving is enabled.

With saving disabled, message metadata and forwarding status are still logged, and forwarding works normally. The GUI listener can show raw content while it remains in memory, but the service cannot show unsaved content later. Failed forwards also produce no `.eml` unless saving is enabled. The metadata saved-file path is empty when no file was saved.

The 30-day log retention policy **does not delete `.eml` files**. Disabling saving affects future messages and does not remove existing emails. Older `.eml` files in previous capture folders are left in place; they are not moved automatically. Manage saved emails separately as needed.

## Incoming TLS with a Local Computer certificate

1. Obtain a server certificate for the SMTP DNS hostname clients will use, for example `smtp.example.com`. Install it with its private key in **Certificates (Local Computer) > Personal > Certificates** (`My`), or in the `WebHosting` machine store. Install any required intermediate certificates too.
2. Give the certificate a stable friendly name, for example `SMTPNG`. The friendly name is only SMTPNG's local selector; clients validate the DNS hostname in the certificate, not this friendly name.
3. Stop the listener/service. Open **Incoming TLS**, check **Enable incoming STARTTLS**, select `My` or `WebHosting`, and enter the friendly name. Matching is exact and case-insensitive. **Refresh friendly names** lists names from the selected machine store. **Check certificate** displays the selected certificate, expiry and thumbprint and checks private-key use as the current GUI user.
4. Optionally check **Require TLS before accepting mail**. When required, plaintext MAIL FROM receives SMTP 530 until the connection upgrades with STARTTLS. When not required, both plaintext and STARTTLS clients are accepted.
5. Save GUI settings or apply service settings, then start the listener/service. The service separately checks key access as LocalSystem when it starts.

The certificate must be currently valid, have a usable RSA/ECDSA private key, and allow server authentication (or have no EKU restrictions). The running GUI user or service identity must be able to read/use the private key. In `certlm.msc`, select the certificate and use **All Tasks > Manage Private Keys** to grant the needed identity read access if necessary. A successful check in the GUI does not prove access as a different service identity.

SMTPNG uses TLS 1.2/1.3 subject to Windows support. This is SMTP STARTTLS on the configured port, not implicit TLS on port 465. Incoming SMTP AUTH and mutual/client-certificate authentication are not implemented. Clients must trust the issuer and connect using a hostname on the certificate. A public Let's Encrypt certificate normally meets public trust requirements; self-signed/internal certificates require appropriate trust on the sending clients. There is no certificate-validation bypass in SMTPNG's upstream forwarding.

## win-acme / Let's Encrypt automatic renewal

Configure win-acme to issue/renew for your SMTP hostname and save the certificate into the **Local Computer** store. Explicitly choose the same store SMTPNG uses, e.g. `--store certificatestore --certificatestore My`. win-acme can otherwise choose `WebHosting` on machines with IIS. Domain validation and the renewal schedule are managed by win-acme, independently of SMTPNG; choose a validation method appropriate for your DNS/network.

To ensure every renewed certificate receives a stable friendly name, configure win-acme's **script installation plugin** to run this installed hook after successful issuance and renewal:

```text
Script: C:\Program Files\SMTPNG\Set-SMTPNGCertificateName.ps1
Parameters: -Thumbprint "{CertThumbprint}" -FriendlyName "SMTPNG" -StoreName "My"
```

The hook runs with win-acme's permissions, needs permission to change certificate properties, and only sets the renewed certificate's friendly name. It does not export keys, delete certificates, or restart services. If you select `WebHosting`, use that value in the script parameters and SMTPNG's store selection. Ensure the renewal process gives the SMTPNG service identity access to the renewed private key too.

For every STARTTLS handshake, SMTPNG reopens the selected store and selects the newest currently valid certificate with that friendly name and an accessible private key. Ordering is newest NotBefore, then latest expiry. No thumbprint is pinned in configuration. TLS session resumption is disabled so new handshakes select the current certificate. Existing connections continue using their established TLS session. A missing/expired/inaccessible certificate prevents listener startup; if it becomes unavailable afterward, new STARTTLS requests receive SMTP 454 and the error is logged. Required-TLS mode continues to reject plaintext mail.

No SMTPNG restart or renewal callback is needed beyond making the renewed certificate available under the same friendly name. Check today's `activity-YYYY-MM-DD.log` after a test connection to confirm the thumbprint actually used. Test your renewal workflow once before relying on it.

Official references: [win-acme certificate store](https://www.win-acme.com/reference/plugins/store/certificatestore), [win-acme script plugin and renewal placeholders](https://www.win-acme.com/reference/plugins/installation/script), [Microsoft SslStream TLS guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-best-practices).

## Optional upstream forwarding and relay access

In **Forwarding and Windows service**, click **Enable forwarding**, enter the upstream host/port, and configure **Require STARTTLS** and credentials. Upstream authentication uses AUTH LOGIN and requires STARTTLS. Certificate hostname/trust validation is enforced. Implicit TLS port 465 is not supported.

In **Relay access**, enter one sending-system IP or CIDR per line. Mix IPv4/IPv6 addresses and subnets, e.g. `192.168.1.50`, `10.20.0.0/16`, `2001:db8::/32`. The preview shows effective networks and invalid rules. Host bits are normalized: `192.168.1.25/24` allows `192.168.1.0/24`. Semicolons/commas are accepted too. Defaults permit only `127.0.0.1` and `::1`. Other clients receive SMTP 550 in forwarding mode. Capture-only does not use the relay allowlist; the incoming TLS requirement applies to both modes.

When `.eml` saving is enabled, a local message file is saved before forwarding; otherwise no message file is saved. **Forwarded** means the upstream accepted DATA, not guaranteed inbox delivery. Upstream failures are logged and return SMTP 451 for the original sender to retry. No background retry queue is provided. A timeout around upstream acceptance can produce duplicates when the sender retries. No background queue, bounce generation or mailbox hosting is provided.

## Direct delivery and DKIM

In **Forwarding and Windows service**, select **Deliver directly to recipient MX** instead of upstream forwarding, and enter your public SMTP hostname in **Public EHLO hostname**. Relay access rules apply in either mode. Capture-only remains the default. Direct mode ignores upstream host, port and credentials.

SMTPNG resolves each recipient domain's MX records using the system DNS resolvers, tries lower-preference values first and falls back to the recipient domain's address when there are no MX records. A null MX rejects delivery. Delivery uses outbound TCP port 25, with STARTTLS when advertised and normal certificate validation. Failed TLS is never retried in plaintext on that host. Direct mode does not implement MTA-STS or DANE.

Your network/provider must permit outbound TCP 25. Configure a public hostname with matching forward/reverse DNS and SPF authorization for your sending IP, and configure DMARC for your domain. Recipient policies and IP reputation can still cause rejection; acceptance by an MX does not guarantee inbox placement.

To enable DKIM (in either relay mode):

1. Enter your sending domain (for example `example.com`) and a selector (for example `smtp2026`).
2. Click **Generate DKIM key**. SMTPNG creates a 2048-bit RSA key and displays the TXT record name and value. Publish that single TXT record in DNS. The displayed zone-file format splits the long value into quoted chunks belonging to the same record.
3. Enable **DKIM signing**, then save GUI settings or apply settings to the stopped service, and restart it. The same saved key is used for signing. DNS publication/propagation is your responsibility; SMTPNG does not verify it automatically.

The private key uses machine-scope Windows DPAPI, like upstream credentials. Keep settings private and retain a protected backup on the same machine. Signing requires exactly one From mailbox matching the configured domain; other From domains fail delivery. Messages are converted to a 7-bit MIME transport before RSA-SHA256 signing with relaxed canonicalization. Captures and saved `.eml` files retain the original incoming message; the outgoing copy carries the signature. Capture-only mode does not sign.

For key rotation use a new selector, generate a new key, publish the new record and apply the settings. Retain the old selector's DNS record while previously sent mail may still be verified. Replacing a key under the same selector requires coordinated DNS propagation.

Delivery is synchronous. Failures return SMTP 451 and are logged; the sending client must retry. With multiple destinations, some may accept before another fails. Retrying the whole message can deliver duplicates to those recipients, as can a lost final DATA response. There is no persistent retry queue or per-recipient retry tracking.

## DMARC setup helper

Open **DMARC setup** and enter the domain in the visible From address. Choose `none` (monitoring), `quarantine` or `reject`, an optional aggregate reporting mailbox, and relaxed (`r`) or strict (`s`) DKIM/SPF alignment. The helper displays the TXT name `_dmarc.yourdomain` and value to publish. Publish only one DMARC record at that name. Save GUI settings to retain the helper choices; apply to service settings if you want those choices retained there as well. The helper does not change delivery or publish DNS.

Start with monitoring and review all legitimate sending systems before publishing an enforcement policy. Reports go to the supplied mailbox/report service; SMTPNG does not host that mailbox or process reports. External reporting services may require authorization DNS records from the provider.

**Check published DNS and DKIM alignment** checks the exact domain's DMARC TXT record, compares the policy with the form, checks exact DKIM/From-domain alignment and compares the published DKIM public key with the configured private key. It uses system DNS resolvers without a local application cache; DNS propagation and resolver caching still apply. Results use a snapshot of the fields when clicked; recheck after changing them.

This is a setup check, not receiver-side DMARC verification. It does not evaluate SPF, inherited/organizational-domain policy, relaxed alignment between different domains, external reporting authorization, every DMARC extension, or actual message signatures at a receiver. A matching DKIM domain/key alone does not establish DMARC pass. Send a test message and inspect receiver Authentication-Results. See the [DMARC specification](https://www.rfc-editor.org/rfc/rfc9989.html).

## Test commands

Plaintext capture, when incoming TLS is not required:

```powershell
Send-MailMessage -SmtpServer '127.0.0.1' -Port 25 -From 'sender@example.test' -To 'recipient@example.test' -Subject 'SMTPNG test' -Body 'Test email'
```

Incoming STARTTLS (use a DNS hostname that matches your certificate):

```powershell
Send-MailMessage -SmtpServer 'smtp.example.com' -Port 25 -UseSsl -From 'sender@example.test' -To 'recipient@example.test' -Subject 'SMTPNG STARTTLS test' -Body 'Encrypted test email'
```

`Send-MailMessage` is deprecated but can be used for this simple Windows PowerShell test. These commands do not authenticate to SMTPNG.

## Troubleshooting and maintenance

* **Port already in use:** stop the GUI listener before starting the service, and vice versa; check other SMTP software using port 25.
* **Service stops during startup:** read today's `service-error-YYYY-MM-DD.log` in the service log folder; check settings, bind IP, port, certificate/key availability and Emails-folder write access if saving is enabled.
* **TLS client rejects the certificate:** check DNS hostname/SAN, expiry, chain/intermediates, and client trust. Friendly name does not replace DNS validation.
* **Certificate changed but old one is still used:** inspect both store and friendly name, NotBefore/expiry, private-key permissions, and the logged handshake thumbprint.
* **SMTP 550:** add the actual source IP or subnet in Relay access, then apply settings and restart the listener/service.
* **SMTP 451:** check today's activity log and upstream connectivity/TLS/credentials. A message file exists only if .eml saving was enabled and the save succeeded.
* **Settings access denied:** accept elevation when prompted; installed settings require administrator rights.
* **Remove service:** stop it, wait for Stopped, then choose Uninstall service. Captures, settings and installed files are retained.

## Build and verification

```powershell
dotnet build -c Release
dotnet publish -c Release -o publish
dotnet run --project ..\SMTPNG.Tests\SMTPNG.Tests.csproj -c Release
```

The included tests exercise DKIM verification after serialization, tamper detection, DNS key consistency, signing-domain enforcement, mode validation, configured EHLO, and direct SMTP transport against a local ephemeral port. Real-world DNS/MX delivery, GUI interaction and Windows service installation require deployment verification.
