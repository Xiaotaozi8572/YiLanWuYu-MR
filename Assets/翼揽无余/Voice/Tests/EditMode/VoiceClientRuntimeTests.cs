using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Bootstrap;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Metrics;
using Yilan.Voice.Runtime.Pico;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Transport;
using UnityEngine.TestTools;

namespace Yilan.Voice.Remediation
{
    [TestFixture]
    public class VoiceClientRuntimeTests
    {
        private static readonly VoiceClientConfig Cfg = new VoiceClientConfig
        {
            WsHost = "voice.test",
            WsPort = 443,
            UseWss = true,
            WsPath = "/ws/voice/session",
            ConnectTimeoutMs = 1000,
            SendTimeoutMs = 1000
        };

        [Test]
        public void StartAsync_HealthFails_DoesNotConnect()
        {
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: false);

            Assert.AreEqual(VoiceCommandResult.TransportFailed, runtime.StartAsync().GetAwaiter().GetResult());
            Assert.AreEqual(0, socket.ConnectCalls);
            Assert.IsNull(runtime.Controller);
            Assert.IsNull(runtime.ViewModel);
        }

        [Test]
        public void StartAsync_HealthSucceeds_ConnectsAndCreatesProjection()
        {
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            Assert.AreEqual(1, socket.ConnectCalls);
            Assert.IsNotNull(runtime.Controller);
            Assert.IsNotNull(runtime.ViewModel);
        }

        [UnityTest]
        public IEnumerator BeginCaptureFromIdle_WaitsForMatchingStartedAckAndCoalescesClicks()
        {
            var socket = new FakeVoiceSocket();
            var runtime = CreateRuntimeWithDevice(socket, out var device, out var microphone);
            int callerThreadId = Thread.CurrentThread.ManagedThreadId;
            var first = runtime.BeginCaptureAsync();
            Assert.IsNotNull(runtime.Controller);
            Assert.AreNotEqual(VoiceClientState.Capturing, runtime.Controller.State,
                "capture must wait until the server ACK is delivered");
            Assert.IsFalse(first.IsCompleted, "the first intent should still be waiting for the server ACK");
            var duplicate = runtime.BeginCaptureAsync();

            Assert.AreSame(first, duplicate,
                $"overlapping record intents must share one handshake (state={runtime.Controller.State}, recording={microphone.IsRecording}, complete={first.IsCompleted})");
            Assert.IsFalse(microphone.IsRecording, "the microphone must stay closed before session.started");
            Assert.IsFalse(first.IsCompleted);

            socket.DispatchMessageQueue(); // Opened -> session.start; ACK has not arrived yet.
            Assert.IsFalse(microphone.IsRecording);
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();

            float deadline = Time.realtimeSinceStartup + 2f;
            while (!first.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(first.IsCompleted, "the matching ACK should release the shared start intent");
            Assert.AreEqual(VoiceCommandResult.Ok, first.GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);
            Assert.IsTrue(microphone.IsRecording);
            Assert.AreEqual(callerThreadId, device.StartThreadId);

            runtime.Dispose();
        }

        [UnityTest]
        public IEnumerator BeginCaptureWithSynchronousStartedAck_OpensMicOnlyAfterAck()
        {
            var socket = new ImmediateAckSocket();
            var runtime = CreateRuntimeWithDevice(socket, out var device, out var microphone);

            var start = runtime.BeginCaptureAsync();
            Assert.IsFalse(microphone.IsRecording);
            float deadline = Time.realtimeSinceStartup + 2f;
            while (!start.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsTrue(start.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.Ok, start.GetAwaiter().GetResult());
            Assert.AreEqual(1, socket.ConnectCalls);
            Assert.AreEqual(1, device.StartCalls);
            Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, device.StartThreadId);
            Assert.IsTrue(microphone.IsRecording);
            runtime.Dispose();
        }

        [Test]
        public void BeginCaptureHandshakeFault_DoesNotOpenMic()
        {
            var socket = new ImmediateAckSocket { MismatchSession = true };
            var runtime = CreateRuntimeWithDevice(socket, out var device, out var microphone);

            var start = runtime.BeginCaptureAsync();

            Assert.AreEqual(VoiceCommandResult.Faulted, start.GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Faulted, runtime.Controller.State);
            Assert.AreEqual(0, device.StartCalls);
            Assert.IsFalse(microphone.IsRecording);
            runtime.Dispose();
        }

        [UnityTest]
        public IEnumerator DestroyDuringCaptureHandshakeInvalidatesPendingIntent()
        {
            var socket = new FakeVoiceSocket();
            var runtime = CreateRuntimeWithDevice(socket, out var device, out var microphone);
            var start = runtime.BeginCaptureAsync();
            Assert.IsFalse(start.IsCompleted);
            runtime.Dispose();

            float deadline = Time.realtimeSinceStartup + 2f;
            while (!start.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(start.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.InvalidState, start.GetAwaiter().GetResult());
            Assert.AreEqual(0, device.StartCalls);
            Assert.IsFalse(microphone.IsRecording);
        }

        [UnityTest]
        public IEnumerator CancelledCaptureIntentDoesNotOpenMicAndCanBeRetried()
        {
            var socket = new FakeVoiceSocket();
            var runtime = CreateRuntimeWithDevice(socket, out var device, out var microphone);
            var cancelled = runtime.BeginCaptureAsync();
            Assert.AreSame(cancelled, runtime.BeginCaptureAsync());
            Assert.IsFalse(microphone.IsRecording);

            Assert.AreEqual(VoiceCommandResult.InvalidState, runtime.EndCaptureAsync().GetAwaiter().GetResult());
            float deadline = Time.realtimeSinceStartup + 2f;
            while (!cancelled.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(cancelled.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.InvalidState, cancelled.GetAwaiter().GetResult());
            Assert.AreEqual(0, device.StartCalls);

            var retry = runtime.BeginCaptureAsync();
            socket.DispatchMessageQueue();
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            deadline = Time.realtimeSinceStartup + 2f;
            while (!retry.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(retry.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.Ok, retry.GetAwaiter().GetResult());
            Assert.AreEqual(1, device.StartCalls, "a canceled intent must not start the microphone; retry starts it once");
            runtime.Dispose();
        }

        [UnityTest]
        public IEnumerator FaultedResetClosesOldSocketRebuildsAndWaitsForNewAckBeforeCapture()
        {
            var oldSocket = new FakeVoiceSocket();
            var runtime = CreateRuntimeWithDevice(oldSocket, out var device, out var microphone);
            runtime.StartAsync().GetAwaiter().GetResult();
            oldSocket.DispatchMessageQueue();
            oldSocket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            oldSocket.DispatchMessageQueue();
            oldSocket.SimulateError(new VoiceSocketError(VoiceSocketErrorKind.Unexpected, "test"));
            oldSocket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, runtime.Controller.State);

            var newSocket = new FakeVoiceSocket();
            typeof(VoiceClientRuntime).GetField("_socketFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(runtime, (Func<IVoiceSocket>)(() => newSocket));
            var reset = (Task<VoiceCommandResult>)typeof(VoiceClientRuntime)
                .GetMethod("ResetFaultedAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runtime, null);
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!reset.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsTrue(reset.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.Ok, reset.GetAwaiter().GetResult());
            Assert.AreEqual(1, oldSocket.CloseCalls);
            Assert.Throws<ObjectDisposedException>(() => oldSocket.ConnectAsync(Cfg.BuildWsUri()).GetAwaiter().GetResult());
            Assert.AreSame(newSocket, runtime.Socket);
            Assert.AreEqual(VoiceClientState.Connecting, runtime.Controller.State);
            Assert.IsFalse(microphone.IsRecording);
            StringAssert.Contains("连接已清理", runtime.ViewModel.HintText);

            var start = runtime.BeginCaptureAsync();
            Assert.IsFalse(microphone.IsRecording);
            newSocket.DispatchMessageQueue();
            newSocket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            newSocket.DispatchMessageQueue();
            deadline = Time.realtimeSinceStartup + 2f;
            while (!start.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(start.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.Ok, start.GetAwaiter().GetResult());
            Assert.AreEqual(1, device.StartCalls);
            runtime.Dispose();
        }

        [Test]
        public void Dispose_ReleasesDependenciesInReverseOwnershipOrder()
        {
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true, out _, out _, out _);
            var order = new List<string>();
            runtime.DisposeObserver = order.Add;

            runtime.StartAsync().GetAwaiter().GetResult();
            runtime.Dispose();

            CollectionAssert.AreEqual(
                new[] { "metrics", "viewModel", "controller", "playback", "microphone", "socket" },
                order);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void StartAsync_DisposedDuringHealthProbe_DoesNotCreateSession(bool healthy)
        {
            var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, true, out _, out _, out _, _ => probe.Task);

            var startup = runtime.StartAsync();
            Assert.IsFalse(startup.IsCompleted);
            runtime.Dispose();
            probe.SetResult(healthy);

            Assert.AreEqual(VoiceCommandResult.InvalidState, startup.GetAwaiter().GetResult());
            Assert.AreEqual(0, socket.ConnectCalls, "a disposed runtime must not open a new connection");
            Assert.IsNull(runtime.Controller);
            Assert.IsNull(runtime.ViewModel);
        }

        [Test]
        public void StartAsync_ProbeThrowsAfterDisposal_DoesNotCreateSession()
        {
            var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, true, out _, out _, out _, _ => probe.Task);

            var startup = runtime.StartAsync();
            runtime.Dispose();
            probe.SetException(new InvalidOperationException("health probe failed"));

            Assert.AreEqual(VoiceCommandResult.InvalidState, startup.GetAwaiter().GetResult());
            Assert.AreEqual(0, socket.ConnectCalls);
            Assert.IsNull(runtime.Controller);
            Assert.IsNull(runtime.ViewModel);
        }

        [Test]
        public void PumpAsync_SendsCapturedFramesUpstreamAsHeaderBinaryPairs()
        {
            // P0-1 regression: PumpAsync used to drain the microphone and DISCARD
            // every frame, so no audio ever reached the server. Each 20ms PCM16
            // frame must leave as one audio.frame header + one binary body.
            var socket = new FakeVoiceSocket();
            var device = new SteppingMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            var microphone = new UnityMicrophoneSource(device, () => permission.HasPermission);
            // playback/metrics are owned and disposed by the runtime itself.
            var playback = new RecordingPlayback();
            var metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            using var runtime = new VoiceClientRuntime(Cfg, socket, _ => Task.FromResult(true), permission, microphone, playback, metrics);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue(); // Opened -> session.start sent

            // Replay the server's session.started(v1) to reach Ready.
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.BeginCaptureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);

            // One 20ms window of mono 16 kHz audio = 320 samples = one 640-byte frame.
            device.Advance(Pcm16AudioConstants.SamplesPerFrame);
            Assert.AreEqual(VoiceCommandResult.Ok, runtime.PumpAsync(4).GetAwaiter().GetResult());

            Assert.AreEqual(2, socket.SentTexts.Count, "session.start + one audio.frame header");
            StringAssert.Contains("\"type\":\"audio.frame\"", socket.SentTexts[1]);
            Assert.AreEqual(1, socket.SentBinaries.Count, "the PCM body must actually be sent");
            Assert.AreEqual(Pcm16AudioConstants.BytesPerFrame, socket.SentBinaries[0].Length);
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);
        }

        [Test]
        public void BeginCaptureAsync_MicStartFails_StaysReadyAndRetrySucceeds()
        {
            // §12.3 fix 1 regression: a microphone that fails to open used to advance
            // the state machine to Capturing FIRST and then strand the turn there
            // with zero frames (no local exit — the UI waits forever). The capture
            // start must open the mic BEFORE RecordStartAsync; on failure the session
            // stays Ready and a retry with a working mic reaches Capturing normally.
            var socket = new FakeVoiceSocket();
            var device = new FlakyMicrophoneDevice { FailStart = true };
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            var microphone = new UnityMicrophoneSource(device, () => permission.HasPermission);
            var playback = new RecordingPlayback();
            var metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            using var runtime = new VoiceClientRuntime(Cfg, socket, _ => Task.FromResult(true), permission, microphone, playback, metrics);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue(); // Opened -> session.start sent
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);

            // Mic fails to open: the turn must NOT start — state stays Ready and no
            // turn-level traffic leaves the socket.
            Assert.AreEqual(VoiceCommandResult.InvalidState, runtime.BeginCaptureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State,
                "mic failure must not strand the turn in Capturing");
            Assert.AreEqual(1, socket.SentTexts.Count, "no turn-level traffic on a failed capture start");

            // Retry with a working mic: normal capture start.
            device.FailStart = false;
            Assert.AreEqual(VoiceCommandResult.Ok, runtime.BeginCaptureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);
        }

        [Test]
        public void PauseThenResumeAsync_ReconnectsSameSessionAndReachesReady()
        {
            // P1-5 regression: OnApplicationPause(true) tears the session down to
            // Idle; the foreground callback previously had NO path back. ResumeAsync
            // must re-open the WebSocket on the SAME session id (server-side
            // conversation context survives) and the controller must reach Ready
            // again after the server's fresh session.started.
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue(); // Opened -> session.start
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);
            string sessionId = runtime.Controller.SessionId;

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.PauseAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Idle, runtime.Controller.State);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.ResumeAsync().GetAwaiter().GetResult());
            Assert.AreEqual(2, socket.ConnectCalls, "resume must re-open the socket");
            socket.DispatchMessageQueue(); // Opened -> fresh session.start
            Assert.AreEqual(sessionId, runtime.Controller.SessionId, "same session id across pause/resume");

            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);
        }

        [Test]
        public void ResumeAsync_WithoutPause_IsRejectedAsNoOp()
        {
            // Resume is only legal from the Idle state PauseAsync settles into; from
            // Ready (never paused) it must be a rejected no-op, never a second
            // concurrent connection attempt.
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true);

            runtime.StartAsync().GetAwaiter().GetResult();
            socket.DispatchMessageQueue();
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);

            Assert.AreEqual(VoiceCommandResult.InvalidState, runtime.ResumeAsync().GetAwaiter().GetResult());
            Assert.AreEqual(1, socket.ConnectCalls, "no second connection may be opened");
        }

        private static VoiceClientRuntime CreateRuntime(
            FakeVoiceSocket socket,
            bool healthOk,
            out RecordingPlayback playback,
            out UnityMicrophoneSource microphone,
            out VoiceMetricsRecorder metrics,
            Func<VoiceClientConfig, Task<bool>> healthProbe = null)
        {
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            microphone = new UnityMicrophoneSource(new FakeMicrophoneDevice(), () => permission.HasPermission);
            playback = new RecordingPlayback();
            metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            return new VoiceClientRuntime(Cfg, socket, healthProbe ?? (_ => Task.FromResult(healthOk)), permission, microphone, playback, metrics);
        }

        private static VoiceClientRuntime CreateRuntime(FakeVoiceSocket socket, bool healthOk)
        {
            return CreateRuntime(socket, healthOk, out _, out _, out _);
        }

        private static VoiceClientRuntime CreateRuntimeWithDevice(
            IVoiceSocket socket,
            out FakeMicrophoneDevice device,
            out UnityMicrophoneSource microphone)
        {
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            device = new FakeMicrophoneDevice();
            microphone = new UnityMicrophoneSource(device, () => permission.HasPermission);
            var playback = new RecordingPlayback();
            var metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            return new VoiceClientRuntime(Cfg, socket, _ => Task.FromResult(true), permission, microphone, playback, metrics);
        }

        /// <summary>Server-style voice.state(session.started, voice-ws-v1) wire frame for the
        /// runtime's own controller (ids are echoed from the session.start it sent).</summary>
        private static string StartedJson(VoiceClientRuntime runtime)
        {
            var controller = runtime.Controller;
            var m = new Yilan.Voice.Runtime.Protocol.VoiceStateMessage
            {
                type = "voice.state",
                session_id = controller.SessionId,
                turn_id = controller.TurnId,
                state = "listening",
                status = "session.started",
                protocol_version = Yilan.Voice.Runtime.Protocol.VoiceProtocolParser.WireVersion
            };
            return UnityEngine.JsonUtility.ToJson(m);
        }

        private sealed class FakePermissionPlatform : IMicrophonePermissionPlatform
        {
            public bool Granted;
            public bool CanAskAgain;

            public bool HasPermission() => Granted;
            public bool CanRequestAgain() => CanAskAgain;
            public void RequestPermission() { }
        }

        private sealed class ImmediateAckSocket : IVoiceSocket
        {
            public event VoiceSocketOpenedHandler Opened;
            public event VoiceSocketPayloadHandler Payload;
            public event VoiceSocketErrorHandler Error;
            public event VoiceSocketClosedHandler Closed;
            public bool IsOpen { get; private set; }
            public bool MismatchSession;
            public int ConnectCalls { get; private set; }
            public System.Collections.Generic.List<string> SentTexts { get; } = new System.Collections.Generic.List<string>();

            public Task ConnectAsync(Uri url, int timeoutMs = 0)
            {
                ConnectCalls++;
                IsOpen = true;
                Opened?.Invoke();
                return Task.CompletedTask;
            }

            public Task SendTextAsync(string text, int timeoutMs = 0)
            {
                SentTexts.Add(text);
                if (text.Contains("\"type\":\"session.start\""))
                {
                    var start = JsonUtility.FromJson<Yilan.Voice.Runtime.Protocol.SessionStartMessage>(text);
                    var ack = new Yilan.Voice.Runtime.Protocol.VoiceStateMessage
                    {
                        type = "voice.state",
                        session_id = MismatchSession ? "other-session" : start.session_id,
                        turn_id = start.turn_id,
                        state = "listening",
                        status = "session.started",
                        protocol_version = Yilan.Voice.Runtime.Protocol.VoiceProtocolParser.WireVersion
                    };
                    Payload?.Invoke(Encoding.UTF8.GetBytes(JsonUtility.ToJson(ack)));
                }
                return Task.CompletedTask;
            }

            public Task SendBinaryAsync(byte[] data, int timeoutMs = 0) => Task.CompletedTask;
            public Task CloseAsync(int code, string reason, int timeoutMs = 0)
            {
                IsOpen = false;
                Closed?.Invoke(new VoiceCloseInfo(code, VoiceCloseMapper.Classify(code), reason));
                return Task.CompletedTask;
            }
            public void DispatchMessageQueue() { }
            public void Dispose() { IsOpen = false; Opened = null; Payload = null; Error = null; Closed = null; }
        }

        private sealed class FakeMicrophoneDevice : IMicrophoneDevice
        {
            public string DeviceName => "fake";
            public int Channels => 1;
            public int DeviceSampleRate => Pcm16AudioConstants.SampleRateHz;
            public bool IsRecording { get; private set; }
            public int StartCalls { get; private set; }
            public int StartThreadId { get; private set; }
            public int CapacitySamples => Pcm16AudioConstants.SampleRateHz;

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                StartCalls++;
                StartThreadId = Thread.CurrentThread.ManagedThreadId;
                IsRecording = true;
                return true;
            }

            public void Stop() => IsRecording = false;
            public int GetPosition() => 0;
            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount) { }
            public void Dispose() => Stop();
        }

        /// <summary>Fake capture device whose open can be made to fail on demand
        /// (§12.3 fix 1: mic-start failure must not strand the turn in Capturing).</summary>
        private sealed class FlakyMicrophoneDevice : IMicrophoneDevice
        {
            public bool FailStart;

            public string DeviceName => "fake-flaky";
            public int Channels => 1;
            public int DeviceSampleRate => Pcm16AudioConstants.SampleRateHz;
            public bool IsRecording { get; private set; }
            public int CapacitySamples => Pcm16AudioConstants.SampleRateHz;

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                if (FailStart) return false;
                IsRecording = true;
                return true;
            }

            public void Stop() => IsRecording = false;
            public int GetPosition() => 0;
            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount) { }
            public void Dispose() => Stop();
        }

        /// <summary>Fake capture device whose write cursor the test advances explicitly;
        /// every requested read region is filled with a constant non-silent sample so
        /// the framer actually produces PCM16 frames.</summary>
        private sealed class SteppingMicrophoneDevice : IMicrophoneDevice
        {
            private int _pos;

            public string DeviceName => "fake-stepping";
            public int Channels => 1;
            public int DeviceSampleRate => Pcm16AudioConstants.SampleRateHz;
            public bool IsRecording { get; private set; }
            public int CapacitySamples => Pcm16AudioConstants.SampleRateHz;

            public void Advance(int samples)
            {
                _pos = (_pos + samples) % CapacitySamples;
            }

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                IsRecording = true;
                _pos = 0;
                return true;
            }

            public void Stop() => IsRecording = false;
            public int GetPosition() => _pos;

            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    dest[destOffset + i] = 0.25f;
                }
            }

            public void Dispose() => Stop();
        }

        private sealed class RecordingPlayback : ITtsPlayback
        {
            public bool HasQueued => false;
            public bool IsPlaying => false;
            public event Action<TtsPlayedItem> FramePlayed;
            public event Action<string> FrameDecodeFailed;
            public event Action<string> PlaybackCompleted;

            public TtsPlaybackCode EnqueueTts(Yilan.Voice.Runtime.Protocol.TtsFrameMessage header, byte[] mp3)
                => TtsPlaybackCode.Accepted;

            public void Cancel() { }

            public void Dispose()
            {
                FramePlayed = null;
                FrameDecodeFailed = null;
                PlaybackCompleted = null;
            }
        }
    }
}
