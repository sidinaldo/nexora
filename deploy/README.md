# Deploy do Nexora — do servidor vazio ao número pareado

VPS única, Docker Compose, entrada só pelo Cloudflare Tunnel.

| parte | onde |
|---|---|
| Painel Angular | Cloudflare Pages → `nexora.softioconsultoria.com.br` |
| API .NET | VPS, atrás do Tunnel → `nexora-api.softioconsultoria.com.br` |
| Postgres, Evolution, backup | a mesma VPS, **sem porta publicada** |

> ⚠️ **Nenhum serviço publica porta.** Quem fala com a internet é o `cloudflared`, e ele abre a
> conexão **de dentro para fora**. Não há porta para varrer, não há certificado para renovar, e
> trocar de VPS não mexe em DNS. O firewall deixa entrar só a 22.
>
> Isso também dispensa o teste de CGNAT que o arranjo anterior exigia: sem porta de entrada, não
> importa se o provedor dá IP público.

---

## 1. A VPS

Ubuntu 24.04, **4 vCPU / 16 GB / 200 GB** — a Hostinger KVM 4, em São Paulo. Chave SSH, nunca
senha.

> ⚠️ **A Hostinger tem firewall próprio no painel dela**, além do UFW que o `setup.sh` configura.
> São duas camadas, e a do painel vale primeiro: confira lá que só a 22 entra. Com o Tunnel, não
> há mais nada que precise de porta aberta.

```bash
ssh-keygen -t ed25519 -C "nexora-deploy"     # se ainda não tiver
```

Cole a pública no painel do provedor ao criar a máquina. Depois:

```bash
ssh root@SEU_IP 'bash -s' < deploy/host/setup.sh
```

O `setup.sh` é idempotente e faz: pacotes, atualizações automáticas, fuso, Docker, **rotação de
log**, **swap de 2 GB**, firewall (só a 22), SSH sem senha e fail2ban.

> ⚠️ **Confira que a sua chave funciona antes de rodar.** O script desliga login por senha; com a
> chave errada em `authorized_keys`, a única saída é o console do provedor.

Confira no fim:

```bash
ss -tlnp        # só a 22
free -h         # o swap apareceu
```

## 2. Os buckets no R2

No painel do Cloudflare, o menu lateral chama **R2 Object Storage** — não "R2". Ali dentro,
**Create bucket**, com o nome `nexora-backups`. A localização não importa: o backup sobe uma
vez a cada 6 horas.

**A regra de ciclo de vida, de 14 dias**, é o que apaga backup velho — o script não apaga nada
lá. Em *Settings → Object lifecycle rules*, "delete objects after 14 days".

> ⚠️ **A credencial deve ser só de escrita.** Uma que pode apagar backup é uma que um invasor usa
> para apagar backup. Em **R2 Object Storage → API → Manage API tokens**, permissão **Object Read & Write** restrita a esse
> bucket, e guarde `Account ID`, `Access Key ID` e `Secret`.

## 3. O Tunnel

Cloudflare **Zero Trust → Networks → Tunnels → Create a tunnel**, tipo *Cloudflared*.

Copie o **token** (é o `CLOUDFLARE_TUNNEL_TOKEN`) e cadastre o hostname público:

| campo | valor |
|---|---|
| Subdomain | `nexora-api` |
| Domain | `softioconsultoria.com.br` |
| Service | `HTTP` → `nexora-api:8080` |

> ⚠️ **`nexora-api:8080`, o nome do contêiner** — não `localhost`. O `cloudflared` roda na mesma
> rede Docker e resolve pelo nome do serviço.
>
> ⚠️ **Só a API ganha hostname.** A Evolution fica sem: ela controla o WhatsApp dos seus clientes
> e não tem por que ser alcançável da internet.

## 4. O `.env`

```bash
ssh root@SEU_IP
git clone https://github.com/sidinaldo/nexora.git /opt/nexora
cd /opt/nexora/deploy
cp .env.example .env
nano .env
```

Gere cada segredo com `openssl rand -hex 32`. Nunca escolha um à mão.

> ⚠️ **Guarde a `BACKUP_SENHA` fora desta máquina**, num gerenciador. Ela mora no `.env`, que está
> no disco que o backup existe para sobreviver — senha só aqui é backup ilegível no dia em que ele
> for preciso.

> ⚠️ **`EMAIL_PROVEDOR=arquivo` não envia nada.** Convite e "esqueci minha senha" falham em
> silêncio. Troque para `smtp` antes do primeiro cliente, na porta **587** (a 465 não funciona com
> o cliente SMTP da BCL).

> ⚠️ **ESTE ROTEIRO É PARA MÁQUINA VIRGEM.** Se o volume `nexora_pg_prod` já existir — porque a
> máquina rodou o arranjo antigo, que usava o MESMO nome —, o entrypoint do Postgres **pula** o
> `01-databases.sh`, e os usuários `nexora`/`evolution` e o database `evolution` nunca são criados.
>
> O sintoma é `password authentication failed for user "nexora"` no `migrate.sh`, com o pareamento
> de todos os clientes preso no volume órfão `nexora_evolution_pg_prod`. Numa máquina assim, crie
> tudo à mão antes de subir:
>
> ```bash
> docker volume ls | grep nexora     # confira o que já existe
> ```
>
> e rode o conteúdo do `postgres/init/01-databases.sh` pelo `psql`, depois restaure o database
> `evolution` do backup antigo.

## 5. Subir

```bash
cd /opt/nexora/deploy && ./deploy.sh
```

Ele faz, em ordem: build → banco de pé → **migrations** → sobe tudo → espera o `/health`
responder. Se a API não responder em 90 s, mostra o log e sai com erro.

> ⚠️ **As migrations são parte do deploy, e isso é novo.** No arranjo anterior era passo manual, e
> esquecê-lo subia a API contra schema velho **em silêncio**. O `migrate.sh` gera o script a partir
> do código que está sendo implantado — não de um arquivo commitado que pode estar velho. Foi
> exatamente o que se encontrou: um `migrations.sql` de agosto com vinte migrations faltando.
>
> Ele também remove o **BOM** que o `dotnet ef` grava (`EF BB BF`). Sem isso o `psql` lê os três
> bytes como parte do primeiro comando e devolve "erro de sintaxe em ou próximo a CREATE" apontando
> para uma linha visivelmente correta — que funciona na primeira aplicação e falha ao reaplicar.

## 6. Publicar o painel

O painel já está no Cloudflare Pages. Confira para onde ele aponta:

```bash
grep -r "apiBase" frontend/nexora-painel/src/environments/
```

Tem de ser `https://nexora-api.softioconsultoria.com.br/api`. E o `.env` do servidor precisa ter
`NEXORA_PUBLIC_PANEL_URL` com o domínio do painel, **sem barra no fim** — a comparação de origem do
CORS é exata, e `https://x/` não casa com `https://x`.

## 7. A primeira empresa

Três caminhos, do mais cômodo ao que sobrevive a mais coisa. Os três batem no **mesmo endpoint**,
com a **mesma chave**, e criam a empresa, o usuário dono, as 5 etapas do funil e a conexão **numa
transação só**.

**a) Pelo painel** — o caminho normal:

```
https://nexora.softioconsultoria.com.br/criar-empresa
```

Página pública, sem link em tela nenhuma: nenhum cliente do produto precisa encontrar um botão de
"criar empresa". Ela pede a `CADASTRO_CHAVE_ADMIN` e não a guarda no navegador.

**b) Pelo servidor**, e só ele funciona com o túnel fora do ar — fala com a API por dentro da rede
do Docker, sem passar pela internet:

```bash
/opt/nexora/deploy/criar-empresa.sh
```

**c) Na mão**, se precisar montar o corpo você mesmo:

```bash
curl -X POST https://nexora-api.softioconsultoria.com.br/api/cadastro/empresa \
  -H "Content-Type: application/json" \
  -H "X-Chave-Admin: SUA_CADASTRO_CHAVE_ADMIN" \
  -d '{"nome":"Padaria do Bairro","nomeDono":"Ana","emailDono":"ana@exemplo.com","senha":"..."}'
```

> ⚠️ O cabeçalho é **`X-Chave-Admin`**. Este README mandou `X-Chave-Administracao` até hoje, e
> seguir aquilo rendia um 401 que, de propósito, não diz se faltou chave ou se ela está errada —
> você conferiria a chave, que estava certa. O nome autoritativo está em
> `CadastroController.CabecalhoChave`.

> ⚠️ **3 cadastros por hora**, por IP, nos três caminhos. É teto de raio de explosão se a chave
> vazar. Esvazie `CADASTRO_CHAVE_ADMIN` quando terminar de criar os clientes — com ela vazia o
> endpoint recusa tudo, venha de onde vier.

## 8. Parear o número

No painel, **Conexão** → ler o QR com o celular da empresa.

---

## Monitoramento

> ⚠️ **É o item que já falhou.** A VPS anterior morreu quando o aluguel venceu e ninguém soube: o
> painel no Pages continuou servindo a tela de login normalmente, porque é estático. Quem abrisse
> via o formulário e não entrava, sem erro nenhum que dissesse o motivo.

**Fora desta VPS**, obrigatoriamente. Vigiar de dentro não pega a máquina inteira caindo.

| Monitor | Tipo | Alvo | Período / folga | Onde |
|---|---|---|---|---|
| Backup | Push | `backup.sh` | 6 h / 1 h | healthchecks.io |
| Disco | Push | `disk-check.sh` | 5 min / 15 min | healthchecks.io |
| API | HTTP | `https://nexora-api.softioconsultoria.com.br/health` | 5 min | UptimeRobot |

### ⚠️ São dois serviços, e tem de ser

Não procure os três no mesmo painel. **O healthchecks.io só faz push** — ele espera ser chamado e
nunca sai chamando. Por isso a checagem HTTP da API mora em outro lugar (UptimeRobot, grátis, 5
min); serviria igual o Better Stack ou um Uptime Kuma noutra máquina.

**Push** (backup e disco): o script chama a URL quando está tudo bem, e o monitor alerta pela
**AUSÊNCIA** do chamado. Invertido de propósito — assim o alarme também dispara se o cron morrer,
se a VPS cair, ou se o disco encher a ponto de o próprio script não rodar. Um script que "avisa
quando dá problema" não avisa quando o problema é ele mesmo.

**HTTP** (API): aqui a lógica é a comum, e precisa ser, porque a API não tem cron nenhum para
bater heartbeat. É também o único dos três que pega o caso do aviso no topo desta seção — API
morta com o painel servindo a tela de login normalmente, porque o painel é estático.

### ⚠️ A unidade do período engana, e os dois valores erraram na primeira tentativa

No healthchecks o número e a unidade são campos separados, e o seletor **não acompanha** o que se
digita ao lado. Na configuração inicial o backup ficou em "6 **minutos**" e o disco em "1 **dia**".
Nenhum dos dois quebra nada de imediato, e é esse o perigo:

- **6 minutos** num backup que roda a cada 6 h: vermelho em quase todo ciclo. Alarme falso
  constante é o jeito mais rápido de alguém aprender a ignorar o e-mail — e aí o alarme de verdade
  chega junto com os outros quarenta.
- **1 dia** no disco: o disco poderia encher e o aviso só sairia **25 horas depois**.

Os dois só apareceram porque o e-mail de teste foi lido linha a linha. **Depois de configurar,
confira o período no e-mail que o monitor manda, e não no formulário onde você acabou de digitar.**

O backup roda a cada 6 h e o monitor tolera 1 h de folga: um ciclo atrasado não vira falso alarme,
um ciclo perdido vira. Período igual ao intervalo do cron (5 h, por exemplo) cria uma corrida que
você perde — a batida chega exatamente no limite, e segundos de atraso viram alarme falso.

### O cron do disco

```bash
crontab -e
*/5 * * * * DISK_PUSH_URL='https://hc-ping.com/SEU-UUID' /opt/nexora/deploy/host/disk-check.sh
```

A variável vai **na linha do cron**, não no `deploy/.env`: aquele arquivo é do compose, e este
script roda no host, fora de contêiner.

---

## Operação

```bash
cd /opt/nexora/deploy
alias nx='docker compose --env-file .env'
```

### Ver log

```bash
nx logs -f api
nx logs -f evolution            # WhatsApp
nx logs -f cloudflared          # o túnel
nx logs --tail 100 backup       # o cron do backup
nx ps
```

### Atualizar

```bash
cd /opt/nexora && git pull && cd deploy && ./deploy.sh
```

O `deploy.sh` já aplica as migrations antes de subir a API.

### Restaurar — **e teste isto ANTES de precisar**

> ⚠️ **Backup nunca restaurado é um arquivo, não um backup.** A restauração nunca foi exercitada
> neste projeto, e o teste é **obrigatório antes do primeiro cliente**.

O `restore.sh` aceita **caminho local** ou chave no R2. Local é o que você vai querer no dia do
incidente, quando já baixou o arquivo pelo painel — e é o que permite ensaiar sem credencial.

Num database descartável, nunca em cima da produção — o `pg_restore --clean` **apaga** os objetos
antes de recriar:

```bash
nx exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db createdb -U postgres nexora_ensaio
nx exec backup restore.sh nexora_ensaio 2026/09/nexora-2026-09-30-0610.dump.enc
nx exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db \
    psql -U postgres -d nexora_ensaio \
    -c "select count(*) from information_schema.tables where table_schema='public';"
```

Tem de dar **27**. Compare também a contagem de linhas de `contatos` e `mensagens` com a origem —
schema certo com tabela vazia é o modo de falha que passa despercebido. Depois `dropdb
nexora_ensaio`.

> ⚠️ **Senha errada para antes de tocar o banco.** O `openssl` falha com "bad decrypt" e o script
> morre ali. E se algum dia ele não falhar — decifrar com senha errada pode produzir lixo em vez de
> erro —, a conferência da assinatura `PGDMP` pega, antes de o `--clean` destruir qualquer coisa.

### Trocar uma senha de banco

O script de init roda **uma vez só**, no primeiro boot com o volume vazio. Mudar o `.env` depois
não tem efeito nenhum:

```bash
nx exec -T db psql -U postgres -c "ALTER USER nexora PASSWORD 'nova';"
# e o .env junto, senão a API não reconecta no próximo restart
```

### Desligar

```bash
nx down                # mantém os volumes
nx down -v             # ⚠️ APAGA TUDO, inclusive o pareamento do WhatsApp
```

---

## Verificações rápidas

```bash
ss -tlnp                                          # só a 22
nx ps                                             # todos running, db healthy
curl -fsS https://nexora-api.softioconsultoria.com.br/health    # Healthy

# a Evolution NÃO responde de fora:
curl --max-time 5 http://SEU_IP:8080/ ; echo "  <- tem que falhar"

# o isolamento cruzado do banco:
nx exec -T -e PGPASSWORD="$NEXORA_DB_PASSWORD" db \
    psql -U nexora -d evolution -c "select 1" ; echo "  <- tem que dar permission denied"

docker inspect nexora-api --format '{{.HostConfig.LogConfig}}'   # max-size 10m
```

---

## Limites deste arranjo

Aceitos conscientemente.

| Limite | Consequência |
|---|---|
| **Mídia em disco local** | Não sobrevive a duas instâncias. O R2 é bloco próprio: o Nexora lê o base64 do webhook e grava, então mover é escrever `ArmazenamentoS3` — não é configuração |
| Rate limit em memória | Com duas instâncias, o teto dobra |
| Sem lock distribuído no agendador | Com duas instâncias, a rodada de follow-up roda duas vezes |
| Sem retenção de `payload_raw` nem de mídia | As duas maiores fontes de dado pessoal crescem para sempre (`docs/SEGURANCA.md`, achado 3) |
| Cache local na Evolution, sem Redis | Com dois ou três clientes basta; ligar depois é trocar duas linhas |
| Importação de CSV é síncrona | Até 2.000 linhas dentro do request — pode passar dos 100 s do Cloudflare. O corte em segundo plano já existe na importação da Meta e é o padrão a copiar |
| Token sem revogação | Usuário desativado entra por até 12 h (`docs/SEGURANCA.md`, achado 4) |

**Este arranjo é de uma instância só.** Subir uma segunda quebra os três primeiros de uma vez.
