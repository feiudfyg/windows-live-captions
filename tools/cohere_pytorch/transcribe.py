"""Reference (PyTorch, full precision) transcription with Cohere Transcribe.

Used to evaluate the fp16/bf16 model against the quantised sherpa-onnx runtime
before deciding what the app should ship.

    python transcribe.py <wav> [<wav> ...] [--language ja] [--dtype bfloat16]
                         [--repeat N] [--compile] [--attn sdpa] [--out file.txt]

Requires the venv in data/python-env and a Hugging Face token in
data/hf-cache/token (the model repo is gated).
"""

import argparse
import sys
import time

import numpy as np
import soundfile as sf
import torch
from transformers import AutoProcessor, CohereAsrForConditionalGeneration

MODEL_ID = "CohereLabs/cohere-transcribe-03-2026"


def load_audio_16k(path: str) -> np.ndarray:
    audio, sample_rate = sf.read(path, dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if sample_rate != 16000:
        import librosa

        audio = librosa.resample(audio, orig_sr=sample_rate, target_sr=16000)
    return np.ascontiguousarray(audio, dtype=np.float32)


def install_sage_attention(report) -> None:
    """Route every scaled_dot_product_attention call through SageAttention.

    Falls back to the original kernel whenever SageAttention cannot handle the
    call (attention masks, dropout, unsupported shapes)."""
    import torch.nn.functional as functional
    from sageattention import sageattn

    original = functional.scaled_dot_product_attention
    stats = {"sage": 0, "fallback": 0, "error": 0}

    def sdpa_sage(query, key, value, attn_mask=None, dropout_p=0.0, is_causal=False, scale=None, **kwargs):
        if attn_mask is None and dropout_p == 0.0 and query.shape[-1] in (64, 128) and query.dtype in (torch.float16, torch.bfloat16):
            try:
                out = sageattn(query, key, value, tensor_layout="HND", is_causal=is_causal, sm_scale=scale)
                stats["sage"] += 1
                return out
            except Exception as exc:  # noqa: BLE001
                if stats["error"] == 0:
                    report(f"[sage] falling back ({type(exc).__name__}: {exc})")
                stats["error"] += 1
        stats["fallback"] += 1
        return original(query, key, value, attn_mask=attn_mask, dropout_p=dropout_p, is_causal=is_causal, scale=scale, **kwargs)

    functional.scaled_dot_product_attention = sdpa_sage
    report(f"[sage] patched scaled_dot_product_attention (SageAttention {getattr(__import__('sageattention'), '__version__', '?')})")
    return stats


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("files", nargs="+")
    parser.add_argument("--model", default=MODEL_ID)
    parser.add_argument("--language", default="ja")
    parser.add_argument("--dtype", default="bfloat16", choices=["bfloat16", "float16", "float32"])
    parser.add_argument("--max-new-tokens", type=int, default=256)
    parser.add_argument("--repeat", type=int, default=1, help="decode each file N times; report best")
    parser.add_argument("--compile", action="store_true", help="wrap model in torch.compile")
    parser.add_argument("--sage", action="store_true", help="replace scaled_dot_product_attention with SageAttention")
    parser.add_argument("--attn", default=None, help="attention implementation, e.g. sdpa/flash_attention_2/eager")
    parser.add_argument("--out", default=None, help="append results to this UTF-8 file")
    parser.add_argument("--no-punct", action="store_true")
    args = parser.parse_args()

    device = "cuda" if torch.cuda.is_available() else "cpu"
    dtype = getattr(torch, args.dtype)

    lines: list[str] = []

    def emit(text: str) -> None:
        print(text, flush=True)
        lines.append(text)

    sage_stats = None

    load_start = time.perf_counter()
    processor = AutoProcessor.from_pretrained(args.model)
    model = CohereAsrForConditionalGeneration.from_pretrained(
        args.model, dtype=dtype, device_map=device, attn_implementation=args.attn
    )
    model.eval()
    if args.compile:
        model = torch.compile(model, mode="reduce-overhead")
    if args.sage:
        sage_stats = install_sage_attention(emit)
    emit(f"[model] {args.model} {args.dtype} attn={args.attn} compile={args.compile} sage={args.sage} on {device}, "
         f"loaded in {time.perf_counter() - load_start:.1f}s")

    warm = False
    for path in args.files:
        audio = load_audio_16k(path)
        duration = len(audio) / 16000

        inputs = processor(
            audio, sampling_rate=16000, return_tensors="pt", language=args.language,
            punctuation=not args.no_punct,
        )
        chunk_index = inputs.get("audio_chunk_index")
        num_chunks = len(chunk_index) if chunk_index is not None else 1
        inputs = inputs.to(model.device, dtype=model.dtype)

        best = None
        texts = []
        for _ in range(args.repeat):
            start = time.perf_counter()
            with torch.inference_mode():
                outputs = model.generate(**inputs, max_new_tokens=args.max_new_tokens)
            elapsed = time.perf_counter() - start
            best = elapsed if best is None else min(best, elapsed)

            if chunk_index is not None:
                text = processor.decode(outputs, skip_special_tokens=True, audio_chunk_index=chunk_index, language=args.language)
                if isinstance(text, list):
                    text = "".join(text)
            else:
                text = processor.decode(outputs, skip_special_tokens=True)
            texts.append(text.strip())

        emit(f"\n=== {path}  (chunks={num_chunks})")
        emit(f"[time] {duration:.1f}s audio best {best:.2f}s ({duration / best:.1f}x realtime), repeat={args.repeat}")
        if sage_stats is not None and sage_stats["sage"] + sage_stats["fallback"] + sage_stats["error"] > 0:
            emit(f"[sage] calls: sage={sage_stats['sage']} fallback={sage_stats['fallback']} error={sage_stats['error']}")
        emit(texts[-1])
        if not warm:
            warm = True
            torch.cuda.empty_cache()

    if args.out:
        with open(args.out, "a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
