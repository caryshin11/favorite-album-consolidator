// File: Audio/PreviewAudioEngine.cs
using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace SoundShelf.Audio
{
    // Lock down ambiguous types regardless of project usings
    using WinFormsTimer = System.Windows.Forms.Timer;
    using NAudioComplex = NAudio.Dsp.Complex;
    using NAudioPlaybackState = NAudio.Wave.PlaybackState;
    using NAudioStoppedEventArgs = NAudio.Wave.StoppedEventArgs;

    /// <summary>
    /// Single-output, dual-input mixer engine with volume ramps (true crossfade).
    /// FFT is computed from the MIXED signal, so equalizer matches what you hear.
    /// </summary>
    public sealed class PreviewAudioEngine : IDisposable
    {
        public event Action<float[]>? BarsUpdated;
        public event Action<Exception?>? PlaybackStopped;

        public int BarCount { get; }
        public int FftSize { get; }

        // gain used in bar scaling (tweak)
        public float MagnitudeGain { get; set; } = 15f;

        private readonly int _m;

        private WaveOutEvent? _out;
        private MediaFoundationReader? _readerA;
        private MediaFoundationReader? _readerB;

        private Channel? _a;
        private Channel? _b;

        private Mixer2? _mixer;
        private FftTap? _tap;

        private int _volume0to100 = 80;
        private bool _isPaused = false;

        private readonly WinFormsTimer _fadeTimer = new WinFormsTimer { Interval = 25 };
        private FadeState? _fade;
        private bool _stoppingInternal = false;

        public PreviewAudioEngine(int barCount = 48, int fftSize = 2048)
        {
            if ((fftSize & (fftSize - 1)) != 0)
                throw new ArgumentException("fftSize must be a power of two.");

            BarCount = Math.Max(8, barCount);
            FftSize = fftSize;
            _m = (int)Math.Log(FftSize, 2);

            _fadeTimer.Tick += (s, e) => FadeTick();
        }

        public NAudioPlaybackState PlaybackState => _out?.PlaybackState ?? NAudioPlaybackState.Stopped;

        public int Volume
        {
            get => _volume0to100;
            set => SetVolume(value);
        }

        public void SetVolume(int volume0to100)
        {
            _volume0to100 = Math.Clamp(volume0to100, 0, 100);
            if (_out != null) _out.Volume = _volume0to100 / 100f;
        }

        /// <summary>Play URL immediately (clears any pending crossfade).</summary>
        public void PlayUrl(string url)
        {
            EnsureGraph();
            CancelFade();

            DisposeReader(ref _readerA);
            DisposeReader(ref _readerB);

            _readerA = new MediaFoundationReader(url);
            _a!.SetSource(_readerA);
            _a.Volume = 1f;

            _b!.ClearSource();
            _b.Volume = 0f;

            StartPlaybackIfNeeded();
        }

        /// <summary>
        /// Crossfade from current A to new URL in B over crossfadeMs.
        /// If nothing is playing, this behaves like PlayUrl().
        /// </summary>
        public void CrossfadeTo(string url, int crossfadeMs)
        {
            EnsureGraph();

            if (_a == null || _b == null)
                return;

            CancelFade();

            // If not currently playing, just start it
            if (_readerA == null || PlaybackState != NAudioPlaybackState.Playing)
            {
                PlayUrl(url);
                return;
            }

            // Load next into B
            DisposeReader(ref _readerB);
            _readerB = new MediaFoundationReader(url);
            _b.SetSource(_readerB);

            // Start next silent
            _b.Volume = 0f;
            _a.Volume = 1f;

            StartPlaybackIfNeeded();

            int ms = Math.Max(150, crossfadeMs);
            _fade = new FadeState(ms, _fadeTimer.Interval);
            _fadeTimer.Start();
        }

        public void Pause()
        {
            if (_out == null) return;
            if (_out.PlaybackState == NAudioPlaybackState.Playing)
            {
                _out.Pause();
                _isPaused = true;
            }
        }

        public void Resume()
        {
            if (_out == null) return;
            if (_out.PlaybackState == NAudioPlaybackState.Paused)
            {
                _out.Play();
                _isPaused = false;
            }
        }

        public void Stop()
        {
            if (_stoppingInternal) return;

            try
            {
                _stoppingInternal = true;

                CancelFade();

                if (_out != null)
                {
                    try { _out.PlaybackStopped -= OnPlaybackStopped; } catch { }
                    try { _out.Stop(); } catch { }
                }

                DisposeReader(ref _readerA);
                DisposeReader(ref _readerB);

                _a?.ClearSource();
                _b?.ClearSource();

                _tap = null;
                _mixer = null;

                _out?.Dispose();
                _out = null;
            }
            finally
            {
                _stoppingInternal = false;
            }
        }

        public void Dispose()
        {
            Stop();
            _fadeTimer.Dispose();
        }

        /// <summary>
        /// Remaining seconds of the "current" stream (A) if known. Used for crossfade triggering.
        /// </summary>
        public double? GetCurrentRemainingSeconds()
        {
            try
            {
                if (_readerA == null) return null;
                double total = _readerA.TotalTime.TotalSeconds;
                if (total <= 0) return null;
                double cur = _readerA.CurrentTime.TotalSeconds;
                return Math.Max(0, total - cur);
            }
            catch { return null; }
        }

        // ---------------- Internals ----------------

        private void EnsureGraph()
        {
            if (_out != null) return;

            _out = new WaveOutEvent { DesiredLatency = 100 };
            _out.Volume = _volume0to100 / 100f;
            _out.PlaybackStopped += OnPlaybackStopped;

            _a = new Channel();
            _b = new Channel();

            _mixer = new Mixer2(_a, _b);

            _tap = new FftTap(_mixer, FftSize, _m);
            _tap.FftCalculated += fft =>
            {
                var bars = ComputeBars(fft);
                BarsUpdated?.Invoke(bars);
            };

            _out.Init(_tap);
        }

        private void StartPlaybackIfNeeded()
        {
            if (_out == null) return;

            // if user paused intentionally, don't auto-resume
            if (_out.PlaybackState == NAudioPlaybackState.Paused && _isPaused)
                return;

            if (_out.PlaybackState != NAudioPlaybackState.Playing)
            {
                _out.Play();
                _isPaused = false;
            }
        }

        private void FadeTick()
        {
            if (_fade == null || _a == null || _b == null)
            {
                CancelFade();
                return;
            }

            _fade.Step++;
            float t = Math.Min(1f, (float)_fade.Step / _fade.TotalSteps);

            // linear fade
            _a.Volume = 1f - t;
            _b.Volume = t;

            if (t >= 1f)
            {
                // Crossfade complete: promote B -> A
                DisposeReader(ref _readerA);
                _readerA = _readerB;
                _readerB = null;

                _a.SetSource(_readerA);
                _a.Volume = 1f;

                _b.ClearSource();
                _b.Volume = 0f;

                CancelFade();
            }
        }

        private void CancelFade()
        {
            _fade = null;
            try { _fadeTimer.Stop(); } catch { }
        }

        private void OnPlaybackStopped(object? sender, NAudioStoppedEventArgs e)
        {
            if (_stoppingInternal) return;

            // If an error occurs or everything is gone, fully reset.
            bool anySource = _readerA != null || _readerB != null;
            if (e.Exception != null || !anySource)
            {
                Stop();
                PlaybackStopped?.Invoke(e.Exception);
            }
        }

        private static void DisposeReader(ref MediaFoundationReader? r)
        {
            if (r == null) return;
            try { r.Dispose(); } catch { }
            r = null;
        }

        // ---------------- FFT -> Bars ----------------

        private float[] ComputeBars(NAudioComplex[] fft)
        {
            int binCount = fft.Length / 2;
            var bars = new float[BarCount];
            if (binCount <= 0) return bars;

            Span<float> mag = binCount <= 4096 ? stackalloc float[binCount] : new float[binCount];

            mag[0] = 0f;
            for (int i = 1; i < binCount; i++)
            {
                float re = fft[i].X;
                float im = fft[i].Y;
                mag[i] = (float)Math.Sqrt(re * re + im * im);
            }

            // Log-ish grouping: low freqs get more resolution
            for (int b = 0; b < BarCount; b++)
            {
                double t0 = (double)b / BarCount;
                double t1 = (double)(b + 1) / BarCount;

                int i0 = (int)Math.Clamp(Math.Pow(binCount, t0), 1, binCount - 1);
                int i1 = (int)Math.Clamp(Math.Pow(binCount, t1), i0 + 1, binCount);

                float sum = 0f;
                for (int i = i0; i < i1; i++) sum += mag[i];

                float avg = sum / Math.Max(1, i1 - i0);

                // compress dynamic range
                float v = (float)Math.Log10(1f + avg * MagnitudeGain);
                bars[b] = Math.Clamp(v, 0f, 1f);
            }

            return bars;
        }

        // ---------------- Mixer + tap ----------------

        private sealed class Channel : ISampleProvider
        {
            private ISampleProvider? _src;
            public float Volume { get; set; } = 1f;

            public void SetSource(MediaFoundationReader? reader) => _src = reader?.ToSampleProvider();
            public void ClearSource() => _src = null;

            public WaveFormat WaveFormat => _src?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

            public int Read(float[] buffer, int offset, int count)
            {
                if (_src == null)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                int read = _src.Read(buffer, offset, count);

                float vol = Volume;
                for (int i = 0; i < read; i++)
                    buffer[offset + i] *= vol;

                // pad remainder with zeros to keep mixer stable
                if (read < count)
                    Array.Clear(buffer, offset + read, count - read);

                return count;
            }
        }

        private sealed class Mixer2 : ISampleProvider
        {
            private readonly Channel _a;
            private readonly Channel _b;

            public Mixer2(Channel a, Channel b) { _a = a; _b = b; }

            public WaveFormat WaveFormat => _a.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                float[] ba = new float[count];
                float[] bb = new float[count];

                _a.Read(ba, 0, count);
                _b.Read(bb, 0, count);

                for (int i = 0; i < count; i++)
                    buffer[offset + i] = ba[i] + bb[i];

                return count;
            }
        }

        private sealed class FftTap : ISampleProvider
        {
            private readonly ISampleProvider _src;
            private readonly int _fftSize;
            private readonly int _m;

            private readonly NAudioComplex[] _buf;
            private int _pos;

            public event Action<NAudioComplex[]>? FftCalculated;

            public FftTap(ISampleProvider src, int fftSize, int m)
            {
                _src = src;
                _fftSize = fftSize;
                _m = m;
                _buf = new NAudioComplex[_fftSize];
            }

            public WaveFormat WaveFormat => _src.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _src.Read(buffer, offset, count);

                for (int n = 0; n < read; n++)
                {
                    float sample = buffer[offset + n];
                    float w = (float)FastFourierTransform.HannWindow(_pos, _fftSize);

                    _buf[_pos].X = sample * w;
                    _buf[_pos].Y = 0f;

                    _pos++;
                    if (_pos >= _fftSize)
                    {
                        _pos = 0;

                        var fft = new NAudioComplex[_fftSize];
                        Array.Copy(_buf, fft, _fftSize);

                        FastFourierTransform.FFT(true, _m, fft);
                        FftCalculated?.Invoke(fft);
                    }
                }

                return read;
            }
        }

        private sealed class FadeState
        {
            public int Step = 0;
            public int TotalSteps;

            public FadeState(int totalMs, int tickMs)
            {
                TotalSteps = Math.Max(1, totalMs / Math.Max(1, tickMs));
            }
        }
    }
}
