# INF-1 — Infraestrutura de produção

Arranjo para o primeiro ambiente público: VPS único (2 vCPU, 4 GB, Ubuntu 24.04), painel no
Cloudflare Pages, API atrás do Caddy em `appnexora.duckdns.org`.

**Arquivos criados:** `docker-compose.prod.yml`, `Caddyfile`, `.env.prod.example`,
`deploy/README.md`. **Alterados:** `Dockerfile` (dono da pasta de mídia), `.gitignore`
(`.env.prod`). **Nenhum código de aplicação foi tocado.**

---

## 1. O que exige mudança de código, e não foi feito

### `environment.ts` de produção aponta para o lugar errado neste arranjo

`frontend/nexora-painel/src/environments/environment.ts`:

```ts
apiBase: '/api',
hubBase: '/hub'
```

Caminho **relativo**, e o comentário do próprio arquivo explica a premissa: *"em produção o painel
e a API ficam atrás do mesmo domínio"*. Neste arranjo não ficam — o painel está no Cloudflare
Pages e a API no VPS. O caminho relativo resolve para `seu-projeto.pages.dev/api/...`, que não
existe.

**Efeito:** o painel abre, a tela de login aparece, e nenhuma requisição chega à API. Não há erro
no servidor porque nada chega até ele.

**O que precisa ser feito** (fora do escopo do INF-1, que proibia mexer em código de aplicação):

```ts
apiBase: 'https://appnexora.duckdns.org/api',
hubBase: 'https://appnexora.duckdns.org/hub'
```

Está no passo 10.1 do `deploy/README.md`, com destaque no topo do arquivo — é o único passo do
roteiro que não é copiar e colar.

> Vale considerar, em vez de chumbar o domínio: manter o caminho relativo e servir o painel pelo
> **mesmo** Caddy (um bloco `handle_path /api/*` mais `file_server` para o bundle). Some o CORS,
> some a URL de preview barrada e some este item — ao custo de perder a CDN do Pages. Decisão de
> arquitetura, não de infraestrutura; fica registrada, não decidida.

---

## 2. Divergências entre o prompt e o código

Duas chaves de configuração citadas no enunciado não existem com aquele nome. Valem os nomes do
código, e é o que está no compose.

| No enunciado | No código | Onde |
|---|---|---|
| `Email:BaseDoPainel` | **`Email:BaseUrlPainel`** | `src/Nexora.Infra/Email/OpcoesEmail.cs:19` |
| `Agendador:Fuso` | **`Agendador:FusoHorario`** | `src/Nexora.Api/Servicos/AgendadorFollowUp.cs:21` |

Configuração no .NET **não reclama de chave desconhecida**: `Email__BaseDoPainel` seria lido,
ignorado, e o `BaseUrlPainel` ficaria no padrão `http://localhost:4200` — o link do convite
apontaria para a máquina de quem clicasse. Falha silenciosa, e é por isso que a divergência
importa.

---

## 3. Decisões

### 3.1 Migrations — passo manual, por script idempotente

**Decisão:** não aplicar no start. Gerar `dotnet ef migrations script --idempotent` na máquina de
desenvolvimento e aplicar no servidor com o `psql` que já existe no container do Postgres.

**Por quê.** Três caminhos foram considerados:

| Caminho | Por que não / por que sim |
|---|---|
| `Migrate()` no start da API | Simples, e com **duas instâncias vira corrida de migration** — duas conexões aplicando o mesmo DDL. Hoje é uma instância só, então seria aceitável; foi descartado porque o `Program.cs` **já** não faz isso, e ligar migração automática é decisão de arquitetura, não de deploy |
| `dotnet ef database update` no servidor | Exige o **SDK inteiro** no VPS (~800 MB) só para isso. Numa máquina de 4 GB é peso a troco de nada |
| **Script idempotente + `psql`** ✅ | Nenhuma ferramenta nova no servidor. O `--idempotent` embrulha cada migration num teste contra `__EFMigrationsHistory`, então reaplicar é seguro. O SQL é legível e pode ser conferido antes de rodar |

**O custo, declarado:** o script é gerado **fora** do servidor e precisa ser transferido a cada
deploy que traga migration nova. Um deploy que esqueça o passo 8 sobe a API contra schema velho —
e o sintoma é erro de coluna inexistente no primeiro uso da funcionalidade nova, não no boot.

Uma alternativa que resolve isso sem SDK no servidor é `dotnet ef migrations bundle`, que produz
um executável autocontido. Fica registrado como melhoria; hoje o script cobre.

> ⚠️ `NEXORA_CONN` é **obrigatória** para gerar o script. A `FabricaDbContextDesignTime` recusa
> rodar sem ela de propósito — havia um padrão apontando para um banco chamado `nexora`, e quem
> rodasse sem a variável criava um banco vazio em silêncio. Sem isso o deploy trava com uma
> mensagem que parece bug e não é.

### 3.2 Certificado — Let's Encrypt pelo Caddy, com plano B declarado

**Decisão:** o Caddy obtém e renova sozinho, por HTTP-01. Sem cron, sem certbot, sem renovação
esquecida.

**Os dois modos de falha, e o que fazer em cada um:**

**DuckDNS pode recusar.** `duckdns.org` é um domínio **compartilhado** por milhares de pessoas, e
o limite do Let's Encrypt de 50 certificados por semana é **por domínio registrado** — não por
subdomínio. O limite pode já ter sido consumido por terceiros, e a emissão falha por motivo que
não é seu. O `Caddyfile` traz, comentado, a alternativa `tls internal` (autoassinado).

⚠️ **O modo `internal` não serve para testar o painel de ponta a ponta.** O Pages entrega HTTPS
válido, e o navegador **recusa** uma chamada a API com certificado que não confia. Serve para
`curl -k` contra a API. A saída definitiva é domínio próprio.

**Proxy do Cloudflare quebra a validação.** Quando migrar para domínio próprio: com a nuvem
**laranja** ligada, o Cloudflare responde no lugar do Caddy e o HTTP-01 falha. Deixe **cinza** até
o certificado sair. Registrado no passo 4 do README.

**O volume `caddy_data` é obrigatório.** Sem ele, todo `down` descarta o certificado e o próximo
`up` pede outro. Cinco pedidos por semana para o mesmo conjunto de domínios e o site fica sem
HTTPS por dias — por um volume esquecido.

### 3.3 Backup — manual, três peças

**Decisão:** procedimento manual documentado, com `pg_dump` dos **dois** bancos mais tar dos
**dois** volumes de dado.

**Por que dois bancos e dois volumes, e não só o banco do Nexora:**

| O quê | Se faltar no backup |
|---|---|
| `pg_dump` do banco do Nexora | Perde contatos, conversas, mensagens, vendas — tudo |
| `pg_dump` do banco da Evolution | Perde a sessão: o cliente **lê o QR de novo** |
| `nexora_midia_prod` | As linhas de `mensagens` sobrevivem apontando para anexos que não existem |
| `nexora_evolution_instances_prod` | As credenciais da sessão pareada |

**O que não está resolvido:** não há automação, não há retenção, não há cópia fora da máquina por
padrão, e a restauração nunca foi testada. Backup no mesmo disco não protege contra perder o
disco. **Automatizar antes de qualquer cliente pagante** — é o item mais urgente da lista da
seção 5.

⚠️ Os arquivos de backup contêm **dado pessoal de terceiros**: conversas, telefones, fotos. Guarde
cifrados e com acesso restrito. `docs/SEGURANCA.md` (achados 2 e 3) trata do mesmo dado do lado da
LGPD.

### 3.4 Três redes, não uma

`borda`, `interna` e `evo`. O Caddy não precisa alcançar os bancos; o banco da Evolution não
precisa ser alcançável pela API. Cada serviço enxerga só o que usa.

Não é a proteção principal — essa é a ausência de `ports:` — mas custa três linhas e limita o
alcance de um container comprometido.

### 3.5 Webhook por dentro da rede

`WEBHOOK_GLOBAL_URL` aponta para `http://nexora-api:8080/...`, não para o domínio público. O
tráfego não sai da máquina, não gasta TLS e não depende de DNS propagado.

**Efeito colateral bom:** o segredo do webhook viaja na query string (a Evolution não suporta
header nem assinatura) e, por não passar pelo Caddy, **não aparece no log de acesso**. Se um dia a
Evolution sair desta máquina, o segredo passa a atravessar o Caddy e o log precisa filtrar a
query — anotado no `Caddyfile`.

---

## 4. A pasta de mídia precisava de dono

Única alteração no `Dockerfile`:

```dockerfile
RUN mkdir -p /app/midia && chown app:app /app/midia
```

**Por que não dava para deixar por conta do volume.** Quando um volume nomeado vazio é montado
sobre um diretório que **já existe** na imagem, o Docker copia o conteúdo e o **dono** daquele
diretório. Se o diretório não existisse, o Docker o criaria como **root**, e a aplicação — que
roda como `app`, não-root — receberia "Access denied" na primeira foto que um cliente mandasse.

O modo de falha é silencioso do lado errado: a mensagem entra normalmente, só o download do anexo
falha, e a causa vai para `mensagens.erro`, que ninguém lê. Uma linha evita o diagnóstico.

`Midia__Raiz` está como caminho **absoluto** (`/app/midia`) no compose. O padrão relativo
(`"midia"`) resolveria para o mesmo lugar pelo WORKDIR e funcionaria por acidente; explícito deixa
claro qual diretório precisa do volume.

O resto do `Dockerfile` já atendia: multi-stage, imagem final sem SDK, `USER app`, e o
`.dockerignore` cobrindo `bin`, `obj`, `node_modules`, `.git`, `.env*` (com `!.env.example`).

---

## 5. Limites registrados, não resolvidos

| Limite | Consequência | Quando morde |
|---|---|---|
| Mídia em disco local | Não sobrevive a duas instâncias nem a container efêmero | Ao escalar, ou ao migrar para plataforma sem volume persistente |
| Rate limit em memória | Com duas instâncias, o teto **dobra** | Ao escalar |
| Sem lock distribuído no agendador | Com duas instâncias, a rodada de follow-up roda **duas vezes** | Ao escalar. As invariantes de banco (teto diário, `uq_msg_lembrete`) impedem mensagem duplicada, então o dano é trabalho repetido, não spam |
| Backup manual | Perda de dados se ninguém rodar | **Hoje.** É o item mais urgente |
| Sem monitoramento | `/health` existe e nada o observa: a API pode estar fora por horas | **Hoje** |
| Migration como passo manual | Deploy que esquece o passo 8 sobe contra schema velho | A cada deploy com migration |
| Uma instância só | Todo deploy tem downtime de alguns segundos | A cada deploy |
| Token sem revogação | Usuário desativado entra por até 12 h | `docs/SEGURANCA.md`, achado 4 |

**Os três primeiros itens quebram juntos no instante em que uma segunda instância subir.** Não é
"escala mal": é comportamento errado — mídia servindo 404 alternado, limite dobrado e follow-up em
duplicidade. Antes de escalar horizontalmente: object storage, rate limit com backplane e lock
distribuído, nessa ordem.

---

## 6. Critério de pronto — verificado

| # | Critério | Como foi verificado |
|---|---|---|
| 1 | `docker compose -f docker-compose.prod.yml config` valida | ✅ Executado com um `.env` de placeholders **fora do repositório** |
| 2 | Só o Caddy declara `ports:` | ✅ Conferido na config **resolvida**: `api`, `db`, `evolution`, `evolution_db` sem `published` |
| 3 | Todo dado que precisa sobreviver em volume nomeado | ✅ Seis: dois bancos, sessão da Evolution, mídia, `caddy_data`, `caddy_config` |
| 4 | `.env.prod` não versionado, exemplo sem valor real | ✅ `git add --dry-run .env.prod` → recusado; `.env.prod.example` → aceito, só placeholders |
| 5 | Dockerfile roda como não-root | ✅ `USER app`, e a pasta de mídia com dono |
| 6 | `DATABASE_SAVE_DATA_HISTORIC` falso | ✅ Na config resolvida, junto dos outros seis `SAVE_DATA` |
| 7 | README do servidor vazio ao número pareado | ✅ 11 passos, mais operação, backup e restauração. **Um passo não é copiar e colar:** o 10.1, que exige editar o `environment.ts` — ver seção 1 |
| 8 | Nenhum segredo real no repositório | ✅ Todos os campos de segredo do `.env.prod.example` estão vazios; o compose usa `${VAR:?}`, que aborta o `up` nomeando a variável faltante em vez de subir com padrão inseguro |
