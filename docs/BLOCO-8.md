# Bloco 8 — Telas de funil e contatos

Estado: **fechado com uma ressalva** — 7 dos 8 critérios verificados por execução; o gesto de
arrastar em si não foi clicado num navegador por mim (ver §7).

`ng build` limpo, sem warning novo. **248 testes de backend verdes** (nenhum novo — este bloco é
só de tela). Nenhuma biblioteca instalada.

---

## 1. O que foi construído

| Arquivo | O que é |
|---|---|
| `nucleo/thread/thread.ts` + `.html` + `.css` | **A thread da conversa, extraída** da caixa de entrada |
| `nucleo/fechamento/modal-fechamento.ts` | O modal de venda ganha / perdida — um componente, duas portas |
| `nucleo/servicos/contatos.servico.ts` | 8 chamadas da API de contatos |
| `nucleo/servicos/funil.servico.ts` | Quadro, coluna por cursor, mover |
| `paginas/funil/` | O kanban |
| `paginas/contatos/` | A lista |
| `paginas/contato/` | O detalhe |
| `nucleo/modelos.ts` | `Pagina<T>`, `ContatoResumo`, `ContatoCard`, `ContatoDetalhe`, `ColunaFunil`, `QuadroFunil` |
| `paginas/caixa/` | Reescrita para **usar** o componente de thread |
| `app.routes.ts` | `/funil`, `/contatos`, `/contatos/:id` deixaram de ser placeholder |

As rotas `funil` e `contatos` apontavam para o componente "em breve". Agora só sobram lá o
dashboard e as configurações, que são as etapas 9 e 10.

---

## 2. A thread, extraída em vez de duplicada

O prompt pede para não duplicar a mecânica da thread. Ela virou `app-thread`, que recebe
`conversaId` e cuida de tudo: paginação por cursor, âncora de rolagem, compositor, tick de ACK e
a assinatura do realtime.

**Por que essa mecânica não pode ser copiada:** ao carregar as mensagens anteriores, o
`scrollTop` é compensado pela altura inserida no topo — sem isso a thread pula na cara de quem
está lendo. E quando chega mensagem, a rolagem só acompanha se o vendedor **já estava** no fim;
se ele subiu para reler algo, aparece o chip "↓ Nova mensagem" e a rolagem não é roubada. São
duas regras que ninguém acerta de primeira e que, duplicadas, seriam corrigidas em um lugar só.

Evidência de que a extração é real, não cosmética: o chunk `caixa` caiu de **61 kB para 34 kB**,
e surgiu um chunk compartilhado de 33 kB que a caixa e o detalhe do contato carregam juntos.

O **cabeçalho ficou de fora** de propósito: a caixa mostra assumir/liberar, o detalhe mostra
outra coisa. O que é comum é a thread, não o que está em volta.

---

## 3. O kanban

### Arrastar sem biblioteca

HTML5 drag-and-drop nativo, `draggable="true"` nos cards.

**A zona de soltura é o espaço ENTRE cards, não o card.** Cada coluna tem separadores de 4px que
engordam para 8px durante o arrasto e para 26px quando são o alvo, com borda tracejada. Usar o
card como alvo obrigaria a adivinhar "caiu na metade de cima ou de baixo?", que erra perto das
bordas — com o separador, o ponto de queda é literalmente onde o card vai ficar.

O `preventDefault()` no `dragover` está lá porque **sem ele o `drop` nunca dispara** — o
navegador não considera o elemento uma zona válida. É a pegadinha nº 1 do DnD nativo e não dá
erro nenhum: o card simplesmente volta.

### Atualização otimista

O card muda de coluna na tela na hora; a chamada vai em paralelo. A lista anterior fica guardada
como snapshot; se a API recusar, `colunas.set(anterior)` devolve tudo ao lugar e um toast diz o
motivo — com a mensagem que veio da API, não uma genérica.

Os totais e as somas das colunas também são ajustados de forma otimista, senão o cabeçalho diria
"12" enquanto a tela mostra 13 cards.

### Conflito entre dois vendedores

Em 409 o card volta **e** as colunas de origem e destino são recarregadas do servidor. A tela não
trava e não vira modal — o vendedor vê o estado real e tenta de novo.

### Renormalização

`mover` devolve a nova `ordemKanban`. Se ela divergir da que o cliente pintou, o servidor
renormalizou a coluna (bloco 7) e as ordens em mãos ficaram velhas. A coluna é recarregada — sem
isso, o "carregar mais" pediria a partir de um cursor que não existe mais.

### Carregamento por coluna

50 cards por vez. O cabeçalho mostra **"50 de 312"** quando há mais, e só o total quando não há.
O "carregar mais" usa cursor `(ordemKanban, id)`, com dedupe por id na anexação.

### O card

Nome, telefone, valor (quando houver), avatar do responsável (ou "sem dono"), contador de não
lidas e o **ponto do semáforo** — calculado no cliente a partir do `aguardandoDesde`, com as
faixas e a janela vindas de `/api/painel/status`. A cor nunca é pedida à API.

---

## 4. A porta única da venda, nas duas telas

`ModalFechamento` é **um componente**, usado por kanban e detalhe. Ele não emite requisição:
devolve `{ tipo, valor, motivo }` e quem abriu decide o que chamar. É isso que permite ao kanban
desfazer o movimento quando o vendedor cancela.

Três caminhos chegam nele, e os três acabam no mesmo `POST /api/contatos/{id}/ganho`:

1. **arrastar o card para a coluna "Venda"** — ao soltar ali, o movimento otimista **não roda**;
   abre o modal. Cancelar não precisa desfazer nada, porque o card nunca saiu do lugar;
2. **botão "Venda fechada"** no rodapé do card;
3. **botão "Venda fechada"** no detalhe do contato.

E um quarto que também converge: o `<select>` de etapa no detalhe. Escolher a etapa de venda ali
abre o mesmo modal, porque a API recusa `mover` para ela. Sem isso, o select seria um caminho que
grava diferente — exatamente o que a decisão de produto do bloco 7 existe para impedir.

"Cliente perdido" usa o mesmo componente com `tipo="perda"`, pedindo motivo obrigatório.

---

## 5. Lista de contatos

Tabela do design system (`.tabela`), busca com debounce de 350ms, filtros por etapa, responsável
e origem, paginação com total.

**Paginada por offset, não por cursor** — o oposto da caixa de entrada, e de propósito: cursor
serve para lista que se reordena sozinha entre requisições. Contato não muda de nome sozinho, e
offset dá o total ("142 contatos") que cursor não fornece.

Contato anonimizado não aparece: quem filtra é a API.

**Uma limitação assumida:** o filtro por **origem** acontece no cliente, sobre a página
carregada. A API do bloco 7 filtra por etapa e responsável, mas não expõe origem. Filtrar 30
linhas no navegador é honesto, e a tela **não mente sobre o total** — com esse filtro ligado, o
subtítulo muda para "N de M nesta página · T no total". Vira filtro de servidor quando a API
ganhar o parâmetro.

O filtro por responsável e o campo de responsável no cadastro só aparecem para o **dono**:
`GET /api/equipe` é `[Authorize(Roles="dono")]`, e pedir como vendedor daria 403.

---

## 6. Detalhe do contato

Duas colunas: dados/negociação/lembretes à esquerda, conversa à direita. A conversa tem altura
própria para a thread rolar sozinha — a página não rola junto com o chat.

- **Dados** — edição inline (o cartão vira formulário no lugar), sem modal.
- **Negociação** — select de etapa, situação atual e as ações. Contato ganho ou perdido tem o
  select desabilitado: mover um negócio fechado não faz sentido sem reabrir antes.
- **Lembretes** — pendentes e concluídos separados, com criar, concluir e cancelar. Os
  automáticos vêm marcados com o selo "follow-up".
- **Conversa** — o `app-thread`, mais um link "Abrir na caixa" que leva a `/caixa?conversa=N`.
  Quando não há conversa ainda, explica que ela começa na primeira mensagem do cliente.

### Anonimizar

Confirmação por **digitação do nome do contato**, como o prompt pede — não um "tem certeza?". O
botão só habilita quando o texto bate. O modal diz exatamente o que será apagado e o que
permanece.

Depois de anonimizado, um aviso no topo explica o estado e a tela some com editar, mover e as
ações de fechamento: a API recusaria de qualquer forma, e oferecer botão que sempre dá erro é
pior que não oferecer.

---

## 7. Como cada critério foi verificado

| # | Critério | Como |
|---|---|---|
| 1 | `ng build` limpo | Executado. Sem warning novo |
| 2 | Arrastar persiste; topo, fim e coluna vazia; ganho abre modal; cancelar devolve | **Parcial** — ver ressalva abaixo |
| 3 | Recusa devolve o card com toast | Código verificado; o 409 e seu corpo confirmados por HTTP |
| 4 | Coluna carrega em partes com total | `GET /funil?porColuna=50` devolve `total`, `contatos` e `temMais` |
| 5 | Lista: busca, filtros, paginação; anonimizado não aparece | Executado ponta a ponta |
| 6 | Detalhe: dados, thread, lembretes, 4 ações | Executado ponta a ponta |
| 7 | Venda no kanban e no detalhe dão o mesmo resultado | Mesmo componente, mesmo endpoint — um caminho só no código |
| 8 | Teste manual documentado | Abaixo |

### A ressalva (critério 2)

**Não cliquei e arrastei num navegador.** Não tenho navegador nesta sessão. O que verifiquei:

- o TypeScript compila e o bundle é gerado;
- a **sequência de chamadas** que o arrasto produz funciona contra a API real, incluindo as três
  bordas (topo com `aposContatoId: null`, fim, e coluna vazia devolvendo `ordemKanban=0`);
- a recusa da etapa de ganho devolve 409 com o corpo que a tela precisa para o toast.

O que **não** foi verificado por execução: os eventos `dragstart` / `dragover` / `drop`
dispararem de fato, os separadores acenderem no lugar certo, e o comportamento em toque (mobile).
O prompt prevê o fallback de `pointerdown`/`pointermove`/`pointerup` se o nativo falhar em
mobile — esse fallback **não foi escrito**, porque escrevê-lo sem poder testar seria adivinhar.

### Teste manual — o fluxo completo contra a API de pé

Percorri a sequência exata que as três telas disparam, na ordem em que o vendedor as dispara,
conferindo **cada nome de campo do JSON** contra as interfaces TypeScript. Um `valorTotal` que
voltasse como `ValorTotal` deixaria o cabeçalho da coluna em branco sem erro nenhum no console —
é a falha silenciosa mais provável numa tela nova.

```
1.  GET  /funil                 -> QuadroFunil e ColunaFunil: 9 campos conferem
2.  POST /contatos              -> id=3
3.  GET  /contatos              -> Pagina<T> e ContatoResumo: 17 campos conferem
4.  GET  /contatos?busca=       -> por nome: 1 · por telefone mascarado "(84) 99555": 1
5.  POST /funil/3/mover         -> 'Primeiro Atendimento', ordemKanban=0 (coluna vazia)
6.  POST /funil/3/mover (venda) -> 409 {"erro":"Para mover para a etapa de venda, registre a venda…"}
7.  POST /contatos/3/ganho      -> 204
8.  reflete nas três telas:
      FUNIL     coluna 'Venda': total=1 valor=800.00, card presente
      LISTA     aparece em 'Ganhos', sumiu de 'Abertos'
      DETALHE   ganhoEm preenchido, valor=800.00, etapa=Venda
      DASHBOARD vendasDoMes=1 faturamento=800.00
9.  POST /contatos/3/reabrir    -> ganhoEm limpo, valor 800 PRESERVADO, voltou para 'Novo Lead'
10. POST /lembretes             -> aparece no detalhe; LembreteDto: 14 campos conferem
11. POST /contatos/3/anonimizar -> nome='Contato anonimizado', telefone='ANON-3'
                                   sumiu da lista E do kanban
```

**Nenhuma divergência de contrato.** Os dados de teste foram removidos do `nexora_dev` depois.

---

## 8. Decisões próprias

1. **Separador como zona de soltura**, em vez do card (justificado em §3).
2. **Recarregar o quadro inteiro após registrar venda**, em vez de mexer nas colunas na mão: o
   ganho mexe em duas colunas e nos dois totais, e o custo de uma requisição é menor que o de um
   bug de contagem.
3. **Select de etapa no detalhe converge para o modal de venda** quando a etapa escolhida é a de
   ganho. Sem isso, seria um caminho paralelo que a API recusaria com um erro que o vendedor não
   saberia resolver.
4. **Filtro por origem no cliente**, com o rótulo do total mudando para não mentir (§5).
5. **Editar contato é inline no detalhe e modal na lista.** No detalhe o cartão já está na tela e
   virar formulário é natural; na lista não há onde editar sem tirar a tabela do lugar.
6. **Clicar no card abre o contato.** Arrastar e clicar convivem porque o `click` só dispara
   quando não houve arrasto — comportamento do próprio navegador.
7. **Nome do contato na caixa de entrada virou link** para o detalhe. A caixa era a única tela com
   o nome do cliente e nenhum caminho para os dados dele.
8. **`porColuna=1` ao carregar as etapas** para os `<select>` da lista e do detalhe: só interessam
   os nomes, e pedir 50 cards por coluna seria pagar o quadro inteiro para preencher um campo.

---

## 9. Pendências e limites

### Deste bloco

| Limite | Consequência |
|---|---|
| **Arrasto não testado em navegador** | O gesto pode precisar de ajuste; o fallback de ponteiro para mobile não existe |
| **Sem arrastar em toque** | Em celular o kanban vira lista com o botão "Venda fechada"; mover exige o select do detalhe |
| Filtro de origem no cliente | Recorta só a página carregada (rótulo do total avisa) |
| Sem mover em lote | Reorganizar o funil inteiro é um card por vez |
| Sem virtualização das colunas | 50 cards por vez resolve hoje; coluna com centenas carregados vai pesar |
| Sem realtime no kanban | Card movido por outro vendedor só aparece ao recarregar. O `mudou` do 409 cobre o conflito direto, não a visualização passiva |
| Sem testes de frontend | Continua zero, desde o bloco 5 |

### Carregadas dos blocos anteriores

- **Nenhum telefone pareado** (desde o bloco 3) — segue sendo o único item com risco em vez de
  volume.
- **Nenhum envio de e-mail** (desde o bloco 1).
- **`ServicoCadastroEmpresa` sem controller** — não há como criar empresa pela API.
- **Sem tela de configuração** (expediente, faixas do semáforo, dias de follow-up).
- **Sem lock distribuído** no agendador de follow-up.
- `senhas-dev.sql` na raiz, com senha em texto puro.

---

## 10. Estado das 13 telas

| Tela | Antes deste bloco | Agora |
|---|---|---|
| Login | pronta | pronta |
| Aceitar convite | pronta | pronta |
| Redefinir senha | pronta | pronta |
| Caixa de entrada | pronta | pronta (usando a thread compartilhada) |
| Meu Dia | pronta | pronta |
| Equipe | pronta | pronta |
| Conexão WhatsApp | pronta | pronta |
| Minha conta | parcial (só senha) | parcial (só senha) |
| **Funil kanban** | ausente | **pronta** |
| **Contatos (lista)** | ausente | **pronta** |
| **Contato (detalhe)** | ausente | **pronta** |
| Dashboard | ausente | ausente — etapa 9 (a API está pronta) |
| Configurações | ausente | ausente — etapa 10 |

**11 de 13**, uma parcial, duas ausentes — e as duas ausentes já têm ou terão backend pronto.

---

## 11. O que falta para a fase 1 estar vendável

1. **Parear um telefone e mandar mensagem de verdade** — esforço desconhecido; o único item com
   risco em vez de volume.
2. **Testar o arrasto num navegador** — baixo, mas é pré-requisito para confiar no kanban.
3. **Tela do dashboard** (etapa 9) — baixo. A API está pronta desde o bloco 6 e agora recebe dado
   real.
4. **Onboarding e configurações** (etapa 10) — médio.
5. **Envio de e-mail** — médio.
