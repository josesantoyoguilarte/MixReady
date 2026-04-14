
/// <summary>
/// Synthesizes genre-specific drum patterns for DJ intros.
/// 
/// Uses 16th-note resolution (16 positions per bar) to accurately represent
/// syncopated rhythms like dembow, cumbia shuffle, and clave patterns.
/// This is essential because genres like reggaeton place hits BETWEEN
/// the 8th-note grid — that's what creates the bounce.
///
/// All sounds are synthesized from scratch — no samples, no extraction, no noise.
/// </summary>
public static class DrumPatternGenerator
{
    private const int StepsPerBar = 16; // 16th-note resolution

    /// <summary>
    /// Generate a genre-appropriate drum pattern at the given BPM.
    /// </summary>
    /// <param name="bpm">Tempo in beats per minute.</param>
    /// <param name="bars">Number of bars to generate.</param>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="genre">Genre name for pattern selection.</param>
    /// <param name="bassFreq">
    /// Root bass frequency in Hz (sub-bass octave, ~32-62 Hz). Detected from
    /// the original track's key so the kick and 808 are in tune with the song.
    /// Defaults to 45 Hz (between F#1 and G1) if not provided.
    /// </param>
    /// <param name="intensity">Overall volume intensity 0-1.</param>
    public static float[] Generate(double bpm, int bars, int sampleRate, int channels, string genre, double bassFreq = 45.0, float intensity = 0.85f)
    {
        var secondsPerBeat = 60.0 / bpm;
        var secondsPerSixteenth = secondsPerBeat / 4.0;
        var totalSteps = bars * StepsPerBar;
        var totalDuration = totalSteps * secondsPerSixteenth;
        var totalSamples = (int)(totalDuration * sampleRate) * channels;

        var output = new float[totalSamples];

        // Reparto uses a two-phase structure:
        //   Bars 1-N/2: clean dembow (no bass) for DJ mix-in
        //   Bars N/2+1-N: add 808 bass to build energy into the track
        // All other genres use a single pattern for all bars.
        var isReparto = genre.Trim().Equals("Reparto", StringComparison.OrdinalIgnoreCase);
        var cleanPattern = GetPattern(genre);
        var bassPattern = isReparto ? GetRepartoBassPattern() : cleanPattern;
        var halfBars = bars / 2;

        for (int bar = 0; bar < bars; bar++)
        {
            // Reparto: first half = clean, second half = with bass
            var pattern = isReparto && bar >= halfBars ? bassPattern : cleanPattern;

            for (int step = 0; step < StepsPerBar; step++)
            {
                var globalStep = bar * StepsPerBar + step;
                var timeSeconds = globalStep * secondsPerSixteenth;
                var startSample = (int)(timeSeconds * sampleRate);

                var hits = pattern[step];

                if (hits.HasFlag(DrumHit.Kick))
                    WriteKick(output, startSample, sampleRate, channels, intensity, bassFreq);

                if (hits.HasFlag(DrumHit.Bass808))
                    WriteBass808(output, startSample, sampleRate, channels, bpm, intensity * 0.8f, bassFreq);

                if (hits.HasFlag(DrumHit.Snare))
                    WriteSnare(output, startSample, sampleRate, channels, intensity * 0.75f);

                if (hits.HasFlag(DrumHit.ClosedHat))
                    WriteHiHat(output, startSample, sampleRate, channels, intensity * 0.2f);

                if (hits.HasFlag(DrumHit.OpenHat))
                    WriteOpenHat(output, startSample, sampleRate, channels, intensity * 0.25f);

                if (hits.HasFlag(DrumHit.Rim))
                    WriteRimshot(output, startSample, sampleRate, channels, intensity * 0.4f);

                if (hits.HasFlag(DrumHit.Perc))
                    WritePerc(output, startSample, sampleRate, channels, intensity * 0.3f);

                if (hits.HasFlag(DrumHit.AccentSnare))
                    WriteAccentSnare(output, startSample, sampleRate, channels, intensity * 0.9f);
            }
        }

        return output;
    }

    // -----------------------------------------------------------------
    // Genre-specific patterns (16 sixteenth-notes per bar)
    //
    //  Beat:     1           2           3           4
    //  Sub:      1  e  &  a  2  e  &  a  3  e  &  a  4  e  &  a
    //  Index:    0  1  2  3  4  5  6  7  8  9  10 11 12 13 14 15
    // -----------------------------------------------------------------

    [Flags]
    private enum DrumHit
    {
        None = 0,
        Kick = 1,
        Snare = 2,
        ClosedHat = 4,
        OpenHat = 8,
        Rim = 16,
        Perc = 32,
        AccentSnare = 64,
        Bass808 = 128
    }

    // Shorthand aliases
    private const DrumHit K = DrumHit.Kick;
    private const DrumHit S = DrumHit.Snare;
    private const DrumHit H = DrumHit.ClosedHat;
    private const DrumHit O = DrumHit.OpenHat;
    private const DrumHit R = DrumHit.Rim;
    private const DrumHit P = DrumHit.Perc;
    private const DrumHit A = DrumHit.AccentSnare;
    private const DrumHit B = DrumHit.Bass808;
    private const DrumHit _ = DrumHit.None;

    private static DrumHit[] GetPattern(string genre) => genre.Trim() switch
    {
        // -- Reggaeton / Dembow -------------------------------------------
        //  Kick on every beat (0,4,8,12), snare dembow bounce, 808 follows kick
        "Reggaeton" or "Dembow" => new DrumHit[16]
        {
        //  1         e    &      a    2      e    &    a    3         e    &      a    4      e    &      a
            K|H|B,    _,   A|H,   S,   K|B,   H,   S,   _,  K|H|B,    _,   A|H,   _,  K|B,   S|H, _,     S|H,
        },

        // -- Reparto (clean phase, no 808) --------------------------------
        // Broken dembow -- irregular, aggressive. NO bass in clean phase
        // so DJs get a stable rhythmic bed to mix into.
        //
        //  Kick:    X . . .  . . X .  X . . .  . X . .   (broken, NOT every beat)
        //  Snare:   . . . X  . . . .  . . . X  . . . X
        //  AccSnr:  . . X .  . X . .  . . X .  . . X .   (busier ghost hits)
        //  HiHat:   X . X .  X . . X  X . X .  X . . X
        "Reparto" => new DrumHit[16]
        {
        //  1       e     &      a     2      e     &      a     3       e     &      a     4      e     &      a
            K|H,    _,    A|H,   S,    H,     A,    K,     H,    K|H,    _,    A|H,   S,    H,     K,    A,     S|H,
        },

        // -- House / Disco/Funk -------------------------------------------
        // Four-on-the-floor, offbeat open hats, snare on 2 and 4
        "House" or "Disco/Funk" => new DrumHit[16]
        {
            K|H, _,   O,   _,  K|S|H, _, O,  _,   K|H, _,   O,   _,  K|S|H, _, O,  _,
        },

        // -- Techno -------------------------------------------------------
        // Driving kick every beat, hats on 8ths
        "Techno" => new DrumHit[16]
        {
            K|H, _,   H,   _,   K|H, _,   H,   _,   K|H, _,   H,   _,   K|H, _,   H,   _,
        },

        // -- Hip-Hop ------------------------------------------------------
        // Boom-bap: kick 1, ghost kick "a of 2" (pos 7), kick 3, snare 2 & 4
        "Hip-Hop" => new DrumHit[16]
        {
            K|H, _,   H,   _,  S|H, _,   H,   K,   K|H, _,   H,   _,  S|H, _,   H,   _,
        },

        // -- Salsa ---------------------------------------------------------
        // 3-2 Son clave (16th resolution) + conga tumbao
        "Salsa" => new DrumHit[16]
        {
            K|R, _,   P,   R,   P,   _,   R,   _,   _,   _,   R,   _,   K|R, _,   P,   _,
        },

        // -- Merengue -----------------------------------------------------
        // Driving tambora + guira on 8ths
        "Merengue" => new DrumHit[16]
        {
            K|H, _,  K|H, _,  S|H, _,  K|H, _,  K|H, _,  K|H, _,  S|H, _,  K|H, _,
        },

        // -- Bachata ------------------------------------------------------
        // Bongo + guira, signature bass on "& of 4"
        "Bachata" => new DrumHit[16]
        {
            K|R|H, _, H,  _,  R|H, _,  H,   _,  K|R|H, _, H,  _,  R|H, _,  S|H, _,
        },

        // -- Cumbia --------------------------------------------------------
        // Offbeat shuffle: perc accents on "e" positions create the swing
        "Cumbia" => new DrumHit[16]
        {
            K|H, P,   H,   _,   H,  P,   H,   _,  K|H, P,   H,   _,   H,  P,  S|H, _,
        },

        // -- Vallenato ----------------------------------------------------
        // Caja + guacharaca, straighter than cumbia
        "Vallenato" => new DrumHit[16]
        {
            K|H, R,   P|H, _,   R|H, _,   P|H, _,  K|H, R,   P|H, _,   R|H, _,  S|R|H, _,
        },

        // -- Drum & Bass --------------------------------------------------
        // Fast breakbeat: kick 1, ghost kick "a of 2" (pos 7), snare 3
        "Drum & Bass" => new DrumHit[16]
        {
            K|H, _,   H,   _,   H,   _,   H,   K,  S|H, _,   H,   _,   H,   _,   H,   _,
        },

        // -- Trance -------------------------------------------------------
        // Euphoric four-on-the-floor, open hats on offbeats
        "Trance" => new DrumHit[16]
        {
            K|H, _,   O,   _,   K|H, _,   O,   _,   K|H, _,   O,   _,   K|H, _,   O,   _,
        },

        // -- Dubstep ------------------------------------------------------
        // Half-time: kick 1, snare 3
        "Dubstep" => new DrumHit[16]
        {
            K|H, _,   H,   _,   H,   _,   H,   _,  S|H, _,   H,   _,   H,   _,   H,   _,
        },

        // -- Pop / R&B / default ------------------------------------------
        _ => new DrumHit[16]
        {
            K|H, _,   H,   _,  S|H, _,   H,   _,  K|H, _,   H,   _,  S|H, _,   H,   _,
        },
    };

    /// <summary>
    /// Reparto bass phase (bars 9-16): same broken dembow but WITH 808 bass
    /// on the kick hits. This builds energy heading into the actual track.
    /// </summary>
    private static DrumHit[] GetRepartoBassPattern() => new DrumHit[16]
    {
    //  1         e     &      a     2      e     &      a     3         e     &      a     4      e     &      a
        K|H|B,    _,    A|H,   S,    H,     A,    K|B,   H,    K|H|B,    _,    A|H,   S,    H,     K|B,  A,     S|H,
    };

    // -----------------------------------------------------------------
    // Sound Synthesis
    // -----------------------------------------------------------------

    /// <summary>
    /// 808-style kick: sine sweep from ~3.5x the bass note down to the root.
    /// The sweep lands on the track's detected key so the kick is in tune.
    /// Includes an initial click transient for punch.
    /// </summary>
    private static void WriteKick(float[] output, int startSample, int sampleRate, int channels, float intensity, double bassFreq)
    {
        var durationSamples = (int)(0.12 * sampleRate);
        var phase = 0.0;
        var startFreq = bassFreq * 3.5;

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var freq = startFreq * Math.Pow(bassFreq / startFreq, t);
            phase += 2.0 * Math.PI * freq / sampleRate;

            var envelope = Math.Exp(-4.5 * t);
            var click = i < (int)(0.003 * sampleRate)
                ? (1.0 - (double)i / (0.003 * sampleRate)) * 0.3 : 0.0;

            var sample = (float)((Math.Sin(phase) * envelope + click) * intensity);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Snare/clap: noise burst (snap) + 200 Hz sine body (thump).
    /// </summary>
    private static void WriteSnare(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.10 * sampleRate);
        var rng = new Random(startSample);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var noise = (float)(rng.NextDouble() * 2.0 - 1.0) * (float)Math.Exp(-8.0 * t);
            var body = (float)(Math.Sin(2.0 * Math.PI * 200.0 * i / sampleRate) * Math.Exp(-12.0 * t));

            var sample = (noise * 0.6f + body * 0.4f) * intensity;
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Closed hi-hat: very short noise burst (~30ms).
    /// </summary>
    private static void WriteHiHat(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.03 * sampleRate);
        var rng = new Random(startSample + 7919);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var sample = (float)((rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-15.0 * t) * intensity);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Open hi-hat: longer noise burst (~80ms) with slower decay.
    /// </summary>
    private static void WriteOpenHat(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.08 * sampleRate);
        var rng = new Random(startSample + 3571);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var sample = (float)((rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-6.0 * t) * intensity);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Rimshot/clave: short tonal click at ~800 Hz.
    /// </summary>
    private static void WriteRimshot(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.02 * sampleRate);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var sample = (float)(Math.Sin(2.0 * Math.PI * 800.0 * i / sampleRate) * Math.Exp(-20.0 * t) * intensity);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Accent snare: brighter, louder snare hit with more high-frequency content.
    /// Used for ghost notes and accent hits in dembow patterns.
    /// </summary>
    private static void WriteAccentSnare(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.08 * sampleRate);
        var rng = new Random(startSample + 2341);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var noise = (float)(rng.NextDouble() * 2.0 - 1.0) * (float)Math.Exp(-10.0 * t);
            var body = (float)(Math.Sin(2.0 * Math.PI * 250.0 * i / sampleRate) * Math.Exp(-15.0 * t));

            var sample = (noise * 0.75f + body * 0.25f) * intensity;
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// 808 bass: sustained sub-bass sine at the track's root note.
    /// Tuned to the detected key so it harmonizes with the original song's bass.
    /// </summary>
    private static void WriteBass808(float[] output, int startSample, int sampleRate, int channels, double bpm, float intensity, double bassFreq)
    {
        var durationSamples = (int)(60.0 / bpm * sampleRate);
        var phase = 0.0;

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            phase += 2.0 * Math.PI * bassFreq / sampleRate;
            var envelope = Math.Exp(-3.0 * t);

            var sample = (float)(Math.Sin(phase) * envelope * intensity);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Percussion hit (conga/tumbadora): mid-frequency tone ~350 Hz.
    /// </summary>
    private static void WritePerc(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.06 * sampleRate);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            var sample = (float)(Math.Sin(2.0 * Math.PI * 350.0 * i / sampleRate) * Math.Exp(-10.0 * t) * intensity);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }
}
