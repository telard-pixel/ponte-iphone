#!/bin/bash
# Installa (o aggiorna) il ricevitore dei rapporti di Ponte iPhone su rob-label-new.
# Rilanciabile: copia lo script, il servizio, aggiunge il blocco Caddy solo se manca.
# Uso: sudo bash installa.sh   (dalla cartella server/ del repository)
set -euo pipefail
cd "$(dirname "$0")"
HOST=server-log.duckdns.org

install -d -m 0755 /opt/ponte-rapporti
install -m 0644 rapporti_ponte.py /opt/ponte-rapporti/rapporti_ponte.py
install -m 0644 ponte-rapporti.service /etc/systemd/system/ponte-rapporti.service

# Token del bot del server (lo stesso di node-health), solo per questo servizio.
install -d -m 0700 /etc/ponte-rapporti
if [ ! -s /etc/ponte-rapporti/bot-token ]; then
    grep -E '^NODE_HEALTH_BOT_TOKEN=' /root/node-health/bot.env | cut -d= -f2- | tr -d '"' > /etc/ponte-rapporti/bot-token
    chmod 0600 /etc/ponte-rapporti/bot-token
fi

# Caddy: blocco a parte. Il file di log va creato PRIMA di validare, altrimenti il reload fallisce.
LOG=/var/log/caddy/serverlog-access.log
[ -e "$LOG" ] || { touch "$LOG"; chown caddy:caddy "$LOG"; chmod 0640 "$LOG"; }
if ! grep -q "^$HOST {" /etc/caddy/Caddyfile; then
    cp /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.prima-rapporti-$(date +%Y%m%d-%H%M%S)"
    cat >> /etc/caddy/Caddyfile <<CADDY

$HOST {
    # Rapporti di Ponte iPhone (14/10/2026): solo ricezione, servizio a parte.
    header {
        Strict-Transport-Security "max-age=31536000"
        X-Content-Type-Options "nosniff"
        -Server
    }
    request_body {
        max_size 700KB
    }
    @rapporto {
        method POST
        path /api/rapporto
    }
    handle @rapporto {
        reverse_proxy 127.0.0.1:8084
    }
    handle /salute {
        reverse_proxy 127.0.0.1:8084
    }
    handle {
        respond 404
    }
    log {
        output file $LOG {
            roll_size 10mb
            roll_keep 5
        }
    }
}
CADDY
fi
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
systemctl daemon-reload
systemctl enable --now ponte-rapporti
systemctl restart ponte-rapporti
systemctl reload caddy
sleep 1
curl -fsS http://127.0.0.1:8084/salute && echo " <- servizio pronto"
