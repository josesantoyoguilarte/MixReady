"""
MixReady Stem Separator - uses Demucs AI for vocal removal.

Loads audio via soundfile/librosa (NOT torchaudio) to avoid
torchcodec/FFmpeg dependency issues on Windows.

Usage:
    python separate.py "<audio_file_path>" "<output_dir>"

Output:
    JSON to stdout with paths to each stem.
"""

import sys
import os
import json
import warnings
import argparse

warnings.filterwarnings("ignore")


def check_demucs_available():
    try:
        import demucs
        return True
    except ImportError:
        return False


def load_audio(file_path, target_sr=44100, channels=2):
    """Load audio using librosa (works everywhere, no FFmpeg needed)."""
    import numpy as np
    import torch

    try:
        import librosa
        # Load as mono first, then we'll handle channels
        y, sr = librosa.load(file_path, sr=target_sr, mono=False)

        if y.ndim == 1:
            # Mono -> duplicate to stereo
            y = np.stack([y, y])
        elif y.shape[0] > channels:
            y = y[:channels]
        elif y.shape[0] < channels:
            # Pad with copies of first channel
            while y.shape[0] < channels:
                y = np.concatenate([y, y[:1]], axis=0)

        # Convert to torch tensor: shape (channels, samples)
        wav = torch.from_numpy(y).float()
        return wav, sr

    except Exception:
        # Last resort: soundfile
        import soundfile as sf
        data, sr = sf.read(file_path, dtype='float32', always_2d=True)
        # data is (samples, channels)
        import torch
        wav = torch.from_numpy(data.T).float()  # -> (channels, samples)

        if wav.shape[0] > channels:
            wav = wav[:channels]
        elif wav.shape[0] < channels:
            wav = wav.repeat(channels, 1)[:channels]

        if sr != target_sr:
            # Simple resample using librosa
            import librosa
            resampled = []
            for ch in range(wav.shape[0]):
                resampled.append(librosa.resample(wav[ch].numpy(), orig_sr=sr, target_sr=target_sr))
            import numpy as np
            wav = torch.from_numpy(np.stack(resampled)).float()
            sr = target_sr

        return wav, sr


def save_wav(tensor, path, sample_rate):
    """Save a torch tensor as WAV using soundfile."""
    import soundfile as sf
    import numpy as np

    # tensor shape: (channels, samples)
    data = tensor.cpu().numpy()
    if data.ndim == 1:
        data = data[np.newaxis, :]
    # soundfile expects (samples, channels)
    sf.write(path, data.T, sample_rate, subtype='FLOAT')


def separate(file_path, output_dir, model_name=None):
    """Separate a track into stems using demucs."""
    import torch
    from demucs.pretrained import get_model
    from demucs.apply import apply_model
    import time

    # Use all available CPU cores for PyTorch
    num_threads = int(os.environ.get("OMP_NUM_THREADS", os.cpu_count() or 4))
    torch.set_num_threads(num_threads)
    torch.set_num_interop_threads(min(4, num_threads))

    print(f"[demucs] Threads: {num_threads}, CPU count: {os.cpu_count()}", file=sys.stderr)

    os.makedirs(output_dir, exist_ok=True)

    # Model selection: env var or argument or default
    if not model_name:
        model_name = os.environ.get("DEMUCS_MODEL", "htdemucs")

    print(f"[demucs] Loading model: {model_name}", file=sys.stderr)
    t0 = time.time()
    model = get_model(model_name)
    model.eval()
    print(f"[demucs] Model loaded in {time.time()-t0:.1f}s", file=sys.stderr)

    # Load audio using our own loader (bypasses torchaudio)
    wav, sr = load_audio(file_path, target_sr=model.samplerate, channels=model.audio_channels)
    duration = wav.shape[-1] / sr
    print(f"[demucs] Audio: {duration:.1f}s, {sr}Hz", file=sys.stderr)

    # Normalize
    ref = wav.mean(0)
    wav = (wav - ref.mean()) / ref.std()

    # Run the model with segment-based processing for lower memory usage
    print(f"[demucs] Separating...", file=sys.stderr)
    t1 = time.time()
    with torch.no_grad():
        sources = apply_model(model, wav[None], device="cpu", split=True, overlap=0.25)[0]
    elapsed = time.time() - t1
    print(f"[demucs] Separation done in {elapsed:.1f}s ({elapsed/duration:.1f}x realtime)", file=sys.stderr)

    # Denormalize
    sources = sources * ref.std() + ref.mean()

    # Save each stem
    stem_names = model.sources  # ['drums', 'bass', 'other', 'vocals']
    result = {}

    for i, name in enumerate(stem_names):
        stem_path = os.path.join(output_dir, f"{name}.wav")
        save_wav(sources[i], stem_path, model.samplerate)
        result[name] = stem_path

    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="MixReady Stem Separator")
    parser.add_argument("file", help="Path to audio file")
    parser.add_argument("output_dir", help="Directory to save stems")
    args = parser.parse_args()

    if not check_demucs_available():
        print(json.dumps({"error": "demucs not installed. Run: pip install demucs"}))
        sys.exit(1)

    try:
        result = separate(args.file, args.output_dir)
        print(json.dumps(result))
    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)
