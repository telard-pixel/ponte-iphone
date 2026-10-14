import importlib.util
import json
import os
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).parent


def load(tmp):
    os.environ["STATE_DIRECTORY"] = str(tmp)
    os.environ["CREDENTIALS_DIRECTORY"] = str(tmp / "cred")
    os.environ["PORTA"] = "0"
    spec = importlib.util.spec_from_file_location("rapporti_ponte", HERE / "rapporti_ponte.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def serve(mod):
    srv = mod.ThreadingHTTPServer(("127.0.0.1", 0), mod.Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, f"http://127.0.0.1:{srv.server_address[1]}"


def post(url, body, ctype="application/json"):
    req = urllib.request.Request(url + "/api/rapporto", data=body, headers={"Content-Type": ctype}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, ""


def report(text="iPhone                     iPhone · iPhone13,2 · iOS 27.0.1\nriga"):
    return json.dumps({"app": "ponte-iphone", "versione": "1.0.25", "motivo": "prova", "testo": text}).encode()


def test_saves_valid_report_and_rejects_the_rest():
    with tempfile.TemporaryDirectory() as d:
        tmp = Path(d)
        mod = load(tmp)
        srv, url = serve(mod)
        try:
            code, name = post(url, report())
            assert code == 200
            saved = (tmp / "rapporti" / name).read_text()
            assert "Motivo: prova" in saved and "iOS 27.0.1" in saved
            assert oct((tmp / "rapporti" / name).stat().st_mode & 0o777) == "0o600"
            assert post(url, report(), "text/plain")[0] == 415
            assert post(url, b"{nonjson")[0] == 400
            assert post(url, json.dumps({"app": "altro", "testo": "x"}).encode())[0] == 400
            assert post(url, b"x" * (mod.MAX_BODY + 1))[0] == 413
            with urllib.request.urlopen(url + "/salute") as r:
                assert r.read() == b"ok"
        finally:
            srv.shutdown()


def test_rate_limit_per_address():
    with tempfile.TemporaryDirectory() as d:
        mod = load(Path(d))
        now = time.time()
        assert all(mod._allowed("1.2.3.4", now) for _ in range(mod.PER_IP_HOUR))
        assert not mod._allowed("1.2.3.4", now)
        assert mod._allowed("5.6.7.8", now)
        assert mod._allowed("1.2.3.4", now + 3700)  # passata l'ora


def test_prune_keeps_the_newest():
    with tempfile.TemporaryDirectory() as d:
        tmp = Path(d)
        mod = load(tmp)
        mod.MAX_FILES = 3
        mod.FOLDER.mkdir(parents=True)
        for i in range(5):
            p = mod.FOLDER / f"{i}.txt"
            p.write_text("x")
            os.utime(p, (1000 + i, 1000 + i))
        mod._prune()
        assert sorted(p.name for p in mod.FOLDER.glob("*.txt")) == ["2.txt", "3.txt", "4.txt"]
