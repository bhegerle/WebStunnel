﻿using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;

namespace WebStunnel;

using static Timeouts;

internal record SocketSnapshot(ImmutableDictionary<SocketId, Socket> Sockets, Lifetime Lifetime) : IDisposable {
    public void Dispose() {
        Lifetime.Dispose();
    }
}

internal interface ISocketMap : IDisposable {
    Task<Socket> GetSocket(SocketId id, CancellationToken token);
    Task RemoveSocket(SocketId id);
    Task Reset();

    Task<SocketSnapshot> Snapshot();
    Task Detach(SocketSnapshot snap);
}

class SocketMap : ISocketMap {
    private readonly SemaphoreSlim mutex;
    private readonly Dictionary<SocketId, Socket> sockMap;
    private Lifetime lastSnapLifetime; // owned by that Snapshot

    internal SocketMap() {
        mutex = new SemaphoreSlim(1);
        sockMap = new Dictionary<SocketId, Socket>();
    }

    public async Task<Socket> GetSocket(SocketId id, CancellationToken _) {
        return await GetSocket(id, true);
    }

    public async Task RemoveSocket(SocketId id) {
        await mutex.WaitAsync();
        try {
            if (sockMap.TryGetValue(id, out var s)) {
                s.Dispose();
                sockMap.Remove(id);
            } else {
                return;
            }

            ReplaceSnapshot();
        } finally {
            mutex.Release();
        }

        await Log.Write($"removed connection {id}");
    }

    public async Task Reset() {
        await mutex.WaitAsync();
        try {
            foreach (var s in sockMap.Values)
                s.Dispose();

            lastSnapLifetime = null;
        } finally {
            mutex.Release();
        }

        await Log.Write($"reset all connections");
    }

    public async Task<SocketSnapshot> Snapshot() {
        await mutex.WaitAsync();
        try {
            if (lastSnapLifetime != null)
                throw new Exception("concurrent snapshots not supported");

            lastSnapLifetime = new Lifetime();

            return new SocketSnapshot(sockMap.ToImmutableDictionary(), lastSnapLifetime);
        } finally {
            mutex.Release();
        }
    }

    public async Task Detach(SocketSnapshot snap) {
        await mutex.WaitAsync();
        try {
            if (ReferenceEquals(snap.Lifetime, lastSnapLifetime))
                lastSnapLifetime = null;
        } finally {
            mutex.Release();
        }
    }

    internal async Task<Socket> GetSocket(SocketId id, bool required) {
        await mutex.WaitAsync();
        try {
            if (sockMap.TryGetValue(id, out var s))
                return s;
            else if (required)
                throw new Exception($"no socket found with id {id}");
            else
                return null;
        } finally {
            mutex.Release();
        }
    }

    internal async Task AddSocket(SocketId id, Socket s) {
        await mutex.WaitAsync();
        try {
            if (sockMap.ContainsKey(id))
                throw new Exception($"socket {id} already mapped");

            sockMap.Add(id, s);
            ReplaceSnapshot();
        } finally {
            mutex.Release();
        }

        await Log.Write($"added connection {id}");
    }

    public void Dispose() {
        foreach (var s in sockMap.Values)
            s.Dispose();

        mutex.Dispose();
    }

    private void ReplaceSnapshot() {
        if (lastSnapLifetime != null)
            lastSnapLifetime.Terminate();

        lastSnapLifetime = null;
    }
}

class AutoconnectSocketMap : ISocketMap {
    private readonly IPEndPoint endPoint;
    private readonly SocketMap sockMap;

    internal AutoconnectSocketMap(IPEndPoint endPoint) {
        this.endPoint = endPoint;
        sockMap = new SocketMap();
    }

    public async Task<Socket> GetSocket(SocketId id, CancellationToken token) {
        var s = await sockMap.GetSocket(id, false);

        if (s == null) {
            s = await Connect(token);
            await sockMap.AddSocket(id, s);
        }

        return s;
    }

    public async Task RemoveSocket(SocketId id) {
        await sockMap.RemoveSocket(id);
    }

    public async Task Reset() {
        await sockMap.Reset();
    }

    public async Task<SocketSnapshot> Snapshot() {
        return await sockMap.Snapshot();
    }

    public async Task Detach(SocketSnapshot snap) {
        await sockMap.Detach(snap);
    }

    public void Dispose() {
        sockMap.Dispose();
    }

    private async Task<Socket> Connect(CancellationToken token) {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp);

        try {
            using var conTimeout = ConnectTimeout(token);
            await s.ConnectAsync(endPoint, conTimeout.Token);
        } catch (Exception e) {
            await Log.Warn($"could not connect to {endPoint}", e);
            throw;
        }

        await Log.Write($"connected new socket to {endPoint}");

        return s;
    }
}
