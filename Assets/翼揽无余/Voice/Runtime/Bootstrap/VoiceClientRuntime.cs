using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Metrics;
using Yilan.Voice.Runtime.Pico;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Runtime.UI;

namespace Yilan.Voice.Runtime.Bootstrap
{
    /// <summary>
    /// Pure C# composition root over the existing transport/session/capture/UI components.
    /// Capture pumping forwards each 20ms PCM16 frame upstream (audio.frame header +
    /// binary body); TTS playback driving is owned by the bootstrap wiring.
    /// </summary>
    public sealed class VoiceClientRuntime : IDisposable
    {
        private readonly VoiceClientConfig _config;
        private IVoiceSocket _socket;
        private Func<IVoiceSocket> _socketFactory;
        private readonly Func<VoiceClientConfig, Task<bool>> _healthProbe;
        private readonly PicoMicrophonePermission _permission;
        private readonly UnityMicrophoneSource _microphone;
        private readonly ITtsPlayback _playback;
        private readonly VoiceMetricsRecorder _metrics;
        private readonly object _lifecycleGate = new object();
        private readonly SynchronizationContext _mainThreadContext;
        private readonly int _mainThreadId;

        private VoiceSessionController _controller;
        private VoiceViewModel _viewModel;
        private Task<VoiceCommandResult> _captureStartTask;
        private CancellationTokenSource _captureStartCancellation;
        private bool _disposed;

        public VoiceClientRuntime(
            VoiceClientConfig config,
            IVoiceSocket socket,
            Func<VoiceClientConfig, Task<bool>> healthProbe,
            PicoMicrophonePermission permission,
            UnityMicrophoneSource microphone,
            ITtsPlayback playback,
            VoiceMetricsRecorder metrics)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _socketFactory = socket is NativeVoiceSocket
                ? (Func<IVoiceSocket>)(() => new NativeVoiceSocket(_config.BuildWsUri()))
                : () => socket;
            _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
            _permission = permission ?? throw new ArgumentNullException(nameof(permission));
            _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
            _playback = playback ?? throw new ArgumentNullException(nameof(playback));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public VoiceSessionController Controller => _controller;
        public VoiceViewModel ViewModel => _viewModel;
        public IVoiceSocket Socket => _socket;

        /// <summary>Test-only observation hook for release order verification.</summary>
        public Action<string> DisposeObserver { get; set; }

        public async Task<VoiceCommandResult> StartAsync()
        {
            VoiceSessionController existingController;
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                existingController = _controller;
            }

            if (existingController != null)
            {
                var connectTask = await InvokeOnMainThreadAsync(() => existingController.ConnectAsync()).ConfigureAwait(false);
                return await connectTask.ConfigureAwait(false);
            }

            bool healthy;
            try
            {
                healthy = await _healthProbe(_config).ConfigureAwait(false);
            }
            catch
            {
                healthy = false;
            }

            // Health I/O uses ConfigureAwait(false); create the session/socket bindings
            // on the Unity thread before ConnectAsync can touch capture/UI owned objects.
            return await CreateControllerAfterProbeAsync(healthy).ConfigureAwait(false);
        }

        private async Task<VoiceCommandResult> CreateControllerAfterProbeAsync(bool healthy)
        {
            lock (_lifecycleGate)
                if (_disposed) return VoiceCommandResult.InvalidState;
            if (!healthy) return VoiceCommandResult.TransportFailed;
            var connectTask = await InvokeOnMainThreadAsync<Task<VoiceCommandResult>>(() =>
            {
                lock (_lifecycleGate)
                {
                    if (_disposed) return Task.FromResult(VoiceCommandResult.InvalidState);
                    if (_controller == null)
                    {
                        _controller = new VoiceSessionController(_config, _socket, _playback)
                        {
                            HealthProbe = _ => Task.FromResult(true)
                        };
                        _viewModel = new VoiceViewModel(_controller);
                    }
                    return _controller.ConnectAsync();
                }
            }).ConfigureAwait(false);
            return await connectTask.ConfigureAwait(false);
        }

        public Task<VoiceCommandResult> BeginCaptureAsync()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                if (_captureStartTask != null && !_captureStartTask.IsCompleted)
                    return _captureStartTask;
                if (_microphone.IsRecording || _controller?.State == VoiceClientState.Capturing)
                    return Task.FromResult(VoiceCommandResult.Idempotent);

                _captureStartCancellation?.Dispose();
                _captureStartCancellation = new CancellationTokenSource();
                var completion = new TaskCompletionSource<VoiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _captureStartTask = completion.Task;
                _ = CompleteCaptureStartAsync(completion, _captureStartCancellation.Token);
                return _captureStartTask;
            }
        }

        private async Task CompleteCaptureStartAsync(
            TaskCompletionSource<VoiceCommandResult> completion, CancellationToken cancellationToken)
        {
            try { completion.TrySetResult(await BeginCaptureCoreAsync(cancellationToken).ConfigureAwait(false)); }
            catch (ObjectDisposedException) { completion.TrySetResult(VoiceCommandResult.InvalidState); }
            catch (Exception exception)
            {
                Debug.LogWarning("[Yilan.Voice] 开始录音流程失败: " + exception.GetType().Name);
                completion.TrySetResult(VoiceCommandResult.TransportFailed);
            }
        }

        private async Task<VoiceCommandResult> BeginCaptureCoreAsync(CancellationToken cancellationToken)
        {
            var controller = _controller;
            if (controller == null)
            {
                bool healthy;
                try { healthy = await _healthProbe(_config).ConfigureAwait(false); }
                catch { healthy = false; }
                if (cancellationToken.IsCancellationRequested) return VoiceCommandResult.InvalidState;
                if (!healthy) return VoiceCommandResult.TransportFailed;
                controller = await InvokeOnMainThreadAsync(() =>
                {
                    lock (_lifecycleGate)
                    {
                        if (_disposed || cancellationToken.IsCancellationRequested) return null;
                        if (_controller == null)
                        {
                            _controller = new VoiceSessionController(_config, _socket, _playback)
                            {
                                HealthProbe = _ => Task.FromResult(true)
                            };
                            _viewModel = new VoiceViewModel(_controller);
                        }
                        return _controller;
                    }
                }).ConfigureAwait(false);
            }
            if (controller == null || cancellationToken.IsCancellationRequested)
                return VoiceCommandResult.InvalidState;
            if (controller.State == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;

            if (controller.State == VoiceClientState.Ready)
                return await StartMicrophoneOnMainThreadAsync(controller, cancellationToken).ConfigureAwait(false);

            // Subscribe before connecting: InlineAckSocket can deliver session.started
            // synchronously from ConnectAsync, while NativeVoiceSocket delivers it from
            // the Update pump. The post-subscribe state check covers both cases.
            var readyTask = WaitForReadyAsync(controller, cancellationToken);
            if (controller.State == VoiceClientState.Idle)
            {
                var connectionTask = await InvokeOnMainThreadAsync(() => controller.ConnectAsync()).ConfigureAwait(false);
                var connectionResult = await connectionTask.ConfigureAwait(false);
                if (connectionResult != VoiceCommandResult.Ok && controller.State != VoiceClientState.Ready)
                    return connectionResult;
            }

            var ready = await readyTask.ConfigureAwait(false);
            if (ready != VoiceCommandResult.Ok) return ready;
            return await StartMicrophoneOnMainThreadAsync(controller, cancellationToken).ConfigureAwait(false);
        }

        private async Task<VoiceCommandResult> StartMicrophoneOnMainThreadAsync(
            VoiceSessionController controller, CancellationToken cancellationToken)
        {
            return await InvokeOnMainThreadAsync(() =>
            {
                if (IsInvalidated(cancellationToken) || !ReferenceEquals(controller, _controller))
                    return VoiceCommandResult.InvalidState;
                if (_permission.EnsurePermission() != MicrophonePermissionStatus.Granted)
                    return VoiceCommandResult.InvalidState;

                // Only start the microphone after a real, current session.started ACK
                // placed the controller in Ready.
                if (controller.State != VoiceClientState.Ready)
                    return controller.State == VoiceClientState.Faulted
                        ? VoiceCommandResult.Faulted
                        : VoiceCommandResult.InvalidState;
                if (!_microphone.IsRecording &&
                    !_microphone.Start(string.Empty, Pcm16AudioConstants.SampleRateHz, 1, 1))
                {
                    Debug.LogWarning(
                        "[Yilan.Voice] 麦克风启动失败，本轮录音未开始。请检查系统麦克风隐私设置" +
                        "（Windows: 设置>隐私和安全性>麦克风>允许桌面应用；Android/PICO: RECORD_AUDIO 权限）" +
                        "与默认录音设备。");
                    return VoiceCommandResult.InvalidState;
                }

                var result = controller.RecordStartAsync();
                if (result != VoiceCommandResult.Ok && result != VoiceCommandResult.Idempotent && _microphone.IsRecording)
                    _microphone.Stop();
                return result;
            }).ConfigureAwait(false);
        }

        private async Task<VoiceCommandResult> WaitForReadyAsync(
            VoiceSessionController controller, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<VoiceCommandResult>();
            Action<VoiceClientState> stateChanged = state =>
            {
                if (state == VoiceClientState.Ready) completion.TrySetResult(VoiceCommandResult.Ok);
                else if (state == VoiceClientState.Faulted) completion.TrySetResult(VoiceCommandResult.Faulted);
                else if (state == VoiceClientState.Cancelling || state == VoiceClientState.Idle)
                    completion.TrySetResult(VoiceCommandResult.InvalidState);
            };
            controller.StateChanged += stateChanged;
            if (controller.State == VoiceClientState.Ready) completion.TrySetResult(VoiceCommandResult.Ok);
            else if (controller.State == VoiceClientState.Faulted) completion.TrySetResult(VoiceCommandResult.Faulted);
            using (cancellationToken.Register(() => completion.TrySetResult(VoiceCommandResult.InvalidState)))
            {
                var deadline = Task.Delay(Math.Max(1, _config.ConnectTimeoutMs));
                var completed = await Task.WhenAny(completion.Task, deadline).ConfigureAwait(false);
                controller.StateChanged -= stateChanged;
                if (completed == deadline)
                {
                    await PauseAfterHandshakeTimeoutAsync(controller).ConfigureAwait(false);
                    return VoiceCommandResult.TransportFailed;
                }
                return await completion.Task.ConfigureAwait(false);
            }
        }

        private async Task PauseAfterHandshakeTimeoutAsync(VoiceSessionController controller)
        {
            if (!ReferenceEquals(controller, _controller) || !VoiceStateRules.IsActive(controller.State)) return;
            try
            {
                var pauseTask = await InvokeOnMainThreadAsync(() => controller.PauseAsync()).ConfigureAwait(false);
                await pauseTask.ConfigureAwait(false);
            }
            catch { }
        }

        private bool IsInvalidated(CancellationToken cancellationToken)
        {
            lock (_lifecycleGate) return _disposed || cancellationToken.IsCancellationRequested;
        }

        private Task<T> InvokeOnMainThreadAsync<T>(Func<T> action)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                try { return Task.FromResult(action()); }
                catch (Exception exception) { return Task.FromException<T>(exception); }
            }
            if (_mainThreadContext == null)
                return Task.FromException<T>(new InvalidOperationException("Unity main-thread context is unavailable"));

            var completion = new TaskCompletionSource<T>();
            _mainThreadContext.Post(_ =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null);
            return completion.Task;
        }

        public Task<VoiceCommandResult> PumpAsync(int maxFramesPerUpdate)
        {
            ThrowIfDisposed();
            if (_controller == null) return Task.FromResult(VoiceCommandResult.InvalidState);
            if (!_microphone.IsRecording) return Task.FromResult(VoiceCommandResult.Idempotent);

            int frameBudget = maxFramesPerUpdate > 0 ? maxFramesPerUpdate : 1;
            _microphone.PollOnce();
            var result = VoiceCommandResult.Ok;
            while (frameBudget-- > 0 && _microphone.TryTakeFrame(out var frame))
            {
                // P0-1: forward each captured 20ms PCM16 frame upstream. The wire
                // protocol is one audio.frame JSON header immediately followed by
                // one binary frame; the server recomputes RMS energy from the
                // payload, so the client-side header energy stays informational (0).
                var send = _controller.SendAudioFrameAsync(
                    _controller.NextAudioSequence, frame, 0f);
                if (send != VoiceCommandResult.Ok)
                {
                    result = send;
                    break;
                }
            }

            return Task.FromResult(result);
        }

        public Task<VoiceCommandResult> EndCaptureAsync()
        {
            ThrowIfDisposed();
            InvalidatePendingCaptureStart();
            if (_controller == null) return Task.FromResult(VoiceCommandResult.InvalidState);

            if (_microphone.IsRecording)
            {
                _microphone.Stop();
            }

            return Task.FromResult(_controller.RecordEndAsync());
        }

        public Task<VoiceCommandResult> CancelAsync()
        {
            ThrowIfDisposed();
            InvalidatePendingCaptureStart();
            if (_controller == null) return Task.FromResult(VoiceCommandResult.InvalidState);

            if (_microphone.IsRecording)
            {
                _microphone.Stop();
            }

            return Task.FromResult(_controller.CancelAsync());
        }

        public async Task<VoiceCommandResult> PauseAsync()
        {
            ThrowIfDisposed();
            InvalidatePendingCaptureStart();
            if (_controller == null) return VoiceCommandResult.InvalidState;

            if (_microphone.IsRecording)
            {
                _microphone.Stop();
            }

            return await _controller.PauseAsync().ConfigureAwait(false);
        }

        public VoiceCommandResult Resume()
        {
            ThrowIfDisposed();
            if (_controller == null) return VoiceCommandResult.InvalidState;
            _permission.ResetSession();
            return _controller.Resume();
        }

        /// <summary>
        /// P1-5: full resume path after <see cref="PauseAsync"/>. Reset the permission
        /// session so the next capture re-validates RECORD_AUDIO, then re-open the
        /// WebSocket on the SAME session id — the server preserved the conversation
        /// context and its finished_turn_ids, so the controller keeps incrementing
        /// its turn counter (never reuses a finished turn id). Only legal from the
        /// quiescent Idle state PauseAsync settled into.
        /// </summary>
        public async Task<VoiceCommandResult> ResumeAsync()
        {
            ThrowIfDisposed();
            if (_controller == null) return VoiceCommandResult.InvalidState;

            var resumed = Resume();
            if (resumed != VoiceCommandResult.Ok) return resumed;

            return await _controller.ConnectAsync().ConfigureAwait(false);
        }

        internal async Task<VoiceCommandResult> ResetFaultedAsync()
        {
            VoiceSessionController oldController;
            IVoiceSocket oldSocket;
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                if (_controller == null || _controller.State != VoiceClientState.Faulted)
                    return VoiceCommandResult.InvalidState;
                InvalidatePendingCaptureStartLocked();
                oldController = _controller;
                oldSocket = _socket;
                oldController.Dispose();
                _viewModel?.Dispose();
                _controller = null;
                _viewModel = null;
            }

            if (_microphone.IsRecording) _microphone.Stop();
            _playback.Cancel();
            try { await oldSocket.CloseAsync(1000, "faulted reset", _config.ConnectTimeoutMs).ConfigureAwait(false); }
            catch { }
            oldSocket.Dispose();

            var rebuilt = await InvokeOnMainThreadAsync(() =>
            {
                lock (_lifecycleGate)
                {
                    if (_disposed) return false;
                    _socket = _socketFactory();
                    _controller = new VoiceSessionController(_config, _socket, _playback)
                    {
                        HealthProbe = _ => Task.FromResult(true)
                    };
                    _viewModel = new VoiceViewModel(_controller);
                    _viewModel.ShowHint("连接已清理，请重新提问。");
                    return true;
                }
            }).ConfigureAwait(false);
            if (!rebuilt) return VoiceCommandResult.InvalidState;
            return await StartAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                _disposed = true;
                InvalidatePendingCaptureStartLocked();

                DisposeObserver?.Invoke("metrics");
                _metrics.Dispose();

                DisposeObserver?.Invoke("viewModel");
                _viewModel?.Dispose();
                _viewModel = null;

                DisposeObserver?.Invoke("controller");
                _controller?.Dispose();
                _controller = null;

                DisposeObserver?.Invoke("playback");
                _playback.Dispose();

                DisposeObserver?.Invoke("microphone");
                _microphone.Dispose();

                DisposeObserver?.Invoke("socket");
                _socket.Dispose();
            }
        }

        private void InvalidatePendingCaptureStart()
        {
            lock (_lifecycleGate) InvalidatePendingCaptureStartLocked();
        }

        private void InvalidatePendingCaptureStartLocked()
        {
            _captureStartCancellation?.Cancel();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VoiceClientRuntime));
            }
        }
    }
}
