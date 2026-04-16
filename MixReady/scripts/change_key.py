"""Change key of an audio file using librosa pitch_shift."""
import sys
import json
import argparse
import warnings
import numpy as np

warnings.filterwarnings("ignore")

NOTES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
FLAT_MAP = {'Db': 'C#', 'Eb': 'D#', 'Gb': 'F#', 'Ab': 'G#', 'Bb': 'A#'}


def parse_key(key_str):
    key = key_str.strip()
    is_minor = key.lower().endswith('m')
    if is_minor:
        key = key[:-1]
    if key in FLAT_MAP:
        key = FLAT_MAP[key]
    if key in NOTES:
        return NOTES.index(key), is_minor
    raise ValueError(f"Unknown key: {key_str}")


def semitones_between(from_key, to_key):
    from_idx, _ = parse_key(from_key)
    to_idx, _ = parse_key(to_key)
    diff = (to_idx - from_idx) % 12
    if diff > 6:
        diff -= 12
    return diff


def change_key(input_path, output_path, semitones):
    import librosa
    import soundfile as sf

    if semitones == 0:
        import shutil
        shutil.copy2(input_path, output_path)
        return

    y, sr = librosa.load(input_path, sr=None, mono=False)
    if y.ndim == 1:
        y_shifted = librosa.effects.pitch_shift(y, sr=sr, n_steps=semitones)
    else:
        channels = []
        for ch in range(y.shape[0]):
            channels.append(librosa.effects.pitch_shift(y[ch], sr=sr, n_steps=semitones))
        y_shifted = np.stack(channels)

    data = y_shifted.T if y_shifted.ndim > 1 else y_shifted
    sf.write(output_path, data, sr)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Change key via pitch shifting")
    parser.add_argument("input", help="Input audio file")
    parser.add_argument("output", help="Output audio file")
    parser.add_argument("semitones", type=int, help="Semitones to shift (positive=up, negative=down)")
    args = parser.parse_args()

    try:
        change_key(args.input, args.output, args.semitones)
        print(json.dumps({"output": args.output, "semitones": args.semitones}))
    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)
