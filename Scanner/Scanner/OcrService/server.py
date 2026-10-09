"""Headless text recognition service for Scanner, based on the baidu/Unlimited-OCR model.

Started and managed by the app (AiOcrService.cs). It listens on 127.0.0.1 only and expects the token passed
via the SCANNER_OCR_TOKEN environment variable in the X-Token header of every request.

Lifecycle:
- The model is loaded in a background thread, /health reports the progress meanwhile.
- The service watches the app process (--parent-pid). Once the app is gone, it keeps running for --linger
  seconds (0 = stop right away), then exits and frees the GPU memory.
- With --idle-timeout > 0 it also exits after that many seconds without requests; the app starts it again
  when needed.

Endpoints:
- GET  /health           {"state": "starting|downloading|loading|ready|error", "message", "device", "dtype"}
- POST /ocr?mode=gundam  body: PNG/JPEG bytes -> {"width", "height", "raw", "seconds"}
                         raw is the unprocessed model output with its <|det|> position markers; the app parses it
                         (Scanner.Core UnlimitedOcrParser). mode is "gundam" (default, best for single pages) or "base".
- POST /shutdown         ends the service

--fake-model answers with a fixed result without PyTorch, for tests of everything around the model.
"""

from __future__ import annotations

import argparse
import contextlib
import hmac
import io
import json
import os
import shutil
import sys
import tempfile
import threading
import time
import traceback
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

MODEL_ID = os.environ.get("SCANNER_OCR_MODEL", "baidu/Unlimited-OCR")
PROMPT = "<image>document parsing."
MAX_LENGTH = 16384
NGRAM_SIZE = 35
NGRAM_WINDOW = 128
MAX_IMAGE_BYTES = 80 * 1024 * 1024

# the two single image configurations from the model card
MODES = {
    "gundam": {"base_size": 1024, "image_size": 640, "crop_mode": True},
    "base": {"base_size": 1024, "image_size": 1024, "crop_mode": False},
}

FAKE_RESULT = (
    "<|det|>title [80, 40, 920, 90]<|/det|># Testdokument\n"
    "<|det|>text [80, 120, 920, 300]<|/det|>Dies ist ein Ergebnis des Testmodus.\n"
    "<|det|>image [100, 350, 500, 600]<|/det|>\n"
)


# ----------------------------------------------------------------------------------------------------------------------
# State
# ----------------------------------------------------------------------------------------------------------------------
class ServiceState:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.state = "starting"
        self.message = ""
        self.device = ""
        self.dtype = ""
        self.last_activity = time.monotonic()

    def set(self, state: str, message: str = "") -> None:
        with self.lock:
            self.state = state
            self.message = message
        log(f"state: {state} {message}".strip())

    def touch(self) -> None:
        with self.lock:
            self.last_activity = time.monotonic()

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                "state": self.state,
                "message": self.message,
                "device": self.device,
                "dtype": self.dtype,
                "model": MODEL_ID,
            }


STATE = ServiceState()
MODEL: Any = None
TOKENIZER: Any = None
FAKE_MODEL = False
MODEL_LOCK = threading.Lock()
SHUTDOWN = threading.Event()


LOG_STREAM: Any = sys.stdout


def log(text: str) -> None:
    # explicit stream: stdout is temporarily captured while the model runs
    print(f"[{time.strftime('%H:%M:%S')}] {text}", file=LOG_STREAM, flush=True)


# ----------------------------------------------------------------------------------------------------------------------
# Model
# ----------------------------------------------------------------------------------------------------------------------
def force_float16_autocast() -> None:
    """The model's code hard-codes `torch.autocast("cuda", dtype=torch.bfloat16)`. GPUs without native bfloat16
    support (e.g. RTX 20 series) would emulate it, so autocast is redirected to float16 there. This only affects
    this dedicated process."""
    import torch

    original_autocast = torch.autocast

    def autocast_float16(device_type: str, dtype: Any = None, *args: Any, **kwargs: Any) -> Any:
        if device_type == "cuda" and dtype == torch.bfloat16:
            dtype = torch.float16
        return original_autocast(device_type, dtype, *args, **kwargs)

    torch.autocast = autocast_float16  # type: ignore[assignment]


def load_model() -> None:
    global MODEL, TOKENIZER
    if FAKE_MODEL:
        STATE.device = "fake"
        STATE.dtype = "none"
        STATE.set("ready", "fake model")
        return

    try:
        STATE.set("loading", "Importing libraries")
        import torch
        from huggingface_hub import snapshot_download
        from transformers import AutoModel, AutoTokenizer

        if not torch.cuda.is_available():
            STATE.set("error", "No CUDA-capable GPU found")
            return

        # native bfloat16 needs Ampere or newer (e.g. RTX 30/40/50); Turing (RTX 20) uses float16 instead
        native_bf16 = torch.cuda.is_bf16_supported(including_emulation=False)
        dtype = torch.bfloat16 if native_bf16 else torch.float16
        if not native_bf16:
            force_float16_autocast()
        STATE.device = torch.cuda.get_device_name(0)
        STATE.dtype = str(dtype).replace("torch.", "")

        STATE.set("downloading", "Downloading model (about 7 GB on first use)")
        snapshot_download(MODEL_ID)

        STATE.set("loading", "Loading model onto the GPU")
        TOKENIZER = AutoTokenizer.from_pretrained(MODEL_ID, trust_remote_code=True)
        MODEL = AutoModel.from_pretrained(
            MODEL_ID,
            trust_remote_code=True,
            use_safetensors=True,
            torch_dtype=dtype,
            low_cpu_mem_usage=True,
            attn_implementation="eager",    # the model code only knows "eager" and "flash_attention_2"
        ).eval().cuda()
        torch.set_grad_enabled(False)

        STATE.set("ready", f"{STATE.device}, {STATE.dtype}")
    except Exception as exc:  # noqa: BLE001 - reported to the app
        traceback.print_exc()
        STATE.set("error", f"{exc.__class__.__name__}: {exc}")


def image_size(image_bytes: bytes) -> tuple[int, int]:
    """Width and height from the PNG/JPEG header, without needing Pillow (the fake model runs without it)."""
    if image_bytes[:8] == b"\x89PNG\r\n\x1a\n" and len(image_bytes) >= 24:
        return int.from_bytes(image_bytes[16:20], "big"), int.from_bytes(image_bytes[20:24], "big")
    try:
        from PIL import Image

        with Image.open(io.BytesIO(image_bytes)) as image:
            return image.size
    except Exception:  # noqa: BLE001 - size is informational
        return 0, 0


def run_ocr(image_bytes: bytes, mode: str) -> dict[str, Any]:
    width, height = image_size(image_bytes)
    if FAKE_MODEL:
        return {"width": width, "height": height, "raw": FAKE_RESULT}

    work_dir = Path(tempfile.mkdtemp(prefix="scanner_ocr_"))
    try:
        image_path = work_dir / "page.png"
        image_path.write_bytes(image_bytes)
        config = MODES.get(mode, MODES["gundam"])

        with MODEL_LOCK:
            captured = io.StringIO()
            try:
                # eval_mode returns the decoded text; older model code only streams it to stdout, so that is
                # captured as a fallback
                with contextlib.redirect_stdout(captured):
                    returned = MODEL.infer(
                        TOKENIZER,
                        prompt=PROMPT,
                        image_file=str(image_path),
                        output_path=str(work_dir),
                        save_results=False,
                        eval_mode=True,
                        max_length=MAX_LENGTH,
                        no_repeat_ngram_size=NGRAM_SIZE,
                        ngram_window=NGRAM_WINDOW,
                        **config,
                    )
            finally:
                # the model temporarily disables its sliding window during generation
                ring_window = getattr(MODEL.config, "_ring_window", None)
                if ring_window is not None:
                    MODEL.config.sliding_window = ring_window

        raw = returned if isinstance(returned, str) and returned.strip() else captured.getvalue()
        return {"width": width, "height": height, "raw": raw}
    finally:
        shutil.rmtree(work_dir, ignore_errors=True)


# ----------------------------------------------------------------------------------------------------------------------
# HTTP
# ----------------------------------------------------------------------------------------------------------------------
class Handler(BaseHTTPRequestHandler):
    server_version = "ScannerOcr/1.0"
    token = ""

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - signature from base class
        pass  # requests may contain document content, keep the console clean

    def _authorized(self) -> bool:
        return hmac.compare_digest(self.headers.get("X-Token", ""), Handler.token)

    def _send_json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802 - http.server naming
        if not self._authorized():
            self._send_json(HTTPStatus.UNAUTHORIZED, {"error": "unauthorized"})
            return
        if self.path.partition("?")[0] == "/health":
            self._send_json(HTTPStatus.OK, STATE.snapshot())
        else:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802 - http.server naming
        if not self._authorized():
            self._send_json(HTTPStatus.UNAUTHORIZED, {"error": "unauthorized"})
            return

        path, _, query = self.path.partition("?")
        params = dict(part.split("=", 1) for part in query.split("&") if "=" in part)

        if path == "/shutdown":
            self._send_json(HTTPStatus.OK, {"state": "stopping"})
            SHUTDOWN.set()
            return

        if path != "/ocr":
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})
            return

        if STATE.snapshot()["state"] != "ready":
            self._send_json(HTTPStatus.SERVICE_UNAVAILABLE, STATE.snapshot())
            return

        length = int(self.headers.get("Content-Length", "0") or 0)
        if length <= 0 or length > MAX_IMAGE_BYTES:
            self._send_json(HTTPStatus.BAD_REQUEST, {"error": "missing or too large image"})
            return

        STATE.touch()
        image_bytes = self.rfile.read(length)
        try:
            started = time.monotonic()
            result = run_ocr(image_bytes, params.get("mode", "gundam"))
            result["seconds"] = round(time.monotonic() - started, 2)
            log(f"page analyzed in {result['seconds']:.1f}s, {len(result['raw'])} characters")
            self._send_json(HTTPStatus.OK, result)
        except Exception as exc:  # noqa: BLE001 - reported to the app
            traceback.print_exc()
            self._send_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": f"{exc.__class__.__name__}: {exc}"})
        finally:
            STATE.touch()


# ----------------------------------------------------------------------------------------------------------------------
# Lifecycle
# ----------------------------------------------------------------------------------------------------------------------
def is_process_alive(pid: int) -> bool:
    if pid <= 0:
        return True
    try:
        import psutil

        return psutil.pid_exists(pid)
    except ImportError:
        pass

    if os.name == "nt":
        import ctypes

        process_query_limited_information = 0x1000
        still_active = 259
        handle = ctypes.windll.kernel32.OpenProcess(process_query_limited_information, False, pid)
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code))
            return code.value == still_active
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)

    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def current_app_pid(state_file: Path, default_pid: int) -> int:
    """A reopened app writes its PID into the state file, so a lingering service follows the new instance."""
    try:
        data = json.loads(state_file.read_text(encoding="utf-8"))
        return int(data.get("appPid", default_pid))
    except (OSError, ValueError):
        return default_pid


def watchdog(state_file: Path, initial_pid: int, linger: float, idle_timeout: float) -> None:
    """Stops the service once the app has been gone for `linger` seconds or nothing happened for `idle_timeout`."""
    watched_pid = initial_pid
    app_gone_since: float | None = None

    while not SHUTDOWN.wait(2):
        now = time.monotonic()

        pid = current_app_pid(state_file, watched_pid)
        if pid != watched_pid:
            log("app reconnected")
            watched_pid = pid
            app_gone_since = None

        if not is_process_alive(watched_pid):
            if app_gone_since is None:
                app_gone_since = now
                log(f"app closed, stopping in {linger:.0f}s unless it comes back")
            elif now - app_gone_since >= linger:
                log("app didn't come back, stopping")
                SHUTDOWN.set()
        else:
            app_gone_since = None

        with STATE.lock:
            idle = now - STATE.last_activity
        if idle_timeout > 0 and idle >= idle_timeout:
            log("idle timeout reached, stopping")
            SHUTDOWN.set()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=0, help="0 picks a free port")
    parser.add_argument("--parent-pid", type=int, default=0)
    parser.add_argument("--state-file", type=Path, required=True, help="written once the port is known")
    parser.add_argument("--linger", type=float, default=0, help="seconds to keep running after the app closed")
    parser.add_argument("--idle-timeout", type=float, default=0, help="seconds without requests until exit, 0 = never")
    parser.add_argument("--fake-model", action="store_true", help="answer with a fixed result, for tests")
    parser.add_argument("--log-file", type=Path, help="the service outlives the app, so it can't log into a pipe")
    args = parser.parse_args()

    global FAKE_MODEL
    FAKE_MODEL = args.fake_model

    if args.log_file:
        args.log_file.parent.mkdir(parents=True, exist_ok=True)
        if args.log_file.exists() and args.log_file.stat().st_size > 5 * 1024 * 1024:
            args.log_file.unlink()
        log_stream = open(args.log_file, "a", encoding="utf-8", buffering=1)  # noqa: SIM115 - lives as long as the process
        sys.stdout = log_stream
        sys.stderr = log_stream

    global LOG_STREAM
    LOG_STREAM = sys.stdout

    token = os.environ.get("SCANNER_OCR_TOKEN", "")
    if len(token) < 16:
        sys.exit("SCANNER_OCR_TOKEN missing or too short")
    Handler.token = token

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    port = server.server_address[1]

    # the token is not written to the state file, the app keeps it in memory and in the environment it passed
    args.state_file.parent.mkdir(parents=True, exist_ok=True)
    args.state_file.write_text(
        json.dumps({"port": port, "pid": os.getpid(), "appPid": args.parent_pid}), encoding="utf-8"
    )
    log(f"listening on 127.0.0.1:{port}")

    threading.Thread(target=load_model, name="model-loader", daemon=True).start()
    threading.Thread(target=server.serve_forever, name="http", daemon=True).start()

    watchdog(args.state_file, args.parent_pid, args.linger, args.idle_timeout)
    server.shutdown()
    try:
        state = json.loads(args.state_file.read_text(encoding="utf-8"))
        if state.get("pid") == os.getpid():
            args.state_file.unlink()
    except (OSError, ValueError):
        pass
    log("stopped")


if __name__ == "__main__":
    main()
