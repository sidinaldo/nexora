#!/usr/bin/env bash
#
# Backup do Nexora — as quatro peças, cifradas, com cópia fora da máquina.
#
# =====================================================================================
#  POR QUE ESTE ARQUIVO EXISTE
#
#  O `README` sempre teve os comandos, escritos para serem digitados à mão. Isso vale
#  enquanto os dados são seus. Com cliente real na base, backup que depende de alguém
#  lembrar não é backup: é uma intenção.
#
#  ⚠️ E QUATRO PEÇAS, NÃO UMA. Só o banco do Nexora não basta:
#     • sem o banco da Evolution + o volume de sessões, todo cliente pareia de novo;
#     • sem a mídia, os anexos das conversas somem.
#
#  ⚠️ CIFRADO, e não por zelo: os arquivos contêm conversa, telefone e foto de
#  TERCEIROS — gente que nunca ouviu falar do Nexora e não escolheu confiar em nós.
#  Um dump em claro num bucket é o pior vazamento que este sistema pode ter.
# =====================================================================================
#
# INSTALAÇÃO
#   cp deploy/backup.sh /opt/nexora/backup.sh && chmod +x /opt/nexora/backup.sh
#   crontab -e
#   15 3 * * * /opt/nexora/backup.sh >> /var/log/nexora-backup.log 2>&1
#
# O QUE ELE LÊ DO .env.prod (além do que o compose já usa)
#   BACKUP_SENHA          obrigatória — a chave da cifragem. SEM ELA NÃO RODA.
#   BACKUP_S3_BUCKET      opcional — sem ela, só guarda local e AVISA
#   BACKUP_S3_ENDPOINT    opcional — para Backblaze/Wasabi; vazio = AWS S3
#   AWS_ACCESS_KEY_ID     credencial do bucket (lida pelo aws-cli)
#   AWS_SECRET_ACCESS_KEY
#
# ⚠️ GUARDE A `BACKUP_SENHA` FORA DESTA MÁQUINA. Ela vive no `.env.prod`, que está no
# disco que o backup existe para sobreviver. Senha só aqui = backup ilegível no dia em
# que ele for preciso.

set -euo pipefail

# ---------------------------------------------------------------- retenção, declarada
# O checklist de hospedagem pede retenção declarada, e "declarada" quer dizer um número
# que alguém escolheu — não "o que couber no disco".
RETENCAO_LOCAL_DIAS=7      # o disco é pequeno; local é só a cópia quente
RETENCAO_REMOTA_DIAS=90    # no bucket, o que responde a "como estava mês passado?"

RAIZ=/opt/nexora
DESTINO=/opt/backup

cd "$RAIZ"
set -a; . ./.env.prod; set +a

nx() { docker compose -f docker-compose.prod.yml --env-file .env.prod "$@"; }

: "${BACKUP_SENHA:?BACKUP_SENHA não está no .env.prod — sem ela o backup sairia em claro}"

D=$(date +%F-%H%M)
mkdir -p "$DESTINO"

# ⚠️ CIFRA NO FLUXO, sem passar por arquivo em claro. Escrever o dump e cifrar depois
# deixa a versão legível no disco entre os dois comandos — e se o script morrer no meio,
# ela FICA lá.
cifrar() { openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:BACKUP_SENHA; }

echo "[$(date +%T)] 1/4 banco do Nexora"
nx exec -T db pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" \
  | cifrar > "$DESTINO/nexora-$D.dump.enc"

echo "[$(date +%T)] 2/4 banco da Evolution (a sessão pareada)"
nx exec -T evolution_db pg_dump -U evolution -Fc evolution \
  | cifrar > "$DESTINO/evolution-$D.dump.enc"

echo "[$(date +%T)] 3/4 mídia"
docker run --rm -v nexora_midia_prod:/d alpine tar czf - -C /d . \
  | cifrar > "$DESTINO/midia-$D.tar.gz.enc"

echo "[$(date +%T)] 4/4 credenciais de sessão da Evolution"
docker run --rm -v nexora_evolution_instances_prod:/d alpine tar czf - -C /d . \
  | cifrar > "$DESTINO/evolution-instances-$D.tar.gz.enc"

# ⚠️ UM DUMP VAZIO NÃO É ERRO PARA O `pg_dump`, mas é um backup inútil. Sem esta
# checagem o cron reportaria sucesso para quatro arquivos de zero byte, e ninguém
# descobriria até o dia de restaurar.
for f in "$DESTINO"/*-"$D".*.enc; do
  tamanho=$(stat -c%s "$f")
  if [ "$tamanho" -lt 1024 ]; then
    echo "ERRO: $f tem $tamanho bytes — backup suspeito, não apagando nada." >&2
    exit 1
  fi
done

# ---------------------------------------------------------------- fora da máquina
# Backup no mesmo disco não protege contra perder o disco — que é o caso que ele existe
# para cobrir.
if [ -n "${BACKUP_S3_BUCKET:-}" ]; then
  echo "[$(date +%T)] enviando para $BACKUP_S3_BUCKET"
  ENDPOINT=${BACKUP_S3_ENDPOINT:+--endpoint-url $BACKUP_S3_ENDPOINT}
  for f in "$DESTINO"/*-"$D".*.enc; do
    # shellcheck disable=SC2086
    aws s3 cp "$f" "s3://$BACKUP_S3_BUCKET/$(date +%Y/%m)/$(basename "$f")" $ENDPOINT
  done
else
  echo "AVISO: BACKUP_S3_BUCKET vazio — a cópia existe SÓ NESTA MÁQUINA." >&2
fi

# ---------------------------------------------------------------- expurgo local
# Só DEPOIS do envio: apagar antes deixaria uma janela em que o antigo já foi e o novo
# ainda não chegou.
find "$DESTINO" -name '*.enc' -mtime "+$RETENCAO_LOCAL_DIAS" -delete

echo "[$(date +%T)] pronto. Retenção local ${RETENCAO_LOCAL_DIAS}d, remota ${RETENCAO_REMOTA_DIAS}d (regra do bucket)."
echo "⚠️ A retenção remota é regra de CICLO DE VIDA do bucket, configurada lá — não neste script."
