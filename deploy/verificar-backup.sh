#!/usr/bin/env bash
#
# Confere que o destino do backup está alcançável e que há cópia recente LÁ, não aqui.
#
# ==========================================================================================
#  ⚠️ ESTE SCRIPT EXISTE POR CAUSA DE UM INCIDENTE REAL.
#
#  Um `R2_ACCOUNT_ID` com dois caracteres trocados de lugar passou pelo deploy sem um aviso. O
#  `backup.sh` fez a coisa certa — saiu com erro e não chamou o heartbeat —, mas o
#  `BACKUP_PUSH_URL` estava vazio, então o erro não tinha para quem reclamar. O dump existiu por
#  12 horas SÓ no disco que o backup serve justamente para sobreviver.
#
#  O erro era invisível de propósito, não por descuido: o endpoint do R2 é
#  `<conta>.r2.cloudflarestorage.com`, e a Cloudflare recusa o TLS de um subdomínio que não
#  conhece ANTES de apresentar certificado. O cliente vê `tls: handshake failure` — que parece
#  problema de rede, e não "você digitou o ID errado".
#
#  Daí a checagem ser de ponta a ponta e não de forma: 32 dígitos hexadecimais podem estar
#  perfeitamente bem formados e simplesmente não existir.
# ==========================================================================================

set -euo pipefail

cd "$(dirname "$0")"
set -a; . ./.env; set +a

if [ -z "${R2_BUCKET_BACKUP:-}" ]; then
    echo "AVISO: R2_BUCKET_BACKUP vazio — o backup fica SÓ NESTA MÁQUINA. Nada a verificar." >&2
    exit 0
fi

: "${R2_ACCOUNT_ID:?R2_ACCOUNT_ID ausente}"
: "${R2_ACCESS_KEY_ID:?R2_ACCESS_KEY_ID ausente}"
: "${R2_SECRET_ACCESS_KEY:?R2_SECRET_ACCESS_KEY ausente}"

# Roda dentro do contêiner de backup porque é o rclone DELE que faz a cópia de verdade.
# Verificar com outra ferramenta, de outro lugar, verificaria outra coisa.
r2() {
    docker exec \
        -e RCLONE_CONFIG_R2_TYPE=s3 \
        -e RCLONE_CONFIG_R2_PROVIDER=Cloudflare \
        -e RCLONE_CONFIG_R2_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID" \
        -e RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY" \
        -e RCLONE_CONFIG_R2_ENDPOINT="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com" \
        -e RCLONE_CONFIG_R2_REGION=auto \
        nexora_backup rclone "$@" --retries 2 --low-level-retries 2 --contimeout 15s --timeout 30s
}

falhar() {
    cat >&2 <<AVISO

  ============================================================================
   O BACKUP NÃO ESTÁ CHEGANDO NO R2.  $1
  ============================================================================
   O sistema está no ar; o que está quebrado é a cópia de segurança.

   Por onde começar, na ordem em que as causas aparecem:

     1. R2_ACCOUNT_ID trocado.  É a causa que já aconteceu. Confira contra o
        painel (R2 Object Storage -> Overview -> Account ID). O ID da conta
        também está dentro do token do Tunnel, no campo "a":

          grep ^CLOUDFLARE_TUNNEL_TOKEN= .env | cut -d= -f2- | base64 -d

     2. R2_BUCKET_BACKUP com nome que não existe, ou de outra conta.
     3. A credencial sem acesso a este bucket, ou revogada.

   Para ver o erro cru:

     ./verificar-backup.sh
  ============================================================================

AVISO
    exit 1
}

# ⚠️ FALA COM O BUCKET, e nunca `lsd r2:` para "ver se responde": listar buckets é permissão de
# CONTA, e o token certo não a tem. A primeira versão desta checagem fazia isso e acusou backup
# quebrado num sistema saudável — o `AccessDenied` ali era a credencial bem feita, não o defeito.
# Least privilege quebra o teste preguiçoso, e o teste é que estava errado.
echo "-- o bucket '$R2_BUCKET_BACKUP' responde?"
r2 size "r2:${R2_BUCKET_BACKUP}" > /tmp/r2.size 2>/tmp/r2.err || {
    sed 's/^/   /' /tmp/r2.err >&2
    grep -q "handshake failure" /tmp/r2.err && falhar "(TLS recusado — quase sempre o ACCOUNT_ID)"
    grep -q "NoSuchBucket"      /tmp/r2.err && falhar "(bucket '$R2_BUCKET_BACKUP' não existe nesta conta)"
    # ⚠️ Com token de escopo num bucket só, "nome errado" e "sem permissão" devolvem o MESMO 403:
    # o R2 não conta que o bucket existe para quem não pode vê-lo. Dizer as duas causas é honesto;
    # apostar numa delas manda procurar no lugar errado.
    grep -q "AccessDenied"      /tmp/r2.err && falhar "(nome do bucket errado, ou credencial sem acesso a ele)"
    falhar "(veja o erro acima)"
}
echo "   ok: ${R2_ACCOUNT_ID}.r2.cloudflarestorage.com, $(grep -oE 'Total objects:.*' /tmp/r2.size | head -1 | tr -s ' ')"

# ⚠️ CONFERE A IDADE, e não só a existência. Um backup de três semanas num bucket que responde
# bem é a falha mais traiçoeira que existe: tudo parece certo, e o que se perde é o mês.
#
# ⚠️ E A IDADE É CONTADA PELO `--max-age` DO RCLONE, de propósito. A primeira versão subtraía
# `date` do timestamp impresso, e aquilo só funcionava por coincidência: a VPS está em UTC-3 e o
# `rclone` imprime hora local, então os dois lados batiam por acidente. Num servidor em UTC — ou
# se o `rclone` mudar de ideia sobre que fuso imprime — a mesma conta erraria em 3 horas e daria
# alarme falso perto do limite. Quem sabe a hora do objeto é quem guardou o objeto.
#
# 30h, e não 24h: o cron roda a cada 6h, e atraso de uma rodada não é incidente.
echo "-- tem dump do 'nexora' das últimas 30 horas?"
dumps() { grep 'nexora-' | grep 'dump.enc' || true; }

TODOS=$(r2 lsf -R --format tp "r2:${R2_BUCKET_BACKUP}" 2>/dev/null | dumps | sort)
[ -n "$TODOS" ] || falhar "(o bucket responde, mas não há NENHUM dump do nexora dentro)"
echo "   mais novo: $(echo "$TODOS" | tail -1 | tr ';' ' ')"
echo "   ao todo:   $(echo "$TODOS" | wc -l) dump(s) do nexora"

RECENTE=$(r2 lsf -R --max-age 30h "r2:${R2_BUCKET_BACKUP}" 2>/dev/null | dumps)
[ -n "$RECENTE" ] || falhar "(há dump no bucket, mas NENHUM das últimas 30h — o cron roda a cada 6h)"
echo "   ok: $(echo "$RECENTE" | wc -l) nas últimas 30h"

echo
echo "Backup chegando no R2."

# ⚠️ E O QUE ISTO *NÃO* PROVA: que o arquivo restaura. Backup que nunca foi restaurado é um
# arquivo, não um backup — o ensaio está no `docs/INF-1.md`, e repeti-lo é trabalho de gente, não
# de script, porque exige olhar o dado restaurado e reconhecê-lo.
