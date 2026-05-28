namespace MixReady.Helpers;

/// <summary>
/// Implements the various intro-to-song transition effects exposed to the UI.
///
/// All effects produce a single mixed/concatenated buffer where:
///   - the intro ("drumIntro") plays first
///   - the original song joins in at <paramref name="crossfadeStartSample"/>
///
/// The "crossfade region" is the overlap during which both signals are mixed
/// (its length is <paramref name="crossfadeSeconds"/>). For hard-cut style
/// effects ("none", "slam-cut") that length is 0.
/// </summary>
public static class TransitionEffects
{
    public static float[] Apply(
        string transition,
        float[] drumIntro,
        float[] original,
        int sampleRate,
        int channels,
        double bpm,
        double crossfadeSeconds,
        int transitionBars,
        int crossfadeStartSample)
    {
        transition = (transition ?? "crossfade").ToLowerInvariant();

        switch (transition)
        {
            case "none":
                return HardCut(drumIntro, original, sampleRate, channels, crossfadeStartSample);

            case "crossfade":
                return IntroGenerator.CombineWithCrossfadeAtPosition(
                    drumIntro, original, sampleRate, channels, crossfadeSeconds, crossfadeStartSample);

            case "lowpass-sweep":
                return FilterSweep(drumIntro, original, sampleRate, channels,
                                   bpm, crossfadeSeconds, crossfadeStartSample,
                                   lowPass: true);

            case "highpass-sweep":
                return FilterSweep(drumIntro, original, sampleRate, channels,
                                   bpm, crossfadeSeconds, crossfadeStartSample,
                                   lowPass: false);

            case "riser-noise":
                return RiserNoise(drumIntro, original, sampleRate, channels,
                                  bpm, transitionBars, crossfadeSeconds, crossfadeStartSample);

            case "downlifter":
                return Downlifter(drumIntro, original, sampleRate, channels,
                                  bpm, transitionBars, crossfadeSeconds, crossfadeStartSample);

            case "echo-tail":
                return EchoTail(drumIntro, original, sampleRate, channels,
                                bpm, crossfadeSeconds, crossfadeStartSample);

            case "volume-fade":
                return VolumeFade(drumIntro, original, sampleRate, channels,
                                  crossfadeSeconds, crossfadeStartSample);

            case "slam-cut":
                return SlamCut(drumIntro, original, sampleRate, channels, bpm, crossfadeStartSample);

            default:
                return IntroGenerator.CombineWithCrossfadeAtPosition(
                    drumIntro, original, sampleRate, channels, crossfadeSeconds, crossfadeStartSample);
        }
    }

    // -----------------------------------------------------------------
    // "Hard cut" join with a short equal-power micro-crossfade at the seam.
    //
    // A pure sample-to-sample splice almost always thumps because the intro's
    // last sample and the song's first sample rarely meet at the same level.
    // Every pro audio editor adds a 10-50 ms crossfade at edit points; the
    // listener perceives it as a clean cut, but the discontinuity is gone.
    //
    // The micro-crossfade only consumes a few ms of the song's head, so no
    // musical material is lost and the drop still lands on the downbeat.
    // -----------------------------------------------------------------
    private static float[] HardCut(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        int crossfadeStartSample)
    {
        crossfadeStartSample = System.Math.Clamp(crossfadeStartSample, 0, original.Length);
        var tailLen = original.Length - crossfadeStartSample;
        if (tailLen <= 0)
        {
            var simple = new float[drumIntro.Length];
            System.Array.Copy(drumIntro, 0, simple, 0, drumIntro.Length);
            return simple;
        }

        // 25 ms seam crossfade -- inaudible as a fade, but kills the click.
        var seamMs = 25.0;
        var seamSamples = (int)(seamMs / 1000.0 * sampleRate) * channels;
        seamSamples = System.Math.Min(seamSamples, System.Math.Min(drumIntro.Length, tailLen));

        if (seamSamples <= 0)
        {
            var simple = new float[drumIntro.Length + tailLen];
            System.Array.Copy(drumIntro, 0, simple, 0, drumIntro.Length);
            System.Array.Copy(original, crossfadeStartSample, simple, drumIntro.Length, tailLen);
            return simple;
        }

        var nonOverlap = drumIntro.Length - seamSamples;
        var remainingOriginal = tailLen - seamSamples;
        var output = new float[nonOverlap + seamSamples + System.Math.Max(0, remainingOriginal)];

        // Bulk of intro
        if (nonOverlap > 0)
            System.Array.Copy(drumIntro, 0, output, 0, nonOverlap);

        // Equal-power blend over the seam (cosine/sine ~= -3 dB centre, no level dip).
        for (int i = 0; i < seamSamples; i++)
        {
            var t = (float)i / seamSamples;        // 0..1
            var fOut = (float)System.Math.Cos(t * System.Math.PI / 2);
            var fIn  = (float)System.Math.Sin(t * System.Math.PI / 2);
            output[nonOverlap + i] =
                drumIntro[nonOverlap + i] * fOut
              + original[crossfadeStartSample + i] * fIn;
        }

        // Remainder of the song
        if (remainingOriginal > 0)
            System.Array.Copy(
                original, crossfadeStartSample + seamSamples,
                output, nonOverlap + seamSamples,
                remainingOriginal);

        return output;
    }

    // -----------------------------------------------------------------
    // Filter sweep transition: crossfade as usual, but the *intro tail* is
    // gradually filtered out (low-pass closing or high-pass opening) so it
    // dissolves into the song instead of just fading.
    // -----------------------------------------------------------------
    private static float[] FilterSweep(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        double bpm, double crossfadeSeconds, int crossfadeStartSample,
        bool lowPass)
    {
        var crossfadeSamples = (int)(crossfadeSeconds * sampleRate) * channels;
        crossfadeSamples = System.Math.Min(crossfadeSamples, drumIntro.Length);
        if (crossfadeSamples > 0)
        {
            // Extract the tail of the intro and sweep its filter
            var tail = new float[crossfadeSamples];
            System.Array.Copy(drumIntro, drumIntro.Length - crossfadeSamples, tail, 0, crossfadeSamples);

            if (lowPass)
            {
                // Open: starts muffled (bass only) -> opens to full -- usually used
                // when intro is dry; for tail-out we go the *other* way (closing).
                LowPassSweep.Apply(tail, sampleRate, channels,
                    startCutoffHz: 18000, endCutoffHz: 200);
            }
            else
            {
                // High-pass: cutoff rises so only highs remain at the end (Serato "filter up")
                HighPassSweep.Apply(tail, sampleRate, channels,
                    startCutoffHz: 20, endCutoffHz: 6000);
            }

            System.Array.Copy(tail, 0, drumIntro, drumIntro.Length - crossfadeSamples, crossfadeSamples);
        }

        return IntroGenerator.CombineWithCrossfadeAtPosition(
            drumIntro, original, sampleRate, channels, crossfadeSeconds, crossfadeStartSample);
    }

    // -----------------------------------------------------------------
    // White-noise riser: add a swelling pink/white noise burst over the last
    // N bars of the intro, then crossfade as usual into the song.
    // -----------------------------------------------------------------
    private static float[] RiserNoise(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        double bpm, int transitionBars, double crossfadeSeconds, int crossfadeStartSample)
    {
        var secondsPerBar = (60.0 / bpm) * 4;
        var riserSeconds = secondsPerBar * System.Math.Max(1, transitionBars);
        var riserSamples = (int)(riserSeconds * sampleRate) * channels;
        riserSamples = System.Math.Min(riserSamples, drumIntro.Length);
        // Pro rule: FX must start on a bar line (downbeat) so the swell feels musical
        riserSamples = SnapToBarLength(riserSamples, sampleRate, channels, bpm, drumIntro.Length);

        if (riserSamples > 0)
        {
            var rng = new System.Random(0x12345);
            var start = drumIntro.Length - riserSamples;
            var totalFrames = riserSamples / channels;
            // Hard-mute ramp length (~5 ms) so the noise dies right ON the downbeat
            // without clicking. Pros cut the riser dead on beat 1 of the drop.
            var muteFrames = System.Math.Max(1, (int)(0.005 * sampleRate));

            var noise = new float[riserSamples];
            for (int f = 0; f < totalFrames; f++)
            {
                var t = (float)f / totalFrames;        // 0..1
                var amp = t * t * 0.35f;               // exponential swell, cap ~ -9 dB

                // Hard mute over the last few ms so the riser ends exactly on
                // the drop instead of bleeding into the song.
                var framesToEnd = totalFrames - f;
                if (framesToEnd < muteFrames)
                    amp *= (float)framesToEnd / muteFrames;

                var n = (float)(rng.NextDouble() * 2 - 1) * amp;
                for (int c = 0; c < channels; c++)
                    noise[f * channels + c] = n;
            }
            // Sweep high-pass from 200 Hz -> 8 kHz so it brightens toward the drop
            HighPassSweep.Apply(noise, sampleRate, channels, 200, 8000);

            // Mix into intro tail
            for (int i = 0; i < riserSamples; i++)
            {
                var s = drumIntro[start + i] + noise[i];
                drumIntro[start + i] = System.Math.Clamp(s, -1f, 1f);
            }
        }

        // Pro placement: the noise builds during the *last bars of the intro* and
        // the song enters dry on the next sample. A crossfade here would let the
        // riser bleed under the song's first downbeat, which is the amateur sound.
        return HardCut(drumIntro, original, sampleRate, channels, crossfadeStartSample);
    }

    // -----------------------------------------------------------------
    // Down-lifter: reverse the last bar of the intro and overlay it (the
    // classic "reverse cymbal" pre-drop sound). Then crossfade into the song.
    // -----------------------------------------------------------------
    private static float[] Downlifter(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        double bpm, int transitionBars, double crossfadeSeconds, int crossfadeStartSample)
    {
        var secondsPerBar = (60.0 / bpm) * 4;
        var rSeconds = secondsPerBar * System.Math.Max(1, transitionBars);
        var rSamples = (int)(rSeconds * sampleRate) * channels;
        rSamples = System.Math.Min(rSamples, drumIntro.Length);
        rSamples = SnapToBarLength(rSamples, sampleRate, channels, bpm, drumIntro.Length);

        if (rSamples > 0)
        {
            var start = drumIntro.Length - rSamples;
            // Capture and reverse
            var rev = new float[rSamples];
            for (int f = 0; f < rSamples / channels; f++)
            {
                var srcF = rSamples / channels - 1 - f;
                for (int c = 0; c < channels; c++)
                    rev[f * channels + c] = drumIntro[start + srcF * channels + c] * 0.6f;
            }
            // Volume swell to the drop, then short hard-mute so the reverse cymbal
            // peaks ON the downbeat instead of bleeding past it.
            var frames = rSamples / channels;
            var muteFrames = System.Math.Max(1, (int)(0.005 * sampleRate));
            for (int f = 0; f < frames; f++)
            {
                var g = (float)f / frames; // 0..1 ramp
                var framesToEnd = frames - f;
                if (framesToEnd < muteFrames)
                    g *= (float)framesToEnd / muteFrames;
                for (int c = 0; c < channels; c++)
                    rev[f * channels + c] *= g;
            }
            // Mix on top of intro tail
            for (int i = 0; i < rSamples; i++)
            {
                var s = drumIntro[start + i] + rev[i];
                drumIntro[start + i] = System.Math.Clamp(s, -1f, 1f);
            }
        }

        // Pro placement: the reverse cymbal peaks ON the downbeat, and the song
        // enters dry on the next sample (no FX bleed into bar 1 of the drop).
        return HardCut(drumIntro, original, sampleRate, channels, crossfadeStartSample);
    }

    // -----------------------------------------------------------------
    // Echo tail (delay throw): the textbook trick is to trigger 4 dotted-eighth
    // echoes from the *last beat* of the intro -- not from the whole last bar --
    // so the final drum hits ring out across the bar line and decay under the
    // first bars of the song.
    // -----------------------------------------------------------------
    private static float[] EchoTail(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        double bpm, double crossfadeSeconds, int crossfadeStartSample)
    {
        var secondsPerBeat = 60.0 / bpm;
        var delaySeconds = secondsPerBeat * 0.75; // dotted-eighth
        var delaySamples = (int)(delaySeconds * sampleRate) * channels;
        var taps = 4;
        var feedback = 0.55f;

        // Source = the LAST BEAT of the intro (delay-throw style), not the last bar
        var sourceSamples = (int)(secondsPerBeat * sampleRate) * channels;
        sourceSamples = System.Math.Min(sourceSamples, drumIntro.Length);

        if (sourceSamples > 0 && delaySamples > 0)
        {
            var srcStart = drumIntro.Length - sourceSamples;
            var dry = new float[sourceSamples];
            System.Array.Copy(drumIntro, srcStart, dry, 0, sourceSamples);

            // The echoes need to ring past the drop, so extend the intro buffer.
            var echoSpan = delaySamples * taps + sourceSamples;
            var newIntro = new float[drumIntro.Length + delaySamples * taps];
            System.Array.Copy(drumIntro, 0, newIntro, 0, drumIntro.Length);
            for (int t = 1; t <= taps; t++)
            {
                var offset = delaySamples * t;
                var gain = (float)System.Math.Pow(feedback, t);
                for (int i = 0; i < sourceSamples; i++)
                {
                    var dst = srcStart + i + offset;
                    if (dst >= newIntro.Length) break;
                    newIntro[dst] = System.Math.Clamp(newIntro[dst] + dry[i] * gain, -1f, 1f);
                }
            }
            drumIntro = newIntro;
        }

        return IntroGenerator.CombineWithCrossfadeAtPosition(
            drumIntro, original, sampleRate, channels, crossfadeSeconds, crossfadeStartSample);
    }

    // -----------------------------------------------------------------
    // Snap an FX length (in interleaved samples) to a whole number of bars,
    // so the effect always starts on a downbeat.
    // -----------------------------------------------------------------
    private static int SnapToBarLength(int requested, int sampleRate, int channels, double bpm, int max)
    {
        if (requested <= 0) return 0;
        var samplesPerBar = (int)((60.0 / bpm) * 4 * sampleRate) * channels;
        if (samplesPerBar <= 0) return requested;
        var bars = System.Math.Max(1, (int)System.Math.Round((double)requested / samplesPerBar));
        var snapped = bars * samplesPerBar;
        return System.Math.Min(snapped, max);
    }

    // -----------------------------------------------------------------
    // Volume fade: linear fade-out of the intro tail + fade-in of the
    // original song over the crossfade region (simpler than the default
    // equal-power crossfade).
    // -----------------------------------------------------------------
    private static float[] VolumeFade(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        double crossfadeSeconds, int crossfadeStartSample)
    {
        crossfadeStartSample = System.Math.Clamp(crossfadeStartSample, 0, original.Length);
        var xfSamples = (int)(crossfadeSeconds * sampleRate) * channels;
        xfSamples = System.Math.Min(xfSamples, System.Math.Min(drumIntro.Length, original.Length - crossfadeStartSample));

        if (xfSamples <= 0)
            return HardCut(drumIntro, original, sampleRate, channels, crossfadeStartSample);

        var nonOverlap = drumIntro.Length - xfSamples;
        var tail = original.Length - crossfadeStartSample - xfSamples;
        var output = new float[nonOverlap + xfSamples + System.Math.Max(0, tail)];
        if (nonOverlap > 0) System.Array.Copy(drumIntro, 0, output, 0, nonOverlap);

        for (int i = 0; i < xfSamples; i++)
        {
            var t = (float)i / xfSamples;
            output[nonOverlap + i] = drumIntro[nonOverlap + i] * (1 - t)
                                   + original[crossfadeStartSample + i] * t;
        }
        if (tail > 0)
            System.Array.Copy(original, crossfadeStartSample + xfSamples,
                              output, nonOverlap + xfSamples, tail);
        return output;
    }

    // -----------------------------------------------------------------
    // Slam cut: snap the song-entry point to the next downbeat (no overlap).
    // -----------------------------------------------------------------
    private static float[] SlamCut(
        float[] drumIntro, float[] original,
        int sampleRate, int channels,
        double bpm, int crossfadeStartSample)
    {
        // Snap intro length to the next full beat to avoid clicks
        var samplesPerBeat = (int)((60.0 / bpm) * sampleRate) * channels;
        if (samplesPerBeat > 0)
        {
            var snapped = ((drumIntro.Length + samplesPerBeat - 1) / samplesPerBeat) * samplesPerBeat;
            if (snapped > drumIntro.Length)
            {
                var padded = new float[snapped];
                System.Array.Copy(drumIntro, 0, padded, 0, drumIntro.Length);
                drumIntro = padded;
            }
        }
        return HardCut(drumIntro, original, sampleRate, channels, crossfadeStartSample);
    }
}
