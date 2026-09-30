#!/usr/bin/env bash
#
# Aplica as migrations no database `nexora`.
#
# ==========================================================================================
#  ⚠️ GERA O SCRIPT NA HORA, dentro de um contêiner do SDK — não usa arquivo commitado.
#
#  O arranjo anterior pedia `dotnet ef migrations script` na máquina do desenvolvedor, remover o
#  BOM à mão e mandar por `scp`. Três passos manuais, e o modo de falha era silencioso: um
#  arquivo velho aplica sem erro e deixa a API rodando contra schema antigo. Foi exatamente o
#  que se encontrou — um `migrations.sql` de agosto com VINTE migrations faltando.
#
#  Gerando aqui, o script sai sempre do código que está sendo implantado. Não há o que
#  desatualizar.
# ==========================================================================================

set -euo pipefail

cd "$(dirname "$0")"
COMPOSE="docker compose --env-file .env"
set -a; . ./.env; set +a

RAIZ=$(cd .. && pwd)
SQL=$(mktemp); trap 'rm -f "$SQL"' EXIT

echo "Gerando o script a partir do código atual..."
# A connection string aqui serve só para o EF LER O MODELO — nada é escrito nela.
# ⚠️ APAGA O RESTO DE UMA RODADA ANTERIOR ANTES DE GERAR. Se uma execução morreu entre gerar e
# aplicar, o arquivo velho fica — e aplicá-lo como se fosse novo é exatamente o "arquivo velho
# aplica sem erro" que este script existe para eliminar.
rm -f .migrations.sql

# ⚠️ SEM `| tail`, e com `set -e` lá dentro. A versão anterior canalizava para `tail -3` dentro
# de um `sh -c`: o dash não tem `pipefail`, então o código de saída era o do `tail` — sempre 0.
# Uma falha ao instalar o `dotnet-ef` passava despercebida, e o script só morria depois no `sed`,
# com "can't read .migrations.sql", apontando para a causa errada.
docker run --rm -v "$RAIZ":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0     sh -c 'set -e
           dotnet tool install -g dotnet-ef --version 8.0.11
           export PATH="$PATH:/root/.dotnet/tools"
           export NEXORA_CONN="Host=x;Database=x;Username=x;Password=x"
           dotnet ef migrations script --idempotent              --project src/Nexora.Infra --startup-project src/Nexora.Api              -o /src/deploy/.migrations.sql'

# Dupla checagem: o `docker run` pode sair 0 e o arquivo não existir se algo mudar no SDK.
[ -s .migrations.sql ] || { echo "ERRO: o script de migration nao foi gerado." >&2; exit 1; }

# ⚠️ O BOM. O `dotnet ef` grava com `EF BB BF`, e o `psql` lê os três bytes como parte do
# primeiro comando: o erro sai como "sintaxe em ou próximo a CREATE" apontando para uma linha
# visivelmente correta. Funciona na primeira aplicação e falha ao reaplicar.
sed '1s/^\xEF\xBB\xBF//' .migrations.sql > "$SQL"
rm -f .migrations.sql

linhas=$(grep -c 'MigrationId' "$SQL" || true)
echo "Aplicando ($linhas verificações de migration)..."

# `ON_ERROR_STOP=1`: sem isto o psql segue depois de um erro e termina com código 0, e o deploy
# continua achando que o schema está certo.
# ⚠️ COMO `nexora`, E NÃO COMO `postgres`. Aplicar migration com o superusuário faz as tabelas
# nascerem com o dono errado, e a aplicação — que conecta como `nexora` — passa a esbarrar em
# permissão num objeto que ela mesma deveria possuir.
$COMPOSE exec -T -e PGPASSWORD="${NEXORA_DB_PASSWORD:?}" db     psql -U nexora -d nexora -v ON_ERROR_STOP=1 -q < "$SQL"

echo "Migrations aplicadas."
