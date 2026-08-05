# Inventário técnico do Nexora

Catálogo do que existe no código. Não é relatório de progresso — não diz o que falta nem avalia
se está completo. Para isso, ver os relatórios `docs/BLOCO-*.md`.

Levantado por leitura do repositório e consulta ao banco `nexora_teste` (schema com todas as
migrations aplicadas).

---

## 0. Sumário

| Seção | O que tem |
|---|---|
| [1. Glossário](#1-glossário-do-domínio) | O que cada termo significa **neste** código |
| [2. Fluxos ponta a ponta](#2-fluxos-ponta-a-ponta) | Os cinco caminhos principais, passo a passo |
| [3. Onde mexer para…](#3-onde-mexer-para) | Tarefa comum → arquivos a tocar |
| [4. Entidades e schema](#4-entidades-e-schema) | 11 entidades, 10 enums, 6 migrations |
| [5. Invariantes de banco](#5-invariantes-de-banco) | 12 índices parciais, 9 checks, 7 triggers |
| [6. Serviços](#6-serviços) | 15 na Infra, 6 no Core |
| [7. Endpoints](#7-endpoints) | 48 rotas, 6 públicas |
| [8. Jobs de fundo](#8-jobs-de-fundo) | 1 |
| [9. Integração Evolution](#9-integração-evolution) | 7 operações, 3 eventos |
| [10. Telas](#10-telas) | 15 rotas Angular |
| [11. Frontend transversal](#11-frontend-transversal) | Núcleo, guards, realtime, design system |
| [12. Configuração](#12-configuração) | 8 Options POCO, 5 segredos |
| [13. Testes](#13-testes) | 287, contra Postgres real |
| [14. Dependências](#14-dependências) | 8 NuGet, 2 npm além do Angular |
| [15. Build e deploy](#15-build-e-deploy) | Dockerfile, compose, sem CI |
| [16. Armadilhas](#16-armadilhas-conhecidas) | 17 decisões que quebram se "limpas" |
| [17. Dívidas e limites](#17-dívidas-e-limites-conhecidos) | Escala, atalhos, estado quebrado |

### Estrutura

```
src/Nexora.Core/     entidades, contratos, regras puras — não conhece EF nem HTTP
src/Nexora.Infra/    EF, SQL, Evolution, e-mail
src/Nexora.Api/      controllers, wiring, segurança, SignalR
tests/Nexora.Tests/  xUnit, integração contra Postgres real
frontend/nexora-painel/   Angular 20 standalone, zoneless, signals
```

### ⚠️ Estado do repositório neste levantamento

**O frontend não compila.** `paginas/meu-dia/meu-dia.ts` foi reescrito para lista única, mas
`meu-dia.html` e `meu-dia.css` continuam na versão de duas listas. O template chama `dia()`,
`conversas()`, `lembretes()`, `rotuloEspera()` e `hora()`, que não existem mais no componente.
`ng build` falha com `TS2339`. O backend está íntegro: `dotnet build` limpo, 287 testes verdes.

---

## 1. Glossário do domínio

| Termo | O que é **neste código** | Onde vive |
|---|---|---|
| **Tenant** | A empresa cliente do Nexora (uma PME). Coluna `empresa_id` em toda tabela de domínio. Nunca chamado de "operador" | `Empresa` |
| **Contato** | O lead/cliente. Dado **frio**: cadastro, origem, posição no funil, valor | `contatos` |
| **Conversa** | A thread de WhatsApp com um contato. Dado **quente**: cada mensagem escreve aqui. **1:1 com contato** (`uq_conversas_contato`) | `conversas` |
| **Mensagem** | Uma linha do chat, entrada ou saída. A tabela **é a outbox** do envio | `mensagens` |
| **Etapa** | Uma coluna do funil kanban. 5 linhas fixas semeadas no cadastro. Uma delas tem `e_ganho` | `etapas_funil` |
| **Lembrete** | O que fazer com um contato, e quando. Duas naturezas na mesma tabela: **automático** (dispara mensagem, entra no teto anti-spam) e **manual** (tarefa do vendedor) | `lembretes` |
| **`aguardando_desde`** | Instante da primeira mensagem de **entrada ainda não respondida**. Entrada chega → grava se estiver NULL; saída sai → volta para NULL. **Materializada**, não calculada | `conversas.aguardando_desde` |
| **Semáforo** | A cor de urgência da conversa, derivada de `aguardando_desde`. **Calculada no cliente**, nunca devolvida pela API — a cor envelhece entre requisições | `nucleo/semaforo.ts` |
| **Minutos úteis** | Tempo de espera **descontando** o que está fora da janela de atendimento e os feriados | `Core/Tempo/TempoUtil.cs` |
| **Janela de atendimento** | Horário comercial da empresa: hora início/fim + bitmask de dias (bit 0 = domingo) | `Empresa.Janela*` |
| **Meu Dia** | Leitura derivada de **duas fontes**: conversas esperando resposta + lembretes vencidos. **Não tem tabela** | `ServicoMeuDia` |
| **Reserva** | Linha de `mensagens` gravada **antes** do POST à Evolution, com `enviada_em` NULL. Se o POST falhar ou for adiado, a linha fica e a drenagem tenta depois | `EnviadorMensagem` |
| **Reserve-defer** | Reservar sem postar quando a janela está fechada ou a conexão caiu, carimbando `data_disparo` no próximo dia permitido | `MotorFollowUp` |
| **Drenagem** | Varredura das reservas pendentes (`enviada_em IS NULL`) para tentar postar de novo | `EnviadorMensagem.PendentesAsync` |
| **ACK** | Confirmação numérica do WhatsApp: 0=erro, 1/2=enviado, 3=entregue, 4=lido. **Só avança** | `ProcessadorEventoEvolution` |
| **Instância** | O nome da sessão **do lado da Evolution** (`instance_name`). Distinto de **conexão**, que é a linha no banco | `Conexao.InstanceName` |
| **Conexão** | O registro da empresa no Nexora apontando para uma instância. **Uma por empresa** na fase 1 | `conexoes` |
| **Teto diário** | No máximo um lembrete **automático com mensagem** por contato por dia. Defesa anti-banimento | `uq_lembrete_teto_diario` |
| **Tenant zero** | `EmpresaId == 0`, o valor fora de requisição autenticada. Faz o query filter devolver **vazio em silêncio** | `IContextoEmpresa` |

### Ambiguidades registradas

| Ambiguidade | Onde |
|---|---|
| **"Reserva"** significa duas coisas próximas: o *ato* de gravar antes de postar, e a *linha* resultante. O código usa `ReservarLembreteAsync` para o ato e "pendente" para a linha | `EnviadorMensagem` |
| **Dois modelos de paginação coexistem**, de propósito: `Pagina<T>` (offset + total, listas estáveis — contatos) e `PaginaCursor<T>` (por valor, listas que se reordenam — caixa, coluna do kanban). Escolher errado produz item pulado ou repetido | `Core/Servicos/Dtos/Comum.cs` |
| **"Tipo" tem dois eixos distintos**: `TipoAcao` (`Responder`/`Lembrete`, no Meu Dia — de onde a ação veio) e `OrigemLembrete` (`Automatico`/`Manual` — quem criou o lembrete). Não são a mesma classificação | `IServicoMeuDia`, `Enums.cs` |
| **Dois serviços de dashboard**: `ServicoDashboard` lê o banco; `ServicoDashboardDemo` gera dados fictícios. Rotas separadas (`/api/dashboard` e `/api/dashboard/demo`) | `Infra/Servicos/` |
| **`empresa_id` é anulável em duas tabelas** e significa coisas diferentes: em `feriados`, NULL = global (vale para todos os tenants); em `emails_enviados`, NULL = fluxo público sem sessão | `Feriado`, `EmailEnviado` |
| **"Ganho" aparece como flag e como carimbo**: `etapas_funil.e_ganho` (qual coluna é a de venda) e `contatos.ganho_em` (quando fechou). O dashboard conta pelo **carimbo**, nunca pela etapa | — |

---

## 2. Fluxos ponta a ponta

### 2.1 Mensagem chega no WhatsApp → aparece na tela

1. Evolution faz `POST /api/webhook/evolution?segredo=…` → `WebhookController`
   (`src/Nexora.Api/Controllers/WebhookController.cs`). Valida o segredo da query string — a
   Evolution **não assina** o payload, então essa é a única barreira. Lê o corpo cru.
2. `ProcessadorEventoEvolution.ProcessarAsync` (`src/Nexora.Infra/Evolution/`) desserializa e
   entra no `switch` do campo `event`.
3. `messages.upsert` → descobre o **tenant** casando `data.instance` com `conexoes.instance_name`
   (com `IgnoreQueryFilters`: o webhook roda sem sessão).
4. Canonicaliza o telefone do `remoteJid` e procura o contato pelas **variantes** do número
   (com e sem nono dígito). Se não achar, **cria o contato** na etapa de menor ordem.
5. Obtém ou cria a `Conversa` (1:1 com o contato).
6. `INSERT … ON CONFLICT DO NOTHING RETURNING id` na `mensagens`. NULL de volta = já existia
   (webhook reentregue ou eco do próprio envio) → para aqui.
7. Atualiza a conversa **na mesma transação**: `ultima_mensagem_em`, prévia, direção, `nao_lidas++`
   e `aguardando_desde ??= agora` (só grava se estiver NULL — segunda mensagem não sobrescreve).
8. Se houver mídia, baixa da Evolution e grava em disco com chave determinística pelo
   `wa_message_id`.
9. `INotificadorPainel.MensagemRecebidaAsync` → `HubPainel` emite `mensagemRecebida` para o grupo
   `empresa-{id}` (`src/Nexora.Api/Realtime/HubPainel.cs`).
10. No cliente, `realtime.servico.ts` recebe; `caixa.ts` chama `mesclarTopo()` (recarrega só a
    primeira página e mescla preservando a cauda paginada); `shell.ts` incrementa o badge.

### 2.2 Vendedor responde → mensagem chega no celular do cliente

1. `POST /api/conversas/{id}/responder` → `ConversasController` → `ServicoConversas.ResponderAsync`.
2. Verifica a conexão: se estiver fora, **recusa com 409** e mensagem clara — o vendedor está
   olhando a tela e precisa saber agora.
3. Se a conversa não tem dono, **atribui ao vendedor** que respondeu.
4. `EnviadorMensagem.EnviarManualAsync` (`src/Nexora.Core/Whatsapp/`) executa o protocolo:
   **grava** a linha em `mensagens` (`lembrete_id` NULL, sem invariante) → **chama** a Evolution →
   **confirma** (`enviada_em`, `wa_message_id` com `NULLIF`) ou **registra a falha** (a linha fica,
   com o erro, e `tentativas++`).
5. `aguardando_desde` volta para NULL e `nao_lidas` zera — **mesmo se o envio falhar**: a mensagem
   existe e aparece na thread, então do ponto de vista de "quem espera resposta", já respondemos.
6. A Evolution devolve `key.id`. Depois chegam webhooks `messages.update` com o ACK, que só avança.
7. `HubPainel` emite `statusMensagem` → o tick muda de estado na tela sem mexer na rolagem.

### 2.3 Rodada diária → lembrete disparado

1. `AgendadorFollowUp` (`src/Nexora.Api/Servicos/`) acorda às 8h **no fuso de negócio**.
2. Semeia os feriados nacionais do ano e do próximo (idempotente).
3. `MotorFollowUp.ExecutarAsync` (`src/Nexora.Core/FollowUp/`) itera as empresas ativas, com
   `try/catch` **por empresa**.
4. Para cada uma: resolve o fuso, calcula "hoje", carrega feriados (hoje..+14d), monta a
   `JanelaAtendimento`, verifica se a janela está aberta e se a instância está conectada.
5. **Expira** reservas fora da janela de reenvio (3 dias).
6. **Gera** lembretes: `DadosFollowUp.ConversasInativasAsync` aplica as 5 condições **no SQL** —
   conversa aberta, última mensagem de **saída**, parada há N dias, contato não terminal, sem
   lembrete pendente. `INSERT … ON CONFLICT DO NOTHING` contra o teto diário; NULL = barrado.
7. **Drena** as reservas pendentes, se puder postar.
8. **Despacha** os lembretes vencidos (`data_alvo <= hoje`): monta a reserva e chama
   `EnviarLembreteAsync` (posta) ou `ReservarLembreteAsync` (só reserva, carimbando o próximo dia
   permitido). Espaça 3s entre envios.
9. Cada resultado tem tratamento próprio: `Enviada` conclui o lembrete; `Adiada` não conclui;
   `Barrada` conclui mesmo assim (a mensagem existe); `Falhou` não conclui.

### 2.4 Contato marcado como ganho → dashboard atualiza

1. `POST /api/contatos/{id}/ganho` com `{ valor }` → `ServicoContatos.MarcarGanhoAsync`.
2. Recusa valor ≤ 0; recusa se já ganho; recusa se **perdido** (`ck_contatos_terminal` proíbe os
   dois juntos) com instrução para reabrir antes.
3. Grava `valor` e `ganho_em`, e **move o card** para a etapa `e_ganho`.
4. `POST /api/funil/{id}/mover` **recusa** a etapa de ganho com 409 — é o que força as duas portas
   (arrastar o card e clicar em "venda fechada") pela mesma rota.
5. `GET /api/dashboard` → `ServicoDashboard` conta `ganho_em >= inicioDoMes` e soma `valor`, **tudo
   agregado no SQL**. A taxa de conversão é ganhos ÷ (ganhos + perdidos) do mês.
6. O funil do payload conta por etapa com `perdido_em IS NULL`.

### 2.5 Login → token → requisição isolada por tenant

1. `POST /api/auth/login` (público, rate limit `login`) → `ServicoAutenticacao.AutenticarAsync`.
2. Busca por `lower(email)` com **`IgnoreQueryFilters`** — não há tenant no contexto ainda.
3. Se o e-mail não existe, confere contra `PoliticaLogin.HashDummy` para gastar o mesmo tempo de
   PBKDF2 e não denunciar a ausência da conta.
4. Verifica bloqueio persistente (10 falhas → 15 min), a senha (PBKDF2-SHA256, 100k), e se a
   empresa está ativa.
5. `GeradorToken.Gerar` emite o JWT com `sub`, `email`, nome, **role** e o claim `empresa_id`.
6. Em cada requisição, `ContextoEmpresaHttp` lê `empresa_id` do `ClaimsPrincipal`.
7. `NexoraDbContext` aplica `HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId)` em toda
   entidade de tenant — o isolamento é do EF, não há RLS no Postgres.
8. No cliente, `auth.servico.ts` guarda token e usuário no `localStorage`;
   `interceptor-token.ts` anexa o `Authorization` e trata 401 e 429.

---

## 3. Onde mexer para…

| Tarefa | Arquivos |
|---|---|
| **Mudar as faixas do semáforo** | Valores: colunas `empresas.semaforo_amarelo_minutos` / `_vermelho_minutos`, editáveis em `paginas/configuracoes/`. Validação: `Infra/Servicos/ServicoConfiguracao.cs` (`Validar`). Entrega ao cliente: `Infra/Servicos/ServicoPainel.cs` → `IServicoPainel.StatusPainel`. Cálculo da cor: `nucleo/semaforo.ts` (`urgenciaDe`). Consumo: `paginas/caixa/caixa.ts`, `paginas/funil/funil.ts`, `paginas/meu-dia/meu-dia.ts` |
| **Mudar a regra que gera lembrete automático** | Elegibilidade (SQL): `Infra/Persistencia/DadosFollowUp.cs` → `ConversasInativasAsync`. Orquestração: `Core/FollowUp/MotorFollowUp.cs` → `GerarLembretesAsync`. Parâmetro: `empresas.dias_sem_resposta_followup`. Texto da mensagem: `MotorFollowUp` (interpolação com `PrimeiroNome`) |
| **Adicionar campo no contato** | Entidade `Core/Entidades/Contato.cs` → mapeamento em `Infra/Persistencia/NexoraDbContext.cs` (bloco `mb.Entity<Contato>`) → migration (`dotnet dotnet-ef migrations add`) → DTOs em `Core/Servicos/IServicoContatos.cs` → projeções em `Infra/Servicos/ServicoContatos.cs` (**três**: lista, detalhe e a do funil em `ServicoFunil.cs`) → `frontend/.../nucleo/modelos.ts` → telas `paginas/contatos/` e `paginas/contato/` |
| **Adicionar evento de realtime** | Contrato: `Core/Whatsapp/INotificadorPainel.cs` → emissão: `Api/Realtime/HubPainel.cs` (`NotificadorSignalR`) → quem dispara (normalmente `Infra/Evolution/ProcessadorEventoEvolution.cs`) → cliente: `nucleo/servicos/realtime.servico.ts` (novo `Subject` + `conexao.on`) → assinatura na página |
| **Mudar o que o dashboard conta** | `Core/Servicos/IServicoDashboard.cs` (o DTO) e `Infra/Servicos/ServicoDashboard.cs` (as consultas). Tela: `paginas/dashboard/`. O modo demonstração é **separado**: `Core/Servicos/IServicoDashboardDemo.cs` + `Infra/Servicos/ServicoDashboardDemo.cs` |
| **Adicionar endpoint com papel novo** | Papel: `Core/Entidades/Enums.cs` (`PapelUsuario`) → **enum nativo do Postgres**, exige migration com `ALTER TYPE … ADD VALUE` → `Infra/ServicosInfra.cs` (`MapearEnums`) → `[Authorize(Roles = "…")]` no controller → `GeradorToken.cs` já emite o role → guards em `nucleo/seguranca/guardas.ts` |
| **Mudar o comportamento do webhook para evento desconhecido** | `Infra/Evolution/ProcessadorEventoEvolution.cs`, o `default` do `switch` na linha ~85. Hoje **ignora em silêncio**. O controller responde 200 de qualquer forma — mudar isso faz a Evolution reentregar em loop |
| **Adicionar coluna com auditoria automática** | A entidade precisa implementar `IEntidadeAuditada` (`Core/Entidades/`). O `Infra/Persistencia/InterceptorAuditoria.cs` preenche `criado_em`/`atualizado_em` em todo `SaveChanges`. Para escrita em **SQL cru** o interceptor não vê — a cobertura vem do trigger `tg_*_atualizado`, criado nas migrations |
| **Mudar a janela de atendimento** | Colunas `empresas.janela_hora_inicio` / `_fim` / `_dias_semana`. Validação: `ServicoConfiguracao.Validar`. Modelo: `Core/Tempo/JanelaAtendimento.cs`. Uso: `Core/FollowUp/MotorFollowUp.cs` (o que pode postar), `Core/Tempo/TempoUtil.cs` (minutos úteis), `ServicoPainel` (entrega ao cliente), `nucleo/semaforo.ts` (a cor). Tela: `paginas/configuracoes/` |

---

## 4. Entidades e schema

11 entidades. Todas com query filter por tenant, exceto as três marcadas.

| Entidade | Tabela | O que é | Query filter |
|---|---|---|---|
| `Empresa` | `empresas` | O tenant. Guarda também a janela, as faixas do semáforo, dias de follow-up e o fuso | `x.Id == EmpresaId` |
| `Usuario` | `usuarios` | Pessoa da equipe. Papel, status, tokens de convite e reset, contador de falhas de login | por `EmpresaId` |
| `Conexao` | `conexoes` | O número de WhatsApp da empresa. Uma por empresa | por `EmpresaId` |
| `EtapaFunil` | `etapas_funil` | Coluna do kanban. Nome, ordem, cor, `e_ganho` | por `EmpresaId` |
| `Contato` | `contatos` | Lead/cliente. Telefone canônico, etapa, `ordem_kanban`, valor, marcos terminais | por `EmpresaId` |
| `Conversa` | `conversas` | Thread do WhatsApp, 1:1 com contato. Guarda `aguardando_desde` | por `EmpresaId` |
| `Mensagem` | `mensagens` | Linha do chat **e** outbox do envio | por `EmpresaId` |
| `Lembrete` | `lembretes` | Follow-up automático ou tarefa manual | por `EmpresaId` |
| `Feriado` | `feriados` | Dia sem atendimento. `empresa_id` **anulável**: NULL = global | `EmpresaId == null \|\| == EmpresaId` |
| `FeriadoIgnorado` | `feriados_ignorados` | A empresa trabalha num feriado global. PK composta | por `EmpresaId` |
| `EmailEnviado` | `emails_enviados` | Registro de tentativa de envio. `empresa_id` **anulável** | `EmpresaId == null \|\| == EmpresaId` |

### Enums nativos do Postgres

Cada um existe em **dois lugares** e os dois têm que concordar: `HasPostgresEnum(name:)` no
`NexoraDbContext` (ensina a migration a criar o tipo) e `MapEnum` em `ServicosInfra.MapearEnums`
(ensina o driver a ler e escrever).

| Enum | Valores |
|---|---|
| `papel_usuario_enum` | dono, gestor, vendedor |
| `status_usuario_enum` | ativo, convidado, inativo |
| `status_conexao_enum` | nao_criada, conectando, conectado, desconectado, offline |
| `origem_lead_enum` | instagram, facebook, whatsapp, google, site, qrcode, indicacao, manual, outro |
| `direcao_mensagem_enum` | entrada, saida |
| `tipo_midia_enum` | nenhum, imagem, documento, audio, video |
| `status_conversa_enum` | aberta, resolvida |
| `status_lembrete_enum` | pendente, concluido, cancelado |
| `origem_lembrete_enum` | automatico, manual |
| `abrangencia_feriado_enum` | nacional, estadual, manual |

### Migrations, em ordem

| Migration | O que traz |
|---|---|
| `20260804101829_Inicial` | `empresas`, `usuarios`, 2 enums, índice funcional `lower(email)`, função e triggers de `atualizado_em` |
| `20260804112453_Dominio` | `conexoes`, `etapas_funil`, `contatos`, `conversas`, `mensagens`, `lembretes`, 7 enums, todos os índices parciais |
| `20260804154450_EnvioConfiavel` | `mensagens.tentativas`, `mensagens.expirada_em`, refaz `ix_msg_pendentes` |
| `20260804181636_CamadaDeTempo` | `feriados`, `abrangencia_feriado_enum`, 3 colunas de config em `empresas`, `uq_feriados` por SQL cru |
| `20260804224642_Configuracoes` | `feriados_ignorados` |
| `20260805112711_RegistroDeEmail` | `emails_enviados` |

---

## 5. Invariantes de banco

### Índices únicos parciais — o `WHERE` **é** a regra

| Índice | Filtro | O que garante | Quem depende |
|---|---|---|---|
| `uq_msg_wa_id` | `wa_message_id IS NOT NULL AND <> ''` | Dedupe do webhook reentregue **e** do eco do próprio envio | `ProcessadorEventoEvolution` (`ON CONFLICT DO NOTHING`), `DadosMensagem.ConfirmarEnvioAsync` (usa `NULLIF`) |
| `uq_msg_lembrete` | `lembrete_id IS NOT NULL` | Um lembrete gera **uma** mensagem, mesmo com crash entre gravar e concluir | `DadosMensagem.ReservarLembreteAsync` — NULL de volta = `ResultadoEnvio.Barrada` |
| `uq_lembrete_teto_diario` | `origem='automatico' AND envia_mensagem AND status<>'cancelado'` | Máximo **um** lembrete automático por contato por dia. Defesa anti-banimento | `DadosFollowUp.CriarLembreteAutomaticoAsync` |
| `uq_contatos_telefone` | `anonimizado_em IS NULL` | Telefone único por empresa **entre os vivos** — anonimizados saem do índice e liberam o número | `ServicoContatos.CriarAsync`/`AtualizarAsync` (a checagem **repete o predicado**) |
| `uq_etapas_ganho` | `e_ganho` | Uma única etapa de venda por empresa | `ServicoContatos.MarcarGanhoAsync`, `ServicoFunil.MoverAsync` |
| `uq_usuarios_token_convite` | `token_convite IS NOT NULL` | Token de convite único globalmente | `ServicoEquipe` |
| `uq_usuarios_token_reset` | `token_reset IS NOT NULL` | Token de reset único globalmente | `ServicoEquipe` |

### Índices únicos sem filtro

`uq_conexoes_empresa` (1 conexão por empresa), `uq_conexoes_instance` (nome de instância global),
`uq_conversas_contato` (1 conversa por contato), `uq_etapas_ordem`, `uq_feriados` (por
`data, abrangencia, COALESCE(uf,''), COALESCE(empresa_id,0)`), `uq_usuarios_email`
(**funcional** em `lower(email)` — criado por SQL cru, não aparece no `ModelSnapshot`), e os cinco
`uq_*_id_empresa`, que existem para sustentar as **FKs compostas** que impedem referência
cross-tenant.

### Índices de leitura parciais

| Índice | Filtro | Serve a |
|---|---|---|
| `ix_msg_pendentes` | `enviada_em IS NULL AND expirada_em IS NULL AND direcao='saida'` | Drenagem |
| `ix_conversas_aguardando` | `aguardando_desde IS NOT NULL` | Semáforo, Meu Dia, contador do dashboard |
| `ix_contatos_kanban` | `perdido_em IS NULL` | Quadro do funil |
| `ix_contatos_ganho` | `ganho_em IS NOT NULL` | Vendas do mês |
| `ix_contatos_responsavel` | `responsavel_id IS NOT NULL` | Filtro por responsável |
| `ix_conversas_responsavel` | `responsavel_id IS NOT NULL` | Aba "Minhas" |
| `ix_lembretes_dia` | `status='pendente'` | Meu Dia |
| `ix_lembretes_disparo` | `status='pendente' AND envia_mensagem` | Rodada do motor |

### Check constraints

| Constraint | Regra |
|---|---|
| `ck_contatos_terminal` | `ganho_em IS NULL OR perdido_em IS NULL` — não pode ser ganho e perdido |
| `ck_conversas_nao_lidas` | `nao_lidas >= 0` |
| `ck_empresas_janela` | `janela_hora_inicio < janela_hora_fim` |
| `ck_empresas_hora_faixa` | início 0–23, fim 1–24 |
| `ck_empresas_dias` | bitmask entre 1 e 127 — **zero é barrado**, senão a empresa nunca atenderia |
| `ck_lembretes_texto` | `NOT envia_mensagem OR texto_mensagem IS NOT NULL` |
| `ck_msg_ack` | ack entre 0 e 4 |
| `ck_msg_data_disparo` | saída exige `data_disparo` |
| `ck_usuarios_senha` | `status='convidado' OR senha_hash IS NOT NULL` |

### Triggers

`tg_{empresas,usuarios,conexoes,etapas_funil,contatos,conversas,lembretes}_atualizado` —
`BEFORE UPDATE`, chamam `fn_atualizado_em()`. Cobrem o que **não passa pelo EF** (SQL cru,
correção manual). `mensagens` não tem: é log append-only, sem `atualizado_em`.

---

## 6. Serviços

### Core — regras puras, sem I/O

| Arquivo | Responsabilidade |
|---|---|
| `Whatsapp/EnviadorMensagem.cs` | **Dono único do protocolo de envio.** `EnviarLembreteAsync`, `ReservarLembreteAsync`, `EnviarManualAsync`, `ReenviarAsync`, `PendentesAsync`, `ExpirarVencidasAsync`, `InstanciaConectadaAsync`, `EspacarAsync`. **Chamado por `MotorFollowUp` e `ServicoConversas`** |
| `FollowUp/MotorFollowUp.cs` | A rodada de follow-up. `ExecutarAsync` |
| `Whatsapp/CanonicalizadorTelefone.cs` | `Canonicalizar`, `Variantes`, `EhValido`, `Formatar`. **Usado no webhook e no cadastro de contato** |
| `Tempo/TempoUtil.cs` | `MinutosUteis` — desconta o que está fora da janela |
| `Tempo/JanelaAtendimento.cs` | `Contem(quando, feriados)` |
| `Tempo/CalendarioAtendimento.cs` | `DiaPermitido`, `ProximaDataPermitida` (trava de 370 iterações) |
| `Tempo/CalculadoraFeriados.cs` | `Pascoa` (Meeus), `Nacionais` (13), `Estaduais` (só RN) |
| `Tempo/FusoDeNegocio.cs` | `Resolver` (fallback UTC-3 fixo), `AgoraNo` |
| `Seguranca/HashSenha.cs` | PBKDF2-SHA256 100k, comparação em tempo constante |
| `Seguranca/PoliticaLogin.cs` | `HashDummy`, `MascararEmail`, limites de bloqueio |
| `Email/MontadorEmail.cs` | Os três templates. Função pura |

### Infra

| Serviço | Responsabilidade | Depende de |
|---|---|---|
| `ServicoAutenticacao` | Login com bloqueio persistente | db, relógio |
| `ServicoCadastroEmpresa` | Cria tenant + dono + conexão + 5 etapas. **Sem controller** | db |
| `ServicoCaixa` | Lista de conversas e thread, por cursor | db, contexto |
| `ServicoConversas` | Responder, assumir, liberar | db, contexto, `EnviadorMensagem`, relógio |
| `ServicoContatos` | CRUD, ganho/perda/reabrir, anonimização LGPD | db, contexto, relógio |
| `ServicoFunil` | Quadro, coluna por cursor, mover com ponto médio e renormalização | db |
| `ServicoConexoes` | QR, pareamento, status, saúde, troca de número | db, cliente WhatsApp, relógio |
| `ServicoEquipe` | Equipe, convite, reset, minha conta. **Dispara os 3 e-mails** | db, contexto, relógio, notificador de e-mail |
| `ServicoConfiguracao` | Ler e gravar config da empresa, com as validações | db |
| `ServicoFeriados` | Seed anual, listar, criar/remover manual, ignorar/reativar | db, contexto, relógio |
| `ServicoLembretes` | CRUD de lembrete manual | db, contexto, relógio |
| `ServicoMeuDia` | Une conversas aguardando + lembretes vencidos | db, contexto, relógio |
| `ServicoPainel` | Payload **barato** do shell (polling de 45s) | db, relógio |
| `ServicoDashboard` | Payload **caro**, sob demanda. Tudo agregado no SQL | db, relógio |
| `ServicoDashboardDemo` | Gerador determinístico de dados fictícios. Não toca no banco | relógio |
| `DadosMensagem` | SQL do envio: `ON CONFLICT`, confirmação com `NULLIF`, expiração | db, relógio |
| `DadosFollowUp` | SQL da elegibilidade. **Tudo com `IgnoreQueryFilters`** — roda como job | db, relógio |
| `ClienteEvolution` | Único ponto que fala com a Evolution | HttpClient |
| `ProcessadorEventoEvolution` | Handler do webhook | db, cliente, armazenamento, notificador |
| `NotificadorEmail` | Monta → entrega → registra. **Nunca lança** | remetente, db, opções, relógio |
| `RemetenteSmtp` / `RemetenteArquivo` | Transporte. Escolhido por configuração | opções |
| `ArmazenamentoDisco` | Mídia em disco | opções |
| `InterceptorAuditoria` | `criado_em`/`atualizado_em` em todo `SaveChanges` | relógio |

---

## 7. Endpoints

48 rotas. **6 públicas** (sem autenticação) — a superfície de ataque.

### Públicas

| Verbo | Rota | Controller | O que faz | Rate limit |
|---|---|---|---|---|
| POST | `/api/auth/login` | `AuthController` | Autentica e devolve JWT | `login` — 5/min por IP |
| POST | `/api/webhook/evolution` | `WebhookController` | Recebe eventos da Evolution. Autentica por **segredo na query string** | `webhook` — 300/min por IP |
| GET | `/api/convite/{token}` | `ConviteController` | Dados do convite para a tela de aceite | — |
| POST | `/api/convite/{token}` | `ConviteController` | Define a senha e devolve JWT | `senha` — 5/15min por IP+token |
| POST | `/api/redefinir/solicitar` | `RedefinicaoController` | "Esqueci minha senha". **Resposta idêntica** exista o e-mail ou não | `recuperacao` — 3/15min por IP |
| GET/POST | `/api/redefinir/{token}` | `RedefinicaoController` | Info do token / redefine a senha | `senha` |

### Autenticadas

| Rota | Verbos | Papel | O que faz |
|---|---|---|---|
| `/api/painel/status` | GET | qualquer | Payload barato: não lidas, aguardando, conexão, faixas do semáforo, janela, feriados recentes |
| `/api/dashboard` | GET | qualquer | Payload rico: 4 números, faturamento, conversão, funil |
| `/api/dashboard/demo` | GET | qualquer | Dados de demonstração gerados |
| `/api/meu-dia` | GET | qualquer | Ações do dia do usuário logado |
| `/api/conversas` | GET | qualquer | Lista por cursor, com filtro e busca |
| `/api/conversas/{id}/mensagens` | GET | qualquer | Thread por cursor |
| `/api/conversas/{id}/lida` | POST | qualquer | Zera não lidas |
| `/api/conversas/{id}/responder` | POST | qualquer | Envia mensagem |
| `/api/conversas/{id}/assumir` \| `/liberar` | POST | qualquer | Atribuição (409 se for de outro) |
| `/api/contatos` | GET, POST | qualquer | Lista paginada por offset; criar |
| `/api/contatos/{id}` | GET, PUT | qualquer | Detalhe (com conversa e lembretes); editar |
| `/api/contatos/{id}/ganho` \| `/perda` \| `/reabrir` | POST | qualquer | Estado terminal |
| `/api/contatos/{id}/anonimizar` | POST | **dono, gestor** | LGPD, irreversível |
| `/api/funil` | GET | qualquer | Quadro paginado por coluna |
| `/api/funil/etapas/{id}/contatos` | GET | qualquer | Mais cards de uma coluna, por cursor |
| `/api/funil/{contatoId}/mover` | POST | qualquer | Mover/reordenar. **Recusa a etapa de ganho** |
| `/api/lembretes` | POST | qualquer | Criar lembrete manual |
| `/api/lembretes/contato/{id}` | GET | qualquer | Lembretes do contato |
| `/api/lembretes/{id}/concluir` \| `/cancelar` | POST | qualquer | Mudar status |
| `/api/feriados` | GET | qualquer | Lista, marcando os dispensados |
| `/api/feriados` | POST | **dono, gestor** | Criar manual |
| `/api/feriados/{id}` | DELETE | **dono** | Só manual — nacional devolve 409 |
| `/api/feriados/{id}/trabalha` | POST, DELETE | **dono** | Dispensar/observar feriado nacional |
| `/api/configuracao` | GET | qualquer | Config da empresa |
| `/api/configuracao/empresa` \| `/atendimento` | PUT | **dono** | Gravar config |
| `/api/conta` | GET, PUT | qualquer | Própria conta. **Não recebe id** |
| `/api/conta/senha` | POST | qualquer | Trocar a própria senha |
| `/api/equipe` | GET, PUT | **dono** | Listar, editar usuário |
| `/api/equipe/convites` | POST | **dono** | Convidar |
| `/api/equipe/{id}/reenviar-convite` \| `/reset-senha` | POST | **dono** | Gerar novo token |
| `/api/conexao` (+ `/status`, `/conectar`, `/parear`, `/desconectar`, `/saude`, `/reconhecer-troca`) | GET, POST | **dono** | Gestão do número |
| `/api/midia/{mensagemId}` | GET | qualquer | Baixa mídia recebida |

---

## 8. Jobs de fundo

**Um único:** `AgendadorFollowUp` (`src/Nexora.Api/Servicos/AgendadorFollowUp.cs`).

| Aspecto | Como é |
|---|---|
| O que faz | Semeia feriados e roda `MotorFollowUp.ExecutarAsync` |
| Frequência | Uma vez por dia, às `OpcoesAgendador.HoraDaRodada` (padrão 8h) |
| Fuso | **De negócio** (`America/Sao_Paulo` por padrão), não o do servidor |
| No boot | Semeia feriados. Só roda a rodada se `RodarNoBoot = true` (padrão **false**) |
| Se falhar | `try/catch` que nunca deixa a exceção subir. O log dentro do catch também é protegido, com fallback para `Console.Error` |
| Concorrência | **Sem lock distribuído.** Com duas instâncias, roda duas vezes |

---

## 9. Integração Evolution

`src/Nexora.Infra/Evolution/ClienteEvolution.cs` é o **único** ponto que fala com a Evolution.

| Operação | Endpoint chamado |
|---|---|
| Enviar texto | `POST message/sendText/{instance}` |
| Enviar mídia | `POST message/sendMedia/{instance}` |
| Baixar mídia | `POST chat/getBase64FromMediaMessage/{instance}` |
| Estado da instância | `GET instance/connectionState/{instance}` |
| Criar + conectar | `POST instance/create` + `GET instance/connect/{instance}[?number=]` |
| Detalhes | `GET instance/fetchInstances?instanceName=` |
| Desconectar | `DELETE instance/logout/{instance}` |

### Eventos de webhook tratados

| Evento | O que faz |
|---|---|
| `messages.upsert` | Cria contato se preciso, abre conversa, insere mensagem, atualiza `aguardando_desde`, baixa mídia, notifica |
| `messages.update` | Atualiza o ACK. **Só avança** |
| `connection.update` | Atualiza status, captura número e perfil, detecta troca de número |

**Evento não tratado:** cai no `default` do `switch` e é **ignorado em silêncio**. O
`WebhookController` responde **200 mesmo assim** — status de erro faria a Evolution reentregar em
loop.

---

## 10. Telas

15 rotas. `frontend/nexora-painel/src/app/app.routes.ts`.

### Públicas (fora do shell)

| Rota | Componente | O que mostra | API |
|---|---|---|---|
| `/entrar` | `paginas/login/` | Login, com contagem regressiva do 429 | `POST /api/auth/login` |
| `/esqueci` | `paginas/esqueci/` | Pede o e-mail. Mensagem idêntica em qualquer caso | `POST /api/redefinir/solicitar` |
| `/convite/:token` | `paginas/convite/` | Aceite: define senha e já entra | `/api/convite/{token}` |
| `/redefinir/:token` | `paginas/redefinir/` | Nova senha por link | `/api/redefinir/{token}` |

### Dentro do shell (`guardaAutenticado`)

| Rota | Componente | O que mostra | API | Papel |
|---|---|---|---|---|
| `/caixa` | `paginas/caixa/` | Lista por cursor + thread. Aceita `?conversa=N` | `/api/conversas`, `/api/painel/status` | qualquer |
| `/dashboard` | `paginas/dashboard/` | 4 números, faturamento, conversão, funil, atividade. Alterna para demonstração | `/api/dashboard`, `/api/dashboard/demo` | qualquer |
| `/meu-dia` | `paginas/meu-dia/` | Ações do dia. ⚠️ **não compila** (ver §0) | `/api/meu-dia` | qualquer |
| `/funil` | `paginas/funil/` | Kanban com arrasto nativo | `/api/funil` | qualquer |
| `/contatos` | `paginas/contatos/` | Tabela com busca, filtros e paginação | `/api/contatos` | qualquer |
| `/contatos/:id` | `paginas/contato/` | Dados, negociação, lembretes, conversa | `/api/contatos/{id}`, `/api/lembretes` | qualquer |
| `/conta` | `paginas/conta/` | Nome, e-mail, senha | `/api/conta` | qualquer |
| `/conta/senha` | — | Redireciona para `/conta` | — | qualquer |
| `/equipe` | `paginas/equipe/` | Convidar, editar papel, resetar senha | `/api/equipe` | **dono** |
| `/conexao` | `paginas/conexao/` | QR, pareamento, status, saúde | `/api/conexao` | **dono** |
| `/configuracoes` | `paginas/configuracoes/` | Dados, janela, semáforo, follow-up, feriados | `/api/configuracao`, `/api/feriados` | **dono** |

`/` redireciona para `/caixa`; `**` redireciona para `/`.

---

## 11. Frontend transversal

Angular 20, **standalone**, **zoneless**, signals, rotas lazy. Sem NgModules, sem NgRx, sem
biblioteca de componentes ou de gráficos.

### Núcleo

| Arquivo | O que faz |
|---|---|
| `nucleo/api-base.ts` | Lê `environment.apiBase` e `hubBase`. **Nenhuma URL escrita aqui** |
| `nucleo/modelos.ts` | Todos os DTOs. Enums como **texto** |
| `nucleo/semaforo.ts` | `urgenciaDe`, `minutosUteis`, `dentroDaJanela`, `janelaDoStatus`, `chaveDia`, `rotuloEspera`. **Espelho de `TempoUtil.cs`** |
| `nucleo/download.ts` | CSV no browser |
| `nucleo/seguranca/guardas.ts` | `guardaAutenticado`, `guardaDono` |
| `nucleo/seguranca/interceptor-token.ts` | Anexa token, trata 401 e 429 |
| `nucleo/seguranca/throttle-login.ts` | Contagem regressiva do 429 |
| `nucleo/servicos/*.servico.ts` | 11 serviços HTTP, um por área |
| `nucleo/servicos/realtime.servico.ts` | SignalR com `accessTokenFactory`; 5 `Subject` |
| `nucleo/toast/` | Pilha de avisos não bloqueantes |
| `nucleo/tick-status/` | Tick de ACK em SVG, 5 estados |
| `nucleo/thread/` | **Componente compartilhado** da conversa: cursor, âncora de rolagem, compositor. Usado pela caixa e pelo detalhe do contato |
| `nucleo/fechamento/modal-fechamento.ts` | Modal de venda/perda. **Uma porta, dois pontos de entrada** |
| `nucleo/graficos/grafico-linha.ts` | Linha/área em SVG puro. Usado só no modo demonstração |

### Design system — `src/styles.css`

Tokens: `--verde`, `--verde-2`, `--verde-3`, `--creme`, `--creme-2`, `--alerta`,
`--alerta-fundo`, `--alerta-borda`, `--branco`, `--linha`, `--texto`, `--texto-fraco`,
`--urgencia-{baixa,media,alta}` (+ `-fundo`), `--raio`, `--sombra`.

Classes disponíveis:

```
.cartao  .cartao-corpo  .cartao-topo
.btn  .btn-neutro  .btn-pequeno  .btn-perigo  .carregar-mais  .link-editar
.campo  .dica
.tabela  .num  .center
.selo  .selo-ok  .selo-atencao  .selo-perigo
.urgencia  .urgencia-baixa  .urgencia-media  .urgencia-alta  .urgencia-fora
.vazio  .erro  .carregando  .skel
.overlay  .modal  .modal-corpo
.linha  .espaco  .fraco  .mono
```

**Restrição de paleta:** verde, creme e **um** tom de alerta. A única exceção são os três estados
do semáforo, e eles são derivados da paleta.

---

## 12. Configuração

| Seção | POCO | Controla | Padrão | Se estiver errado |
|---|---|---|---|---|
| `ConnectionStrings:Nexora` | — | Banco | — | **Derruba no boot**, com mensagem acionável |
| `Jwt` | `OpcoesJwt` | Emissor, audiência, validade (12h) | — | Chave < 32 chars **derruba no boot** |
| `Evolution` | `OpcoesEvolution` | BaseUrl, ApiKey | `http://localhost:8082` | Envio e pareamento falham |
| `Webhook` | `OpcoesWebhook` | Segredo da query string | — | Segredo vazio → **recusa tudo** (deliberado) |
| `Midia` | `OpcoesMidia` | Raiz do disco | `midia` | Mídia recebida não grava |
| `Envio` | `OpcoesEnvio` | Intervalo entre envios (3s), janela de reenvio (3 dias) | — | Intervalo zero aumenta risco de banimento |
| `RateLimit` | `OpcoesRateLimit` | 5 limites + `ConfiarProxyReverso` | 100/5/300/5/3 | `ConfiarProxyReverso=true` fora de proxy deixa forjar IP |
| `Agendador` | `OpcoesAgendador` | Hora da rodada (8h), fuso, `RodarNoBoot` (false) | — | `RodarNoBoot=true` em produção dispara a cada deploy |
| `Email` | `OpcoesEmail` | Provedor, remetente, base do painel, SMTP | provedor `arquivo` | Base errada → link do convite aponta para o lugar errado |
| `Cors:Origens` | — | Origens em produção | `[]` | Painel bloqueado por CORS |

### Segredos — nunca no `appsettings.json` versionado

`ConnectionStrings:Nexora`, `Jwt:Chave`, `Evolution:ApiKey`, `Webhook:Segredo`,
`Email:Usuario` e `Email:Senha`. Em dev vão para user-secrets; os comandos estão no
`.env.example`.

Em **desenvolvimento**, CORS aceita qualquer origem de **loopback** (`SetIsOriginAllowed`); em
produção, lista explícita.

---

## 13. Testes

**287 testes**, xUnit, em `tests/Nexora.Tests/`.

### Unidade — sem banco

| Arquivo | Testes | Cobre |
|---|---|---|
| `TempoTests.cs` | 27 (+18 inline) | Páscoa 2025–2030, feriados móveis, bitmask, deslize, fuso, minutos úteis, janela |
| `CanonicalizadorTelefoneTests.cs` | 8 (+20 inline) | Canonicalização, variantes do nono dígito, validação |
| `HashSenhaTests.cs` | 5 (+7 inline) | PBKDF2, formato, tempo constante |

### Integração — contra **Postgres real**

`BancoTeste` cria o banco `nexora_teste` se não existir, aplica as migrations e monta o data
source com os enums. Cada teste roda numa **transação sempre revertida**. Provider in-memory não
reproduz índice parcial, `ON CONFLICT` nem query filter — daí o banco de verdade.

| Arquivo | Testes | Cobre |
|---|---|---|
| `ContatosDbTests.cs` | 24 | CRUD, ganho/perda/reabrir, LGPD, tenant, **dashboard saindo do zero** |
| `InvariantesDbTests.cs` | 24 | As invariantes de banco, nos dois sentidos |
| `ConfiguracaoDbTests.cs` | 22 | Validações, papel, feriados, **janela mudando a rodada** |
| `WebhookEvolutionDbTests.cs` | 21 (+8 inline) | Eventos, dedupe, ACK, mídia, `aguardando_desde` |
| `MeuDiaDbTests.cs` | 20 | Meu Dia, dashboard, lembretes manuais, feriados |
| `EnvioMensagemDbTests.cs` | 18 | O protocolo, reenvio, expiração, freio por conexão |
| `FollowUpDbTests.cs` | 18 | Elegibilidade, teto diário, reserve-defer, isolamento de falha |
| `EmailDbTests.cs` | 17 | Camadas, falha do provedor, registro, resposta uniforme, templates |
| `FunilDbTests.cs` | 16 | Ponto médio, bordas, renormalização, recusa do ganho |
| `CadastroEmpresaDbTests.cs` | 8 | Cadastro de tenant |
| `LoginDbTests.cs` | 6 | Login, bloqueio, timing |
| `IsolamentoDominioDbTests.cs` | 5 | Cross-tenant no domínio |
| `IsolamentoTenantDbTests.cs` | 4 | Cross-tenant básico |

### Sem cobertura nenhuma

- **Frontend: zero testes.** Karma e Jasmine estão instalados; não há `.spec.ts` além do
  `app.spec.ts` gerado pelo CLI.
- **Controllers**: não há teste de HTTP ponta a ponta (`WebApplicationFactory`). O papel é
  verificado lendo o atributo `[Authorize]` por reflexão em `ConfiguracaoDbTests`.
- **`ClienteEvolution`**: nenhum teste — os testes usam `ClienteWhatsAppFalso`.
- **`RemetenteSmtp`**: nenhum teste; só o `RemetenteArquivo` é exercitado.
- **`ServicoDashboardDemo`**, **`AgendadorFollowUp`**, **`ArmazenamentoDisco`**: sem teste direto.

---

## 14. Dependências

### NuGet (além do que vem no template)

| Pacote | Onde | Por quê |
|---|---|---|
| `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.11 | Infra | Provider EF |
| `Npgsql` 8.0.6 | Infra | Data source com enums nativos |
| `Microsoft.EntityFrameworkCore.Design` 8.0.11 | Infra | Só para `dotnet ef` |
| `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.11 | Api | JWT |
| `Swashbuckle.AspNetCore` 6.6.2 | Api | Swagger, só em dev |
| `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` 8.0.11 | Api | `/health` com checagem de banco |
| `Microsoft.Extensions.Http` 8.0.1 | Infra | Typed client da Evolution |
| `Microsoft.Extensions.Logging.Abstractions` 8.0.2 | Core, Tests | `ILogger` no Core sem depender da Api |

Sem MediatR, AutoMapper, FluentValidation, Serilog, MailKit, Polly ou Hangfire. SMTP usa
`System.Net.Mail` da BCL; agendamento usa `BackgroundService`.

### npm (além do Angular)

| Pacote | Por quê |
|---|---|
| `@microsoft/signalr` ^10.0.11 | Realtime. **Único uso**: `nucleo/servicos/realtime.servico.ts` |
| `rxjs` ~7.8.0 | Vem com o Angular; usado em `HttpClient` e nos `Subject` do realtime |

Karma, Jasmine e `@types/jasmine` estão em devDependencies **sem uso real** — não há specs.

---

## 15. Build e deploy

| Arquivo | O que é |
|---|---|
| `global.json` | Pin do SDK 8.0.100, `rollForward: latestFeature` |
| `.config/dotnet-tools.json` | `dotnet-ef` **8.0.11 local** — invocar como `dotnet dotnet-ef` |
| `Dockerfile` | Build multi-stage da API |
| `.dockerignore` | — |
| `docker-compose.yml` | Evolution API + Postgres dela. Porta **8082** (a 8081 é do Recupera) |
| `.env` e `.env.example` | Só o compose. Segredos da aplicação vão para user-secrets |
| `nexora.sln` | 4 projetos |

**Não há CI.** Não existe diretório `.github`.

### Rodar local do zero

```
docker compose up -d                      # Evolution + Postgres dela
dotnet user-secrets set "ConnectionStrings:Nexora" "..."  --project src/Nexora.Api
dotnet user-secrets set "Jwt:Chave" "<32+ caracteres>"    --project src/Nexora.Api
dotnet dotnet-ef database update --project src/Nexora.Infra --startup-project src/Nexora.Api
dotnet run --project src/Nexora.Api        # http://localhost:5123
cd frontend/nexora-painel && npm install && npx ng serve
```

⚠️ A fábrica de design-time (`FabricaDbContextDesignTime`) usa por padrão um banco chamado
**`nexora`**, enquanto o de desenvolvimento normalmente é `nexora_dev` (definido em user-secrets).
Rodar `dotnet ef database update` sem `NEXORA_CONN` cria um banco `nexora` vazio. Existe um assim
no ambiente atual, sem uso.

---

## 16. Armadilhas conhecidas

Decisões não-óbvias que quebram o produto se alguém "limpar". **Sintoma silencioso marcado com 🔇**
— bug que não lança exceção é o que mais custa.

| # | Onde | Se for removida | Sintoma |
|---|---|---|---|
| 1 | **Tenant zero** — `IContextoEmpresa.EmpresaId` é 0 fora de requisição autenticada. Login, webhook e job usam `IgnoreQueryFilters` + filtro explícito | O query filter compara com 0 e não acha nada | 🔇 Consulta volta **vazia**, sem erro. Login não autentica, webhook não acha o tenant, rodada não vê empresa nenhuma |
| 2 | **`IgnoreQueryFilters` no login** (`ServicoAutenticacao`) | Idem acima, no caminho mais visível | 🔇 Ninguém consegue entrar, sem mensagem que explique |
| 3 | **`MapInboundClaims`** — `ContextoEmpresaHttp` lê `sub` **e** `ClaimTypes.NameIdentifier` | Só um dos dois é lido | 🔇 `UsuarioId` fica 0; atribuição e auditoria gravam NULL |
| 4 | **`OnMessageReceived` do SignalR** (`Program.cs`) resgata o token da query string em `/hub` | WebSocket não manda header `Authorization` | 🔇 Hub conecta **anônimo**, cliente não entra no grupo, realtime não chega — sem erro |
| 5 | **`NULLIF(wa_message_id, '')`** em `DadosMensagem.ConfirmarEnvioAsync` | Duas strings vazias colidem no índice único | Segunda mensagem legítima recusada |
| 6 | **ACK só avança** (`WHERE ack IS NULL OR ack < novo`) | Webhook fora de ordem sobrescreve | 🔇 Mensagem lida volta a "entregue" |
| 7 | **Canonicalização de telefone** — os dois lados (cadastro e webhook) têm que produzir o mesmo formato | Formatos divergem | 🔇 Mensagem recebida **não casa com ninguém**: sem conversa, sem badge, sem log |
| 8 | **Variantes do nono dígito** (`CanonicalizadorTelefone.Variantes`) | Só a forma exata é procurada | 🔇 Contato antigo (sem o 9) nunca casa |
| 9 | **`aguardando_desde ??=`** — só grava se estiver NULL | Segunda mensagem sobrescreve | 🔇 Semáforo reinicia a contagem e nunca fica vermelho |
| 10 | **Ponto médio + renormalização** (`ServicoFunil`) | Sem renormalizar, o intervalo entre vizinhos se esgota | 🔇 Card **para de aceitar reordenação**, sem erro |
| 11 | **`preventDefault` no `dragover`** (`funil.ts`) | O navegador não considera zona de soltura válida | 🔇 `drop` nunca dispara; o card volta |
| 12 | **`HasPostgresEnum(name: "…")`** — o parâmetro posicional é **schema**, não nome | Migration emite anotação errada | Erro `42704: tipo não existe` no `database update` |
| 13 | **`MapEnum` ausente no design-time** (`FabricaDbContextDesignTime`) | O Npgsql resolve o OID ao abrir, e o tipo ainda não existe | `database update` morre antes de rodar DDL |
| 14 | **Checagem de telefone repete o predicado do índice parcial** (`ServicoContatos`) | Sem `AnonimizadoEm == null` na checagem | Cadastro barrado por um contato anonimizado, com mensagem mentirosa |
| 15 | **Log dentro do `catch` do `BackgroundService`** está protegido (`AgendadorFollowUp.Registrar`) | O `LogError` lança durante shutdown | 🔇 O `BackgroundService` **cai** — o mecanismo que existe para nunca cair vira o motivo da queda |
| 16 | **`chaveDia` usa data local, não `toISOString()`** (`semaforo.ts`) | Às 21h em Brasília o ISO devolve o dia seguinte | 🔇 Feriado descontado no dia errado |
| 17 | **`ForeignKeyIndexConvention` removida** (`ConfigureConventions`) | O EF cria 12 índices de FK não previstos | Índices a mais nas tabelas de maior escrita |

Duas mais, sobre tempo e SQL:

- **`now()` é a hora de início da transação**, não do statement. Dois `UPDATE` na mesma transação
  recebem o mesmo carimbo — comportamento desejado, e o que faz um teste ingênuo de trigger falhar.
- **Nunca aplicar função sobre a coluna em filtro** (`CURRENT_DATE - data_alvo`,
  `criado_em::date = current_date`): descarta o índice e a varredura vira seq scan. O padrão do
  código é calcular o limite na aplicação e comparar contra a coluna.

---

## 17. Dívidas e limites conhecidos

### Estado quebrado agora

- **`ng build` falha.** `paginas/meu-dia/meu-dia.ts` está na versão de lista única; o `.html` e o
  `.css` continuam na de duas listas.

### Limites de escala

| Limite | Consequência com 10× mais dado ou 2 instâncias |
|---|---|
| **Rate limit em memória** (`RateLimitingConfig`) | Com duas instâncias, o limite dobra na prática |
| **Sem lock distribuído** no `AgendadorFollowUp` | A rodada roda duas vezes; as invariantes impedem mensagem duplicada, mas o espaçamento de 3s deixa de valer entre instâncias — risco de banimento |
| **`QuadroAsync` faz uma consulta por coluna** | 5 consultas por carregamento do funil; cresce com etapas configuráveis |
| **`feriadosRecentes` limitado a 30 dias** | Espera de mais de um mês tem desconto incompleto |
| **`TempoUtil` trava em 400 iterações** | Espera acima de ~400 dias tem o total limitado |
| **`ServicoMeuDia` calcula minutos úteis em memória** | Sobre o conjunto já recortado pelo SQL; cresce com o número de conversas aguardando |
| **Mídia em disco local** (`ArmazenamentoDisco`) | Não sobrevive a mais de uma instância nem a container efêmero |

### Atalhos assumidos

- **Um telefone por contato.** PJ com dois números vira contato duplicado. A saída registrada é
  extrair `telefones_contato`.
- **Uma conexão por empresa** (`uq_conexoes_empresa`).
- **Feriados estaduais não são semeados** — falta `empresas.uf`. `CalculadoraFeriados.Estaduais`
  existe e cobre só RN.
- **Fuso horário não é editável pela tela** — a coluna existe e é lida.
- **Sem histórico de movimentação entre etapas.**
- **Sem endpoint de série temporal** — `grafico-linha` só é alimentado pelo modo demonstração.
- **Sem controle de concorrência ao mover card** — dois vendedores ao mesmo tempo: o último ganha.
- **`ServicoCadastroEmpresa` não tem controller** — a primeira empresa nasce por SQL ou teste.
- **Envio de e-mail: uma tentativa, sem retry.** Registro em `emails_enviados`; nada relê para
  reenviar.
- **Timing do "esqueci minha senha" difere** entre e-mail existente e inexistente. O corpo da
  resposta é idêntico; o tempo não. Mitigado pelo limite de 3/15min por IP.
- **`SmtpClient` da BCL não fala SMTPS implícito (porta 465).**
- **Sem SPF/DKIM/DMARC documentados** para o domínio remetente.

### Achados de higiene

- **`senhas-dev.sql` na raiz do repositório**, com senha em texto puro de dois usuários de
  desenvolvimento.
- **Banco `nexora` vazio** no ambiente, criado por engano ao rodar `database update` sem
  `NEXORA_CONN`. Sem uso.
- **Sem CI.** Nenhum build, teste ou lint automático.
- **Karma e Jasmine instalados sem specs.**
- **Modo demonstração do dashboard é provisório** e some quando os endpoints de série e de
  atividades existirem.
