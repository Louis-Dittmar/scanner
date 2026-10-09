"""Tests for server.py without a GPU: everything around the model runs with --fake-model.

Run with `python -m pytest Scanner/Scanner/OcrService/tests` (needs only pytest; psutil is optional).
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import pytest

SERVER = Path(__file__).resolve().parents[1] / "server.py"
TOKEN = "0123456789abcdef0123456789abcdef"

# a 3x2 PNG header is enough for the size detection
PNG_3X2 = (
    b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + (3).to_bytes(4, "big") + (2).to_bytes(4, "big") + b"\x08\x02\x00\x00\x00"
)


class Service:
    def __init__(self, tmp_path: Path, *extra: str, parent_pid: int | None = None) -> None:
        self.state_file = tmp_path / "service.json"
        self.log_file = tmp_path / "service.log"
        env = dict(os.environ, SCANNER_OCR_TOKEN=TOKEN, PYTHONUTF8="1")
        self.process = subprocess.Popen(
            [
                sys.executable, "-u", str(SERVER),
                "--state-file", str(self.state_file),
                "--log-file", str(self.log_file),
                "--parent-pid", str(parent_pid if parent_pid is not None else os.getpid()),
                "--fake-model",
                *extra,
            ],
            env=env,
        )
        deadline = time.monotonic() + 20
        while not self.state_file.exists():
            assert self.process.poll() is None, self.log()
            assert time.monotonic() < deadline, "service didn't start"
            time.sleep(0.1)
        time.sleep(0.1)
        self.port = json.loads(self.state_file.read_text(encoding="utf-8"))["port"]

    def log(self) -> str:
        return self.log_file.read_text(encoding="utf-8") if self.log_file.exists() else ""

    def request(self, method: str, path: str, body: bytes | None = None, token: str = TOKEN) -> tuple[int, dict]:
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", data=body, method=method)
        request.add_header("X-Token", token)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read())

    def wait_ready(self) -> dict:
        deadline = time.monotonic() + 20
        while True:
            status, health = self.request("GET", "/health")
            if health["state"] == "ready":
                return health
            assert time.monotonic() < deadline, health
            time.sleep(0.1)

    def stop(self) -> None:
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait(10)


@pytest.fixture
def service(tmp_path: Path):
    s = Service(tmp_path)
    yield s
    s.stop()


def test_rejects_requests_without_token(service: Service) -> None:
    status, body = service.request("GET", "/health", token="wrong-token-wrong-token")
    assert status == 401
    assert body == {"error": "unauthorized"}

    status, _ = service.request("POST", "/ocr", PNG_3X2, token="")
    assert status == 401


def test_health_reports_ready(service: Service) -> None:
    health = service.wait_ready()
    assert health["device"] == "fake"
    assert health["model"] == "baidu/Unlimited-OCR"


def test_ocr_returns_raw_output_with_markers(service: Service) -> None:
    service.wait_ready()

    status, body = service.request("POST", "/ocr?mode=base", PNG_3X2)

    assert status == 200, body
    assert body["width"] == 3 and body["height"] == 2
    assert "<|det|>title [80, 40, 920, 90]<|/det|>" in body["raw"]
    assert body["seconds"] >= 0


def test_ocr_rejects_empty_body(service: Service) -> None:
    service.wait_ready()
    status, _ = service.request("POST", "/ocr", b"")
    assert status == 400


def test_unknown_paths(service: Service) -> None:
    assert service.request("GET", "/nope")[0] == 404
    assert service.request("POST", "/nope", b"x")[0] == 404


def test_state_file_does_not_contain_the_token(service: Service) -> None:
    content = service.state_file.read_text(encoding="utf-8")
    assert TOKEN not in content
    assert json.loads(content)["pid"] == service.process.pid


def test_shutdown_stops_and_removes_state_file(service: Service) -> None:
    status, body = service.request("POST", "/shutdown", b"")
    assert status == 200 and body["state"] == "stopping"

    service.process.wait(15)
    assert not service.state_file.exists()
    assert "stopped" in service.log()


def test_stops_when_the_app_is_gone(tmp_path: Path) -> None:
    # a process that exits right away plays the app
    app = subprocess.Popen([sys.executable, "-c", "pass"])
    app.wait(10)
    s = Service(tmp_path, "--linger", "0", parent_pid=app.pid)
    try:
        s.process.wait(20)
        assert "app closed" in s.log()
    finally:
        s.stop()


def test_follows_a_reopened_app(tmp_path: Path) -> None:
    app = subprocess.Popen([sys.executable, "-c", "pass"])
    app.wait(10)
    s = Service(tmp_path, "--linger", "4", parent_pid=app.pid)
    try:
        # the reopened app (this test process) registers itself before the linger time is over
        state = json.loads(s.state_file.read_text(encoding="utf-8"))
        state["appPid"] = os.getpid()
        s.state_file.write_text(json.dumps(state), encoding="utf-8")

        time.sleep(7)
        assert s.process.poll() is None, s.log()
        assert "app reconnected" in s.log()
    finally:
        s.stop()


def test_idle_timeout(tmp_path: Path) -> None:
    s = Service(tmp_path, "--idle-timeout", "1")
    try:
        s.process.wait(20)
        assert "idle timeout" in s.log()
    finally:
        s.stop()


def test_refuses_short_token(tmp_path: Path) -> None:
    env = dict(os.environ, SCANNER_OCR_TOKEN="short")
    result = subprocess.run(
        [sys.executable, str(SERVER), "--state-file", str(tmp_path / "s.json"), "--fake-model"],
        env=env, capture_output=True, text=True, timeout=20,
    )
    assert result.returncode != 0
    assert "SCANNER_OCR_TOKEN" in result.stderr
