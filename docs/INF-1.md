# INF-1 — Infraestrutura de produção

Arranjo para o primeiro ambiente público: VPS único (2 vCPU, 4 GB, Ubuntu 24.04), painel no
Cloudflare Pages, API atrás do Caddy em `appnexora.duckdns.org`.

**Arquivos criados:** `docker-compose.prod.yml`, `Caddyfile`, `.env.prod.example`,
`deploy/README.md`. **Alterados:** `Dockerfile` (dono da pasta de mídia), `.gitignore`
(`.env.prod`).

O bloco INF-1 não tocou código de aplicação. O `environment.ts` — único item que exigia — foi
alterado **depois**, em commit próprio, quando o domínio foi confirmado. Ver seção 1.

---

## 1. `environment.ts` — resolvido depois, fora do escopo original

> **Estado:** feito. O INF-1 registrou este item sem executá-lo, porque o escopo dele proibia
> mexer em código de aplicação. Com o domínio confirmado (`appnexora.duckdns.org`), a alteração
> foi aplicada num commit próprio. O relato abaixo fica porque explica o *porquê*.

### O que estava errado neste arranjo

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

**O que foi feito:**

```ts
apiBase: 'https://appnexora.duckdns.org/api',
hubBase: 'https://appnexora.duckdns.org/hub'
```

Verificado no bundle de produção, não só no fonte: a string `appnexora.duckdns.org` aparece no
`chunk-*.js` gerado por `ng build`. O build de desenvolvimento continua em `localhost:5123` — o
`fileReplacements` do `angular.json` troca o arquivo, e o de dev não foi tocado.

**⚠️ Trocar este domínio exige mexer em TRÊS lugares**, e o esquecimento do terceiro é o que
custa tempo:

| # | Onde | Se esquecer |
|---|---|---|
| 1 | `environment.ts` | o painel chama o domínio antigo |
| 2 | `DOMINIO_API` no `.env.prod` | o Caddy pede certificado para o domínio errado |
| 3 | `PAINEL_URL` no `.env.prod` | **o painel inteiro é barrado por CORS** — e o navegador só diz "No 'Access-Control-Allow-Origin' header is present", que não aponta para nenhum dos três |

**A alternativa descartada:** manter o caminho relativo e servir o painel pelo **mesmo** Caddy
(`handle /api/*` mais `file_server` para o bundle). Dispensaria CORS e faria a URL de preview do
Pages funcionar sozinha, ao custo de perder a CDN e de o build do painel entrar no deploy do VPS.
Decisão de arquitetura, tomada a favor do Pages.

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

> ⚠️ **Parágrafo vencido.** Os quatro itens acima foram resolvidos no deploy da Hostinger: cron a
> cada 6 horas, retenção de 14 dias por regra de ciclo de vida do bucket, cópia cifrada no
> Cloudflare R2 e **restauração exercitada na máquina de produção**. Está na seção 7.

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
| 7 | README do servidor vazio ao número pareado | ✅ 11 passos, mais operação, backup e restauração. O passo 10.1 (editar o `environment.ts`) **já foi executado** — ver seção 1 |
| 8 | Nenhum segredo real no repositório | ✅ Todos os campos de segredo do `.env.prod.example` estão vazios; o compose usa `${VAR:?}`, que aborta o `up` nomeando a variável faltante em vez de subir com padrão inseguro |

---

## 7. O ensaio de restauração — executado em 2026-10-01

Backup que nunca foi restaurado é um arquivo, não um backup. O ensaio foi feito na máquina de
produção, contra o objeto que estava **no R2**, e em dois tempos — porque o primeiro provou menos
do que parecia.

### 7.1 Primeiro ensaio: forte na aparência, fraco no conteúdo

Restaurou do R2 num database descartável e bateu com o banco vivo em todas as métricas: 27
tabelas, 339 colunas, 98 índices, 62 chaves estrangeiras, histórico de migrations até
`20260926004055_PaginaDoAnuncio`.

**E não provava quase nada.** O banco estava vazio — zero empresas, zero contatos, zero mensagens.
Um backup vazio restaura num banco vazio trivialmente. O que ficou provado foi o caminho do
*schema*; o caminho do *dado* seguia sem teste.

### 7.2 Segundo ensaio: o caminho do dado

Dois databases descartáveis, `nexora` e `evolution` intocados. Na origem, o schema real vindo do
backup do R2 e uma semente escolhida para quebrar se a cadeia estragar; depois o mesmo
`pg_dump -Fc | openssl enc` do `backup.sh`, e o `restore.sh` no destino.

| O que foi verificado | Como | Resultado |
|---|---|---|
| Conteúdo, não contagem | `md5` da **linha inteira**, ordenada, em 9 tabelas | Idêntico. 120 mensagens, 3 contatos, 3 negociações |
| Codificação | `Açaí & Cia — Ltda ✅`, `Zoé 🙂 Ñuñez`, `D'Ávila`, emoji, `chr(10)`, aspas duplas | Sobreviveu tudo |
| Sequences | `nextval` depois de restaurar | **4**, não 1 — restauração que zera sequence colide no primeiro insert |
| Invariantes do schema | `ck_usuarios_senha`, `ck_msg_data_disparo` | Recusaram a semente ingênua; a semente foi corrigida, não a restrição |
| Produção | contagem de empresas ao final | 0 — nunca foi tocada |

A semente respeitar as restrições do banco **é parte do resultado**: as duas que reclamaram
(usuário precisa de hash ou estar convidado; mensagem de saída precisa de `data_disparo`) estão
vivas e barram dado inconsistente.

### 7.3 O incidente que o ensaio descobriu

O ensaio não era para achar isto, e achou: **o backup não estava chegando no R2.** Duas rodadas de
cron, 00:10 e 06:10, falharam no envio com `remote error: tls: handshake failure`. Os dumps
existiam — cifrados, íntegros — **só no disco que o backup serve para sobreviver**.

A causa era um `R2_ACCOUNT_ID` com dois caracteres trocados de lugar:

```
certo:  567a1199d2e949e4df0e9e9cfddd dcc4
no .env: 567a1199d2e949e4df0e9e9cfddd ccc4
                                      ^^ d <-> c
```

Três coisas conspiraram para o erro ser invisível:

1. **O ID estava bem formado.** 32 dígitos hexadecimais. Nenhuma validação de forma pegaria.
2. **A Cloudflare recusa o TLS antes de apresentar certificado** para um subdomínio de
   `*.r2.cloudflarestorage.com` que não conhece. O cliente vê `handshake failure`, que parece
   problema de rede — não "você digitou errado". Gastei quatro sondagens perseguindo IPv6, versão
   de TLS e ALPN antes de desconfiar do ID.
3. **O erro não tinha para quem reclamar.** O `backup.sh` fez tudo certo: saiu com código
   diferente de zero e não chamou o heartbeat. Mas o `BACKUP_PUSH_URL` está vazio, então o
   silêncio não acusa nada.

O ID verdadeiro foi conferido sem entrar no painel: o token do Tunnel é um JSON em base64 e o
campo `a` é o ID da conta.

```sh
grep ^CLOUDFLARE_TUNNEL_TOKEN= .env | cut -d= -f2- | base64 -d
```

### 7.4 O que mudou por causa disso

**`deploy/verificar-backup.sh`** (novo), chamado como passo 6/6 do `deploy.sh`. Fala com o bucket
pelo rclone **do próprio contêiner de backup**, confirma que há dump do `nexora` das últimas 30
horas e, se falhar, nomeia a causa provável. O `exit 1` não derruba nada — a API já está no ar
nesse ponto. Ele existe para o deploy não dizer "pronto" com a cópia de segurança quebrada.

Dois erros meus dentro dessa checagem, e os dois valem registro:

- **A primeira versão fazia `rclone lsd r2:`** para "ver se responde". Listar buckets é permissão
  de **conta**, e o token certo não a tem: ela acusou backup quebrado num sistema saudável. O
  `AccessDenied` era a credencial bem feita. Least privilege quebra o teste preguiçoso.
- **A primeira versão contava a idade com `date`**, subtraindo do timestamp impresso pelo
  `rclone`. Dava 0h — por coincidência: a VPS está em UTC-3 e o `rclone` imprime hora local, então
  os dois lados batiam por acidente. Num servidor em UTC a mesma conta erraria em 3 horas. Agora a
  idade é do `--max-age` do rclone: quem sabe a hora do objeto é quem guardou o objeto.

Seis sabotagens, cada uma derrubando a verificação, e o controle intacto passando:

| Sabotagem | Pegou por |
|---|---|
| `account_id` com os dois caracteres trocados | TLS recusado |
| bucket com nome errado | 403 — e a mensagem diz as **duas** causas, porque com token de escopo "nome errado" e "sem permissão" são indistinguíveis |
| access key inválida | erro cru do rclone |
| secret inválido | erro cru do rclone |
| janela de idade reduzida a 1s | nenhum dump recente |
| nome do dump procurado trocado | nenhum dump do nexora no bucket |

### 7.5 O que continua aberto

- **`BACKUP_PUSH_URL` vazio.** É o que transformou um erro bem reportado em 12 horas de silêncio.
  Enquanto não houver monitor externo, a verificação só acontece quando alguém roda o deploy.
- **Repetir o ensaio depois do primeiro cliente**, com dado real e volume real. Os 120 registros
  daqui provam a cadeia, não o tempo de restauração de um banco cheio.
- **A mídia e as credenciais da Evolution não foram restauradas no ensaio** — só os dois dumps. Os
  tarballs estavam com 112 bytes porque as pastas estão vazias, o que é a informação correta hoje
  e deixa de ser no dia em que houver anexo.
