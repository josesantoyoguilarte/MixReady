"""
MixReady Audio Analyzer
=======================

Analyzes a track and outputs a JSON analysis to stdout containing:
- bpm (float)
- key (string, e.g. "E")
- bass_frequency (float, sub-bass Hz for kick/808 tuning)
- genre (string, best guess)
- beats (list of float, beat times in seconds)
- downbeats (list of float, beat-1-of-bar times in seconds)
- bars (list of [start, end] pairs in seconds)
- first_vocal_entry (float, seconds)
- first_strong_rhythm (float, seconds)
- energy_curve (list of float, normalized 0-1 per bar)
- recommended_crossfade_start (float, seconds)

Dependencies:
    pip install librosa madmom soundfile numpy

Usage:
    python analyze.py "<audio_file_path>"
    python analyze.py "<audio_file_path>" --genre "Reggaeton"
"""

import sys
import json
import warnings
import argparse

import numpy as np
import librosa

warnings.filterwarnings("ignore")

# ---------------------------------------------------------------
# Genre BPM ranges (for octave correction when genre is provided)
# ---------------------------------------------------------------
GENRE_BPM_RANGES = {
    "reggaeton": (85, 105),
    "reparto": (90, 115),
    "dembow": (115, 140),
    "house": (118, 132),
    "techno": (125, 150),
    "drum & bass": (160, 180),
    "hip-hop": (75, 110),
    "pop": (100, 130),
    "r&b": (60, 100),
    "trance": (128, 145),
    "dubstep": (135, 145),
    "disco/funk": (110, 130),
    "salsa": (80, 110),
    "merengue": (120, 160),
    "bachata": (125, 145),
    "cumbia": (80, 110),
    "vallenato": (110, 150),
}

# ---------------------------------------------------------------
# BPM Detection
# ---------------------------------------------------------------

def detect_bpm(y, sr, genre=None):
    """Detect BPM using librosa with genre-aware octave correction."""
    tempo, _ = librosa.beat.beat_track(y=y, sr=sr)

    # librosa >= 0.10 returns an array
    if hasattr(tempo, '__len__'):
        tempo = float(tempo[0])
    else:
        tempo = float(tempo)

    # Genre-aware octave correction
    if genre and genre.lower() in GENRE_BPM_RANGES:
        low, high = GENRE_BPM_RANGES[genre.lower()]
        candidates = [tempo * 0.5, tempo * 2/3, tempo, tempo * 3/2, tempo * 2.0]
        in_range = [c for c in candidates if low <= c <= high]
        if in_range:
            # Pick the one closest to the middle of the range
            mid = (low + high) / 2
            tempo = min(in_range, key=lambda c: abs(c - mid))

    return round(tempo, 3)  # Full precision — rounding causes cumulative drift


# ---------------------------------------------------------------
# Key Detection
# ---------------------------------------------------------------

# Note frequencies in octave 1 (sub-bass, for kick/808 tuning)
PITCH_CLASSES = [
    ("C",  32.703),
    ("C#", 34.648),
    ("D",  36.708),
    ("D#", 38.891),
    ("E",  41.203),
    ("F",  43.654),
    ("F#", 46.249),
    ("G",  48.999),
    ("G#", 51.913),
    ("A",  55.000),
    ("A#", 58.270),
    ("B",  61.735),
]

def detect_key(y, sr):
    """Detect musical key using chroma features."""
    chroma = librosa.feature.chroma_cqt(y=y, sr=sr)
    chroma_mean = chroma.mean(axis=1)

    best_pc = int(np.argmax(chroma_mean))
    note_name = PITCH_CLASSES[best_pc][0]
    bass_freq = PITCH_CLASSES[best_pc][1]

    return note_name, bass_freq


# ---------------------------------------------------------------
# Beat & Downbeat Tracking
# ---------------------------------------------------------------

def detect_beats_and_downbeats(y, sr, bpm):
    """
    Detect beat positions and downbeats.

    Uses librosa's beat tracker for beat positions, then determines
    downbeats by analyzing which beat phase has the strongest kick
    energy (60-200 Hz band).
    """
    _, beat_frames = librosa.beat.beat_track(y=y, sr=sr, bpm=bpm)
    beat_times = librosa.frames_to_time(beat_frames, sr=sr).tolist()

    if len(beat_times) < 4:
        return beat_times, beat_times, []

    # Determine downbeat phase by scoring 4 rotations
    # Isolate kick/bass band for downbeat detection
    y_bass = librosa.effects.preemphasis(y, coef=0.0)  # keep all
    # Simple low-pass via resampling trick: downsample to 400Hz captures 0-200Hz
    y_low = librosa.resample(y, orig_sr=sr, target_sr=400)
    sr_low = 400

    best_rotation = 0
    best_score = -np.inf

    for rotation in range(4):
        score = 0.0
        count = 0
        for i in range(rotation, len(beat_times), 4):
            # Get onset strength at this beat time in the bass band
            sample_idx = int(beat_times[i] * sr_low)
            window = 20  # ~50ms at 400Hz
            start = max(0, sample_idx - window // 2)
            end = min(len(y_low), sample_idx + window // 2)
            if end > start:
                score += float(np.sum(y_low[start:end] ** 2))
                count += 1

        if count > 0:
            score /= count

        if score > best_score:
            best_score = score
            best_rotation = rotation

    # Extract downbeats
    downbeat_times = [beat_times[i] for i in range(best_rotation, len(beat_times), 4)]

    # Build bar boundaries
    bars = []
    for i in range(len(downbeat_times)):
        start = downbeat_times[i]
        end = downbeat_times[i + 1] if i + 1 < len(downbeat_times) else beat_times[-1]
        bars.append([round(start, 4), round(end, 4)])

    return (
        [round(t, 4) for t in beat_times],
        [round(t, 4) for t in downbeat_times],
        bars
    )


# ---------------------------------------------------------------
# Structural Analysis
# ---------------------------------------------------------------

def detect_first_vocal_entry(y, sr):
    """
    Detect where vocals first appear by monitoring the 300-4000 Hz band.
    Returns time in seconds.
    """
    # Isolate vocal band
    y_vocal = librosa.effects.preemphasis(y, coef=0.97)

    # Compute RMS in short windows
    rms = librosa.feature.rms(y=y_vocal, frame_length=2048, hop_length=512)[0]
    times = librosa.frames_to_time(np.arange(len(rms)), sr=sr, hop_length=512)

    if len(rms) < 10:
        return 0.0

    # Baseline from first second
    baseline_frames = min(int(sr / 512), len(rms) // 2)
    baseline = float(np.median(rms[:max(1, baseline_frames)]))
    threshold = max(baseline * 3.0, 1e-6)

    # Find first sustained energy above threshold (3+ consecutive frames)
    consecutive = 0
    for i, r in enumerate(rms):
        if r > threshold:
            consecutive += 1
            if consecutive >= 3:
                return round(float(times[max(0, i - 2)]), 3)
        else:
            consecutive = 0

    return round(float(times[-1]), 3)  # No clear vocal entry


def detect_first_strong_rhythm(y, sr, bpm):
    """
    Detect where a stable rhythmic pattern begins.
    Returns time in seconds.
    """
    # Onset strength in the percussive band
    onset_env = librosa.onset.onset_strength(y=y, sr=sr, hop_length=512)
    times = librosa.frames_to_time(np.arange(len(onset_env)), sr=sr, hop_length=512)

    # Compute autocorrelation of onset strength in bar-sized windows
    samples_per_bar = int(4 * 60 / bpm * sr / 512)  # in frames

    if samples_per_bar < 2 or len(onset_env) < samples_per_bar * 2:
        return 0.0

    bar_count = len(onset_env) // samples_per_bar
    scores = []

    for bar in range(bar_count):
        start = bar * samples_per_bar
        end = min(start + samples_per_bar, len(onset_env))
        segment = onset_env[start:end]

        if len(segment) < 4:
            scores.append(0.0)
            continue

        # Score = standard deviation of onset strength (regular beats = consistent peaks)
        scores.append(float(np.std(segment)))

    if not scores:
        return 0.0

    peak_score = max(scores)
    threshold = peak_score * 0.5

    for bar, score in enumerate(scores):
        if score >= threshold:
            return round(float(bar * samples_per_bar * 512 / sr), 3)

    return 0.0


def compute_energy_curve(y, sr, bars):
    """Compute normalized RMS energy per bar."""
    if not bars:
        return []

    energies = []
    for start, end in bars:
        s = int(start * sr)
        e = int(end * sr)
        segment = y[s:e]
        if len(segment) > 0:
            energies.append(float(np.sqrt(np.mean(segment ** 2))))
        else:
            energies.append(0.0)

    max_e = max(energies) if energies else 1.0
    if max_e > 0:
        energies = [round(e / max_e, 3) for e in energies]

    return energies


def find_recommended_crossfade(downbeats, first_vocal, first_rhythm, duration):
    """
    Find the best crossfade start point:
    1. Must be on a downbeat
    2. Must be before first vocal entry
    3. Should be at or after first strong rhythm
    4. If vocals start immediately, return 0
    """
    if not downbeats:
        return 0.0

    # If vocals start in the first 2 seconds, crossfade from the start
    if first_vocal < 2.0:
        return 0.0

    # Find the last downbeat before vocals
    best = 0.0
    for db in downbeats:
        if db < first_vocal:
            best = db
        else:
            break

    # If there's a strong rhythm section before vocals, prefer that
    if first_rhythm > 0 and first_rhythm < first_vocal:
        for db in downbeats:
            if db <= first_rhythm:
                best = db
            else:
                break

    return round(best, 4)


# ---------------------------------------------------------------
# Genre Classification (simple heuristic, can be replaced with ML)
# ---------------------------------------------------------------

def classify_genre(y, sr, bpm):
    """Simple genre classification based on audio features."""
    # Spectral features
    spectral_centroid = float(np.mean(librosa.feature.spectral_centroid(y=y, sr=sr)))

    # Onset density (transients per second)
    onsets = librosa.onset.onset_detect(y=y, sr=sr)
    onset_density = len(onsets) / (len(y) / sr) if len(y) > 0 else 0

    # Bass energy ratio
    S = np.abs(librosa.stft(y))
    freqs = librosa.fft_frequencies(sr=sr)
    bass_mask = freqs < 200
    bass_energy = float(np.sum(S[bass_mask, :] ** 2))
    total_energy = float(np.sum(S ** 2))
    bass_ratio = bass_energy / total_energy if total_energy > 0 else 0

    # Simple rules (will be replaced with ML later)
    if 85 <= bpm <= 105 and bass_ratio > 0.3:
        return "Reggaeton"
    if 90 <= bpm <= 115 and bass_ratio > 0.3 and onset_density > 8:
        return "Reparto"
    if 115 <= bpm <= 140 and bass_ratio > 0.35:
        return "Dembow"
    if 118 <= bpm <= 132 and bass_ratio > 0.2:
        return "House"
    if 125 <= bpm <= 150 and onset_density < 6:
        return "Techno"
    if 160 <= bpm <= 180:
        return "Drum & Bass"
    if 75 <= bpm <= 110 and bass_ratio > 0.3 and onset_density < 5:
        return "Hip-Hop"
    if bpm < 100 and onset_density < 4:
        return "R&B"

    return "Pop"


# ---------------------------------------------------------------
# Main
# ---------------------------------------------------------------

def analyze(file_path, genre_override=None):
    """Full track analysis. Returns a dict with all analysis data."""

    # Load audio
    y, sr = librosa.load(file_path, sr=None, mono=True)
    duration = float(len(y) / sr)

    # BPM
    bpm = detect_bpm(y, sr, genre=genre_override)

    # Key
    key_name, bass_freq = detect_key(y, sr)

    # Genre
    if genre_override:
        genre = genre_override
    else:
        genre = classify_genre(y, sr, bpm)

    # Beats, downbeats, bars
    beats, downbeats, bars = detect_beats_and_downbeats(y, sr, bpm)

    # Structural analysis
    first_vocal = detect_first_vocal_entry(y, sr)
    first_rhythm = detect_first_strong_rhythm(y, sr, bpm)
    energy_curve = compute_energy_curve(y, sr, bars)
    crossfade_start = find_recommended_crossfade(downbeats, first_vocal, first_rhythm, duration)

    return {
        "bpm": bpm,
        "key": key_name,
        "bass_frequency": round(bass_freq, 3),
        "genre": genre,
        "duration": round(duration, 3),
        "beats": beats,
        "downbeats": downbeats,
        "bars": bars,
        "first_vocal_entry": first_vocal,
        "first_strong_rhythm": first_rhythm,
        "energy_curve": energy_curve,
        "recommended_crossfade_start": crossfade_start,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="MixReady Audio Analyzer")
    parser.add_argument("file", help="Path to audio file")
    parser.add_argument("--genre", default=None, help="Genre override for BPM correction")
    args = parser.parse_args()

    result = analyze(args.file, genre_override=args.genre)
    print(json.dumps(result))
