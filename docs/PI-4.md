# PI-4 — Série temporal e fim do modo demonstração

Estado: **fechado**. Os 5 critérios verificados por execução.

`dotnet build -warnaserror` limpo, `ng build` limpo, **352 testes de backend** (305 → 352, +47) e
**89 de frontend**, todos verdes.

O `grafico-linha.ts` existia desde o bloco 9 com um aviso no topo: *"AINDA NÃO ESTÁ LIGADO EM
NENHUMA TELA — não existe endpoint que devolva série temporal"*. Agora existe.

---

## 1. `GET /api/dashboard/serie`

```
GET /api/dashboard/serie?de=2026-07-07&ate=2026-08-05&agrupamento=dia|semana|mes
```

Quatro séries numa consulta só: **leads criados**, **vendas fechadas**, **faturamento** e **tempo
médio de resposta**. Padrão: últimos 30 dias por dia. Teto de 400 dias — não por medo do banco,
que agrega isso sem suar, mas porque o gráfico tem 1000px: mais que isso é um ponto por pixel.

### O filtro preserva o índice

Os cortes são sempre `coluna >= @inicio AND coluna < @fim`, com os limites calculados no C# a
partir do fuso de negócio e passados como **parâmetro**. `date_trunc` aparece só no `SELECT` e no
`GROUP BY`, sobre o conjunto já recortado.

`WHERE date_trunc('month', criado_em) = @mes` daria o mesmo resultado e descartaria o índice:
função sobre a coluna força o planejador a calcular a expressão linha a linha.

`contatos` já tinha `ix_contatos_criado` e `ix_contatos_ganho`. Foram criados dois índices novos:

| Índice | Serve |
|---|---|
| `ix_msg_serie (empresa_id, criado_em DESC)` | tempo de resposta **e** feed de atividades |
| `ix_lembretes_concluido (empresa_id, concluido_em DESC) WHERE concluido_em IS NOT NULL` | feed |

Um índice em vez de dois para mensagens: no webhook, mensagem de entrada recebe o mesmo instante
em `recebida_em` e em `criado_em`, então ordenar por `criado_em` dá a mesma ordem sem custar uma
segunda árvore para manter em cada INSERT.

### Dia sem dado volta com zero

`generate_series` gera todos os pontos do intervalo e as CTEs de dado entram por `LEFT JOIN`. Sem
isso, um dia sem venda simplesmente não voltaria — e a linha ligaria o ponto anterior no seguinte,
desenhando subida onde houve buraco.

**Uma exceção, deliberada:** `tempoRespostaMinutos` volta **null**, não zero, em período sem
resposta medida. O período em si nunca falta — que é a regra do prompt e o que impede o gráfico de
mentir sobre a tendência. Mas média zero diria *"respondeu instantaneamente"*, e a métrica passaria
a mostrar seu melhor número justamente nos dias em que ninguém trabalhou. Contagem e dinheiro em
período vazio valem zero porque isso é um fato; média não.

Na tela, os períodos sem medição ficam fora da linha e a legenda diz quantos foram. Se preferir
zero, é uma linha em [IServicoSerie.cs](src/Nexora.Core/Servicos/IServicoSerie.cs).

### Tempo de resposta

Para cada **primeira mensagem de uma rajada** de entrada, a primeira saída seguinte na mesma
conversa. "Primeira da rajada" é a mesma regra do `aguardando_desde ??=` do webhook: se o cliente
manda três mensagens seguidas, ele espera desde a primeira. Contando as três, a média cairia
artificialmente — as duas últimas teriam "esperado" quase nada.

Conversa sem resposta fica **fora** da média: entrar como zero premiaria quem não respondeu.

A janela de varredura vai **2 dias além** do fim do período, só para achar a resposta. Sem isso, a
mensagem que chega 23h50 do último dia e é respondida 00h10 do dia seguinte entraria como "sem
resposta" — o corte da consulta inventaria um problema de atendimento que não existiu.

---

## 2. Minutos úteis agora existe TRÊS vezes — e as três estão amarradas

A média desconta o que está fora da janela e os feriados. Como toda agregação tem que ser SQL, a
regra precisou de uma implementação em PL/pgSQL: `nexora_minutos_uteis(...)`.

Ela é a **terceira** cópia da mesma regra, ao lado de `TempoUtil.MinutosUteis` (C#, para o Meu Dia)
e `minutosUteis` (TypeScript, para a cor do semáforo envelhecer sem novo fetch).

Três cópias é dívida. Ela é paga com o arreio montado no PI-3: **os mesmos 16 casos de
`tests/paridade/minutos-uteis.json` agora rodam contra os três lados.**

```
ParidadeMinutosUteisTests          (C#)          16 casos + guarda
ParidadeMinutosUteisSqlDbTests     (PostgreSQL)  16 casos + trava de 400 + NULL
semaforo.paridade.spec.ts          (TypeScript)  16 casos + guarda
```

Os três concordaram nos 16 casos na primeira execução. Mexer num lado só deixa os outros vermelhos.

⚠️ **Uma armadilha de fuso que quase entrou:** a primeira versão passava o fuso para o SQL como
`"UTC-03"`, montado à mão a partir do id do fallback `br-fixo`. Em sintaxe POSIX o sinal é
**invertido** — `AT TIME ZONE 'UTC-03'` significa três horas a *leste* de Greenwich, o oposto de
Brasília. Seriam seis horas de erro em cada ponto, sem nenhum erro de execução para denunciar. O
serviço manda o nome IANA: o fallback fixo do .NET existe porque `FindSystemTimeZoneById` depende
do tzdata do host, e o PostgreSQL embarca o próprio.

---

## 3. `GET /api/dashboard/atividades`

Feed por cursor com quatro fontes em `UNION ALL`: **mensagem recebida**, **venda fechada**,
**follow-up concluído** e **contato criado**.

`proposta enviada` **não existe no domínio** — não há entidade de proposta no Nexora. Fica de fora
e registrado.

### O cursor

`(quando, chave)`, onde `chave` é `tipo:id`. O `id` sozinho colide entre as tabelas — mensagem 7 e
contato 7 existem ao mesmo tempo —, e sem desempate estável a paginação pularia ou repetiria linha
no empate de timestamp.

### A visibilidade é decidida na API

| Papel | Vê |
|---|---|
| Vendedor | as próprias atividades **e** as de contato sem dono |
| Gestor / Dono | a empresa inteira, com filtro opcional por responsável |

Para Vendedor o parâmetro `responsavelId` é **descartado** e o próprio usuário é imposto. Filtro de
visibilidade que confia no parâmetro do cliente não é filtro.

"Sem dono" entrar junto é a mesma regra do Meu Dia e da caixa: contato sem responsável é de todo
mundo. O que o vendedor não pode ver é o que está com **outro** vendedor.

Verificado contra `nexora_dev`, por HTTP:

```
gestor    -> responsáveis presentes: [null, 1 Ana, 7 Beatriz, 8 Rafael]
vendedor  -> responsáveis presentes: [null, 8 Rafael]
vendedor pedindo ?responsavelId=7  -> responsáveis presentes: [null, 8]
```

---

## 4. O tempo medido

Carga: **30.000 mensagens** espalhadas por ~416 dias num tenant, alternando entrada e saída
(~13 mil rajadas respondidas). Postgres 17 local. Primeira execução descartada (plano e cache
frios); o número é da segunda.

| Consulta | Pontos | Tempo |
|---|---|---|
| 30 dias por dia | 31 | **21 ms** |
| 90 dias por semana | 14 | **55 ms** |
| 365 dias por mês | 13 | **182 ms** |
| Feed de atividades (20 itens) | 20 | **11 ms** |

### O que essa medição pegou

A primeira versão da consulta **estourou o timeout de 30 segundos**. A causa não era volume:

```sql
MIN(criado_em) FILTER (WHERE direcao = 'saida') OVER (
    PARTITION BY conversa_id ORDER BY id
    ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING)
```

Lê igualzinho ao enunciado do problema — "o instante da primeira saída depois desta linha" — e é
**quadrático**. `MIN` não tem função de transição *inversa*, então o Postgres não consegue mover o
quadro incrementalmente: ele recalcula o agregado inteiro a cada linha. Numa conversa com 26 mil
mensagens são ~676 milhões de operações.

A forma equivalente linear: `SUM` sobre janela padrão (que **tem** transição inversa) numerando as
saídas, e um join de `grupo + 1`. Mesma resposta, uma passada e um hash join.

**De timeout para 182 ms no pior caso.** Sem a medição exigida pelo critério 4, isso teria ido para
produção e só aparecido no primeiro cliente com um ano de histórico.

O teste `O_FILTRO_PRESERVA_O_INDICE_E_A_AGREGACAO_ACONTECE_NO_BANCO` guarda os três fatos:
`EXPLAIN` mostra `ix_msg_serie`, não mostra `Seq Scan on mensagens`, e a consulta devolve 31
linhas em vez de 30 mil.

⚠️ Registro de um erro meu no caminho: a primeira versão desse teste concentrava as 30 mil
mensagens nos mesmos 31 dias consultados, e o planejador escolhia `Seq Scan` — **corretamente**,
porque ler 100% da tabela por índice é mais caro que varrer. O teste reprovava um comportamento
certo. Índice só se prova onde ele serve: recorte pequeno sobre base grande, que é a forma real da
pergunta do dashboard.

---

## 5. O veredicto sobre o modo demonstração

**Ainda tem uso. Não pode sair agora.**

O que os dois endpoints novos substituíram foi a *dependência técnica*: o gráfico e o feed já não
precisam do gerador. O que sobrou é uma função comercial, que nenhum dado real resolve:

- **Empresa nova tem o painel vazio.** É o estado normal no dia 1, e é exatamente quando o cliente
  decide se fica. O modo demo responde "como isto fica quando estiver rodando" — e a tela vazia já
  o oferece explicitamente ("Quer ver como o painel fica cheio?").
- **Demonstração comercial e material de venda** precisam de um painel cheio que não seja de um
  cliente real. Usar dados de cliente em captura de tela é problema de LGPD, não de conveniência.
- O gerador é **determinístico e não toca no banco** (`Singleton`, sem `DbContext`), então não tem
  custo de manutenção acoplado ao schema.

### Como ele ficou rotulado

Enquanto existir, ninguém pode confundir um print do painel com um relatório:

- **Faixa fixa no topo**, acima de todo número da tela:
  *"⚠ Exemplo fictício — estes números não são da sua empresa."*
- O botão diz **"Ver exemplo"** / **"Ver meus dados"**, não "Ver demonstração": *demonstração* pode
  ser lido como "uma amostra dos meus dados".
- Continua em **rota separada** (`/api/dashboard/demo`), não numa flag do endpoint real. Uma flag
  colocaria dado inventado no mesmo caminho do dado verificado, e bastaria alguém esquecê-la ligada.

### Quando puder sair, sai isto junto

| Arquivo / trecho | |
|---|---|
| `src/Nexora.Infra/Servicos/ServicoDashboardDemo.cs` | arquivo inteiro |
| `src/Nexora.Core/Servicos/IServicoDashboardDemo.cs` | interface + 7 records (`IndicadorDemo`, `EtapaFunilDemo`, `OrigemDemo`, `AtividadeDemo`, `TarefaDemo`, `PontoSerieDemo`, `DashboardDemo`) |
| `DashboardController.Demo()` | ação + injeção de `IServicoDashboardDemo` |
| `ServicosInfra.cs` | `AddSingleton<IServicoDashboardDemo, ...>` |
| `modelos.ts` | os mesmos 7 tipos + `TipoAtividade` |
| `dashboard.servico.ts` | `demo()` |
| `dashboard.ts` | `modoDemo`, `demo`, `carregandoDemo`, `erroDemo`, `serieEscolhida`, `alternarDemo`, `carregarDemo`, `serie`, `formatoSerie`, `fatias`, `totalOrigens`, `larguraFaixa`, `iconeAtividade`, `valorIndicador`, `variacao`, `etapaValor` |
| `dashboard.html` | bloco `@if (modoDemo())` inteiro, os dois botões do topo, e o convite na tela vazia |
| `dashboard.css` | `.faixa-demo`, `.rosca`, `.funil-demo`, `.abas` do bloco demo |

Nada disso é compartilhado com o caminho real — a remoção é mecânica.

---

## 6. Critérios

| # | Critério | Estado |
|---|---|---|
| 1 | Builds limpos, testes verdes | ✅ 352 backend + 89 frontend |
| 2 | Os seis testes exigidos | ✅ ver abaixo |
| 3 | Dashboard mostra gráfico com dado real | ✅ verificado em `nexora_dev` |
| 4 | Base grande responde em tempo aceitável, medido | ✅ 21/55/182 ms |
| 5 | Modo demo rotulado de forma inequívoca | ✅ faixa + rótulo do botão |

| Exigido | Teste |
|---|---|
| série bate com contagem manual | `SERIE_BATE_COM_A_CONTAGEM_MANUAL` |
| dia sem dado volta com zero | `DIA_SEM_DADO_VOLTA_COM_ZERO_NAO_AUSENTE` |
| tempo médio desconta fora da janela | `TEMPO_MEDIO_DESCONTA_AS_HORAS_FORA_DA_JANELA` (22h → 8h05 = **5 min**, não 10h) |
| Vendedor não vê o de outro, nem pela API | `VENDEDOR_NAO_VE_ATIVIDADE_DE_OUTRO_VENDEDOR_NEM_PELA_API_DIRETA` |
| nenhuma agregação em memória | `O_FILTRO_PRESERVA_O_INDICE_...` — 31 linhas de 30 mil mensagens |
| nenhuma função sobre coluna em filtro | mesmo teste — `EXPLAIN` acha `ix_msg_serie`, não acha `Seq Scan` |

E mais: só a primeira da rajada conta, conversa sem resposta fora da média, agrupamento por
semana/mês, isolamento de tenant nas duas rotas, feed com os quatro tipos, e cursor sem repetir
nem pular.

Verificação por HTTP contra `nexora_dev` (tenant com 162 mensagens, 40 contatos):

```
GET /api/dashboard/serie?agrupamento=dia   -> 30 pontos, 19 com dado, 11 zerados PRESENTES
GET /api/dashboard/serie?...&agrupamento=semana -> tempo médio 6,4 min e 153 min nas semanas medidas
GET /api/dashboard/atividades              -> recorte por papel confirmado (§3)
```

---

## Pendências

**Deste bloco:**

1. **A semente escrevia `criado_em` errado — corrigido, e vale explicar.** O
   `InterceptorAuditoria` carimba `criado_em` com o relógio em todo INSERT. Enquanto a semente
   servia só à thread (que ordena por `id` e exibe `enviada_em`/`recebida_em`), isso não incomodava
   — havia até um comentário no `ServicoSemente` dizendo que não era problema. O comentário ficou
   **falso** neste bloco: a série e o feed usam `criado_em` como instante do evento. Com o carimbo
   de "agora", 55 das 56 rajadas respondidas caíam na semana da geração com média **zero**, e
   parecia defeito da consulta. A semente agora fixa `criado_em`, e os dados de `nexora_dev`
   existentes foram alinhados com a mesma regra.
2. **A semente só roda uma vez por banco.** `CriarEquipeAsync` usa e-mails fixos (`@semente.dev`) e
   `uq_usuarios_email` é global, então semear um segundo tenant dá 500. Anterior ao PI-4; não
   corrigi porque está fora do escopo. O conserto é sufixar o e-mail com o `empresa_id`.
3. **Tempo de resposta conta follow-up automático como resposta.** Uma mensagem disparada pelo
   motor encerra a espera do cliente — do ponto de vista dele, foi respondido. Para medir a
   equipe, o certo seria excluir `lembrete_id IS NOT NULL`. É um `AND` na CTE `saidas`; deixei como
   está porque a escolha é de produto, não técnica.
4. **`criado_em` é o instante do evento, não `recebida_em`.** No webhook os dois recebem o mesmo
   valor, então em produção não há diferença — e `criado_em` é o que tem índice. Se algum caminho
   futuro divergir os dois, esta escolha precisa ser revista.
5. **Sem cache.** Toda abertura do dashboard refaz as quatro séries. A 21ms não incomoda; com
   muitos usuários simultâneos na mesma empresa, um cache curto por tenant seria o próximo passo.

**Carregadas, ainda abertas:**

6. **Pipeline nunca executou** — o Nexora não é repositório git (PI-3, critério 1). Falta
   `git init`, um commit e um remoto.
7. **`<input type="time">` manda `"14:30"` e `TimeOnly` espera `"14:30:00"` → 400.**
   [contato.ts:281](frontend/nexora-painel/src/app/paginas/contato/contato.ts#L281). Desde o PI-1.
8. **Funil do dashboard e do kanban contam diferente.**
   [ServicoDashboard.cs:57-59](src/Nexora.Infra/Servicos/ServicoDashboard.cs#L57-L59) não filtra
   `anonimizado_em IS NULL`. Aberta desde o bloco 9 — é uma linha, e segue aguardando sua decisão.
9. **`paginas/em-breve/` é código morto** (PI-3).
10. Nenhum celular pareado de verdade; arrastar card do kanban nunca testado em navegador; sem lock
    distribuído no agendador; sem SPF/DKIM/DMARC documentados; três tenants de verificação em
    `nexora_dev`.
