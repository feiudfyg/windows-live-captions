"""Offline VAD comparison on real material (used to pick the segmentation model).

Runs FireRedVAD (streaming) and Silero VAD v6 over a wav and prints run counts,
speech coverage and gap statistics. TEN VAD results come from the C# smoke test
(`LiveCaptions.SmokeTest vad <model> <wav>`) and are compared by hand.

    python vad_compare.py <wav> [<wav> ...] [--firered <model_dir>] [--json out.json]
"""

import argparse
import json
import sys

import numpy as np
import soundfile as sf
import torch


def load_audio_16k(path: str) -> np.ndarray:
    audio, sample_rate = sf.read(path, dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if sample_rate != 16000:
        import librosa

        audio = librosa.resample(audio, orig_sr=sample_rate, target_sr=16000)
    return np.ascontiguousarray(audio, dtype=np.float32)


def runs_from_frames(speech: list[bool], shift_s: float) -> list[tuple[float, float]]:
    runs = []
    start = None
    for index, flag in enumerate(speech):
        if flag and start is None:
            start = index
        elif not flag and start is not None:
            runs.append((start * shift_s, index * shift_s))
            start = None
    if start is not None:
        runs.append((start * shift_s, len(speech) * shift_s))
    return runs


def summarize(name: str, runs: list[tuple[float, float]], total_s: float) -> str:
    speech = sum(end - start for start, end in runs)
    gaps = [(runs[i][0] - runs[i - 1][1]) * 1000 for i in range(1, len(runs))]
    line = f"[{name}] {len(runs)} runs, speech {speech:.1f}s / {total_s:.1f}s ({speech / total_s * 100:.0f}%)"
    if gaps:
        gaps_sorted = sorted(gaps)
        p = lambda q: gaps_sorted[min(len(gaps_sorted) - 1, int(q * len(gaps_sorted)))]
        line += f", gaps(ms) med={p(0.5):.0f} p75={p(0.75):.0f} p90={p(0.9):.0f} max={gaps_sorted[-1]:.0f}"
    return line


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("files", nargs="+")
    parser.add_argument("--firered", default=r"E:\CodeProjects\windows-live-captions\data\models\FireRedVAD\Stream-VAD")
    parser.add_argument("--json", default=None)
    parser.add_argument("--no-silero", action="store_true")
    args = parser.parse_args()

    from fireredvad.stream_vad import FireRedStreamVad, FireRedStreamVadConfig

    firered = FireRedStreamVad.from_pretrained(
        args.firered, FireRedStreamVadConfig(use_gpu=False, speech_threshold=0.4)
    )

    silero = None
    if not args.no_silero:
        from silero_vad import load_silero_vad

        silero = load_silero_vad(onnx=True)

    report = {}
    for path in args.files:
        audio = load_audio_16k(path)
        total_s = len(audio) / 16000

        firered.reset()
        results, _ = firered.detect_full(audio)
        fr_runs = runs_from_frames([r.is_speech for r in results], 0.01)

        print(f"\n=== {path} ({total_s:.1f}s)")
        print(summarize("FireRed stream", fr_runs, total_s))
        for index, (start, end) in enumerate(fr_runs):
            print(f"  fr run{index:3d}: {start:7.2f}s dur={end - start:5.2f}s")

        entry = {"firered": fr_runs}

        if silero is not None:
            from silero_vad import get_speech_timestamps

            stamps = get_speech_timestamps(
                torch.from_numpy(audio), silero, sampling_rate=16000, return_seconds=True
            )
            sil_runs = [(s["start"], s["end"]) for s in stamps]
            print(summarize("Silero v6", sil_runs, total_s))
            entry["silero6"] = sil_runs

        report[path] = entry

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(report, handle, ensure_ascii=False, indent=1)

    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
