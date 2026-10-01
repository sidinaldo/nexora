#!/usr/bin/env bash
#
# Cria um cliente novo: empresa, usuário dono, funil padrão e a conexão de WhatsApp.
#
#   ./criar-empresa.sh
#
# ==========================================================================================
#  ⚠️ NÃO EXISTE TELA PARA ISTO, E É DE PROPÓSITO.
#
#  O Nexora não tem auto-cadastro aberto: ninguém cria empresa pela internet. Quem cria cliente
#  é o operador, com a `CADASTRO_CHAVE_ADMIN` — e essa chave deve ser ESVAZIADA no `.env` quando
#  a fase de cadastro terminar. Com ela vazia, o endpoint recusa tudo.
#
#  ⚠️ FALA COM A API POR DENTRO DA REDE do Docker, não pelo domínio público. Dois motivos: o
#  cadastro continua funcionando com o túnel fora do ar, e a chave de administração não atravessa
#  a internet nem aparece em log de borda.
# ==========================================================================================

set -euo pipefail

cd "$(dirname "$0")"
set -a; . ./.env; set +a

: "${CADASTRO_CHAVE_ADMIN:?CADASTRO_CHAVE_ADMIN vazia no .env — o cadastro está DESLIGADO.
   Preencha para criar clientes, e esvazie de novo quando terminar.}"

perguntar() {
    local rotulo="$1" var="$2" valor=""
    while [ -z "$valor" ]; do
        printf '%s: ' "$rotulo" >&2
        IFS= read -r valor || true
        valor=$(printf '%s' "$valor" | tr -d '\r' | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')
        [ -n "$valor" ] || echo "  (obrigatório)" >&2
    done
    printf -v "$var" '%s' "$valor"
}

echo "== cliente novo =="
perguntar "Nome da empresa      " NOME
perguntar "Nome do responsável  " DONO
perguntar "E-mail do responsável" MAIL

printf 'CNPJ (opcional, Enter pula): ' >&2
IFS= read -r DOC || true
DOC=$(printf '%s' "${DOC:-}" | tr -d '\r' | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')

# ⚠️ SENHA OCULTA E CONFERIDA DUAS VEZES. Ela não aparece na tela, então um erro de digitação só
# apareceria no primeiro login do cliente — e aí já é constrangimento, não bug.
while :; do
    printf 'Senha inicial (mín. 8, não aparece): ' >&2; IFS= read -rs SENHA || true; echo >&2
    printf 'Repita a senha                     : ' >&2; IFS= read -rs SENHA2 || true; echo >&2
    [ "${#SENHA}" -ge 8 ] || { echo "  mínimo 8 caracteres." >&2; continue; }
    [ "$SENHA" = "$SENHA2" ] || { echo "  as duas não batem." >&2; continue; }
    break
done

# O JSON é montado em Python para o nome com acento, aspas ou barra invertida não quebrar o corpo
# — e vai por STDIN, nunca por argumento, para a senha não aparecer num `ps`.
CORPO=$(NOME="$NOME" DOC="$DOC" DONO="$DONO" MAIL="$MAIL" SENHA="$SENHA" python3 -c '
import json, os
print(json.dumps({
    "nome":      os.environ["NOME"],
    "documento": os.environ["DOC"] or None,
    "nomeDono":  os.environ["DONO"],
    "emailDono": os.environ["MAIL"],
    "senha":     os.environ["SENHA"],
}))')

echo >&2
echo "Criando..." >&2
RESPOSTA=$(printf '%s' "$CORPO" | docker run --rm -i --network nexora_borda curlimages/curl:8.11.1 \
    -sS --max-time 20 -w '\n%{http_code}' \
    -X POST http://nexora-api:8080/api/cadastro/empresa \
    -H 'Content-Type: application/json' \
    -H "X-Chave-Admin: ${CADASTRO_CHAVE_ADMIN}" \
    --data-binary @-)

CODIGO=$(printf '%s' "$RESPOSTA" | tail -1)
CORPO_RESP=$(printf '%s' "$RESPOSTA" | sed '$d')

case "$CODIGO" in
    200|201)
        echo
        echo "Empresa criada: $CORPO_RESP"
        echo
        echo "O que já existe para ela: usuário dono ($MAIL), funil com as 5 etapas padrão,"
        echo "e uma conexão de WhatsApp ainda NÃO pareada."
        echo
        echo "Próximo passo: o dono entra em ${NEXORA_PUBLIC_PANEL_URL:-o painel} e lê o QR."
        ;;
    401)
        echo "RECUSADO (401): a CADASTRO_CHAVE_ADMIN do .env não confere com a que a API carregou." >&2
        echo "Se você mudou o .env agora, a API precisa ser recriada para enxergar." >&2
        exit 1 ;;
    409)
        echo "CONFLITO (409): $CORPO_RESP" >&2
        echo "Normalmente é e-mail já usado por outro usuário, em QUALQUER empresa." >&2
        exit 1 ;;
    *)
        echo "FALHOU (HTTP $CODIGO): $CORPO_RESP" >&2
        exit 1 ;;
esac
