using System.Drawing;

namespace SoundShelf.Audio
{

    public static class EqualizerConfig
    {
        // FFT / bar layout
        public const int BarCount = 24;      // 32 / 48 / 64
        public const int FftSize = 2048;     // 1024 / 2048 / 4096 (power of 2)

        // Animation feel
        // Attack: how quickly bars rise to new values (0..1). Higher = snappier.
        public const float Attack = 0.45f;

        // Decay: multiplier applied each frame when signal drops (0..1). Lower = faster fall.
        public const float Decay = 0.92f;

        // Rendering
        public const int FrameMs = 16;   
        public const int PaddingPx = 14;     // inset from edges

        // Gap between bars (px). 
        public const float GapPx = 3f;

        // Rounded corners
        public const float MaxCornerRadius = 8f;

        // Keep a tiny baseline
        public const float MinBarHeightPx = 2f;

        // Body bar color and alpha
        public static readonly Color BarBody = Color.FromArgb(55, 170, 120, 255);

        // Top “cap” highlight (slightly brighter)
        public static readonly Color BarCap = Color.FromArgb(85, 236, 236, 241);

        // Background fill color for the equalizer layer (match gridHost)
        public static readonly Color Background = Color.FromArgb(31, 31, 31);

        // --- FFT -> bar dynamic range ---
        // Higher = louder bars. Typical range: 40–90.
        public const float MagnitudeGain = 60f;
    }
}
