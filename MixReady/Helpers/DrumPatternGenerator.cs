
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

                // --- Micro-timing swing for groove ---
                // Snare/clap positions (offbeats: "a" and "&" in dembow) are
                // pushed slightly late (~20ms) to create the laid-back bounce
                // that defines the dembow feel. Without this, the pattern
                // sounds robotic and stiff.
                var swingOffsetSeconds = 0.0;
                var stepInBar = step % 16;
                // Positions 3,6,11,14 are the dembow snare hits ("a" and "&")
                if (stepInBar == 3 || stepInBar == 6 || stepInBar == 11 || stepInBar == 14)
                    swingOffsetSeconds = 0.020; // 20ms late = laid-back feel

                var startSample = (int)((timeSeconds + swingOffsetSeconds) * sampleRate);

                var hits = pattern[step];

                // --- Velocity variation for human feel ---
                // Beat 1 kick (pos 0) = full power, Beat 3 kick (pos 8) = 88%
                // Hats get slight random variation per hit
                var kickVelocity = (stepInBar == 0) ? 1.0f : 0.88f;
                var hatVelocity = 1.0f + (float)((globalStep * 7 % 13) - 6) * 0.01f; // +/- 6%

                // --- Dynamic hierarchy ---
                // Kick DOMINATES, clap sits behind, hats are subtle texture
                if (hits.HasFlag(DrumHit.Kick))
                    WriteKick(output, startSample, sampleRate, channels, intensity * kickVelocity, bassFreq);

                if (hits.HasFlag(DrumHit.Bass808))
                    WriteBass808(output, startSample, sampleRate, channels, bpm, intensity * 0.85f * kickVelocity, bassFreq);

                if (hits.HasFlag(DrumHit.Snare))
                    WriteSnare(output, startSample, sampleRate, channels, intensity * 0.6f);

                if (hits.HasFlag(DrumHit.ClosedHat))
                    WriteHiHat(output, startSample, sampleRate, channels, intensity * 0.18f * hatVelocity);

                if (hits.HasFlag(DrumHit.OpenHat))
                    WriteOpenHat(output, startSample, sampleRate, channels, intensity * 0.12f);

                if (hits.HasFlag(DrumHit.Rim))
                    WriteRimshot(output, startSample, sampleRate, channels, intensity * 0.4f);

                if (hits.HasFlag(DrumHit.Perc))
                    WritePerc(output, startSample, sampleRate, channels, intensity * 0.3f);

                if (hits.HasFlag(DrumHit.AccentSnare))
                    WriteAccentSnare(output, startSample, sampleRate, channels, intensity * 0.5f);
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
        // Authentic dembow: kick on beats 1 and 3 ONLY.
        // Snare/clap on the offbeats: pos 3 (a of 1), 6 (& of 2),
        // 11 (a of 3), 14 (& of 4). These 4 claps ARE the dembow.
        // Hi-hats on every 8th note for the ride.
        // 808 follows the kick (beats 1 and 3).
        //
        //  Pos:  0  1  2  3  4  5  6  7  8  9  10 11 12 13 14 15
        //  Beat: 1  e  &  a  2  e  &  a  3  e  &  a  4  e  &  a
        //  Kick: X  .  .  .  .  .  .  .  X  .  .  .  .  .  .  .
        //  Clap: .  .  .  X  .  .  X  .  .  .  .  X  .  .  X  .
        //  Hat:  X  .  X  .  X  .  X  .  X  .  X  .  X  .  X  .
        //  808:  X  .  .  .  .  .  .  .  X  .  .  .  .  .  .  .
        "Reggaeton" or "Dembow" => new DrumHit[16]
        {
        //  1       e     &     a     2     e     &     a     3       e     &     a     4     e     &     a
            K|H|B,  _,    H,    S,    H,    _,    S,    _,    K|H|B,  _,    H,    S,    H,    _,    S,    _,
        },

        // -- Reparto (clean phase, no 808) --------------------------------
        // Universal Reparto Pattern: controlled, not chaotic.
        //
        // Key features:
        //   - Strong kick anchors (not every beat, not random)
        //   - Double clap accent at beat 3 (the signature)
        //   - Off-beat clap hits for groove
        //   - Consistent energy — loops cleanly
        //   - NOT pure dembow (too clean) and NOT chaotic (too messy)
        //
        //  Pos:  0  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15
        //  Beat: 1  e  &  a  2  e  &  a  3  e  &  a  4  e  &  a
        //  Kick: X  .  .  .  .  X  .  .  .  .  .  X  .  .  .  .
        //  Clap: .  .  .  X  .  .  .  .  X  X  .  .  .  .  X  .
        //  Hat:  X  .  X  .  X  .  X  .  X  .  X  .  X  .  X  .
        //                              ^^ double clap = reparto signature
        "Reparto" => new DrumHit[16]
        {
        //  1       e     &     a     2     e     &     a     3     e     &     a     4     e     &     a
            K|H,    _,    H,    S,    H,    K,    H,    _,    S|H,  S,    H,    K,    H,    _,    S,    _,
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
    /// Reparto bass phase (bars 9-16): same controlled pattern but WITH 808 bass
    /// on the kick anchors. Builds energy heading into the track.
    /// </summary>
    private static DrumHit[] GetRepartoBassPattern() => new DrumHit[16]
    {
    //  1         e     &     a     2       e     &     a     3     e     &       a     4     e     &     a
        K|H|B,    _,    H,    S,    H,      K|B,  H,    _,    S|H,  S,    H,      K|B,  H,    _,    S,    _,
    };

    // -----------------------------------------------------------------
    // Sound Synthesis
    // -----------------------------------------------------------------

    /// <summary>
    /// Reggaeton 808 kick: body-dominant, sub-heavy.
    ///
    ///   The sound is 95% a low sine wave at 60-80 Hz with a soft pitch bend
    ///   at the very start (100 Hz -> bassFreq in ~10ms) for a subtle attack.
    ///   NO high-frequency click/punch layer.
    ///   NO transient spike.
    ///
    ///   Think: BUM (not click, not tick, not chik)
    ///
    ///   - Soft attack: sine starts smoothly, no impulse
    ///   - Strong body: sustained 60-80 Hz for ~150ms
    ///   - Short tail: dies cleanly, doesn't ring into next beat
    ///   - Key-tuned: sine frequency = track's detected bass note
    /// </summary>
    private static void WriteKick(float[] output, int startSample, int sampleRate, int channels, float intensity, double bassFreq)
    {
        var durationSamples = (int)(0.15 * sampleRate);
        var phase = 0.0;

        // Use octave 2 for the kick body so it's audible on all speakers.
        // Octave 1 (32-62 Hz) requires a subwoofer. Octave 2 (65-124 Hz)
        // is where reggaeton kicks actually live in real productions.
        var kickFreq = bassFreq * 2.0; // octave 2: 65-124 Hz

        // Pitch bend: start at kickFreq * 2.5 (~200 Hz) for the "buh" attack,
        // settle to kickFreq in 15ms. This gives audible punch + body.
        var bendStart = kickFreq * 2.5;
        var bendSamples = (int)(0.015 * sampleRate);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;

            double freq;
            if (i < bendSamples)
            {
                var bendT = (double)i / bendSamples;
                // Exponential settle for punchier attack
                freq = bendStart * Math.Pow(kickFreq / bendStart, bendT);
            }
            else
            {
                freq = kickFreq;
            }

            phase += 2.0 * Math.PI * freq / sampleRate;

            // Envelope: 1ms fade-in, strong body, smooth decay
            var attackSamples = (int)(0.001 * sampleRate);
            double envelope;
            if (i < attackSamples)
            {
                envelope = (double)i / attackSamples;
            }
            else
            {
                var decayT = (double)(i - attackSamples) / (durationSamples - attackSamples);
                envelope = Math.Exp(-3.0 * decayT);
            }

            // Higher amplitude — kick must dominate the mix
            var sample = (float)(Math.Sin(phase) * envelope * intensity * 1.3);
            sample = Math.Clamp(sample, -1.0f, 1.0f);
            for (int ch = 0; ch < channels; ch++)
                output[idx + ch] += sample;
        }
    }

    /// <summary>
    /// Electronic clap for dembow: controlled brightness.
    /// Layered noise bursts but NOT harsh — should sit behind the kick, not dominate.
    /// </summary>
    private static void WriteSnare(float[] output, int startSample, int sampleRate, int channels, float intensity)
    {
        var durationSamples = (int)(0.06 * sampleRate); // 60ms — shorter, snappier
        var rng = new Random(startSample);

        // Two micro-bursts for clap texture (not three — less harsh)
        var microDelays = new[] { 0, (int)(0.012 * sampleRate) };

        foreach (var delay in microDelays)
        {
            var layerRng = new Random(startSample + delay * 31);
            for (int i = 0; i < durationSamples; i++)
            {
                var idx = (startSample + delay + i) * channels;
                if (idx >= output.Length - (channels - 1)) break;

                var t = (double)i / durationSamples;

                var noise = (float)(layerRng.NextDouble() * 2.0 - 1.0);

                // Moderate decay — not too harsh, not too soft
                var envelope = (float)Math.Exp(-12.0 * t);

                // NO high-frequency click transient — that was adding cricket sound
                var sample = noise * envelope * intensity * 0.25f;
                for (int ch = 0; ch < channels; ch++)
                    output[idx + ch] += sample;
            }
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
    /// 808 bass with sidechain ducking:
    ///
    ///   - Sustained sub-bass sine at the track's root note
    ///   - First ~50ms is DUCKED (silent) so the kick hits unobstructed
    ///   - Then swells in over ~30ms for a smooth pump effect
    ///   - Tighter envelope than before — doesn't ring forever
    ///
    /// Without sidechain, the 808 and kick collide and the kick loses impact.
    /// This is the #1 reason reggaeton intros sound soft.
    /// </summary>
    private static void WriteBass808(float[] output, int startSample, int sampleRate, int channels, double bpm, float intensity, double bassFreq)
    {
        // Duration = one beat, but tighter decay
        var durationSamples = (int)(60.0 / bpm * sampleRate);
        var phase = 0.0;

        // Sidechain: duck for 50ms, swell in over next 30ms
        var duckSamples = (int)(0.050 * sampleRate);
        var swellSamples = (int)(0.030 * sampleRate);

        for (int i = 0; i < durationSamples; i++)
        {
            var idx = (startSample + i) * channels;
            if (idx >= output.Length - (channels - 1)) break;

            var t = (double)i / durationSamples;
            phase += 2.0 * Math.PI * bassFreq / sampleRate;

            // Tighter decay: -4.0 instead of -3.0
            var envelope = Math.Exp(-4.0 * t);

            // Sidechain ducking: silence during kick transient, then swell
            double sidechain;
            if (i < duckSamples)
                sidechain = 0.0; // completely silent while kick attacks
            else if (i < duckSamples + swellSamples)
                sidechain = (double)(i - duckSamples) / swellSamples; // linear swell 0->1
            else
                sidechain = 1.0;

            var sample = (float)(Math.Sin(phase) * envelope * sidechain * intensity);
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
