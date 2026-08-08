# NEG-3 — Ciclo do cliente recorrente

**Migração:** `20260808145030_CanalDoCiclo` · aplicada em `nexora_dev` e `nexora_teste`
**Suítes:** 749 testes de integração ✅ · 318 do painel ✅ (1 vermelho pré-existente, ver Pendências)

---

## O problema

Cliente compra, a venda é concluída, e ele volta três semanas depois por outra campanha. Duas
coisas ficavam erradas, e as duas em silêncio:

**A conversa continuava atribuída ao vendedor de antes.** A mensagem nova não caía em "Não
atribuídas", quem estava disponível não a via, e o cliente ficava esperando alguém que talvez
nem estivesse de plantão. O NEG-1 deu ao produto um estado terminal por contato; faltava dizer
o que acontece *depois* dele.

**Não ficava registro de qual campanha trouxe a segunda compra.** A origem do contato não é
reescrita na volta — e isso está certo, a primeira origem é a verdadeira (NEG-1) —, mas a
segunda não era guardada em lugar nenhum. O relatório de origem só sabia responder "qual canal
traz *gente*", nunca "qual canal traz *dinheiro*", que é a pergunta que o dono realmente faz.

**Nenhuma tabela nova.** Duas colunas.

---

## Uma correção à premissa do prompt

O prompt previa que o INT-2 talvez não existisse, e mandava criar `vendas.canal_id` anulável e
**sem FK ativa** nesse caso.

Ele existe. `canais_captacao` está no ar — é a tabela dos canais de QR/link, mexida no bloco
anterior (a mensagem editável do link). Então `canal_id` nasceu com **FK de verdade**, e o
preenchimento entrou agora, não depois.

⚠️ **Formulário do site NÃO é canal.** São duas tabelas (`formularios_captura` e
`canais_captacao`), e `canal_id` referencia só a segunda. Venda originada de formulário fica com
`canal_id` nulo. Aceitável hoje — o formulário saiu da tela de Captação no bloco anterior — e
está registrado nas limitações do relatório.

---

## O que foi construído

### Banco — `20260808145030_CanalDoCiclo`

| Coluna | Tipo | FK |
|---|---|---|
| `conversas.canal_ciclo_id` | `bigint NULL` | → `canais_captacao(id)` `ON DELETE SET NULL` |
| `vendas.canal_id` | `bigint NULL` | → `canais_captacao(id)` `ON DELETE SET NULL` |

Mais `ix_vendas_canal (empresa_id, canal_id, fechada_em) WHERE canal_id IS NOT NULL` — parcial,
porque a maioria das vendas não tem canal e mantê-las no índice faria a varredura do mês ler
linhas que serão descartadas.

**As duas FKs são simples, e não compostas com `empresa_id`** como o resto de `conversas`. Duas
razões, e as duas são de banco e não de gosto:

- `ON DELETE SET NULL` **não existe** em FK composta cujo segundo membro é `NOT NULL` — o
  Postgres tentaria anular `empresa_id` junto;
- `Restrict` faria um canal recém-criado virar indeletável só porque alguém escaneou o QR e a
  conversa ainda carrega o código.

O recorte por empresa continua garantido **na origem**: quem grava `canal_ciclo_id` é
`CanalDoTextoAsync`, que filtra por `empresaId` explicitamente; e `MarcarGanhoAsync` valida o
canal informado com o filtro de tenant ligado antes de gravá-lo — sem essa validação, um id vindo
do corpo da requisição apontaria para o canal de outra empresa.

### O canal do ciclo — `ProcessadorEventoEvolution`

A detecção **subiu** de dentro de `CriarContatoAsync` para o fluxo da mensagem. Era ali que
morria: só acontecia no primeiro contato, então cliente que já existia escaneava o QR da campanha
nova e o código não ia para lugar nenhum.

Agora vale para contato novo **e** existente, e o destino é a **conversa**:

```
conversas.canal_ciclo_id ← o último código detectado em mensagem RECEBIDA,
                           desde a última venda concluída
```

Quatro decisões que os testes travam:

- **Só na entrada.** Código numa mensagem nossa é o vendedor mandando o próprio link, não o
  cliente chegando por ele;
- **O último do ciclo vence.** Dentro do mesmo ciclo, o código mais recente é o caminho mais
  recente que a pessoa percorreu;
- **Mensagem sem código não apaga nada.** Entre o "tenho interesse #k7m2" e o fechamento existem
  dez mensagens comuns, e qualquer uma delas zeraria o crédito;
- **`LeadsRecebidos` não sobe.** Aquele contador conta *lead* — gente que chegou pela primeira
  vez. Somá-lo na volta faria o cliente medir custo por lead com um denominador que inclui quem
  já era cliente.

⚠️ **`contatos.origem` continua intocada**, e há teste para isso desde o INT-2. As duas colunas
respondem perguntas diferentes: `contatos.origem` é "de onde essa pessoa veio"; `canal_ciclo_id`
é "por que ela está falando comigo agora".

### O canal da venda — `MarcarGanhoAsync(id, valor, canalId, ct)`

Precedência, do mais confiável para o menos:

1. `canalId` informado no fechamento — alguém olhou e confirmou;
2. `conversas.canal_ciclo_id` — o código detectado neste ciclo;
3. `NULL`.

**Nunca** o canal do cadastro original. Herdar dali faria o relatório creditar receita nova a
campanha velha — e a campanha velha sempre pareceria a melhor, porque acumularia todas as voltas
dos clientes que ela trouxe um dia.

### Concluir libera o ciclo — `LiberacaoDeCiclo`

Concluir a **última** venda em aberto de um contato: `responsavel_id`, `atribuido_em` e
`canal_ciclo_id` voltam a nulo.

⚠️ **`status` não é tocado.** Liberar responsável e resolver conversa são coisas diferentes:
resolver é decisão do atendente, e confundir as duas faria a conversa sumir da caixa de quem
ainda precisa dela — o vendedor descobriria pelo cliente reclamando.

⚠️ **Só libera sem venda em aberto.** Pedido entregue + pedido a caminho = atendimento em
andamento, e o dono continua.

**Um lugar só, e não três.** A venda conclui por três portas, e o plano previa só a primeira:

| porta | quando |
|---|---|
| `ServicoVendas.ConcluirAsync` | o botão, em lote |
| `ServicoContatos.MarcarGanhoAsync` | prazo zero — a venda nasce concluída (padaria, salão, balcão) |
| `ConclusaoAutomatica` | a rodada diária, todas as empresas |

Cobrir só a primeira teria deixado o recurso sem efeito justamente para quem mais tem cliente que
volta — e as duas automáticas são as que ninguém olha.

⚠️ **A rodada diária precisou de um segundo comando, e não de uma terceira CTE.** Todas as
instruções de um `WITH` enxergam o **mesmo snapshot**: a liberação veria as vendas que a CTE
acabou de concluir ainda como `'fechada'`, o `NOT EXISTS` nunca passaria, e nenhuma conversa
seria liberada — em silêncio. O comando passou a terminar em `SELECT contato_id`, e a CTE de
auditoria continua executando porque no Postgres CTE que escreve sempre roda, referenciada ou não.

### Relatório 3b — faturamento por campanha

`SqlVendasPorCanal`, endpoint `GET /api/relatorios/canais`, segunda tabela no mesmo cartão e um
segundo bloco no CSV.

**Tabela separada, e não colunas a mais.** São chaves e recortes diferentes:

| | agrupa por | recorta por |
|---|---|---|
| relatório 3 | `contatos.origem` (tipo de origem) | **criação** do lead |
| relatório 3b | campanha nomeada | **fechamento** da venda |

Alinhá-las lado a lado convidaria a dividir uma pela outra, e o número que sairia dali não
significaria nada.

`LEFT JOIN` e não `JOIN`: a venda sem canal aparece como uma linha própria. Ela é a maioria hoje,
e omiti-la faria a soma da tabela não bater com o faturamento do relatório 1 sem nada na tela
explicando a diferença. Há teste para isso (`Assert.Equal(2500m, linhas.Sum(...))`).

### Telas

**Modal de fechamento** — campo de campanha **opcional**, pré-preenchido com o detectado. É o
único ponto do produto onde alguém sabe de verdade por que o cliente voltou. Obrigatório viraria
"primeira opção da lista" em uma semana, que é pior que vazio.

O detectado chega numa requisição própria (`GET /api/contatos/{id}/canais-fechamento`), disparada
ao **abrir** o modal — o funil abre o mesmo modal a partir de um card, que não carrega o detalhe
do contato, e engordar o payload do kanban por um campo que só aparece num modal seria pagar em
toda rolagem do quadro. Falha em silêncio: derrubar o fechamento porque a lista de campanhas não
veio trocaria um campo a menos por uma venda não registrada.

⚠️ O pré-preenchimento **não atropela a escolha do vendedor**. O detectado chega depois da
abertura, então o padrão é aplicado por `effect` — e só enquanto o campo estiver vazio. Sem essa
guarda, uma resposta lenta trocaria a campanha que a pessoa acabou de escolher, sem nada na tela
denunciando. É o teste `o detectado atrasado NÃO atropela a escolha do vendedor`.

**Caixa de entrada** — faixa entre o cabeçalho e a thread para quem já comprou: "Cliente
recorrente · N compras · última em dd/mm" + **Abrir nova negociação**.

O botão chama o `reabrir` que **já existia** (NEG-1) — move para a primeira etapa, limpa o carimbo
e preserva o histórico de vendas. Nada de endpoint novo, que seria uma segunda porta para o mesmo
fato.

⚠️ **Mensagem de quem já comprou NÃO abre negociação sozinha.** "Obrigado, chegou tudo certo"
viraria oportunidade falsa e sujaria o funil. O vendedor decide, e há teste para isso.

`ConversaResumo` ganhou `contatoGanhou` — sai de `contatos.ganho_em`, que a projeção já lê pelo
mesmo join da etapa: nenhum custo a mais por linha. A **contagem** de compras não vem na lista de
propósito (exigiria um subselect em `vendas` por linha, em toda rolagem); ela é buscada só para a
conversa aberta.

**Detalhe do contato** — o botão "Reabrir negociação" passa a se chamar **"Abrir nova
negociação"** quando o contato tem venda fechada. Para quem comprou, "reabrir" descreve errado o
que vai acontecer: a negociação anterior terminou e esta é outra.

---

## Acrescentado depois: o atalho de registrar venda na caixa

O vendedor fecha a venda **na conversa** — é ali que o cliente diz "pode mandar". Sem atalho ele
sai da caixa, procura o contato ou o card, e no meio do caminho a venda simplesmente não é
registrada.

Botão **"Registrar venda"** no cabeçalho da conversa, abrindo o **mesmo** `app-modal-fechamento`
do funil e do detalhe, com o mesmo `marcarGanho` e o mesmo campo de campanha pré-preenchido. Uma
tela de venda própria aqui seria a terceira porta para o mesmo fato — e a que esqueceria o campo
de campanha na primeira mudança.

Duas guardas, e as duas são sobre não oferecer o que vai falhar:

- ⚠️ **só em atendimento.** Conversa sem dono não tem quem responda pela venda: primeiro alguém
  assume. Mesma linha de corte de "Liberar";
- ⚠️ **só para quem ainda não comprou.** Com `ganho_em` carimbado a API devolve 409, e o caminho
  certo é a faixa de cliente recorrente logo acima — "Abrir nova negociação" limpa o carimbo e aí
  o botão aparece. Um botão que sempre erra é pior que botão nenhum.

Depois de registrar, a caixa recarrega **do servidor**: a etapa mudou, `contatoGanhou` mudou, e
com prazo zero a venda já nasce concluída e a conversa volta para a fila — nada disso dá para
adivinhar no cliente.

**Mutação:** tirar a guarda do `contatoGanhou` derruba
`NÃO aparece para quem já tem venda fechada`.

---

## Verificação

Escritos **antes** da implementação, em `VendasDbTests`, `CanaisDbTests` e `RelatoriosDbTests`:

- concluir a última venda em aberto **libera** `responsavel_id` e `atribuido_em`;
- concluir com outra em aberto **não** libera;
- liberar **não** muda o `Status` nem grava `ResolvidoEm`;
- a venda registra o canal do **ciclo**, não o do cadastro;
- sem canal identificado, a venda fecha com `canal_id` NULL;
- o canal informado no fechamento **ganha** do detectado;
- concluir **limpa** `canal_ciclo_id` — mas a venda guardou;
- código detectado em **contato existente** vai para a conversa, e `contatos.origem` fica intocada;
- contato **novo** também nasce com o canal na conversa (compra no mesmo dia);
- código em mensagem **nossa** não vira canal do ciclo;
- o **último** código do ciclo substitui o anterior;
- mensagem **sem** código não apaga o canal;
- mensagem em conversa liberada **não** atribui responsável;
- vendas por canal soma por campanha e mostra a linha sem canal;
- venda cancelada não entra no relatório por canal.

No painel, `modal-fechamento.spec.ts` (8 casos) cobre o pré-preenchimento, a não-sobrescrita, o
canal no resultado, a ausência do seletor sem campanhas e o rótulo de campanha encerrada.

### Provas por mutação

| mutação | teste que morreu |
|---|---|
| liberar o responsável sem checar venda em aberto | `COM_OUTRA_VENDA_EM_ABERTO_o_responsavel_NAO_e_liberado` |
| `MarcarGanho` herdar o canal do cadastro do contato | `A_VENDA_REGISTRA_O_CANAL_DO_CICLO_NAO_O_DO_CADASTRO` e `CONCLUIR_LIMPA_O_CANAL_DO_CICLO` |

---

## Corrigido depois, no mesmo assunto

### A coluna "Responsável" da lista de contatos vinha vazia

`contatos.responsavel_id` e `conversas.responsavel_id` são a mesma ideia em dois lugares, e só a
segunda era escrita pelo fluxo vivo: quem digita o contato à mão preenche a primeira pelo
formulário, e quem chega pelo WhatsApp — todo lead de verdade — não preenchia nenhuma, porque a
única atribuição que acontece é o "Assumir" da caixa.

Medido: na empresa de trabalho, **zero** contatos com responsável e **oito** conversas com
responsável. Nas empresas de demonstração o oposto — 400 contatos e zero conversas —, porque lá
quem escreve é o semeador. Nenhuma metade estava completa, e por isso nada denunciava.

⚠️ **Não era só a coluna da tabela.** Leem `contatos.responsavel_id`: a lista de contatos, o card
do kanban, o filtro "por responsável" e o `Meu Dia`. Quatro telas dizendo "sem responsável" para
lead com dono havia semanas.

O próprio semeador já documentava a invariante que faltava: ele copia
`conversa.ResponsavelId = contato.ResponsavelId`.

Agora assumir preenche as duas, liberar solta as duas, e a liberação de ciclo do NEG-3 também.
Em todos os casos **só mexe no que é de quem está agindo**: um gestor pode ter atribuído o
contato pelo formulário, e assumir a conversa é dizer "eu atendo", não "o lead virou meu".

⚠️ **Isto ampliou o NEG-3**: concluir a última venda agora solta o lead, não só a conversa.
Deixar de fora criaria a mesma incoerência de cabeça para baixo — a caixa mostrando "Não
atribuídas" e a lista de contatos ainda no nome do vendedor de antes.

8 linhas corrigidas em `nexora_dev`; zero divergentes depois.

### A campanha do ciclo era gravada e invisível

Relatado assim: *"abri um novo lead depois de concluir uma venda, mandei 'Olá! tenho interesse
#bmvb', não registrou para qual campanha"*.

O banco estava certo — a trilha mostra a mensagem às 13:03 gravando `canal_ciclo_id`, e a nova
negociação às 13:05. O que faltava era **tela**: a campanha só apareceria no seletor do modal de
fechamento e, depois da venda, no relatório. Durante toda a negociação nada dizia que existia.

⚠️ **Gravar sem mostrar não é registrar.** O usuário fez o fluxo inteiro certo e concluiu, pelo
que via, que nada tinha sido guardado — e a conclusão era razoável.

Agora a campanha do ciclo aparece em dois lugares:

- **caixa de entrada** — selo "via Balcão TV" ao lado do da etapa, no cabeçalho da conversa;
- **detalhe do contato** — "Voltou pela campanha X. É ela que vai ser creditada quando a venda
  fechar.", ao lado da origem do cadastro.

As duas na tela ao mesmo tempo, com rótulos diferentes de propósito: "Origem" é a campanha que
trouxe a pessoa da primeira vez e não se reescreve; a outra é a que a trouxe de volta. Sem as
duas visíveis, quem lê acha que uma delas está errada.

### A campanha não aparecia no dashboard nem no funil

Relatado assim: *"no dashboard não aparece, relatório, funil"* e, em seguida, *"de onde vêm seus
leads não aparece"*.

A rosca **De onde vêm seus leads** agrupava só pelo ENUM de origem. O nome da campanha que
capturou o lead está em `contatos.origem_detalhe` desde o INT-2 — e o gráfico o jogava fora: o
dono criava "Promoção de Julho", imprimia o QR, recebia o lead, e o painel dizia "Instagram".

Três acréscimos, todos com o mesmo princípio: **dado gravado que a tela descarta é o mesmo que
dado não gravado.**

| onde | o quê |
|---|---|
| rosca do dashboard | a fatia continua sendo a ORIGEM; a campanha vira sub-linha recuada na legenda |
| dashboard | bloco novo **"Campanhas que mais venderam"** — as três primeiras por receita do mês |
| card do kanban | selo `via Balcão TV` abaixo do telefone |

#### A campanha NÃO é uma origem — corrigido depois de errar

A primeira versão trocou o rótulo da fatia pelo nome da campanha. Está errado, e a pergunta do
usuário é que expôs: *"Promoção de Julho é campanha que tem Origem Instagram, teve um link de
WhatsApp distribuído pelo Instagram; de onde é a origem?"*

**A origem é Instagram.** A campanha é a peça dentro dela — e o modelo sempre disse isso:
`canais_captacao.origem` é escolhida ao criar o canal, e `CriarContatoAsync` faz
`Origem = canal.Origem` com o nome do canal indo para `origem_detalhe`. Hierarquia, não
alternativas:

```
Instagram                 ← de ONDE veio (a mídia onde o link circulou)
└── Promoção de Julho     ← por QUAL peça (o canal de captação)
```

Promover a campanha a fatia própria **achata a hierarquia**: com duas campanhas no Instagram
apareceriam duas fatias, e o "quanto o Instagram me traz" — a pergunta que uma rosca de origens
existe para responder — sumiria da tela.

A fatia voltou a ser a origem; a campanha desceu para sub-linha recuada na legenda, onde detalha
sem competir. O servidor continua devolvendo uma linha por `(origem, campanha)` — é essa
granularidade que permite as duas leituras —, e a soma por origem acontece no cliente sobre
algumas dezenas de linhas já agregadas no banco.

⚠️ **A soma das campanhas pode ser menor que o total da origem**, e é correto: lead que chegou sem
código conta na origem e em campanha nenhuma. Não há sub-linha "(sem campanha)" — a diferença
entre os dois números já diz.

⚠️ A consulta do ranking agrupa por **`canal_id`, e não por `Canal.Nome`**: agrupar pela navegação
não traduz — o EF precisaria juntar `canais_captacao`, que tem query filter de tenant, dentro da
chave do `GROUP BY`, e desiste com *"could not be translated"*. O nome vem numa segunda leitura de
no máximo três linhas, depois do `Take`.

⚠️ **A venda sem campanha não entra no bloco do dashboard.** Ali ela seria quase sempre a maior
barra e empurraria as campanhas de verdade para fora das três. O total honesto, com a fatia sem
atribuição, está no relatório 3b.

### A caixa dizia "Venda" depois do pedido entregue

Relatado assim: *"a venda está concluída e está mostrando a etiqueta Venda na caixa de entrada?"*.

Estava. A caixa mostrava `etapaNome` cru. Para quem comprou e **recebeu**, isso dizia "Venda" —
apontando para uma coluna do funil onde o card **não está**: o NEG-2 tira da coluna Venda quem não
tem pedido em aberto, senão ela acumula para sempre.

Duas telas do mesmo produto discordando sobre o mesmo contato, e a caixa era a que mentia: "Venda"
lê como negócio acontecendo, e o vendedor abriria a conversa esperando um pedido a caminho.

`ConversaResumo` ganhou `VendasEmAberto`, com o **mesmo predicado do kanban**
(`RegrasContato.ComVendaEmAberto`), para as duas telas não divergirem de novo. A etiqueta passa a
ser "Pedido concluído" quando o contato comprou e não há mais nada em aberto.

⚠️ **Não é `contatoGanhou` sozinho.** Quem comprou e tem OUTRO pedido a caminho continua sendo
"Venda" — é exatamente a situação que mantém o card na coluna do kanban.

⚠️ A projeção usa a **navegação** `c.Contato.Vendas.Count(...)`, e não `db.Vendas.Count(...)`: a
expressão é `static readonly` (uma só para a lista e para a busca por id) e um campo do construtor
primário não pode ser citado dentro dela — CS9105. O SQL gerado é o mesmo subselect correlacionado.

### Seção vazia não pode sumir

A tabela "Faturamento por campanha" do relatório 3b vivia dentro de um `@if` sem `@else`: sem
dados, sumia. Uma seção que desaparece quando está vazia é indistinguível de uma seção que não foi
construída — foi exatamente assim que ela pareceu não existir. Agora o título fica e o corpo
explica o vazio. O mesmo vale para o bloco novo do dashboard.

### O `<select>` de etapa mentia

Dizia "Novo Lead" para todo contato que não estivesse na primeira etapa. `[value]` num `<select>`
é aplicado quando o contato chega, e as `<option>` vêm de outra requisição — um select sem a
opção correspondente descarta o valor em silêncio e passa a exibir a primeira. Quando as opções
chegam, a ligação não roda de novo.

Não era cosmético: é o controle que **move** o contato de etapa. Trocado por `[ngModel]` +
`[ngValue]`, que é para isso que o `SelectControlValueAccessor` existe.

⚠️ Os três filtros de `contatos.html` usam o mesmo `[value]` em select e **não** estão quebrados
hoje, porque o valor inicial (`''`) casa com a `<option value="">` estática, que já está no DOM.
Quebrariam no dia em que alguém restaurar um filtro pela URL.

---

## Pendências registradas

### A tabela de oportunidades — terceira aparição

Duas coisas que este bloco **não** resolve, e não resolve pela mesma razão:

**Ciclo que não converteu fica sem registro.** Cliente voltou pela campanha Verão, negociou e não
comprou: não há linha para isso, porque só a venda guarda o canal. O relatório 3b mede o que a
campanha *fechou*, nunca o que ela *movimentou*.

**O funil continua contando contatos, não negociações.** Contato com duas negociações abertas
aparece uma vez, na etapa da mais recente.

Os dois exigem uma tabela de **oportunidades** — uma linha por ciclo, com canal, responsável,
etapa e desfecho próprios. `contatos` deixaria de ser lead-e-negócio ao mesmo tempo.

⚠️ **É a terceira vez que ela aparece**: no NEG-1 (o carimbo terminal que apagava a venda
anterior), no pós-venda, e agora. Não foi feita ainda porque o custo é alto — reescreve funil,
kanban, dashboard e quatro dos sete relatórios — e o produto ainda funciona sem ela.

**O sinal de que chegou a hora será o cliente pedir duas negociações abertas ao mesmo tempo para
a mesma pessoa.** Enquanto o pedido for "quero registrar que ele voltou", o desenho atual serve.
Quando for "quero as duas na tela ao mesmo tempo", não serve mais, e nenhum remendo vai resolver.

### Conhecidas, não resolvidas

- **Venda de formulário do site fica sem canal.** `formularios_captura` é outra tabela e
  `vendas.canal_id` não a referencia. Como o formulário saiu da tela de Captação no bloco
  anterior, o caso tende a zero — mas quem tem um formulário antigo colado num site continua
  recebendo lead, e a venda dele entra em "sem campanha identificada".

- **`conversas.canal_ciclo_id` não tem índice próprio.** Apagar um canal faz um seq scan em
  `conversas` pelo `ON DELETE SET NULL`. Aceitável: a remoção só é permitida com zero leads e é
  rara. Se um dia a regra de remoção afrouxar, o índice entra junto.

- **`contatos.responsavel_id` e `conversas.responsavel_id` continuam sendo DUAS colunas.** Elas
  passaram a andar juntas (assumir preenche as duas, liberar e concluir soltam as duas), mas a
  sincronia é mantida por código, não pelo banco. Uma coluna só — ou a tabela de oportunidades —
  tiraria a chance de divergirem de novo.

- **`lateral.spec.ts` — rodapé com 78px contra um teto de 64px.** Único vermelho do painel,
  anterior a este bloco e sem relação com ele. Continua registrado.

- **`StatusConversa.Resolvida` segue decorativa.** Nenhum serviço a escreve — só o seeder. O
  filtro "Resolvidas" da caixa existe e nunca tem o que mostrar. Este bloco reforçou a decisão de
  **não** usar a conclusão da venda para resolver conversa; falta a ação explícita do atendente,
  que é outro bloco.
