// UnityTtsAudioPlayerTests.cs
// P0-5 regression (PlayMode): the TTS audio player must actually sound decoded clips.
// Before the fix, Mp3PlaybackQueue.FramePlayed had no runtime subscriber and the decoded
// AudioClips were silently discarded; these tests pin the player-side contract the
// bootstrap wiring relies on: idle channel plays immediately, busy channel queues strictly
// in arrival order, Tick advances when the channel frees, Cancel/Dispose silence and clear.
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Yilan.Voice.Runtime.Playback;

namespace Yilan.Voice.Playback
{
    public class UnityTtsAudioPlayerTests
    {
        private GameObject _owner;
        private AudioSource _source;
        private UnityTtsAudioPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("tts-player-test");
            _source = _owner.AddComponent<AudioSource>();
            _player = new UnityTtsAudioPlayer(_source);
        }

        [TearDown]
        public void TearDown()
        {
            _player?.Dispose();
            if (_owner != null)
            {
                UnityEngine.Object.Destroy(_owner);
            }
        }

        private static AudioClip Clip(string name)
        {
            // 100 ms of mono 16 kHz — a plausible one-frame TTS slice.
            return AudioClip.Create(name, 1600, 1, 16000, false);
        }

        [Test]
        public void Constructor_NullSource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new UnityTtsAudioPlayer(null));
        }

        [Test]
        public void Enqueue_IdleChannel_PlaysClipImmediately()
        {
            var clip = Clip("tts-a");

            _player.Enqueue(clip);

            Assert.IsTrue(_source.isPlaying, "decoded clip must start sounding on an idle channel");
            Assert.AreSame(clip, _source.clip);
            Assert.AreEqual(0, _player.PendingCount);
        }

        [Test]
        public void Enqueue_BusyChannel_QueuesClipInArrivalOrder()
        {
            var first = Clip("tts-a");
            var second = Clip("tts-b");
            _player.Enqueue(first);
            Assume.That(_source.isPlaying, "first clip must own the channel for the queueing path");

            _player.Enqueue(second);

            Assert.AreEqual(1, _player.PendingCount, "second clip must wait, never overlap");
            Assert.AreSame(first, _source.clip);
        }

        [UnityTest]
        public IEnumerator Tick_FreedChannel_AdvancesToNextQueuedClip()
        {
            var first = Clip("tts-a");
            var second = Clip("tts-b");
            _player.Enqueue(first);
            _player.Enqueue(second);
            Assume.That(_source.isPlaying, "first clip must own the channel");
            _player.Tick(); // establish that Unity actually started this clip
            _source.Stop(); // deterministic stand-in for "the current clip finished"

            _player.Tick();
            yield return null;
            _player.Tick();

            Assert.AreSame(second, _source.clip, "Tick must advance to the next queued clip");
            Assert.AreEqual(0, _player.PendingCount);
        }

        [UnityTest]
        public IEnumerator Tick_TransientNotPlayingObservation_DoesNotAdvanceTheQueue()
        {
            var first = Clip("tts-transient-a");
            var second = Clip("tts-transient-b");
            _player.Enqueue(first);
            _player.Enqueue(second);
            Assume.That(_source.isPlaying, "first clip must own the channel");
            _player.Tick();

            _source.Stop();
            _player.Tick(); // provisional false observation
            _source.Play(); // model a brief isPlaying=false observation before Unity resumes
            yield return null;
            _player.Tick();

            Assert.AreSame(first, _source.clip, "one transient false must not advance to the tail clip");
            Assert.AreEqual(1, _player.PendingCount);
        }

        [Test]
        public void Tick_PlayingChannel_ChangesNothing()
        {
            var first = Clip("tts-a");
            _player.Enqueue(first);
            Assume.That(_source.isPlaying, "first clip must own the channel");

            _player.Tick();

            Assert.AreSame(first, _source.clip);
        }

        [Test]
        public void Cancel_StopsSoundingClip_AndClearsPending()
        {
            var first = Clip("tts-a");
            var second = Clip("tts-b");
            _player.Enqueue(first);
            _player.Enqueue(second);
            Assume.That(_source.isPlaying, "first clip must own the channel");

            _player.Cancel();

            Assert.IsFalse(_source.isPlaying, "barge-in must silence the sounding clip");
            Assert.IsNull(_source.clip);
            Assert.AreEqual(0, _player.PendingCount);
        }

        [Test]
        public void Enqueue_NullClip_IsNoOp()
        {
            // NOTE: a fresh AudioSource reports isPlaying == true under Unity's dummy
            // audio driver (batchmode), so only the player-owned state is asserted here.
            _player.Enqueue(null);

            Assert.AreEqual(0, _player.PendingCount);
            Assert.IsNull(_source.clip, "null clip must never bind to the channel");
        }

        [Test]
        public void Dispose_SilencesAndIgnoresLaterEnqueue()
        {
            var first = Clip("tts-a");
            _player.Enqueue(first);
            Assume.That(_source.isPlaying, "first clip must own the channel");

            _player.Dispose();
            _player.Enqueue(Clip("tts-after-dispose"));

            Assert.IsFalse(_source.isPlaying);
            Assert.IsNull(_source.clip);
            Assert.AreEqual(0, _player.PendingCount);
        }

        [UnityTest]
        public IEnumerator Player_ReportsActualDrainOnceAndCancelNeverReportsNormalDrain()
        {
            drainCount = stopCount = 0;
            drainedId = stoppedId = null;
            drainedGeneration = stoppedGeneration = -1;
            var drained = typeof(UnityTtsAudioPlayer).GetEvent("PlaybackDrained");
            var stopped = typeof(UnityTtsAudioPlayer).GetEvent("PlaybackStopped");
            Assert.IsNotNull(drained, "actual AudioSource completion needs an explicit drain notification");
            Assert.IsNotNull(stopped, "cancel must be observable as stopped, not normal completion");

            var drainedHandler = Delegate.CreateDelegate(drained.EventHandlerType, this,
                GetType().GetMethod(nameof(OnDrained), BindingFlags.Instance | BindingFlags.NonPublic));
            var stoppedHandler = Delegate.CreateDelegate(stopped.EventHandlerType, this,
                GetType().GetMethod(nameof(OnStopped), BindingFlags.Instance | BindingFlags.NonPublic));
            drained.AddEventHandler(_player, drainedHandler);
            stopped.AddEventHandler(_player, stoppedHandler);

            Assert.IsFalse(_player.Enqueue(null, "pb-empty", 7));
            _player.Tick();
            Assert.AreEqual(0, drainCount, "an empty queue cannot report a successful playback drain");
            _player.Enqueue(Clip("tts-drain-a"), "pb-drain", 7);
            _player.Enqueue(Clip("tts-drain-b"), "pb-drain", 7);
            float deadline = Time.realtimeSinceStartup + 3f;
            while (drainCount == 0 && Time.realtimeSinceStartup < deadline)
            {
                _player.Tick();
                yield return null;
            }

            Assert.AreEqual(1, drainCount, "all queued clips must finish before one round drain is raised");
            Assert.AreEqual("pb-drain", drainedId);
            Assert.AreEqual(7, drainedGeneration);
            Assert.AreEqual(0, stopCount);

            _player.Enqueue(Clip("tts-cancel"), "pb-cancel", 8);
            _player.Cancel();
            Assert.AreEqual(1, drainCount, "cancel must never produce a normal drain");
            Assert.AreEqual(1, stopCount);
            Assert.AreEqual("pb-cancel", stoppedId);
            Assert.AreEqual(8, stoppedGeneration);
        }

        [Test]
        public void Enqueue_InvalidClip_ReportsFailureWithRoundGeneration()
        {
            var failed = typeof(UnityTtsAudioPlayer).GetEvent("PlaybackFailed");
            Assert.IsNotNull(failed, "invalid clips need a classified failure path instead of silent non-completion");
            failureCount = 0;
            failureReason = failureId = null;
            failureGeneration = -1;
            failed.AddEventHandler(_player, Delegate.CreateDelegate(failed.EventHandlerType, this,
                GetType().GetMethod(nameof(OnPlaybackFailed), BindingFlags.Instance | BindingFlags.NonPublic)));

            Assert.IsFalse(_player.Enqueue(null, "pb-invalid", 12));
            Assert.AreEqual(1, failureCount);
            Assert.AreEqual("pb-invalid", failureId);
            Assert.AreEqual(12, failureGeneration);
            Assert.AreEqual("invalid_audio_clip", failureReason);
        }

        [Test]
        public void Cancel_AdvancesGenerationAndDropsLateOldClip()
        {
            Assert.IsTrue(_player.Enqueue(Clip("tts-old-generation"), "pb-old", 4));
            _player.Cancel(5);

            Assert.IsFalse(_player.Enqueue(Clip("tts-late-generation"), "pb-old", 4));
            Assert.IsNull(_source.clip, "a cancelled generation cannot revive its old clip");
            Assert.AreEqual(0, _player.PendingCount);
        }

        private void OnPlaybackFailed(string playbackId, int generation, string reason)
        {
            failureCount++;
            failureId = playbackId;
            failureGeneration = generation;
            failureReason = reason;
        }

        private void OnDrained(string playbackId, int generation)
        {
            drainCount++;
            drainedId = playbackId;
            drainedGeneration = generation;
        }

        private void OnStopped(string playbackId, int generation)
        {
            stopCount++;
            stoppedId = playbackId;
            stoppedGeneration = generation;
        }

        private int drainCount;
        private int stopCount;
        private string drainedId;
        private int drainedGeneration;
        private string stoppedId;
        private int stoppedGeneration;
        private int failureCount;
        private string failureId;
        private int failureGeneration;
        private string failureReason;
    }
}
