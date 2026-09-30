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
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin prohibit-password/' /etc/ssh/sshd_config
systemctl restart ssh

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
