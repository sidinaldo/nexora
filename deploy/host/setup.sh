#!/usr/bin/env bash
#
# Prepara uma VPS Ubuntu 24.04 do zero. IDEMPOTENTE: rodar de novo não estraga nada.
#
#   ssh root@IP 'bash -s' < deploy/host/setup.sh
#
# ==========================================================================================
#  ⚠️ O FIREWALL SÓ ABRE A 22, e é o ponto do arranjo inteiro.
#
#  Não há 80 nem 443 porque não há nada escutando nelas: quem fala com a internet é o
#  `cloudflared`, e ele abre a conexão DE DENTRO PARA FORA. Não há porta para varrer nem
#  certificado para renovar.
#
#  Se algum dia alguém publicar uma porta no compose "só para testar", isso quebra a premissa em
#  que o `RateLimit__ConfiarProxyReverso: true` se apoia — com um caminho alternativo aberto,
#  qualquer um forja o cabeçalho de IP e escapa de todos os limites.
# ==========================================================================================

set -euo pipefail

echo "== 1/8 pacotes =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get upgrade -y -qq
apt-get install -y -qq ca-certificates curl git ufw fail2ban unattended-upgrades

echo "== 2/8 atualizações de segurança automáticas =="
dpkg-reconfigure -f noninteractive unattended-upgrades

echo "== 3/8 fuso horário =="
timedatectl set-timezone America/Sao_Paulo

echo "== 4/8 Docker =="
if ! command -v docker > /dev/null; then
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
        > /etc/apt/sources.list.d/docker.list
    apt-get update -qq
    apt-get install -y -qq docker-ce docker-ce-cli containerd.io \
        docker-buildx-plugin docker-compose-plugin
fi
docker compose version

echo "== 5/8 rotação de log do Docker =="
# ⚠️ SEM ISTO O DISCO ENCHE. O default do Docker é json-file SEM limite: o log da Evolution, que
# é falante, cresce até acabar o disco — e quando acaba, o Postgres para de aceitar escrita. São
# 40 GB no total, então 30 MB por contêiner é teto de sobra.
cat > /etc/docker/daemon.json <<'JSON'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" }
}
JSON
systemctl restart docker

echo "== 6/8 swap =="
# O limite de memória impede um contêiner de crescer sem fim; o swap transforma o pico que sobra
# em LENTIDÃO em vez de morte. Sem ele, o kernel mata o maior processo — que quase sempre é o
# Postgres, e não quem vazou.
if ! swapon --show | grep -q /swapfile; then
    fallocate -l 2G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile
    swapon /swapfile
    grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
sysctl -w vm.swappiness=10 > /dev/null
grep -q '^vm.swappiness' /etc/sysctl.conf || echo 'vm.swappiness=10' >> /etc/sysctl.conf

echo "== 7/8 firewall =="
ufw --force reset > /dev/null
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp comment 'SSH'
ufw --force enable
ufw status verbose

echo "== 8/8 SSH e fail2ban =="
# ⚠️ CONFIRA QUE A SUA CHAVE FUNCIONA ANTES DE RODAR ISTO. Desligar senha e login de root com a
# chave errada no `authorized_keys` tranca você para fora, e a única saída é o console do
# provedor.
#
# ===================== POR QUE UM ARQUIVO `01-`, E NÃO UM `sed` =====================
# ⚠️ EDITAR O `sshd_config` NÃO FUNCIONA NO UBUNTU 24.04, e o modo de falha é o pior: o script
# termina dizendo "SSH sem senha" e a senha continua ligada.
#
# A imagem de nuvem põe `Include /etc/ssh/sshd_config.d/*.conf` NO TOPO do `sshd_config`, e
# traz um `50-cloud-init.conf` com `PasswordAuthentication yes`. O sshd honra a PRIMEIRA
# ocorrência de cada diretiva — então o `yes` do include vence o `no` escrito mais abaixo.
#
# Pelo mesmo motivo o arquivo aqui é `01-`: um `99-` seria lido DEPOIS do `50-cloud-init` e
# perderia. Quem chega primeiro manda.
#
# Verificado na máquina: antes disto, `sshd -T` respondia `passwordauthentication yes` com o
# `sshd_config` dizendo `no`.
# ===============================================================================
cat > /etc/ssh/sshd_config.d/01-nexora.conf <<'CONF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
CONF
chmod 600 /etc/ssh/sshd_config.d/01-nexora.conf

# ⚠️ VALIDA ANTES DE REINICIAR. Um `sshd_config` inválido faz o serviço não subir, e aí a
# máquina fica inalcançável por SSH — com o firewall já fechado, sem console não há volta.
sshd -t
systemctl restart ssh

# E confere o que o sshd de fato passou a usar, não o que o arquivo diz.
efetivo=$(sshd -T | grep -i '^passwordauthentication' | awk '{print $2}')
if [ "$efetivo" != "no" ]; then
    echo "ERRO: senha continua habilitada (sshd -T diz '$efetivo'). Confira os includes." >&2
    exit 1
fi
echo "Senha desligada, confirmado por sshd -T."

cat > /etc/fail2ban/jail.local <<'INI'
[sshd]
enabled = true
maxretry = 5
bantime = 1h
INI
systemctl enable --now fail2ban
systemctl restart fail2ban

echo
echo "Pronto. Confira que só a 22 escuta:"
echo "  ss -tlnp"
