using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yilan.Voice.Runtime.Playback
{
    /// <summary>
    /// Plays decoded clips sequentially on one AudioSource and reports completion only after
    /// the source has stopped sounding the last accepted clip. Tick must run on Unity's main thread.
    /// </summary>
    public sealed class UnityTtsAudioPlayer : IDisposable
    {
        private sealed class QueuedClip
        {
            public AudioClip Clip;
            public string PlaybackId;
            public int Generation;
        }

        private readonly AudioSource _source;
        private readonly Queue<QueuedClip> _pending = new Queue<QueuedClip>();
        private QueuedClip _active;
        private bool _activeObservedPlaying;
        private int _stoppedObservationFrame = -1;
        private int _knownGeneration = -1;
        private bool _disposed;

        public event Action<string, int> PlaybackDrained;
        public event Action<string, int> PlaybackStopped;
        public event Action<string, int, string> PlaybackFailed;

        public UnityTtsAudioPlayer(AudioSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>Number of decoded clips waiting behind the active clip.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Queue a valid decoded clip with its round identity.</summary>
        public bool Enqueue(AudioClip clip, string playbackId, int generation)
        {
            if (_disposed || generation < 0 || generation < _knownGeneration) return false;
            if (generation > _knownGeneration)
            {
                StopActive();
                _knownGeneration = generation;
            }
            if (string.IsNullOrEmpty(playbackId) || !IsPlayable(clip))
            {
                if (_active != null && _active.Generation == generation)
                    StopActive();
                PlaybackFailed?.Invoke(playbackId ?? string.Empty, generation, "invalid_audio_clip");
                return false;
            }
            if (_active != null && (_active.Generation != generation || _active.PlaybackId != playbackId))
                StopActive();
            var item = new QueuedClip { Clip = clip, PlaybackId = playbackId, Generation = generation };
            if (_active == null)
                return Start(item);
            else
                _pending.Enqueue(item);
            return true;
        }

        /// <summary>Compatibility entry point for direct player tests without a round identity.</summary>
        public void Enqueue(AudioClip clip)
        {
            if (_disposed || clip == null) return;
            var item = new QueuedClip { Clip = clip, PlaybackId = string.Empty, Generation = -1 };
            if (_active == null)
                Start(item);
            else
                _pending.Enqueue(item);
        }

        /// <summary>
        /// Observe AudioSource on the main thread. A single isPlaying=false observation is
        /// provisional; a later frame must confirm it so a transient Unity false does not drop
        /// the clip tail or signal an early drain.
        /// </summary>
        public void Tick()
        {
            if (_disposed || _active == null) return;
            if (_source.isPlaying)
            {
                _activeObservedPlaying = true;
                _stoppedObservationFrame = -1;
                return;
            }

            if (!_activeObservedPlaying) return;
            if (_stoppedObservationFrame < 0)
            {
                _stoppedObservationFrame = Time.frameCount;
                return;
            }
            if (Time.frameCount <= _stoppedObservationFrame) return;

            var completed = _active;
            _active = null;
            _activeObservedPlaying = false;
            _stoppedObservationFrame = -1;
            if (_pending.Count > 0)
            {
                Start(_pending.Dequeue());
                return;
            }

            _source.clip = null;
            if (completed != null && !string.IsNullOrEmpty(completed.PlaybackId))
                PlaybackDrained?.Invoke(completed.PlaybackId, completed.Generation);
        }

        /// <summary>Stop the active round and discard queued clips; this never raises PlaybackDrained.</summary>
        public void Cancel()
        {
            if (_disposed) return;
            Cancel(_knownGeneration + 1);
        }

        /// <summary>Stop playback and synchronize the generation supplied by the owner queue.</summary>
        public void Cancel(int currentGeneration)
        {
            if (_disposed) return;
            _knownGeneration = Math.Max(_knownGeneration, currentGeneration);
            StopActive();
        }

        private bool Start(QueuedClip item)
        {
            _active = item;
            _activeObservedPlaying = false;
            _stoppedObservationFrame = -1;
            try
            {
                _source.clip = item.Clip;
                _source.Play();
                return true;
            }
            catch
            {
                if (_source.isPlaying) _source.Stop();
                _active = null;
                _source.clip = null;
                PlaybackFailed?.Invoke(item.PlaybackId, item.Generation, "local_playback");
                return false;
            }
        }

        private static bool IsPlayable(AudioClip clip)
        {
            return clip != null && clip.samples > 0 && clip.length > 0f &&
                   clip.loadState == AudioDataLoadState.Loaded;
        }

        private void StopActive()
        {
            var stopped = _active ?? (_pending.Count > 0 ? _pending.Peek() : null);
            _pending.Clear();
            _active = null;
            _activeObservedPlaying = false;
            _stoppedObservationFrame = -1;
            if (_source.isPlaying) _source.Stop();
            _source.clip = null;
            if (stopped != null && !string.IsNullOrEmpty(stopped.PlaybackId))
                PlaybackStopped?.Invoke(stopped.PlaybackId, stopped.Generation);
        }

        public void Dispose()
        {
            if (_disposed) return;
            StopActive();
            _disposed = true;
            PlaybackDrained = null;
            PlaybackStopped = null;
            PlaybackFailed = null;
        }
    }
}
