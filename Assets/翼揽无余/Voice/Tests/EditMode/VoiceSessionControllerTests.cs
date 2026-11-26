using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Runtime.UI;
using Yilan.Voice.Transport;

namespace Yilan.Voice.Session
{
    /// <summary>
    /// EditMode tests for the voice session state machine (VR2-T07). The controller is driven
    /// against <see cref="FakeVoiceSocket"/> (a deterministic, main-thread-pumped transport
    /// double). Gold samples for the wire frames are replayed as text payloads.
    ///
    /// Coverage: legal full chain Idle->...->Playing->Ready; illegal transition rejection;
    /// duplicate start/end/cancel idempotency; stale-round / other-session / late-message
    /// rejection; non-v1 started -> Faulted; audio/tts sequence out-of-order rejection.
    ///
    /// P0-3 (phase 4): playback_id is SERVER-issued (voice-ws-v1 tts.frame) and learned by the
    /// controller from the first tts.frame of a round; tests therefore pass explicit server-format
    /// playback ids ("playback_server1") instead of the retired local "playback-N" fabrication.
    /// </summary>
    [TestFixture]
    public class VoiceSessionControllerTests
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

        // ---- Payload builders (project typed DTOs to wire JSON via JsonUtility) ----

        private static string StartedJson(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "listening",
                status = "session.started",
                protocol_version = VoiceProtocolParser.WireVersion
            };
            return JsonUtility.ToJson(m);
        }

        private static string StartedJsonWithCapabilities(
            string sessionId, string turnId, params string[] capabilities)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "listening",
                status = "session.started",
                protocol_version = VoiceProtocolParser.WireVersion,
                accepted_capabilities = capabilities
            };
            return JsonUtility.ToJson(m);
        }

        private static string PlaybackFailedJson(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "clarify",
                status = "playback.failed"
            };
            return JsonUtility.ToJson(m);
        }

        private static string ClarificationStateJson(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "clarify",
                status = "clarification"
            };
            return JsonUtility.ToJson(m);
        }

        private static string AnswerEventJson(
            string sessionId, string turnId, string eventType, string requestId, int sequence)
        {
            return "{\"type\":\"answer.event\",\"schema_version\":1," +
                   "\"event_type\":\"" + eventType + "\",\"request_id\":\"" + requestId + "\"," +
                   "\"session_id\":\"" + sessionId + "\",\"turn_id\":\"" + turnId +
                   "\",\"sequence\":" + sequence + ",\"payload\":{\"code\":\"GENERATION_FAILED\"}}";
        }

        private static string StartedJsonNonV1(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "listening",
                status = "session.started",
                protocol_version = "voice-ws-v0"
            };
            return JsonUtility.ToJson(m);
        }

        private static string AnswerJson(string sessionId, string turnId)
        {
            var m = new AnswerDisplayMessage
            {
                type = "answer.display",
                session_id = sessionId,
                turn_id = turnId,
                answer_id = "ans-1",
                evidence_package_id = "ev-1",
                answer = new PublicAnswer { content = "你好", source = "rag" }
            };
            return JsonUtility.ToJson(m);
        }

        private static string TtsJson(string sessionId, string turnId, string playbackId, int seq, bool isFinal)
        {
            var m = new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = sessionId,
                turn_id = turnId,
                playback_id = playbackId,
                sequence = seq,
                codec = "pcm16",
                duration_ms = 100,
                is_final = isFinal
            };
            return JsonUtility.ToJson(m);
        }

        private static void SimulateTtsPair(
            FakeVoiceSocket socket, string sessionId, string turnId, string playbackId, int sequence, bool isFinal)
        {
            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(sessionId, turnId, playbackId, sequence, isFinal)));
            socket.DispatchMessageQueue();
            socket.SimulatePayload(new byte[640]);
            socket.DispatchMessageQueue();
        }

        private static string PlaybackCompletedJson(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "speaking",
                status = "playback.completed"
            };
            return JsonUtility.ToJson(m);
        }

        private static string AsrJson(string sessionId, string turnId, string type, string transcript)
        {
            var m = new AsrTranscriptMessage
            {
                type = type,
                session_id = sessionId,
                turn_id = turnId,
                transcript = transcript,
                confidence = 0.82f,
                provider = "whisper"
            };
            return JsonUtility.ToJson(m);
        }

        private static string BargeInJson(string sessionId, string turnId, string status, string intent)
        {
            var m = new BargeInAcceptedMessage
            {
                type = "barge_in.accepted",
                session_id = sessionId,
                turn_id = turnId,
                status = status,
                intent = intent
            };
            return JsonUtility.ToJson(m);
        }

        private static string ErrorJson(string sessionId, string turnId, string code)
        {
            // VoiceErrorMessage declares only code/field beyond the envelope, so the
            // wire JSON is built by hand to mirror the server payload (ids included).
            return $"{{\"type\":\"voice.error\",\"session_id\":\"{sessionId}\"," +
                   $"\"turn_id\":\"{turnId}\",\"code\":\"{code}\",\"field\":\"tts\"}}";
        }

        // ---- Harness helpers ----

        private static (VoiceSessionController controller, FakeVoiceSocket socket) NewController()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var controller = new VoiceSessionController(Cfg, socket);
            return (controller, socket);
        }

        private sealed class RecordingPlayback : ITtsPlayback
        {
            public bool HasQueued { get; private set; }
            public bool IsPlaying { get; private set; }
            public int EnqueueCalls { get; private set; }
            public TtsFrameMessage LastHeader { get; private set; }
            public byte[] LastBody { get; private set; }
            public Action OnEnqueue;
            public event Action<TtsPlayedItem> FramePlayed;
            public event Action<string> FrameDecodeFailed;
            public event Action<string> PlaybackCompleted;

            public TtsPlaybackCode EnqueueTts(TtsFrameMessage header, byte[] mp3)
            {
                OnEnqueue?.Invoke();
                EnqueueCalls++;
                LastHeader = header;
                LastBody = mp3;
                HasQueued = true;
                return TtsPlaybackCode.Accepted;
            }

            public void Cancel()
            {
                HasQueued = false;
                IsPlaying = false;
            }

            public void CompleteDecode(string playbackId)
            {
                PlaybackCompleted?.Invoke(playbackId);
            }

            public void Dispose()
            {
                FramePlayed = null;
                FrameDecodeFailed = null;
                PlaybackCompleted = null;
            }
        }

        private static bool ReadControllerFlag(VoiceSessionController controller, string name)
        {
            var field = typeof(VoiceSessionController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "expected controller completion fact " + name);
            return (bool)field.GetValue(controller);
        }

        private static int ReadControllerSequence(VoiceSessionController controller, string name)
        {
            var field = typeof(VoiceSessionController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "expected controller sequence fact " + name);
            return (int)field.GetValue(controller);
        }

        private sealed class InlineAckSocket : IVoiceSocket
        {
            public event VoiceSocketOpenedHandler Opened;
            public event VoiceSocketPayloadHandler Payload;
            public event VoiceSocketErrorHandler Error;
            public event VoiceSocketClosedHandler Closed;
            public bool IsOpen { get; private set; }
            public bool RaiseCloseOnClose = true;
            public Task CloseResult = Task.CompletedTask;
            public List<string> SentTexts { get; } = new List<string>();

            public Task ConnectAsync(Uri url, int timeoutMs = 0)
            {
                IsOpen = true;
                Opened?.Invoke();
                return Task.CompletedTask;
            }

            public Task SendTextAsync(string text, int timeoutMs = 0)
            {
                SentTexts.Add(text);
                if (text.Contains("\"type\":\"session.start\""))
                {
                    var start = JsonUtility.FromJson<SessionStartMessage>(text);
                    Payload?.Invoke(Encoding.UTF8.GetBytes(StartedJsonWithCapabilities(
                        start.session_id, start.turn_id, "answer-stream-v1")));
                }
                return Task.CompletedTask;
            }

            public Task SendBinaryAsync(byte[] data, int timeoutMs = 0) => Task.CompletedTask;
            public Task CloseAsync(int code, string reason, int timeoutMs = 0)
            {
                IsOpen = false;
                if (RaiseCloseOnClose)
                    Closed?.Invoke(new VoiceCloseInfo(code, VoiceCloseMapper.Classify(code), reason));
                return CloseResult;
            }
            public void DispatchMessageQueue() { }
            public void Dispose() { IsOpen = false; }
            public void SendPayload(string json) => Payload?.Invoke(Encoding.UTF8.GetBytes(json));
            public void SendClose(int code, string reason) =>
                Closed?.Invoke(new VoiceCloseInfo(code, VoiceCloseMapper.Classify(code), reason));
        }

        /// <summary>Connect and pump so the socket opens, session.start is sent, then replay
        /// the server session.started(v1) to reach Ready. Returns the controller.</summary>
        private static VoiceSessionController ToReady(out FakeVoiceSocket socket)
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.AreEqual(VoiceClientState.Connecting, c.State);
            s.DispatchMessageQueue(); // delivers Opened -> session.start sent
            Assert.AreEqual(1, s.SentTexts.Count); // session.start dispatched
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            socket = s;
            return c;
        }

        // =============================== Legal full chain ===============================

        [Test]
        public void FullChain_Legal_FinalHeaderAndPayloadRemainSeparateFromLocalPlayback()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            // Monotonic audio sequence 0,1,2 all accepted in order.
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.35f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(1, 0.12f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(2, 0.05f));

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // TTS monotonic 0,1(final), each header immediately paired with its body.
            SimulateTtsPair(s, c.SessionId, c.TurnId, "playback_server1", 0, false);
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_server1", 1, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State,
                "the final header records sequence but does not finish transport payload");
            Assert.IsFalse(ReadControllerFlag(c, "_finalPayloadReceived"));
            s.SimulatePayload(new byte[640]);
            s.DispatchMessageQueue();
            Assert.IsTrue(ReadControllerFlag(c, "_finalPayloadReceived"));
            Assert.AreEqual(VoiceClientState.Playing, c.State,
                "a paired final body still does not imply decoder or player completion");

            s.Dispose();
        }

        [Test]
        public void FinalTtsHeader_DoesNotFinalizeBeforeItsBinaryPayloadIsPaired()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var playback = new RecordingPlayback();
            var c = new VoiceSessionController(Cfg, socket, playback);
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            c.RecordStartAsync();
            c.RecordEndAsync();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();

            const string playbackId = "playback-final-body";
            bool finalPayloadWasSetBeforeEnqueue = false;
            playback.OnEnqueue = () =>
                finalPayloadWasSetBeforeEnqueue = ReadControllerFlag(c, "_finalPayloadReceived");
            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, playbackId, 0, true)));
            socket.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Playing, c.State,
                "the final header records sequence but cannot close the turn");
            Assert.AreEqual(playbackId, c.PlaybackId, "the active playback binding must survive the final header");
            Assert.AreEqual(0, playback.EnqueueCalls, "no frame may be enqueued before its body arrives");
            Assert.AreEqual(0, ReadControllerSequence(c, "_finalTtsSequence"));
            Assert.IsFalse(ReadControllerFlag(c, "_finalPayloadReceived"));

            var body = new byte[640];
            socket.SimulatePayload(body);
            socket.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Playing, c.State,
                "pairing the last body records transport completion; local playback owns turn completion");
            Assert.AreEqual(1, playback.EnqueueCalls);
            Assert.AreEqual(playbackId, playback.LastHeader.playback_id);
            Assert.IsTrue(playback.LastHeader.is_final);
            CollectionAssert.AreEqual(body, playback.LastBody);
            Assert.IsTrue(finalPayloadWasSetBeforeEnqueue,
                "the payload fact must be set before EnqueueTts can raise a synchronous callback");
            Assert.IsTrue(ReadControllerFlag(c, "_finalPayloadReceived"),
                "set the payload fact before EnqueueTts can raise a synchronous decode callback");

            c.Dispose();
            socket.Dispose();
        }

        [Test]
        public void PlaybackCompleted_IsRecordedWithoutCompletingLocalPlayback()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var playback = new RecordingPlayback();
            var c = new VoiceSessionController(Cfg, socket, playback);
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            c.RecordStartAsync();
            c.RecordEndAsync();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            const string playbackId = "playback-backend-complete";
            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, playbackId, 0, true)));
            socket.DispatchMessageQueue();
            socket.SimulatePayload(new byte[640]);
            socket.DispatchMessageQueue();

            socket.SimulatePayload(Encoding.UTF8.GetBytes(PlaybackCompletedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();

            Assert.IsTrue(ReadControllerFlag(c, "_backendSendCompleted"),
                "the turn-bound backend completion is retained for the later local drain gate");
            Assert.IsTrue(ReadControllerFlag(c, "_finalPayloadReceived"));
            Assert.AreEqual(VoiceClientState.Playing, c.State,
                "backend send completion cannot stand in for decode or actual audio playback");
            Assert.AreEqual(playbackId, c.PlaybackId);
            Assert.IsTrue(playback.HasQueued);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordStartAsync());

            c.Dispose();
            socket.Dispose();
        }

        [Test]
        public void FinalTurn_CompletesOnlyAfterDecodeFinalClipAndMatchingAudioSourceDrain()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var playback = new RecordingPlayback();
            var c = new VoiceSessionController(Cfg, socket, playback);
            int readyCount = 0;
            c.StateChanged += state => { if (state == VoiceClientState.Ready) readyCount++; };
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            readyCount = 0;
            c.RecordStartAsync();
            c.RecordEndAsync();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            const string playbackId = "playback-five-facts";
            socket.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, playbackId, 0, true)));
            socket.DispatchMessageQueue();
            socket.SimulatePayload(new byte[640]);
            socket.DispatchMessageQueue();

            socket.SimulatePayload(Encoding.UTF8.GetBytes(PlaybackCompletedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            playback.CompleteDecode(playbackId);
            Assert.IsTrue(ReadControllerFlag(c, "_decoderDrainCompleted"));
            Assert.AreEqual(VoiceClientState.Playing, c.State, "decode drain is not the AudioSource drain");

            c.ReportAudioPlaybackDrained(playbackId, 4);
            Assert.AreEqual(VoiceClientState.Playing, c.State, "drain without a valid final clip is ignored");
            c.ReportPlaybackClipEnqueued(playbackId, 0, 5);
            Assert.IsTrue(ReadControllerFlag(c, "_finalClipEnqueued"));
            Assert.AreEqual(VoiceClientState.Playing, c.State, "a stale generation drain cannot complete the turn");

            c.ReportAudioPlaybackDrained(playbackId, 5);
            Assert.AreEqual(VoiceClientState.Ready, c.State,
                "answer, final payload, decode drain, final clip and matching player drain are all present");
            Assert.AreEqual(1, readyCount, "normal completion is emitted once");
            c.ReportAudioPlaybackDrained(playbackId, 5);
            Assert.AreEqual(1, readyCount, "duplicate drain cannot emit a second completion");

            c.Dispose();
            socket.Dispose();
        }

        [Test]
        public void LongValidPlayback_DoesNotTimeoutAtThirtySeconds_ThenReturnsReadyForNextTurn()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var playback = new RecordingPlayback();
            var c = new VoiceSessionController(new VoiceClientConfig(), socket, playback);
            DateTime now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
            typeof(VoiceSessionController).GetField("UtcNow", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(c, new Func<DateTime>(() => now));
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            string firstTurn = c.TurnId;
            const string playbackId = "playback-long-answer";
            socket.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, firstTurn, playbackId, 0, true)));
            socket.DispatchMessageQueue();
            socket.SimulatePayload(new byte[640]);
            socket.DispatchMessageQueue();
            playback.CompleteDecode(playbackId);
            c.ReportPlaybackClipEnqueued(playbackId, 0, 1);

            now = now.AddSeconds(31);
            c.TickPlaybackDeadline();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            c.ReportAudioPlaybackDrained(playbackId, 1);
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            Assert.AreNotEqual(firstTurn, c.TurnId);
            c.Dispose();
            socket.Dispose();
        }

        [Test]
        public void DecodeNeverCompletes_TimesOutToTextDegradationAndStaleFailureCannotCloseNextTurn()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var playback = new RecordingPlayback();
            var config = new VoiceClientConfig();
            var timeoutField = typeof(VoiceClientConfig).GetField("PlaybackCompletionTimeoutMs");
            Assert.IsNotNull(timeoutField, "playback waits need an explicit technical deadline setting");
            timeoutField.SetValue(config, 25);
            var c = new VoiceSessionController(config, socket, playback);

            DateTime now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
            var clock = typeof(VoiceSessionController).GetField("UtcNow", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(clock, "controller deadline clock must be deterministic in tests");
            clock.SetValue(c, new Func<DateTime>(() => now));
            var tick = typeof(VoiceSessionController).GetMethod("TickPlaybackDeadline", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(tick, "the runtime must poll the bounded audio completion deadline");

            var received = new List<string>();
            int degraded = 0;
            c.AnswerReceived += text => received.Add(text);
            c.TtsDegraded += () => degraded++;
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            c.RecordStartAsync();
            c.RecordEndAsync();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            string expiredTurn = c.TurnId;
            const string expiredPlayback = "playback-decode-timeout";
            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, expiredPlayback, 0, true)));
            socket.DispatchMessageQueue();
            socket.SimulatePayload(new byte[640]);
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            now = now.AddMilliseconds(26);
            tick.Invoke(c, null);

            Assert.AreEqual(VoiceClientState.Ready, c.State, "missing final header/body must not strand the turn forever");
            CollectionAssert.AreEqual(new[] { "你好" }, received, "text answer stays available after audio timeout");
            Assert.AreEqual(1, degraded, "timeout degrades the turn once");
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync(), "a new turn is allowed after timeout degradation");
            var reportFailure = typeof(VoiceSessionController).GetMethod("ReportPlaybackFailure",
                new[] { typeof(string), typeof(string), typeof(int) });
            Assert.IsNotNull(reportFailure, "playback failure callbacks must carry their queue generation");
            reportFailure.Invoke(c, new object[] { "stale-playback", "late_decode", 0 });
            Assert.AreEqual(VoiceClientState.Capturing, c.State,
                "a late failure from the timed-out turn cannot close its successor");

            c.Dispose();
            socket.Dispose();
        }

        [Test]
        public void FinalHeaderWithoutBody_TimesOutWithoutReportingNormalPlaybackCompletion()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var playback = new RecordingPlayback();
            var config = new VoiceClientConfig { PlaybackCompletionTimeoutMs = 25 };
            var c = new VoiceSessionController(config, socket, playback);
            DateTime now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
            var clock = typeof(VoiceSessionController).GetField("UtcNow", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(clock);
            clock.SetValue(c, new Func<DateTime>(() => now));
            int degraded = 0;
            c.TtsDegraded += () => degraded++;
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            c.RecordStartAsync();
            c.RecordEndAsync();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback-missing-body", 0, true)));
            socket.DispatchMessageQueue();

            now = now.AddMilliseconds(26);
            c.TickPlaybackDeadline();

            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(0, playback.EnqueueCalls, "a missing final body is never enqueued or counted as played");
            Assert.AreEqual(1, degraded);
            c.Dispose();
            socket.Dispose();
        }

        [Test]
        public void SessionStartedAck_InlineDuringConnect_IsAcceptedBeforeConnectReturns()
        {
            var socket = new InlineAckSocket();
            var controller = new VoiceSessionController(Cfg, socket);

            Assert.DoesNotThrow(() => controller.ConnectAsync().GetAwaiter().GetResult());

            Assert.AreEqual(VoiceClientState.Ready, controller.State);
            Assert.IsTrue(controller.AnswerStreamAccepted);
            Assert.AreEqual(1, socket.SentTexts.Count);
        }

        [Test]
        public void ViewModel_KeepsFinalAnswerThroughIdBoundTtsDegradation_AndOnlyProjectsHints()
        {
            var (controller, socket) = NewController();
            var viewModel = new VoiceViewModel(controller);
            Assert.DoesNotThrow(() => controller.ConnectAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue();
            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                StartedJsonWithCapabilities(controller.SessionId, controller.TurnId, "answer-stream-v1")));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.Ok, controller.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, controller.RecordEndAsync());

            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                "{\"type\":\"answer.event\",\"schema_version\":1," +
                "\"event_type\":\"answer.block\",\"request_id\":\"run-1\"," +
                "\"session_id\":\"" + controller.SessionId + "\",\"turn_id\":\"" + controller.TurnId +
                "\",\"sequence\":1,\"payload\":{\"attempt\":1,\"block_id\":1,\"text\":\"候选内容\"}}"));
            socket.DispatchMessageQueue();
            Assert.AreEqual("候选内容", viewModel.AnswerText);

            socket.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(controller.SessionId, controller.TurnId)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceUiState.AnswerDisplayed, viewModel.State);
            Assert.AreEqual("你好", viewModel.AnswerText,
                "正式答案替换候选内容，而不是和候选文本拼接");

            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                "{\"type\":\"voice.error\",\"code\":\"VOICE_TTS_ERROR\",\"field\":\"tts\"}"));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, controller.State,
                "an idless error alone does not choose a turn");
            Assert.AreEqual(VoiceUiState.AnswerDisplayed, viewModel.State);

            socket.SimulatePayload(Encoding.UTF8.GetBytes(
                PlaybackFailedJson(controller.SessionId, controller.TurnId)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, controller.State);
            Assert.AreEqual(VoiceUiState.Ready, viewModel.State);
            Assert.AreEqual("你好", viewModel.AnswerText);
            Assert.AreEqual("语音播报不可用，已显示文字回答", viewModel.HintText);

            viewModel.NotifyDegraded();
            Assert.AreEqual(VoiceClientState.Ready, controller.State,
                "view-model notifications only project presentation state");
            viewModel.Dispose();
            socket.Dispose();
        }

        [Test]
        public void VoiceError_TtsAfterAnswer_WaitsForBoundPlaybackFailedThenFinalizesOnce()
        {
            // VR10: VOICE_TTS_ERROR after answer.display is the server's display-only
            // degradation (connection stays open, text on screen) — settle to Ready.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            int degraded = 0;
            c.TtsDegraded += () => degraded++;

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_TTS_ERROR")));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Playing, c.State,
                "an idless voice.error cannot settle a guessed turn by itself");
            Assert.AreEqual(0, degraded);
            string completedTurn = c.TurnId;
            VoiceCommandResult reentrantStart = VoiceCommandResult.InvalidState;
            c.StateChanged += state =>
            {
                if (state == VoiceClientState.Ready)
                    reentrantStart = c.RecordStartAsync();
            };
            s.SimulatePayload(Encoding.UTF8.GetBytes(PlaybackFailedJson(c.SessionId, completedTurn)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.Ok, reentrantStart,
                "a synchronous Ready listener may immediately begin the next turn");
            Assert.AreEqual(VoiceClientState.Capturing, c.State,
                "the re-entrant new turn must not be overwritten by old-turn cleanup");
            Assert.AreNotEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(1, degraded, "TtsDegraded must fire exactly once");

            Assert.AreNotEqual(completedTurn, c.TurnId, "degradation must start a fresh turn");
            Assert.AreEqual(0, c.NextAudioSequence, "new turn starts audio sequence at zero");
            Assert.AreEqual(3, s.SentTexts.Count,
                "the initial and both per-turn session.start frames must be sent exactly once");
            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, completedTurn, "VOICE_TTS_ERROR")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Capturing, c.State,
                "a duplicate idless error from the completed turn cannot fault its successor");
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                StartedJsonWithCapabilities(c.SessionId, c.TurnId, "answer-stream-v1")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Capturing, c.State,
                "a matching per-turn ACK must not reset active capture to Ready");
            Assert.IsTrue(c.AnswerStreamAccepted);
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                StartedJsonWithCapabilities(c.SessionId, c.TurnId, "answer-stream-v1")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            Assert.IsTrue(c.AnswerStreamAccepted,
                "duplicate ACK is idempotent and keeps the accepted per-turn capability");

            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, completedTurn)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            Assert.AreEqual(VoiceCommandResult.StaleTurn, c.LastRejection,
                "an old turn ACK must be ignored");
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson("other-session", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            Assert.AreEqual(VoiceCommandResult.OtherSession, c.LastRejection,
                "an ACK from another session must be rejected");

            s.Dispose();
        }

        [Test]
        public void VoiceError_TtsBeforeAnswer_StillFaulted()
        {
            // Regression guard: TTS failure BEFORE any answer means the user got
            // nothing — still a terminal fault.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            int degraded = 0;
            c.TtsDegraded += () => degraded++;

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_TTS_ERROR")));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(0, degraded);

            s.Dispose();
        }

        [TestCase("request.failed")]
        [TestCase("request.cancelled")]
        public void AnswerStreamTerminal_FinalizesRoundAndAllowsNextTurn(string terminalEvent)
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                StartedJsonWithCapabilities(c.SessionId, c.TurnId, "answer-stream-v1")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            string failedTurn = c.TurnId;

            s.SimulatePayload(Encoding.UTF8.GetBytes(
                AnswerEventJson(c.SessionId, failedTurn, terminalEvent, "request-1", 1)));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Ready, c.State,
                "a request terminal must release the active turn without requiring an answer or playback");
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync(),
                "the same session must accept a fresh turn after a request terminal");
            Assert.AreNotEqual(failedTurn, c.TurnId);
            Assert.AreEqual(0, c.NextAudioSequence);

            s.Dispose();
        }

        [Test]
        public void ClarificationTerminal_FromEarlyVadFinalizesAndKeepsClarificationProjection()
        {
            var (c, s) = NewController();
            var viewModel = new VoiceViewModel(c);
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());

            string endedTurn = c.TurnId;
            var clarification = new ClarificationRequiredMessage
            {
                type = "clarification.required",
                session_id = c.SessionId,
                turn_id = endedTurn,
                reason = "no_speech_detected"
            };
            s.SimulatePayload(Encoding.UTF8.GetBytes(JsonUtility.ToJson(clarification)));
            s.SimulatePayload(Encoding.UTF8.GetBytes(ClarificationStateJson(c.SessionId, endedTurn)));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Ready, c.State,
                "the terminal clarification must close the turn even when VAD ended input during capture");
            Assert.AreEqual(VoiceUiState.Clarification, viewModel.State);
            Assert.AreEqual("no_speech_detected", viewModel.ClarificationReason);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreNotEqual(endedTurn, c.TurnId);
            Assert.AreEqual(0, c.NextAudioSequence);

            viewModel.Dispose();
            s.Dispose();
        }

        [Test]
        public void VoiceError_OtherCode_StillFaulted()
        {
            // Regression guard: every non-degradation code stays terminal.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_PROTOCOL_INVALID")));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual("server_error", c.LastFaultReason);
            Assert.AreEqual("VOICE_PROTOCOL_INVALID", c.LastFaultCode);
            Assert.AreEqual("tts", c.LastFaultField);
            Assert.IsFalse(c.LastFaultCode.Contains("\n"));

            s.Dispose();
        }

        [Test]
        public void Closed_4408_ActiveState_SettlesIdle_NotFaulted()
        {
            // VR10: 4408 VOICE_SESSION_TIMEOUT is the server reaping an idle/expired
            // session, not a protocol violation — settle to Idle (via the legal
            // Cancelling two-hop) with the SessionExpired hint instead of faulting.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            int expired = 0;
            c.SessionExpired += () => expired++;

            s.SimulateServerClose(4408, "voice session timeout");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Idle, c.State, "4408 must settle back to Idle");
            Assert.AreNotEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(1, expired, "SessionExpired must fire exactly once");

            s.Dispose();
        }

        [Test]
        public void SessionTimeoutErrorThen4408_SettlesOnceAndKeepsDisplayedText()
        {
            var c = ToReady(out var s);
            var viewModel = new VoiceViewModel(c);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            string answer = viewModel.AnswerText;
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            int expired = 0;
            c.SessionExpired += () => expired++;
            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_SESSION_TIMEOUT")));
            s.DispatchMessageQueue();
            Assert.AreNotEqual(VoiceClientState.Faulted, c.State,
                "the timeout notification must stop the round without first faulting it");
            s.SimulateServerClose(4408, "voice session timeout");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Idle, c.State);
            Assert.AreEqual(1, expired);
            Assert.AreEqual(answer, viewModel.AnswerText);
            viewModel.Dispose();
            s.Dispose();
        }

        [Test]
        public void SessionTimeoutCloseThenLateError_DoesNotRefaultSettledSession()
        {
            var c = ToReady(out var s);
            s.SimulateServerClose(4408, "voice session timeout");
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Idle, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_SESSION_TIMEOUT")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Idle, c.State,
                "a reordered duplicate timeout error must not turn the settled session Faulted");
            s.Dispose();
        }

        [UnityTest]
        public System.Collections.IEnumerator SessionTimeoutWithoutCloseCallback_SettlesAtConfiguredDeadline()
        {
            var s = new InlineAckSocket();
            s.RaiseCloseOnClose = false;
            s.CloseResult = new TaskCompletionSource<bool>().Task;
            var shortCloseConfig = new VoiceClientConfig
            {
                WsHost = Cfg.WsHost,
                WsPort = Cfg.WsPort,
                UseWss = Cfg.UseWss,
                WsPath = Cfg.WsPath,
                ConnectTimeoutMs = 1,
                SendTimeoutMs = Cfg.SendTimeoutMs
            };
            var c = new VoiceSessionController(shortCloseConfig, s);
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            int expired = 0;
            c.SessionExpired += () => expired++;

            s.SendPayload(ErrorJson(c.SessionId, c.TurnId, "VOICE_SESSION_TIMEOUT"));
            float deadline = Time.realtimeSinceStartup + 2f;
            while (c.State == VoiceClientState.Cancelling && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.AreEqual(VoiceClientState.Idle, c.State,
                "if no close callback arrives, the configured close-settlement deadline releases the session");
            Assert.AreEqual(1, expired);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordStartAsync());
            c.Dispose();
        }

        [TestCase(4400)]
        [TestCase(4401)]
        public void SessionTimeoutFollowedByProtocolOrIdentityClose_RemainsFaulted(int closeCode)
        {
            var s = new InlineAckSocket { RaiseCloseOnClose = false };
            var shortCloseConfig = new VoiceClientConfig
            {
                WsHost = Cfg.WsHost,
                WsPort = Cfg.WsPort,
                UseWss = Cfg.UseWss,
                WsPath = Cfg.WsPath,
                ConnectTimeoutMs = 1000,
                SendTimeoutMs = Cfg.SendTimeoutMs
            };
            var c = new VoiceSessionController(shortCloseConfig, s);
            Assert.DoesNotThrow(() => c.ConnectAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            int expired = 0;
            c.SessionExpired += () => expired++;

            s.SendPayload(ErrorJson(c.SessionId, c.TurnId, "VOICE_SESSION_TIMEOUT"));
            Assert.AreEqual(VoiceClientState.Cancelling, c.State);
            s.SendClose(closeCode, "application fault");

            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual("websocket_close", c.LastFaultReason);
            Assert.AreEqual(closeCode, c.LastCloseCode);
            Assert.AreEqual(0, expired);
            c.Dispose();
        }

        [Test]
        public void Closed_4400_StillFaulted()
        {
            // Regression guard: protocol violations (4400/4401 Application codes) stay
            // terminal — a re-open cannot fix them.
            var c = ToReady(out var s);

            int expired = 0;
            c.SessionExpired += () => expired++;

            s.SimulateServerClose(4400, "voice error close");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual("websocket_close", c.LastFaultReason);
            Assert.AreEqual(4400, c.LastCloseCode);
            Assert.AreEqual(0, expired);

            s.Dispose();
        }

        [Test]
        public void MalformedPayload_StoresStableReasonWithoutPayloadText()
        {
            var c = ToReady(out var s);
            s.SimulatePayload(Encoding.UTF8.GetBytes("{malformed-private-data"));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual("protocol_invalid_json", c.LastFaultReason);
            Assert.IsFalse(c.LastFaultReason.Contains("private"));
            s.Dispose();
        }

        [Test]
        public void Reset_FromFaulted_GoesIdle_ThenCanReconnect()
        {
            // VR10: the Faulted exit — Reset() clears per-session state, then a fresh
            // ConnectAsync reaches Ready on a NEW session id (the dead one is gone).
            var c = ToReady(out var s);
            var deadSession = c.SessionId;

            s.SimulateServerClose(4400, "voice error close");
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, c.State);

            Assert.AreEqual(VoiceCommandResult.Ok, c.Reset());
            Assert.AreEqual(VoiceClientState.Idle, c.State);

            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreNotEqual(deadSession, c.SessionId, "Reset must discard the dead session id");

            s.Dispose();
        }

        [Test]
        public void SendAudioFrame_WithPayload_SendsHeaderThenBinaryAtomically()
        {
            // P0-1 regression: the wire protocol pairs every audio.frame JSON header
            // with exactly one binary PCM16 body; a header-only send leaves the
            // server's pending-header state armed and invalidates the next message.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            Assert.AreEqual(0, c.NextAudioSequence);

            var pcm = new byte[640];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xff);

            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, pcm, 0.5f));

            Assert.AreEqual(2, s.SentTexts.Count, "session.start + one audio.frame header");
            StringAssert.Contains("\"type\":\"audio.frame\"", s.SentTexts[1]);
            StringAssert.Contains("\"sequence\":0", s.SentTexts[1]);
            Assert.AreEqual(1, s.SentBinaries.Count, "exactly one binary body must follow the header");
            Assert.AreEqual(640, s.SentBinaries[0].Length);
            CollectionAssert.AreEqual(pcm, s.SentBinaries[0]);
            Assert.AreEqual(1, c.NextAudioSequence);

            // An empty payload is not a protocol frame and must be refused outright.
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.SendAudioFrameAsync(1, new byte[0], 0f));
            Assert.AreEqual(2, s.SentTexts.Count, "the rejected frame must not reach the wire");
            Assert.AreEqual(1, s.SentBinaries.Count);

            s.Dispose();
        }

        [Test]
        public void FinalPayload_DoesNotStartNextRoundBeforeLocalPlaybackCompletion()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // Final transport payload is not local playback completion. The next turn
            // remains unavailable until F06-02 adds the real player-drained signal.
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.30f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(1, 0.10f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            SimulateTtsPair(s, c.SessionId, c.TurnId, "playback_server1", 0, false);
            SimulateTtsPair(s, c.SessionId, c.TurnId, "playback_server1", 1, true);
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordStartAsync());

            s.Dispose();
        }

        // ====================== P1-1 regression: VAD early endpoint while capturing ======================

        [Test]
        public void EarlyAnswer_DuringCapturing_IsAcceptedAndWaitsForLocalPlaybackCompletion()
        {
            // P1-1 regression: the server VAD endpoint consumed the utterance and
            // published answer.display while the push-to-talk user still HELD the
            // button (client Capturing). The answer must be ACCEPTED — the old code
            // rejected it as StaleTurn, losing the round and every tts.frame after.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            string gotAnswer = null;
            c.AnswerReceived += a => gotAnswer = a;

            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State, "early answer must move Capturing -> Playing");
            Assert.AreEqual("你好", gotAnswer, "the answer text must be projected");

            // Trailing mic frames are refused while Playing (client-side drop; the
            // server drops them too via its trailing-frame guard).
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.SendAudioFrameAsync(0, 0.5f));

            // The final transport pair cannot finish the local playback round.
            SimulateTtsPair(s, c.SessionId, c.TurnId, "playback_server1", 0, true);
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // The user finally releases the button: no crash, no fault, and a new
            // round cannot start on the same connection until the local player drains.
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordEndAsync());
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            s.Dispose();
        }

        [Test]
        public void EarlyLowConfidenceClarification_DuringCapturing_IsIntermediate()
        {
            // Same P1-1 family: a low-confidence early endpoint can raise
            // clarification.required while the client is still Capturing. The
            // The reason is projected immediately but remains an intermediate event:
            // the backend accepts another utterance on this same turn.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            string reason = null;
            c.ClarificationRequired += r => reason = r;

            var m = new ClarificationRequiredMessage
            {
                type = "clarification.required",
                session_id = c.SessionId,
                turn_id = c.TurnId,
                reason = "low_confidence_asr"
            };
            s.SimulatePayload(Encoding.UTF8.GetBytes(JsonUtility.ToJson(m)));
            s.SimulatePayload(Encoding.UTF8.GetBytes(ClarificationStateJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();

            Assert.AreEqual("low_confidence_asr", reason, "the clarification reason must be projected");
            Assert.AreEqual(VoiceClientState.Capturing, c.State,
                "low confidence is intermediate and leaves the same turn available for clarification");

            s.Dispose();
        }

        // ====================== P1-5 regression: pause -> resume reconnect ======================

        [Test]
        public void PauseThenReconnect_ReusesSessionId_AndAdvancesTurnIds()
        {
            // P1-5 regression: a lifecycle pause closes the socket and settles the
            // controller to Idle with nobody driving a reconnect. When the caller
            // DOES reconnect (VoiceClientRuntime.ResumeAsync -> ConnectAsync), the
            // SAME session id must be reused (the server keeps the conversation
            // context) while turn ids keep ADVANCING: the server rejects any turn_id
            // already registered in finished_turn_ids, so resetting the counter to 0
            // would resend "turn-0001" and the session.start would be refused.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            string sessionBefore = c.SessionId;

            // The active turn consumed turn-0001 before lifecycle pause closes it.
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            SimulateTtsPair(s, c.SessionId, c.TurnId, "playback_server1", 0, true);
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // Lifecycle pause: local teardown (socket close 1000) -> Idle.
            Assert.AreEqual(VoiceCommandResult.Ok, c.PauseAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Idle, c.State);

            // Resume reconnect from Idle.
            Assert.AreEqual(VoiceCommandResult.Ok, c.ConnectAsync().GetAwaiter().GetResult());
            s.DispatchMessageQueue(); // Opened -> fresh session.start

            Assert.AreEqual(sessionBefore, c.SessionId, "the session id must survive the pause");
            StringAssert.Contains("turn-0002", s.SentTexts[s.SentTexts.Count - 1],
                "the reconnect session.start must carry an ADVANCED turn id (the server forbids reuse)");

            // The server accepts the fresh binding and the client is Ready again.
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            s.Dispose();
        }

        // ====================== P0-3 regression: server-issued playback id ======================

        [Test]
        public void ServerGeneratedPlaybackId_IsLearnedAndFinalPayloadDoesNotSetReady()
        {
            // P0-3 regression: the client must accept a SERVER-format playback id
            // (e.g. "playback_ab12cd34ef56" from new_id("playback")) instead of
            // fabricating a local "playback-N" that never matches the server's.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            Assert.IsNull(c.PlaybackId, "no playback id is fabricated at answer time anymore");

            // First server frame carries the server-issued id -> learned and bound.
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_ab12cd34ef56", 0, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual("playback_ab12cd34ef56", c.PlaybackId,
                "the server-issued playback id must be learned from the first tts.frame");
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            s.SimulatePayload(new byte[640]);
            s.DispatchMessageQueue();

            // The final header with the SAME id does not complete the round.
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_ab12cd34ef56", 1, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            s.SimulatePayload(new byte[640]);
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            Assert.IsTrue(ReadControllerFlag(c, "_finalPayloadReceived"));
            s.Dispose();
        }

        [Test]
        public void MidRoundPlaybackIdSwitch_IsRejectedAsLate()
        {
            // After learning a playback id, a frame with a DIFFERENT id mid-round is a
            // binding mismatch for the live turn -> late classification (anti bleed).
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, false)));
            s.DispatchMessageQueue();
            s.SimulatePayload(new byte[640]);
            s.DispatchMessageQueue();

            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_intruder", 1, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.LateAnswer, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Playing, c.State, "a foreign playback id must not advance the state");
            s.Dispose();
        }

        // ====================== P1-2 regression: asr / barge_in downstream types ======================

        [Test]
        public void AsrTranscripts_PartialThenFinal_FireEvent_WithoutStateChange()
        {
            // P1-2 regression: asr.partial / asr.final must reach subscribers (UI subtitle)
            // and must NOT disturb the AwaitingAnswer state (informational by protocol).
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            var received = new List<AsrTranscriptMessage>();
            c.TranscriptReceived += received.Add;

            s.SimulatePayload(Encoding.UTF8.GetBytes(AsrJson(c.SessionId, c.TurnId, "asr.partial", "飞机的发动机")));
            s.SimulatePayload(Encoding.UTF8.GetBytes(AsrJson(c.SessionId, c.TurnId, "asr.final", "飞机的发动机是做什么的")));
            s.DispatchMessageQueue();

            Assert.AreEqual(2, received.Count, "both asr events must be surfaced to subscribers");
            Assert.AreEqual("asr.partial", received[0].type);
            Assert.AreEqual("asr.final", received[1].type);
            Assert.AreEqual("飞机的发动机是做什么的", received[1].transcript);
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State, "asr events are informational only");
            Assert.AreEqual(VoiceCommandResult.Ok, c.LastRejection);
            s.Dispose();
        }

        [Test]
        public void AsrTranscriptWhileAwaitingLocalPlayback_IsStillBoundToActiveTurn()
        {
            // Final payload completion does not close the active turn; same-turn informational
            // events remain bound until local playback completion finalizes the turn.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            SimulateTtsPair(s, c.SessionId, c.TurnId, "playback_server1", 0, true);
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            var fired = 0;
            c.TranscriptReceived += _ => fired++;
            s.SimulatePayload(Encoding.UTF8.GetBytes(AsrJson(c.SessionId, c.TurnId, "asr.final", "迟到的转写")));
            s.DispatchMessageQueue();

            Assert.AreEqual(1, fired, "same-turn transcript remains valid until the turn is finalized");
            Assert.AreEqual(VoiceCommandResult.Ok, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            s.Dispose();
        }

        [Test]
        public void BargeInAccepted_WhileCancelling_SettlesToReady_AndFiresEvent()
        {
            // VR9: the barge-in acknowledgement settles the controller out of Cancelling back
            // to Ready (the "barge-in done" edge), so the user can record the next round
            // without restarting PlayMode. The event still fires for UI projection.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            c.CancelAckDelayAsync = _ => new TaskCompletionSource<object>().Task;
            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceClientState.Cancelling, c.State);

            BargeInAcceptedMessage ack = null;
            c.BargeInAccepted += m => ack = m;
            s.SimulatePayload(Encoding.UTF8.GetBytes(BargeInJson(c.SessionId, c.TurnId, "stopped", "stop")));
            s.DispatchMessageQueue();

            Assert.IsNotNull(ack, "barge_in.accepted must be surfaced to subscribers");
            Assert.AreEqual("stopped", ack.status);
            Assert.AreEqual("stop", ack.intent);
            Assert.AreEqual(VoiceClientState.Ready, c.State, "the ack settles the cancel back to Ready");
            s.Dispose();
        }

        // ====================== P1-8 regression: unique per-device session id ======================

        [Test]
        public void SessionId_IsUniquePerController_AndWireLegal()
        {
            // P1-8 regression: two devices (controllers) must never share the hardcoded
            // "vr-session-001" — they would collide in the server's active-binding registry.
            var (c1, s1) = NewController();
            var (c2, s2) = NewController();
            Assert.DoesNotThrow(() => { c1.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.DoesNotThrow(() => { c2.ConnectAsync().GetAwaiter().GetResult(); });
            s1.DispatchMessageQueue();
            s2.DispatchMessageQueue();

            Assert.AreNotEqual(c1.SessionId, c2.SessionId, "each controller must own a unique session id");
            Assert.AreNotEqual("vr-session-001", c1.SessionId, "the hardcoded demo id must be gone");
            StringAssert.IsMatch("^vr-session-[0-9a-f]{12}$", c1.SessionId);
            StringAssert.IsMatch("^vr-session-[0-9a-f]{12}$", c2.SessionId);
            // The session.start on the wire must carry exactly the generated id.
            Assert.IsTrue(s1.SentTexts[0].Contains(c1.SessionId), "session.start must carry the generated id");
            Assert.IsTrue(s2.SentTexts[0].Contains(c2.SessionId), "session.start must carry the generated id");

            c1.Dispose();
            c2.Dispose();
            s1.Dispose();
            s2.Dispose();
        }

        // =============================== State-table legality ===============================

        [Test]
        public void StateRules_LegalChain_IsOk()
        {
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Idle, VoiceClientState.HealthChecking));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.HealthChecking, VoiceClientState.Connecting));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Connecting, VoiceClientState.Ready));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Ready, VoiceClientState.Capturing));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Capturing, VoiceClientState.AwaitingAnswer));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.AwaitingAnswer, VoiceClientState.Playing));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Playing, VoiceClientState.Ready));
        }

        [Test]
        public void StateRules_IllegalJumps_AreRejected()
        {
            // Skipping states is illegal.
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Idle, VoiceClientState.Connecting));
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Ready, VoiceClientState.Playing));
            // Wrong direction on the forward chain.
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Capturing, VoiceClientState.Ready));
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Playing, VoiceClientState.Capturing));
        }

        [Test]
        public void StateRules_Faulted_IsTerminalExceptReset()
        {
            Assert.AreEqual(VoiceStateTransitionCode.Terminal, VoiceStateRules.TryTransition(VoiceClientState.Faulted, VoiceClientState.Ready));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Faulted, VoiceClientState.Idle));
        }

        // =============================== Duplicate idempotency ===============================

        [Test]
        public void DuplicateStart_IsIdempotent()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            s.Dispose();
        }

        [Test]
        public void DuplicateEnd_IsIdempotent()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);
            s.Dispose();
        }

        [Test]
        public void Cancel_Twice_IsIdempotent()
        {
            // VR9: only the Playing wire path keeps a cancel IN FLIGHT (Capturing/AwaitingAnswer
            // cancels settle synchronously to Ready), so drive to Playing to exercise the
            // idempotency guard. The default 5 s watchdog (real Task.Delay) cannot fire inside
            // this synchronous test, so the state stays Cancelling between the two calls.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.CancelAsync());
            Assert.AreEqual(VoiceClientState.Cancelling, c.State);
            s.Dispose();
        }

        [Test]
        public void NoThrow_DuplicateAndInvalidCommands()
        {
            var c = ToReady(out var s);
            // These must never throw, only return stable codes.
            Assert.DoesNotThrow(() => c.RecordEndAsync());
            Assert.DoesNotThrow(() => c.SendAudioFrameAsync(0));
            Assert.DoesNotThrow(() => c.CancelAsync());
            s.Dispose();
        }

        // =============================== Command misuse (illegal transitions via controller) ===============================

        [Test]
        public void RecordEnd_BeforeCapturing_ReturnsInvalidState()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            s.Dispose();
        }

        [Test]
        public void RecordStart_WhileIdle_ReturnsInvalidState()
        {
            var (c, s) = NewController();
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Idle, c.State);
            s.Dispose();
        }

        // =============================== Sequence out-of-order ===============================

        [Test]
        public void AudioFrame_OutOfOrder_Rejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0));
            // Skip to 5 -> out of order.
            Assert.AreEqual(VoiceCommandResult.SequenceMismatch, c.SendAudioFrameAsync(5));
            Assert.AreEqual(VoiceCommandResult.SequenceMismatch, c.LastRejection);
            s.Dispose();
        }

        [Test]
        public void TtsFrame_OutOfOrder_RejectedAndFaulted()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 2, false))); // expecting 0
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.SequenceMismatch, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual("tts_sequence_mismatch", c.LastFaultReason);
            s.Dispose();
        }

        // =============================== Stale / other-session / late rejection ===============================

        [Test]
        public void OtherSession_StartedReplay_IsRejected()
        {
            var c = ToReady(out var s);
            // A started for a different session while already Ready is stale/other.
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson("vr-session-OTHER", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State); // unchanged
            s.Dispose();
        }

        [Test]
        public void OtherSession_Answer_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson("vr-session-OTHER", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.OtherSession, c.LastRejection);
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State); // not advanced to Playing
            s.Dispose();
        }

        [Test]
        public void StaleTurn_Answer_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, "turn-9999")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.StaleTurn, c.LastRejection);
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);
            s.Dispose();
        }

        [Test]
        public void LateAnswer_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // A second answer for the same already-answered turn is late.
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.LateAnswer, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            s.Dispose();
        }

        [Test]
        public void TtsHeaderAfterFinalHeader_IsRejectedAsLate()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            const string pb = "playback_server1"; // server-issued id for this round
            SimulateTtsPair(s, c.SessionId, c.TurnId, pb, 0, true);
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // The final sequence is terminal for transport while local playback continues.
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, pb, 1, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.LateTts, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            s.Dispose();
        }

        // =============================== Version gating ===============================

        [Test]
        public void NonV1_Started_TransitionsFaulted()
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            s.DispatchMessageQueue(); // open + session.start
            // Server replies started with a non-v1 protocol_version.
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJsonNonV1("vr-session-001", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            s.Dispose();
        }

        [Test]
        public void Reset_FromFaulted_ReturnsToIdle()
        {
            var c = NonV1Faulted();
            Assert.AreEqual(VoiceCommandResult.Ok, c.Reset());
            Assert.AreEqual(VoiceClientState.Idle, c.State);
        }

        [Test]
        public void Reset_FromNonFaulted_ReturnsInvalidState()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.Reset());
            s.Dispose();
        }

        [Test]
        public void Faulted_Commands_ReturnFaulted()
        {
            var c = NonV1Faulted();
            Assert.AreEqual(VoiceCommandResult.Faulted, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Faulted, c.RecordEndAsync());
            Assert.AreEqual(VoiceCommandResult.Faulted, c.CancelAsync());
        }

        private static VoiceSessionController NonV1Faulted()
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJsonNonV1("vr-session-001", c.TurnId)));
            s.DispatchMessageQueue();
            s.Dispose();
            return c;
        }
    }
}
