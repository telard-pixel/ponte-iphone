#!/usr/bin/env python3
"""Ricevitore dei rapporti di Ponte iPhone (server-log.duckdns.org, dietro Caddy).

Scelta dell'utente del 14/10/2026: quando l'app incontra un problema manda da sola il suo rapporto
(diagnostica + registro, niente foto), invece di chiedergli di salvarlo e spedirlo.

Solo ricezione: nessuna pagina, nessuna lettura dei rapporti da fuori (si leggono via SSH nella
cartella di stato). Niente chiavi nell'app (il repository è pubblico), quindi i limiti stanno qui:
dimensione massima, rapporti per indirizzo all'ora e al giorno, spazio totale. Un avviso breve
sul bot del server, con un limite suo.
Solo libreria standard. Configurazione da variabili d'ambiente e credenziali systemd.
"""
import json
import os
import re
import secrets
import time
import urllib.parse
import urllib.request
from collections import defaultdict, deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock

PORT = int(os.environ.get("PORTA", "8084"))
STATE = Path(os.environ.get("STATE_DIRECTORY", "/var/lib/ponte-rapporti"))
FOLDER = STATE / "rapporti"
CRED = Path(os.environ.get("CREDENTIALS_DIRECTORY", "/etc/ponte-rapporti"))
CHAT_ID = os.environ.get("CHAT_ID", "")

MAX_BODY = 600_000            # byte per rapporto (l'app ne manda al massimo ~400k caratteri)
PER_IP_HOUR = 12
PER_IP_DAY = 40
ALL_DAY = 300
MAX_FILES = 2000
MAX_TOTAL = 300 * 1024 * 1024  # 300 MB
ALERTS_PER_HOUR = 6

_lock = Lock()
_by_ip = defaultdict(deque)   # ip -> tempi degli ultimi rapporti
_all = deque()
_alerts = deque()


def _clean(text, limit):
    text = re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", "", str(text))
    return text[:limit]


def _allowed(ip, now):
    """Limiti per indirizzo e totali (finestre scorrevoli). True se il rapporto si può accettare."""
    with _lock:
        q = _by_ip[ip]
        while q and now - q[0] > 86400:
            q.popleft()
        while _all and now - _all[0] > 86400:
            _all.popleft()
        last_hour = sum(1 for t in q if now - t < 3600)
        if last_hour >= PER_IP_HOUR or len(q) >= PER_IP_DAY or len(_all) >= ALL_DAY:
            return False
        q.append(now)
        _all.append(now)
        return True


def _prune():
    """Tiene la cartella entro numero di file e spazio: via i più vecchi."""
    files = sorted(FOLDER.glob("*.txt"), key=lambda p: p.stat().st_mtime)
    total = sum(p.stat().st_size for p in files)
    while files and (len(files) > MAX_FILES or total > MAX_TOTAL):
        old = files.pop(0)
        total -= old.stat().st_size
        old.unlink(missing_ok=True)


def _summary(text):
    """Telefono e Windows dal rapporto, per l'avviso (le prime righe della diagnostica)."""
    found = {}
    for line in text.splitlines()[:40]:
        for key in ("iPhone", "Stato", "Windows"):
            if line.startswith(key + " ") and key not in found:
                found[key] = line[len(key):].strip()
    return found


def _alert(message):
    token_file = CRED / "bot-token"
    if not CHAT_ID or not token_file.exists():
        return
    now = time.time()
    with _lock:
        while _alerts and now - _alerts[0] > 3600:
            _alerts.popleft()
        if len(_alerts) >= ALERTS_PER_HOUR:
            return
        _alerts.append(now)
    token = token_file.read_text().strip()
    data = urllib.parse.urlencode({"chat_id": CHAT_ID, "text": message}).encode()
    try:
        urllib.request.urlopen(f"https://api.telegram.org/bot{token}/sendMessage", data=data, timeout=10).close()
    except OSError:
        pass


def save_report(payload, ip):
    """Salva un rapporto valido e restituisce il nome del file."""
    reason = _clean(payload.get("motivo", ""), 200) or "senza motivo"
    version = _clean(payload.get("versione", ""), 40)
    text = _clean(payload.get("testo", ""), MAX_BODY)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    name = f"{stamp}-{secrets.token_hex(3)}.txt"
    FOLDER.mkdir(parents=True, exist_ok=True)
    path = FOLDER / name
    header = f"Ricevuto: {time.strftime('%Y-%m-%d %H:%M:%S')}\nDa: {ip}\nVersione: {version}\nMotivo: {reason}\n\n"
    path.write_text(header + text, encoding="utf-8")
    os.chmod(path, 0o600)
    _prune()
    info = _summary(text)
    phone = info.get("iPhone", "iPhone ?")
    _alert(f"📱 Ponte iPhone {version}: {reason}\n{phone}\nRapporto: {name}")
    return name


class Handler(BaseHTTPRequestHandler):
    server_version = "rapporti"
    sys_version = ""

    def _send(self, code, body=b""):
        self.send_response(code)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def _ip(self):
        # Dietro Caddy: l'indirizzo vero è in X-Forwarded-For (Caddy lo imposta lui).
        forwarded = self.headers.get("X-Forwarded-For", "")
        return forwarded.split(",")[0].strip() or self.client_address[0]

    def do_GET(self):
        self._send(200, b"ok") if self.path == "/salute" else self._send(404)

    def do_POST(self):
        if self.path != "/api/rapporto":
            return self._send(404)
        if "application/json" not in self.headers.get("Content-Type", ""):
            return self._send(415)
        length = self.headers.get("Content-Length")
        if length is None:
            return self._send(411)
        try:
            n = int(length)
        except ValueError:
            return self._send(400)
        if n <= 0 or n > MAX_BODY:
            return self._send(413)
        body = self.rfile.read(n)
        ip = self._ip()
        if not _allowed(ip, time.time()):
            return self._send(429)
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return self._send(400)
        if not isinstance(payload, dict) or payload.get("app") != "ponte-iphone" or not payload.get("testo"):
            return self._send(400)
        name = save_report(payload, ip)
        self._send(200, name.encode())

    def log_message(self, fmt, *args):  # niente registro di ogni richiesta (lo fa già Caddy)
        pass


def main():
    FOLDER.mkdir(parents=True, exist_ok=True)
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
