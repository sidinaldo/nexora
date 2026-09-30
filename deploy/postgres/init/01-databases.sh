#!/bin/bash
#
# Cria os dois databases e os dois usuários, com isolamento cruzado.
#
# ==========================================================================================
#  ⚠️ RODA UMA VEZ SÓ, e é fácil errar isso.
#
#  O entrypoint do Postgres executa `/docker-entrypoint-initdb.d/*` apenas quando o diretório de
#  dados está VAZIO — ou seja, no primeiríssimo boot do volume. Mudar este arquivo depois não
#  tem efeito nenhum; um `docker compose up` seguinte nem o lê.
#
#  Para trocar senha depois do primeiro boot, é `ALTER USER ... PASSWORD ...` à mão. Está no
#  README.
# ==========================================================================================
#
#  ⚠️ POR QUE O ISOLAMENTO CRUZADO IMPORTA
#
#  Um Postgres só, dois databases. Sem os `REVOKE`, qualquer usuário conectaria em qualquer
#  database — e a credencial da Evolution vive dentro de um contêiner de terceiro, rodando
#  Baileys, que é a peça de maior superfície do sistema.
#
#  Com eles, comprometer a Evolution dá acesso ao database `evolution` (sessões do WhatsApp) e
#  não ao `nexora` (conversas, contatos e vendas de todos os tenants).
#
#  `REVOKE ... FROM PUBLIC` é o que fecha de verdade: sem isso, o `CONNECT` vem do papel PUBLIC,
#  que todo usuário herda, e conceder nada a ninguém ainda deixaria a porta aberta.

set -euo pipefail

: "${NEXORA_DB_PASSWORD:?NEXORA_DB_PASSWORD não veio do .env}"
: "${EVOLUTION_DB_PASSWORD:?EVOLUTION_DB_PASSWORD não veio do .env}"

# ⚠️ AS SENHAS VÃO POR VARIÁVEL DO PSQL, e não interpoladas no SQL. Uma senha com aspa simples
# — `p4ss'w0rd` — quebraria o `CREATE USER` ao meio: com `ON_ERROR_STOP=1` o psql aborta, o
# entrypoint marca a inicialização como falha, e o cluster fica PELA METADE (o `nexora` pode
# existir sem o `evolution` e sem os `REVOKE` cruzados).
#
# E o sintoma apareceria longe da causa, como "permission denied for schema public" na primeira
# migration. O `:'nome'` faz o psql citar o valor com as regras dele, e a classe de erro some.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres      -v senha_nexora="$NEXORA_DB_PASSWORD" -v senha_evolution="$EVOLUTION_DB_PASSWORD" <<-EOSQL
    CREATE USER nexora    WITH PASSWORD :'senha_nexora';
    CREATE USER evolution WITH PASSWORD :'senha_evolution';

    CREATE DATABASE nexora    OWNER nexora;
    CREATE DATABASE evolution OWNER evolution;

    -- Tira o CONNECT de todo mundo, inclusive do PUBLIC que cada usuário herda...
    REVOKE CONNECT ON DATABASE nexora    FROM PUBLIC;
    REVOKE CONNECT ON DATABASE evolution FROM PUBLIC;

    -- ...e devolve só ao dono de cada um.
    GRANT CONNECT ON DATABASE nexora    TO nexora;
    GRANT CONNECT ON DATABASE evolution TO evolution;
EOSQL

# ⚠️ O SCHEMA `public` TAMBÉM, e este é o passo que se esquece. Desde o Postgres 15 o `public`
# não é mais gravável por PUBLIC, mas o dono do database precisa de permissão explícita para
# criar nele — sem isto a primeira migration falha com "permission denied for schema public",
# um erro que parece de conexão e não é.
for banco in nexora evolution; do
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$banco" <<-EOSQL
        REVOKE ALL ON SCHEMA public FROM PUBLIC;
        GRANT ALL ON SCHEMA public TO ${banco};
EOSQL
done

echo "Databases nexora e evolution criados, com CONNECT cruzado revogado."
