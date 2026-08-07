# NEG-2 — Conclusão de venda

**Escopo entregue:** os estados `concluida` e `cancelada` da venda, com a coluna Venda do kanban
deixando de acumular. **Devolução ficou de fora, a pedido — V2.**

---

## O problema

A coluna "Venda" do kanban acumulava para sempre: contato que comprou em março continuava lá em
dezembro. Depois de um ano são centenas de cards e a coluna deixa de informar. O mesmo defeito
achatava o gráfico de funil do dashboard — a barra "Venda" só crescia e virava a maior por
definição.

Faltava dizer **"esse pedido acabou"**. O estado foi para a **venda**, não para o contato: um
contato pode ter três compras, uma entregue, uma a caminho e uma com pendência.

---

## A semântica

| | O que significa | Efeito no relatório |
|---|---|---|
| **Fechada** | está na coluna Venda | conta |
| **Concluída** | o pedido acabou; sai do quadro | **continua contando** |
| **Cancelada** | nunca deveria ter sido registrada | **sai retroativamente** |

**Concluir e cancelar mexem no mesmo lugar do modelo e têm efeitos opostos no relatório.** Se
concluir tirasse faturamento, ninguém concluiria — e a coluna voltaria a acumular em três meses,
com o bloco não tendo resolvido nada.

`Faturamento do mês` = `SUM(valor)` de vendas com `status <> 'cancelada'` e `fechada_em` no mês.
É o mesmo predicado do índice parcial `ix_vendas_periodo`.

### O que ficou para a V2

**Devolução** — "a venda aconteceu e voltou" — é um terceiro caso e não cabe em nenhum dos dois:
ela precisa preservar o mês em que a venda fechou e descontar no mês em que ocorreu. Forçá-la
dentro de `cancelada` faria o faturamento de um mês já fechado mudar sozinho, que é exatamente o
problema que o NEG-1 existiu para resolver. Está registrado no comentário de `StatusVenda`, para
quem for implementá-la não colapsar as duas coisas por engano.

---

## O que foi construído

### Banco — `20260807165120_ConclusaoVenda`

- Enum nativo `status_venda_enum { fechada, concluida, cancelada }`, registrado nos **dois**
  lugares (`HasPostgresEnum` no `OnModelCreating` e `MapEnum` no `ServicosInfra`). Faltando o
  segundo, a aplicação compila, sobe e estoura na primeira consulta.
- `vendas.status`, `vendas.concluida_em`, `vendas.concluida_por`. `cancelada_em`/`cancelada_por`
  permanecem.
- `empresas.dias_para_concluir_venda` (`smallint`, padrão **7**, `CHECK BETWEEN 0 AND 90`).
  **Zero = concluir na hora**, e é valor legítimo — padaria, salão, balcão.
- `ix_vendas_periodo`: filtro passou de `cancelada_em IS NULL` para `status <> 'cancelada'`, o que
  **mantém** `concluida` no índice.
- `ix_vendas_contato_status`: a coluna do kanban pergunta "este contato tem venda em aberto?" a
  cada card.

**Resultado do backfill:** `nexora_dev` — 203 linhas viraram `fechada`, 0 viraram `cancelada`
(não havia venda cancelada no ambiente). `nexora_teste` — tabela vazia. Aplicado nos **dois**
bancos.

### Serviço — `ServicoVendas`

- `ConcluirAsync(IReadOnlyList<long> vendaIds)` — **em lote desde o começo**; um id é lista de um.
  Um `ExecuteUpdate` só, com `Status == Fechada` no `WHERE` (não numa checagem antes), o que o
  torna idempotente e seguro contra corrida. Devolve quantas de fato mudaram.
- `ConcluirDoContatoAsync(IReadOnlyList<long> contatoIds)` — o mesmo, dito pelo contato: é o que o
  card do kanban tem em mãos. Lê os ids e **delega**, em vez de repetir o `UPDATE` com outro
  predicado.
- **Qualquer papel** conclui; só dono e gestor cancelam. Concluir é ação operacional do vendedor
  sobre o próprio pedido; cancelar tira dinheiro do relatório.
- **Não toca o contato.** `ganho_em` e `valor` ficam como estão — limpá-los faria o kanban devolver
  o card para "Novo Lead", que é o oposto de "acabou".
- Trilha (AUD-1): `AcaoAuditoria.Concluiu` por venda. `ExecuteUpdate` não passa pelo interceptor,
  então o `SaveChanges` seguinte é o que grava os eventos declarados.

### Conclusão automática — `ConclusaoAutomatica`

Um `UPDATE` único, sem tenant, com o prazo de **cada** empresa vindo do join — varrer empresa a
empresa seriam N consultas para o mesmo conjunto. A trilha sai na mesma ida ao banco, por CTE.

`concluida_por = NULL` e `ator = 'Sistema'`: ninguém clicou. Carimbar um usuário produziria
autoria falsa.

Roda na **rodada diária que já existe** (`AgendadorFollowUp`), depois do follow-up e do expurgo —
nenhum `BackgroundService` novo. Um serviço próprio teria de reimplementar as proteções que já
existem ali (o catch que não deixa exceção subir, o log protegido, o fuso de negócio).

`dias = 0` conclui **no `MarcarGanhoAsync`**, não só no job: senão o card ficaria na coluna até as
8h do dia seguinte.

### Kanban e dashboard

- A etapa de ganho conta só contatos **com venda em aberto** — `RegrasContato.ComVendaEmAberto`, a
  mesma expressão nos dois lugares (kanban e dashboard), pela mesma razão que `NoQuadro` existe:
  escrita por extenso em dois lugares, ela já divergiu uma vez.
- `ColunaAsync` aplica o filtro também, e não só o `QuadroAsync`: é a chamada que a paginação usa
  direto, e sem isso rolar a coluna traria de volta os cards que o cabeçalho já não conta.
- Cabeçalho da coluna de ganho: **"41 concluídas"** abaixo do total. Sem esse segundo número, a
  coluna esvaziando pareceria perda de dado e o vendedor deixaria de concluir.
- `ContatoCard.vendasEmAberto`: o card mostra "2 vendas" quando o contato tem mais de uma.
- **Rótulo do dashboard** corrigido: `situação agora · a etapa de venda é o acumulado` →
  `situação agora · igual ao quadro`. A primeira metade estava falsa desde o NEG-1.

### Telas

- **Funil:** botão "Concluir" no card da coluna de ganho (espelhando "Registrar venda" nas outras)
  + caixa de seleção e barra de lote na coluna.
- **Contato:** cada venda com seu estado. Concluída aparece **marcada, não riscada** — riscar as
  duas do mesmo jeito faria o vendedor achar que concluir apaga a venda. Concluir não pede
  `confirm`; cancelar continua pedindo.
- **Configurações:** campo "Concluir a venda sozinha após", com o texto do que o número significa
  ("conclui assim que a venda é registrada" quando é zero).

---

## Verificação

`dotnet build` limpo · `dotnet test` **668 passando, 0 falhando** · `ng build` limpo ·
`ng test` **269 passando**.

### Provas por mutação

| Mutação | Resultado |
|---|---|
| `ConcluirAsync` escreve em `cancelada_em` (concluir = cancelar) | **4 testes caem**, entre eles o central |
| `ConclusaoAutomatica` ignora `dias_para_concluir_venda` | `A_RODADA_DIARIA_…` cai |
| `alternarSelecao` sem `stopPropagation` | o teste da caixa de seleção cai |

### Dois defeitos encontrados por teste, não por leitura

1. **A consulta de devoluções não tinha teto** (na versão com devolução, antes de ela ser
   removida): `devolvida_em >= inicioDoMes` sem limite superior fazia devolução de qualquer mês
   futuro descontar do mês corrente — um mês com 900 zerava. Encontrado por
   `DEVOLUCAO_EM_OUTRO_MES_NAO_TOCA_O_MES_DA_VENDA`.

2. **`AddColumn` com `defaultValue: 0` não funciona em enum nativo.** O Postgres recusa: "coluna
   status é do tipo status_venda_enum mas expressão padrão é do tipo integer". Enum nativo aceita
   o rótulo, não o ordinal. A coluna virou SQL cru, com `DROP DEFAULT` logo depois — e o efeito
   colateral é desejado: todo `INSERT` cru em `vendas` passa a ter que dizer em que estado a venda
   nasce (foi o que pegou o `ReconciliadorVendas`).

---

## Pendências registradas

### Corrigida de passagem: a migração `DuracaoAudio` era invisível ao EF

`20260807140000_DuracaoAudio` foi escrita à mão no bloco 13, **sem `Designer.cs`** — e é ele que
carrega o atributo `[Migration]`. Sem o atributo o EF não a enxerga: `database update DuracaoAudio`
responde "não encontrada", e **num banco criado do zero ela nunca rodaria**. O snapshot tinha a
coluna `mensagens.midia_duracao_segundos`; o SQL nunca a criaria.

A criação passou para esta migração, condicional (`ADD COLUMN IF NOT EXISTS`): banco novo ganha a
coluna, banco existente segue em frente.

### Conhecidas, não resolvidas

- **O funil conta CONTATOS, não vendas.** Contato com duas em aberto conta uma no gráfico do
  dashboard. O card mostra "2 vendas", mas a barra não. É a mesma raiz da pendência `negocios` do
  NEG-1, e a resolução das duas é a mesma.
- **Não existe tela de relatórios.** O item §7 do prompt não tem onde morar — `relatórios` é o item
  14 do Bloco G do `PROGRESSO.md`, não construído. Os números ficam no dashboard e na série.
- **`lateral.spec.ts` falha, e a falha é ANTERIOR a este bloco** — confirmado com `git stash`. O
  rodapé da barra lateral está com 78px onde o teste exige no máximo 64 ("voltou a empilhar?").
  Provável regressão dos ajustes de CSS de sessões anteriores. Não foi tocada aqui.
- **A rodada diária não tem lock distribuído** (herdado do `AgendadorFollowUp`): com duas
  instâncias, a conclusão automática roda duas vezes. O `status = 'fechada'` no `WHERE` torna a
  segunda inofensiva — ela afeta zero linhas —, então este job especificamente é seguro.
