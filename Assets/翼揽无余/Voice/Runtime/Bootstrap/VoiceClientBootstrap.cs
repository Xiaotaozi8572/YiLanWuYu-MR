using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Metrics;
using Yilan.Voice.Runtime.Pico;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Runtime.UI;

namespace Yilan.Voice.Runtime.Bootstrap
{
    [DisallowMultipleComponent]
    public sealed class VoiceClientBootstrap : MonoBehaviour
    {
        [SerializeField] private VoicePanel panel;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private string wsHost = "127.0.0.1";
        [SerializeField] private int wsPort = 8765;
        [SerializeField] private string wsPath = "/ws/voice/session";
        [SerializeField] private int maxFramesPerUpdate = 4;

        private VoiceClientRuntime _runtime;
        private VoiceSessionController _observedController;
        private UnityTtsAudioPlayer _ttsPlayer;
        private Mp3PlaybackQueue _playbackQueue;
        private string _pendingFailureHint;
        private bool _pumpInFlight;

        public bool IsValid { get; private set; }
        public VoiceClientRuntime Runtime => _runtime;

        private void Awake()
        {
            NormalizeCanvasForXr();
            IsValid = ValidateReferences(out _);
            if (!IsValid)
            {
                return;
            }

            var config = new VoiceClientConfig
            {
                WsHost = wsHost,
                WsPort = wsPort,
                WsPath = wsPath,
                UseWss = false
            };

            var permission = new PicoMicrophonePermission(new AndroidMicrophonePermissionPlatform());
            var microphone = new UnityMicrophoneSource(new UnityMicrophoneDevice(), () => permission.HasPermission);
            _playbackQueue = new Mp3PlaybackQueue(new UnityMp3Decoder(config.PlaybackCompletionTimeoutMs));
            var metrics = new VoiceMetricsRecorder(VoiceIdHasher.CreateRandomSalt());
            _runtime = new VoiceClientRuntime(
                config,
                new NativeVoiceSocket(config.BuildWsUri()),
                ProbeHealthAsync,
                permission,
                microphone,
                _playbackQueue,
                metrics);

            // P0-5: bridge decoded TTS clips onto the AudioSource. Mp3PlaybackQueue only
            // decodes; without this subscription the clips were silently dropped.
            _ttsPlayer = new UnityTtsAudioPlayer(audioSource);
            _ttsPlayer.PlaybackDrained += OnTtsPlaybackDrained;
            _ttsPlayer.PlaybackFailed += OnTtsPlayerFailed;
            _playbackQueue.FramePlayed += OnTtsFramePlayed;
            _playbackQueue.FrameDecodeFailed += OnPlaybackDecodeFailed;
            _playbackQueue.PlaybackFailed += OnPlaybackFailed;

            // P0-2: visible recording entry. The panel only raises intents; the actions
            // here are the single place that drives the runtime from UI input.
            panel.RecordRequested += OnRecordRequested;
            panel.StopRequested += OnStopRequested;
            panel.CancelRequested += OnCancelRequested;
            // VR10: Faulted finally has a UI exit — the reset button (visible only while the
            // ViewModel is Faulted) settles the controller back to Idle; the next record
            // click reconnects through the existing ConnectAsync path.
            panel.ResetRequested += OnResetRequested;
        }

        // DEVICE-TEST FIX (first PICO 4 session): Screen Space - Overlay canvases do
        // not render into XR eye buffers — on device the user saw only the main
        // camera's solid blue background while the panel stayed invisible (it renders
        // fine in Editor PlayMode, which is why unit/PlayMode tests never caught it).
        // Normalize the prefab's canvas to Screen Space - Camera so the panel renders
        // through the head-tracked main camera at a comfortable 2 m plane distance.
        // No-op when the canvas is missing, already not overlay, or no main camera
        // exists (stripped test harnesses keep their own canvas setup untouched).
        private void NormalizeCanvasForXr()
        {
            var canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                return;
            }
            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 2f;
        }

        private async void Start()
        {
            await StartRuntimeAsync();
        }

        private async Task StartRuntimeAsync()
        {
            if (!IsValid || _runtime == null) return;

            var runtime = _runtime;
            panel.ShowStartupStatus("正在连接语音服务…");
            VoiceCommandResult result;
            try
            {
                result = await runtime.StartAsync();
            }
            catch (Exception exception)
            {
                if (this == null || !ReferenceEquals(_runtime, runtime) || panel == null) return;
                panel.ShowStartupStatus("语音服务连接失败", "启动异常：" + exception.GetType().Name);
                Debug.LogError("[Yilan.Voice] 语音服务启动失败: " + exception);
                return;
            }

            // Scene unload / leaving Play Mode can destroy us while startup is pending.
            // Check Unity object lifetime as well as the runtime before touching the UI.
            if (this == null || !ReferenceEquals(_runtime, runtime) || panel == null) return;

            BindRuntimeUi(runtime);
            if (runtime.ViewModel == null || result != VoiceCommandResult.Ok)
            {
                string hint = "请检查眼镜网络和服务器地址（" + result + "）";
                if (runtime.ViewModel != null) runtime.ViewModel.ShowHint(hint);
                else panel.ShowStartupStatus("语音服务连接失败", hint);
            }
        }

        private void BindRuntimeUi(VoiceClientRuntime runtime)
        {
            if (runtime.ViewModel != null && !ReferenceEquals(panel.ViewModel, runtime.ViewModel))
                panel.Bind(runtime.ViewModel);

            if (ReferenceEquals(_observedController, runtime.Controller)) return;
            if (_observedController != null)
            {
                _observedController.StateChanged -= OnControllerStateChanged;
                _observedController.FaultReasonChanged -= OnControllerFaultReasonChanged;
                _observedController.PlaybackFailureReported -= OnPlaybackFailureReported;
                _observedController.TtsDegraded -= OnControllerTtsDegraded;
            }
            _observedController = runtime.Controller;
            if (_observedController == null) return;
            _observedController.StateChanged += OnControllerStateChanged;
            _observedController.FaultReasonChanged += OnControllerFaultReasonChanged;
            _observedController.PlaybackFailureReported += OnPlaybackFailureReported;
            _observedController.TtsDegraded += OnControllerTtsDegraded;
        }

        private void Update()
        {
            if (!IsValid || _runtime == null) return;

            BindRuntimeUi(_runtime);
            _runtime.Socket?.DispatchMessageQueue();
            _ttsPlayer?.Tick();
            _runtime.Controller?.TickPlaybackDeadline();
            if (!string.IsNullOrEmpty(_pendingFailureHint) && _runtime.ViewModel != null)
            {
                _runtime.ViewModel.ShowHint(_pendingFailureHint);
                _pendingFailureHint = null;
            }
            _ = PumpGuardedAsync();
        }

        private async void OnApplicationPause(bool pauseStatus)
        {
            if (_runtime == null) return;
            if (pauseStatus)
            {
                await _runtime.PauseAsync();
                return;
            }
            // P1-5: app foregrounded again — re-open the WebSocket on the SAME
            // session id. PauseAsync settled the controller to Idle; ResumeAsync
            // re-validates the permission session and reconnects from there
            // (a controller that was never paused reports InvalidState, no-op).
            await _runtime.ResumeAsync();
        }

        private void OnDestroy()
        {
            IsValid = false;
            if (_observedController != null)
            {
                _observedController.StateChanged -= OnControllerStateChanged;
                _observedController.FaultReasonChanged -= OnControllerFaultReasonChanged;
                _observedController.PlaybackFailureReported -= OnPlaybackFailureReported;
                _observedController.TtsDegraded -= OnControllerTtsDegraded;
                _observedController = null;
            }
            if (_playbackQueue != null)
            {
                _playbackQueue.FramePlayed -= OnTtsFramePlayed;
                _playbackQueue.FrameDecodeFailed -= OnPlaybackDecodeFailed;
                _playbackQueue.PlaybackFailed -= OnPlaybackFailed;
            }
            if (_ttsPlayer != null)
            {
                _ttsPlayer.PlaybackDrained -= OnTtsPlaybackDrained;
                _ttsPlayer.PlaybackFailed -= OnTtsPlayerFailed;
            }
            if (panel != null)
            {
                panel.RecordRequested -= OnRecordRequested;
                panel.StopRequested -= OnStopRequested;
                panel.CancelRequested -= OnCancelRequested;
                panel.ResetRequested -= OnResetRequested;
            }
            _runtime?.Dispose();
            _runtime = null;
            _playbackQueue = null;
            _ttsPlayer?.Dispose();
            _ttsPlayer = null;
        }

        public bool ValidateReferences(out string code)
        {
            bool valid = panel != null
                && audioSource != null
                && HasBoundText(panel, "stateText")
                && HasBoundText(panel, "answerText")
                && HasBoundText(panel, "clarificationText")
                && !string.IsNullOrWhiteSpace(wsHost)
                && wsPort > 0
                && !string.IsNullOrWhiteSpace(wsPath)
                && maxFramesPerUpdate > 0;

            IsValid = valid;
            code = valid ? "ok" : "bootstrap_invalid";
            return valid;
        }

        private void OnTtsFramePlayed(TtsPlayedItem item)
        {
            if (item == null) return;
            if (_ttsPlayer == null || _playbackQueue == null || _runtime?.Controller == null) return;
            int generation = _playbackQueue.CurrentGeneration;
            if (_ttsPlayer.Enqueue(item.Clip, item.PlaybackId, generation))
                _runtime.Controller.ReportPlaybackClipEnqueued(item.PlaybackId, item.Sequence, generation);
        }

        private void OnTtsPlaybackDrained(string playbackId, int generation)
        {
            if (_playbackQueue == null || _playbackQueue.CurrentGeneration != generation) return;
            _runtime?.Controller?.ReportAudioPlaybackDrained(playbackId, generation);
        }

        private void OnTtsPlayerFailed(string playbackId, int generation, string reason)
        {
            if (_playbackQueue == null || _playbackQueue.CurrentGeneration != generation) return;
            _runtime?.Controller?.ReportPlaybackFailure(playbackId, reason, generation);
        }

        private void OnPlaybackDecodeFailed(string playbackId)
        {
            if (_playbackQueue == null) return;
            int generation = _playbackQueue.CurrentGeneration;
            _runtime?.Controller?.ReportPlaybackFailure(playbackId, "mp3_decode", generation);
        }

        private void OnPlaybackFailed(string playbackId, string reason)
        {
            if (_playbackQueue == null) return;
            int generation = _playbackQueue.CurrentGeneration;
            _runtime?.Controller?.ReportPlaybackFailure(playbackId, reason, generation);
        }

        private void OnPlaybackFailureReported(string reason, string playbackId)
        {
            _ttsPlayer?.Cancel(_playbackQueue != null ? _playbackQueue.CurrentGeneration : -1);
            _pendingFailureHint = "语音播放失败（" + reason + "）";
        }

        private void OnControllerTtsDegraded()
        {
            _ttsPlayer?.Cancel(_playbackQueue != null ? _playbackQueue.CurrentGeneration : -1);
        }

        private void OnControllerFaultReasonChanged(string reason)
        {
            _pendingFailureHint = "语音连接失败（" + reason + "）";
        }

        private void OnRecordRequested()
        {
            _ = _runtime?.BeginCaptureAsync();
        }

        private void OnStopRequested()
        {
            _ = _runtime?.EndCaptureAsync();
        }

        private void OnCancelRequested()
        {
            _ = _runtime?.CancelAsync();
        }

        private async void OnResetRequested()
        {
            var runtime = _runtime;
            if (runtime?.Controller == null || runtime.Controller.State != VoiceClientState.Faulted) return;

            // A Faulted controller cannot recover by Reset() alone: close and detach its
            // socket first, then rebuild through the runtime's existing Native factory.
            var result = await runtime.ResetFaultedAsync();
            if (this == null || !ReferenceEquals(_runtime, runtime) || panel == null) return;
            if (result == VoiceCommandResult.Ok)
                panel.Bind(runtime.ViewModel);
        }

        private void OnControllerStateChanged(VoiceClientState state)
        {
            if (state == VoiceClientState.Cancelling
                || state == VoiceClientState.Reconnecting
                || state == VoiceClientState.Faulted)
            {
                _ttsPlayer?.Cancel(_playbackQueue != null ? _playbackQueue.CurrentGeneration : -1);
            }
        }

        private async Task PumpGuardedAsync()
        {
            if (_pumpInFlight || _runtime == null) return;

            _pumpInFlight = true;
            try
            {
                await _runtime.PumpAsync(maxFramesPerUpdate);
            }
            finally
            {
                _pumpInFlight = false;
            }
        }

        private async Task<bool> ProbeHealthAsync(VoiceClientConfig config)
        {
            var ws = config.BuildWsUri();
            var baseUri = new UriBuilder(config.UseWss ? "https" : "http", ws.Host, ws.Port).Uri;
            var result = await VoiceHealthClient.CheckAsync(
                VoiceHealthClient.HealthUrlFromBase(baseUri),
                config.HealthProbeTimeoutMs).ConfigureAwait(false);
            return result.Status == VoiceHealthStatus.Ok;
        }

        private static bool HasBoundText(VoicePanel voicePanel, string fieldName)
        {
            var field = typeof(VoicePanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.GetValue(voicePanel) is Text text && text != null;
        }
    }
}
