#!/usr/bin/env bash
#
# definir-segredo.sh <NOME_DA_VARIAVEL>
#
# Pede o valor sem mostrar na tela e grava no `deploy/.env`.
#
#   ./definir-segredo.sh EMAIL_SENHA
#
# ==========================================================================================
#  ⚠️ PARA O SEGREDO NÃO PASSAR POR LUGAR NENHUM QUE GUARDE HISTÓRICO.
#
#  Colar um segredo numa conversa, num chamado ou num e-mail cria uma cópia que você não
#  controla mais — foi assim que o token do Tunnel e a chave do R2 deste projeto precisaram ser
#  considerados vazados. E passar na linha de comando (`VAR=segredo ./script`) não resolve: fica
#  no `~/.bash_history` e aparece inteiro para quem rodar `ps` no momento certo.
#
#  Aqui o valor é lido com o eco desligado, vai direto para o arquivo, e não encosta no
#  histórico nem na lista de processos.
# ==========================================================================================

set -euo pipefail

cd "$(dirname "$0")"
CHAVE="${1:?uso: ./definir-segredo.sh NOME_DA_VARIAVEL}"

case "$CHAVE" in
    [A-Z_][A-Z0-9_]*) ;;
    *) echo "ERRO: '$CHAVE' não parece nome de variável (MAIÚSCULAS e _)." >&2; exit 1 ;;
esac

[ -f .env ] || { echo "ERRO: .env não encontrado em $(pwd)." >&2; exit 1; }

printf 'Valor de %s (não aparece na tela, cole e dê Enter): ' "$CHAVE" >&2
# ⚠️ O `|| true` cobre o fim-de-arquivo SEM quebra de linha. O `read` devolve 1 nesse caso mesmo
# tendo lido o valor inteiro, e com `set -e` o script morreria antes de gravar — calado, porque
# ninguém lê código de saída de `read`. Acontece sempre que a entrada vem por cano em vez de
# teclado, que é exatamente como um teste automatizado chama isto. O valor vazio ainda é pego
# logo abaixo.
IFS= read -rs VALOR || true
echo >&2

[ -n "$VALOR" ] || { echo "ERRO: valor vazio. Nada foi alterado." >&2; exit 1; }

case "$VALOR" in
    *$'\n'*|*$'\r'*) echo "ERRO: o valor tem quebra de linha — cole só o segredo." >&2; exit 1 ;;
esac

# ⚠️ ESCRITO EM PYTHON, e não com `sed`, de propósito: um segredo pode conter `&`, `|`, `/` ou
# barra invertida, e todos têm significado no lado direito do `s|...|...|`. O `sed` não erraria
# de um jeito visível — ele gravaria um valor PARECIDO com o certo, e a autenticação falharia
# depois, longe daqui. O valor vai pelo ambiente, nunca por argumento.
CHAVE="$CHAVE" VALOR="$VALOR" python3 - <<'PY'
import os, re
chave, valor = os.environ['CHAVE'], os.environ['VALOR']
linhas = open('.env', encoding='utf-8').read().splitlines()
achou = False
for i, l in enumerate(linhas):
    if re.match(rf'^{re.escape(chave)}=', l):
        linhas[i] = f'{chave}={valor}'
        achou = True
        break
if not achou:
    linhas.append(f'{chave}={valor}')
open('.env', 'w', encoding='utf-8', newline='\n').write('\n'.join(linhas) + '\n')
print(f"{chave} gravado ({len(valor)} caracteres){'' if achou else ' — linha nova'}")
PY

echo
echo "Agora aplique: docker compose --env-file .env up -d --force-recreate api"
