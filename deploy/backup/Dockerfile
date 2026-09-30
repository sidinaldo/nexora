# Backup do Nexora: pg_dump + cifragem + envio para o R2, num contêiner com cron.
#
# ⚠️ A VERSÃO DO CLIENTE TEM DE ACOMPANHAR A DO SERVIDOR. `pg_dump` de uma versão MENOR que a do
# servidor recusa rodar com "server version mismatch" — e o sintoma seria o backup parando no dia
# em que alguém subisse o Postgres. Alpine 3.21 traz o cliente 17, que é o do `db`.
FROM alpine:3.21

# `postgresql17-client` = pg_dump/pg_restore. `rclone` = envio para o R2.
# `openssl` = a cifragem. `curl` = o heartbeat do Uptime Kuma.
RUN apk add --no-cache postgresql17-client rclone openssl curl tzdata bash

ENV TZ=America/Sao_Paulo

COPY backup.sh restore.sh /usr/local/bin/
COPY crontab /etc/crontabs/root
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/restore.sh

# `-f` em primeiro plano: é o processo 1 do contêiner, e sem ele o contêiner sai na hora.
# `-d 8` manda o log do cron para o stdout, que é onde o `docker logs` procura.
CMD ["crond", "-f", "-d", "8"]
