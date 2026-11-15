namespace DummySmtp;

internal sealed class DeliveryQueue : IDisposable
{
    private readonly QueueStore store;
    private readonly AppSettings settings;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stop = new();
    private Task[] workers = Array.Empty<Task>();
    private readonly Func<DeliveryJob, SmtpSession, CancellationToken, Task>? deliver;
    internal QueueStore Store => store;
    private volatile bool faulted;
    internal string Enqueue(string sender, IReadOnlyList<string> recipients, string raw, AppSettings route)
    {
        if (faulted) throw new IOException("Queue storage fault; resolve the alert and restart the listener.");
        return store.Enqueue(sender, recipients, raw, route);
    }
    internal DeliveryQueue(string directory, AppSettings settings, Action<string> log, Func<DeliveryJob, SmtpSession, CancellationToken, Task>? deliver = null)
    { store = new(directory); this.settings = settings; this.log = log; this.deliver = deliver; }
    internal void Start() => workers = Enumerable.Range(0, settings.DeliveryWorkers).Select(_ => Task.Run(WorkAsync)).ToArray();
    private void Log(string text) { try { log(text); } catch { /* Logging failure must never undo queue state. */ } }
    private async Task WorkAsync()
    {
        await using var session = new SmtpSession();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (faulted) { await Task.Delay(Timeout.Infinite, stop.Token); break; }
                var job = store.Claim(settings.ConnectionsPerDestination);
                if (job == null)
                {
                    // Notices are durable queue messages, with null reverse paths to prevent bounce loops.
                    if (!store.Paused && store.CreateFailureNotice()) continue;
                    await Task.Delay(1000, stop.Token); continue;
                }
                string? error = null; bool permanent = false;
                try
                {
                    if (DateTime.UtcNow >= job.CreatedUtc.AddDays(job.Settings.RetryDays)) throw new SmtpDeliveryException("Delivery retry lifetime expired.", true);
                    if (deliver != null) await deliver(job, session, stop.Token);
                    else if (job.Settings.DirectDelivery) await DirectDelivery.SendAsync(job.Settings, job.Sender, new[] { job.Recipient }, job.Raw, stop.Token);
                    else await session.SendAsync(job.Settings, job.Settings.UpstreamHost, job.Settings.UpstreamPort, job.Sender, new[] { job.Recipient }, job.Raw, stop.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    error = stop.IsCancellationRequested ? "Delivery interrupted by shutdown; retrying." : ex.Message;
                    permanent = ex is SmtpDeliveryException { Permanent: true };
                }
                store.Complete(job, error, permanent);
                Log($"Queue {job.MessageId} recipient {job.Recipient}: {(error == null ? "Delivered" : permanent ? "Failed: " + error : "Retry scheduled: " + error)}");
            }
            catch (QueueCapacityException ex)
            {
                Log("QUEUE ALERT: delivery notice waiting for capacity: " + ex.Message);
                try { await Task.Delay(30000, stop.Token); } catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                faulted = true;
                Log("QUEUE ALERT: " + ex.Message + ". Delivery state retained; restart after resolving storage errors.");
                // A failed state commit must not trigger another network attempt. Keep the owner
                // lock until shutdown; restart recovers interrupted recipients from durable state.
                try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    public void Dispose()
    {
        stop.Cancel();
        // Every network operation observes cancellation. Wait before releasing the queue owner lock.
        Task.WhenAll(workers).GetAwaiter().GetResult();
        store.Dispose(); stop.Dispose();
    }
}
