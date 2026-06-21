using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace ExtendedLSC
{
    /// <summary>
    /// Optional "Customize Radio": loops the PLAYER'S OWN music (mp3/wav dropped into the radio folder) while the
    /// LSC menu is open, like a classic tuner-game garage radio. No music is bundled — the player supplies the
    /// tracks. Playback is polled from the script tick (no cross-thread callbacks) so it stays in lockstep with
    /// the menu lifecycle.
    /// </summary>
    public class CustomizeRadio
    {
        public static Action<string> Log;

        private readonly List<string> _playlist = new List<string>();
        private int _index = -1;
        private bool _active;
        private string _dir;
        private float _volume = 0.6f;
        private readonly Random _rng = new Random();

        private WaveOutEvent _output;
        private AudioFileReader _reader;
        private bool _failed;   // NAudio unavailable / device error -> disable silently

        public bool HasTracks => _playlist.Count > 0;
        public bool IsActive => _active;
        public string CurrentTrack { get; private set; }

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Max(0f, Math.Min(1f, value));
                try { if (_reader != null) _reader.Volume = _volume; } catch { }
            }
        }

        public void Initialize(string dir, float volume)
        {
            _dir = dir; _volume = Math.Max(0f, Math.Min(1f, volume));
            try { if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir); } catch { }
            Rescan();
        }

        /// <summary>(Re)scan the radio folder for tracks and reshuffle the order.</summary>
        public void Rescan()
        {
            _playlist.Clear();
            try
            {
                if (Directory.Exists(_dir))
                    foreach (var f in Directory.GetFiles(_dir))
                    {
                        string e = Path.GetExtension(f).ToLowerInvariant();
                        if (e == ".mp3" || e == ".wav") _playlist.Add(f);
                    }
            }
            catch (Exception ex) { Log?.Invoke($"[Radio] scan error: {ex.Message}"); }
            for (int i = _playlist.Count - 1; i > 0; i--)   // Fisher-Yates shuffle
            {
                int j = _rng.Next(i + 1);
                var t = _playlist[i]; _playlist[i] = _playlist[j]; _playlist[j] = t;
            }
            _index = -1;
        }

        public void Start()
        {
            if (_active || _failed) return;
            if (_playlist.Count == 0) { Rescan(); if (_playlist.Count == 0) return; }
            _active = true;
            PlayNext();
        }

        public void Stop()
        {
            _active = false;
            DisposePlayback();
            CurrentTrack = null;
        }

        /// <summary>Call each frame while active — advances to the next track when one finishes.</summary>
        public void Tick()
        {
            if (!_active || _failed) return;
            try
            {
                if (_output == null || _output.PlaybackState == PlaybackState.Stopped)
                    PlayNext();
            }
            catch (Exception ex) { Log?.Invoke($"[Radio] tick error: {ex.Message}"); }
        }

        private void PlayNext()
        {
            DisposePlayback();
            if (_playlist.Count == 0) { _active = false; return; }
            for (int tries = 0; tries < _playlist.Count; tries++)
            {
                _index = (_index + 1) % _playlist.Count;
                string path = _playlist[_index];
                try
                {
                    _reader = new AudioFileReader(path) { Volume = _volume };
                    _output = new WaveOutEvent();
                    _output.Init(_reader);
                    _output.Play();
                    CurrentTrack = Path.GetFileNameWithoutExtension(path);
                    Log?.Invoke($"[Radio] now playing: {CurrentTrack}");
                    return;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[Radio] could not play '{Path.GetFileName(path)}': {ex.Message}");
                    DisposePlayback();
                    // try the next track; if EVERY track fails it's likely NAudio/device — give up quietly
                }
            }
            _failed = true; _active = false;
            Log?.Invoke("[Radio] no playable tracks / audio unavailable — disabled");
        }

        private void DisposePlayback()
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _output = null; _reader = null;
        }
    }
}
