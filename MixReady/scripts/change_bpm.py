"""Change BPM of an audio file using librosa time_stretch."""
import sys
import json
import argparse
import warnings
import numpy as np

warnings.filterwarnings("ignore")


def change_bpm(input_path, output_path, original_bpm, target_bpm):
    import librosa
    import soundfile as sf

    rate = target_bpm / original_bpm
    y, sr = librosa.load(input_path, sr=None, mono=False)

    if y.ndim == 1:
        y_stretched = librosa.effects.time_stretch(y, rate=rate)
    else:
        channels = []
        for ch in range(y.shape[0]):
            channels.append(librosa.effects.time_stretch(y[ch], rate=rate))
        y_stretched = np.stack(channels)

    data = y_stretched.T if y_stretched.ndim > 1 else y_stretched
    sf.write(output_path, data, sr)
    return output_path


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Change BPM of audio file")
    parser.add_argument("input", help="Input audio file")
    parser.add_argument("output", help="Output audio file")
    parser.add_argument("original_bpm", type=float)
    parser.add_argument("target_bpm", type=float)
    args = parser.parse_args()

    try:
        result = change_bpm(args.input, args.output, args.original_bpm, args.target_bpm)
        print(json.dumps({"output": result}))
    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)
