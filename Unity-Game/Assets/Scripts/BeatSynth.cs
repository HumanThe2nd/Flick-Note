using UnityEngine;

/// <summary>
/// Generates a simple backing track in code (no audio files needed): kick on
/// every beat, snare on 2 and 4, hi-hats on eighths, a bassline and soft
/// chords (Am - F - C - G), one chord per bar.
/// </summary>
public static class BeatSynth
{
    public static AudioClip Create(float bpm, int beats, int sampleRate = 44100)
    {
        float spb = 60f / bpm;                          // seconds per beat
        int length = Mathf.CeilToInt((beats * spb + 1f) * sampleRate);
        var data = new float[length];
        var rng = new System.Random(1234);

        // Chord roots (Hz) and chord tones for Am, F, C, G.
        float[] roots = { 55.00f, 43.65f, 65.41f, 49.00f };
        float[][] chords =
        {
            new[] { 220.00f, 261.63f, 329.63f },
            new[] { 174.61f, 220.00f, 261.63f },
            new[] { 196.00f, 261.63f, 329.63f },
            new[] { 196.00f, 246.94f, 293.66f },
        };

        for (int b = 0; b < beats; b++)
        {
            int start = (int)(b * spb * sampleRate);
            int bar = b / 4;
            bool countIn = b < 8;

            Kick(data, start, sampleRate, countIn ? 0.5f : 0.9f);
            if (!countIn && b % 2 == 1) Snare(data, start, sampleRate, rng);
            Hat(data, start, sampleRate, rng, 0.12f);
            Hat(data, start + (int)(spb * 0.5f * sampleRate), sampleRate, rng, 0.07f);

            if (!countIn)
            {
                float root = roots[bar % 4];
                Tone(data, start, sampleRate, root, spb * 0.9f, 0.22f, 0.02f, 0.25f);
                if (b % 4 == 0)
                    foreach (float f in chords[bar % 4])
                        Tone(data, start, sampleRate, f, spb * 4f, 0.035f, 0.15f, 0.6f);
            }
        }

        float peak = 0.001f;
        foreach (float s in data) peak = Mathf.Max(peak, Mathf.Abs(s));
        for (int i = 0; i < data.Length; i++) data[i] *= 0.8f / peak;

        var clip = AudioClip.Create($"Beat_{bpm}bpm", length, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static void Kick(float[] d, int start, int sr, float amp)
    {
        int n = (int)(0.18f * sr);
        double phase = 0;
        for (int i = 0; i < n && start + i < d.Length; i++)
        {
            float t = (float)i / sr;
            float freq = 45f + 70f * Mathf.Exp(-t * 30f);   // pitch drops quickly: "thump"
            phase += 2 * Mathf.PI * freq / sr;
            d[start + i] += amp * Mathf.Exp(-t * 18f) * (float)System.Math.Sin(phase);
        }
    }

    private static void Snare(float[] d, int start, int sr, System.Random rng)
    {
        int n = (int)(0.2f * sr);
        for (int i = 0; i < n && start + i < d.Length; i++)
        {
            float t = (float)i / sr;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            d[start + i] += 0.35f * Mathf.Exp(-t * 22f) * noise
                          + 0.2f * Mathf.Exp(-t * 35f) * Mathf.Sin(2 * Mathf.PI * 185f * t);
        }
    }

    private static void Hat(float[] d, int start, int sr, System.Random rng, float amp)
    {
        int n = (int)(0.05f * sr);
        float prev = 0f;
        for (int i = 0; i < n && start + i < d.Length; i++)
        {
            float t = (float)i / sr;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float bright = noise - prev;                    // crude high-pass: keeps the "tss"
            prev = noise;
            d[start + i] += amp * Mathf.Exp(-t * 90f) * bright;
        }
    }

    private static void Tone(float[] d, int start, int sr, float freq, float dur, float amp, float attack, float release)
    {
        int n = (int)(dur * sr);
        for (int i = 0; i < n && start + i < d.Length; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Min(1f, t / attack) * Mathf.Min(1f, (dur - t) / release);
            float x = 2 * Mathf.PI * freq * t;
            // Sine plus a little 2nd/3rd harmonic for warmth.
            d[start + i] += amp * env * (Mathf.Sin(x) + 0.3f * Mathf.Sin(2 * x) + 0.1f * Mathf.Sin(3 * x));
        }
    }
}
