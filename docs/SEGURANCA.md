# SEC-1 — Auditoria de segurança

**Escopo:** código da API (.NET 8), painel (Angular 20), infraestrutura declarada
(`docker-compose.yml`, `Dockerfile`) e dependências.
**Data:** 2026-08-08 · commit `376504d` · branch `main`
**Natureza:** somente leitura. Nenhuma correção aplicada.

---

## 1. Veredicto

O **código** está pronto para exposição pública. O isolamento multi-tenant é sólido: 61 usos de
`IgnoreQueryFilters` foram inspecionados um a um e todos têm filtro explícito por `empresa_id` ou
operam sobre chave global por construção. Não há injeção de SQL, não há XSS, os segredos estão
fora do repositório e o fluxo de autenticação é bem construído.

**O que trava o lançamento não é código, é implantação.** O `docker-compose.yml` publica a
Evolution API e o Postgres em portas do host. Subido como está numa máquina pública, o número de
WhatsApp do cliente fica a uma chave de distância de qualquer um na internet.

**Dois débitos de LGPD** precisam de decisão antes de guardar dado de cliente real: a anonimização
não alcança o conteúdo das mensagens, e não existe retenção para `payload_raw`.

Resumo: **feche a rede, ponha TLS na frente e decida a retenção. Depois pode subir.**

---

## 2. Achados por severidade

| # | Sev | Onde | O que permite |
|---|---|---|---|
| 1 | 🔴 | `docker-compose.yml:26,70` | Evolution e Postgres publicados no host — controle do número de WhatsApp e leitura do banco a partir da internet |
| 2 | 🟠 | `ServicoContatos.cs:485-506` | Anonimização não remove PII de `mensagens`, `payload_raw`, mídia em disco nem `conversas.ultima_mensagem_previa` |
| 3 | 🟠 | ausência | Sem retenção de `payload_raw` / mensagens / mídia — PII acumula indefinidamente |
| 4 | 🟠 | `GeradorToken.cs:15` | Sem revogação: token de 12 h sobrevive à desativação do usuário e ao rebaixamento de papel |
| 5 | 🟠 | `Program.cs` (pipeline) | Sem HTTPS/HSTS na aplicação — Kestrel exposto direto entrega JWT em claro |
| 6 | 🟡 | `WebhookController.cs:13` | Entropia do segredo do webhook não é validada — segredo fraco permite forjar "o contato respondeu" |
| 7 | 🟡 | `WebhookController.cs:40` | Webhook sem `RequestSizeLimit`: corpo de até 30 MB lido inteiro em memória, 300×/min por IP |
| 8 | 🟡 | `ProcessadorEventoEvolution.cs:59` | Payload completo (telefone + texto do cliente) gravado em log de erro |
| 9 | 🟡 | `ServicoEquipe.cs:50` | Enumeração cross-tenant: descobre se um e-mail já é usuário de qualquer empresa |
| 10 | 🟡 | `Program.cs:109` + `RateLimitingConfig.cs:78` | Sem `FallbackPolicy` e sem limite global para anônimo — controller novo nasce público e ilimitado |
| 11 | 🟡 | `ServicoContatos.cs` vs `ServicoRelatorios.cs:724` | Recorte por papel inconsistente: relatório esconde números de outro vendedor, lista de contatos entrega |
| 12 | 🟡 | `ValidadorMidia.cs:22` | Tipo de mídia validado pelo MIME declarado, não pelo conteúdo; sem `nosniff` |
| 13 | 🟡 | `ClienteEvolution.cs:47` | Corpo de erro da Evolution repassado ao cliente no 502 |
| 14 | 🟡 | `Program.cs` (pipeline) | Sem cabeçalhos de segurança (`nosniff`, `X-Frame-Options`, CSP, `Referrer-Policy`) |
| 15 | ⚪ | vários | Nove itens de endurecimento — seção 5 |

Nada classificado como 🔴 no código da aplicação.

---

## 3. Detalhamento

### 🔴 1 — Evolution e Postgres publicados em portas do host

**Onde:** `docker-compose.yml:26` (`"${POSTGRES_PORT:-5433}:5432"`) e `docker-compose.yml:70`
(`"${EVOLUTION_PORT:-8082}:8080"`).

**O que permite.** A forma `porta:porta` do Docker faz *bind* em `0.0.0.0`. Numa máquina com IP
público e sem firewall, os dois serviços ficam acessíveis pela internet:

- **Evolution API na 8082** — protegida apenas pelo header `apikey`
  (`AUTHENTICATION_API_KEY`). Quem tiver essa chave envia mensagem pelo número do cliente, lê o
  QR code, cria e apaga instâncias. É o pior desfecho previsto no escopo desta auditoria.
- **Postgres na 5433** — protegido apenas pela senha. Contém todas as conversas de todos os
  tenants, sem RLS: uma conexão bem-sucedida ignora o isolamento inteiro, que vive na aplicação.

**Dificuldade.** Trivial se as portas estiverem abertas — um `nmap` na faixa acha. A partir daí é
força bruta ou vazamento da chave/senha. Não exige nenhuma falha no código.

**Atenuante.** O arquivo se declara "dependências de **desenvolvimento**", e as portas altas
(5433, 8082) foram escolhidas para evitar conflito local. A falha só se materializa se ele for
reaproveitado em produção — que é exatamente o que costuma acontecer no primeiro deploy.

**Como corrigir.** Não publicar porta nenhuma dessas duas em produção. A API alcança ambos pela
rede interna do Docker; nada precisa passar pelo host. Se o acesso externo à Evolution for
necessário para o pareamento do QR, exponha atrás do mesmo proxy da API, com TLS e caminho
restrito. Feche 5432/5433/8080/8082 no firewall independentemente disso — defesa em profundidade,
não alternativa.

---

### 🟠 2 — Anonimização não alcança o conteúdo das mensagens

**Onde:** `ServicoContatos.AnonimizarAsync` (`src/Nexora.Infra/Servicos/ServicoContatos.cs:485`)
e o comentário em `:503-505`, que declara o que fica de fora.

**O que permite.** A operação limpa `nome`, `telefone`, `email`, `observacoes` e `origem_detalhe`
do contato, e mascara a trilha de auditoria (`MascararTrilhaAsync`, `:530`) — essa parte está
bem-feita e reconhece explicitamente que "se essas linhas ficassem, a anonimização não teria
acontecido". O mesmo raciocínio não foi aplicado a quatro lugares:

| onde | que PII sobrevive |
|---|---|
| `mensagens.payload_raw` | `data.key.remoteJid` = **o telefone**; `data.pushName` = **o nome no WhatsApp** |
| `mensagens.texto` | o conteúdo integral da conversa, nas palavras da própria pessoa |
| `conversas.ultima_mensagem_previa` | cópia do último texto |
| disco de mídia (`midia/emp-{id}/`) | fotos e documentos que a pessoa enviou |

**Medido no banco de desenvolvimento:** 1.903 de 3.057 mensagens têm `payload_raw`, e uma amostra
confirma `remoteJid` e `pushName` presentes.

**O que um atacante consegue.** Não é um vetor de ataque — é um risco de conformidade. Um pedido
de eliminação sob a LGPD respondido com "os dados foram removidos" seria **falso**: o telefone do
titular continua recuperável com uma consulta.

**Dificuldade.** N/A. É o comportamento normal do sistema.

**Como corrigir.** Estender a anonimização para: apagar `payload_raw` das mensagens do contato,
substituir `mensagens.texto` por um marcador (`[removido]`, preservando a linha e o carimbo de
tempo para o histórico continuar coerente), limpar `ultima_mensagem_previa` e apagar os arquivos
de mídia daquele contato. Decidir conscientemente o que **preserva** — a contagem de mensagens e
as datas provavelmente devem ficar, como já ficam etapa e valor — e escrever essa decisão junto do
método, no mesmo estilo do bloco que trata da trilha.

---

### 🟠 3 — Sem retenção para `payload_raw`, mensagens e mídia

**Onde:** ausência. Existe expurgo para a trilha (`ExpurgoTrilha.cs`) e para as entregas de
webhook (`MotorWebhooks.cs:167`), e nenhum para as três maiores fontes de dado pessoal.

**O que permite.** `payload_raw` guarda o evento cru do WhatsApp para sempre. É o dado mais
sensível do sistema (telefone, nome, conteúdo) e o menos usado: serve para depurar o processador
nos primeiros dias de uma mensagem. Guardá-lo por anos amplia a superfície de qualquer vazamento
futuro sem nenhum ganho operacional, e contraria a minimização que a LGPD exige.

**Como corrigir.** Um expurgo periódico que zere `payload_raw` de mensagens acima de um prazo
curto (30 a 90 dias) — sem apagar a mensagem, que é o registro de negócio. Para mensagem e mídia,
definir uma política de retenção explícita e contratá-la com o cliente; o mesmo job já existente
serve de molde.

---

### 🟠 4 — Token sem revogação

**Onde:** `src/Nexora.Api/Seguranca/GeradorToken.cs:15` (`HorasDeValidade = 12`) e a ausência de
qualquer verificação por requisição em `ContextoEmpresaHttp.cs`.

**O que permite.** O JWT carrega `sub`, `empresa_id` e `role`, é assinado (HS256) e validado
quanto a emissor, audiência, assinatura e validade. Não há `jti`, lista de revogação, nem
releitura do usuário a cada requisição. Consequências, todas confirmadas no código:

- **Usuário desativado continua entrando** por até 12 h. `ServicoAutenticacao` bloqueia o
  *login* de usuário inativo (`:71`), mas isso só roda no login.
- **Rebaixamento de papel não tem efeito** até o token expirar. Um gestor rebaixado a vendedor
  mantém `role: gestor` no token e continua passando em `[Authorize(Roles="dono,gestor")]`.
- **Token roubado vale 12 h**, sem forma de cortar.

**Dificuldade.** Exige já ter o token (roubo de dispositivo, XSS em outra aplicação da mesma
origem, log indevido) ou ser o próprio usuário recém-desativado — este último é o cenário
realista: alguém demitido com o painel aberto.

**Como corrigir.** O caminho barato é um carimbo `senha_alterada_em` / `credenciais_versao` no
usuário, incluído como claim e conferido no `OnTokenValidated`; qualquer desativação ou troca de
papel incrementa e invalida os tokens antigos. O caminho completo é *refresh token* com
sessão persistida. Reduzir as 12 h ajuda mas não resolve — a janela continua existindo.

---

### 🟠 5 — Sem HTTPS nem HSTS na aplicação

**Onde:** `src/Nexora.Api/Program.cs`, pipeline entre as linhas 258 e 266. Não há
`UseHttpsRedirection` nem `UseHsts`.

**O que permite.** Se o Kestrel for exposto diretamente (sem proxy TLS), todo o tráfego vai em
claro: JWT no header `Authorization`, senha no `POST /api/auth/login`, conteúdo das conversas.
Interceptação na rede entrega conta e dado pessoal.

**Atenuante.** É o desenho esperado para quem roda atrás de proxy — terminar TLS no Nginx/Caddy/
Cloudflare e falar HTTP na rede interna é prática corrente. O risco é o deploy sem proxy.

**Como corrigir.** Garantir TLS no proxy e negar o acesso direto ao Kestrel pela rede (item do
checklist, seção 4). Se algum dia a aplicação for exposta sem proxy, `UseHttpsRedirection` +
`UseHsts` passam a ser obrigatórios. Ligar HSTS antes de ter certificado válido em todos os
subdomínios costuma dar mais problema que solução — decidir junto com a topologia.

---

### 🟡 6 — Entropia do segredo do webhook não é validada

**Onde:** `src/Nexora.Api/Controllers/WebhookController.cs:13` e `:34`.

**O que permite.** O segredo é a **única** barreira do webhook — a Evolution não assina o payload,
e o próprio código registra isso. Quem acertar o segredo consegue injetar eventos forjados:
"o contato respondeu" (criando contatos e mensagens falsas), ACKs de entrega, mudanças de status
de conexão. Não é leitura de dado alheio, mas é escrita arbitrária em tabela de negócio.

A checagem no boot é apenas "não vazio". Compare com a chave JWT, que **derruba a aplicação** se
tiver menos de 32 caracteres (`Program.cs:33`). Um `Webhook:Segredo=teste` sobe sem reclamação.

**Dificuldade.** Depende inteiramente de quem configurou. Com o valor sugerido no `.env.example`
(`openssl rand -hex 24`, 192 bits) é inatacável; com um valor curto, é força bruta a 300
tentativas por minuto por IP.

**Como corrigir.** Validar no boot com o mesmo rigor da chave JWT: comprimento mínimo e recusa de
subir se não atender. Alternativa mais forte: aceitar apenas requisições vindas do IP/rede da
Evolution, já que ela é um serviço conhecido.

---

### 🟡 7 — Webhook sem limite de corpo

**Onde:** `WebhookController.cs:40-41` — `new StreamReader(Request.Body)` +
`ReadToEndAsync`, sem `[RequestSizeLimit]`.

**O que permite.** Vale o limite padrão do Kestrel (~30 MB). Cada requisição materializa o corpo
inteiro numa `string` (UTF-16: ~60 MB de heap para 30 MB de corpo), e o rate limit do webhook
permite 300 por minuto **por IP**. Um punhado de IPs derruba o processo por pressão de memória.

Note o contraste: os dois endpoints de upload de mídia têm `[RequestSizeLimit]`
(`ConversasController.cs:80` e `:100`). O webhook, que é anônimo, não tem.

**Dificuldade.** Precisa do segredo do webhook — sem ele o 401 acontece **antes** da leitura do
corpo, o que limita bem o alcance. É por isso que é 🟡 e não mais.

**Como corrigir.** `[RequestSizeLimit]` compatível com o maior evento real da Evolution (algumas
centenas de KB é folgado) e leitura em *stream* em vez de `ReadToEndAsync`.

---

### 🟡 8 — Payload completo em log de erro

**Onde:** `src/Nexora.Infra/Evolution/ProcessadorEventoEvolution.cs:59` —
`log.LogError(ex, "Falha ao processar webhook. Payload: {Payload}", payloadJson)`.

**O que permite.** Qualquer falha de processamento grava o evento cru no log: telefone, nome e
texto da mensagem do cliente. Logs costumam ir para um agregador externo, com retenção e controle
de acesso diferentes dos do banco — dado pessoal sai do perímetro sem que ninguém tenha decidido
isso.

**Atenuante.** Só ocorre no caminho de erro, e a decisão de logar o corpo é defensável para
depurar formato desconhecido. O resto do sistema é cuidadoso: `PoliticaLogin.MascararEmail`
mascara e-mail em todo log de autenticação, e nenhum segredo é registrado (verificado).

**Como corrigir.** Logar o `messageType`, o `instance` e um hash do payload em vez do corpo. Se o
corpo for indispensável, gravar em armazenamento separado com retenção curta, não no log da
aplicação.

---

### 🟡 9 — Enumeração cross-tenant de e-mail

**Onde:** `src/Nexora.Infra/Servicos/ServicoEquipe.cs:50` — a checagem de e-mail no convite usa
`IgnoreQueryFilters()` sem filtro por empresa (intencional: o índice único é global) e devolve
`"Já existe usuário com este e-mail."`.

**O que permite.** Um dono de tenant descobre, e-mail a e-mail, quem mais é usuário do Nexora —
inclusive em empresas concorrentes. É informação sobre a base de clientes do produto, não sobre o
dado de um tenant específico.

**Dificuldade.** Requer conta de dono. O rate limit geral (100/min por usuário) permite ~144 mil
tentativas por dia.

**Como corrigir.** A colisão precisa mesmo ser detectada — o índice é global e o `INSERT`
estouraria com erro ilegível. O que dá para mudar é a **resposta**: uma mensagem que não
distinga "existe em outra empresa" de outros motivos de recusa, por exemplo tratando o convite
como enviado e resolvendo a colisão fora do caminho síncrono.

---

### 🟡 10 — Sem política de autorização padrão e sem limite para anônimo

**Onde:** `Program.cs:109` (`AddAuthorization()` sem `FallbackPolicy`) e
`RateLimitingConfig.cs:78` (`GetNoLimiter("anon")`).

**O que permite.** Duas ausências que se somam. Um controller novo sem `[Authorize]` nasce
**público**; se também não tiver `[EnableRateLimiting]`, nasce **ilimitado**. Nenhuma das duas
condições produz erro, aviso ou teste vermelho — a rota simplesmente fica aberta.

Hoje isso está sob controle: todos os 29 controllers foram conferidos, os cinco anônimos são
deliberados (`cadastro`, `captura`, `convite`, `demonstracao`, `redefinir`), o `AuthController`
expõe só o login e o `WebhookController` se protege pelo segredo. Duas ações anônimas ficaram sem
política nomeada — `GET /api/convite/{token}` e `GET /api/redefinir/{token}` —, mas o token tem
192 bits e não é enumerável; sobra o custo de consulta ao banco sem teto.

**Como corrigir.** Definir `FallbackPolicy = RequireAuthenticatedUser()` e marcar as cinco rotas
públicas com `[AllowAnonymous]` (quatro já têm). Trocar o `GetNoLimiter("anon")` por uma janela
por IP, para que o padrão do anônimo seja limitado e a exceção seja explícita.

---

### 🟡 11 — Recorte por papel inconsistente entre relatório e lista

**Onde:** `ServicoRelatorios.cs:724` (`ResponsavelEfetivo`) contra `ServicoContatos.ListarAsync`
(`:40-51`) e `ServicoFunil.ColunaAsync` (`:95`).

**O que permite.** Os relatórios forçam o vendedor a ver **apenas os próprios números**:
`ResponsavelEfetivo` ignora o `responsavelId` pedido e substitui pelo id do usuário. `MeuDia` e o
feed de atividades também recortam.

`Contatos`, `Funil` e `Caixa` **não têm recorte nenhum** — e `GET /api/contatos?responsavelId=X`
aceita o id de qualquer colega. O vendedor lista os contatos do outro, com valor e situação, e
reconstrói à mão o relatório que a tela lhe negou.

**O que isso significa.** Não é vazamento entre tenants — tudo está dentro da mesma empresa. É uma
**inconsistência do modelo de autorização**: ou a caixa compartilhada é a regra (e aí a restrição
do relatório é enfeite que dá falsa sensação de confidencialidade), ou o vendedor deve ser
recortado (e aí faltam três telas).

**Dificuldade.** Trocar um parâmetro na URL.

**Como corrigir.** Decidir qual dos dois é o produto e aplicar em todos os caminhos. Sabendo que a
caixa é deliberadamente compartilhada ("Não atribuídas", "quem pegar assume"), o mais provável é
que o certo seja **remover** a restrição do relatório e assumir a transparência — o que é uma
decisão de produto, não de segurança.

---

### 🟡 12 — Mídia validada pelo MIME declarado

**Onde:** `src/Nexora.Core/Whatsapp/ValidadorMidia.cs:22` — *whitelist* fechada de sete MIMEs,
teto de 16 MB, sem inspeção dos bytes.

**O que permite.** O tipo vem do payload da Evolution (entrada) ou do `ContentType` do upload
(saída). Um arquivo HTML declarado `image/png` é aceito, gravado e depois servido por
`GET /api/midia/{id}` com `Content-Type: image/png` — vindo do mesmo campo declarado.

**Atenuantes, e são fortes:**
- `MidiaController.cs:39` usa `File(stream, mime, nomeArquivo)`, que emite
  `Content-Disposition: attachment` — o navegador **baixa** em vez de renderizar;
- o endpoint exige `[Authorize]` e o isolamento é o query filter (`:28`), então só quem já está
  logado no tenant alcança;
- a raiz do armazenamento é blindada contra travessia de caminho (`ArmazenamentoDisco.cs:44`),
  com verificação de que o caminho resolvido continua dentro da raiz.

O que sobra é o caso de `MidiaNome` nulo, em que a resposta vai sem `attachment`, combinado com a
ausência de `X-Content-Type-Options: nosniff` (achado 14): um navegador que farejar conteúdo pode
renderizar HTML na origem da API. Como o JWT não vive em cookie, o dano é limitado.

**Como corrigir.** Conferir os *magic bytes* contra o MIME declarado antes de gravar, e enviar
`nosniff` em todas as respostas. Servir mídia de um domínio separado é a correção estrutural,
e já está prevista para a fase 2 (URL assinada contra object storage).

---

### 🟡 13 — Corpo de erro da Evolution repassado ao cliente

**Onde:** `src/Nexora.Infra/Evolution/ClienteEvolution.cs:47`, `:159`, `:240` —
`throw new IntegracaoWhatsAppException($"Evolution API respondeu {status}: {corpo}")`, e
`FiltroRegraDeNegocio.cs:26` devolve essa mensagem como corpo do 502.

**O que permite.** A resposta de erro de um serviço interno chega ao navegador do usuário.
Tipicamente é uma mensagem inócua ("instance not found"), mas é conteúdo de terceiro fora do nosso
controle: mudanças de versão da Evolution podem passar a incluir caminhos, versões ou fragmentos
de configuração.

**Como corrigir.** Registrar o corpo no log e devolver ao cliente uma mensagem estável
("O WhatsApp não respondeu. Tente de novo em instantes.").

---

### 🟡 14 — Sem cabeçalhos de segurança

**Onde:** `Program.cs`, pipeline. Não há `X-Content-Type-Options`, `X-Frame-Options` /
`frame-ancestors`, `Content-Security-Policy` nem `Referrer-Policy`.

**O que permite.** Farejamento de conteúdo (compõe com o achado 12), *clickjacking* do painel e
vazamento de URL com identificadores pelo cabeçalho `Referer` para terceiros.

**Como corrigir.** Um middleware curto com `nosniff`, `X-Frame-Options: DENY` e
`Referrer-Policy: strict-origin-when-cross-origin`. A CSP é mais trabalhosa (o painel é Angular
compilado) e pode vir depois, começando em modo `report-only`.

---

## 4. Checklist de hospedagem

Itens que precisam estar certos na infraestrutura **mesmo com o código correto**.

**Rede — bloqueia o lançamento**

- [ ] Postgres **sem porta publicada** no host. Só a rede interna do Docker.
- [ ] Evolution API **sem porta publicada** no host. A API a alcança pela rede interna.
- [ ] Firewall negando 5432, 5433, 8080 e 8082 de fora, mesmo com as portas fechadas no compose.
- [ ] Kestrel inacessível diretamente: só o proxy reverso fala com ele.

**TLS**

- [ ] Certificado válido no proxy, HTTP redirecionando para HTTPS.
- [ ] `Cors:Origens` com o domínio real do painel — em produção o código já usa lista explícita
      (`Program.cs:207`), mas a lista precisa ser preenchida.
- [ ] `RateLimit:ConfiarProxyReverso = true` **somente** se o proxy for o único caminho de
      entrada. Com ele ligado e uma rota alternativa aberta, qualquer um forja `X-Forwarded-For`
      e escapa do limite de login (o código zera `KnownProxies`, `Program.cs:181`).

**Segredos — nenhum no repositório**

- [ ] `ConnectionStrings:Nexora`, `Jwt:Chave`, `Evolution:ApiKey`, `Webhook:Segredo`,
      `Cadastro:ChaveAdministracao`, `Email:Senha` por variável de ambiente ou cofre.
- [ ] `Jwt:Chave` com 32+ caracteres aleatórios (o boot recusa menos, mas não julga qualidade).
- [ ] `Webhook:Segredo` gerado com `openssl rand -hex 24`. É a única barreira do webhook e o boot
      **não** valida entropia (achado 6).
- [ ] `Cadastro:ChaveAdministracao` definida — vazia desliga o cadastro, que é o padrão seguro.
- [ ] `Demonstracao:Habilitado` **ausente ou false** em produção.
- [ ] `EVOLUTION_DB_PASS` trocada: o `.env.example` e o compose têm `evolution_dev` como padrão.

**Aplicação**

- [ ] `ASPNETCORE_ENVIRONMENT=Production`. Em Development o Swagger é publicado
      (`Program.cs:254`) e o CORS aceita qualquer origem de *loopback*.
- [ ] `/health` alcançável só pelo orquestrador — ele revela o estado do banco.
- [ ] Volume de mídia com backup e cifrado em repouso: contém foto e documento de cliente.
- [ ] Backup do Postgres cifrado, com retenção declarada. **Contém dado pessoal de terceiros** e
      não há nada no repositório que trate disso.
- [ ] Log da aplicação com retenção curta e acesso restrito (achado 8).
- [ ] Instância **única**: o rate limit é em memória e a mídia é disco local. Duas instâncias
      dobram o limite e servem 404 alternado para anexos.

---

## 5. Limites conhecidos

Aceitos conscientemente, com a razão registrada no próprio código.

| Limite | Onde está escrito | Por que é aceitável hoje |
|---|---|---|
| Rate limit em memória | `RateLimitingConfig.cs:47` | Instância única. Duas instâncias dobram o teto — está documentado e é premissa do desenho |
| Mídia em disco local, sem expurgo | `ArmazenamentoDisco.cs:18` | Fase 1. Object storage com URL assinada está previsto e a interface já isola a troca |
| Segredo do webhook na query string | `WebhookController.cs:32` | A Evolution não suporta header nem assinatura. Mitigável desligando o log de query string no proxy |
| Comparação do segredo do webhook não é em tempo constante | `WebhookController.cs:32` | O segredo já viaja na URL; um oráculo de tempo pela rede sobre 192 bits não é praticável |
| Isolamento na aplicação, não em RLS | `IContextoEmpresa.cs:14` | Consistente e verificado nos 61 pontos. RLS seria defesa em profundidade, não correção |
| PBKDF2-SHA256 com 100 mil iterações | `HashSenha.cs:13` | Abaixo da recomendação atual do OWASP (600 mil). O formato guarda o número de iterações, então dá para elevar sem quebrar hash existente |
| Timing residual no login | `ServicoAutenticacao.cs:57-69` | O caminho "senha errada" faz um `SaveChanges` a mais que o "usuário inexistente". Diferença de ~1 ms contra ~50 ms de PBKDF2 — não sustenta enumeração |
| Endpoints sem paginação | `ServicoEtapas`, `ServicoEquipe`, `ServicoFeriados` | Conjuntos limitados por natureza (etapas de funil, equipe, feriados do ano) |
| Busca por `LIKE '%termo%'` | `ServicoContatos.cs:151` | Varredura sequencial, mas recortada por tenant e paginada. Exige índice trigram quando um tenant passar de dezenas de milhares de contatos |

**Cinco usos de `IgnoreQueryFilters` sem `empresa_id` explícito** — todos verificados e todos
seguros, porque operam sobre chave global obtida de consulta já recortada. Listados para que
ninguém precise reauditá-los, e para que fiquem sob suspeita se algum dia a chave passar a vir da
requisição:

- `ProcessadorEventoEvolution.cs:73` — conexão por `instance_name` (chave global; é assim que o
  tenant é descoberto)
- `ProcessadorEventoEvolution.cs:490` — conversa por `contato_id`, com o contato já resolvido
  dentro do tenant
- `ServicoConversas.cs:99` e `:381` — texto de erro pelo `mensagemId` que o envio **acabou de
  criar**; não vem da requisição
- `DadosFollowUp.cs:139` e `:146` — lembrete e contato por id vindo de consulta já filtrada
- `ServicoAutenticacao.cs:30` e `ServicoEquipe.cs:147` — usuário por e-mail (índice único global;
  é o que torna o login possível)

---

## 6. Dependências vulneráveis

### .NET — `dotnet list package --vulnerable --include-transitive`

Os três projetos que vão para produção estão limpos:

```
Nexora.Core  — nenhum pacote vulnerável
Nexora.Infra — nenhum pacote vulnerável
Nexora.Api   — nenhum pacote vulnerável
```

Só o projeto de testes acusa, por dependência transitiva:

| Pacote | Versão | Sev | Aviso |
|---|---|---|---|
| `System.Net.Http` | 4.3.0 | High | GHSA-7jgj-8wvc-jh57 |
| `System.Text.RegularExpressions` | 4.3.0 | High | GHSA-cmhx-cq75-c4mj |

**Não são embarcados.** `Nexora.Tests` não é publicado nem entra na imagem — o `Dockerfile` copia
apenas o resultado de `dotnet publish src/Nexora.Api`. Vale resolver mesmo assim, porque um
`--vulnerable` que sempre acusa algo treina a equipe a ignorar o comando. Costuma sair com uma
referência direta às versões atuais desses dois pacotes no `.csproj` de teste.

### npm — `npm audit`

```
3 vulnerabilidades moderadas
@hono/node-server <2.0.5  — travessia de caminho no serve-static em Windows via `%5C`
  └─ @modelcontextprotocol/sdk 1.25.0–1.29.0
      └─ @angular/cli 20.3.14–20.3.33
```

**Cadeia exclusiva de ferramenta de desenvolvimento.** O `@angular/cli` não entra no *bundle*
servido ao navegador; o `serve-static` afetado é o do servidor de desenvolvimento. A correção
(`npm audit fix --force`) sobe para `@angular/cli@21`, que é mudança de maior versão — não vale
fazer por causa disto. O risco real é para quem roda `ng serve` numa máquina Windows exposta na
rede, o que não é o caso do deploy.

Nenhuma vulnerabilidade de dependência chega a produção.
