// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.UI.Dispatching;
using NopeView.App.Native;

namespace NopeView.App;

/// <summary>
/// The one viewer process that owns this session. The first launch claims the
/// instance name and serves it; later launches send their arguments over a
/// named pipe and leave, so a second copy never opens a second window. The
/// object is created before the framework starts, because a handover can
/// arrive while the first window is still being built.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private const string InstanceName = "NopeView.App.SingleInstance";

    // How long a later launch waits for the running viewer to answer. The
    // window is up in a fraction of a second, so a longer wait would only
    // delay the fallback that should not be needed.
    private const int ConnectTimeoutMs = 3000;
    private const int ConnectWaitMs = 50;

    private readonly string _name;
    private readonly Mutex? _owner;

    // Requests that arrived before the window existed. The pipe thread keeps
    // accepting them, and the window drains the queue once it can act.
    private readonly ConcurrentQueue<string?> _pending = new();

    private volatile bool _disposed;
    private Action<string?>? _handler;
    private NamedPipeServerStream? _server;

    private SingleInstanceService(string name, Mutex? owner)
    {
        _name = name;
        _owner = owner;
    }

    /// <summary>
    /// Whether this process is the one others hand over to. False means a
    /// viewer is already running and this launch should forward and exit.
    /// </summary>
    internal bool IsPrimary => _owner is not null;

    /// <summary>
    /// Claims the instance name for this process. A launch that finds the name
    /// taken still gets an object, but a non-primary one never serves a pipe.
    /// </summary>
    internal static SingleInstanceService Claim()
    {
        // The mutex is session local, so another user signed in through fast
        // switching runs their own copy. The pipe lives in the machine-wide
        // pipe namespace, so its name carries the session id to match.
        var name = $"{InstanceName}.{Process.GetCurrentProcess().SessionId}";
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var createdNew);
        if (!createdNew)
        {
            // This launch lost the race to a process that already owns the
            // name; it keeps no handle and only needs the identity to connect.
            mutex.Dispose();
            return new SingleInstanceService(name, owner: null);
        }

        var service = new SingleInstanceService(name, mutex);
        service.StartServer();
        return service;
    }

    /// <summary>
    /// Hands one launch's request to the running viewer and reports whether it
    /// was accepted. An empty path only asks the viewer to come forward.
    /// </summary>
    internal bool Forward(string? path)
    {
        // The existing window is about to be activated, and this launch, which
        // Windows just activated, is the process that may hand that right on.
        Win32Window.AllowForeground();

        var deadline = Environment.TickCount64 + ConnectTimeoutMs;
        while (!_disposed && Environment.TickCount64 < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", _name, PipeDirection.Out, PipeOptions.None,
                    TokenImpersonationLevel.Impersonation);
                client.Connect(ConnectWaitMs);

                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.WriteLine(path ?? string.Empty);
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                // The viewer is still finishing the previous handover. The
                // next attempt starts a fresh pipe.
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Single instance: connect failed: {ex.Message}");
                Thread.Sleep(ConnectWaitMs);
            }
        }

        return false;
    }

    /// <summary>
    /// Starts delivering requests to the window. Requests that arrived while
    /// the window was being built are queued first, and every delivery runs on
    /// the window's own thread because the pipe thread cannot touch it.
    /// </summary>
    internal void BeginListening(DispatcherQueue dispatcher, Action<string?> accept)
    {
        var handler = new Action<string?>(path => dispatcher.TryEnqueue(() => accept(path)));
        Volatile.Write(ref _handler, handler);

        while (_pending.TryDequeue(out var path))
        {
            handler(path);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the stream is what releases a thread blocked in accept. The
        // mutex is only disposed, never released: ownership ends with the
        // process, and a release from another thread would throw.
        _server?.Dispose();
        _owner?.Dispose();
    }

    private void StartServer()
    {
        var thread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "NopeView single instance"
        };
        thread.Start();
    }

    private void ServerLoop()
    {
        while (!_disposed)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
            }
            catch (Exception ex)
            {
                // Without a pipe a later launch falls back to opening its own
                // window. The viewer itself keeps running, so this is reported
                // but not fatal.
                Debug.WriteLine($"Single instance: server failed: {ex.Message}");
                return;
            }

            _server = server;
            try
            {
                server.WaitForConnection();
                ReadRequests(server);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A client that leaves mid-message, or the pipe closed at
                // shutdown, is the normal end of one round.
            }
            finally
            {
                _server = null;
                server.Dispose();
            }
        }
    }

    private void ReadRequests(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(
            server, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);

        while (!_disposed)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                return;
            }

            // One request per line. A path cannot contain a line break, so the
            // empty line is free to mean "come forward without a file".
            var path = line.Length == 0 ? null : line;
            var handler = Volatile.Read(ref _handler);
            if (handler is null)
            {
                _pending.Enqueue(path);
            }
            else
            {
                handler(path);
            }
        }
    }
}
