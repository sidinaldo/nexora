# MOB-5 — As abas de filtro no topo da caixa

Escopo: CSS, template e seis linhas de TypeScript na caixa. Nenhuma mudança de API, rota ou regra
de negócio.

---

## 1. A variante existia, e já estava aplicada

`styles.css` tem `.abas.rolam`, com o comentário *"Numa lista estreita (a caixa de entrada), as
abas rolam em vez de quebrar linha"*. O MOB-2 a aplicou na caixa e o MOB-3 no indicador de etapas
do funil — as duas usam `class="abas rolam"` hoje.

**Então não havia regra nova para escrever: havia regra existente para consertar.** É o que foi
feito. O CSS novo entrou dentro de `.abas.rolam`, e por isso corrigiu a caixa e o funil de uma vez.

---

## 2. ⚠️ Nada está fixo no topo — o que existe é outro defeito

A busca por `position: fixed` e `position: sticky` no projeto inteiro devolve **uma ocorrência**: o
`.overlay` dos modais. Não há cabeçalho fixo, aba fixa nem busca fixa em tela nenhuma.

Logo, a hipótese do enunciado — "elemento fixo que não reserva espaço no conteúdo", o defeito da
barra inferior do MOB-3 agora no topo — **não é o que está acontecendo**. A faixa de abas está no
fluxo normal, e `.lista-corpo` é irmã dela num flex em coluna: a lista já encolhe sozinha, sem
ninguém reservar nada.

Medido na caixa em 390px, antes de qualquer correção:

```
LISTA-TOPO   top=0  h=123
ABAS         top=0  h=68   clientH=53   scrollW=540  clientW=390   overflowY=auto
BUSCA        top=68 h=54
pílulas      "Aguardando resposta" left=9 · "Minhas" 174 · "Não atribuídas" 251
             · "Todas" 374 · "Resolvidas" 441
AS ABAS COBREM A BUSCA?  não
```

Dos três sintomas relatados:

| relatado | o que a medição mostra |
|---|---|
| quarta aba cortada à direita, sem indicação | **confirmado.** 540px de conteúdo em 390 — 150px fora da tela, e "Todas" começa a 374, com 16px visíveis |
| abas cobrindo o campo de busca | **não se reproduz.** As abas terminam em 68 e a busca começa em 68 — encostam, não se sobrepõem |
| borda superior cortada | **não é corte.** As pílulas ficam a 9px do topo, sem clipping. Ver §3 — o que existe ali é outra coisa, e explica a leitura |

### O que a medição encontrou e o relato não nomeava

**A faixa gastava 15px de altura com barra de rolagem.** `h=68` de caixa para `clientH=53` de
conteúdo: a barra horizontal era desenhada por dentro, e com `padding-bottom: 0` ela encostava na
base das pílulas — um risco cinza entre as abas e o campo de busca. É a leitura mais provável tanto
de "borda cortada" quanto de "cobrindo a busca": não há sobreposição, há uma barra cinza no meio.

**`overflow-y` tinha virado `auto`.** É regra do CSS, e o efeito não é óbvio: com um eixo em `auto`,
o outro sai de `visible` e vira `auto` também. A faixa era container de rolagem nos **dois** eixos,
e qualquer crescimento vertical — anel de foco, uma pílula mais alta — passaria a ser cortado.

---

## 3. As telas varridas

Cada tela foi montada em 390px e cada faixa de topo (`.abas`, `.abas-serie`, `.linha-filtros`,
`.coluna-topo`) foi medida quanto a transbordo horizontal e a altura roubada por barra de rolagem.

**Resultado: uma única tela tem faixa que transborda.**

| tela | faixa | transborda | corrigido |
|---|---|---|---|
| `/caixa` | abas de filtro | **150px**, com 15px de barra | sim |
| `/funil` | indicador de etapas (MOB-3) | não, com 3 etapas curtas — mas usa a mesma variante | sim, pela variante |
| `/contatos` | busca + filtros + chips | não | — |
| `/meu-dia` | abas de tipo | não | — |
| `/dashboard` | alternador de período e de métrica | não | — |
| `/captacao` | abas de formulários e QR | não | — |
| `/configuracoes` · `/equipe` · `/conexao` · `/contatos/:id` | não têm faixa de topo — o cabeçalho de página rola com o conteúdo | — | — |

O indicador do funil não transbordava no teste porque as três etapas de exemplo têm nomes curtos.
Com cinco etapas e nomes reais, ele teria exatamente o mesmo defeito — e por isso a correção ficou
na variante compartilhada, e não numa cópia dentro da caixa.

---

## 4. O que foi corrigido

### `.abas.rolam` — a variante compartilhada

**`overflow-y: hidden` explícito.** Fecha o container de rolagem vertical que o CSS criava
sozinho. Ali não há o que rolar para cima ou para baixo.

**Sombras nas bordas, sem uma linha de JavaScript.** Item cortado sem indicação parece defeito;
com indicação é convite a rolar. Quatro camadas de `background` com `background-attachment`:

- duas `local`, da cor do fundo, que rolam **junto** com o conteúdo;
- duas `scroll`, que ficam **paradas** na borda.

Encostado no início, a camada opaca cobre a sombra da esquerda; ao rolar, ela sai e a sombra
aparece. Some sozinha quando não há mais conteúdo fora. Nenhuma cor nova: o tom é o `--verde` da
marca com alfa, o mesmo par que o token `--sombra` já usa.

`--fundo-abas` existe porque a faixa vive sobre superfícies diferentes — branco na caixa, creme no
funil. A camada opaca tem que ser da cor do fundo, senão vira mancha.

**A barra de rolagem some — só no dedo.** Em `@media (pointer: coarse)`. Os 15px que ela cobrava
saem direto da altura da lista de conversas, e em toque a rolagem se faz arrastando. ⚠️ **No mouse
ela fica**: ali a barra é a única alça, e a caixa tem a mesma faixa no desktop — a lista tem 340px e
as cinco abas somam 540. Esconder lá seria trocar um defeito por outro.

### `.lista-topo` — respiro, só no celular

`padding-top: 6px` abaixo de 861px, levando a folga da pílula contra a borda superior de 9px para
15px — a mesma que ela já tem dos lados. No desktop a faixa vive dentro de um painel com vizinhos
em volta; no celular ela é a primeira coisa da tela, e 9px lê como corte de renderização.

### A aba ativa não some da tela

Seis linhas em `caixa.ts`. O caso que quebra: no celular, abrir uma conversa **destrói** a lista —
é o `@if` que faz estado e DOM dizerem a mesma coisa — e voltar recria a faixa com a rolagem
zerada. Quem filtrava por "Resolvidas", a última das cinco, voltava sem enxergar em que filtro
estava, com uma lista curta que parecia a caixa inteira.

⚠️ Mexe só no `scrollLeft` da própria faixa. `scrollIntoView` resolveria em uma linha e poderia
rolar os **ancestrais** junto — inclusive o `.conteudo` do shell, que é a rolagem da página.

---

## 5. Qual elemento fica fixo, por tela

**Nenhum.** E é a resposta, não uma omissão.

O enunciado pede para decidir, na caixa, se fixa as abas ou a busca — porque dois fixos empilhados
comeriam metade da tela, que foi o problema da barra inferior vertical. A decisão foi a mesma do
MOB-3 para o rodapé: **fluxo, não `position: fixed`.**

`.lista` é um flex em coluna com `.lista-topo` em `flex: 0 0 auto` e `.lista-corpo` em `flex: 1`
com `overflow-y: auto`. O topo não rola porque **não está dentro do que rola** — e não porque está
preso à janela. O resultado que se queria (as abas e a busca sempre visíveis, a lista correndo por
baixo) já acontecia; o que faltava era a faixa não gastar altura à toa.

O que isso evita, e que `fixed` cobraria: reservar `padding-top` equivalente em cada uma das 22
telas, e a que ninguém lembrasse de reservar nasceria com a primeira linha coberta. É a mesma conta
do MOB-3, registrada lá.

Um teste trava a disciplina: **nenhuma tela pode ter elemento `fixed` ou `sticky` sobre o
conteúdo**, com duas exceções nomeadas — `.overlay` (o modal, que cobre tudo porque é isso que um
modal faz) e `.pilha` (os toasts, aviso transitório que some sozinho). A lista é explícita para uma
terceira exceção ser uma decisão visível.

---

## 6. A área segura, e por que ela vale zero hoje

`main` ganhou `padding-top: env(safe-area-inset-top, 0px)`. É a única fronteira superior do painel
— acima dele só existe a borda da janela —, então um recuo ali cobre as 22 telas de uma vez, e a
faixa de WhatsApp desconectado junto, que é a primeira coisa dentro dele.

⚠️ **Isso não faz nada hoje, e o motivo importa:** `env(safe-area-inset-*)` só é diferente de zero
quando o `<meta name="viewport">` traz `viewport-fit=cover`. O do projeto é
`width=device-width, initial-scale=1` — sem `viewport-fit`. Sem ele o próprio navegador mantém a
página fora do entalhe, e o recuo é desnecessário.

Vale para o `env(safe-area-inset-bottom)` da barra inferior também, do MOB-2: ele está inerte desde
que foi escrito.

**Não liguei o `viewport-fit=cover`**, e a decisão é deliberada: ligá-lo faz a página passar a se
estender **por baixo** do entalhe e do indicador de gestos, e aí as **quatro** bordas passam a
precisar de tratamento — inclusive as laterais, que em paisagem num aparelho com entalhe também
ganham recuo. Hoje o comportamento é seguro; ligar o `viewport-fit` sem cobrir as quatro criaria o
problema que ele existe para resolver. As declarações ficam escritas no lugar certo, e a decisão
fica registrada aqui como um bloco próprio.

---

## 7. O desktop

A faixa de desconexão continua no topo do `main`, `flex: 0 0 auto`, encolhendo a área de conteúdo
em vez de empurrá-la — e o teste de que **um único container rola** (`.conteudo`) continua verde.
Nada no MOB-5 a toca.

Das regras novas, quatro não têm limite de largura. Todas conferidas:

| regra | por que sem limite |
|---|---|
| `.abas.rolam` (variante) | corrige o mesmo defeito nas duas larguras — a caixa do desktop tem a mesma faixa, com 200px escondidos numa lista de 340px |
| `main { padding-top: env(...) }` | área segura é do aparelho, não da largura. Zero no desktop |
| `.indicador-etapas { --fundo-abas }` | declaração de variável, inerte sem consumidor; e o elemento só existe sob `@if (ehCelular())` |
| `.lista-topo` | falso positivo da varredura: a regra base não mudou, o que entrou está dentro do `@media` |

O que **ficou** com limite: o respiro de 6px (`max-width: 860px`) e o some-a-barra
(`pointer: coarse`).

```
ng build                 sem erro, sem warning novo
npm test -- --no-watch   300 SUCCESS   (inalterado)
npm run test:celular      80 SUCCESS   (eram 55; +25 neste bloco)
```

Os 25 novos são 22 do laço "não põe nada flutuando sobre o conteúdo", uma por tela, mais três da
faixa de abas: não perde altura para barra de rolagem, não cobre a busca, e a ativa continua
visível ao voltar da conversa.

---

## 8. Pendências

**Dois dos três sintomas relatados não se reproduzem** na medição — as abas não cobrem a busca, e
não há corte na borda superior. O que existia no lugar foi corrigido (a barra de 15px e os 150px
sem indicação), e é provável que resolva a queixa. **Se o defeito persistir num aparelho de
verdade, é outra coisa, e a medição acima é o ponto de partida** — ela dá os números para comparar.

**`viewport-fit=cover` continua desligado**, e com ele todo o trabalho de área segura — deste bloco
e do MOB-2 — permanece inerte. Ligá-lo é uma decisão de quatro bordas, não de uma linha.

**As sombras de rolagem não foram vistas em aparelho.** O mecanismo de `background-attachment` é
CSS puro e não tem como falhar em teste, mas o quanto elas *leem* como "há mais aqui" só se
descobre olhando. É o tipo de coisa que um teste não decide.

**A faixa do funil segue sem transbordar no teste**, porque as três etapas de exemplo têm nomes
curtos. Com cinco etapas reais ela rola — e aí passa a depender das mesmas sombras, sem nenhuma
asserção cobrindo esse caso.
