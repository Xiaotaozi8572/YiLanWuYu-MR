using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Yilan.Voice.Runtime.Transport
{
    /// <summary>
    /// Adapter over the approved NativeWebSocket baseline package
    /// (<c>com.endel.nativewebsocket</c>, assembly <c>colyseus.nativewebsocket</c>).
    ///
    /// Design rules enforced here:
    ///   * Raw binary frames are surfaced verbatim on <see cref="Payload"/> — no UTF-8 or
    ///     content sniffing. Whether a byte stream is a TTS PCM16 body is decided by the
    ///     protocol layer's pending state (<see cref="Yilan.Voice.Runtime.Protocol.TtsBinaryPairer"/>),
    ///     never by guessing here.
    ///   * ALL outgoing messages go through a strict-FIFO outbox. The package keeps separate
    ///     internal queues for text and binary sends, so once any send is in flight, later
    ///     text frames drain ahead of queued binary frames (and vice versa) — reordering
    ///     protocol header+body pairs on the wire. Serializing every send through one queue
    ///     and awaiting each library send to completion keeps the package's internal queues
    ///     out of the picture and preserves global send order.
    ///   * Events are delivered on the main thread: the package queues events and the caller
    ///     pumps them with <see cref="DispatchMessageQueue"/> (typically from Unity Update).
    ///   * Connect/send/close honour a timeout and classify failures via
    ///     <see cref="VoiceSocketErrorKind"/>. Server close codes map through
    ///     <see cref="VoiceCloseMapper"/>.
    ///   * <see cref="CloseAsync"/> is idempotent; <see cref="Dispose"/> (OnDestroy) unsubscribes
    ///     every callback so no event reaches a destroyed consumer, and fails any queued
    ///     outbox sends.
    ///   * Logging never includes payload content or the full URL query — only length/type/
    ///     category/state and a host-only endpoint.
    /// </summary>
    public sealed class NativeVoiceSocket : IVoiceSocket
    {
        public const int DefaultTimeoutMs = 8000;

        private readonly object _gate = new object();
        private _ConnectionGeneration _connection;
        private int _nextGeneration;
        private bool _disposed;

        // Strict-FIFO outgoing pump (see class doc: the library's split text/binary queues
        // reorder header+body pairs; this outbox restores global send order).
        private readonly object _outboxLock = new object();
        private readonly Queue<_OutboxItem> _outbox = new Queue<_OutboxItem>();
        private bool _pumpRunning;

        private sealed class _OutboxItem
        {
            public _ConnectionGeneration Connection;
            public bool IsText;
            public string Text;      // set when IsText
            public byte[] Binary;    // set when !IsText
            public int TimeoutMs;
            public volatile bool Succeeded;
            public volatile string FailReason;
            public readonly TaskCompletionSource<bool> Done =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>All callback and wait state belongs to exactly one native object.</summary>
        private sealed class _ConnectionGeneration
        {
            public readonly int Id;
            public readonly NativeWebSocket.WebSocket Socket;
            public readonly TaskCompletionSource<bool> Opened =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Closed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public NativeWebSocket.WebSocketOpenEventHandler OnOpen;
            public NativeWebSocket.WebSocketMessageEventHandler OnMessage;
            public NativeWebSocket.WebSocketErrorEventHandler OnError;
            public NativeWebSocket.WebSocketCloseEventHandler OnClose;
            public volatile bool Retired;
            public bool ClosedFired;
            public bool UserClosePending;

            public _ConnectionGeneration(int id, NativeWebSocket.WebSocket socket)
            {
                Id = id;
                Socket = socket;
            }
        }

        public event VoiceSocketOpenedHandler Opened;
        public event VoiceSocketPayloadHandler Payload;
        public event VoiceSocketErrorHandler Error;
        public event VoiceSocketClosedHandler Closed;

        /// <summary>Endpoint this socket was constructed against (host-only for safe logging).</summary>
        public Uri Target { get; }

        public bool IsOpen
        {
            get
            {
                lock (_gate)
                {
                    return _connection != null && !_connection.Retired &&
                           _connection.Socket.State == NativeWebSocket.WebSocketState.Open;
                }
            }
        }

        public NativeVoiceSocket(Uri url)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            string scheme = (url.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme != "ws" && scheme != "wss")
                throw new ArgumentException("Unsupported WebSocket scheme: " + scheme, nameof(url));
            Target = url;
        }

        public void DispatchMessageQueue()
        {
            _ConnectionGeneration connection;
            lock (_gate) { connection = _connection; }
            if (connection != null && !connection.Retired)
                connection.Socket.DispatchMessageQueue();
        }

        public async Task ConnectAsync(Uri url, int timeoutMs = 0)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;

            _ConnectionGeneration connection;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_connection != null && !_connection.Retired)
                    throw new InvalidOperationException("A native socket generation is already active.");
                connection = CreateAndSubscribeLocked(url);
            }

            // Fire-and-forget connect; open is signalled by the OnOpen handler through tcs.
            _ = connection.Socket.Connect();

            var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            while (!connection.Opened.Task.IsCompleted)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    RaiseError(connection, VoiceSocketErrorKind.Timeout, "connect timeout");
                    await QuietAbortAsync(connection);
                    return;
                }
                if (!connection.Retired)
                    connection.Socket.DispatchMessageQueue();
                await Task.Delay(5).ConfigureAwait(false);
            }

            if (!await connection.Opened.Task.ConfigureAwait(false))
                throw new InvalidOperationException("Native socket closed before opening.");
            if (connection.Retired)
                throw new InvalidOperationException("Native socket generation was retired.");
            LogState("connected", $"generation={connection.Id} state={connection.Socket.State}");
        }

        public async Task SendTextAsync(string text, int timeoutMs = 0)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            _ConnectionGeneration connection;
            lock (_gate) { connection = _connection; }

            if (connection == null || connection.Retired ||
                connection.Socket.State != NativeWebSocket.WebSocketState.Open)
            {
                RaiseError(connection, VoiceSocketErrorKind.InvalidState, "send text: socket not open");
                return;
            }

            bool ok = await EnqueueOutbox(connection, true, text ?? string.Empty, null, timeout).ConfigureAwait(false);
            if (ok) LogState("send-text", $"bytes={(text ?? string.Empty).Length}");
        }

        public async Task SendBinaryAsync(byte[] data, int timeoutMs = 0)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            _ConnectionGeneration connection;
            lock (_gate) { connection = _connection; }

            if (connection == null || connection.Retired ||
                connection.Socket.State != NativeWebSocket.WebSocketState.Open)
            {
                RaiseError(connection, VoiceSocketErrorKind.InvalidState, "send binary: socket not open");
                return;
            }

            bool ok = await EnqueueOutbox(connection, false, null, data ?? Array.Empty<byte>(), timeout).ConfigureAwait(false);
            if (ok) LogState("send-binary", $"bytes={(data?.Length ?? 0)}");
        }

        public async Task CloseAsync(int code, string reason, int timeoutMs = 0)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            _ConnectionGeneration connection;
            lock (_gate)
            {
                if (_disposed) return; // idempotent no-op after dispose
                connection = _connection;
                if (connection != null) connection.UserClosePending = true;
            }

            if (connection == null || connection.Retired ||
                connection.Socket.State != NativeWebSocket.WebSocketState.Open)
            {
                LogState("close", "already closed (no-op)");
                return;
            }

            try
            {
                var close = connection.Socket.Close((NativeWebSocket.WebSocketCloseCode)code, reason ?? string.Empty);
                await WaitOrTimeoutAsync(
                    connection, close, timeout, VoiceSocketErrorKind.Unexpected, "close");
                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                while (!connection.Closed.Task.IsCompleted && !connection.Retired &&
                       DateTime.UtcNow < deadline)
                {
                    connection.Socket.DispatchMessageQueue();
                    await Task.Delay(5).ConfigureAwait(false);
                }
                if (!connection.Closed.Task.IsCompleted)
                {
                    RaiseError(connection, VoiceSocketErrorKind.Timeout, "close notification timeout");
                    await QuietAbortAsync(connection);
                }
                LogState("close", $"generation={connection.Id} code={code}");
            }
            catch (Exception ex)
            {
                RaiseError(connection, VoiceSocketErrorKind.Unexpected, "close failed: " + ex.GetType().Name);
                await QuietAbortAsync(connection);
            }
            finally
            {
                lock (_gate) { connection.UserClosePending = false; }
            }
        }

        public void Dispose()
        {
            _ConnectionGeneration connection;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                connection = _connection;
                _connection = null;
                if (connection != null)
                    RetireGenerationLocked(connection, "disposed");
            }

            if (connection != null)
            {
                try { connection.Socket.CancelConnection(); } catch { }
                // NativeWebSocket.WebSocket disposes its inner ClientWebSocket internally; there is
                // no public Dispose on the adapter, so cancelling the connection is sufficient.
            }

            FailOutbox(connection, "disposed", all: true);

            // Unsubscribe every callback so nothing reaches a destroyed consumer.
            Opened = null;
            Payload = null;
            Error = null;
            Closed = null;
            LogState("dispose", "transport released");
        }

        // ---------------- internals ----------------

        private _ConnectionGeneration CreateAndSubscribeLocked(Uri url)
        {
            var s = new NativeWebSocket.WebSocket(url.ToString());
            var previous = _connection;
            if (previous != null)
            {
                RetireGenerationLocked(previous, "replaced");
                FailOutbox(previous, "connection replaced");
            }

            var connection = new _ConnectionGeneration(++_nextGeneration, s);
            _connection = connection;
            connection.OnOpen = () => HandleOpen(connection);
            connection.OnMessage = bytes => HandleMessage(connection, bytes);
            connection.OnError = msg => HandleError(connection, msg);
            connection.OnClose = code => HandleClose(connection, (int)code);
            s.OnOpen += connection.OnOpen;
            s.OnMessage += connection.OnMessage;
            s.OnError += connection.OnError;
            s.OnClose += connection.OnClose;
            return connection;
        }

        private void HandleOpen(_ConnectionGeneration connection)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_connection, connection) || connection.Retired)
                    return;
                connection.Opened.TrySetResult(true);
            }
            LogState("on-open", $"generation={connection.Id}");
            Opened?.Invoke();
        }

        private void HandleMessage(_ConnectionGeneration connection, byte[] bytes)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_connection, connection) || connection.Retired)
                    return;
            }
            // Raw bytes are forwarded only from the current native object.
            LogState("on-payload", $"generation={connection.Id} bytes={bytes?.Length ?? 0}");
            Payload?.Invoke(bytes ?? Array.Empty<byte>());
        }

        private void HandleError(_ConnectionGeneration connection, string message)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_connection, connection) || connection.Retired)
                    return;
            }
            // Native error text may contain endpoint details; do not forward it.
            LogState("on-error", $"generation={connection.Id}");
            RaiseError(connection, VoiceSocketErrorKind.Unexpected, "native socket error");
        }

        private void HandleClose(_ConnectionGeneration connection, int codeValue)
        {
            bool userClose;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_connection, connection) ||
                    connection.Retired || connection.ClosedFired)
                    return;
                connection.ClosedFired = true;
                connection.Retired = true;
                userClose = connection.UserClosePending;
                connection.Opened.TrySetResult(false);
                connection.Closed.TrySetResult(true);
            }
            Unsubscribe(connection);
            FailOutbox(connection, "connection closed");
            if (!userClose)
                LogState("on-close", $"generation={connection.Id} code={codeValue} category={VoiceCloseMapper.Classify(codeValue)}");
            Closed?.Invoke(new VoiceCloseInfo(codeValue, VoiceCloseMapper.Classify(codeValue), null));
        }

        private void RetireGenerationLocked(_ConnectionGeneration connection, string reason)
        {
            if (connection == null || connection.Retired) return;
            connection.Retired = true;
            connection.Opened.TrySetResult(false);
            connection.Closed.TrySetResult(true);
            Unsubscribe(connection);
            LogState("generation-retired", $"generation={connection.Id} reason={reason}");
        }

        private void FailOutbox(_ConnectionGeneration connection, string reason, bool all = false)
        {
            lock (_outboxLock)
            {
                FailQueuedLocked(connection, reason, all);
                if (_outbox.Count == 0 && !_pumpRunning)
                    _pumpRunning = false;
            }
        }

        private void FailQueuedLocked(_ConnectionGeneration connection, string reason, bool all = false)
        {
            if (_outbox.Count == 0) return;
            var remaining = new Queue<_OutboxItem>();
            while (_outbox.Count > 0)
            {
                var item = _outbox.Dequeue();
                if (all || ReferenceEquals(item.Connection, connection))
                {
                    item.FailReason = reason;
                    item.Done.TrySetResult(false);
                }
                else
                {
                    remaining.Enqueue(item);
                }
            }
            while (remaining.Count > 0) _outbox.Enqueue(remaining.Dequeue());
        }

        private static void Unsubscribe(_ConnectionGeneration connection)
        {
            if (connection == null) return;
            // NativeWebSocket exposes events but not a bulk unsubscribe. Subscriptions use
            // named delegates below so retirement can detach the exact generation handlers.
            connection.Socket.OnOpen -= connection.OnOpen;
            connection.Socket.OnMessage -= connection.OnMessage;
            connection.Socket.OnError -= connection.OnError;
            connection.Socket.OnClose -= connection.OnClose;
        }

        // ---------------- strict-FIFO outbox ----------------

        /// <summary>
        /// Enqueue one outgoing message and start the pump if idle. Returns a task that
        /// completes with true when the message was handed to the library and its send
        /// completed, false on failure/timeout/dispose.
        /// </summary>
        private Task<bool> EnqueueOutbox(
            _ConnectionGeneration connection, bool isText, string text, byte[] binary, int timeoutMs)
        {
            var item = new _OutboxItem
            {
                Connection = connection,
                IsText = isText,
                Text = text,
                Binary = binary,
                TimeoutMs = timeoutMs,
            };

            lock (_outboxLock)
            {
                if (_disposed || connection == null || connection.Retired)
                {
                    item.FailReason = _disposed ? "disposed" : "connection retired";
                    item.Done.TrySetResult(false);
                    return item.Done.Task;
                }
                _outbox.Enqueue(item);
                if (!_pumpRunning)
                {
                    // Reserve the slot BEFORE starting the pump: when the library
                    // send completes synchronously (loopback + small frame), the
                    // whole pump drains inline inside this very call and clears
                    // the flag itself. Storing the pump task AFTER the call (the
                    // previous design) resurrected that already-completed task as
                    // a zombie "running" flag, so every message queued behind the
                    // first one was stranded in the outbox forever.
                    _pumpRunning = true;
                    _ = PumpOutboxAsync();
                }
            }
            return item.Done.Task;
        }

        /// <summary>
        /// Single-consumer pump: takes one item at a time and fully awaits its library send
        /// before starting the next, so the library never has two sends in flight and its
        /// internal split text/binary queues never reorder our messages. Fails fast: once an
        /// item fails, every queued item is failed too (ordering can no longer be guaranteed).
        /// </summary>
        private async Task PumpOutboxAsync()
        {
            while (true)
            {
                _OutboxItem item;
                lock (_outboxLock)
                {
                    if (_outbox.Count == 0)
                    {
                        _pumpRunning = false;
                        return;
                    }
                    item = _outbox.Dequeue();
                }

                LogState("pump-send-start", item.IsText ? "text" : "binary");
                bool ok = await SendOutboxItemAsync(item).ConfigureAwait(false);
                LogState("pump-send-end", $"ok={ok}");
                if (!ok)
                {
                    lock (_outboxLock)
                    {
                        FailQueuedLocked(item.Connection, "send aborted");
                        if (_outbox.Count == 0)
                        {
                            _pumpRunning = false;
                            return;
                        }
                    }
                }
            }
        }

        private async Task<bool> SendOutboxItemAsync(_OutboxItem item)
        {
            _ConnectionGeneration connection = item.Connection;
            NativeWebSocket.WebSocket s = connection?.Socket;

            if (s == null || connection.Retired || s.State != NativeWebSocket.WebSocketState.Open)
            {
                item.FailReason = "socket not open";
                RaiseError(connection, VoiceSocketErrorKind.InvalidState, "send: socket not open");
                item.Done.TrySetResult(false);
                return false;
            }

            try
            {
                Task send = item.IsText
                    ? s.SendText(item.Text ?? string.Empty)
                    : s.Send(item.Binary ?? Array.Empty<byte>());
                await WaitForSendAsync(connection, send, item.TimeoutMs,
                    item.IsText ? "send text" : "send binary").ConfigureAwait(false);
                item.Succeeded = true;
                item.Done.TrySetResult(true);
                return true;
            }
            catch (TimeoutException)
            {
                // WaitOrTimeoutAsync already raised the Timeout error; the library send may
                // still be in flight, so ordering is no longer guaranteed — fail fast.
                item.FailReason = "timeout";
                item.Done.TrySetResult(false);
                return false;
            }
            catch (Exception ex)
            {
                item.FailReason = "send failed: " + ex.GetType().Name;
                RaiseError(connection, VoiceSocketErrorKind.Send,
                    (item.IsText ? "send text" : "send binary") + " failed: " + ex.GetType().Name);
                item.Done.TrySetResult(false);
                return false;
            }
        }

        private async Task WaitOrTimeoutAsync(
            _ConnectionGeneration connection, Task op, int timeoutMs,
            VoiceSocketErrorKind kind, string what)
        {
            if (op.IsCompleted)
            {
                await op.ConfigureAwait(false);
                return;
            }
            var delay = Task.Delay(timeoutMs);
            var done = await Task.WhenAny(op, delay).ConfigureAwait(false);
            if (done != op)
            {
                RaiseError(connection, VoiceSocketErrorKind.Timeout, what + " timeout");
                throw new TimeoutException(what + " timed out");
            }
            await op.ConfigureAwait(false);
        }

        private async Task WaitForSendAsync(
            _ConnectionGeneration connection, Task op, int timeoutMs, string what)
        {
            if (op.IsCompleted)
            {
                await op.ConfigureAwait(false);
                return;
            }
            var delay = Task.Delay(timeoutMs);
            var done = await Task.WhenAny(op, connection.Closed.Task, delay).ConfigureAwait(false);
            if (done == connection.Closed.Task)
                throw new InvalidOperationException("connection generation closed during " + what);
            if (done != op)
            {
                RaiseError(connection, VoiceSocketErrorKind.Timeout, what + " timeout");
                throw new TimeoutException(what + " timed out");
            }
            await op.ConfigureAwait(false);
        }

        private async Task QuietAbortAsync(_ConnectionGeneration connection)
        {
            if (connection == null) return;
            lock (_gate)
            {
                RetireGenerationLocked(connection, "quiet-abort");
                if (ReferenceEquals(_connection, connection)) _connection = null;
            }
            FailOutbox(connection, "connection aborted");
            try { connection.Socket.CancelConnection(); } catch { }
            await Task.CompletedTask;
        }

        private void RaiseError(_ConnectionGeneration connection, VoiceSocketErrorKind kind, string message)
        {
            if (connection != null && connection.Retired) return;
            LogState("error", $"generation={connection?.Id ?? 0} kind={kind}");
            Error?.Invoke(new VoiceSocketError(kind, message));
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NativeVoiceSocket));
        }

        // Logging must never expose payload content or the full URL query.
        private void LogState(string action, string detail)
        {
            string ep = SanitizeHost(Target);
            Debug.Log($"[NativeVoiceSocket] {action} endpoint={ep} {detail}".Trim());
        }

        internal static string SanitizeHost(Uri url)
        {
            if (url == null) return "<null>";
            try { return $"{url.Scheme}://{url.Host}:{url.Port}"; }
            catch { return "<unparseable>"; }
        }
    }
}
