# Bloco 9 — Tela do dashboard

Estado: **fechado**. Os 6 critérios verificados por execução.

`ng build` limpo. Nenhuma biblioteca instalada. Nenhum endpoint criado — as duas faltas de API
estão registradas na §2.

---

## 1. O que foi construído

| Arquivo | O que é |
|---|---|
| `paginas/dashboard/dashboard.ts` + `.html` + `.css` | A tela |
| `nucleo/graficos/grafico-linha.ts` + `.html` + `.css` | O gráfico do Recupera, portado |
| `app.routes.ts` | rota `/dashboard` |
| `layout/shell/shell.html` | link "Dashboard" no topo da navegação |

**Acrescentado DEPOIS do fechamento original deste bloco** (a pedido, para desenhar a tela cheia
sem depender de uma base com meses de uso):

| `Core/Servicos/IServicoDashboardDemo.cs` + `Infra/Servicos/ServicoDashboardDemo.cs` | Gerador de demonstração — não lê o banco |
| `Api/Controllers/DashboardController.cs` | rota `GET /api/dashboard/demo` |
| `dashboard.servico.ts` | método `demo()` |

O serviço `dashboard.servico.ts` já existia desde o bloco 6; ganhou só o método do modo
demonstração.

### A tela

- **Os quatro números** em cartões clicáveis, cada um levando à tela onde se age sobre ele: leads
  → contatos, aguardando → caixa, follow-ups → Meu Dia, vendas → funil. Um número que o vendedor
  não pode atacar é um número que ele ignora.
- **Faturamento do mês** e **taxa de conversão** em dois cartões maiores.
- **Funil visual** — barra horizontal por etapa, com contagem e valor.
- **Atividade recente** — as 8 conversas mais recentes, com prévia e contador de não lidas.

---

## 2. O que faltou de API

O prompt manda registrar a falta e seguir sem criar endpoint. Faltam **duas coisas**, e uma delas
inviabiliza um dos complementos pedidos.

### 2.1 Série temporal — não existe. O gráfico ficou sem alimento

O prompt pede "gráfico de linha — evolução no período". **Não existe endpoint que devolva série
temporal.** O `DashboardDto` (bloco 6) tem os agregados do momento e o funil, nada por dia.

Porte feito. **Com dado real, o gráfico não é renderizado** — e não por descuido. A alternativa
seria montar a série no navegador a partir de `GET /api/contatos`, e ela seria errada de duas
formas: a lista é paginada e ordenada por **nome**, então uma série derivada dela mostraria só os
contatos da página corrente — e agregar em memória sobre lista paginada é exatamente o que o
projeto proíbe desde o bloco 5. Um gráfico que mente é pior que um gráfico ausente.

**ATUALIZAÇÃO (depois do fechamento original):** o componente PASSOU a ser renderizado, mas só no
**modo demonstração** (§2.3), alimentado pela série gerada de 90 dias. No caminho de dado real
ele continua sem alimento, e a falta de endpoint segue valendo.

**O que ele precisa para valer com dado real:** um endpoint tipo
`GET /api/dashboard/serie?de=&ate=&metrica=` devolvendo `[{ data, valor }]` agregado **no SQL**
por dia. As métricas úteis seriam leads/dia, vendas/dia e faturamento/dia. Os índices
`ix_contatos_criado` e `ix_contatos_ganho` (ambos do bloco 2) já servem essas consultas.

### 2.2 Atividades recentes — só uma das três fontes existe

O prompt pede "últimas mensagens, vendas e lembretes concluídos". O que dá para montar hoje:

| Fonte | Estado |
|---|---|
| Últimas mensagens | **ok** — `GET /api/conversas` ordena por `ultima_mensagem_em DESC`, que é exatamente isso |
| Vendas recentes | **não dá** — `GET /api/contatos?filtro=Ganhos` ordena por **nome**, não por `ganho_em` |
| Lembretes concluídos | **não existe** — só há `GET /api/lembretes/contato/{id}`, por contato |

Sobre as vendas: eu poderia puxar uma página e ordenar por `ganhoEm` no cliente, mas isso
mostraria as vendas mais recentes **entre as N primeiras em ordem alfabética** — a venda de
"Zuleica" fechada hoje sumiria em favor da de "Ana" fechada semana passada. É errado de um jeito
que o usuário não perceberia. Deixei de fora.

O cartão ficou como **"Atividade recente"** listando conversas — o que ele mostra é verdade. Para
o resto: `GET /api/contatos?ordenar=ganho_em` (ou um `GET /api/dashboard/atividades` que já una as
três fontes ordenadas no SQL) e um `GET /api/lembretes?status=concluido&limite=N`.

### 2.3 O modo demonstração — acrescentado depois

Para desenhar a tela cheia sem uma base com meses de uso, entrou um **endpoint de demonstração**:
`GET /api/dashboard/demo`. Não lê o banco; é um gerador determinístico.

**Rota separada, e não uma flag no `/api/dashboard`.** O dashboard real foi conferido número a
número contra o SQL (§6). Uma flag colocaria dado inventado no mesmo caminho do dado verificado, e
bastaria alguém esquecê-la ligada para a empresa ver faturamento que não existe. Na tela é um
MODO, com faixa de aviso no topo.

O que ele devolve:

| Bloco | Volume | O que falta para virar real |
|---|---|---|
| Indicadores | 4, com variação % | comparação entre dois períodos — o `/api/dashboard` só agrega o momento |
| Funil | 5 etapas | nada; já existe no payload real |
| Origens | 5, somando 100% | só um `GROUP BY` sobre `contatos.origem` — barato |
| Atividades | 60 eventos tipados | o endpoint da §2.2 |
| Tarefas pendentes | 4 contadores | "follow-ups" já existe; o resto depende de tipo de tarefa, que a fase 1 não modela |
| Série faturamento / leads | 90 dias cada | o endpoint da §2.1 |

**Determinístico** (semente fixa): a mesma chamada devolve os mesmos números. Números que mudam a
cada F5 impedem revisar layout e escondem bug de renderização atrás de ruído. As **datas**, ao
contrário, saem do relógio — um mock com data fixa envelhece e passa a mostrar "última atividade
há três meses".

Duas peças de tela nasceram aqui e servem para dado real quando os endpoints existirem: o **funil
desenhado como trapézio** (com piso de 28% de largura, senão a última etapa vira um fio de 2% e o
rótulo não cabe) e a **rosca de origens em SVG puro**, sem biblioteca, no mesmo princípio do
`grafico-linha`.

---

## 3. A separação barato / caro

Respeitada, e verificada no código:

- **`/api/painel/status`** (barato) — quem chama em polling de 45s é o **shell**, em
  `shell.ts:carregarStatus`. Continua sendo o único que ele chama. Não toquei nisso.
- **`/api/dashboard`** (caro) — a página pede **uma vez**, no `ngOnInit`. Não há `setInterval`
  nesta tela; o botão "Atualizar" é explícito, do usuário.

**Uma divergência entre o prompt e o que existe, que vale registrar:** o prompt descreve o
endpoint barato como sendo "os contadores". Ele não é. O `/api/painel/status` tem `naoLidas`,
`aguardando`, estado da conexão, faixas do semáforo e feriados — dos quatro números do dashboard,
só "aguardando resposta" está lá. Leads de hoje, follow-ups pendentes e vendas do mês vêm do
payload caro. Isso não é problema (a tela pede uma vez), mas significa que **os quatro números não
poderiam ser postados de 45 em 45 segundos** sem um endpoint novo.

---

## 4. Comportamento

### Estado vazio orientado

Empresa sem nenhum contato **não vê zeros**: vê "Seu painel começa aqui", a explicação de que o
primeiro cliente que mandar mensagem vira lead automaticamente, e dois botões de próximo passo —
"Conectar meu WhatsApp" (só para o dono) e "Cadastrar um contato".

O sinal usado é a **soma de contatos no funil**, não os quatro números. É o único campo do payload
que distingue **empresa nova** de **mês fraco**: um dashboard zerado em agosto é informação
legítima; zerado porque nada nunca aconteceu precisa de outra tela. Zerar por mês ruim continua
mostrando os números.

Verificado contra uma empresa recém-criada de verdade (§6).

### Carregamento progressivo

Duas requisições **independentes e paralelas**: `/api/dashboard` (números e funil) e
`/api/conversas` (atividade). Cada bloco tem seu esqueleto `.skel` — os quatro cartões aparecem
assim que os números chegam, sem esperar a lista.

### Falha parcial

Cada requisição tem seu próprio `error`. Se a atividade falhar, os quatro números continuam na
tela e só aquele cartão mostra o aviso, com um "Recarregar". Se os números falharem, aparece uma
faixa de erro com "Tentar de novo" — e a atividade, que é outra requisição, continua visível.

Não existe caminho em que uma falha derrube a tela inteira.

---

## 5. Cores

**Nenhuma cor nova.** O funil usa a coluna `etapas_funil.cor`, semeada no cadastro da empresa como
um degradê de um tom só:

```
#7FA88B → #5C8F6E → #3E7554 → #2F5D3A → #1E4028
```

Verde clareando para escuro conforme a etapa avança — não são cinco cores diferentes, e não foram
escolhidas por mim: vêm do banco.

O gráfico portado trocou o âmbar do Recupera (`--amber`, `--ink`, `--ink-2`) pelos tokens do
Nexora (`--verde-2`, `--verde-3`, `--verde`, `--texto-fraco`).

Os números usam `--verde` como padrão, `--urgencia-media` quando há coisa esperando (aguardando
resposta e follow-ups pendentes acima de zero) e `--verde-3` nas vendas. Todos tokens existentes.

`funil-estados` do Recupera **não foi portado**, como o prompt determina — é funil por estado de
dívida.

---

## 6. Como cada critério foi verificado

| # | Critério | Resultado |
|---|---|---|
| 1 | `ng build` limpo | Executado, sem warning novo. `tsc --noEmit` também passa |
| 2 | Os quatro números batem com o banco | **Conferido número a número contra SQL** |
| 3 | Marcar venda muda vendas do mês e faturamento | **Medido antes e depois** |
| 4 | Empresa sem dado mostra estado vazio orientado | **Payload de empresa vazia real conferido** |
| 5 | Falha do caro não derruba os contadores | Duas requisições independentes, cada uma com seu `error` |
| 6 | Shell continua no endpoint barato | `shell.ts` só chama `painel.status()`; não foi alterado |

### Critérios 2 e 3 — cada número contra o SQL equivalente

Com a API de pé contra o `nexora_dev`, comparei cada campo do `/api/dashboard` com a consulta SQL
correspondente, usando o mesmo corte de fuso (`America/Sao_Paulo`) que o serviço usa:

```
ANTES DA VENDA
  leadsHoje            API=1        BANCO=1
  aguardandoResposta   API=0        BANCO=0
  followUpsPendentes   API=0        BANCO=0
  vendasDoMes          API=0        BANCO=0
  faturamentoDoMes     API=0,0      BANCO=0.00
  taxaConversao        API=0        BANCO=0
  funil (5 etapas)     todas batem

>>> venda de R$ 2.450,75 registrada pela API da etapa 7

DEPOIS DA VENDA
  leadsHoje            API=2        BANCO=2
  vendasDoMes          API=1        BANCO=1     <- mudou
  faturamentoDoMes     API=2450,75  BANCO=2450.75  <- mudou
  taxaConversao        API=1        BANCO=1     <- mudou
  funil: Venda         API=1        BANCO=1     <- o card foi para a coluna
```

Duas falhas apareceram no meio do caminho e as duas eram **do meu script de conferência**, não do
produto: comparação de decimal como texto (`0,0` contra `0.00`) e, depois, um round-trip pela
cultura pt-BR que lia `2450.75` como 245.075. Corrigidas para comparação numérica com
`InvariantCulture`.

### Critério 4 — a empresa vazia de verdade

Criei uma empresa sem etapas e sem contatos direto no banco, emiti um token para ela e chamei o
endpoint:

```json
{ "leadsHoje": 0, "aguardandoResposta": 0, "followUpsPendentes": 0,
  "vendasDoMes": 0, "faturamentoDoMes": 0.0, "taxaConversao": 0, "funil": [] }
```

`funil` vem **vazio** — empresa nova nem tem etapas ainda. O `reduce` com valor inicial 0 lida com
os dois casos (funil vazio e funil com etapas zeradas) e dispara o estado vazio. A empresa
temporária foi removida em seguida, junto do contato de teste; o `nexora_dev` voltou ao estado
anterior (1 empresa, 1 contato, sem venda).

**O que não foi verificado por renderização:** não tenho navegador nesta sessão, então confirmei o
payload que cada estado consome e a lógica que reage a ele, não os pixels.

---

## 7. Decisões próprias

1. **Cartões de número são links.** Cada indicador leva à tela onde se age sobre ele.
2. **Barra do funil proporcional à MAIOR etapa**, não ao total. Com proporção sobre o total, um
   funil equilibrado vira cinco barrinhas de 20% e não se lê nada.
3. **Moeda compacta** no cartão grande ("R$ 47,5 mil"), com o valor exato no `title`. "R$
   47.500,00" estoura o cartão em tela estreita.
4. **`aguardando resposta` e `follow-ups pendentes` em âmbar quando > 0.** São os dois números que
   pedem ação; leads e vendas são informativos.
5. **Atividade limitada a 8 linhas.** Cabe sem rolagem ao lado do funil.
6. **Link "Dashboard" no topo da navegação**, antes da caixa de entrada — é a tela de abrir o
   sistema para quem gerencia. A rota inicial continua sendo `/caixa`, que é a de quem atende.
7. **`grafico-linha` portado mesmo sem uso.** Instrução explícita do prompt, e o próximo bloco que
   ganhar o endpoint de série não precisa refazê-lo. Está documentado no cabeçalho do arquivo por
   que não está ligado.

---

## 8. Pendências

### Deste bloco

| Limite | Consequência |
|---|---|
| **Sem gráfico de evolução com dado real** | Falta o endpoint de série (§2.1). O componente está pronto e já roda no modo demonstração |
| **Atividade real só mostra conversas** | Vendas e lembretes concluídos precisam de API (§2.2) |
| **Modo demonstração é provisório** | Sai quando os endpoints da §2.1 e §2.2 existirem. Enquanto existir, é um caminho de tela que ninguém deve confundir com o real — daí a faixa de aviso e a rota própria |
| **Sem comparação com o mês anterior** | "12 vendas" não diz se é bom. Precisaria de agregação por período |
| **Sem seletor de período** | O dashboard é sempre "mês corrente", fixo no servidor |
| **Sem realtime** | Os números só mudam ao recarregar ou trocar de tela |
| Não renderizado em navegador | Payloads e lógica conferidos; pixels não |

### Carregadas dos blocos anteriores

- **Nenhum telefone pareado** (desde o bloco 3) — segue sendo o único item com risco em vez de
  volume.
- **Arrasto do kanban não testado em navegador** (bloco 8).
- **Nenhum envio de e-mail** (desde o bloco 1).
- **`ServicoCadastroEmpresa` sem controller** — não há como criar empresa pela API. Ficou visível
  neste bloco: para testar a empresa vazia, precisei inserir a linha direto no banco.
- **Sem tela de configuração** — etapa 10.
- **Sem lock distribuído** no agendador de follow-up.
- **Nenhum teste de frontend.**
- `senhas-dev.sql` na raiz, com senha em texto puro.

---

## 9. Estado das 13 telas

| Tela | Estado |
|---|---|
| Login · Aceitar convite · Redefinir senha | prontas |
| Caixa de entrada · Meu Dia · Equipe · Conexão | prontas |
| Funil · Contatos · Contato (detalhe) | prontas (bloco 8) |
| **Dashboard** | **pronta** |
| Minha conta | parcial — só troca de senha |
| Configurações | ausente — etapa 10 |

**12 de 13**, uma parcial, uma ausente.

---

## 10. O que falta para a fase 1 estar vendável

1. **Parear um telefone e mandar mensagem de verdade** — o único item com risco em vez de volume.
2. **Testar o arrasto do kanban num navegador** — baixo, mas é pré-requisito para confiar no funil.
3. **Onboarding e configurações** (etapa 10) — médio. Inclui expor o `ServicoCadastroEmpresa`.
4. **Envio de e-mail** — médio.
5. **Endpoint de série temporal** — baixo, e destrava o gráfico que já está pronto.
