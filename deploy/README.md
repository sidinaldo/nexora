# Deploy do Nexora — do servidor vazio ao número pareado

Ambiente de teste num VPS único. Ubuntu 24.04, 2 vCPU, 4 GB.

| Peça | Onde |
|---|---|
| Painel Angular | Cloudflare Pages |
| API .NET 8 | VPS, atrás do Caddy, em `appnexora.duckdns.org` |
| Postgres do Nexora | VPS, rede interna — **sem porta publicada** |
| Evolution API | VPS, rede interna — **sem porta publicada** |
| Postgres da Evolution | VPS, rede interna — **sem porta publicada** |

Só o Caddy é alcançável de fora. Todo o resto conversa por nome de serviço na rede do Docker.

---

## ⚠️ Leia antes de começar

**O roteiro pressupõe o domínio `appnexora.duckdns.org`.** Ele está chumbado em dois lugares: no
`environment.ts` do painel (já commitado) e no `DOMINIO_API` do `.env.prod` (você preenche no
passo 6). Se for usar outro domínio, os dois mudam juntos — mais o `PAINEL_URL`, se o domínio do
painel também mudar. Ver `docs/INF-1.md`, seção 1.

**Confirme que as portas chegam na máquina antes de começar.** Este roteiro assume um VPS com IP
público e 80/443 alcançáveis. Apontar o DuckDNS para uma conexão residencial brasileira costuma
não funcionar: a maioria dos provedores usa CGNAT (sem IP público não há port-forward que
resolva) e vários bloqueiam 80/443 de entrada. O Let's Encrypt valida pela 80 — sem ela, não há
certificado.

```bash
# NA MÁQUINA que vai receber. Se o IP for diferente do que o domínio resolve, é CGNAT.
curl -s ifconfig.me; echo
dig +short appnexora.duckdns.org
```

---

## 1. Criar o VPS

Ubuntu 24.04, 2 vCPU, 4 GB, **chave SSH — nunca senha**.

```bash
# na sua máquina, se ainda não tiver par de chaves
ssh-keygen -t ed25519 -C "nexora-deploy"
```

Cole a chave pública no painel do provedor ao criar a máquina. Depois:

```bash
ssh root@SEU_IP
```

Se o provedor só oferecer senha, troque para chave e desligue a autenticação por senha antes de
seguir:

```bash
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
systemctl restart ssh
```

## 2. Firewall do provedor

No painel do provedor (Hetzner Cloud Firewall, DigitalOcean Firewall, etc.), **entrada liberada
apenas em**:

| Porta | Para quê |
|---|---|
| 22 | SSH |
| 80 | HTTP — o Let's Encrypt valida por aqui |
| 443 | HTTPS |

Tudo o mais bloqueado. Em especial 5432, 5433, 8080 e 8082: o compose de produção não publica
nenhuma delas, e o firewall é a segunda barreira, não a primeira.

```bash
# defesa em profundidade também na máquina
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp
ufw --force enable
```

> O Docker escreve direto no `iptables` e **passa por cima do ufw** quando um serviço publica
> porta. Como aqui só o Caddy publica (80 e 443, que estão liberadas mesmo), não há conflito — mas
> não confie no ufw sozinho para conter uma porta publicada por engano. A garantia é o compose não
> declarar `ports:`.

## 3. Docker e plugin compose

```bash
apt update && apt install -y ca-certificates curl git
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
  > /etc/apt/sources.list.d/docker.list
apt update && apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

docker compose version   # tem que responder v2.x
```

## 4. Apontar o DuckDNS

Em [duckdns.org](https://www.duckdns.org), crie o subdomínio `appnexora` e ponha o IP do VPS.

```bash
# confirme a propagação ANTES de subir o Caddy
dig +short appnexora.duckdns.org
```

Se não devolver o IP, espere. Subir o Caddy com o DNS errado gasta uma tentativa do limite de
emissão do Let's Encrypt.

> ⚠️ **Quando migrar para domínio próprio:** se o DNS estiver atrás do proxy do Cloudflare (a
> nuvem **laranja** no painel de DNS), a validação HTTP-01 do Let's Encrypt falha — o Cloudflare
> responde no lugar do Caddy. Deixe a nuvem **cinza** (DNS only) até o certificado ser emitido, ou
> use o modo Full (strict) com certificado de origem do próprio Cloudflare. Para DuckDNS isso não
> se aplica.

## 5. Clonar o repositório

```bash
mkdir -p /opt && cd /opt
git clone https://github.com/sidinaldo/nexora.git
cd nexora
```

## 6. Criar o `.env.prod`

```bash
cp .env.prod.example .env.prod
chmod 600 .env.prod
nano .env.prod
```

**Gere cada segredo, não invente:**

```bash
openssl rand -hex 32     # POSTGRES_PASSWORD, JWT_CHAVE, EVOLUTION_API_KEY,
                         # EVOLUTION_DB_PASS, CADASTRO_CHAVE_ADMIN
openssl rand -hex 24     # WEBHOOK_SEGREDO
```

⚠️ **`WEBHOOK_SEGREDO` merece atenção.** É a única barreira do endpoint que recebe as mensagens —
a Evolution não assina o payload —, e o boot da API **não valida a entropia dele**: um valor curto
sobe sem reclamação. (A chave JWT, essa sim, derruba a aplicação com menos de 32 caracteres.)

`PAINEL_URL` vai para dois lugares — `Cors:Origens` e `Email:BaseUrlPainel`. Com `https://`, **sem
barra no final**: origem de CORS é comparada como string exata.

O `.env.prod` está no `.gitignore` e **não pode voltar para o repositório**. Ele é a única cópia
dos segredos — anote-os num gerenciador de senhas antes de fechar o editor.

## 7. Subir

**Antes, confirme que nenhum campo ficou vazio.** O compose usa `${VAR:?}` e aborta listando as
variáveis faltantes — é o comportamento certo, mas é mais rápido ver de uma vez:

```bash
grep -E '^[A-Z_]+=' .env.prod | awk -F= '{ if ($2=="") print "  VAZIO -> " $1; else print "  ok    -> " $1 }'
```

O comando mostra o **nome** de cada chave e se ela está preenchida — nunca o valor.

Se algum segredo ficou em branco, preencha sem digitar à mão (só substitui o que está vazio,
então dá para rodar de novo sem risco de sobrescrever o que já está certo):

```bash
for n in POSTGRES_PASSWORD JWT_CHAVE EVOLUTION_API_KEY EVOLUTION_DB_PASS CADASTRO_CHAVE_ADMIN; do
  sed -i "s|^${n}=$|${n}=$(openssl rand -hex 32)|" .env.prod
done
sed -i "s|^WEBHOOK_SEGREDO=$|WEBHOOK_SEGREDO=$(openssl rand -hex 24)|" .env.prod
```

`ACME_EMAIL` e `PAINEL_URL` não são gerados — preencha à mão.

Validação seca, sem subir nada:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod config -q && echo "compose ok"

docker run --rm --env-file .env.prod   -v "$PWD/Caddyfile:/etc/caddy/Caddyfile:ro" caddy:2-alpine   caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
```

O `caddy validate` pega erro de sintaxe **antes** de o container subir e gastar tentativa do
Let's Encrypt. Vale rodar sempre que mexer no `Caddyfile`.

Então:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
docker compose -f docker-compose.prod.yml --env-file .env.prod ps
```

Os cinco containers precisam ficar `running` (`db` e `evolution_db` também `healthy`).

Se faltar alguma variável obrigatória, o `up` **aborta dizendo o nome dela** — é a forma `${VAR:?}`
do compose, escolhida para não subir com padrão inseguro.

Acompanhe o Caddy pegando o certificado:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod logs -f caddy
curl -I https://appnexora.duckdns.org/health
```

> ⚠️ **Se o certificado não sair.** `duckdns.org` é compartilhado por milhares de pessoas e o
> Let's Encrypt limita 50 certificados por semana **por domínio registrado** — o limite pode já ter
> sido consumido por terceiros. O `Caddyfile` traz, comentado no final, a alternativa `tls
> internal` (autoassinado) para destravar o teste da API com `curl -k`. Ela **não** serve para
> testar o painel: o Pages tem HTTPS válido e o navegador recusa chamar API com certificado que
> não confia. A saída definitiva é domínio próprio.

## 8. Aplicar as migrations

**A API não aplica migration ao subir** — não há `Migrate()` no `Program.cs`, e a decisão de manter
assim está em `docs/INF-1.md`. É passo manual.

A imagem de runtime não tem SDK nem `dotnet-ef`, então o script sai da **sua máquina** e entra pelo
`psql` que já existe no container do Postgres.

Na sua máquina, na raiz do repositório:

```bash
# a string aponta para o banco de DEV; serve só para o EF ler o modelo,
# nada é escrito nela
export NEXORA_CONN="Host=localhost;Port=5432;Database=nexora_dev;Username=postgres;Password=..."
dotnet ef migrations script --idempotent \
  --project src/Nexora.Infra --startup-project src/Nexora.Api \
  -o migrations.sql
# ⚠️ TIRE O BOM. O `dotnet ef` grava com marca de ordem de byte (EF BB BF), e o psql
# lê os três primeiros bytes como parte do primeiro comando:
#
#     ERRO: erro de sintaxe em ou próximo a "CREATE"
#     LINHA 1: CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
#
# A mensagem aponta para uma linha visivelmente correta — o pior tipo de erro para
# depurar. Sem isto o script funciona na primeira aplicação e falha ao reaplicar.
sed -i '1s/^\xEF\xBB\xBF//' migrations.sql

scp migrations.sql root@SEU_IP:~/nexora/
```

**Ensaie num banco descartável antes de mandar.** Leva segundos e evita descobrir um erro de
sintaxe com o schema do servidor meio aplicado:

```bash
createdb nexora_ensaio
psql -d nexora_ensaio -v ON_ERROR_STOP=1 -f migrations.sql && echo "aplicou limpo"
psql -d nexora_ensaio -t -c 'SELECT COUNT(*) FROM "__EFMigrationsHistory";'   # tem que bater
psql -d nexora_ensaio -v ON_ERROR_STOP=1 -f migrations.sql && echo "reaplicar tambem ok"
dropdb nexora_ensaio
```

> ⚠️ **`NEXORA_CONN` é obrigatória.** A `FabricaDbContextDesignTime` recusa rodar sem ela, de
> propósito: existia um padrão apontando para um banco chamado `nexora`, e quem rodasse o comando
> sem definir a variável criava um banco vazio **em silêncio**. Sem a variável, o comando falha com
> a instrução na mensagem — não é bug.
>
> `--idempotent` gera um script que pode rodar quantas vezes quiser: cada migration vem embrulhada
> num `IF NOT EXISTS` contra `__EFMigrationsHistory`. É o que torna o deploy repetível.

No servidor:

```bash
cd /opt/nexora
source .env.prod
docker compose -f docker-compose.prod.yml --env-file .env.prod exec -T db \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 < migrations.sql

# conferir
docker compose -f docker-compose.prod.yml --env-file .env.prod exec -T db \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c '\dt' | head -20
```

Reinicie a API depois da primeira aplicação:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod restart api
```

## 9. Criar a primeira empresa

```bash
source .env.prod
curl -X POST https://appnexora.duckdns.org/api/cadastro/empresa \
  -H "Content-Type: application/json" \
  -H "X-Chave-Admin: $CADASTRO_CHAVE_ADMIN" \
  -d '{
        "nome": "Empresa Teste",
        "documento": null,
        "nomeDono": "Seu Nome",
        "emailDono": "voce@exemplo.com",
        "senha": "TROQUE-ESTA-SENHA",
        "nomeConexao": "Principal"
      }'
```

Responde `{"empresaId": N}`. Chave errada ou ausente devolve **401 sem detalhe** — é a mesma
resposta nos dois casos, de propósito.

A chamada cria, numa transação só: a empresa, o usuário **dono**, as cinco etapas do funil e a
conexão. Empresa sem etapa é empresa quebrada — o kanban não renderiza e `Contato.EtapaId` é NOT
NULL —, por isso vem tudo junto.

> Campos conforme o record `NovaEmpresa` em
> `src/Nexora.Core/Servicos/IServicoCadastroEmpresa.cs`. `documento`, `nomeConexao` e
> `instanceName` são opcionais. O endpoint aceita **3 chamadas por hora por IP**.

## 10. Publicar o painel no Cloudflare Pages

### 10.1 — Conferir para onde o painel aponta

Já está feito: `frontend/nexora-painel/src/environments/environment.ts` aponta para

```ts
apiBase: 'https://appnexora.duckdns.org/api',
hubBase: 'https://appnexora.duckdns.org/hub'
```

Só há o que fazer aqui se o seu domínio for outro. Nesse caso troque as duas linhas **e** o
`DOMINIO_API` do `.env.prod` — o Caddy usa o segundo para pedir o certificado, e um sem o outro
deixa o painel chamando um domínio sem certificado válido.

### 10.2 — Configurar o Pages

No painel do Cloudflare, **Workers & Pages → Create → Pages → Connect to Git**:

| Campo | Valor |
|---|---|
| Framework preset | Angular |
| Build command | `npm ci && npm run build` |
| Build output directory | `dist/nexora-painel/browser` |
| Root directory | `frontend/nexora-painel` |

### 10.3 — Fechar o CORS

Pegue a URL final (`https://seu-projeto.pages.dev`), ponha em `PAINEL_URL` no `.env.prod` e:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d api
```

> ⚠️ **A URL de PREVIEW do Pages vai ser barrada, e isso não é bug.**
> Cada branch e cada pull request ganha uma URL própria — `abc123.seu-projeto.pages.dev` — que
> **não** está em `Cors:Origens`. O sintoma é o painel de preview abrir e nenhuma requisição
> passar, com "No 'Access-Control-Allow-Origin' header is present" no console.
>
> São meia hora perdidas procurando bug onde não há. Ou você testa só pela URL de produção, ou
> acrescenta a origem de preview como `Cors__Origens__1` no compose. Não use curinga: a política
> usa `AllowCredentials`, e curinga com credenciais é ilegal no CORS.

## 11. Parear o número

1. Entre no painel com o e-mail e a senha do passo 9
2. **Configurações → Conexão → Adicionar conexão**
3. Leia o QR Code com o WhatsApp do celular: *Aparelhos conectados → Conectar aparelho*
4. O status vira **conectado** — o painel atualiza sozinho pelo SignalR

Teste de ponta a ponta: mande uma mensagem **de outro número** para o número pareado. Ela tem que
aparecer na caixa de entrada em segundos, criando o contato.

Não apareceu? Nesta ordem:

```bash
# a Evolution recebeu?
docker compose -f docker-compose.prod.yml --env-file .env.prod logs --tail 50 evolution
# a API recebeu o webhook?
docker compose -f docker-compose.prod.yml --env-file .env.prod logs --tail 50 api | grep -i webhook
```

`Webhook recusado: token invalido` significa que `WEBHOOK_SEGREDO` não bate entre a API e a
`WEBHOOK_GLOBAL_URL` da Evolution. Como as duas leem a **mesma** variável do `.env.prod`, isso só
acontece se a Evolution subiu antes de você preencher — recrie o container:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --force-recreate evolution
```

---

## Operação

Todos os comandos assumem `cd /opt/nexora`. Para encurtar:

```bash
alias nx='docker compose -f docker-compose.prod.yml --env-file .env.prod'
```

### Ver log

```bash
nx logs -f api                  # a aplicação
nx logs -f caddy                # certificado, requisições
nx logs -f evolution            # WhatsApp
nx logs --tail 100 api | grep -iE "error|fail|exceç"
nx ps                           # o que está de pé
```

### Atualizar

```bash
cd /opt/nexora
git pull
nx build api
nx up -d api
# se o pull trouxe migration nova, repita o passo 8 ANTES do up
```

O `--build` reconstrói a imagem; os volumes não são tocados. Downtime: o tempo de subir o
container, alguns segundos.

### Backup manual

⚠️ **Três coisas, não uma.** Só o banco do Nexora não basta: sem o volume da Evolution o cliente
tem que parear de novo, e sem a mídia os anexos das conversas somem.

```bash
mkdir -p /opt/backup && cd /opt/nexora && source .env.prod
D=$(date +%F-%H%M)

# 1. banco do Nexora — contatos, conversas, mensagens, vendas
nx exec -T db pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" > /opt/backup/nexora-$D.dump

# 2. banco da Evolution — a sessão pareada
nx exec -T evolution_db pg_dump -U evolution -Fc evolution > /opt/backup/evolution-$D.dump

# 3. mídia + credenciais da sessão (volumes)
docker run --rm -v nexora_midia_prod:/d -v /opt/backup:/b alpine \
  tar czf /b/midia-$D.tar.gz -C /d .
docker run --rm -v nexora_evolution_instances_prod:/d -v /opt/backup:/b alpine \
  tar czf /b/evolution-instances-$D.tar.gz -C /d .

ls -lh /opt/backup
```

**Tire os arquivos da máquina.** Backup no mesmo disco não protege contra perder o disco:

```bash
# da sua máquina
scp root@SEU_IP:/opt/backup/*-$D.* ./backups/
```

Esses arquivos contêm **dado pessoal de terceiros** — conversas, telefones, fotos. Guarde
cifrados e com acesso restrito.

### Restaurar

```bash
cd /opt/nexora && source .env.prod
nx stop api evolution

# banco do Nexora
nx exec -T db pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists \
  < /opt/backup/nexora-AAAA-MM-DD-HHMM.dump

# banco da Evolution
nx exec -T evolution_db pg_restore -U evolution -d evolution --clean --if-exists \
  < /opt/backup/evolution-AAAA-MM-DD-HHMM.dump

# mídia
docker run --rm -v nexora_midia_prod:/d -v /opt/backup:/b alpine \
  sh -c "rm -rf /d/* && tar xzf /b/midia-AAAA-MM-DD-HHMM.tar.gz -C /d"

# sessão da Evolution
docker run --rm -v nexora_evolution_instances_prod:/d -v /opt/backup:/b alpine \
  sh -c "rm -rf /d/* && tar xzf /b/evolution-instances-AAAA-MM-DD-HHMM.tar.gz -C /d"

nx start api evolution
```

⚠️ **Teste a restauração antes de precisar dela.** Backup nunca testado é backup que não existe —
e o momento de descobrir que o dump está truncado não é o dia do incidente.

### Desligar

```bash
nx stop            # para os containers, PRESERVA os volumes
nx down            # remove os containers, PRESERVA os volumes nomeados
nx down -v         # ⚠️ APAGA OS VOLUMES: banco, pareamento, mídia e certificado
```

O `-v` é irreversível. Não existe motivo para usá-lo em produção.

---

## Verificações rápidas

```bash
# nada além do Caddy pode aparecer aqui
nx ps --format '{{.Service}}\t{{.Ports}}'

# do lado de fora: só 22, 80 e 443
nmap -Pn SEU_IP

# a Evolution NÃO pode responder de fora
curl -m 5 http://SEU_IP:8080/  # tem que dar timeout ou recusa

# a API responde por HTTPS
curl -I https://appnexora.duckdns.org/health
```

---

## Limites deste arranjo

Aceitos conscientemente. Detalhamento em `docs/INF-1.md`.

| Limite | Consequência |
|---|---|
| Mídia em disco local | Não sobrevive a duas instâncias nem a container efêmero |
| Rate limit em memória | Com duas instâncias, o teto dobra |
| Sem lock distribuído no agendador | Com duas instâncias, a rodada de follow-up roda duas vezes |
| Backup manual | Automatizar **antes** de qualquer cliente pagante |
| Sem monitoramento | `/health` existe e nada o observa |
| Migration como passo manual | Um deploy que esqueça o passo 8 sobe a API contra schema velho |
| Token sem revogação | Usuário desativado entra por até 12 h (`docs/SEGURANCA.md`, achado 4) |

**Este arranjo é de uma instância só.** Subir uma segunda quebra os três primeiros itens de
uma vez.
