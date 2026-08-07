# NEG-1 — Histórico de vendas

## A divisão que o bloco introduz

| | Responde | Onde vive |
|---|---|---|
| **Carimbo** | em que estado o contato está agora | `contatos.ganho_em`, `contatos.valor` |
| **Histórico** | o que já aconteceu | tabela `vendas` |

A coluna continua existindo, e continua sendo o que o kanban lê para saber que o card está na
etapa de ganho. Ela deixou de ser a fonte da verdade para faturamento e contagem.

Reabrir limpa o carimbo. As linhas ficam.

---

## O teste que prova o bloco

Escrito **antes** da tabela existir, conforme o critério, e por isso não compilava — `db.Vendas`,
`amb.Vendas` e `Venda` não existiam ainda:

```csharp
await amb.Contatos.MarcarGanhoAsync(joao.Id, 5000m, default);   // março
await amb.Contatos.ReabrirAsync(joao.Id, default);              // ele volta
await amb.Contatos.MarcarGanhoAsync(joao.Id, 3000m, default);   // julho

Assert.Equal(2, painel.VendasDoMes);
Assert.Equal(8000m, painel.FaturamentoDoMes);   // não 3.000
```

**Provado por mutação:** revertido o dashboard para contar por `contatos.ganho_em`, **dois testes
falham** — este e `Mes_fechado_NAO_muda_depois_de_reabrir_um_contato_daquele_mes`. São exatamente
os dois que descrevem o defeito.

---

## Decisão sobre editar o valor

**Não existe editar. Existe cancelar e registrar de novo.**

O prompt deixava escolher entre editar registrando quem editou, ou cancelar e criar. A segunda,
por uma razão só: **relatório de mês fechado não pode mudar.**

Editar `valor` in place altera retroativamente o faturamento de um mês que o dono já conferiu, e
não deixa como saber o que ele viu antes. Um campo `editado_por` registra quem, não *quanto era*.
Cancelar e registrar de novo mantém as duas linhas: a de 5.000 riscada e a de 3.000 valendo — e o
mês fechado continua explicável, porque a soma antiga ainda pode ser reconstruída.

O custo é um clique a mais para corrigir um dígito. É o preço de o número do mês passado ser
estável, e é barato.

**`cancelada_em`, nunca `DELETE`.** Faturamento que some sem rastro é pior que faturamento errado:
o primeiro não tem investigação possível. Cancelar exige **dono ou gestor** — a checagem está no
serviço, não num `[Authorize(Roles=...)]`, porque é regra de negócio e precisa valer também quando
outro código chamar o serviço sem passar por HTTP.

**Cancelar a venda vigente limpa o carimbo** e devolve o card ao quadro — senão ele ficaria na
etapa de ganho sem venda nenhuma por trás. Cancelar uma venda antiga não toca em nada.

### Um defeito que o teste encontrou

A primeira versão identificava a venda vigente por `contato.ganho_em == venda.fechada_em`. O teste
`Cancelar_uma_venda_ANTIGA_nao_mexe_no_carimbo_da_vigente` derrubou: com o relógio controlado — e
em produção sempre que duas chamadas caem no mesmo tick — as duas vendas têm o mesmo instante,
casavam as duas, e cancelar a antiga limpava o carimbo da nova.

**Timestamp não é chave.** Passou a ser `ORDER BY fechada_em DESC, id DESC` entre as não
canceladas: o `id` é monotônico e desempata pelo que foi gravado depois.

---

## Resultado da migração

Rodada em `nexora_dev`, com dado real:

| | |
|---|---|
| contatos com `ganho_em` | **201** |
| linhas geradas em `vendas` | **201** |
| ganhos sem linha depois | **0** |
| **sem valor** (o caso do `COALESCE`) | **0** |
| total antes (soma de `contatos.valor`) | **994.974,00** |
| total depois (soma de `vendas.valor`) | **994.974,00** |

Rodado o `INSERT` uma segunda vez: **`INSERT 0 0`**, total inalterado. O `NOT EXISTS` torna a
migração idempotente, o que importa em restauração de backup — sem ele o faturamento dobraria.

Sobre o `COALESCE(valor, 0.01)`: existe ganho antigo sem valor, porque o campo era opcional. O
CHECK `ck_vendas_valor` exige `> 0`, e as alternativas eram afrouxar o CHECK (aceitando zero para
sempre, em toda venda nova) ou descartar essas linhas (perdendo a **contagem** de vendas). Um
centavo preserva a contagem, que é o que aquele registro sempre teve, sem inventar faturamento que
ninguém digitou. Nesta base o caso não apareceu; em outra pode aparecer, e o número está no log.

---

## O que mais precisou mudar

**Os semeadores.** Eles carimbam `ganho_em` em lote com `ExecuteUpdate`, por fora do
`MarcarGanhoAsync` — e sem linha em `vendas` o tenant de demonstração abriria com **faturamento
zero**, que é o oposto do que ele existe para mostrar. Cinco testes pegaram isso.

A correção foi `ReconciliadorVendas.SincronizarAsync`: o mesmo `INSERT ... NOT EXISTS` do backfill,
disponível para quem escreve em lote. Não é uma segunda porta de gravação — é a reconciliação de
uma escrita que já aconteceu, e o preço de `ganho_em` continuar existindo como carimbo. Código novo
que fecha venda chama o **serviço**.

**A ordem de exclusão.** A FK `vendas → contatos` é `Restrict` de propósito: apagar contato não
pode levar faturamento junto sem alguém decidir. Os semeadores apagam contatos ao re-semear, e
passaram a apagar `vendas` antes — outros cinco testes pegaram isso, o que é o comportamento certo
do `Restrict`.

**A série temporal** passou a agrupar por `vendas.fechada_em`. Era `contatos.ganho_em`, e reabrir
um card apagava o ponto do mês passado do gráfico. Série histórica que muda para trás não é série
histórica.

**O funil do kanban NÃO mudou** — ele conta pela etapa do contato, e isso é correto: mostra onde os
cards estão agora. Os dois números convivem na mesma tela com rótulos de período distintos ("agora"
no funil, "no mês" no cartão de vendas), como o DES-1 pediu.

---

## Telas

- **Seção "Vendas"** no detalhe do contato: valor, data, quem fechou, e "Cancelar" para dono e
  gestor. Cabeçalho com o rótulo *"histórico · não muda quando você reabre"*.
- **Cancelada aparece riscada**, com selo e `opacity`, **não some**: quem confere o mês depois
  precisa ver que existiu uma venda de 5.000 e que ela foi desfeita.
- **"Cliente recorrente"** junto do botão de reabrir — que é onde a negociação recomeça, e onde a
  informação muda o que o vendedor vai dizer. Conta só as não canceladas.

---

## Verificação

- `dotnet build -warnaserror` — **0 avisos**
- `dotnet test` — **608 passando** (11 novos)
- frontend — **237 passando**

| teste | o que fixa |
|---|---|
| **compra, reabre, compra soma as duas** | o bloco inteiro |
| mês fechado não muda depois de reabrir | o sintoma que o dono sentia |
| reabrir não apaga a linha | carimbo e histórico são coisas diferentes |
| ganho grava coluna **e** linha | o mesmo instante liga as duas |
| valor inválido não deixa nem carimbo nem linha | recusa antes de escrever |
| linha e carimbo caem juntos quando o banco recusa | atomicidade real, forçada por `ck_vendas_valor` |
| cancelar marca e sai da contagem | sem `DELETE` |
| cancelar a vigente limpa o carimbo | o card não fica em ganho sem venda |
| cancelar a antiga não mexe na vigente | achou o defeito do timestamp como chave |
| vendedor não cancela, gestor cancela | a asserção dupla evita uma regra que recusa todo mundo |
| query filter isola `vendas` | e cancelar venda de outro tenant não encontra a linha |

Tudo agregado no SQL: `COUNT`/`SUM` no banco, faixa semi-aberta (`>= inicio`), sem função sobre
coluna no filtro, e o predicado `cancelada_em IS NULL` é o mesmo do índice parcial
`ix_vendas_periodo`.

**Não coberto por teste automatizado:** a seção "Vendas" na tela só passa pelo *smoke test* de
renderização — não há asserção de que a cancelada aparece riscada nem de que o aviso de recorrente
some quando todas foram canceladas. A lógica está num `computed` puro e seria barato testar; ficou
de fora.

---

## Pendência: a tabela `negocios`

Esta tabela **não** resolve dois negócios abertos ao mesmo tempo para o mesmo contato, com etapas
independentes — o cliente que está negociando a reforma da cozinha e o orçamento do banheiro ao
mesmo tempo continua sendo um card só.

Esse é o modelo correto a longo prazo, e ele muda funil, dashboard, Meu Dia e todas as telas de
contato. Não é um desvio do que foi feito aqui: `vendas` é o **embrião** dele. Quando `negocios` se
justificar, cada linha de `vendas` já é um negócio ganho, com valor, data, responsável e etapa — o
que falta é o negócio *aberto*.

O gatilho para encarar isso é o cliente pedir para acompanhar duas propostas do mesmo contato
separadamente. Antes disso, `negocios` custa muito mais do que entrega.
