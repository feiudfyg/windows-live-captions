"""Local HTTP server exposing Cohere Transcribe (full precision, PyTorch CUDA).

The WinUI app launches this process and POSTs raw 16 kHz mono float32 PCM
buffers, receiving {"text": ...} JSON back. Model weights, the Hugging Face
cache and the token all live inside the repo's data/ folder (see the
LiveCaptions app for the matching client).

    python server.py --port 12360 [--dtype bfloat16] [--device cuda]

Endpoints:
    GET  /health      -> {"status": "ok", ...}
    POST /transcribe?language=ja[&punctuation=1]  body: float32 LE PCM
"""

import argparse
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
import torch
from transformers import AutoProcessor, CohereAsrForConditionalGeneration

MODEL_ID = "CohereLabs/cohere-transcribe-03-2026"

_processor = None
_model = None
_lock = threading.Lock()
_info = {}
_firered_vad = None
_vad_lock = threading.Lock()
_vad_carry = np.zeros(0, dtype=np.float32)

MAX_BODY_BYTES = 64 * 1024 * 1024  # ~21 minutes of 16 kHz float32 PCM


def log(message: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {message}", flush=True)


def process_vad_frames(samples: np.ndarray, reset: bool) -> dict:
    """Streams PCM through FireRedVAD and returns the per-frame speech flags.

    The feature extractor builds a fresh fbank per call and drops the tail, so
    the samples that were not covered by an emitted frame are carried over. The
    carry is computed from the number of frames actually emitted, which keeps the
    10 ms frame grid aligned for any block size (first block included).
    """
    global _vad_carry

    with _vad_lock:
        if _firered_vad is None:
            raise RuntimeError("FireRedVAD not loaded")
        if reset:
            _firered_vad.reset()
            _vad_carry = np.zeros(0, dtype=np.float32)

        audio = np.concatenate([_vad_carry, samples]) if _vad_carry.size else samples

        frame_len, hop = 400, 160  # 25 ms window, 10 ms shift at 16 kHz
        frames = 0 if audio.size < frame_len else (audio.size - frame_len) // hop + 1
        next_start = frames * hop
        _vad_carry = audio[next_start:].copy() if audio.size > next_start else np.zeros(0, dtype=np.float32)

        results = _firered_vad.detect_chunk((audio * 32768.0).astype(np.float32))

        flags = [1 if r.is_speech else 0 for r in results]
        starts = sum(1 for r in results if r.is_speech_start)
        ends = sum(1 for r in results if r.is_speech_end)
        return {"frames": flags, "speech": any(flags), "starts": starts, "ends": ends}


def transcribe(samples: np.ndarray, language: str, punctuation: bool) -> str:
    with _lock:
        inputs = _processor(
            samples, sampling_rate=16000, return_tensors="pt", language=language, punctuation=punctuation
        )
        chunk_index = inputs.get("audio_chunk_index")
        inputs = inputs.to(_model.device, dtype=_model.dtype)

        with torch.inference_mode():
            outputs = _model.generate(**inputs, max_new_tokens=1024)

        if chunk_index is not None:
            text = _processor.decode(
                outputs, skip_special_tokens=True, audio_chunk_index=chunk_index, language=language
            )
            if isinstance(text, list):
                text = "".join(text)
        else:
            text = _processor.decode(outputs, skip_special_tokens=True)

    return text.strip()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # silence per-request access logs
        return

    def _send_json(self, payload: dict, status: int = 200) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802
        if self.path.split("?")[0] == "/health":
            self._send_json({"status": "ok", **_info})
        else:
            self._send_json({"error": "not found"}, status=404)

    def do_POST(self):  # noqa: N802
        route = self.path.split("?")[0]
        if route not in ("/transcribe", "/vad"):
            self._send_json({"error": "not found"}, status=404)
            return

        query = {}
        if "?" in self.path:
            for pair in self.path.split("?", 1)[1].split("&"):
                if "=" in pair:
                    key, value = pair.split("=", 1)
                    query[key] = value

        if self.headers.get("Transfer-Encoding"):
            self._send_json({"error": "chunked bodies are not supported"}, status=411)
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
        except (TypeError, ValueError):
            self._send_json({"error": "invalid Content-Length"}, status=400)
            return

        if length < 0 or length > MAX_BODY_BYTES:
            self._send_json({"error": "body too large"}, status=413)
            return

        body = self.rfile.read(length) if length else b""
        try:
            samples = np.frombuffer(body, dtype="<f4")
        except ValueError:
            self._send_json({"error": "body must be little-endian float32 PCM"}, status=400)
            return

        if route == "/vad":
            started = time.perf_counter()
            try:
                result = process_vad_frames(samples, reset=query.get("reset") == "1")
            except Exception as exc:  # noqa: BLE001
                log(f"[vad][error] {type(exc).__name__}: {exc}")
                self._send_json({"error": str(exc)}, status=500)
                return

            result["elapsed_ms"] = (time.perf_counter() - started) * 1000
            self._send_json(result)
            return

        language = query.get("language", _info.get("language", "ja")) or "ja"
        punctuation = query.get("punctuation", "1") not in ("0", "false", "False")
        if samples.size == 0:
            self._send_json({"text": ""})
            return

        started = time.perf_counter()
        try:
            text = transcribe(samples, language, punctuation)
        except Exception as exc:  # noqa: BLE001
            log(f"[error] {type(exc).__name__}: {exc}")
            self._send_json({"error": str(exc)}, status=500)
            return

        elapsed = time.perf_counter() - started
        log(f"[asr] {samples.size / 16000:.2f}s audio in {elapsed * 1000:.0f}ms -> {text[:60]}")
        self._send_json({"text": text, "seconds": samples.size / 16000, "elapsed_ms": elapsed * 1000})


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=12360)
    parser.add_argument("--model", default=MODEL_ID)
    parser.add_argument("--dtype", default="bfloat16", choices=["bfloat16", "float16", "float32"])
    parser.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--language", default="ja", help="default language when the request does not specify one")
    parser.add_argument("--vad-model", default=None, help="FireRedVAD stream model directory (enables /vad)")
    args = parser.parse_args()

    global _processor, _model, _firered_vad
    started = time.perf_counter()
    log(f"[boot] loading {args.model} ({args.dtype}) on {args.device} ...")
    _processor = AutoProcessor.from_pretrained(args.model)
    _model = CohereAsrForConditionalGeneration.from_pretrained(
        args.model, dtype=getattr(torch, args.dtype), device_map=args.device
    )
    _model.eval()
    log(f"[boot] model loaded in {time.perf_counter() - started:.1f}s")

    log("[boot] warming up (kernel autotune) ...")
    warm_started = time.perf_counter()
    # Keep the intra-op pool small: the model runs on CUDA and the VAD shares this
    # process, so a huge CPU pool only adds scheduling overhead.
    torch.set_num_threads(max(2, min(4, os.cpu_count() or 4)))
    for samples in (np.zeros(16000, dtype=np.float32), np.random.randn(32000).astype(np.float32) * 0.05):
        try:
            transcribe(samples, args.language, True)
        except Exception as exc:  # noqa: BLE001
            log(f"[boot] warmup failed: {exc}")
    log(f"[boot] warmup done in {time.perf_counter() - warm_started:.1f}s")

    if args.vad_model:
        if os.path.isdir(args.vad_model):
            try:
                from fireredvad.stream_vad import FireRedStreamVad, FireRedStreamVadConfig

                _firered_vad = FireRedStreamVad.from_pretrained(
                    args.vad_model, FireRedStreamVadConfig(use_gpu=False, speech_threshold=0.5)
                )
                log(f"[boot] FireRedVAD stream ready: {args.vad_model}")
            except Exception as exc:  # noqa: BLE001
                log(f"[boot] FireRedVAD unavailable: {exc}")
        else:
            log(f"[boot] FireRedVAD model dir not found: {args.vad_model}")

    _info.update({
        "model": args.model,
        "dtype": args.dtype,
        "device": args.device,
        "language": args.language,
        "torch": torch.__version__,
        "vad": _firered_vad is not None,
    })

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    log(f"[boot] listening on http://{args.host}:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()

    return 0


if __name__ == "__main__":
    sys.exit(main())
