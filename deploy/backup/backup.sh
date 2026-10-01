#!/usr/bin/env bash
#
# Backup do Nexora — as quatro peças, cifradas, com cópia fora da máquina.
#
# ==========================================================================================
#  ⚠️ QUATRO PEÇAS, NÃO UMA. Só o banco do Nexora não basta:
#     • sem o database `evolution` + o volume de sessões, todo cliente pareia o WhatsApp de novo;
#     • sem a mídia, os anexos das conversas somem.
#
#  ⚠️ CIFRADO, e não por zelo: os arquivos contêm conversa, telefone e foto de TERCEIROS — gente
#  que nunca ouviu falar do Nexora e não escolheu confiar em nós. Um dump em claro num bucket é o
#  pior vazamento que este sistema pode ter.
#
#  ⚠️ E O SCRIPT NÃO APAGA NADA NO R2. A retenção de 14 dias é regra de ciclo de vida do bucket,
#  configurada lá — uma credencial que pode apagar backup é uma credencial que um invasor usa
#  para apagar backup.
# ==========================================================================================

set -euo pipefail

RETENCAO_LOCAL_DIAS=2     # o disco da VPS é pequeno; local é só a cópia quente
DESTINO=/tmp/backup

: "${BACKUP_SENHA:?BACKUP_SENHA ausente — sem ela o backup sairia em claro}"
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD ausente}"

export PGPASSWORD="$POSTGRES_PASSWORD"
export PGHOST="${PGHOST:-db}"
export PGUSER=postgres

D=$(date +%F-%H%M)
mkdir -p "$DESTINO"

# ⚠️ CIFRA NO FLUXO, sem passar por arquivo em claro. Escrever o dump e cifrar depois deixa a
# versão legível no disco entre os dois comandos — e se o script morrer no meio, ela FICA lá.
cifrar() { openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:BACKUP_SENHA; }

echo "[$(date +%T)] 1/4 database nexora"
pg_dump -Fc nexora    | cifrar > "$DESTINO/nexora-$D.dump.enc"

echo "[$(date +%T)] 2/4 database evolution (as sessões pareadas)"
pg_dump -Fc evolution | cifrar > "$DESTINO/evolution-$D.dump.enc"

echo "[$(date +%T)] 3/4 mídia"
tar czf - -C /dados/midia .                | cifrar > "$DESTINO/midia-$D.tar.gz.enc"

echo "[$(date +%T)] 4/4 credenciais de sessão da Evolution"
tar czf - -C /dados/evolution-instances .  | cifrar > "$DESTINO/evolution-instances-$D.tar.gz.enc"

# ⚠️ SÓ OS DOIS DUMPS, E NÃO OS TARBALLS. A primeira versão checava os quatro arquivos, e o
# ensaio mostrou que isso quebra a instalação NOVA: mídia e pareamento começam vazios, o
# `tar` de um diretório vazio dá ~45 bytes, e o backup falharia a cada 6 horas desde o primeiro
# dia — alertando por nada, que é o jeito mais rápido de a equipe aprender a ignorar
# alarme.
#
# Um dump é diferente: ele sempre carrega o schema, então minúsculo ali é sinal de que algo deu
# errado sem o `pg_dump` reclamar. O tarball vazio é uma informação legítima.
#
# (O caso "o comando falhou" já está coberto: `set -o pipefail` derruba o script inteiro se
# qualquer lado do pipe sair diferente de zero.)
for f in "$DESTINO"/nexora-"$D".dump.enc "$DESTINO"/evolution-"$D".dump.enc; do
    tamanho=$(stat -c%s "$f")
    if [ "$tamanho" -lt 256 ]; then
        echo "ERRO: $f tem $tamanho bytes — dump suspeito, nada foi apagado." >&2
        exit 1
    fi
done

# ---------------------------------------------------------------- fora da máquina
if [ -n "${R2_BUCKET_BACKUP:-}" ]; then
    echo "[$(date +%T)] enviando para o R2"
    export RCLONE_CONFIG_R2_TYPE=s3
    export RCLONE_CONFIG_R2_PROVIDER=Cloudflare
    export RCLONE_CONFIG_R2_ACCESS_KEY_ID="${R2_ACCESS_KEY_ID:?}"
    export RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="${R2_SECRET_ACCESS_KEY:?}"
    export RCLONE_CONFIG_R2_ENDPOINT="https://${R2_ACCOUNT_ID:?}.r2.cloudflarestorage.com"
    # `auto` é a única região do R2, e o rclone precisa de uma.
    export RCLONE_CONFIG_R2_REGION=auto

    rclone copy "$DESTINO" "r2:${R2_BUCKET_BACKUP}/$(date +%Y/%m)/" \
        --include "*-$D.*.enc" --s3-no-check-bucket
else
    echo "AVISO: R2_BUCKET_BACKUP vazio — a cópia existe SÓ NESTA MÁQUINA." >&2
fi

# Expurgo local só DEPOIS do envio: apagar antes deixaria uma janela em que o antigo já foi e o
# novo ainda não chegou.
find "$DESTINO" -name '*.enc' -mtime "+$RETENCAO_LOCAL_DIAS" -delete

# ⚠️ O HEARTBEAT É A ÚLTIMA LINHA, e é de propósito. O monitor externo alerta pela AUSÊNCIA do
# chamado — então qualquer `exit` acima (dump vazio, R2 fora do ar, senha faltando) já avisa
# sozinho, sem precisar de tratamento de erro nenhum.
if [ -n "${BACKUP_PUSH_URL:-}" ]; then
    curl -fsS --max-time 10 "$BACKUP_PUSH_URL" > /dev/null || \
        echo "AVISO: heartbeat não foi entregue (o backup em si deu certo)." >&2
fi

echo "[$(date +%T)] pronto. Local ${RETENCAO_LOCAL_DIAS}d; remoto pela regra do bucket (14d)."
