#!/usr/bin/env bash
#
# restore.sh <nexora|evolution> <arquivo-no-r2>
#
# Exemplo:
#   docker compose exec backup restore.sh nexora 2026/09/nexora-2026-09-30-0610.dump.enc
#
# ==========================================================================================
#  ⚠️ ESTE SCRIPT É PARA SER RODADO ANTES DE PRECISAR DELE.
#
#  Backup que nunca foi restaurado é um arquivo, não um backup. A restauração nunca tinha sido
#  exercitada neste projeto — está registrado no `docs/INF-1.md` —, e o item continua obrigatório
#  antes do primeiro cliente.
#
#  ⚠️ E NÃO RESTAURE EM CIMA DA PRODUÇÃO PARA TESTAR. O `--clean` APAGA os objetos antes de
#  recriar. Para o ensaio, crie um database descartável e passe o nome dele.
# ==========================================================================================

set -euo pipefail

BANCO="${1:?uso: restore.sh <database> <arquivo-no-r2>}"
ARQUIVO="${2:?uso: restore.sh <database> <arquivo-no-r2>}"

: "${BACKUP_SENHA:?BACKUP_SENHA ausente — sem ela o arquivo não decifra}"
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD ausente}"

export PGPASSWORD="$POSTGRES_PASSWORD"
export PGHOST="${PGHOST:-db}"
export PGUSER=postgres

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

export RCLONE_CONFIG_R2_TYPE=s3
export RCLONE_CONFIG_R2_PROVIDER=Cloudflare
export RCLONE_CONFIG_R2_ACCESS_KEY_ID="${R2_ACCESS_KEY_ID:?}"
export RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="${R2_SECRET_ACCESS_KEY:?}"
export RCLONE_CONFIG_R2_ENDPOINT="https://${R2_ACCOUNT_ID:?}.r2.cloudflarestorage.com"
export RCLONE_CONFIG_R2_REGION=auto

echo "Baixando $ARQUIVO"
rclone copyto "r2:${R2_BUCKET_BACKUP:?}/${ARQUIVO}" "$TMP/backup.enc"

echo "Decifrando"
openssl enc -d -aes-256-cbc -pbkdf2 -pass env:BACKUP_SENHA \
    -in "$TMP/backup.enc" -out "$TMP/backup.dump"

# ⚠️ CONFERE ANTES DE DESTRUIR. Senha errada não faz o `openssl` falhar de um jeito óbvio em todo
# caso — ele pode produzir lixo. Um dump do `pg_dump -Fc` começa com a assinatura "PGDMP"; sem
# ela, o `--clean` teria apagado o database para depois falhar ao restaurar.
if ! head -c 5 "$TMP/backup.dump" | grep -q PGDMP; then
    echo "ERRO: o arquivo decifrado não é um dump do Postgres. Senha errada? Nada foi tocado." >&2
    exit 1
fi

echo "Restaurando em '$BANCO' (--clean apaga os objetos antes de recriar)"
pg_restore --clean --if-exists --no-owner --no-privileges -d "$BANCO" "$TMP/backup.dump"

echo "Pronto. Confira a contagem de tabelas antes de confiar:"
echo "  psql -d $BANCO -c \"select count(*) from information_schema.tables where table_schema='public';\""
