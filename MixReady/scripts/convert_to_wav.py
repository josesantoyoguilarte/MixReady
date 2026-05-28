"""
Convert any supported audio format (mp3, flac, ogg, aac, m4a, wav) to a WAV file.

Used by the C# AudioConverter helper to bridge NAudio's Windows-only
MediaFoundation dependency on non-Windows hosts (macOS/Linux).

Usage:
    python convert_to_wav.py "<input_path>" "<output_wav_path>"

Output:
    Exit 0 on success, non-zero on failure. Errors go to stderr.
"""

import sys
import warnings

warnings.filterwarnings("ignore")


def main():
    if len(sys.argv) != 3:
        print("usage: convert_to_wav.py <input> <output.wav>", file=sys.stderr)
        sys.exit(2)

    src, dst = sys.argv[1], sys.argv[2]

    import librosa
    import soundfile as sf
    import numpy as np

    # Preserve original sample rate and channel layout.
    y, sr = librosa.load(src, sr=None, mono=False)
    if y.ndim == 1:
        data = y
    else:
        # librosa returns (channels, samples); soundfile wants (samples, channels)
        data = y.T

    sf.write(dst, data.astype(np.float32), sr, subtype="FLOAT")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"convert_to_wav failed: {e}", file=sys.stderr)
        sys.exit(1)
