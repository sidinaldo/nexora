# NEG-3 — Ciclo do cliente recorrente

**Migração:** `20260808145030_CanalDoCiclo` · aplicada em `nexora_dev` e `nexora_teste`
**Suítes:** 736 testes de integração ✅ · 305 do painel ✅ (1 vermelho pré-existente, ver Pendências)

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

- **`lateral.spec.ts` — rodapé com 78px contra um teto de 64px.** Único vermelho do painel,
  anterior a este bloco e sem relação com ele. Continua registrado.

- **`StatusConversa.Resolvida` segue decorativa.** Nenhum serviço a escreve — só o seeder. O
  filtro "Resolvidas" da caixa existe e nunca tem o que mostrar. Este bloco reforçou a decisão de
  **não** usar a conclusão da venda para resolver conversa; falta a ação explícita do atendente,
  que é outro bloco.
