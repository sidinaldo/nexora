# MOB-2 — Responsividade mobile, implementada

Origem: `docs/MOBILE.md`. Escopo: CSS, template, teste e rota. Nenhuma mudança de API ou regra de
negócio.

---

## 1. Etapa 1 — o teste que faltava

### A segunda execução

`npm run test:celular` roda numa janela de celular, com ponteiro grosso, carregando **só** os
`*.celular.spec.ts`. O `npm test` continua exatamente como estava.

**Duas execuções e não redimensionamento por suíte**, porque não existe a segunda opção: o Chrome
recusa `window.resizeTo` em janela que o script não abriu, e media query responde à janela, não à
caixa em que o teste renderiza. A largura se escolhe no lançamento do navegador.

**Só os specs de celular** porque rodar as 26 suítes duas vezes dobraria o tempo sem cobrir nada
novo — e quebraria as que medem desktop de propósito: `larguras.spec.ts` mede numa caixa de 1400px,
`lateral.spec.ts` conta os itens de uma barra lateral que em 390px nem existe mais.

### ⚠️ O Chrome não entrega 390px, e isso está medido

`--window-size=390,844` **não** produz 390px. Pedindo 360, 390, 480 ou 500, o resultado é sempre o
mesmo: 504px de janela, ~489px de viewport dentro do iframe do karma. O headless antigo aceitava
qualquer tamanho e foi removido no Chrome 132.

O pedido continua 390 no arquivo, de propósito: é a intenção registrada, e no dia em que o piso
cair a execução passa a valer sem ninguém precisar lembrar.

O que isso custa, e o que não custa:

- as media queries do produto (860px) ficam **ativas** — 489 < 860. Era o que faltava, e é o que
  torna a caixa de entrada mensurável pela primeira vez;
- **nenhuma media query abaixo de ~489px pode ser testada.** O produto não tem nenhuma, e agora há
  um motivo escrito para continuar assim;
- a largura-alvo de 390px é aplicada pela **caixa** em que cada suíte renderiza. Isso é legítimo
  agora: o layout que está sendo espremido já é o de celular, não o de desktop.

### O ponteiro grosso, que também não vinha de graça

`@media (pointer: coarse)` é o que separa dedo de mouse, e o headless nasce `fine` — as regras de
alvo de toque não valeriam, e o teste delas passaria medindo o layout de mouse. As flags de
`--blink-settings` (`primaryPointerType=2`, `primaryHoverType=1`) resolvem.

### A guarda

`janela.celular.spec.ts` afirma as duas premissas: janela abaixo de 860px e ponteiro grosso.
Vermelho ali significa "ignore o resto da execução" — sem ele, uma flag que parasse de funcionar
deixaria toda a suíte de celular verde e sem sentido. **Foi essa guarda que pegou o piso de 504px
na primeira execução.**

### O spec que media errado

Os testes de transbordo saíram de `paginas.render.spec.ts` para `paginas.celular.spec.ts`. O
arquivo antigo media o layout de **desktop** espremido em 380px e admitia isso em comentário — e
por isso precisava **isentar** a caixa de entrada da medição (`SEM_COBERTURA_A_380PX`), que era
justamente a tela quebrada. A isenção morreu por não ter mais razão de existir: hoje as 22 telas
entram, a caixa inclusive, nas suas duas vistas.

O inventário de telas virou `telas-do-painel.ts`, compartilhado pelas duas suítes — com uma cópia
em cada arquivo, a tela nova entraria numa e não na outra, e a que ficasse para trás continuaria
verde.

---

## 2. O teste da caixa: antes e depois

Escrito **antes** da correção, e reprovando pelo motivo certo:

```
caixa no celular — tocar num contato abre a conversa
  TOCAR NUM CONTATO PÕE A CONVERSA NA TELA FAILED
    a conversa foi selecionada mas não ocupa espaço nenhum na tela — no celular o vendedor
    toca no contato e nada acontece: Expected 0 to be greater than 0.
Executed 5 of 5 (1 FAILED)
TOTAL: 1 FAILED, 4 SUCCESS
```

`Expected 0 to be greater than 0` é o `display: none` medido: a conversa estava selecionada, o
elemento existia no DOM, e a altura dele era zero.

Depois da Etapa 2:

```
Executed 5 of 5 SUCCESS
TOTAL: 5 SUCCESS
```

A suíte de celular fechou o bloco com **50 testes**.

---

## 3. Etapa 2 — a caixa de entrada

### Duas vistas, decididas no template

Saiu `@media (max-width: 860px) { .conversa { display: none } }`. Entrou `@if`:

```html
@if (mostrarLista())    { <section class="lista">…</section> }
@if (mostrarConversa()) { <section class="conversa">…</section> }
```

No desktop as duas condições são verdadeiras e nada mudou. No celular, uma de cada vez.

A diferença não é estilística. Escondendo por CSS, `sel()` dizia "conversa aberta" enquanto a tela
não mostrava nada — o estado e o DOM discordavam, e era exatamente essa discordância que fazia o
toque parecer sem efeito. Sem elemento renderizado, não há como divergir.

Quem decide é `nucleo/viewport.ts`: um sinal sobre `matchMedia('(max-width: 860px)')`, com
listener para quem gira o aparelho. **Ele não substitui media query** — aparência continua sendo
CSS; ali mora só a decisão de quais painéis existem, que é a única que o CSS não sabe tomar sem
mentir sobre o estado.

### A conversa aberta mora na URL

`/caixa` é a lista, `/caixa?conversa=42` é a conversa. Com isso: o Voltar do Android volta para a
lista, recarregar mantém a conversa, e o link é compartilhável.

**Parâmetro de consulta e não `/caixa/:id`**: duas entradas de rota para o mesmo componente fazem o
Angular destruir e recriar a Caixa a cada navegação — a lista, o cursor de paginação e o filtro se
perderiam. Com o parâmetro, a instância é reaproveitada.

De brinde, é a forma que o Meu Dia e o detalhe do contato **já usavam**: nenhum caller mudou. O que
mudou foi ler a rota por **assinatura** em vez de `snapshot` — lendo uma vez só, o Voltar mudaria o
endereço sem mudar a tela.

Abrir **empilha** no histórico e fechar pelo botão da tela **substitui**: empilhando nos dois, o
Voltar do aparelho reabriria a conversa que a pessoa acabou de fechar.

**A rolagem da lista** é o único estado que o `@if` descarta, e é restaurada por `scrollTop`
guardado no `afterNextRender` — quem estava na trigésima conversa não volta para o topo.

### O chip "nova mensagem"

Estava a `bottom: 96px` — a altura do compositor de desktop, escrita como número. Bastava o
compositor mudar de altura (celular, prévia de anexo, gravação em curso, campo crescido até cinco
linhas) para o chip cair dentro do rodapé ou boiar acima dele. Agora ele se ancora ao fim da área
de mensagens (`.thread-area`), e acompanha qualquer altura. Vale nas duas telas — a thread é a
mesma na caixa e no detalhe do contato.

### As abas

`class="abas"` virou `class="abas rolam"`. A variante existia em `styles.css`, foi escrita com o
comentário dizendo que era para a caixa de entrada, e ninguém a usava — as cinco abas somam ~474px
e quebravam em duas linhas.

---

## 4. Etapa 3 — os quatro estruturais

**Campo de 16px** abaixo de 861px. O corpo do painel é 15px e os campos herdavam; o Safari do
iPhone dá zoom automático abaixo de 16px e a página fica deslocada. É limiar de sistema
operacional, não preferência. **Só no celular**: no desktop os 15px são a medida que o design
system calibrou e não causam problema nenhum.

**Alvo de toque de 44px.** Duas correções:

- a regra `@media (pointer: coarse)` que engorda o menu lateral **existia e estava sendo anulada**
  por ordem de declaração — o bloco de `max-width: 860px` definia `nav a` depois, com a mesma
  especificidade. Não faltava regra: faltava ela vir por último. Corrigido movendo o bloco para o
  fim do arquivo;
- os demais controles (`.aba`, `.btn`, `.btn-pequeno`, `.link-editar`) ganharam `min-height: 44px`.

  ⚠️ **A primeira tentativa foi pseudo-elemento e não fechou.** A ideia era esticar a área sem
  engordar o visual. Funciona isolado e falha em conjunto — o teste mostrou por quê: a área
  ampliada de um controle passa por cima da do vizinho, e quem vem depois no DOM leva o toque. Em
  `/contatos` o "Limpar" ficava com **37px** porque a pílula de filtro 12px abaixo comia o resto.
  Não é detalhe de implementação: dois alvos de 44px precisam de 44px de espaço **cada**.
  Empilhados a 12px de distância, não existe truque que dê 44 aos dois — um deles está mentindo.

  Então o alvo cresce de verdade. O texto não muda de tamanho nem de posição; o que cresce é a
  caixa clicável, e só no dedo.

**Modal com topo alcançável.** `align-items: center` no overlay, somado à ausência de teto de
altura, fazia um modal alto perder o topo **e** o rodapé, sem rolagem. Com o teclado virtual aberto
(~350px de área útil) esse é o caso comum, não o extremo. A correção já estava escrita no mesmo
arquivo, no `.tela-centro`, e os modais não a tinham recebido: centralizar por `margin: auto` no
filho, teto em `100dvh` e rolagem no corpo.

**Gráficos respondem a toque.** `mousemove` → `pointermove` + `pointerdown`, tipo `PointerEvent`.
O corpo de `mover()` **não mudou uma linha** — `PointerEvent` estende `MouseEvent`. Mais
`touch-action: pan-y`, para arrastar no gráfico não roubar a rolagem vertical da página. E o
esconder passou a ignorar toque: `pointerleave` dispara ao levantar o dedo, e esconder ali faria o
valor piscar e sumir dentro do mesmo gesto.

---

## 5. Etapa 4 — navegação e funil

### A barra inferior

Cinco itens no rodapé, 56px de alvo: **Meu Dia · Caixa · Funil · Contatos · Mais**. Idêntica para
dono e vendedor — há teste montando os dois papéis e comparando. O recorte por papel acontece
dentro do "Mais".

A lateral **não é renderizada** no celular (`@if`, não `display: none`): treze links fora da tela
não precisam existir. Todo o CSS do recolhimento de 68px saiu junto — ele descrevia um elemento que
não existe mais.

Isso **revoga a escolha do DES-3 para o celular**, e o motivo está medido: no recolhido os itens
ficavam com ~26px de altura, e recuperar o alvo engordaria a barra, comendo altura — que é o que
falta na caixa e no funil. Somado aos 68px de largura cobrados o tempo todo (17% de um aparelho de
390px), a conta não fecha. A partir de 861px nada mudou.

O que veio da lateral e não se perdeu, com teste para cada:

| | onde ficou |
|---|---|
| badge de não lidas | no item Caixa da barra |
| ponto de status da conexão | no item "Mais" quando não está `ok`, e dentro do Mais junto do link de Conexão |
| faixa de WhatsApp desconectado | intocada, no topo do conteúdo |

A tela `/mais` lista Dashboard, Relatórios, o grupo de Configuração (só dono), a conta e o Sair —
com "Primeiros passos" no topo enquanto o checklist não fecha.

⚠️ **Relatórios e Primeiros passos não estavam na lista do enunciado**, e entraram: sem eles, duas
rotas ficariam inalcançáveis no celular — a barra não as tem e a lateral não existe mais.

### O funil

**Não virou abas de etapa**, e há teste afirmando que as três colunas continuam existindo lado a
lado. Kanban existe para a leitura lado a lado: quem só enxerga "Negociação" perde que há 40 cards
parados em "Novo Lead".

O que entrou:

- **parada por coluna** (`scroll-snap-type: x mandatory`), em ponteiro grosso. Sem snap o arrasto
  para no meio de duas colunas e o vendedor lê metade de cada uma;
- **indicador de posição** acima do quadro: uma pílula por etapa, reusando `.abas.rolam` do design
  system. Tocar leva até a coluna; rolar destaca a que chegou. Aponta, não filtra.

### Mover de etapa no celular

**"Mover para…" no rodapé do card**, só em ponteiro grosso. Um toque abre a lista de etapas, um
segundo move.

Ele **reusa `moverOtimista`**: o mesmo movimento na tela antes da resposta, o mesmo desfazer, a
mesma `versao` que transforma dois vendedores mexendo no mesmo card num 409 explícito, e a mesma
recarga de coluna. Nada de segundo caminho de escrita.

E obedece à **mesma regra do arrasto**: etapa de ganho não é movimento — a API recusa `mover` para
ela de propósito, e o caminho é o modal de fechamento. O card só sai do lugar depois de confirmado.
Há teste para os dois caminhos.

A etapa atual continua na lista, desabilitada e marcada "está aqui": tirá-la mudaria as posições
conforme a coluna de origem, e o dedo erraria por decorar o lugar errado.

**O subtítulo da tela mudou com o ponteiro.** Ele prometia "Arraste os cards entre as colunas" —
descrevendo um gesto que não funciona em toque. No celular ele agora diz o que funciona.

O DES-4 deixou pendente "avisar na tela que arrastar não funciona em toque". **Um botão visível no
card responde melhor que um aviso**: em vez de contar por que o gesto falha, oferece o que
funciona. A pendência fecha por substituição.

---

## 6. Verificação

```
ng build                 sem erro, sem warning novo
npm test -- --no-watch   299 SUCCESS   (eram 298 antes do bloco; +1 = a tela /mais)
npm run test:celular      50 SUCCESS
```

A linha de base foi conferida antes de qualquer alteração: **319 testes, todos verdes**. O vermelho
pré-existente que o plano registrava como risco não existia mais.

| critério | onde é afirmado |
|---|---|
| 3 · o teste da caixa falha antes e passa depois | §2 |
| 4 · nenhuma tela transborda em 390px | `paginas.celular.spec.ts`, 22 telas |
| 5 · campo ≥ 16px | `design-system.celular.spec.ts` |
| 6 · alvo ≥ 44px | idem, via `elementFromPoint` — mede o dedo, não o CSS |
| 7 · modal com topo alcançável | idem |
| 8 · gráfico responde a toque | `graficos.celular.spec.ts` |
| 9 · barra igual para todo papel | `shell.celular.spec.ts` |
| 10 · snap e mover sem arrastar | `funil.celular.spec.ts` |
| 12 · chip acima do compositor | `thread.celular.spec.ts` |

---

## 7. Pendências

**O critério 11 não foi verificado.** "Compositor visível com o teclado virtual aberto" não tem
como ser automatizado: o teclado é do sistema operacional e o headless não o abre. O que existe é a
condição necessária — `100dvh` na cadeia do shell, e teste afirmando que o compositor fica no
rodapé com as mensagens acima. **Falta abrir num aparelho de verdade e conferir.** É o único item
do critério de pronto que este bloco não fecha.

**Nenhuma media query abaixo de ~489px é testável** enquanto o piso do Chrome existir. O produto
não tem nenhuma, e isso agora é uma restrição consciente e não um acaso — quem escrever uma vai
precisar de outro caminho de verificação.

**A rolagem horizontal do funil não tem teste de gesto.** O snap é afirmado pelo CSS computado, não
por um arrasto real — pelo mesmo motivo que o DES-4 registrou: karma não arrasta nada. O gesto
continua sendo verificação manual.

**O "Mais" do vendedor tem três linhas** (Dashboard, Relatórios, Conta) mais o Sair. Funciona, mas
é uma tela inteira para pouca coisa. Se incomodar, a alternativa é a barra mudar por papel — e aí
são dois desenhos para manter, o que este bloco recusou de propósito.
