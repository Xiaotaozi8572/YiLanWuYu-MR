using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Yilan.Voice.Runtime.Bootstrap;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Metrics;
using Yilan.Voice.Runtime.Pico;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Runtime.UI;

namespace Yilan.Voice.Remediation
{
    public class VoiceClientBootstrapTests
    {
        [UnityTest]
        public IEnumerator Panel_ShowsStartupFailureWithoutViewModel()
        {
            var root = BuildValidBootstrapRoot();
            root.GetComponent<VoiceClientBootstrap>().enabled = false;
            var panel = root.GetComponent<VoicePanel>();
            var state = (Text)typeof(VoicePanel)
                .GetField("stateText", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);

            panel.ShowStartupStatus("语音服务连接失败", "请检查网络");
            yield return null;

            Assert.AreEqual("语音服务连接失败", state.text);
            Object.Destroy(root);
        }

        [UnityTest]
        public IEnumerator Bootstrap_MissingAudioSource_RefusesStartup()
        {
            var go = new GameObject("voice-bootstrap-test");
            go.AddComponent<VoicePanel>();
            var bootstrap = go.AddComponent<VoiceClientBootstrap>();

            Assert.IsFalse(bootstrap.ValidateReferences(out var code));
            Assert.IsFalse(bootstrap.IsValid);
            Assert.AreEqual("bootstrap_invalid", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_MissingPanel_RefusesStartup()
        {
            var go = new GameObject("voice-bootstrap-test");
            go.AddComponent<AudioSource>();
            var bootstrap = go.AddComponent<VoiceClientBootstrap>();

            Assert.IsFalse(bootstrap.ValidateReferences(out var code));
            Assert.IsFalse(bootstrap.IsValid);
            Assert.AreEqual("bootstrap_invalid", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_MissingConfig_RefusesStartup()
        {
            var go = BuildValidBootstrapRoot();
            var bootstrap = go.GetComponent<VoiceClientBootstrap>();
            SetPrivateField(bootstrap, "wsHost", string.Empty);

            Assert.IsFalse(bootstrap.ValidateReferences(out var code));
            Assert.IsFalse(bootstrap.IsValid);
            Assert.AreEqual("bootstrap_invalid", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_ValidReferences_PassesValidation()
        {
            var go = BuildValidBootstrapRoot();
            var bootstrap = go.GetComponent<VoiceClientBootstrap>();

            Assert.IsTrue(bootstrap.ValidateReferences(out var code));
            Assert.IsTrue(bootstrap.IsValid);
            Assert.AreEqual("ok", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_PlaybackFailureSubscriptions_AreRemovedOnDestroy()
        {
            var root = BuildValidBootstrapRoot();
            var bootstrap = root.GetComponent<VoiceClientBootstrap>();
            bootstrap.enabled = false;
            typeof(VoiceClientBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            var queue = (Yilan.Voice.Runtime.Playback.Mp3PlaybackQueue)typeof(VoiceClientBootstrap)
                .GetField("_playbackQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bootstrap);
            Assert.IsNotNull(queue);

            var decodeEvent = typeof(Yilan.Voice.Runtime.Playback.Mp3PlaybackQueue)
                .GetField("FrameDecodeFailed", BindingFlags.Instance | BindingFlags.NonPublic);
            var playbackEvent = typeof(Yilan.Voice.Runtime.Playback.Mp3PlaybackQueue)
                .GetField("PlaybackFailed", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.AreEqual(1, ((System.Delegate)decodeEvent.GetValue(queue)).GetInvocationList().Length);
            Assert.AreEqual(1, ((System.Delegate)playbackEvent.GetValue(queue)).GetInvocationList().Length);

            typeof(VoiceClientBootstrap).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            Assert.IsNull(decodeEvent.GetValue(queue));
            Assert.IsNull(playbackEvent.GetValue(queue));
            Object.Destroy(root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_PlayerDrainCallback_IsBridgedAndRemovedOnDestroy()
        {
            var root = BuildValidBootstrapRoot();
            var bootstrap = root.GetComponent<VoiceClientBootstrap>();
            bootstrap.enabled = false;
            typeof(VoiceClientBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            var player = (UnityTtsAudioPlayer)typeof(VoiceClientBootstrap)
                .GetField("_ttsPlayer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bootstrap);
            var callback = typeof(UnityTtsAudioPlayer)
                .GetField("PlaybackDrained", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(player);
            Assert.AreEqual(1, ((Delegate)callback.GetValue(player)).GetInvocationList().Length,
                "Bootstrap must bridge the real player drain to the controller");

            typeof(VoiceClientBootstrap).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            Assert.IsNull(callback.GetValue(player), "destroy must detach the player event");
            Object.Destroy(root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_UsesConfiguredPlaybackCompletionTimeoutForDecoder()
        {
            var config = new VoiceClientConfig();
            Assert.AreEqual(90000, config.PlaybackCompletionTimeoutMs,
                "the playback deadline is a configurable technical timeout with a bounded default");

            var root = BuildValidBootstrapRoot();
            var bootstrap = root.GetComponent<VoiceClientBootstrap>();
            bootstrap.enabled = false;
            typeof(VoiceClientBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            var queue = (Mp3PlaybackQueue)typeof(VoiceClientBootstrap)
                .GetField("_playbackQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bootstrap);
            var decoder = typeof(Mp3PlaybackQueue)
                .GetField("_decoder", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(queue);
            var timeout = typeof(UnityMp3Decoder)
                .GetField("_timeoutSeconds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(decoder);
            Assert.AreEqual(30, timeout, "decoder request timeout follows the configured playback deadline");

            typeof(VoiceClientBootstrap).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            Object.Destroy(root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_DestroyedDuringSuccessfulHealthProbe_StopsStartup()
        {
            return DestroyDuringHealthProbe(VoiceHealthStatus.Ok);
        }

        [UnityTest]
        public IEnumerator Bootstrap_DestroyedDuringFailedHealthProbe_StopsStartup()
        {
            return DestroyDuringHealthProbe(VoiceHealthStatus.Unreachable);
        }

        [UnityTest]
        public IEnumerator Bootstrap_FaultedResetClosesOldSocketRebuildsAndRebindsViewModel()
        {
            var root = BuildValidBootstrapRoot();
            var bootstrap = root.GetComponent<VoiceClientBootstrap>();
            bootstrap.enabled = false;
            var panel = root.GetComponent<VoicePanel>();
            var oldSocket = new TestSocket();
            var device = new TestMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new TestPermissionPlatform());
            var playback = new TestPlayback();
            var config = new VoiceClientConfig { WsHost = "voice.test", WsPort = 443, UseWss = true, WsPath = "/ws/voice/session" };
            var runtime = new VoiceClientRuntime(
                config,
                oldSocket,
                _ => Task.FromResult(true),
                permission,
                new UnityMicrophoneSource(device, () => permission.HasPermission),
                playback,
                new VoiceMetricsRecorder(new VoiceIdHasher("bootstrap-reset-test"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false));

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            oldSocket.DispatchMessageQueue();
            oldSocket.InjectPayload(
                "{\"type\":\"voice.error\",\"code\":\"VOICE_PROTOCOL_INVALID\",\"field\":\"session\"}");
            oldSocket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, runtime.Controller.State);

            var newSocket = new TestSocket();
            typeof(VoiceClientRuntime).GetField("_socketFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(runtime, (Func<IVoiceSocket>)(() => newSocket));
            SetPrivateField(bootstrap, "_runtime", runtime);
            panel.Bind(runtime.ViewModel);
            var oldController = runtime.Controller;

            typeof(VoiceClientBootstrap).GetMethod("OnResetRequested", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, null);
            float deadline = Time.realtimeSinceStartup + 4f;
            while (ReferenceEquals(runtime.Controller, oldController) && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.AreNotSame(oldController, runtime.Controller);
            Assert.AreEqual(1, oldSocket.CloseCalls);
            Assert.Throws<ObjectDisposedException>(() => oldSocket.ConnectAsync(config.BuildWsUri()).GetAwaiter().GetResult());
            Assert.AreSame(newSocket, runtime.Socket);
            Assert.AreEqual(VoiceClientState.Connecting, runtime.Controller.State);
            Assert.AreSame(runtime.ViewModel, typeof(VoicePanel)
                .GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel));
            StringAssert.Contains("连接已清理", runtime.ViewModel.HintText);
            Assert.AreEqual(0, device.StartCalls, "reset must not open the microphone before the new ACK");

            newSocket.DispatchMessageQueue();
            newSocket.InjectPayload(StartedJson(runtime));
            newSocket.DispatchMessageQueue();
            var capture = runtime.BeginCaptureAsync();
            deadline = Time.realtimeSinceStartup + 2f;
            while (!capture.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(capture.IsCompleted);
            Assert.AreEqual(VoiceCommandResult.Ok, capture.GetAwaiter().GetResult());
            Assert.AreEqual(1, device.StartCalls);

            Object.Destroy(root);
            yield return null;
        }

        private static IEnumerator DestroyDuringHealthProbe(VoiceHealthStatus status)
        {
            var originalTransport = VoiceHealthClient.Transport;
            var probe = new TaskCompletionSource<VoiceHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            GameObject root = null;
            try
            {
                VoiceHealthClient.Transport = (_, __, ___) => probe.Task;
                root = BuildValidBootstrapRoot();
                var bootstrap = root.GetComponent<VoiceClientBootstrap>();
                // Wiring is supplied after AddComponent's Awake in this harness.
                // Disable automatic Start so the test can observe the startup Task directly.
                bootstrap.enabled = false;
                typeof(VoiceClientBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(bootstrap, null);
                var runtime = bootstrap.Runtime;
                Assert.IsNotNull(runtime);
                var startup = (Task)typeof(VoiceClientBootstrap)
                    .GetMethod("StartRuntimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(bootstrap, null);
                Assert.IsFalse(startup.IsCompleted);

                Object.Destroy(root);
                yield return null;
                Assert.IsTrue(bootstrap == null, "destroy the owner before completing the probe");
                probe.SetResult(new VoiceHealthResult(status, status == VoiceHealthStatus.Ok ? (int?)200 : null, "test"));

                float deadline = Time.realtimeSinceStartup + 5f;
                while (!startup.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(startup.IsCompleted, "startup did not settle after the probe completed");
                startup.GetAwaiter().GetResult(); // propagates the original NullReferenceException on regression
                Assert.IsNull(runtime.Controller);
                Assert.IsNull(runtime.ViewModel);
            }
            finally
            {
                VoiceHealthClient.Transport = originalTransport;
                probe.TrySetResult(new VoiceHealthResult(VoiceHealthStatus.Unreachable, null, "cleanup"));
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        private static GameObject BuildValidBootstrapRoot()
        {
            var root = new GameObject("voice-bootstrap-test");
            root.AddComponent<AudioSource>();
            var panel = root.AddComponent<VoicePanel>();
            var bootstrap = root.AddComponent<VoiceClientBootstrap>();

            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvas.transform.SetParent(root.transform, false);
            canvas.layer = 5;

            SetPrivateField(panel, "stateText", CreateText(canvas.transform, "StateText"));
            SetPrivateField(panel, "answerText", CreateText(canvas.transform, "AnswerText"));
            SetPrivateField(panel, "clarificationText", CreateText(canvas.transform, "ClarificationText"));
            SetPrivateField(bootstrap, "panel", panel);
            SetPrivateField(bootstrap, "audioSource", root.GetComponent<AudioSource>());

            return root;
        }

        private static Text CreateText(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            go.layer = 5;
            return go.GetComponent<Text>();
        }

        private static string StartedJson(VoiceClientRuntime runtime)
        {
            var ack = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = runtime.Controller.SessionId,
                turn_id = runtime.Controller.TurnId,
                state = "listening",
                status = "session.started",
                protocol_version = VoiceProtocolParser.WireVersion
            };
            return JsonUtility.ToJson(ack);
        }

        private sealed class TestSocket : IVoiceSocket
        {
            private readonly Queue<Action> _pending = new Queue<Action>();
            private bool _disposed;
            public event VoiceSocketOpenedHandler Opened;
            public event VoiceSocketPayloadHandler Payload;
            public event VoiceSocketErrorHandler Error;
            public event VoiceSocketClosedHandler Closed;
            public bool IsOpen { get; private set; }
            public int CloseCalls { get; private set; }
            public Task ConnectAsync(Uri url, int timeoutMs = 0)
            {
                if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(TestSocket)));
                IsOpen = true;
                _pending.Enqueue(() => Opened?.Invoke());
                return Task.CompletedTask;
            }
            public Task SendTextAsync(string text, int timeoutMs = 0) => Task.CompletedTask;
            public Task SendBinaryAsync(byte[] data, int timeoutMs = 0) => Task.CompletedTask;
            public Task CloseAsync(int code, string reason, int timeoutMs = 0)
            {
                CloseCalls++;
                IsOpen = false;
                return Task.CompletedTask;
            }
            public void InjectPayload(string json) => _pending.Enqueue(() => Payload?.Invoke(Encoding.UTF8.GetBytes(json)));
            public void DispatchMessageQueue()
            {
                while (_pending.Count > 0) _pending.Dequeue()();
            }
            public void Dispose()
            {
                _disposed = true;
                IsOpen = false;
                Opened = null;
                Payload = null;
                Error = null;
                Closed = null;
                _pending.Clear();
            }
        }

        private sealed class TestPermissionPlatform : IMicrophonePermissionPlatform
        {
            public bool HasPermission() => true;
            public bool CanRequestAgain() => true;
            public void RequestPermission() { }
        }

        private sealed class TestMicrophoneDevice : IMicrophoneDevice
        {
            public string DeviceName => "test";
            public int Channels => 1;
            public int DeviceSampleRate => 16000;
            public bool IsRecording { get; private set; }
            public int CapacitySamples => 16000;
            public int StartCalls { get; private set; }
            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                StartCalls++;
                IsRecording = true;
                return true;
            }
            public void Stop() => IsRecording = false;
            public int GetPosition() => 0;
            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount) { }
            public void Dispose() => Stop();
        }

        private sealed class TestPlayback : ITtsPlayback
        {
            public bool HasQueued => false;
            public bool IsPlaying => false;
            public event Action<TtsPlayedItem> FramePlayed;
            public event Action<string> FrameDecodeFailed;
            public event Action<string> PlaybackCompleted;
            public TtsPlaybackCode EnqueueTts(TtsFrameMessage header, byte[] mp3) => TtsPlaybackCode.Accepted;
            public void Cancel() { }
            public void Dispose() { FramePlayed = null; FrameDecodeFailed = null; PlaybackCompleted = null; }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "missing private field: " + fieldName);
            field.SetValue(target, value);
        }
    }
}
