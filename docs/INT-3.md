# INT-3 — Webhook de saída

O Nexora deixou de ser uma ilha: cinco eventos saem daqui para o sistema que o cliente já usa.

Nenhum conector por plataforma. Um gancho assinado, e o cliente pluga onde quiser — n8n, Make,
Zapier, ou o ERP dele direto.

---

## 1. O payload

```json
{
  "versao": 1,
  "id": "1f0a…-…-…",
  "evento": "venda.fechada",
  "ocorridoEm": "2026-08-06T13:00:00Z",
  "empresaId": 7,
  "dados": {
    "id": 42,
    "etapaId": 3,
    "etapaNome": "Venda",
    "nome": "Marcos Antunes",
    "telefone": "5584988887777",
    "email": "marcos@exemplo.com",
    "origem": "whatsapp",
    "origemDetalhe": "Panfleto Julho",
    "valor": 2500.00,
    "responsavelId": 4,
    "etapaAnteriorId": 2,
    "motivoPerda": null
  }
}
```

### `"versao": 1` desde o primeiro dia

Mudar o formato depois quebra a integração de todo cliente que já ligou — e ele não descobre por um
erro, descobre porque o pedido parou de entrar no ERP. Com a versão no corpo ele tem onde ramificar
e nós temos como mudar sem quebrar. Custa um campo; não versionar custa uma migração coordenada com
cada cliente.

### `id` é para o receptor, não para nós

As três tentativas da mesma entrega carregam o **mesmo** `id`. É o que permite processar uma vez e
ignorar as repetições — sem ele, um timeout **nosso** (a entrega chegou, a resposta se perdeu) vira
pedido duplicado no sistema dele. É também a defesa que funciona quando o job roda em duas
instâncias, o que o Nexora ainda não impede.

### Nomes com ponto

`lead.criado`, `venda.fechada`. `ToString().ToLower()` daria `leadcriado` — e o nome é **parte do
contrato**: o cliente escreve um `switch` em cima dele. O ponto separa objeto de ação, que é a
convenção de todo webhook que ele já viu (Stripe, GitHub, Shopify): o formato que ele espera sem
ler documentação.

### Campos ausentes ≠ campos nulos

No modo "só ids" os campos de PII **não existem** no corpo, em vez de virem `null`. `nome: null`
diria "este lead não tem nome"; ausente diz "não te mandamos isso".

---

## 2. Os eventos

| evento | quando | padrão |
|---|---|---|
| `lead.criado` | contato criado — WhatsApp, formulário do site ou digitado | ✅ |
| `lead.movido` | contato mudou de **etapa** | ✅ |
| `venda.fechada` | contato marcado como ganho | ✅ |
| `venda.perdida` | contato marcado como perdido | ✅ |
| `mensagem.recebida` | mensagem de **entrada** | ❌ |
| `webhook.teste` | o botão da tela — **não é assinável** | — |

`mensagem.recebida` nasce desmarcado: é o de maior volume de longe (uma conversa ativa gera dezenas
por dia) e a maioria não precisa. Ligado por padrão, a primeira integração do cliente viraria uma
enxurrada. Há teste no `WebhookSaida` novo e no schema — o default é `false` no **banco** também,
para um webhook criado por SQL cru não nascer assinando ele.

### Três decisões de granularidade

**`lead.movido` só dispara quando a etapa MUDA.** Reordenar o card dentro da coluna passa pelo mesmo
método; para o sistema do cliente a posição não significa nada, e um evento por arrasto encheria a
fila de ruído.

**Fechar venda emite UM evento, não dois.** Carimbar o ganho move de etapa junto, mas quem recebe
`venda.fechada` não precisa também de um `lead.movido` da mesma ação — seriam dois eventos para um
fato só. A etapa anterior vai **dentro** do payload de `venda.fechada`.

**`mensagem.recebida` só na ENTRADA.** `fromMe=true` é o eco do que nós mandamos; avisar o sistema do
cliente de que "chegou uma mensagem" que foi ele mesmo quem mandou é o começo de um laço de
integração.

### O `webhook.teste` é tipo próprio

Não um `lead.criado` de mentira: o receptor precisa distinguir o teste do real, senão o primeiro
clique no botão cria um lead fantasma no ERP. Os ids do exemplo são **negativos** — nenhum id real
do sistema é —, então quem gravar por engano consegue achar depois. E `Assina()` devolve falso para
ele, então nada no sistema o dispara sozinho.

---

## 3. Assinatura — o que a Evolution não faz

A Evolution **não assina** o que manda para o Nexora, e por isso a autenticação de entrada depende
de um segredo na query string — coisa que vaza em log de proxy, em histórico e em `Referer`. Isso é
limitação dela, não modelo a seguir.

```
X-Nexora-Assinatura: sha256=<hmac-sha256 de "{timestamp}.{corpo}">
X-Nexora-Timestamp:  1780000000
X-Nexora-Evento:     venda.fechada
X-Nexora-Entrega:    1f0a…            (o id do evento — o mesmo nas 3 tentativas)
```

### O timestamp entra na assinatura

Assinar só o corpo deixa o replay aberto: quem capturou uma entrega válida a reenvia amanhã com
qualquer timestamp e a assinatura continua conferindo. Assinando `{timestamp}.{corpo}` o par fica
amarrado.

Quem **recusa** o replay é o receptor (janela de tolerância dele). O que o Nexora faz é dar a ele um
timestamp em que dá para confiar — e a tela documenta as duas metades, porque assinatura que ninguém
valida é enfeite.

### O corpo postado é o corpo guardado, byte a byte

Montado **uma vez**, na publicação, e gravado em `jsonb`. O `HttpClient` posta a string, não um
objeto reserializado — reserializar mudaria ordem, escapes ou indentação, e a assinatura deixaria de
conferir do lado do receptor sem nenhum erro do nosso. Há teste comparando `entrega.Payload` com o
corpo que chegou ao cliente.

### O segredo sai uma vez

Na criação e ao ser regerado. Depois disso nem a tela do dono o recupera — `WebhookDto` não tem o
campo. Um segredo que a tela busca a cada carregamento é um segredo que vive no histórico do
navegador, no cache do proxy e na captura de tela do suporte.

Guardado em claro no banco, e a razão importa: ele não é credencial de acesso ao Nexora — é a chave
com que **assinamos**. Precisa ser recuperável para assinar cada entrega, e um hash não assina nada.

---

## 4. Entrega — fora do caminho do usuário

```
lead criado  →  INSERT em entregas_webhook (pendente, vence agora)  →  requisição termina
                                    ↓
                    AgendadorWebhooks, a cada 30s
                                    ↓
              valida a URL (DNS fresco) → POST com 10s de timeout
                                    ↓
        2xx → entregue          falha → +1min → +5min → +30min → falhou
```

**Fechar uma venda não pode ficar lento porque o servidor do cliente está devagar.** Há teste
cronometrado: o receptor falso dorme 3 segundos em toda entrega, e criar um contato tem que levar
menos de 1. Ele também exige `Chamadas == 0` — nada foi postado no caminho do usuário.

### A tabela é a fila

Nada de broker, nada de fila distribuída — a mesma disciplina do envio de mensagem. A linha nasce
`pendente` com `proxima_tentativa_em`, a rodada busca o que venceu, e o resultado volta para a
própria linha. Uma peça a menos de infraestrutura para operar, e o histórico e a fila são a mesma
coisa — o que torna "o cliente diz que não recebeu" uma consulta, e não uma investigação.

O índice da fila é **parcial** (`WHERE status = 'pendente'`): o que já foi entregue nunca mais é lido
pela fila, e é essa a parte da tabela que cresce.

### Por que um segundo agendador

O `AgendadorFollowUp` roda **uma vez por dia**, no começo do expediente — ritmo certo para lembrete,
errado para webhook: um lead criado às 9h05 chegaria no ERP no dia seguinte.

O que **é** reaproveitado dele é a rodada diária: o **expurgo** de entregas com mais de 30 dias mora
lá, porque é exatamente isso — trabalho diário. Pendurá-lo no agendador de webhooks, que acorda a
cada 30s, obrigaria a inventar um controle de "já rodei hoje".

`AgendadorWebhooks` herda as proteções do outro, e não por cópia cega:

- `try/catch` em volta da rodada — exceção que sobe derruba o `BackgroundService`, e a drenagem
  pararia em silêncio até o próximo deploy;
- **o log dentro do catch também é protegido.** Custou um diagnóstico de verdade no
  `AgendadorFollowUp`: o provider de EventLog do Windows lança `ObjectDisposedException` no
  desligamento, a exceção sai do catch, sobe, e derruba o serviço — o mecanismo que existe para o
  serviço nunca cair era por onde ele caía;
- `Task.Delay` com o `TimeProvider` injetado, para o teste não esperar de verdade.

Não há fuso, e a ausência é decisão: este agendador não tem hora do dia. Fuso importa para "às 8h",
que é o problema do outro.

### Retry limitado — e por que aqui ele existe

`IFilaSegundoPlano` faz uma tentativa de e-mail e desiste, de propósito: do outro lado há uma
**pessoa** e existe caminho alternativo (o link continua na tela). Aqui não há nenhum dos dois — o
receptor é um sistema, ninguém vai olhar uma tela, e um evento perdido é um pedido que nunca chegou
ao ERP.

Mas **para**: 1 min, 5 min, 30 min, e desiste. Os três espaçamentos cobrem três falhas reais e
distintas — o deploy do cliente, a queda curta, a manutenção. Além disso não é intermitência, é o
sistema dele fora do ar, e esperar não resolve. Repetir para sempre transformaria um receptor
quebrado numa fila que só cresce, e no dia em que ele voltasse receberia semanas de eventos velhos
de uma vez.

Timeout de **10s por tentativa**, com `CancellationToken` — não mexendo no `Timeout` do `HttpClient`,
que é compartilhado e afetaria todas as entregas em voo.

### A exceção: o botão de teste

É o único lugar do sistema que entrega **dentro da requisição**. A pessoa está olhando o botão, e um
"enviado, veja depois" não resolveria o chamado de suporte que ele existe para resolver — que é
sempre "não está chegando, e não sei por quê". Registrado como qualquer outra entrega, e **sem
reagendamento**: reagendar faria o botão mandar o mesmo evento de novo, sozinho, um minuto depois.

---

## 5. SSRF — a URL é do cliente, o servidor é nosso

Sem guarda, o cliente aponta para `http://169.254.169.254/latest/meta-data/` e o Nexora busca as
credenciais de nuvem da própria infraestrutura e as entrega — de dentro da rede, autenticado pelo
simples fato de a requisição sair de lá. Não é hipótese: é a forma mais comum de transformar
"webhook configurável" em leitura da rede interna.

**Recusado:** qualquer coisa que não seja `https`; `localhost`, `*.localhost`, `*.local`; loopback;
`10.`, `172.16-31.`, `192.168.`, `169.254.`, `100.64/10` (CGNAT), `0.0.0.0/8`, multicast; e em IPv6
link-local, site-local, `fc00::/7` e — o que quase escapa — **`::ffff:10.0.0.1`**, o IPv4 privado
vestido de IPv6.

A lista é por **exclusão**, não por inclusão: enumerar o que é público erraria para o lado perigoso a
cada faixa nova que a IANA reservar.

Não há exceção configurável para `http` em desenvolvimento. Uma exceção configurável vira a
configuração de produção de alguém.

### Validar duas vezes não é redundância

No cadastro **e antes de cada entrega**. A razão é DNS: `webhook.cliente.com` pode resolver para um
IP público no dia do cadastro e para `127.0.0.1` amanhã — quem controla a zona é o cliente, e ele
muda sem tocar no Nexora. Validar só na entrada é validar um valor que o outro lado pode trocar
depois.

**Confirmado por mutação:** troquei a validação da entrega pela versão sem DNS e
`ENTREGA_PARA_IP_PRIVADO_E_RECUSADA_NA_HORA_DO_ENVIO` reprovou. Revertido.

Duas travas a mais no `HttpClient`, que o cliente da Evolution não precisa ter:

- **`AllowAutoRedirect = false`.** Redirecionamento é a forma clássica de furar a checagem: a URL
  cadastrada é pública, responde 302, e o destino é `127.0.0.1`. Nós validamos a URL, não o que ela
  mandar seguir;
- `MaxConnectionsPerServer = 4` — um receptor lento não pode consumir o pool da aplicação inteira.

---

## 6. PII — a decisão é do cliente, e ela mora num lugar só

Nome e telefone saindo daqui para o servidor de um terceiro é **compartilhamento de dado pessoal**, e
precisa estar no contrato do cliente com esse terceiro. O Nexora não tem como saber se está — então
dá a opção, e a tela avisa, **colada na opção que a resolve**. Aviso longe do controle é aviso que
ninguém liga à decisão que está tomando.

`somente_ids` omite `nome`, `telefone`, `email`, `etapaNome`, `origemDetalhe`, `motivoPerda` e o
`texto` da mensagem. Dois desses merecem nota:

- **`motivoPerda` é texto livre** escrito pelo vendedor: "cliente sumiu", "o marido não deixou".
  Costuma ter nome de gente dentro;
- **o texto da mensagem** é o campo mais sensível do sistema inteiro: a conversa é do cliente do
  cliente, e ninguém do outro lado consentiu que ela saísse.

**Um lugar só monta o objeto do lead** (`PayloadWebhook.Lead`). Se cada evento montasse o seu,
bastaria um esquecer o `somenteIds` — e essa falha não aparece em teste de tela nem em log: aparece
numa auditoria, depois.

Falso por padrão porque o caso comum (criar o pedido no ERP) precisa do nome. Quem só quer disparar
uma sincronia liga e busca o resto pela API.

---

## 7. Schema

**Migration:** `20260806171623_WebhooksSaida`, aplicada em `nexora_dev`.

```sql
webhooks_saida (
    id, empresa_id, url varchar(500), segredo varchar(128),
    ativo, somente_ids,
    em_lead_criado, em_lead_movido, em_venda_fechada, em_venda_perdida,
    em_mensagem_recebida DEFAULT false,
    criado_em, atualizado_em)
uq_webhooks_empresa  UNIQUE (empresa_id)

entregas_webhook (
    id, empresa_id, evento_id uuid, evento evento_webhook_enum,
    payload jsonb, url, status status_entrega_webhook_enum,
    tentativas, codigo_resposta, erro,
    proxima_tentativa_em, entregue_em, criado_em)
ix_entregas_fila     (proxima_tentativa_em) WHERE status = 'pendente'
ix_entregas_empresa  (empresa_id, id)
ix_entregas_criado   (criado_em)
```

**Uma coluna booleana por evento**, não bitmask: `WHERE em_lead_criado` é legível numa consulta de
suporte, e a migration diz o nome de cada evento em vez de um número que só o código decodifica. São
cinco; o dia em que forem trinta, aí vira tabela.

**Um webhook por empresa**, e a trava é de schema — ao contrário do limite de conexões do ARQ-2. Lá o
número vem do contrato e muda; aqui é decisão de produto, e o dia em que mudar exige tela nova,
tabela de entrega por destino e outra conversa de suporte — ou seja, exige a migration de qualquer
jeito. Quem precisa de mais de um destino já usa um roteador, que é o público desta funcionalidade.

**Retenção: 30 dias.** É o suficiente para responder "vocês mandaram ou não mandaram?", que é a única
pergunta que o registro existe para responder.

---

## 8. Onde os eventos são publicados

| ponto | evento |
|---|---|
| `ServicoContatos.CriarAsync` | `lead.criado` |
| `ServicoContatos.MarcarGanhoAsync` | `venda.fechada` |
| `ServicoContatos.MarcarPerdidoAsync` | `venda.perdida` |
| `ServicoFunil.MoverAsync` | `lead.movido` (só quando a etapa muda) |
| `ProcessadorEventoEvolution` | `lead.criado` + `mensagem.recebida` |
| `ServicoCaptura` | `lead.criado` |

**`IgnoreQueryFilters` no publicador não é opcional.** Metade desses caminhos roda em **tenant zero**:
o processador do webhook da Evolution e a captação pública. Com o query filter, a busca de
`webhooks_saida` voltaria vazia nesses caminhos — e o resultado seria "o lead que entra pelo WhatsApp
nunca dispara webhook", em silêncio, enquanto o criado à mão na tela dispara. É a mesma armadilha do
INT-2, e custa o mesmo.

**Publicar nunca lança.** O lead continua criado e a venda continua fechada — um webhook que derruba
a operação do cliente é pior que um webhook que não sai.

### ⚠️ A fragilidade deste desenho, registrada

São **seis pontos de chamada explícitos**. Um caminho novo que crie contato sem chamar o publicador
não dispara evento nenhum, e nada acusa.

A alternativa considerada foi um interceptor de `SaveChanges` lendo o `ChangeTracker` — quatro dos
cinco eventos são estado de `Contato`, e o interceptor os pegaria num lugar só, sem poder ser
esquecido. Não foi feito por três razões concretas:

1. os ids de linhas novas só existem **depois** do INSERT, então a publicação teria que acontecer em
   `SavedChanges` — que é um segundo `SaveChanges`, fora da transação. A atomicidade que justificaria
   a complexidade não vem junto;
2. `mensagem.recebida` ficaria de fora de qualquer jeito: a mensagem é inserida por SQL cru
   (`INSERT … ON CONFLICT`), e o `ChangeTracker` não a vê;
3. o interceptor precisaria de estado por contexto, e o `InterceptorAuditoria` de hoje é sem estado.

A mitigação aceita é o teste: cada um dos seis pontos tem um caso que prende o disparo.

---

## 9. Testes

**Backend: 579 passando** (eram 512 ao abrir o bloco).

| arquivo | o que prende |
|---|---|
| `WebhookSaidaTests` (25) | assinatura contra o corpo exato e contra o timestamp, backoff 1/5/30 e parada, 2xx incluindo 202, SSRF em 19 URLs (incluindo `::ffff:10.0.0.1` e o IP de metadados), nome público que resolve para privado, payload versionado, nomes do contrato, `somenteIds` no lead **e** na mensagem, `webhook.teste` não assinável, `mensagem.recebida` desmarcado |
| `WebhookSaidaDbTests` (19) | os cinco eventos disparando quando marcados e não disparando quando desmarcados, webhook desativado, isolamento por tenant, **receptor lento não atrasa a requisição**, entrega assinada com o corpo idêntico ao guardado, backoff que reagenda e para na terceira, SSRF na entrega, evento de teste sem dado real, reenvio, segredo revelado uma vez, expurgo aos 30 dias (com contra-teste aos 29) |

Os oito do critério, nominalmente:

| critério | teste |
|---|---|
| cada evento dispara quando marcado, e não quando desmarcado | `LEAD_CRIADO_DISPARA_QUANDO_MARCADO` · `LEAD_CRIADO_NAO_DISPARA_QUANDO_DESMARCADO` · `MENSAGEM_RECEBIDA_SO_SAI_SE_MARCADA` · `LEAD_MOVIDO_SO_DISPARA_QUANDO_A_ETAPA_MUDA` · `VENDA_FECHADA_E_PERDIDA_DISPARAM_COM_A_ETAPA_ANTERIOR` |
| assinatura confere contra o corpo exato | `A_ASSINATURA_CONFERE_CONTRA_O_CORPO_EXATO` · `A_RODADA_ENTREGA_E_ASSINA_O_CORPO_EXATO` |
| receptor lento não atrasa a requisição | `RECEPTOR_LENTO_NAO_ATRASA_A_REQUISICAO_DO_USUARIO` |
| falha reagenda com backoff e para na terceira | `FALHA_REAGENDA_COM_BACKOFF_E_PARA_NA_TERCEIRA` |
| URL privada/loopback/http recusada no cadastro **e** na entrega | `URL_INTERNA_E_RECUSADA_NO_CADASTRO` · `ENTREGA_PARA_IP_PRIVADO_E_RECUSADA_NA_HORA_DO_ENVIO` |
| modo "só ids" não envia nome nem telefone | `MODO_SO_IDS_NAO_DEIXA_NOME_NEM_TELEFONE_SAIR` · `MODO_SO_IDS_NAO_MANDA_NOME_NEM_TELEFONE` |
| entrega com mais de 30 dias é expurgada | `ENTREGA_COM_MAIS_DE_30_DIAS_E_EXPURGADA` (+ `Entrega_recente_NAO_e_expurgada`) |
| evento de teste sem dado real | `O_EVENTO_DE_TESTE_FUNCIONA_SEM_DADO_REAL` |

**O receptor falso assina com o mesmo código do cliente real**, e o teste confere a assinatura de
verdade — em vez de comparar com uma string inventada no próprio teste.

**Frontend: 217 passando** (eram 202). `integracoes.spec.ts` novo, com 11 casos: o segredo aparecendo
uma vez e não sendo apagado ao salvar de novo, o aviso de LGPD no mesmo cartão do controle, o modo
"só ids" indo no PUT, o resultado do teste na tela, só a entrega falha oferecendo reenviar, e o
exemplo de validação contendo `timestamp + '.' + corpo`, `express.raw` e `timingSafeEqual`.

O último merece nota: **um snippet errado na tela vira receptor inseguro em todo cliente que
copiar.** Os três erros clássicos que ele evita são assinar só o corpo (replay), reserializar o corpo
(HMAC não bate) e comparar com `===` (vaza a assinatura byte a byte pelo relógio).

---

## 10. Menu

`Integrações` entrou no grupo CONFIGURAÇÃO — e não antes. O NAV-1 registrou a regra e a deixou como
teste; ele foi reescrito aqui para o que **não** muda de bloco para bloco:

> Todo item de menu leva a uma rota que existe.

É o inverso da mesma exigência, e continua valendo para o próximo item que alguém quiser adicionar.

---

## 11. Pendências e limites

| O quê | Por quê |
|---|---|
| **Sem lock distribuído** | O mesmo limite do `AgendadorFollowUp`: com duas instâncias, as duas drenam e o receptor pode receber a mesma entrega duas vezes. A defesa que funciona nesse caso é o `id` do evento — o receptor deduplica. Advisory lock do Postgres resolve em poucas linhas quando o Nexora escalar horizontal |
| Seis pontos de publicação explícitos | §8. Um caminho novo pode esquecer, e nada acusa |
| Um webhook por empresa | §7. Quem precisa de mais usa um roteador |
| Sem filtro por etapa ou responsável | `lead.movido` sai em toda mudança de etapa. Quem quiser só "chegou em Proposta" filtra do lado dele |
| Sem `webhook.entregue` / relatório de saúde | O registro na tela responde; não há métrica agregada nem alerta quando as entregas começam a falhar |
| Nada verificado contra um receptor real | Segue valendo o que os blocos anteriores registram: as entregas foram exercitadas contra um cliente falso, e nenhum servidor de verdade recebeu um POST deste código |
| O `MaximoPorRodada` de 200 é silencioso | A rodada corta em 200 por passada e o resto sai na seguinte. Não há log dizendo "sobrou fila" |

---

## 12. Arquivos

**Backend**

```
src/Nexora.Core/Entidades/WebhookSaida.cs               novo — config + EntregaWebhook
src/Nexora.Core/Entidades/Enums.cs                      + EventoWebhook, StatusEntregaWebhook, ParaApi()
src/Nexora.Core/Webhooks/AssinaturaWebhook.cs           novo — HMAC, headers, segredo
src/Nexora.Core/Webhooks/ValidadorUrlWebhook.cs         novo — SSRF + IResolvedorDns
src/Nexora.Core/Webhooks/PoliticaEntrega.cs             novo — retry, timeout, retenção
src/Nexora.Core/Webhooks/PayloadWebhook.cs              novo — envelope versionado + PII
src/Nexora.Core/Webhooks/IPublicadorEventos.cs          novo
src/Nexora.Core/Servicos/IServicoWebhooks.cs            novo
src/Nexora.Infra/Webhooks/PublicadorEventos.cs          novo
src/Nexora.Infra/Webhooks/ClienteWebhook.cs             novo — POST + ResolvedorDns
src/Nexora.Infra/Webhooks/MotorWebhooks.cs              novo — drenagem + expurgo
src/Nexora.Infra/Servicos/ServicoWebhooks.cs            novo
src/Nexora.Api/Controllers/WebhooksSaidaController.cs   novo
src/Nexora.Api/Servicos/AgendadorWebhooks.cs            novo
src/Nexora.Api/Servicos/AgendadorFollowUp.cs            + expurgo na rodada diária
src/Nexora.Infra/Servicos/{ServicoContatos,ServicoFunil,ServicoCaptura}.cs   pontos de publicação
src/Nexora.Infra/Evolution/ProcessadorEventoEvolution.cs                     pontos de publicação
src/Nexora.Infra/Persistencia/NexoraDbContext.cs
src/Nexora.Infra/ServicosInfra.cs                       + AdicionarWebhooksSaida
src/Nexora.Api/Program.cs
src/Nexora.Infra/Persistencia/Migrations/20260806171623_WebhooksSaida.cs
```

**Frontend**

```
src/app/paginas/integracoes/{integracoes.ts,.html,.css,.spec.ts}   novos
src/app/nucleo/servicos/webhooks.servico.ts                        novo
src/app/nucleo/modelos.ts                                          + os DTOs
src/app/app.routes.ts                                              /integracoes
src/app/layout/shell/shell.html                                    item de menu
```

**Testes**

```
tests/Nexora.Tests/Unidade/WebhookSaidaTests.cs        novo (25) + DnsFalso
tests/Nexora.Tests/Integracao/WebhookSaidaDbTests.cs   novo (19) + ClienteWebhookFalso
tests/Nexora.Tests/Integracao/PublicadorDeTeste.cs     novo — o publicador REAL nos testes alheios
```
