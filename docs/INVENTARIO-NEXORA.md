# Inventário de reaproveitamento — Recupera → Nexora

Auditoria somente-leitura do repositório `f:/projetos/recupera` (commit `8ccae40`).
Nenhum arquivo do Recupera foi alterado, criado, movido ou removido.

Todos os caminhos abaixo são relativos à raiz do repositório do Recupera.

---

## 1. Resumo executivo

Aproveitamento estimado para a fase 1 do Nexora: **~40% do esforço técnico total**, distribuído
de forma muito desigual — cerca de **70% da camada de infraestrutura** (multi-tenant, auth,
Evolution, resiliência de envio, transversais, design system) e praticamente **0% da camada de
domínio** (funil, contato, etapa, Meu Dia, dashboard), que precisa ser escrita do zero.

As três maiores economias de tempo:

1. **Integração Evolution API** — cliente HTTP, pareamento QR/código, webhook, ACK, mídia,
   canonicalização de telefone (nono dígito) e `docker-compose` já sintonizado. É o que mais
   custa acertar por tentativa e erro; aqui já está pago.
2. **Resiliência de envio** — o protocolo grava→dispara→confirma, o dedupe por `wa_message_id`
   e o reserve-defer. Resolvem bugs invisíveis (mensagem duplicada, eco do próprio envio,
   webhook reentregue) que só aparecem em produção.
3. **Andaime transversal** — isolamento multi-tenant por query filter, JWT + travamento de
   conta, rate limit nativo, filtro global de erro e o `styles.css`. Tira o Nexora do zero até
   a primeira tela funcional.

---

## 2. Tabela geral

| Item | Caminho no repo | Categoria | Nível | Esforço |
|---|---|---|---|---|
| `IContextoEmpresa` (contrato de tenant) | `src/Recupera.Core/IContextoEmpresa.cs` | 1 | **A** | baixo |
| `ContextoEmpresaHttp` (tenant via claims) | `src/Recupera.Api/ContextoEmpresaHttp.cs` | 1 | **A** | baixo |
| Query filter global por tenant | `src/Recupera.Infra/Persistencia/RecuperaDbContext.cs` | 1 | **D** | médio |
| Coluna `empresa_id` denormalizada + índices | `schema_mvp_cobranca.sql` | 1 | **D** | médio |
| Cadastro de tenant + dono + conexão | `src/Recupera.Infra/Servicos/ServicoEmpresa.cs:17-68` | 1 | **B** | baixo |
| `HashSenha` (PBKDF2 100k) | `src/Recupera.Core/Seguranca/HashSenha.cs` | 2 | **A** | baixo |
| `PoliticaLogin` (lockout, hash dummy, máscara) | `src/Recupera.Core/Seguranca/PoliticaLogin.cs` | 2 | **A** | baixo |
| `GeradorToken` (JWT) | `src/Recupera.Api/Seguranca/GeradorToken.cs` | 2 | **B** | baixo |
| `ServicoAutenticacao` (login + bloqueio) | `src/Recupera.Infra/Servicos/ServicoAutenticacao.cs` | 2 | **B** | baixo |
| Wiring JWT + `access_token` do SignalR | `src/Recupera.Api/Program.cs:61-99` | 2 | **A** | baixo |
| Convite por link (aceite público) | `src/Recupera.Api/Controllers/ConviteController.cs` + `ServicoEmpresa.cs:104-155,241-270` | 2 | **B** | baixo |
| Redefinição de senha por link | `src/Recupera.Api/Controllers/RedefinicaoController.cs` + `ServicoEmpresa.cs:276-317` | 2 | **B** | baixo |
| Papéis (`Dono`/`Gestor`/`Atendente`) | `src/Recupera.Core/Entidades/Enums.cs:96-110` | 2 | **B** | baixo |
| `IClienteWhatsApp` (gateway) | `src/Recupera.Core/Motor/IClienteWhatsApp.cs` | 3 | **A** | baixo |
| `ClienteEvolution` (cliente HTTP completo) | `src/Recupera.Infra/Evolution/ClienteEvolution.cs` | 3 | **A** | baixo |
| `EventosEvolution` (DTOs do webhook) | `src/Recupera.Infra/Evolution/EventosEvolution.cs` | 3 | **A** | baixo |
| `ProcessadorEventoEvolution` (handler webhook) | `src/Recupera.Infra/Evolution/ProcessadorEventoEvolution.cs` | 3 | **B** | alto |
| `CanonicalizarTelefone` + `VariantesTelefone` | `src/Recupera.Core/Motor/RenderizadorTemplate.cs:42-75` | 3 | **A** | baixo |
| `WebhookController` (token na URL, sempre 200) | `src/Recupera.Api/Controllers/WebhookController.cs` | 3 | **A** | baixo |
| `ServicoConexoes` (QR, pareamento, status, saúde) | `src/Recupera.Infra/Servicos/ServicoConexoes.cs` | 3 | **B** | médio |
| Hub SignalR + notificador | `src/Recupera.Api/Realtime/HubPainel.cs` | 3 | **B** | baixo |
| `INotificadorPainel` (contrato de push) | `src/Recupera.Core/Motor/INotificadorPainel.cs` | 3 | **B** | baixo |
| Tela de conexão (QR + polling) | `frontend/recupera-painel/src/app/paginas/conexao/` | 3 | **B** | médio |
| `EnviadorMensagem` (dono do protocolo) | `src/Recupera.Core/Servicos/EnviadorMensagem.cs` | 4 | **B** | médio |
| `IDadosMensagem` + `DadosMensagem` (reserva SQL) | `src/Recupera.Infra/Persistencia/DadosMensagem.cs` | 4 | **B** | médio |
| Índice `uq_msg_wa_id` (dedupe de webhook/eco) | `schema_mvp_cobranca.sql:751-762` | 4 | **A** | baixo |
| Índice de teto diário por destinatário | `schema_mvp_cobranca.sql:739-750` | 4 | **D** | baixo |
| Índice anti-reenvio por etapa | `schema_mvp_cobranca.sql:730-737` | 4 | **D** | baixo |
| Espaçamento entre envios (`IntervaloEntreEnvios`) | `src/Recupera.Core/Motor/MotorReguaCobranca.cs:8-21,203-206` | 4 | **A** | baixo |
| Mapeamento explícito snake_case (sem convention pack) | `src/Recupera.Infra/Persistencia/RecuperaDbContext.cs:7-12` | 5 | **D** | médio |
| Enums nativos Postgres + `MapEnum` no data source | `src/Recupera.Infra/ServicosInfra.cs:21-41` | 5 | **B** | baixo |
| `Pagina<T>` e `PaginaCursor<T>` | `src/Recupera.Core/Servicos/Dtos/Comum.cs` | 5 | **A** | baixo |
| Paginação por cursor (valor, não offset) | `src/Recupera.Infra/Servicos/ServicoInbox.cs:177-199` | 5 | **D** | médio |
| Anonimização LGPD (sem delete físico) | `src/Recupera.Core/Entidades/Devedor.cs:26-29` | 5 | **D** | médio |
| `FiltroRegraDeNegocio` + `RegraDeNegocioException` | `src/Recupera.Api/FiltroRegraDeNegocio.cs`, `src/Recupera.Core/Servicos/RegraDeNegocioException.cs` | 6 | **A** | baixo |
| `RateLimitingConfig` (5 políticas, 429 + Retry-After) | `src/Recupera.Api/Seguranca/RateLimitingConfig.cs` | 6 | **A** | baixo |
| CORS com credenciais + `ForwardedHeaders` | `src/Recupera.Api/Program.cs:120-143` | 6 | **A** | baixo |
| Options POCO por seção + user-secrets | `src/Recupera.Api/Program.cs:22-34,105-118` | 6 | **A** | baixo |
| Agendador diário (BackgroundService) | `src/Recupera.Api/Servicos/AgendadorExpurgo.cs` | 6 | **A** | baixo |
| `interceptor-token` (token + 401 + 429) | `frontend/.../nucleo/seguranca/interceptor-token.ts` | 7 | **B** | baixo |
| `throttle-login` (contagem regressiva do 429) | `frontend/.../nucleo/seguranca/throttle-login.ts` | 7 | **A** | baixo |
| Guards de rota | `frontend/.../nucleo/seguranca/guarda-*.ts` | 7 | **B** | baixo |
| `AuthServico` (signals + localStorage) | `frontend/.../nucleo/servicos/auth.servico.ts` | 7 | **A** | baixo |
| `api-base.ts` | `frontend/.../nucleo/api-base.ts` | 7 | **B** | baixo |
| `styles.css` (design system completo) | `frontend/recupera-painel/src/styles.css` | 7 | **B** | baixo |
| Shell + sidebar + badge realtime | `frontend/.../layout/shell/` | 7 | **B** | médio |
| `RealtimeServico` (SignalR no cliente) | `frontend/.../nucleo/servicos/realtime.servico.ts` | 7 | **A** | baixo |
| Mecânica da thread de conversa (cursor + âncora) | `frontend/.../paginas/caixa/caixa.ts` | 7 | **B** | alto |
| `download.ts` (CSV no browser) | `frontend/.../nucleo/download.ts` | 7 | **A** | baixo |
| `RotuloAck` (0..4 do WhatsApp) | `frontend/.../nucleo/rotulos.ts:49-62` | 7 | **A** | baixo |
| `grafico-linha` (SVG sem biblioteca) | `frontend/.../nucleo/graficos/grafico-linha.ts` | 7 | **B** | baixo |
| Estrutura de módulos (nucleo/paginas/layout, zoneless) | `frontend/recupera-painel/src/app/` | 7 | **D** | baixo |
| `CalendarioRegua` (dia permitido / próximo dia útil) | `src/Recupera.Core/Feriados/CalendarioRegua.cs` | 8 | **A** | baixo |
| `CalculadoraFeriados` (nacionais + Páscoa) | `src/Recupera.Core/Feriados/CalculadoraFeriados.cs` | 8 | **A** | baixo |
| `ServicoFeriados` (seed anual idempotente) | `src/Recupera.Infra/Servicos/ServicoFeriados.cs` | 8 | **B** | baixo |
| `AgendadorMotor` (disparo diário no fuso de negócio) | `src/Recupera.Api/Servicos/AgendadorMotor.cs` | 8 | **B** | baixo |
| Janela de envio (hora + bitmask de dias) | `src/Recupera.Core/Entidades/Empresa.cs:21-33` | 8 | **B** | baixo |
| `MotorReguaCobranca` (motor da régua) | `src/Recupera.Core/Motor/MotorReguaCobranca.cs` | 8 | **D** | alto |
| Semáforo "aguardando resposta > 4h" | `src/Recupera.Infra/Servicos/ServicoDashboard.cs:158-176` | 8 | **B** | médio |
| `docker-compose` (Evolution + Postgres dela + MinIO) | `docker-compose.yml` | 9 | **A** | baixo |
| `.env.example` | `.env.example` | 9 | **B** | baixo |
| `global.json` (pin do SDK) | `global.json` | 9 | **A** | baixo |
| `BancoTeste` (fixture com rollback por teste) | `tests/Recupera.Tests/Integracao/BancoTeste.cs` | 9 | **B** | baixo |
| `.sql` como fonte da verdade + `migracao_*.sql` | raiz do repo | 9 | **D** | médio |

**Não encontrados no Recupera** (não há o que reaproveitar; ver §7): Dockerfile, pipeline de CI
(`.github/workflows/` existe mas está **vazio**), health check, envio de e-mail, log estruturado
além do `ILogger` padrão, biblioteca de validação, refresh token, soft delete, migrations do EF,
script de seed, componente de toast/notificação.

---

## 2b. Visão por tela / funcionalidade

A tabela acima é organizada por categoria técnica. Esta é o mesmo conteúdo recortado por
funcionalidade do produto, para quem pergunta "e a tela X, aproveita?".

| Funcionalidade | Existe no Recupera? | Nível | O que muda |
|---|---|---|---|
| **Login** | `AuthController`, `ServicoAutenticacao`, `PoliticaLogin`, `HashSenha`, `PolLogin`, `paginas/login/`, `throttle-login` | **A** (back) / **B** (front) | Backend copia inteiro. Front: remover credenciais chumbadas (ver §7.1). Falta refresh token. |
| **Recuperação de senha** | `RedefinicaoController`, `ServicoEmpresa.{GerarReset,ResetInfo,RedefinirSenha}Async`, `PolSenha`, `paginas/redefinir/` | **B** | Falta o "esqueci minha senha" (só o dono gera link, sem e-mail). Separar as colunas de token do convite. |
| **Convite de usuário** | `ConviteController`, `ServicoEmpresa.{Convidar,AceitarConvite}Async`, `PolConvite`, `paginas/convite/` | **B** | Mesmo buraco: link copiado à mão, sem e-mail. |
| **Equipe** | `EmpresaController` (5 endpoints, só dono), `ServicoEmpresa`, `paginas/equipe/` | **B** | Remover `comissaoPct` + `HistoricoComissaoUsuario`. Renomear `atendente`→`vendedor`; `gestor` talvez some. Manter as travas: anti-lockout, ≥1 dono ativo, limite conta ativos+convidados. |
| **Trocar a própria senha** | `ContaController`, `paginas/conta-senha/` | **A** | Nada. 25 + 54 linhas, zero domínio. |
| **Caixa de entrada** | `InboxController`, `ServicoInbox` (581 linhas), `caixa.servico.ts`, `paginas/caixa/` (526 linhas) | **B** (alto) | ~50% aproveita. Ver detalhamento abaixo. |
| **Tick de status da mensagem** | `nucleo/tick-status/tick-status.ts` | **A** | Nada. SVG inline, 5 estados, variante para fundo escuro. |
| **Conexão / WhatsApp** | `ConexaoController` (10 endpoints), `ServicoConexoes` (228 linhas), `paginas/conexao/` | **B** | Trabalho é *cortar*: sem `is_padrao`, sem `usuario_id`, sem CRUD de múltiplas. Fica ~metade. |
| **Empresa (tenant)** | Entidade `Empresa`; **não há tela**. `EmpresaController` é o controller da Equipe. Cadastro fica no `SuperController`. | **B** | Definir como um cliente novo entra (self-service não existe). `CadastrarAsync` serve. Remover campos de backoffice. |
| **Dashboard** | `DashboardController`, `ServicoDashboard` (314 linhas) | **D** | Os 4 números do Nexora são outros. Aproveita: a separação `status()` (barato, polling) vs `dashboard()` (caro), e `AguardandoResposta4hAsync` (semente do semáforo). |
| **Meu Dia** | Não existe | — | Escrever do zero. |
| **Funil kanban / contato / etapa** | Não existe | — | Escrever do zero. |

**Recorte da Caixa de entrada** — é o item que mais exige bisturi:

- **Mecânica da lista — B, alto valor.** `mesclarTopo()` (`caixa.ts:165-177`) recarrega só a
  primeira página e a mescla preservando a cauda já paginada, com dedupe por id. É a resposta
  certa para lista que se reordena em tempo real, e não se acerta de primeira. Junto vem a
  âncora de scroll com três modos: `auto` (rola se já estava no fim), `preservar` (ACK não mexe
  na rolagem) e o chip "↓ Nova mensagem" quando o operador subiu na thread.
- **Atribuição — B.** "Atribuição, não fila": dono opcional por conversa; sem dono = Aguardando,
  com dono = Atendendo; **responder sem dono atribui automaticamente**; assumir conversa de outro
  devolve 409. É exatamente o multi-atendente da fase 1 do Nexora. As abas (Aguardando resposta /
  Minhas / Não atribuídas / Todas / Resolvidas) servem quase iguais.
- **Descartar — E.** As duas granularidades (thread por devedor × ticket por recebível) não
  existem no Nexora, onde contato = conversa. Some `AbrirOuObterTicketAsync`, some `recebivel_id`
  no ticket, some o `resolver(retomarRegua)`, some o painel de contexto lateral (score, recebíveis,
  acordo ativo) e a resposta rápida com recebível em foco.

---

## 3. Detalhamento por categoria

### 3.1 Infraestrutura multi-tenant

**Correção de premissa:** o Recupera **não usa `operador_id`**. A coluna de tenant é
`empresa_id`, presente em toda tabela que a aplicação consulta por conta própria (117 ocorrências
em código e schema; `operador_id` tem **zero**). O termo "operador" aparece 153 vezes, mas com
dois sentidos distintos, nenhum deles de coluna de tenant:

- no painel do tenant, "operador" = a **pessoa** que atende (sinônimo informal de usuário);
- no backoffice `/super`, "operador" = o **cliente do SaaS**, isto é, a assessoria — daí
  `metricas_operador_mes`, `MetricaOperadorMes`, `super-operadores`.

**Consequência prática para o Nexora: não há renomeação a fazer no núcleo.** `empresa_id` já é o
nome certo — no Nexora o tenant também é uma empresa (a PME cliente). A herança nominal a evitar é
outra: **não trazer o vocabulário "operador"**, que é ambíguo já no Recupera. Se um dia o Nexora
ganhar backoffice, os tenants devem se chamar `cliente` ou `conta`, nunca "operador".

**Como o isolamento funciona hoje.** `RecuperaDbContext` aplica `HasQueryFilter(x => x.EmpresaId
== _contexto.EmpresaId)` em cada entidade de tenant; `_contexto` é o `IContextoEmpresa`, resolvido
por escopo e implementado por `ContextoEmpresaHttp`, que lê o claim `empresa_id` do JWT. Não há RLS
no Postgres — a barreira é inteiramente o EF. O mecanismo é **genérico**: nenhuma regra de cobrança
está embutida nele.

- `IContextoEmpresa` e `ContextoEmpresaHttp` — **A**. Copiar literalmente. `ContextoEmpresaHttp`
  documenta duas armadilhas reais que valem ouro: (a) `empresa_id` não está no mapa de claims do
  JwtBearer e chega intacto; (b) `MapInboundClaims=true` remapeia `sub` para
  `ClaimTypes.NameIdentifier`, por isso o código lê os dois — foi esse bug que deixava
  `criado_por` sempre NULL.
- **Query filter global** — **D**. O mecanismo é genérico, mas os 632 linhas de
  `RecuperaDbContext` são 100% entidades de cobrança. Replicar o padrão, não o arquivo.
- **A armadilha central a herdar junto com o padrão:** fora de requisição autenticada (login,
  webhook, job) `EmpresaId` é `0`, e a consulta volta **vazia em silêncio, sem erro nenhum**.
  Todo caminho sem tenant precisa de `.IgnoreQueryFilters()` + filtro explícito por `empresaId`.
  Isso está corretamente aplicado em `ServicoAutenticacao`, `DadosMotor`, `DadosMensagem` e
  `ProcessadorEventoEvolution`. É a maior fonte de bug silencioso do desenho — no Nexora, cubra
  com teste de integração desde o primeiro dia.
- **Índices** — o schema declara `empresa_id` como primeira coluna de quase todo índice composto
  (`ix_recebiveis_estado`, `ix_tickets_inbox`, `ix_msg_timeline`). Padrão correto, **D**.
- `ServicoEmpresa.CadastrarAsync` — **B**. Cria tenant + usuário dono + conexão padrão numa
  transação lógica só. Serve direto ao onboarding do Nexora; remover a validação de
  `instance_name` duplicado contra `empresas` (herança do desenho antigo de 1 instância por
  empresa) e manter só a checagem contra `conexoes`.

### 3.2 Autenticação e autorização

Tudo aqui é genérico. Nenhum papel do Recupera é de cobrança: `Dono` / `Gestor` / `Atendente` são
papéis de SaaS comum — não existe "negociador" nem "supervisor de carteira".

- `HashSenha` — **A**. PBKDF2-SHA256, 100k iterações, formato `pbkdf2$iter$salt$hash` com as
  iterações embutidas (permite aumentar sem invalidar senhas antigas), comparação em tempo
  constante. 50 linhas, zero dependência. Copiar como está.
- `PoliticaLogin` — **A**. Bloqueio persistente por conta (10 falhas → 15 min, cross-IP, o que
  o rate limit por IP não pega), `HashDummy` para equalizar timing quando o e-mail não existe, e
  máscara de e-mail para log. Copiar como está.
- `ServicoAutenticacao` — **B**. A lógica é genérica; ajustes: (a) a checagem
  `usuario.Empresa.Ativo` é o liga/desliga do SaaS — mantenha, mas a mensagem "Empresa inativa"
  serve; (b) o `.IgnoreQueryFilters()` no login é **obrigatório** e está bem comentado no arquivo.
- `GeradorToken` — **B**. Remover `ClaimSuper` / `ClaimEscopo` / `ClaimAdminId` (backoffice, fora
  da fase 1). Validade de 12h é razoável.
- **Wiring JWT + SignalR** (`Program.cs:78-91`) — **A**. O `OnMessageReceived` que resgata o
  token da query string em `/hub` é indispensável: o browser não deixa o WebSocket mandar header
  `Authorization`. Sem isso o hub conecta anônimo, o cliente nunca entra no grupo da empresa e o
  realtime **não chega, sem erro nenhum**.
- **Convite e reset por link** — **B**. Fluxo público completo (`GET` info do token → `POST`
  define senha → devolve JWT para login imediato), token hex de 24 bytes, expiração de 7 dias no
  convite e 2h no reset, ambos reusando as mesmas colunas `token_convite`/`convite_expira`.
  Funciona bem, mas **não há envio de e-mail**: o dono copia o link e repassa por fora. O Nexora
  vai querer e-mail — isso é código novo.
- **Ausências a suprir no Nexora:** não há refresh token (sessão de 12h e acabou), não há
  "esqueci minha senha" auto-serviço (só reset gerado pelo dono), não há 2FA.

### 3.3 Integração Evolution API

**A área de maior valor do inventário.** Cinco arquivos concentram tudo que o Nexora precisa para
falar WhatsApp, e quase nada ali é de cobrança.

**`ClienteEvolution` (351 linhas) — nível A.** É o único ponto do sistema que fala com a Evolution;
as tabelas dela nunca são lidas. Cobre:

| Operação | Endpoint | Observação |
|---|---|---|
| Enviar texto | `POST message/sendText/{instance}` | devolve `key.id` (correlaciona o ACK) |
| Enviar mídia | `POST message/sendMedia/{instance}` | base64 sem prefixo `data:` |
| Baixar mídia recebida | `POST chat/getBase64FromMediaMessage/{instance}` | por `wa_message_id` |
| Resolver número real | `POST chat/whatsappNumbers/{instance}` | **o nono dígito** — ver abaixo |
| Estado da instância | `GET instance/connectionState/{instance}` | `open`/`connecting`/`close`/`nao_criada`/`offline` |
| Criar + conectar (QR ou pareamento) | `POST instance/create` + `GET instance/connect/{instance}[?number=]` | 403/409 = já existe, segue |
| Detalhes da conexão | `GET instance/fetchInstances?instanceName=` | `ownerJid`, nome e foto do perfil |
| Desconectar | `DELETE instance/logout/{instance}` | mantém a instância |

Três coisas nesse arquivo justificam sozinhas o reaproveitamento:

1. **Armadilha do nono dígito.** Muita conta brasileira de WhatsApp vive sem o nono dígito mesmo
   com o número tendo 9. Mandar com o 9 para uma conta que não o tem cai num JID fantasma: a
   mensagem fica `PENDING` e **nunca chega**. `ResolverNumeroAsync` pergunta o JID real à Evolution
   antes de todo envio, e falha alto se o número não existe no WhatsApp — melhor que silêncio.
2. **Parse defensivo.** A Evolution varia o formato entre versões: QR aninhado em `qrcode` ou nos
   campos de topo; `fetchInstances` devolvendo array ou objeto, campos no topo ou dentro de
   `instance`. `LerQr` e `ObterDetalhesInstanciaAsync` cobrem as duas formas e nunca lançam.
3. **2xx sem `key.id` não é erro.** Se a Evolution responde 200 sem id, o cliente loga e devolve
   string vazia em vez de lançar — lançar faria o motor reenviar e o contato receber duas vezes.

*Único ajuste:* a mensagem de erro em `ClienteEvolution.cs:99` diz "confira o cadastro do devedor".
Trocar por "confira o cadastro do contato".

**`RenderizadorTemplate.CanonicalizarTelefone` / `VariantesTelefone` — nível A.** Copiar sem tocar.
São 30 linhas que decidem se o produto funciona: o cadastro vem `(11) 98888-7777` (sem DDI) e o
WhatsApp entrega `5511988887777@s.whatsapp.net` (com DDI). Se os dois lados não canonicalizarem
igual, a resposta do contato **não casa com ninguém** — sem conversa, sem badge, sem erro no log.
As *variantes* cobrem o nono dígito na direção inversa (mensagem recebida sem o 9).
⚠️ O resto da classe (`Renderizar`, com `{{credor}}`/`{{vencimento}}`/`{{dias_atraso}}`) é
cobrança pura — nível **E**. Separe os dois na extração.

**`EventosEvolution` — nível A.** DTOs do payload do webhook, só os campos usados; o resto vai
inteiro para `payload_raw` (auditoria/replay). Inclui o detalhe de que o texto pode vir de
`conversation`, `extendedTextMessage.text` ou da legenda da mídia.

**`ProcessadorEventoEvolution` (428 linhas) — nível B, esforço alto.** É o handler do webhook.
Vale copiar o esqueleto e amputar, não reescrever. Divisão honesta:

*Aproveitável quase intacto:*
- Derivar o tenant de `data.instance` casando com `conexoes.instance_name` (não com uma tabela
  de empresa) — desenho certo, já preparado para N conexões.
- `ProcessarConexaoAsync` (`connection.update`): ao conectar descobre o número real via
  `ownerJid`, captura nome/foto de perfil e detecta **troca de número** (guarda o antigo em
  `numero_anterior` para a tela avisar). Ao cair, marca `desconectado` — o que serve de freio.
- `ProcessarAckAsync` (`messages.update`): o ACK numérico é fonte de verdade e **só avança**
  (`WHERE ack IS NULL OR ack < novo`), porque os webhooks chegam fora de ordem — um
  `DELIVERY_ACK` atrasado não pode sobrescrever um `READ` já recebido. Mapa `ERROR=0 … READ/
  PLAYED=4`.
- `InserirMensagemAsync`: `INSERT … ON CONFLICT DO NOTHING RETURNING id`. Devolve NULL quando a
  mensagem já existia — que cobre de uma vez o webhook reentregue **e** o eco do próprio envio.
- `ReceberAnexoAsync`: baixa, valida contra whitelist e tamanho, e grava com **chave determinística
  pelo `wa_message_id`** — com chave aleatória cada reentrega deixaria um objeto órfão no storage.
- Nunca lançar (o webhook precisa devolver 2xx senão a Evolution reentrega em loop eterno); grupos
  e broadcast ignorados.

*A remover:*
- `AbrirOuObterTicketAsync` inteiro. A heurística "sobre qual dívida ele está falando?" não existe
  no Nexora: um contato = uma conversa. É o trecho mais acoplado do arquivo.
- O ramo "número fora da carteira" pode virar "criar contato automaticamente" no Nexora — decisão
  de produto, mas o gancho (`SemCadastroAsync` no painel) é bom e já existe.

**`WebhookController` — nível A.** Lê o corpo cru e entrega ao serviço; o controller não conhece o
formato da Evolution. Autenticação por segredo na query string, porque **a Evolution não assina o
payload** — essa é a única barreira. Responde 200 mesmo quando o processamento falha, de propósito.

**`ServicoConexoes` — nível B.** Cobre criar/editar/remover conexão, definir padrão (índice único
parcial `uq_conexao_padrao`), QR, pareamento por número, status com persistência guardada (só
escreve quando muda — a tela faz polling de 3s), backfill de número quando o webhook se perdeu,
reconhecimento de troca de número e um endpoint de saúde (enviadas hoje / na fila). Para o Nexora,
que tem **1 número por empresa**, isso simplifica: remover `is_padrao`, a atribuição por usuário
(`usuario_id`) e o rebaixamento da padrão. Esforço médio, mas para cortar, não para escrever.

**SignalR** (`HubPainel`, `NotificadorSignalR`, `INotificadorPainel`) — **B**. Cada conexão entra
no grupo `empresa-{id}` lendo o claim direto de `Context.User` (não via `IHttpContextAccessor` —
dentro de WebSocket não há `HttpContext` e o accessor devolveria null, deixando o usuário fora do
grupo sem erro). Os quatro eventos (`mensagemRecebida`, `ticketAberto`, `statusMensagem`,
`semCadastro`) precisam virar o vocabulário do Nexora; os records `MensagemPainel`/`TicketPainel`
carregam credor e valor e mudam.

**`docker-compose.yml` — nível A.** Stack autocontida: Evolution + Postgres dela (isolado, opaco)
+ MinIO + job de criação de bucket. O bloco mais valioso são os flags `DATABASE_SAVE_*`: os
defaults da Evolution são todos `true`, e `DATABASE_SAVE_DATA_HISTORIC=true` **importa o WhatsApp
inteiro do cliente ao conectar** — volume e exposição LGPD à toa. Aqui só a sessão é salva.
Copiar renomeando containers e volumes.

### 3.4 Resiliência de envio

Não é um Outbox canônico com fila e worker de drenagem. É um padrão mais simples e, para o volume
do produto, adequado: **a própria tabela `mensagens` é a outbox**, e o "worker" é a rodada diária.

**O protocolo (`EnviadorMensagem`) — nível B.** Regra que não se inverte:
`grava no banco → só então chama o WhatsApp → confirma (ou registra a falha)`. Disparar antes de
gravar significa que um crash entre as duas etapas reenvia a mensagem na próxima rodada.

Na falha, **a linha fica**, com o erro gravado. Apagar liberaria a invariante — e um POST que na
verdade chegou (mas deu timeout na resposta) viraria mensagem duplicada. O reenvio reaproveita a
mesma linha.

Métodos: `EnviarDaReguaAsync` (reserva + posta), `ReservarDaReguaAsync` (reserva sem postar),
`ResponderAsync` / `ResponderMidiaAsync` (manual, sem invariante), `ReenviarAsync`,
`ReenviosPendentesAsync`, `InstanciaConectadaAsync`. Só os dois primeiros têm nome de régua; a
mecânica serve igual ao lembrete de follow-up do Nexora.

**`DadosMensagem` — nível B.** `INSERT … ON CONFLICT DO NOTHING RETURNING id` em SQL cru, porque o
EF não expressa `ON CONFLICT` e a alternativa (capturar `DbUpdateException` por linha) envenena o
ChangeTracker e usa exceção como fluxo de controle numa operação que barra de propósito na maior
parte das vezes. Detalhe fácil de perder: `ConfirmarEnvioAsync` aplica `NULLIF(id,'')` porque duas
strings vazias colidiriam no índice único.

**As invariantes, garantidas por índice único parcial — o banco é o árbitro, não a aplicação:**

| Índice | O que garante | Nível p/ Nexora |
|---|---|---|
| `uq_msg_wa_id` (`instance_name`, `wa_message_id`) | dedupe de webhook reentregue **e** eco do próprio envio | **A** — copiar literal |
| `uq_msg_teto_diario_devedor` (`devedor_id`, `data_disparo`) | no máximo 1 automática por destinatário por dia | **D** — replicar com `contato_id` |
| `uq_msg_anti_reenvio` (`recebivel_id`, `template_id`, `dias_offset`) | não repete a mesma etapa na mesma dívida | **D** — a chave muda inteira |

O teto diário é a defesa anti-spam que impede queimar o número — no Nexora, com lembretes de
follow-up, a mesma proteção continua fazendo sentido (um contato não deve receber dois lembretes
automáticos no mesmo dia). O anti-reenvio depende de `(recebível, template, offset)`, que não tem
equivalente direto; a versão Nexora seria `(contato, lembrete_id)`.

**Reserve-defer.** Quando a janela está fechada ou a conexão caiu, a linha é **reservada sem
POST** (`enviada_em` NULL) e carimbada com `data_disparo = próximo dia útil`. A próxima rodada
dentro da janela drena os pendentes. Isso preserva a data-alvo exata sem duplicar. Padrão muito bom
— nível **D** para replicar.

**Ausências reais:** não há backoff exponencial, não há dead letter queue, não há fila em memória
nem worker dedicado. A "política de retry" é: a rodada diária varre reservas não despachadas dos
últimos `JanelaReenvioDias` (default 3) e tenta de novo. Se a Evolution ficar fora do ar mais de 3
dias, a mensagem se perde silenciosamente. Se o Nexora quiser algo mais forte, o padrão a herdar
é o protocolo e as invariantes — a política de retry precisa ser escrita.

**Acoplamento ao domínio:** médio. `EnviadorMensagem` e `DadosMensagem` dependem da entidade
`Mensagem`, que carrega `recebivel_id`, `template_id`, `regua_cobranca_id` e `dias_offset`. No
Nexora essas colunas viram `contato_id` / `lembrete_id` (ou somem). O corpo dos métodos muda pouco.

### 3.5 Camada de dados

- **Mapeamento explícito** — **D**. `HasColumnName` linha a linha, sem `EFCore.NamingConventions`,
  de propósito: mapeamento explícito falha alto e claro no boot se divergir do banco, e não
  adiciona dependência. Decisão defensável; herdar a decisão, não o arquivo.
- **Enums nativos do Postgres** — **B**. Precisam ser registrados nos **dois** lugares:
  `HasPostgresEnum` no `OnModelCreating` **e** `MapEnum` no `NpgsqlDataSourceBuilder`
  (`ServicosInfra.cs:23-38`). Esquecer o segundo quebra em runtime. A estrutura do
  `AdicionarInfra` copia bem; a lista de 14 enums é toda de cobrança.
- **Migrations** — **D, com ressalva.** Não há migrations do EF. O `schema_mvp_cobranca.sql` (58 KB)
  é a fonte da verdade, aplicado à mão, e as mudanças chegam como 16 arquivos `migracao_*.sql`
  soltos na raiz, sem ordem explícita nem controle de aplicação. Funciona para dev solo; **não
  recomendo replicar no Nexora**. Use migrations do EF ou uma ferramenta de versionamento de
  schema desde o começo.
- **Soft delete: não existe.** Não há coluna de exclusão lógica em lugar nenhum. O que existe é
  **anonimização LGPD** (`Devedor.AnonimizadoEm`): zera a PII, apaga os anexos e preserva o
  histórico. Padrão bom, nível **D** — o Nexora vai precisar do equivalente para contatos.
- **Auditoria** — apenas `criado_em` / `atualizado_em` em quase toda tabela, preenchidos **à mão**
  em cada serviço. Não há interceptor, nem override de `SaveChanges`, nem `criado_por` genérico
  (só onde o domínio pediu: `acordos.criado_por`, `historico_comissao.alterado_por`). Nível **D** —
  e vale a pena o Nexora fazer melhor, com um interceptor de `SaveChanges`.
- **Paginação** — dois modelos, ambos úteis: `Pagina<T>` (offset, para listas estáveis) e
  `PaginaCursor<T>` (valor, para listas que se reordenam em tempo real). Os records: **A**. A
  implementação do cursor em `ServicoInbox.ConversasAsync` (ordena por
  `(ultima_mensagem_em DESC, id DESC)`, o mesmo par do cursor) é **D** — a ideia transfere, o
  código está entrelaçado com tickets e recebíveis.
- ⚠️ **Armadilha conhecida:** `ConversasAsync` materializa **todos** os tickets do status antes de
  paginar em memória (`ServicoInbox.cs:108-113`). O próprio comentário admite que "Resolvidas
  cresce". No Nexora, com funil e histórico, faça a paginação no SQL desde o início.

### 3.6 Camadas transversais

- **`FiltroRegraDeNegocio` + `RegraDeNegocioException`** — **A**. Um `IExceptionFilter` traduz
  exceções esperadas para HTTP num lugar só: `RegraDeNegocioException` → 400 (entrada inválida) ou
  409 (conflito de estado, via flag `Conflito`); `EnvioWhatsAppException` → 502. Os serviços lançam
  sem conhecer HTTP e os controllers ficam sem `try/catch`. Padrão limpo e o Nexora usa Evolution
  também, então até o caso 502 serve. Copiar.
- **`RateLimitingConfig`** — **A**. Rate limiter nativo do .NET 8, em memória, cinco políticas:
  geral (100/min por **usuário logado**), login (5/min por IP), senha (3/15min por IP+token),
  convite (5/10min por IP), webhook (300/min por IP). Resposta 429 com `Retry-After` e mensagem
  idêntica em qualquer caso (nunca revela se o e-mail existe). Copiar; só trocar os nomes das
  políticas. ⚠️ É **em memória** — só funciona com instância única. Escalar horizontal exige
  backplane distribuído, e isso está marcado como TODO no próprio arquivo.
- **CORS + ForwardedHeaders** — **A**. `AllowCredentials` é incompatível com `AllowAnyOrigin`, daí
  a origem explícita; `WithExposedHeaders("Retry-After")` porque o header não é CORS-safelisted e
  sem ele o Angular não lê os segundos do 429. `UseForwardedHeaders` como primeiro middleware, com
  `ConfiarProxyReverso` desligado em dev (senão um cliente forjando o header vira qualquer IP).
  Detalhes que custam uma tarde cada para descobrir.
- **Configuração por ambiente** — **A**. Options POCO por seção (`OpcoesJwt`, `OpcoesEvolution`,
  `OpcoesMotor`, `OpcoesRateLimit`, …) ligados via `cfg.GetSection(...).Get<T>()`, segredos em
  user-secrets, e validação no boot (a chave JWT menor que 32 caracteres derruba a aplicação com
  mensagem acionável). Bom padrão.
- **Agendamento de job** — **A** para `AgendadorExpurgo`, que é a forma mais limpa:
  `BackgroundService` → calcula o tempo até a próxima hora-alvo → `Task.Delay` → `CreateScope`
  (o serviço é Scoped, o host é Singleton) → `try/catch` que **nunca** deixa a exceção subir,
  porque isso derrubaria o loop e o job pararia de rodar em silêncio até o próximo deploy.
  ⚠️ Sem lock distribuído: com duas instâncias, o job roda duas vezes.
- **Log estruturado: não há** além do `ILogger<T>` padrão com template de mensagem. Sem Serilog,
  sem correlation id, sem sink externo. O que o Nexora pode herdar é a **disciplina**: mensagens
  com placeholders nomeados, nunca logar senha, e-mail sempre mascarado (`PoliticaLogin.
  MascararEmail`).
- **Health check: não há.** Nenhum `MapHealthChecks`. O Nexora vai precisar de um (banco +
  Evolution) para qualquer deploy sério.
- **Validação: não há biblioteca.** Sem FluentValidation, sem DataAnnotations. A validação é manual
  dentro dos serviços, lançando `RegraDeNegocioException` com mensagem em português voltada ao
  usuário final ("Dê um nome à conexão."). Nível **D** — o padrão é coerente e as mensagens são
  boas; não há código de infraestrutura para copiar.
- **Envio de e-mail: não há.** Nenhuma dependência SMTP/SendGrid/MailKit. Convite e reset de senha
  são links copiados à mão. Lacuna que o Nexora precisa preencher.

### 3.7 Frontend Angular

Angular moderno: componentes standalone, **zoneless change detection**, signals, rotas com
`loadComponent` (lazy), `pt-BR` registrado. Estrutura: `nucleo/` (api-base, servicos, seguranca,
modelos, rotulos, graficos) + `paginas/` + `layout/shell/`. Sem NgModules, sem NgRx, sem biblioteca
de componentes. A estrutura em si é **D** (replicar a organização), e vários arquivos concretos
saem em **A**.

- **`styles.css` (193 linhas) — B, alto valor.** Design system inteiro em variáveis CSS: tokens de
  cor, `.cartao`, `.btn` (4 variantes), `.tabela`, `.selo` (4 estados), `.vazio`, `.erro`,
  `.carregando`, `.skel` (shimmer), `.overlay`/`.modal`, utilitários. Zero domínio. O ajuste é
  trocar a paleta (`--ink` verde-escuro, `--paper` bege, `--amber`) pela identidade do Nexora — o
  resto da folha vale como está. ⚠️ Inclui um comentário explicando por que `.tabela th.num`
  precisa reforçar o alinhamento (especificidade classe+elemento vence classe sozinha).
- **`interceptor-token.ts` — B.** Anexa o token, trata 401 (limpa sessão e redireciona) e 429 (
  dispara a contagem regressiva do botão de login). Remover o ramo de token `super` deixa o arquivo
  na metade do tamanho.
- **`throttle-login.ts` — A**, **guards — B** (renomear papéis), **`auth.servico.ts` — A**
  (trocar as chaves `recupera.token`/`recupera.usuario`), **`realtime.servico.ts` — A** (só os
  nomes dos eventos mudam; o `accessTokenFactory` e o "falhar aqui não pode quebrar a tela" ficam).
- **`api-base.ts` — B.** Três linhas, mas **hardcoded** para `https://localhost:7074`, sem arquivos
  de `environment`. Não replique assim: o Nexora precisa de configuração por ambiente desde o
  começo.
- **Shell — B.** Sidebar + `RouterLinkActive` + badge de não lidos que sobe sozinho no realtime +
  banner de WhatsApp caído com poll leve de 45s num endpoint enxuto separado do payload rico do
  dashboard. A separação `status()` (barato, polling) vs `dashboard()` (caro, sob demanda) é uma
  boa ideia para o Nexora copiar.
- **Mecânica da thread de conversa (`caixa.ts`, 526 linhas) — B, esforço alto.** É o que mais se
  parece com a caixa de entrada do Nexora: paginação por cursor com âncora de scroll, append em
  tempo real, tick de ACK por mensagem, envio de anexo. Vale extrair a mecânica; o painel de
  contexto lateral (score, recebíveis, acordo ativo) é descartável.
- **`download.ts` — A.** CSV no browser com separador `;` e BOM UTF-8 (Excel brasileiro).
- **`rotulos.ts`** — `RotuloAck` (0..4 do WhatsApp) é **A**; `RotuloEstado` e `RotuloStatusAcordo`
  são **E**. `DiasAtraso` é cobrança, mas a mecânica "dias desde uma data" serve ao semáforo — **D**.
- **Gráficos** — `grafico-linha` (SVG puro, sem biblioteca) é **B**; `funil-estados` é um funil
  horizontal por estado de dívida, **E** — não confundir com o kanban do Nexora, que é outra coisa.
- **`modelos.ts` (941 linhas) — E.** Espelha os DTOs do backend; quase tudo é cobrança.
- **Toast/notificação: não existe.** O padrão é `erro = signal('')` por página + classe `.erro`.
  Funciona, mas o Nexora provavelmente quer um componente de toast — código novo.

### 3.8 Automação por tempo

Pergunta central: **quanto do motor é genérico?** Resposta: o *esqueleto* e os *mecanismos de
proteção* são genéricos e valiosos; a *elegibilidade* é 100% cobrança.

**`MotorReguaCobranca` — D.** A estrutura da rodada:

```
para cada tenant ativo:
    tem conexão padrão?          → não: pula
    régua pausada no tenant?     → sim: pula
    calcula janela (dia + hora) e a próxima data permitida
    conexão pareada agora?       → não: reserva sem postar
    drena os pendentes das últimas N rodadas
    para cada regra ativa:
        data-alvo = hoje - offset
        para cada item elegível: reserva (+ posta) e espaça 3s
    exceção de um tenant não derruba a rodada dos outros
```

Cinco mecanismos que valem replicar literalmente no lembrete de follow-up do Nexora:

1. **Data-alvo calculada na aplicação, não no banco.** `hoje.AddDays(-offset)` comparado por
   igualdade contra a coluna — função sobre a coluna (`CURRENT_DATE - data_vencimento`) descarta o
   índice parcial e a varredura vira seq scan quando a base cresce.
2. **Freio por conexão.** Uma checagem por tenant por rodada; se o número caiu, reserva sem postar.
3. **Reserve-defer pela janela.** Já detalhado em §3.4.
4. **Espaçamento de 3s entre envios.** Mandar em lote é o jeito clássico de ter o número banido.
5. **Isolamento de falha por tenant.** `try/catch` por empresa dentro do laço.

O que **não** transfere: `RecebiveisElegiveisAsync` (estado `em_aberto`, `regua_pausada`,
`data_vencimento`, freio por ticket aberto, desempate por maior valor), o conceito de
`dias_offset`, e a renderização do template com placeholders de dívida.

**`CalendarioRegua` — A.** Duas funções puras e testadas: `DiaPermitido` (bitmask de dia da semana
+ feriado) e `ProximaDataPermitida` (desliza para o próximo dia útil, com trava de 370 iterações
contra dado ruim). Serve direto ao "não agendar follow-up para domingo". Renomear a classe.

**`CalculadoraFeriados` — A.** Feriados nacionais fixos + móveis derivados da Páscoa (computus de
Meeus/Butcher), com testes unitários cobrindo 2025–2030. Estrutura para estaduais pronta (só RN
semeado). 60 linhas, zero I/O. Se o Nexora quiser que lembretes não caiam em feriado, é de graça.
Se não quiser na fase 1, é fase 2 — mas custa quase nada trazer junto.

**`AgendadorMotor` — B.** O detalhe que vale: todo o agendamento roda no **fuso de negócio**
(`America/Sao_Paulo`), não no do servidor, com fallback para UTC-3 fixo se o id não existir no host
(o Brasil não tem horário de verão desde 2019). Sem isso, um servidor em UTC dispararia às 6h BRT,
fora da janela, e o motor só reservaria — nunca postaria. Bug caro de diagnosticar.

**Janela de envio** (`Empresa.JanelaHoraInicio` / `JanelaHoraFim` / `JanelaDiasSemana` bitmask,
default 8h–20h seg-sáb) — **B**. No Recupera é conformidade CDC; no Nexora vira simplesmente
"horário comercial da empresa". Mesma estrutura, outra justificativa.

**Semáforo de urgência — B, achado relevante.** O Recupera já tem o embrião:
`ServicoDashboard.AguardandoResposta4hAsync` conta os contatos cuja **última** mensagem é de
entrada e já passou de 4h — exatamente o "tempo sem resposta" do Nexora. Duas coisas a herdar:
(a) a técnica (`max(id)` por contato, depois filtra as linhas desses ids); (b) a regra de que o
alerta **só acende dentro da janela de atendimento**, para não piscar de madrugada. O que muda: no
Nexora vira faixas (verde/amarelo/vermelho) em vez de um contador de 4h.

### 3.9 Build e deploy

- **`docker-compose.yml` — A.** Já detalhado em §3.3. É o ativo mais reaproveitável desta categoria.
- **`.env.example` — B.** Documenta bem as variáveis; note que está **dessincronizado** do
  compose (`EVOLUTION_TAG=v2.2.3` no exemplo, `v2.3.7` no compose; `EVOLUTION_PORT=8080` vs `8081`;
  `EVOLUTION_DB_URI` não é mais usada). Copiar a estrutura, não os valores.
- **`global.json` — A.** Pin do SDK. Trivial e correto.
- **`BancoTeste` — B.** Fixture de teste de integração contra Postgres **real** (não in-memory),
  com transação sempre revertida por teste e `ICollectionFixture` para compartilhar o data source
  entre classes. É o jeito certo de testar `ON CONFLICT`, índices parciais e query filters — coisas
  que provider in-memory não reproduz. Padrão a herdar.
- **Dockerfile — NÃO EXISTE.** Nenhum no repositório.
- **Pipeline de CI — NÃO EXISTE.** O diretório `.github/workflows/` existe mas está vazio.
- **Scripts de migration — D.** `schema_mvp_cobranca.sql` como fonte da verdade + 16 arquivos
  `migracao_*.sql` soltos na raiz, aplicados à mão. Ver ressalva em §3.5.
- **Seed — quase inexistente.** O que há: bootstrap do primeiro admin da plataforma no
  `Program.cs:147-155` (idempotente, condicionado a `SuperAdmin:Senha` na config) e o seed anual de
  feriados disparado pelo agendador. Não há seed de dados de desenvolvimento.

---

## 4. Ordem sugerida de extração

Cada bloco entrega algo verificável. A ordem é de dependência, não de importância.

**Bloco 1 — Andaime (destrava tudo).**
`IContextoEmpresa` + `ContextoEmpresaHttp` → `HashSenha` + `PoliticaLogin` → `GeradorToken` +
wiring JWT do `Program.cs` → `RegraDeNegocioException` + `FiltroRegraDeNegocio` →
`RateLimitingConfig` → CORS + ForwardedHeaders → Options POCO por seção.
*Resultado: API que autentica, isola tenant e trata erro de forma coerente.*

**Bloco 2 — Dados.**
Estrutura do `DbContext` com query filter por `empresa_id` (entidades novas do Nexora) →
`AdicionarInfra` com enums nativos → `Pagina<T>` / `PaginaCursor<T>` → schema com `empresa_id`
como primeira coluna dos índices compostos.
*Resultado: persistência multi-tenant com o filtro global valendo. **Escreva aqui o teste de
integração que prova que o filtro barra cross-tenant** — usando o `BancoTeste`.*

**Bloco 3 — WhatsApp (o núcleo de valor).**
`docker-compose` da Evolution → `IClienteWhatsApp` + `ClienteEvolution` → `CanonicalizarTelefone` +
`VariantesTelefone` → `EventosEvolution` → `WebhookController` → `ProcessadorEventoEvolution`
amputado (manter conexão/ACK/dedupe/mídia, remover ticket-por-recebível) → `ServicoConexoes`
simplificado para 1 número.
*Resultado: pareia o número, recebe mensagem, casa com o contato, registra ACK. É o marco que
prova o produto.*

**Bloco 4 — Envio confiável.**
`EnviadorMensagem` → `IDadosMensagem` + `DadosMensagem` → índice `uq_msg_wa_id` → teto diário
adaptado a `(contato_id, data)` → espaçamento entre envios.
*Resultado: mensagem sai sem duplicar, sobrevive à Evolution fora do ar.*

**Bloco 5 — Frontend base.**
Estrutura `nucleo`/`paginas`/`layout` + zoneless + `pt-BR` → `styles.css` com a paleta do Nexora →
`auth.servico` + guards + `interceptor-token` (sem o ramo super) + `throttle-login` →
`RealtimeServico` + Hub SignalR → shell com sidebar e badge.
*Resultado: painel logável e navegável, com realtime.*

**Bloco 6 — Tempo.**
`AgendadorExpurgo` como agendador diário genérico → resolução de fuso de negócio do
`AgendadorMotor` → `CalendarioRegua` → janela de horário comercial → semáforo a partir de
`AguardandoResposta4hAsync`.
*Resultado: lembrete de follow-up e semáforo de urgência funcionando.*

**Fora dos blocos, escreva do zero:** contato, funil de 5 etapas, kanban, Meu Dia, dashboard de 4
números, e o e-mail transacional.

---

## 5. Não reaproveitar

| Item | Caminho | Motivo |
|---|---|---|
| `RenderizadorTemplate.Renderizar` | `src/Recupera.Core/Motor/RenderizadorTemplate.cs:15-34` | Placeholders são todos de dívida: `{{credor}}`, `{{valor}}`, `{{vencimento}}`, `{{dias_atraso}}`, `{{documento}}`. |
| `ServicoAcordo`, `DadosAcordo`, `AcordosController` | `src/Recupera.Core/Servicos/ServicoAcordo.cs`, `src/Recupera.Infra/Persistencia/DadosAcordo.cs` | Acordo, parcela, renegociação — não existem no Nexora. |
| Entidades `Acordo`, `ParcelaAcordo`, `Recebivel`, `Devedor`, `Credor`, `DividaLegadoDetalhe`, `Repasse`, `WebhookPsp` | `src/Recupera.Core/Entidades/` | Vocabulário de cobrança inteiro. |
| Pix / boleto / PSP | `ParcelaAcordo` (colunas `pix_*`, `psp_*`), `webhooks_psp` | Fora do escopo por definição. |
| `FaixasComissao`, `ComissaoHistorica`, `ComissaoHistoricaUsuario` | `src/Recupera.Core/Comissao/`, `src/Recupera.Infra/Servicos/` | Comissão sobre valor recuperado. |
| `ClassificadorScore` | `src/Recupera.Core/Servicos/ClassificadorScore.cs` | Score de confiança do devedor, derivado de acordos honrados/quebrados. |
| `ServicoCarteira`, `ServicoCredores`, `ImportadorCarteira`, `ImportadorCredores` | `src/Recupera.Infra/Servicos/`, `src/Recupera.Infra/Importacao/` | Carteira e credor não existem; o CSV importa dívidas. (Ver §6 — a mecânica do importador serve na fase 2.) |
| `ServicoRegua` + entidades `ReguaCobranca` / `TemplateMensagem` | `src/Recupera.Infra/Servicos/ServicoRegua.cs` | Régua configurável por offset é o coração da cobrança; o Nexora tem funil fixo e lembrete pontual. |
| `ServicoRelatorios`, `ServicoRelatoriosPlataforma`, `EscritorCsv` | `src/Recupera.Infra/Servicos/` | Relatórios avançados estão fora da fase 1. |
| Backoffice `/super` inteiro | `SuperController`, `ServicoBackoffice`, `ServicoPlataforma`, `AgendadorMetricas`, `frontend/.../paginas/super-*` | MRR, planos, trial, auditoria de admin. Produto diferente, fase futura. |
| `funil-estados` (gráfico) | `frontend/.../nucleo/graficos/funil-estados.ts` | Funil por estado de dívida — não é o kanban de vendas do Nexora. |
| `modelos.ts` | `frontend/.../nucleo/modelos.ts` | 941 linhas espelhando DTOs de cobrança. |
| Páginas `carteira`, `acordos`, `credores`, `credor-detalhe`, `regua`, `relatorios` | `frontend/.../paginas/` | Telas de domínio de cobrança. |

---

## 6. Fase 2 e 3

Valioso, mas fora da fase 1 — não extrair agora.

- **`ImportadorCarteira` / `ImportadorCredores`** (`src/Recupera.Infra/Importacao/`) — importação
  CSV com CsvHelper, resumo de erros agregado em JSONB, tabela de lotes com contagem
  ok/erro e download dos rejeitados. A **mecânica** (não o mapeamento de colunas) serve direto para
  importação de contatos quando isso entrar. Nível B na fase 2.
- **Anexos** (`ServicoAnexos`, `ArmazenamentoS3`, `ValidadorAnexo`, `AgendadorExpurgo`,
  `docker-compose` do MinIO) — envio e recebimento de PDF/JPG/PNG com whitelist, teto de 10 MB,
  hash SHA-256, URL assinada de 15 min e expurgo por retenção. Storage S3-compatível (MinIO em dev,
  R2 em prod). O Nexora vai querer isso, mas não na fase 1. `ValidadorAnexo` é **A** quando chegar
  a hora.
- **`CalculadoraFeriados` + `ServicoFeriados`** — se o Nexora não se importar com lembrete caindo
  em feriado na fase 1, adie. Custo de trazer depois é baixo (é função pura).
- **Backoffice `/super`** — quando o Nexora tiver mais de uma dezena de clientes, o desenho de
  métricas mensais materializadas por job noturno (`AgendadorMetricas`,
  `metricas_operador_mes` / `metricas_plataforma_mes`) e o token de plataforma separado do token de
  tenant (`ClaimSuper`, guarda e rotas próprias, interceptor que escolhe o token pela URL) são
  referências boas. Renomeie "operador" para "cliente" na travessia.
- **Multi-conexão por empresa** — `ServicoConexoes` já suporta N números com uma padrão e
  atribuição por usuário, e `ServicoInbox` já roteia a resposta pela conexão do ticket ("a resposta
  sai pelo número que o contato conhece"). Quando o Nexora passar de 1 número por empresa, o
  desenho está pronto.
- **Templates de mensagem / resposta rápida** — `ServicoInbox.RespostaRapidaAsync` renderiza um
  template com o registro em foco e devolve só o texto, para o usuário revisar antes de enviar.
  Bom padrão para respostas rápidas de vendas na fase 2.
- **Rate limit distribuído** — quando o Nexora escalar horizontalmente, o limiter em memória
  precisa de backplane. Já marcado como TODO no Recupera.

---

## 7. Observações sobre o Recupera

Detectadas durante a auditoria. **Nada foi implementado.** Listadas por severidade.

1. **Credenciais chumbadas na tela de login.** `frontend/.../paginas/login/login.ts:18-19` inicializa
   os campos com `email = signal('maria@t.com')` e `senha = signal('senhaforte123')`. Não é
   condicional a ambiente nem a build de desenvolvimento — o formulário vai para produção
   pré-preenchido com uma credencial real de teste. Para um produto em fase final de entrega, é o
   item a resolver antes do go-live.
2. **`.github/workflows/` está vazio — não há CI.** Nenhum build, teste ou lint automático roda em
   push. Com testes de integração que dependem de um Postgres com schema aplicado, o risco de
   regressão silenciosa é real num produto em fase final de entrega.
3. **Não há Dockerfile.** O deploy da API não está codificado em lugar nenhum do repositório. Só a
   Evolution e o MinIO têm receita (`docker-compose.yml`).
4. **Não há health check.** Nenhum endpoint para orquestrador ou monitor saber se a aplicação está
   viva e se o banco e a Evolution respondem.
5. **`api-base.ts` está apontando para HTTPS enquanto o comentário do próprio arquivo diz o
   contrário.** O comentário (linhas 1-9) explica em detalhe por que se usa HTTP em dev
   (`http://localhost:5280`), e a linha 10 exporta `https://localhost:7074`. Comentário e código
   discordam; um dos dois está errado e vai custar tempo de alguém.
6. **`.env.example` dessincronizado do `docker-compose.yml`.** Tag da Evolution (`v2.2.3` vs
   `v2.3.7`), porta (`8080` vs `8081`), e `EVOLUTION_DB_URI` que o compose não usa mais (o Postgres
   da Evolution virou container próprio). Quem seguir o `cp .env.example .env` cai em confusão.
7. **`ServicoInbox.ConversasAsync` materializa todos os tickets do status antes de paginar**
   (`ServicoInbox.cs:108-113`). O comentário no código já reconhece que "Resolvidas cresce". É uma
   bomba-relógio de performance no tenant que usar o produto por alguns meses.
8. **Retry de envio sem backoff e sem dead letter.** Reserva não despachada há mais de
   `JanelaReenvioDias` (default 3) simplesmente para de ser tentada, sem alerta. Se a Evolution
   ficar fora do ar num fim de semana prolongado, mensagens somem silenciosamente. O alerta
   `outbox` do dashboard conta pendentes, mas não distingue "vai ser tentada" de "expirou".
9. **Rate limit e agendadores assumem instância única.** O limiter é em memória (documentado) e os
   três `BackgroundService` não têm lock distribuído — com duas instâncias, a régua roda duas vezes.
   As invariantes do banco protegem contra mensagem duplicada, mas o expurgo e as métricas rodariam
   em duplicidade sem essa rede.
10. **`Feriado` tem query filter que admite globais, mas `TelefoneDevedor` não filtra por
   `EmpresaId` na navegação** — o filtro existe (`RecuperaDbContext.cs:267`) mas o
   `HasOne(x => x.Devedor)` não declara o relacionamento com `EmpresaId`, dependendo do filtro do
   `Devedor` em cascata. Funciona, mas é frágil a uma consulta que parta de `TelefonesDevedor`
   direto.
11. **`empresas.instance_name` ficou órfão.** O desenho migrou para `conexoes.instance_name`, mas a
    coluna antiga continua na entidade, no schema, com índice único, e ainda é validada em
    `ServicoEmpresa.CadastrarAsync` e `ServicoConexoes.CriarAsync`. É verdade duplicada — dois
    lugares para o mesmo fato.
12. **Ambiguidade do termo "operador".** No painel significa a pessoa; no `/super` significa o
    tenant. Aparece nos dois sentidos em código, telas e nomes de tabela (`metricas_operador_mes`).
    Vale um glossário no `CLAUDE.md`, mesmo sem renomear nada.
13. **Auditoria manual.** `criado_em`/`atualizado_em` são atribuídos à mão em dezenas de pontos.
    Basta um `SaveChanges` esquecer para a coluna mentir. Um interceptor resolveria de uma vez.
