using DummySmtp;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

internal static class QueueTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "SMTPNG-queue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new AppSettings { Forwarding = true, UpstreamHost = "localhost", MinimumFreeDiskMegabytes = 0, NotifyDeliveryFailures = false };
        const string raw = "From: sender@example.com\r\nTo: user@example.net\r\nSubject: queue test\r\n\r\nDurable body\r\n";
        string Dir(string name) => Path.Combine(root, name);
        try
        {
            using (var store = new QueueStore(Dir("recipients")))
            {
                var id = store.Enqueue("sender@example.com", new[] { "first@example.net", "second@example.net" }, raw, settings);
                var first = store.Claim(1)!;
                check(store.Claim(1) == null, "Per-destination concurrency limit enforced");
                store.Complete(first, null, false);
                var second = store.Claim(1)!; store.Complete(second, "451 try later", false);
                check(store.Overview().Delivered == 1 && store.Overview().Pending == 1, "Partial delivery tracked by recipient");
                check(store.Claim(1) == null, "Retry waits for scheduled backoff");
                store.Retry(second.RecipientId);
                var retried = store.Claim(1)!;
                check(retried.Recipient == "second@example.net", "Manual retry never resends delivered recipient");
                store.Complete(retried, null, false);
                check(store.Overview().BodyBytes == 0 && store.Overview().Delivered == 2, "Body released only after every recipient delivered");
                try { store.ReadBody(first.RecipientId); throw new Exception("Expected missing body"); }
                catch (IOException ex) { check(ex.Message.Contains("released"), "Delivered metadata retained without body"); }
            }
            using (var store = new QueueStore(Dir("permanent")))
            {
                store.Enqueue("sender@example.com", new[] { "bad@example.net" }, raw, settings);
                var job = store.Claim(1)!; store.Complete(job, "550 no mailbox", true);
                check(store.Overview().Failed == 1 && store.ReadBody(job.RecipientId) == raw, "Permanent failures retain original queued body");
                store.SetPaused(true); store.Retry(job.RecipientId);
                check(store.Claim(1) == null && store.Paused, "Pause prevents new delivery claims");
                store.SetPaused(false); check(store.Claim(1) != null, "Resume releases pending delivery");
            }
            using (var store = new QueueStore(Dir("capacity")))
            {
                store.Enqueue("sender@example.com", new[] { "user@example.net" }, raw, settings with { MaxQueueMessages = 1 });
                try { store.Enqueue("sender@example.com", new[] { "other@example.net" }, raw, settings with { MaxQueueMessages = 1 }); throw new Exception("Expected capacity rejection"); }
                catch (QueueCapacityException) { check(store.Overview().Pending == 1, "Full queue rejects new mail without altering accepted mail"); }
            }
            using (var store = new QueueStore(Dir("disk"), freeBytes: () => 0))
            {
                try { store.Enqueue("sender@example.com", new[] { "user@example.net" }, raw, settings); throw new Exception("Expected disk rejection"); }
                catch (QueueCapacityException) { check(store.Overview().Rows.Length == 0, "Low disk space rolls back acceptance atomically"); }
            }
            using (var store = new QueueStore(Dir("expiry")))
            {
                var then = DateTime.UtcNow.AddDays(-6);
                store.Enqueue("sender@example.com", new[] { "user@example.net" }, raw, settings, then);
                var job = store.Claim(1)!; store.Complete(job, "Timeout", false);
                check(store.Overview().Failed == 1, "Expired retries become retained failures");
            }
            check(QueueStore.RetryDelay(settings, 1) == TimeSpan.FromMinutes(30) && QueueStore.RetryDelay(settings, 2) == TimeSpan.FromMinutes(60) && QueueStore.RetryDelay(settings, 100) == TimeSpan.FromDays(1), "Retry backoff grows and caps at one day");
            using (var store = new QueueStore(Dir("notice")))
            {
                var notify = settings with { NotifyDeliveryFailures = true, HeloHostname = "smtp.example.com", MaxQueueMessages = 1 };
                store.Enqueue("sender@example.com", new[] { "bad@example.net" }, raw, notify);
                var job = store.Claim(1)!; store.Complete(job, "550 unknown recipient", true);
                check(store.CreateFailureNotice() && !store.CreateFailureNotice(), "Failure notice queued exactly once in same transaction");
                var bounce = store.Claim(1)!;
                check(bounce.Sender == "" && bounce.Recipient == "sender@example.com", "Bounce uses null reverse path and original envelope sender");
                using var message = MimeMessage.Load(new MemoryStream(Encoding.Latin1.GetBytes(bounce.Raw)));
                check(message.Body is MultipartReport && message.Headers[HeaderId.AutoSubmitted] == "auto-replied", "Bounce is marked automatic multipart delivery report");
                store.Complete(bounce, "550 sender missing", true); store.CreateFailureNotice();
                check(store.Overview().Rows.Length == 2, "Failed bounce does not create a bounce loop");
            }
            check(DeliveryNotice.Create(settings with { NotifyDeliveryFailures = true }, "sender@example.com", "bad@example.net", "550 failed", "id", raw.Replace("Subject:", "Auto-Submitted: auto-generated\r\nSubject:")) == null, "No bounces in response to automated messages");
            var crash = Dir("crash");
            using (var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--queue-abrupt-exit", crash } })!)
            { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); check(process.ExitCode == 0, "Queue producer exited without cleanup"); }
            using (var recovered = new QueueStore(crash))
            {
                var job = recovered.Claim(1)!;
                check(job.Raw.Contains("Durable message") && job.Attempts == 2, "Committed mail and interrupted delivery recovered after process exit");
                try { using var competing = new QueueStore(crash); throw new Exception("Expected exclusive owner"); }
                catch (IOException) { check(true, "Only one worker process owns a queue"); }
            }
            using (var store = new QueueStore(Dir("parallel")))
            {
                await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Task.Run(() => store.Enqueue("sender@example.com", new[] { $"user{i}@example.net" }, raw, settings))));
                check(store.Overview().Pending == 25, "Concurrent producers preserve every accepted message");
            }
            int active = 0, peak = 0, delivered = 0;
            using (var queue = new DeliveryQueue(Dir("workers"), settings with { DeliveryWorkers = 4, ConnectionsPerDestination = 2 }, _ => { }, async (job, session, token) => {
                var count = Interlocked.Increment(ref active); int old;
                do { old = peak; if (old >= count) break; } while (Interlocked.CompareExchange(ref peak, count, old) != old);
                try { await Task.Delay(100, token); Interlocked.Increment(ref delivered); }
                finally { Interlocked.Decrement(ref active); }
            }))
            {
                for (int i = 0; i < 8; i++) queue.Enqueue("sender@example.com", new[] { $"user{i}@example.net" }, raw, settings);
                queue.Start(); await Until(() => queue.Store.Overview().Delivered == 8);
                check(peak <= 2 && peak > 0 && delivered == 8, "Workers deliver every queued recipient within destination limit");
            }
            await TestSmtpAcceptance(root, settings, raw, check);
            await TestSessionReuse(settings, raw, check);
            await TestSmtpFailure(settings, raw, 451, false, check);
            await TestSmtpFailure(settings, raw, 550, true, check);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shutdownDirectory = Dir("shutdown");
            var interrupted = new DeliveryQueue(shutdownDirectory, settings, _ => { }, async (job, session, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            interrupted.Enqueue("sender@example.com", new[] { "recipient@example.net" }, raw, settings);
            interrupted.Start(); await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); interrupted.Dispose();
            using (var restarted = new QueueStore(shutdownDirectory)) check(restarted.Overview().Pending == 1 && restarted.ReadBody(restarted.Overview().Rows[0].RecipientId) == raw, "Shutdown during delivery preserves queued mail for retry");
            using (var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 65537))))
            using (var reader = new SmtpLineReader(stream))
            {
                try { await reader.ReadLineAsync(CancellationToken.None); throw new Exception("Expected bounded line rejection"); }
                catch (IOException) { check(true, "Unterminated oversized SMTP lines cannot grow memory indefinitely"); }
            }
            var source = Dir("install-source"); var destination = Dir("install-destination");
            Directory.CreateDirectory(Path.Combine(source, "runtimes", "win-x64", "native"));
            File.WriteAllText(Path.Combine(source, "SMTPNG.exe"), "app"); File.WriteAllText(Path.Combine(source, "Dependency.dll"), "library");
            File.WriteAllText(Path.Combine(source, "settings.json"), "private settings"); File.WriteAllText(Path.Combine(source, "queue.db"), "private queue");
            File.WriteAllText(Path.Combine(source, "runtimes", "win-x64", "native", "e_sqlite3.dll"), "native library");
            WindowsService.CopyApplicationFiles(source, destination);
            check(File.Exists(Path.Combine(destination, "Dependency.dll")) && File.Exists(Path.Combine(destination, "runtimes", "win-x64", "native", "e_sqlite3.dll")) && !File.Exists(Path.Combine(destination, "settings.json")) && !File.Exists(Path.Combine(destination, "queue.db")), "Service install copies runtime/dependencies without private mutable files");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }
    private static async Task TestSmtpAcceptance(string root, AppSettings settings, string raw, Action<bool, string> check)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var local = settings with { BindAddress = "127.0.0.1", Port = port, MaxIncomingConnections = 1, MaxQueueMessages = 1, StartTls = false };
        var path = Path.Combine(root, "server"); var server = new SmtpServer(local, path);
        try
        {
            server.Start(IPAddress.Loopback, port); server.Queue!.SetPaused(true);
            using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, port);
            using var stream = peer.GetStream(); using var reader = new StreamReader(stream); using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            check((await reader.ReadLineAsync())!.StartsWith("220"), "SMTP listener ready with queue");
            using (var overload = new TcpClient()) { await overload.ConnectAsync(IPAddress.Loopback, port); using var busy = new StreamReader(overload.GetStream()); check((await busy.ReadLineAsync())!.StartsWith("421"), "Connection overload returns 421 for sender retry"); }
            async Task<string> Command(string command) { await writer.WriteLineAsync(command); string line; do { line = (await reader.ReadLineAsync())!; } while (line.Length > 3 && line[3] == '-'); return line; }
            await Command("EHLO test");
            async Task<string> Send()
            {
                await Command("MAIL FROM:<sender@example.com>"); await Command("RCPT TO:<user@example.net>"); await Command("DATA");
                await writer.WriteAsync(raw); return await Command(".");
            }
            var accepted = await Send();
            using (var disk = new QueueStore(server.QueueDirectory, owner: false)) check(accepted.StartsWith("250") && disk.Overview().Pending == 1 && disk.ReadBody(disk.Overview().Rows[0].RecipientId) == raw, "SMTP 250 is issued only after durable message is readable on disk");
            check((await Send()).StartsWith("451") && server.Queue.Overview().Pending == 1, "SMTP full queue returns 451 while preserving accepted mail");
            await Command("QUIT");
        }
        finally { server.Stop(); }
        using var recovered = new QueueStore(Path.Combine(path, "Queue"));
        check(recovered.Overview().Pending == 1 && recovered.Paused, "Queued message and pause state survive listener restart");
    }
    private static async Task TestSmtpFailure(AppSettings settings, string raw, int code, bool permanent, Action<bool, string> check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () => {
            using var peer = await listener.AcceptTcpClientAsync(); using var stream = peer.GetStream();
            using var reader = new StreamReader(stream); using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 ready"); await reader.ReadLineAsync(); await writer.WriteLineAsync("250 hello");
            await reader.ReadLineAsync(); await writer.WriteLineAsync("250 sender"); await reader.ReadLineAsync(); await writer.WriteLineAsync($"{code} mailbox unavailable");
        });
        try
        {
            try { await SmtpForwarder.SendToHostAsync(settings with { StartTls = false }, "127.0.0.1", port, "sender@example.com", new[] { "recipient@example.net" }, raw, CancellationToken.None); throw new Exception("Expected SMTP rejection"); }
            catch (SmtpDeliveryException ex) { check(ex.Permanent == permanent, $"SMTP {code} classified as {(permanent ? "permanent failure" : "temporary retry")}"); }
            await serve;
        }
        finally { listener.Stop(); }
    }

    private static async Task TestSessionReuse(AppSettings settings, string raw, Action<bool, string> check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () => {
            using var peer = await listener.AcceptTcpClientAsync(); using var stream = peer.GetStream(); using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 test"); await reader.ReadLineAsync(); await writer.WriteLineAsync("250 test");
            for (int transaction = 0; transaction < 2; transaction++)
            {
                if (transaction > 0) { if (await reader.ReadLineAsync() != "NOOP") throw new Exception("Expected NOOP before connection reuse"); await writer.WriteLineAsync("250 still ready"); }
                if (!(await reader.ReadLineAsync())!.StartsWith("MAIL FROM:")) throw new Exception("Expected reused MAIL transaction");
                await writer.WriteLineAsync("250 sender"); await reader.ReadLineAsync(); await writer.WriteLineAsync(transaction == 0 ? "251 forwarded recipient" : "250 recipient");
                await reader.ReadLineAsync(); await writer.WriteLineAsync("354 data");
                while ((await reader.ReadLineAsync()) != ".") { }
                await writer.WriteLineAsync("250 accepted");
            }
        });
        try
        {
            await using var session = new SmtpSession();
            var route = settings with { StartTls = false };
            for (int i = 0; i < 2; i++) await session.SendAsync(route, "127.0.0.1", port, "sender@example.com", new[] { "recipient@example.net" }, raw, CancellationToken.None);
            await server.WaitAsync(TimeSpan.FromSeconds(10)); check(true, "Upstream connection reused for multiple messages and RCPT 251 accepted");
        }
        finally { listener.Stop(); }
    }
}
