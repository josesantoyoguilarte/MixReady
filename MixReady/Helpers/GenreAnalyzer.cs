using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Analyzes audio characteristics to estimate the genre of a track.
///
/// Classification is based on measurable audio features:
/// - BPM range (each genre has a typical tempo range)
/// - Bass weight (ratio of sub-200 Hz energy to total)
/// - Mid presence (ratio of 300--4000 Hz vocal/melody band)
/// - High presence (ratio of 8000+ Hz energy -- hats, air, brightness)
/// - Transient density (how many sharp hits per second -- sparse vs. busy)
/// - Beat regularity (how metronomic the rhythm is)
/// - Dynamic range (difference between loud and quiet moments)
///
/// This is heuristic-based, not ML. It works well for common DJ genres.
/// </summary>
public static class GenreAnalyzer
{
    public static string Analyze(string filePath, double bpm)
    {
        using var reader = new AudioFileReader(AudioConverter.EnsureWav(filePath));
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;

        var samples = ReadAllSamples(reader);
        var mono = ToMono(samples, channels);

        // Measure audio features
        var features = new AudioFeatures
        {
            Bpm = bpm,
            BassWeight = BandRatio(mono, sampleRate, 20, 200),
            MidWeight = BandRatio(mono, sampleRate, 300, 4000),
            HighWeight = BandRatio(mono, sampleRate, 8000, 18000),
            TransientDensity = MeasureTransientDensity(mono, sampleRate),
            BeatRegularity = MeasureBeatRegularity(mono, sampleRate, bpm),
            DynamicRange = MeasureDynamicRange(mono, sampleRate),
            DembowScore = MeasureDembowPattern(mono, sampleRate, bpm),
            ClaveScore = MeasureClavePattern(mono, sampleRate, bpm),
            SteadyPulseScore = MeasureSteadyEighthPulse(mono, sampleRate, bpm),
            CumbiaShuffleScore = MeasureCumbiaShuffle(mono, sampleRate, bpm)
        };

        return Classify(features);
    }

    /// <summary>
    /// Analyze a track and return all genre scores ranked by confidence.
    /// This lets the user see why a genre was picked and override if wrong.
    /// </summary>
    public static GenreAnalysisResult AnalyzeWithScores(string filePath, double bpm)
    {
        using var reader = new AudioFileReader(AudioConverter.EnsureWav(filePath));
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;

        var samples = ReadAllSamples(reader);
        var mono = ToMono(samples, channels);

        var features = new AudioFeatures
        {
            Bpm = bpm,
            BassWeight = BandRatio(mono, sampleRate, 20, 200),
            MidWeight = BandRatio(mono, sampleRate, 300, 4000),
            HighWeight = BandRatio(mono, sampleRate, 8000, 18000),
            TransientDensity = MeasureTransientDensity(mono, sampleRate),
            BeatRegularity = MeasureBeatRegularity(mono, sampleRate, bpm),
            DynamicRange = MeasureDynamicRange(mono, sampleRate),
            DembowScore = MeasureDembowPattern(mono, sampleRate, bpm),
            ClaveScore = MeasureClavePattern(mono, sampleRate, bpm),
            SteadyPulseScore = MeasureSteadyEighthPulse(mono, sampleRate, bpm),
            CumbiaShuffleScore = MeasureCumbiaShuffle(mono, sampleRate, bpm)
        };

        var scores = GetAllScores(features);
        var totalScore = scores.Values.Sum();
        var ranked = scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new GenreScore
            {
                Genre = kv.Key,
                Confidence = totalScore > 0 ? Math.Round(kv.Value / totalScore * 100, 1) : 0
            })
            .ToList();

        return new GenreAnalysisResult
        {
            BestMatch = ranked.First().Genre,
            Scores = ranked
        };
    }

    /// <summary>
    /// Returns the list of supported genre names.
    /// </summary>
    public static IReadOnlyList<string> SupportedGenres => new[]
    {
        "House", "Techno", "Drum & Bass", "Hip-Hop", "Pop", "R&B",
        "Reggaeton", "Reparto", "Trance", "Dubstep", "Disco/Funk", "Salsa",
        "Merengue", "Bachata", "Dembow", "Cumbia", "Vallenato"
    };

    private static string Classify(AudioFeatures f)
    {
        var scores = GetAllScores(f);
        return scores.OrderByDescending(kv => kv.Value).First().Key;
    }

    private static Dictionary<string, double> GetAllScores(AudioFeatures f)
    {
        return new Dictionary<string, double>
        {
            ["House"] = ScoreHouse(f),
            ["Techno"] = ScoreTechno(f),
            ["Drum & Bass"] = ScoreDnB(f),
            ["Hip-Hop"] = ScoreHipHop(f),
            ["Pop"] = ScorePop(f),
            ["R&B"] = ScoreRnB(f),
            ["Reggaeton"] = ScoreReggaeton(f),
            ["Reparto"] = ScoreReparto(f),
            ["Trance"] = ScoreTrance(f),
            ["Dubstep"] = ScoreDubstep(f),
            ["Disco/Funk"] = ScoreDiscoFunk(f),
            ["Salsa"] = ScoreSalsa(f),
            ["Merengue"] = ScoreMerengue(f),
            ["Bachata"] = ScoreBachata(f),
            ["Dembow"] = ScoreDembow(f),
            ["Cumbia"] = ScoreCumbia(f),
            ["Vallenato"] = ScoreVallenato(f)
        };
    }

    public record GenreScore
    {
        public string Genre { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }

    public record GenreAnalysisResult
    {
        public string BestMatch { get; init; } = string.Empty;
        public List<GenreScore> Scores { get; init; } = new();
    }

    // --- Genre scoring functions ---
    // Each returns a 0--100 score based on how well the features match the genre profile.

    private static double ScoreHouse(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 118, 132) * 30;         // House: 118--132 BPM
        s += f.BeatRegularity * 25;                   // Very regular 4-on-the-floor
        s += f.BassWeight * 20;                       // Solid bass
        s += (1 - f.DynamicRange) * 15;               // Consistent energy (not too dynamic)
        s += f.MidWeight * 10;                         // Vocals/chords present
        return s;
    }

    private static double ScoreTechno(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 125, 150) * 25;           // Techno: 125--150 BPM
        s += f.BeatRegularity * 30;                    // Very metronomic
        s += f.BassWeight * 20;                        // Heavy bass
        s += (1 - f.MidWeight) * 15;                   // Minimal vocals/melody
        s += f.TransientDensity * 10;                  // Busy percussion
        return s;
    }

    private static double ScoreDnB(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 160, 180) * 35;           // D&B: 160--180 BPM
        s += f.BassWeight * 25;                        // Heavy bass
        s += f.TransientDensity * 20;                  // Very busy breaks
        s += f.BeatRegularity * 10;                    // Moderate regularity (breakbeats)
        s += f.HighWeight * 10;                        // Bright hats/cymbals
        return s;
    }

    private static double ScoreHipHop(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 75, 110) * 25;            // Hip-Hop: 75--110 BPM (tighter upper bound)
        s += f.BassWeight * 25;                        // 808 bass heavy
        s += f.MidWeight * 15;                         // Vocals present
        s += (1 - f.TransientDensity) * 15;            // Sparse beats
        s += (1 - f.BeatRegularity) * 10;              // Swing/groove, not metronomic
        s += (1 - f.DembowScore) * 10;                 // Strong penalty if dembow detected
        return s;
    }

    private static double ScorePop(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 100, 130) * 25;           // Pop: wide range 100--130
        s += f.MidWeight * 30;                         // Strong vocals/melody
        s += f.DynamicRange * 15;                      // Dynamic (verse/chorus contrast)
        s += (1 - f.BassWeight) * 10;                  // Moderate bass (not dominant)
        s += f.HighWeight * 10;                        // Bright production
        s += (1 - f.TransientDensity) * 10;            // Not too busy
        return s;
    }

    private static double ScoreRnB(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 60, 100) * 30;            // R&B: 60--100 BPM (slow)
        s += f.MidWeight * 25;                         // Strong vocals
        s += f.BassWeight * 15;                        // Warm bass
        s += (1 - f.TransientDensity) * 15;            // Smooth, sparse
        s += f.DynamicRange * 15;                      // Dynamic, expressive
        return s;
    }

    private static double ScoreReggaeton(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 88, 105) * 25;            // Reggaeton: 88--105 BPM
        s += f.DembowScore * 30;                       // Dembow pattern is THE differentiator
        s += f.BassWeight * 20;                        // Heavy bass
        s += f.BeatRegularity * 10;                    // Regular pattern
        s += f.MidWeight * 10;                         // Vocals present
        s += f.TransientDensity * 5;                   // Moderate percussion
        return s;
    }

    /// <summary>
    /// Reparto: ~90--115 BPM, Cuban street genre derived from reggaeton/dembow.
    ///
    /// Key differentiators from reggaeton:
    /// - More broken/irregular rhythm (LOW beat regularity)
    /// - Higher transient density (distorted kicks, loud claps, vocal chops)
    /// - Higher dynamic range (sudden drops, beat switches)
    /// - Still dembow-based but messier
    /// - Heavy distorted bass
    ///
    /// The combination of dembow pattern + LOW regularity + HIGH dynamics
    /// is what separates Reparto from clean reggaeton.
    /// </summary>
    private static double ScoreReparto(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 90, 115) * 20;             // Reparto: 90--115 BPM (wider than reggaeton)
        s += f.DembowScore * 20;                       // Still dembow-based
        s += f.BassWeight * 20;                        // Heavy distorted bass
        s += (1 - f.BeatRegularity) * 15;              // IRREGULAR -- broken beats, not clean loops
        s += f.TransientDensity * 15;                  // Loud claps, distorted kicks, vocal chops
        s += f.DynamicRange * 10;                      // Sudden drops, beat switches
        return s;
    }

    private static double ScoreTrance(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 128, 145) * 25;           // Trance: 128--145 BPM
        s += f.BeatRegularity * 20;                    // Regular beat
        s += f.MidWeight * 20;                         // Melodic content
        s += f.HighWeight * 15;                        // Bright, airy
        s += f.DynamicRange * 15;                      // Builds and drops
        s += (1 - f.BassWeight) * 5;                   // Less bass-heavy than techno
        return s;
    }

    private static double ScoreDubstep(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 138, 142) * 25;           // Dubstep: ~140 BPM (half-time feel)
        s += f.BassWeight * 30;                        // Massive bass
        s += f.DynamicRange * 20;                      // Big drops
        s += (1 - f.BeatRegularity) * 15;              // Irregular, choppy
        s += f.TransientDensity * 10;                  // Busy during drops
        return s;
    }

    private static double ScoreDiscoFunk(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 110, 130) * 25;           // Disco: 110--130 BPM
        s += f.MidWeight * 25;                         // Instruments + vocals
        s += f.BeatRegularity * 15;                    // Regular groove
        s += f.HighWeight * 15;                        // Bright hats/strings
        s += f.DynamicRange * 10;                      // Some dynamics
        s += f.BassWeight * 10;                        // Funky bassline
        return s;
    }

    /// <summary>
    /// Salsa: ~160--220 BPM (or ~80--110 counted in half-time), clave-driven,
    /// bright mids (brass/piano), syncopated, high percussion density.
    /// </summary>
    private static double ScoreSalsa(AudioFeatures f)
    {
        double s = 0;
        // Salsa can be counted fast (160--220) or half-time (80--110)
        var bpmFit = Math.Max(BpmFit(f.Bpm, 160, 220), BpmFit(f.Bpm, 80, 110));
        s += bpmFit * 20;
        s += f.ClaveScore * 25;                        // Clave pattern is THE signature
        s += f.MidWeight * 20;                         // Brass, piano, vocals
        s += f.TransientDensity * 15;                  // Busy percussion (congas, timbales)
        s += f.HighWeight * 10;                        // Bright cymbals/cowbell
        s += (1 - f.DembowScore) * 10;                 // NOT dembow
        return s;
    }

    /// <summary>
    /// Merengue: ~120--160 BPM, extremely steady driving pulse (gÃ¼ira/tambora),
    /// very high transient density, metronomic eighth-note feel.
    /// </summary>
    private static double ScoreMerengue(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 120, 160) * 20;
        s += f.SteadyPulseScore * 25;                  // Steady gÃ¼ira eighth-notes
        s += f.TransientDensity * 20;                  // Very busy percussion
        s += f.BeatRegularity * 15;                    // Extremely regular
        s += f.MidWeight * 10;                         // Accordion/vocals present
        s += f.HighWeight * 10;                        // GÃ¼ira is bright/shimmery
        return s;
    }

    /// <summary>
    /// Bachata: ~125--145 BPM, bongo/gÃ¼ira pattern, strong guitar mids,
    /// moderate dynamics, distinctive syncopated feel.
    /// </summary>
    private static double ScoreBachata(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 125, 145) * 20;
        s += f.MidWeight * 25;                         // Guitar + vocals dominate mids
        s += f.TransientDensity * 15;                  // Bongo pattern
        s += f.DynamicRange * 10;                      // Verse/chorus contrast
        s += (1 - f.BassWeight) * 10;                  // Not bass-heavy (guitar-driven)
        s += f.BeatRegularity * 10;                    // Steady but with syncopation
        s += (1 - f.DembowScore) * 5;                  // Not dembow
        s += (1 - f.SteadyPulseScore) * 5;             // Not as driving as merengue
        return s;
    }

    /// <summary>
    /// Dembow: ~115--140 BPM, derived from dancehall/reggaeton but faster and more
    /// aggressive, very heavy bass, hard-hitting pattern, minimal melody.
    /// Distinct from reggaeton by higher tempo and less vocal melody.
    /// </summary>
    private static double ScoreDembow(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 115, 140) * 20;
        s += f.DembowScore * 20;                       // Dembow rhythm pattern
        s += f.BassWeight * 25;                        // Very heavy bass
        s += (1 - f.MidWeight) * 15;                   // Minimal melody/vocals
        s += f.TransientDensity * 10;                  // Hard percussion
        s += f.BeatRegularity * 10;                    // Regular pattern
        return s;
    }

    /// <summary>
    /// Cumbia: ~80--110 BPM, signature offbeat shuffle (energy on "&" positions),
    /// gÃ¼iro/scraper rhythm, accordion/guitar mids, moderate bass, syncopated.
    /// The key differentiator is the "swing" -- offbeats are louder than downbeats.
    /// </summary>
    private static double ScoreCumbia(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 80, 110) * 20;
        s += f.CumbiaShuffleScore * 25;                // Offbeat shuffle is THE signature
        s += f.MidWeight * 20;                         // Accordion/guitar
        s += f.HighWeight * 10;                        // GÃ¼iro scraper shimmer
        s += f.BassWeight * 10;                        // Moderate bass (tumbadora)
        s += f.TransientDensity * 10;                  // Moderate percussion
        s += (1 - f.DembowScore) * 5;                  // Not dembow
        return s;
    }

    /// <summary>
    /// Vallenato: ~110--150 BPM, accordion-driven (strong mids), caja vallenata drum
    /// with emphasis on beat 4, guacharaca scraper (bright highs), melodic.
    /// Faster than cumbia, less shuffled, more straight-ahead groove.
    /// </summary>
    private static double ScoreVallenato(AudioFeatures f)
    {
        double s = 0;
        s += BpmFit(f.Bpm, 110, 150) * 20;
        s += f.MidWeight * 25;                         // Accordion dominates mids
        s += f.HighWeight * 15;                        // Guacharaca scraper
        s += f.BeatRegularity * 15;                    // More regular than cumbia
        s += (1 - f.BassWeight) * 5;                   // Not bass-heavy
        s += f.TransientDensity * 10;                  // Caja + guacharaca
        s += (1 - f.CumbiaShuffleScore) * 5;           // Less shuffled than cumbia
        s += (1 - f.DembowScore) * 5;                  // Not dembow
        return s;
    }

    /// <summary>
    /// Returns 0--1 based on how well the BPM fits within the expected range.
    /// 1.0 = dead center, tapering off outside the range.
    /// </summary>
    private static double BpmFit(double bpm, double low, double high)
    {
        var center = (low + high) / 2;
        var halfRange = (high - low) / 2;

        if (bpm >= low && bpm <= high)
            return 1.0;

        // Taper off outside the range (gaussian-ish)
        var distance = bpm < low ? low - bpm : bpm - high;
        return Math.Exp(-(distance * distance) / (2 * halfRange * halfRange));
    }

    // --- Audio feature measurement ---

    private static double BandRatio(float[] mono, int sampleRate, double lowHz, double highHz)
    {
        var bandEnergy = FilterAndMeasure(mono, sampleRate, lowHz, highHz);
        var fullEnergy = FilterAndMeasure(mono, sampleRate, 20, 18000);

        return fullEnergy > 1e-12 ? bandEnergy / fullEnergy : 0;
    }

    private static double FilterAndMeasure(float[] mono, int sampleRate, double lowHz, double highHz)
    {
        var hp = ApplyBiquad(mono, sampleRate, lowHz, highPass: true);
        var bp = ApplyBiquad(hp, sampleRate, highHz, highPass: false);

        double energy = 0;
        for (int i = 0; i < bp.Length; i++)
            energy += bp[i] * (double)bp[i];

        return energy / bp.Length;
    }

    /// <summary>
    /// Measures how many sharp transient peaks occur per second.
    /// Returns 0--1 normalized (0 = very sparse, 1 = very busy).
    /// </summary>
    private static double MeasureTransientDensity(float[] mono, int sampleRate)
    {
        var windowSize = sampleRate / 100; // 10ms windows
        var windowCount = mono.Length / windowSize;
        if (windowCount < 10) return 0;

        var energies = new double[windowCount];
        double total = 0;

        for (int w = 0; w < windowCount; w++)
        {
            double e = 0;
            for (int i = 0; i < windowSize; i++)
            {
                var s = mono[w * windowSize + i];
                e += s * s;
            }
            energies[w] = e / windowSize;
            total += energies[w];
        }

        var avg = total / windowCount;
        if (avg < 1e-12) return 0;

        // Count peaks above threshold
        int peaks = 0;
        for (int w = 1; w < windowCount - 1; w++)
        {
            if (energies[w] > avg * 1.5 &&
                energies[w] > energies[w - 1] &&
                energies[w] >= energies[w + 1])
                peaks++;
        }

        var durationSeconds = (double)mono.Length / sampleRate;
        var peaksPerSecond = peaks / durationSeconds;

        // Normalize: 0 peaks/s = 0, 15+ peaks/s = 1
        return Math.Min(peaksPerSecond / 15.0, 1.0);
    }

    /// <summary>
    /// Measures how regularly energy peaks align with the beat grid.
    /// Returns 0--1 (1 = perfectly metronomic).
    /// </summary>
    private static double MeasureBeatRegularity(float[] mono, int sampleRate, double bpm)
    {
        var windowSize = sampleRate / 100;
        var windowCount = mono.Length / windowSize;
        if (windowCount < 10) return 0;

        var energies = new double[windowCount];
        double total = 0;

        for (int w = 0; w < windowCount; w++)
        {
            double e = 0;
            for (int i = 0; i < windowSize; i++)
            {
                var s = mono[w * windowSize + i];
                e += s * s;
            }
            energies[w] = e / windowSize;
            total += energies[w];
        }

        var avg = total / windowCount;
        if (avg < 1e-12) return 0;

        // Find peaks
        var peakPositions = new List<int>();
        for (int w = 1; w < windowCount - 1; w++)
        {
            if (energies[w] > avg * 1.3 &&
                energies[w] > energies[w - 1] &&
                energies[w] >= energies[w + 1])
                peakPositions.Add(w);
        }

        if (peakPositions.Count < 4) return 0;

        var windowsPerBeat = (60.0 / bpm) / (1.0 / 100);
        var tolerance = windowsPerBeat * 0.15;

        int onBeat = 0;
        for (int i = 1; i < peakPositions.Count; i++)
        {
            var interval = peakPositions[i] - peakPositions[i - 1];
            foreach (var r in new[] { 0.5, 1.0, 2.0 })
            {
                if (Math.Abs(interval - windowsPerBeat * r) < tolerance)
                {
                    onBeat++;
                    break;
                }
            }
        }

        return (double)onBeat / (peakPositions.Count - 1);
    }

    /// <summary>
    /// Measures dynamic range: difference between loud and quiet sections.
    /// Returns 0--1 (0 = constant loudness, 1 = extreme variation).
    /// </summary>
    private static double MeasureDynamicRange(float[] mono, int sampleRate)
    {
        // Measure RMS in 1-second windows
        var windowSize = sampleRate;
        var windowCount = mono.Length / windowSize;
        if (windowCount < 2) return 0;

        var rmsValues = new double[windowCount];

        for (int w = 0; w < windowCount; w++)
        {
            double sum = 0;
            for (int i = 0; i < windowSize; i++)
            {
                var s = mono[w * windowSize + i];
                sum += s * s;
            }
            rmsValues[w] = Math.Sqrt(sum / windowSize);
        }

        Array.Sort(rmsValues);

        // Dynamic range = ratio of loud (90th percentile) to quiet (10th percentile)
        var quiet = rmsValues[(int)(windowCount * 0.1)];
        var loud = rmsValues[(int)(windowCount * 0.9)];

        if (quiet < 1e-8) return 1.0; // Silence present = very dynamic

        var ratio = loud / quiet;
        // Normalize: ratio 1 = no dynamics, ratio 10+ = very dynamic
        return Math.Min((ratio - 1.0) / 9.0, 1.0);
    }

    /// <summary>
    /// Detects the dembow rhythm pattern characteristic of reggaeton.
    /// 
    /// The dembow pattern in one bar (4 beats, 8 eighth-notes) is:
    ///   Position:  1   &amp;   2   &amp;   3   &amp;   4   &amp;
    ///   Kick:      X   .   .   .   X   .   X   .
    ///   Snare:     .   .   .   X   .   .   .   X
    /// 
    /// We check if energy peaks consistently land at positions 1, 3, 4 (kick)
    /// and 2&amp;, 4&amp; (snare) within each bar across the track.
    /// Returns 0--1 (1 = strong dembow pattern).
    /// </summary>
    private static double MeasureDembowPattern(float[] mono, int sampleRate, double bpm)
    {
        if (bpm < 60 || bpm > 140) return 0; // Wider range to catch all reggaeton/dembow variants

        var samplesPerBeat = (int)(60.0 / bpm * sampleRate);
        var samplesPerEighth = samplesPerBeat / 2;
        var samplesPerBar = samplesPerBeat * 4;

        // Analyze in 8th-note windows
        var windowSize = samplesPerEighth;
        var barCount = mono.Length / samplesPerBar;

        if (barCount < 4) return 0;

        // Expected dembow energy pattern per bar (8 eighth-notes):
        // [KICK, low, low, SNARE, KICK, low, KICK, SNARE]
        // Indices with high energy: 0, 3, 4, 6, 7  (but 4 and 6 are kicks, 3 and 7 are snares)
        // Indices with low energy: 1, 2, 5
        var hitPositions = new[] { 0, 3, 4, 7 };   // Must have energy
        var gapPositions = new[] { 1, 2, 5 };        // Must be quieter

        int matchingBars = 0;

        // Sample bars evenly across the track
        var step = Math.Max(1, barCount / 30); // Check ~30 bars
        for (int bar = 0; bar < barCount; bar += step)
        {
            var barStart = bar * samplesPerBar;
            var eighthEnergies = new double[8];

            for (int e = 0; e < 8; e++)
            {
                var offset = barStart + e * windowSize;
                double energy = 0;
                for (int i = 0; i < windowSize && offset + i < mono.Length; i++)
                {
                    var s = mono[offset + i];
                    energy += s * s;
                }
                eighthEnergies[e] = energy / windowSize;
            }

            // Average energy across all positions
            var avgEnergy = eighthEnergies.Average();
            if (avgEnergy < 1e-10) continue;

            // Check: hit positions should be above average, gap positions below
            var hitsAbove = hitPositions.Count(p => eighthEnergies[p] > avgEnergy * 1.1);
            var gapsBelow = gapPositions.Count(p => eighthEnergies[p] < avgEnergy * 0.9);

            // Need at least 3/4 hits and 2/3 gaps to count as a dembow bar
            if (hitsAbove >= 3 && gapsBelow >= 2)
                matchingBars++;
        }

        var barsChecked = (barCount + step - 1) / step;
        return barsChecked > 0 ? (double)matchingBars / barsChecked : 0;
    }

    /// <summary>
    /// Detects the son clave rhythm pattern characteristic of salsa.
    /// 
    /// The 3-2 son clave over 2 bars (16 eighth-notes) is:
    ///   Position:  1 . . 2 . . 3 . | 1 . 2 . 3 . . .
    ///   Hit:       X . . X . . X . | . . X . X . . .
    ///   Indices:   0     3     6       10    12
    /// 
    /// The 2-3 son clave is the reverse (bar 2 first, bar 1 second).
    /// We check both and take the better match.
    /// Returns 0--1 (1 = strong clave pattern).
    /// </summary>
    private static double MeasureClavePattern(float[] mono, int sampleRate, double bpm)
    {
        if (bpm < 60 || bpm > 240) return 0;

        var samplesPerBeat = (int)(60.0 / bpm * sampleRate);
        var samplesPerEighth = samplesPerBeat / 2;
        var samplesPerTwoBars = samplesPerBeat * 8; // 2 bars = 8 beats

        var windowSize = samplesPerEighth;
        var twoBarCount = mono.Length / samplesPerTwoBars;

        if (twoBarCount < 3) return 0;

        // 3-2 clave hit positions (16 eighth-notes across 2 bars)
        var clave32hits = new[] { 0, 3, 6, 10, 12 };
        var clave32gaps = new[] { 1, 2, 4, 5, 7, 8, 9, 11, 13, 14, 15 };

        // 2-3 clave (reversed)
        var clave23hits = new[] { 2, 4, 8, 11, 14 };
        var clave23gaps = new[] { 0, 1, 3, 5, 6, 7, 9, 10, 12, 13, 15 };

        double best32 = 0, best23 = 0;
        int checked32 = 0, checked23 = 0;

        var step = Math.Max(1, twoBarCount / 20);
        for (int tb = 0; tb < twoBarCount; tb += step)
        {
            var tbStart = tb * samplesPerTwoBars;
            var eighthEnergies = new double[16];

            for (int e = 0; e < 16; e++)
            {
                var offset = tbStart + e * windowSize;
                double energy = 0;
                for (int i = 0; i < windowSize && offset + i < mono.Length; i++)
                {
                    var s = mono[offset + i];
                    energy += s * s;
                }
                eighthEnergies[e] = energy / windowSize;
            }

            var avg = eighthEnergies.Average();
            if (avg < 1e-10) continue;

            // Score 3-2
            var hits32 = clave32hits.Count(p => eighthEnergies[p] > avg * 1.1);
            var gaps32 = clave32gaps.Count(p => eighthEnergies[p] < avg * 1.0);
            if (hits32 >= 4 && gaps32 >= 7) best32++;
            checked32++;

            // Score 2-3
            var hits23 = clave23hits.Count(p => eighthEnergies[p] > avg * 1.1);
            var gaps23 = clave23gaps.Count(p => eighthEnergies[p] < avg * 1.0);
            if (hits23 >= 4 && gaps23 >= 7) best23++;
            checked23++;
        }

        var score32 = checked32 > 0 ? best32 / checked32 : 0;
        var score23 = checked23 > 0 ? best23 / checked23 : 0;

        return Math.Max(score32, score23);
    }

    /// <summary>
    /// Detects a steady eighth-note pulse characteristic of merengue.
    /// Merengue has a constant gÃ¼ira/tambora pattern where almost every
    /// eighth-note position has energy, creating a driving, relentless feel.
    /// Returns 0--1 (1 = every eighth-note has a consistent hit).
    /// </summary>
    private static double MeasureSteadyEighthPulse(float[] mono, int sampleRate, double bpm)
    {
        if (bpm < 100 || bpm > 180) return 0;

        var samplesPerBeat = (int)(60.0 / bpm * sampleRate);
        var samplesPerEighth = samplesPerBeat / 2;
        var samplesPerBar = samplesPerBeat * 4;

        var windowSize = samplesPerEighth;
        var barCount = mono.Length / samplesPerBar;

        if (barCount < 4) return 0;

        int steadyBars = 0;
        var step = Math.Max(1, barCount / 30);

        for (int bar = 0; bar < barCount; bar += step)
        {
            var barStart = bar * samplesPerBar;
            var eighthEnergies = new double[8];

            for (int e = 0; e < 8; e++)
            {
                var offset = barStart + e * windowSize;
                double energy = 0;
                for (int i = 0; i < windowSize && offset + i < mono.Length; i++)
                {
                    var s = mono[offset + i];
                    energy += s * s;
                }
                eighthEnergies[e] = energy / windowSize;
            }

            var avg = eighthEnergies.Average();
            if (avg < 1e-10) continue;

            // In merengue, ALL eighth-note positions should have significant energy.
            // Count how many are above 70% of the average (allowing some variation)
            var activeCount = eighthEnergies.Count(e => e > avg * 0.7);

            // Also check that the variation between positions is low (steady, not syncopated)
            var maxE = eighthEnergies.Max();
            var minE = eighthEnergies.Min();
            var evenness = minE / (maxE + 1e-12);

            // Steady bar = at least 7/8 positions active AND low variation
            if (activeCount >= 7 && evenness > 0.3)
                steadyBars++;
        }

        var barsChecked = (barCount + step - 1) / step;
        return barsChecked > 0 ? (double)steadyBars / barsChecked : 0;
    }

    /// <summary>
    /// Detects the cumbia shuffle pattern -- offbeat emphasis.
    /// 
    /// In cumbia, the "&" (offbeat) eighth-note positions carry more energy
    /// than the downbeat positions, creating the characteristic swing/shuffle.
    /// 
    /// Per bar (8 eighth-notes):
    ///   Position:  1   &   2   &   3   &   4   &
    ///   Index:     0   1   2   3   4   5   6   7
    ///   Downbeats: 0, 2, 4, 6
    ///   Offbeats:  1, 3, 5, 7
    /// 
    /// Cumbia has offbeat energy ? downbeat energy.
    /// Returns 0--1 (1 = strong offbeat-dominant shuffle).
    /// </summary>
    private static double MeasureCumbiaShuffle(float[] mono, int sampleRate, double bpm)
    {
        if (bpm < 60 || bpm > 130) return 0;

        var samplesPerBeat = (int)(60.0 / bpm * sampleRate);
        var samplesPerEighth = samplesPerBeat / 2;
        var samplesPerBar = samplesPerBeat * 4;

        var windowSize = samplesPerEighth;
        var barCount = mono.Length / samplesPerBar;

        if (barCount < 4) return 0;

        var downbeatPositions = new[] { 0, 2, 4, 6 };
        var offbeatPositions = new[] { 1, 3, 5, 7 };

        int shuffleBars = 0;
        var step = Math.Max(1, barCount / 30);

        for (int bar = 0; bar < barCount; bar += step)
        {
            var barStart = bar * samplesPerBar;
            var eighthEnergies = new double[8];

            for (int e = 0; e < 8; e++)
            {
                var offset = barStart + e * windowSize;
                double energy = 0;
                for (int i = 0; i < windowSize && offset + i < mono.Length; i++)
                {
                    var s = mono[offset + i];
                    energy += s * s;
                }
                eighthEnergies[e] = energy / windowSize;
            }

            var avg = eighthEnergies.Average();
            if (avg < 1e-10) continue;

            // Sum energy on downbeats vs offbeats
            var downEnergy = downbeatPositions.Sum(p => eighthEnergies[p]);
            var offEnergy = offbeatPositions.Sum(p => eighthEnergies[p]);

            // Cumbia shuffle: offbeats should have at least 90% of downbeat energy
            // (in strong cumbia, offbeats are actually louder)
            if (offEnergy > downEnergy * 0.9)
            {
                // Also check that beats 2 and 4 (positions 2, 6) or their offbeats
                // (positions 3, 7) are emphasized -- the "backbeat" feel
                var backbeatEnergy = eighthEnergies[3] + eighthEnergies[7];
                var frontbeatEnergy = eighthEnergies[0] + eighthEnergies[4];

                if (backbeatEnergy > frontbeatEnergy * 0.8)
                    shuffleBars++;
            }
        }

        var barsChecked = (barCount + step - 1) / step;
        return barsChecked > 0 ? (double)shuffleBars / barsChecked : 0;
    }

    // --- Helpers ---

    private static float[] ReadAllSamples(AudioFileReader reader)
    {
        var sampleCount = (int)(reader.Length / (reader.WaveFormat.BitsPerSample / 8));
        var buffer = new float[sampleCount];
        var totalRead = 0;
        int read;

        while ((read = reader.Read(buffer, totalRead, Math.Min(4096, buffer.Length - totalRead))) > 0)
        {
            totalRead += read;
            if (totalRead >= buffer.Length) break;
        }

        return buffer[..totalRead];
    }

    private static float[] ToMono(float[] samples, int channels)
    {
        if (channels == 1) return samples;

        var mono = new float[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += samples[i * channels + ch];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private static float[] ApplyBiquad(float[] input, int sampleRate, double freqHz, bool highPass)
    {
        var output = new float[input.Length];
        double w0 = 2.0 * Math.PI * freqHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * 0.707);
        double cosW0 = Math.Cos(w0);
        double norm = 1.0 + alpha;

        double a0, a1, a2;
        if (highPass)
        {
            a0 = ((1.0 + cosW0) / 2.0) / norm;
            a1 = (-(1.0 + cosW0)) / norm;
            a2 = ((1.0 + cosW0) / 2.0) / norm;
        }
        else
        {
            a0 = ((1.0 - cosW0) / 2.0) / norm;
            a1 = (1.0 - cosW0) / norm;
            a2 = ((1.0 - cosW0) / 2.0) / norm;
        }

        double b1 = (-2.0 * cosW0) / norm;
        double b2 = (1.0 - alpha) / norm;

        double x1d = 0, x2d = 0, y1d = 0, y2d = 0;
        for (int i = 0; i < input.Length; i++)
        {
            double x0 = input[i];
            double y0 = a0 * x0 + a1 * x1d + a2 * x2d - b1 * y1d - b2 * y2d;
            output[i] = (float)y0;
            x2d = x1d; x1d = x0;
            y2d = y1d; y1d = y0;
        }

        return output;
    }

    private record AudioFeatures
    {
        public double Bpm { get; init; }
        public double BassWeight { get; init; }
        public double MidWeight { get; init; }
        public double HighWeight { get; init; }
        public double TransientDensity { get; init; }
        public double BeatRegularity { get; init; }
        public double DynamicRange { get; init; }
        public double DembowScore { get; init; }
        public double ClaveScore { get; init; }
        public double SteadyPulseScore { get; init; }
        public double CumbiaShuffleScore { get; init; }
    }
}
