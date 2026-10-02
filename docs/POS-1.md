# POS-1 — Etapas depois da venda, e o dashboard que mentia

> Branch `pos-1-etapas-pos-venda`. Sete commits, cada um verde e revertível sozinho.

## O que foi pedido

Dois defeitos vistos em tela no mesmo dia, 2026-10-02:

**1.** *"vendas é o ganho, mas preciso incluir etapas depois dela — pós-venda, entregue. Hoje está
preso a vendas e não muda. Não pode voltar uma etapa, mas avançar pode."*

**2.** *"a segunda imagem é um bug: quando chega a primeira mensagem ainda acusa que falta um
contato."*

## As decisões, e quem as tomou

| | Escolhido | Quem |
|---|---|---|
| Conclusão automática | Só vale **enquanto o card está na etapa de venda** | o dono |
| Liga/desliga | **Interruptor novo** nas Configurações | o dono |
| Voltar etapa | **Só o card vendido** é proibido. Em negociação continua livre nos dois sentidos | o dono, perguntado |
| Fim do pedido | **Só o botão "Concluir"**. A última etapa não conclui sozinha | o dono, perguntado |
| Onde mora a regra | `RegrasDoQuadro`, irmão de `RegrasNegociacao` | desenho |
| Código HTTP das recusas | 409, igual às que já existiam | desenho |

As duas perguntas que eu fiz antes de desenhar valeram: a primeira porque *"não volta uma etapa"*
podia querer dizer "nenhum card volta", o que transformaria um arrasto errado em chamado de suporte;
a segunda porque "só o botão Concluir" tem um preço que precisava ser dito na tela (ver R1).

---

## O que eu errei, e como descobri

Vai primeiro porque é a parte útil deste documento.

**1. Eu quase entreguei um card que desaparece.** O plano dizia que o bloqueio era o guarda do
arrasto. Era, mas não só: `ServicoFunil` recorta cada coluna pelo `EGanho` da etapa, e um negócio
`ganha` numa etapa de pós-venda (que não é `EGanho`) **não casava com nenhum dos dois ramos**. Tirar
só o guarda faria o arrasto devolver 200 e o card sumir — cabeçalho 0, lista vazia, depois de o
otimista já ter pintado ele lá. É por isso que o recorte (A2) vem **antes** de liberar o movimento
(A3) na ordem de trabalho.

**2. Eu contei quatro cópias do recorte. São cinco.** A quinta é a "foto" do relatório 4
(`ServicoRelatorios.SqlFunilAgora`), e é a única escrita em **SQL cru** — ela não quebra quando as
outras mudam, ela só diverge. Achei lendo o risco que o próprio plano listava como "conferir antes de
subir".

**3. E ao consertar a quinta, errei de novo — e o teste pegou na primeira execução.** O par antigo
(`ganho=ganha OU comum=aberta`) cobria o equivalente do `NoQuadro` **por acidente**: ao nomear os dois
status permitidos, ele excluía `concluida`/`perdida`/`cancelada` sem dizer que estava fazendo isso. A
minha cláusula nova, sozinha, passou a admitir qualquer status nas colunas comuns, e o relatório
contou 6 onde o quadro mostrava 5.

**4. O risco mais grave tinha TRÊS portas, não duas.** O plano previu `ReordenarAsync` e
`RemoverAsync`. Faltava `DefinirGanhoAsync` — e é **a mais provável das três**: *"agora quem fecha é
Entregue"* é um clique natural na tela de etapas.

**5. Dois alarmes do plano eram falsos, e dizer isso importa mais que consertá-los.**

- O plano afirmava que deixar a `LiberacaoDeCiclo` na condição velha devolveria a conversa para a
  fila em silêncio. **Não devolve:** ela tem o próprio `NOT EXISTS (negociação 'ganha')`, e com a
  chave desligada o negócio fica `ganha`. A sabotagem ficou impune porque não é um defeito. O
  gatilho ficou por clareza, com um comentário dizendo que sabotar aquele ramo não derruba teste.
- `migrations.sql` não é artefato versionado — é gerado no deploy e está no `.gitignore`.

**6. Três fixtures de teste descreviam um estado impossível.** Cards na coluna de venda com
`ganha: false`. Elas só passavam porque o controle do card olhava a *coluna*; quando ele passou a
olhar o *card*, os três testes caíram — e a fixture era a errada.

**7. O meu primeiro teste do drop ganho→ganho passava sozinho.** `aoSoltar` já sai antes, porque
soltar onde se estava não é movimento. Descobri sabotando: apaguei o guarda e nenhum teste caiu. O
caso real é voltar da pós-venda para a coluna de venda — e ali eu tinha escrito um `return` seco, que
é justamente o silêncio que o DES-4 existiu para matar. Virou toast com a frase certa.

**8. Uma sabotagem achou um buraco que eu teria deixado.** Apagar a linha que lê
`conclusaoAutomatica` do servidor não derrubava teste nenhum, porque o sinal nasce `true` e a fixture
também era `true`. Uma empresa que desligou abriria a tela vendo o interruptor ligado, e o primeiro
"salvar" de qualquer outro campo religaria a feature.

---

## A regra, num lugar só

**`src/Nexora.Core/Entidades/RegrasDoQuadro.cs`.** Uma pergunta ("este negócio pode ir para esta
etapa?"), uma resposta: `null` = pode, ou a frase que o cliente lê.

Fica em arquivo próprio e não dentro de `RegrasNegociacao` porque aquela classe é inteira
`Expression`, para o Postgres avaliar; esta é decisão em C# sobre quatro escalares. Separadas, *"isto
vira SQL?"* se responde pelo nome do arquivo.

Com `og` = ordem da etapa de ganho, `oa` = ordem atual, `od` = ordem do destino:

| # | Quando | Resultado |
|---|---|---|
| 1 | destino é a etapa de ganho | recusa — a porta única do ganho, de **qualquer** status |
| 2 | `Perdida` | recusa, reabra primeiro |
| 3 | `Concluida` / `Cancelada` | recusa — a etapa é o registro de onde fechou |
| 4 | `Aberta` e `od > og` | recusa — pós-venda é de quem já vendeu |
| 5 | `Aberta`, resto | **pode**, nos dois sentidos |
| 6 | `Ganha` trocando de funil | recusa |
| 7 | `Ganha` e `od < og` | recusa — "não volta para a negociação" |
| 8 | `Ganha` e `od < oa` | recusa — "só avança" |
| 9 | `Ganha`, resto (`od >= oa`) | **pode** |

Três coisas não óbvias:

- **a ordem das linhas 7 e 8 é o produto.** De Pós-Venda para Proposta as duas se aplicam, e a frase
  que sai deve ser a da 7 — ela diz o **motivo**, não o mecanismo. Há um teste que afirma a frase, e
  é o único que percebe a inversão;
- **`od == oa` é permitido** para `Ganha`: é reordenar dentro da coluna, e `MoverAsync` é o único
  caminho que calcula `OrdemKanban`. Com vinte entregas pendentes o vendedor vai querer ordenar a
  fila;
- **a linha 3 trocou de texto.** A frase antiga — *"este negócio já foi fechado e não se move mais no
  quadro"* — cobria `Ganha` também, e teria passado a mentir para quem acabou de arrastar um card
  vendido com sucesso na coluna ao lado.

**Três coisas que o bloco conserta sem ter sido pedido:**

1. **a coluna de pós-venda aceitava exatamente a coisa errada.** O card vendido era recusado; o card
   **em aberto** entrava e aparecia, porque a etapa não é `EGanho`. Dava para pôr em "Entregue" um
   negócio que nunca foi vendido;
2. **um card invisível que existe hoje em produção.** `ServicoContatos.MarcarGanhoAsync` busca a etapa
   de ganho com `FirstOrDefaultAsync`; em funil **sem** etapa de ganho dá `null`, o card fica na etapa
   pré-venda e vira `ganha` — e o recorte velho não o mostrava. Negócio vendido, fora da tela, com a
   vaga do funil ocupada e nenhum jeito de concluir pelo quadro;
3. **a porta única do ganho estava escrita duas vezes, com duas frases diferentes.**

## A conclusão automática

`empresas.conclusao_automatica boolean NOT NULL DEFAULT TRUE`.

Coluna própria e não um valor-sentinela no número: `0` já significa "na hora" e é legítimo (padaria,
salão), `90` é "quase nunca" mas ainda conclui, e `NULL`/`-1` batem no `ck_empresas_conclusao`.
Guardar o número com a chave desligada é o que faz religar devolver o prazo antigo.

A migration **não tem SQL cru**: `defaultValue: true` vira DEFAULT no DDL e o Postgres preenche toda
linha existente. Não é sorte — é preferir o DEFAULT a um `UPDATE` à mão justamente porque a armadilha
do `;` do OPE-1 só existe em `migrationBuilder.Sql`.

No `ConclusaoAutomatica`, a condição do relógio é:

```sql
AND NOT EXISTS (SELECT 1 FROM etapas_funil g
                 WHERE g.pipeline_id = n.pipeline_id AND g.e_ganho AND g.ordem < et.ordem)
```

**E não o óbvio `AND et.e_ganho`.** Os dois são iguais nos dois casos que a gente pensa e diferentes
nos dois que a gente esquece:

| o card está… | `et.e_ganho` | `NOT EXISTS` |
|---|---|---|
| na etapa de venda | conclui | conclui |
| na pós-venda | pula ✔ | pula ✔ |
| numa etapa anterior, `ganha` (legado) | **pula para sempre** ✘ | conclui ✔ |
| em funil **sem** etapa de ganho | **pula para sempre** ✘ | conclui ✔ (é o de hoje) |

⚠️ **O apelido é o perigo real.** `empresas e` virou `emp` porque `et` é a etapa. Três `{0}` numa
string crua, rodada diária, exceção engolida pelo `AgendadorFollowUp`. Um `e.` esquecido compila e
para a conclusão automática de **todos os clientes**, com uma linha de log como única evidência. A
rede é o teste `A_RODADA_DIARIA_CONCLUI_O_QUE_PASSOU_DO_PRAZO_com_autor_sistema`, que não foi tocado.

## O quadro

O recorte das cinco cópias virou uma cláusula **menor** do que a que estava lá:

```csharp
.Where(n => !e.EGanho || n.Status == StatusNegociacao.Ganha)   // somado a NoQuadro
```

*A coluna de ganho mostra só o que está ganho; as outras mostram o que o `NoQuadro` admitir.*

Dois campos novos no DTO, com trabalhos diferentes e um aviso escrito entre eles:
`ColunaFunil.PosGanho` manda no **enfeite da coluna**; `CardFunil.Ganha` manda nos **controles do
card**. Usar a coluna para decidir o controle é o erro que cria o beco sem saída: um card vendido em
"Pós-Venda" mostrava **"Registrar venda"** — que dá 409 *"este contato não tem negócio em aberto"* — e
**nenhum "Concluir"**, que é o único jeito de liberar a vaga do funil daquele contato.

No Angular, a recusa acontece **antes** do drop. Mas o `preventDefault` **fica** mesmo na coluna
recusada: sem ele o `drop` nunca dispara, o card volta sozinho sem mensagem, e é exatamente a falha do
DES-4 (*"o vendedor tentava, falhava, e concluía que o kanban não funciona"*). A coluna avisa durante
o arrasto e o toast explica depois.

O `podeReceber` do cliente é **deliberadamente mais estreito** que `RegrasDoQuadro`, e a assimetria
está em comentário: estreito demais produz "a API recusou algo que o cliente deixou passar", que o
toast já trata; largo demais torna um movimento legal impossível, e nenhum teste pegaria.

---

## Riscos

**R1 — a vaga do funil fica presa, e isso foi escolhido.** Parar o relógio na pós-venda +
`uq_negociacoes_card_por_funil` = aquele contato não pode abrir novo negócio naquele funil enquanto o
card estiver lá, e `ServicoContatos` simplesmente para de listar o funil, sem explicação.

A resposta é a frase na tela de Configurações, quando a chave está desligada: *"enquanto ele estiver
lá, este contato não pode abrir outro negócio neste funil"*. É a única tela onde alguém escolhe pagar
esse preço.

> **O que ficou de fora, e eu recomendo:** um selo **"parado há N dias"** no card de pós-venda. Não um
> segundo prazo escondido — um segundo relógio invisível reintroduziria a classe de bug "o card sumiu
> e ninguém sabe por quê" que o NEG-2 inteiro existiu para matar.

**R2 — mexer nas etapas podia desfazer a venda de todo mundo. Resolvido, e era o mais grave.**
Silencioso **e** diferido. Os cards continuam visíveis, e é por isso que nada parecia errado: o
`NOT EXISTS` passava a ler "não há etapa de ganho antes de mim" e a rodada da noite concluía todos.
Guarda nas três portas, com a invariante escrita ao lado da do arrasto.

⚠️ O guarda **só recusa o que a mudança piora**. Negócio `ganha` parado numa etapa anterior já existe;
recusar por causa do estado atual travaria a tela de etapas para sempre, por um estado que a empresa
não criou e não tem como consertar dali.

**R3 — o relatório 4 ganha linhas novas.** `SqlFunilEntradas` conta entradas de etapa pela trilha, e
movimentos de pós-venda produzem entradas em etapas que nunca tiveram. Não é erro: é o fato novo. Mas
quem compara um relatório de setembro com um de outubro vai ver mais linhas.

**R4 — os webhooks `lead.movido` passam a disparar para negócios já vendidos.** Tráfego novo no
sistema dos clientes. Nota de release.

**R5 — fora de escopo, mas fica mais visível:** o funil do dashboard não filtra por pipeline — desenha
as etapas de **todos** os funis lado a lado e normaliza a largura sobre o conjunto. Já era assim; com
etapas de pós-venda, mais colunas.

---

## Verificação

```
dotnet build nexora.sln -warnaserror     # ⚠️ derrubar a Nexora.Api antes: trava o build e a 5123
dotnet test nexora.sln                   # 1384
npm run test:ci                          # 493
npm run test:celular:ci                  # 105
```

**23 sabotagens conferidas uma a uma**, cada uma derrubando o teste que a nomeia. Duas exigiram
cuidado especial:

- `defaultValue: false` na migration **só aparece com o `nexora_teste` derrubado** — `Migrate()` não
  reaplica migration já registrada;
- a sabotagem da `LiberacaoDeCiclo` é **impune de propósito**, e está documentada no código: ela não
  é um defeito.

### Na máquina, antes de subir

1. criar "Pós-Venda" e "Entregue" depois de "Venda", vender um contato, arrastar até "Entregue" e
   conferir que **em cada parada** o card aparece, com valor, e oferece "Concluir";
2. tentar arrastar de volta — recusa, sem o card sair do lugar;
3. arrastar um card em negociação para "Pós-Venda" — recusa;
4. com a chave ligada e `dias = 0`: vender e confirmar que conclui na hora; mover para pós-venda e
   confirmar que **não** conclui;
5. desligar a chave e repetir o item 4 — nada conclui sozinho;
6. abrir o dashboard da empresa real (1 contato, 2 vendas concluídas) e confirmar que ela vê as **2
   vendas**, e não o aviso de boas-vindas;
7. reordenar etapas pondo a de ganho no fim — recusa, citando a conclusão automática.
