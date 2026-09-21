using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Devices;

public sealed class DeviceConnection(string id, string hash, WebSocket socket)
{
    public string DeviceId { get; } = id;
    public string CredentialHash { get; } = hash;
    public WebSocket Socket { get; } = socket;
    private long lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;
    public DateTimeOffset LastSeen { get => new(Interlocked.Read(ref lastSeenTicks), TimeSpan.Zero); set => Interlocked.Exchange(ref lastSeenTicks, value.UtcTicks); }
    public Channel<bool> Notifications { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(2)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
}

public sealed class DeviceConnections
{
    // Serialize replacement and telemetry writes: an obsolete connection must never overwrite its successor.
    public SemaphoreSlim Gate { get; } = new(1);
    private readonly ConcurrentDictionary<string, DeviceConnection> items = new();
    public bool Connected(string id) => items.TryGetValue(id, out var c) && c.Socket.State == WebSocketState.Open
        && c.LastSeen > DateTimeOffset.UtcNow.AddSeconds(-45);
    public bool Current(DeviceConnection c) => items.TryGetValue(c.DeviceId, out var current) && ReferenceEquals(c, current);
    public void Add(DeviceConnection c)
    {
        if (items.TryGetValue(c.DeviceId, out var old)) old.Socket.Abort();
        items[c.DeviceId] = c;
        c.Notifications.Writer.TryWrite(true);
    }
    public void Remove(DeviceConnection c)
    {
        ((ICollection<KeyValuePair<string, DeviceConnection>>)items).Remove(new(c.DeviceId, c));
        c.Notifications.Writer.TryComplete();
    }
    public void Reconcile()
    {
        foreach (var c in items.Values) c.Notifications.Writer.TryWrite(true);
    }
    public void AbortAll()
    {
        foreach (var c in items.Values) c.Socket.Abort();
    }
    public async Task Validate(BackendDbContext db, CancellationToken ct)
    {
        var snapshot = items.Values.ToArray();
        if (snapshot.Length == 0) return;
        var ids = snapshot.Select(c => c.DeviceId).ToArray();
        var allowed = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.DeviceId) && d.DeviceEnabled)
            .ToDictionaryAsync(d => d.DeviceId, d => d.DeviceTokenHash, ct);
        foreach (var c in snapshot)
            if (allowed.GetValueOrDefault(c.DeviceId) != c.CredentialHash || c.LastSeen < DateTimeOffset.UtcNow.AddSeconds(-45))
                c.Socket.Abort();
    }
}
