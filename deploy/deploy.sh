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

echo "== 1/6 build =="
$COMPOSE build

echo "== 2/6 banco de pé =="
$COMPOSE up -d db
$COMPOSE exec -T db sh -c 'until pg_isready -U postgres -q; do sleep 1; done'

echo "== 3/6 migrations =="
./migrate.sh

echo "== 4/6 subindo o resto =="
$COMPOSE up -d --remove-orphans

echo "== 5/6 esperando a API responder =="
# ⚠️ ESPERA O /health, e não o contêiner existir. `docker compose up` volta assim que o processo
# arranca; a API ainda leva segundos para abrir conexão com o banco. Sem esta espera, o deploy
# "termina com sucesso" e o primeiro acesso dá erro.
# A imagem do curl é usada porque a de runtime do .NET não traz curl nem wget — decisão
# registrada no `Dockerfile`, e o motivo de não haver `HEALTHCHECK` lá dentro.
NO_AR=nao
for i in $(seq 1 45); do
    if docker run --rm --network nexora_borda curlimages/curl:8.11.1            -fsS --max-time 5 http://nexora-api:8080/health > /dev/null 2>&1; then
        echo "API respondendo depois de $(( i * 2 ))s."
        NO_AR=sim
        break
    fi
    sleep 2
done

if [ "$NO_AR" = nao ]; then
    echo "ERRO: a API não respondeu em 90s. Log:" >&2
    $COMPOSE logs --tail 60 api >&2
    exit 1
fi

docker image prune -f > /dev/null

# ⚠️ ESTE PASSO EXISTE PORQUE UM DEPLOY JÁ DECLAROU SUCESSO COM O BACKUP QUEBRADO.
#
# Um `R2_ACCOUNT_ID` com dois caracteres trocados passou daqui sem um aviso, e o dump ficou 12
# horas só no disco local — o disco que o backup serve justamente para sobreviver. O detalhe
# cruel: o `backup.sh` falhou direito, mas o `BACKUP_PUSH_URL` estava vazio, então o erro não
# tinha para quem reclamar.
#
# Roda por último de propósito: se ele falhar, a API já está no ar e nada está fora. O `exit 1`
# não é para derrubar nada — é para o deploy não dizer "pronto" quando a cópia de segurança
# está quebrada. Um deploy assim é um deploy que mente.
echo "== 6/6 o backup está chegando no R2? =="
./verificar-backup.sh
