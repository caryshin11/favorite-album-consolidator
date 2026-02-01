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
        private float[]? _prevZoneLevel;   // previous raw level (per bar)
        private float[]? _beatEnv;         // envelope for beat motion (per bar)
        private float _kickEnv = 0f;
        private float _prevKickLevel = 0f;


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

        private float[] ComputeBars(NAudio.Dsp.Complex[] fft)
        {
            int binCount = fft.Length / 2;
            var bars = new float[BarCount];
            if (binCount <= 0) return bars;

            // init history buffers
            if (_prevZoneLevel == null || _prevZoneLevel.Length != BarCount)
                _prevZoneLevel = new float[BarCount];
            if (_beatEnv == null || _beatEnv.Length != BarCount)
                _beatEnv = new float[BarCount];

            int sampleRate = 44100;
            try { sampleRate = _tap?.WaveFormat.SampleRate ?? 44100; } catch { }

            // magnitudes
            float[] mag = new float[binCount];
            mag[0] = 0f;
            for (int i = 1; i < binCount; i++)
            {
                float re = fft[i].X;
                float im = fft[i].Y;
                mag[i] = (float)Math.Sqrt(re * re + im * im);
            }

            // --- Zones (hip hop) ---
            const float F_LEFT_LO = 55f;     // beat/groove region
            const float F_LEFT_HI = 260f;

            const float F_MID_LO = 260f;     // covered by grid: keep small
            const float F_MID_HI = 900f;

            const float F_VOX_LO = 900f;     // vocals/presence
            const float F_VOX_HI = 6500f;

            // Dedicated kick detector band (sub-band)
            const float F_KICK_LO = 55f;
            const float F_KICK_HI = 120f;

            int leftCount = (int)Math.Round(BarCount * 0.40);
            int midCount = (int)Math.Round(BarCount * 0.16);
            int rightCount = BarCount - leftCount - midCount;

            const float MID_ATTENUATION = 0.35f;

            // gains
            const float LEFT_GAIN = 1.15f;  // beatness comes from envelope
            const float VOX_GAIN = 6.0f;   // stronger vocals

            // beat envelope tuning (left dancing)
            const float BEAT_SENS = 3.0f;
            const float ATTACK = 0.55f;
            const float RELEASE = 0.86f;
            const float LEVEL_MIX = 0.22f;

            // kick detector tuning (push bars higher on kicks)
            const float KICK_SENS = 4.2f;  // ↑ more = more kick spikes
            const float KICK_ATTACK = 0.65f; // fast rise
            const float KICK_RELEASE = 0.88f; // slower fall
            const float KICK_BOOST_MAX = 2.2f; // max multiplier applied to LEFT bars

            int FreqToBin(float f) => (int)Math.Round((f * FftSize) / (float)sampleRate);

            float ReadBandRms(float f0, float f1)
            {
                if (f1 <= f0) return 0f;

                int i0 = Math.Clamp(FreqToBin(f0), 1, binCount - 1);
                int i1 = Math.Clamp(FreqToBin(f1), i0 + 1, binCount);

                double sumSq = 0.0;
                int n = 0;

                for (int i = i0; i < i1; i++)
                {
                    float v = mag[i];
                    sumSq += v * v;
                    n++;
                }

                if (n <= 0) return 0f;
                return (float)Math.Sqrt(sumSq / n);
            }

            // ---- Kick detector envelope (sub-band) ----
            {
                float kickRms = ReadBandRms(F_KICK_LO, F_KICK_HI);

                // compress kick band
                float kickLevel = (float)Math.Log10(1f + kickRms * (MagnitudeGain * 1.25f));
                kickLevel = Math.Clamp(kickLevel, 0f, 1f);

                // onset = upward changes
                float kickFlux = Math.Max(0f, kickLevel - _prevKickLevel);
                _prevKickLevel = kickLevel;

                float kick = Math.Clamp(kickFlux * KICK_SENS, 0f, 1f);

                // attack/release envelope
                if (kick > _kickEnv) _kickEnv = _kickEnv + (kick - _kickEnv) * KICK_ATTACK;
                else _kickEnv = _kickEnv * KICK_RELEASE;
            }

            void FillZone(int startBar, int count, float fLo, float fHi, float gain, float zoneAtten, bool beatDance, bool vocalLift)
            {
                if (count <= 0) return;

                fLo = Math.Max(1f, fLo);
                fHi = Math.Max(fLo + 1f, fHi);

                double logLo = Math.Log(fLo);
                double logHi = Math.Log(fHi);

                for (int b = 0; b < count; b++)
                {
                    int bi = startBar + b;

                    float t0 = (float)b / count;
                    float t1 = (float)(b + 1) / count;

                    float bandLo = (float)Math.Exp(logLo + (logHi - logLo) * t0);
                    float bandHi = (float)Math.Exp(logLo + (logHi - logLo) * t1);

                    float rms = ReadBandRms(bandLo, bandHi);

                    // compress to 0..1 "level"
                    float level = (float)Math.Log10(1f + rms * (MagnitudeGain * gain));
                    level = Math.Clamp(level, 0f, 1f);

                    float v = level;

                    if (beatDance)
                    {
                        // onset / beat energy from upward changes
                        float prev = _prevZoneLevel![bi];
                        float flux = Math.Max(0f, level - prev);

                        float beat = Math.Clamp(flux * BEAT_SENS, 0f, 1f);

                        // envelope
                        float env = _beatEnv![bi];
                        if (beat > env) env = env + (beat - env) * ATTACK;
                        else env = env * RELEASE;

                        _beatEnv[bi] = env;
                        _prevZoneLevel[bi] = level;

                        // mix a bit of level + mostly env (dancing)
                        v = (LEVEL_MIX * level) + ((1f - LEVEL_MIX) * env);

                        // KICK BOOST: multiply left bars up when kicks hit
                        float kickMul = 1f + _kickEnv * (KICK_BOOST_MAX - 1f);
                        v = Math.Clamp(v * kickMul, 0f, 1f);
                    }
                    else
                    {
                        _prevZoneLevel![bi] = level;

                        // light smoothing for non-beat zones
                        float env = _beatEnv![bi];
                        _beatEnv[bi] = env * 0.92f + level * 0.08f;
                        v = _beatEnv[bi];
                    }

                    if (vocalLift)
                    {
                        // strong lift + soft floor so vocals animate
                        v = 1f - (float)Math.Pow(1f - Math.Clamp(v, 0f, 1f), 3.8f);

                        const float floor = 0.12f;
                        v = floor + (1f - floor) * v;
                    }

                    v *= zoneAtten;
                    bars[bi] = Math.Clamp(v, 0f, 1f);
                }
            }

            // Left: beat dance + kick boost
            FillZone(0, leftCount, F_LEFT_LO, F_LEFT_HI, LEFT_GAIN, 1f, beatDance: true, vocalLift: false);

            // Middle: downplay
            FillZone(leftCount, midCount, F_MID_LO, F_MID_HI, 1f, MID_ATTENUATION, beatDance: false, vocalLift: false);

            // Right: vocals boosted
            FillZone(leftCount + midCount, rightCount, F_VOX_LO, F_VOX_HI, VOX_GAIN, 1f, beatDance: false, vocalLift: true);

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
