#!/usr/bin/env bash
#
# Sobe ou atualiza a produção. Roda de /opt/nexora.
#
# ⚠️ ELE APLICA AS MIGRATIONS ANTES DE SUBIR A API, e isso é o oposto do arranjo anterior, onde
# era passo manual. O motivo está registrado na tabela de limites do README antigo: "um deploy
# que esqueça o passo 8 sobe a API contra schema velho" — e esquecer era silencioso.
#
# Como não há registry, a imagem é construída aqui mesmo; o SDK já vem no estágio de build, então
# gerar o script de migração custa quase nada.

set -euo pipefail

cd "$(dirname "$0")"
COMPOSE="docker compose --env-file .env"

echo "== 1/5 build =="
$COMPOSE build

echo "== 2/5 banco de pé =="
$COMPOSE up -d db
$COMPOSE exec -T db sh -c 'until pg_isready -U postgres -q; do sleep 1; done'

echo "== 3/5 migrations =="
./migrate.sh

echo "== 4/5 subindo o resto =="
$COMPOSE up -d --remove-orphans

echo "== 5/5 esperando a API responder =="
# ⚠️ ESPERA O /health, e não o contêiner existir. `docker compose up` volta assim que o processo
# arranca; a API ainda leva segundos para abrir conexão com o banco. Sem esta espera, o deploy
# "termina com sucesso" e o primeiro acesso dá erro.
# A imagem do curl é usada porque a de runtime do .NET não traz curl nem wget — decisão
# registrada no `Dockerfile`, e o motivo de não haver `HEALTHCHECK` lá dentro.
for i in $(seq 1 45); do
    if docker run --rm --network nexora_borda curlimages/curl:8.11.1            -fsS --max-time 5 http://nexora-api:8080/health > /dev/null 2>&1; then
        echo "API respondendo depois de $(( i * 2 ))s."
        docker image prune -f > /dev/null
        exit 0
    fi
    sleep 2
done

echo "ERRO: a API não respondeu em 90s. Log:" >&2
$COMPOSE logs --tail 60 api >&2
exit 1
