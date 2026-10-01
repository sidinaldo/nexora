#!/usr/bin/env bash
#
# Bate o heartbeat no Uptime Kuma ENQUANTO o disco estiver saudável.
#
#   */5 * * * * DISK_PUSH_URL=https://hc-ping.com/SEU-UUID /opt/nexora/deploy/host/disk-check.sh
#
# A variável vai NA LINHA DO CRON, e não no `deploy/.env`: aquele arquivo é do compose, e este
# script roda no host, fora de contêiner. Sem ela o `:?` abaixo mata o script no primeiro ciclo
# — o que, pela lógica invertida, até avisa: o heartbeat para de chegar.
#
# ⚠️ A LÓGICA É INVERTIDA DE PROPÓSITO, e é o que faz isto funcionar sem servidor de alerta.
#
# Ele chama a URL quando está TUDO BEM e fica calado quando não está. Quem alerta é o Kuma, pela
# AUSÊNCIA do chamado — então o alarme também dispara se a VPS estiver fora do ar, se o cron
# morrer, ou se o disco encher a ponto de o próprio script não rodar.
#
# Um script que "avisa quando dá problema" não avisa quando o problema é ele mesmo.

set -euo pipefail

: "${DISK_PUSH_URL:?defina DISK_PUSH_URL no ambiente ou no crontab}"

USO=$(df --output=pcent / | tail -1 | tr -dc '0-9')

if [ "$USO" -lt 80 ]; then
    curl -fsS --max-time 10 "$DISK_PUSH_URL" > /dev/null
else
    echo "Disco em ${USO}% — heartbeat NÃO enviado de propósito. O Kuma vai alertar." >&2
    exit 1
fi
