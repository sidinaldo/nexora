# MOB-4 — O desktop depois do MOB-2 e do MOB-3

Varredura de regressão. A varredura foi somente leitura; as correções propostas em §6 foram
aplicadas depois, com a lista fechada — o estado de cada uma está lá.

**Como foi verificado.** Leitura do diff completo dos dois blocos, inventário de toda regra CSS
nova classificada por limite de largura, e as duas suítes de teste. Não houve inspeção visual —
onde a conclusão depende de olhar a tela, está dito.

---

## 1. Veredicto

O desktop **não regrediu**. Nenhum layout empilhou, nenhuma tela ficou com espaço reservado para
uma barra que não existe, e as 299 asserções de desktop passam sem nenhuma ter sido afrouxada. O
que muda no desktop são três regras de modal e uma da thread, e as quatro são correções que valem
igual em qualquer largura. Onde dói é na **cobertura**: não existe teste que prove que a barra
inferior não aparece no desktop, ninguém testa a fronteira de 860/861px, e sobrou um teste medindo
um cenário que deixou de existir.

---

## 2. Tabela por tela

O mecanismo que sustenta a coluna "intacto" é único e vale para todas: cada peça de celular —
barra inferior, botão de voltar, indicador de etapas, "Mover para…" — está atrás de
`@if (ehCelular())`, que é `matchMedia('(max-width: 860px)')`. Em 861px ou mais o elemento **não
existe no DOM**, e o CSS dele não tem em que pegar.

| tela | desktop intacto | o que mudou | severidade |
|---|---|---|---|
| `/caixa` | sim | lista e thread lado a lado (`@if` verdadeiro nos dois painéis); chip da thread 12px acima do compositor em vez de 96px do rodapé do componente | — |
| `/funil` | sim | arrastar intocado; snap só em `pointer: coarse`; "Mover para…" ausente | — |
| `/dashboard` | sim | gráficos passaram a `pointermove`/`pointerdown` — o mouse dispara os dois | — |
| `/contatos` | sim | modal com teto de altura e rolagem no corpo | — |
| `/contatos/:id` | sim | empilha a 1100px como antes (`contato.css` não foi tocado); modal idem | — |
| `/equipe` | sim | modal idem | — |
| `/meu-dia` | sim | nada | — |
| `/relatorios` · `/conta` · `/conexao` · `/configuracoes` · `/etapas` · `/captacao` · `/integracoes` · `/comecar` | sim | nada | — |
| `/entrar` · `/esqueci` · `/convite/:token` · `/redefinir/:token` | sim | nada — ficam fora do shell | — |
| `/mais` | n/a | tela nova; no desktop nada aponta para ela, mas a URL responde | baixa |

**Nenhuma tela tem `padding-bottom` reservando espaço de barra.** Confirmado por busca: o único
`padding-bottom` novo é o `env(safe-area-inset-bottom)` da própria barra.

---

## 3. Regressões encontradas

**Nenhuma.**

Foram procuradas as oito da lista de risco. As quatro que não são vácuo:

**Modal com altura máxima** — a mudança vale em qualquer largura, e foi verificada contra o caso
"modal curto ocupando altura demais": `.overlay` passou a `align-items: flex-start`, e quem
centraliza agora é `margin-block: auto` no `.modal`. Margem automática no eixo transversal absorve
o espaço livre e vence `align-items` — então o modal continua centralizado quando cabe, e só encosta
no topo quando não cabe. É o mesmo par que o `.tela-centro` usa desde as telas públicas.
`max-height` só passa a valer quando o conteúdo excede a janela, e `overflow-y: auto` não desenha
barra de rolagem em conteúdo que cabe.

Um detalhe foi conferido e está certo: com `.modal` virando flex em coluna, o cabeçalho
(`.cartao-topo`) poderia ser espremido pelo corpo. Não é: o tamanho mínimo automático de item flex
(`min-height: auto`) o protege, porque ele não é container de rolagem. Quem encolhe é o
`.modal-corpo`, que tem `min-height: 0` de propósito.

**Gráficos com eventos de ponteiro** — `PointerEvent` estende `MouseEvent`, e o mouse dispara
`pointermove`/`pointerdown`/`pointerleave` normalmente. O corpo de `mover()` não mudou. O
`touch-action: pan-y` só governa entrada por toque.

**Scroll snap no funil** — está dentro de `@media (pointer: coarse)`. Num desktop com mouse a
regra não casa (ver §4 sobre o que `pointer: coarse` significa de fato), e o quadro rola solto como
antes. A área de soltura do DES-4 não foi tocada.

**`100dvh` no esqueleto** — `.app { height: 100dvh }` é do DES-3 e não foi alterado. O único `dvh`
novo é o teto do modal, e no desktop `dvh` e `vh` valem o mesmo, porque não há barra de navegador
que apareça e suma.

---

## 4. Regras de CSS sem limite de largura

Lista completa das regras novas ou alteradas que **não** estão dentro de um `@media` de largura.

### 4.1 Afetam o desktop de verdade — e são intencionais

| arquivo:linha | regra | efeito no desktop |
|---|---|---|
| `styles.css:500` | `.overlay` — `align-items: flex-start`, `overflow-y: auto` | modal continua centralizado; encosta no topo só quando não cabe |
| `styles.css:505` | `.modal` — `margin-block: auto`, `max-height: calc(100dvh - 40px)`, `display: flex; flex-direction: column` | modal alto passa a rolar por dentro em vez de ser cortado |
| `styles.css:515` | `.modal-corpo` — `flex: 1 1 auto; min-height: 0; overflow-y: auto` | idem |
| `thread.css:11` | `.thread-area` (novo invólucro) | chip "nova mensagem" passa a 12px acima do compositor, em vez de 96px acima do rodapé do componente |

As quatro estão em §7 — são melhorias que não deveriam ser revertidas.

### 4.2 Sem efeito no desktop, mas sem limite escrito

Estas só não vazavam porque o **elemento** não é renderizado acima de 860px. A proteção era um
`@if` em TypeScript, não uma media query.

| arquivo | regra | depois da correção |
|---|---|---|
| `shell.css` | `.barra-inferior` e as oito regras filhas | ✅ dentro de `@media (max-width: 860px)` (correção 5) |
| `caixa.css` | `.voltar-lista`, `.voltar-lista:hover` | continua só com o `@if` |
| `funil.css` | `.indicador-etapas`, `.indicador-etapas .mono` | continua só com o `@if` |
| `funil.css` | `.link-editar.mover` | continua só com o `@if` |
| `funil.css` | `.lista-etapas`, `.etapa-alvo` e variantes, `.ponto-etapa` | continua só com o `@if` — agora um `@if` direto (correção 4) |

**O que isso custava: nada hoje.** O que custava amanhã: quem removesse um `@if` — ou renderizasse
`<nav class="barra-inferior">` em outro lugar — levaria a barra para o desktop sem que nenhuma media
query o impedisse, e sem teste que acusasse. As duas pontas foram fechadas para a barra: o `@media`
(5) e o teste de que ela não existe no desktop (1).

As sete que sobram são de elementos menores, dentro de telas que já se comportam. Ficam registradas
porque a lista foi pedida completa — e porque a diferença entre "não vaza" e "não pode vazar"
continua sendo um `@if` que alguém pode mexer.

O caso do `.etapa-alvo` era o mais frouxo: o menu de etapas era `@if (menuMover())`, sem
`ehCelular()` junto — inalcançável no desktop só porque o único botão que preenche `menuMover()` é
de ponteiro grosso. Uma cadeia de dois passos, não uma condição. A correção 4 trocou a cadeia por
uma guarda direta.

### 4.3 `@media (pointer: coarse)` — sem largura, e isso é deliberado

| arquivo:linha | regra |
|---|---|
| `styles.css:577` | `.aba, .btn, .btn-pequeno { min-height: 44px }` |
| `styles.css:581` | `.link-editar { min-height: 44px; inline-flex }` |
| `shell.css:341` | `.lateral nav a`, `.primeiros-passos` (padding de 10px) |
| `caixa.css:105` | `.voltar-lista` (área de toque) |
| `funil.css:196` | `.quadro`, `.coluna` (scroll snap) |

⚠️ **`pointer: coarse` descreve o ponteiro PRIMÁRIO, não a existência de toque.** Um notebook com
tela sensível e trackpad reporta `pointer: fine` — as regras não valem ali. Quem casa é aparelho
cujo ponteiro principal é o dedo: celular, tablet, e um monitor de toque sem mouse.

Isso quer dizer que **em desktop normal nada incha**, e num tablet de 1024px em paisagem os alvos
crescem — que é exatamente o que o DES-3 queria quando escreveu "um tablet largo continua sendo
dedo". Continuidade da decisão, não vazamento. Fica registrado porque a lista foi pedida completa,
e porque é a única categoria em que uma tela larga muda de aparência.

### 4.4 O que foi verificado e está limpo

- **Nenhum `!important` novo** em nenhum arquivo.
- **Nenhum token de cor novo.** A barra usa `--branco`, `--linha`, `--verde`, `--texto-fraco` e
  `--urgencia-media`; os ícones herdam `currentColor`.
- **Nenhuma regra de desktop declarada antes de uma mobile que a sobrescreva.** O caso do MOB-2 —
  `@media (pointer: coarse)` anulado por `@media (max-width: 860px)` que vinha depois — foi
  corrigido movendo o bloco para o fim do `shell.css`, e não se repetiu.
- **`px` fixo onde havia unidade relativa:** não houve. Os `px` novos (58px de barra, 44px de alvo,
  22px de ícone) são medidas de toque, que são absolutas por natureza.

---

## 5. Cobertura de teste

### O que continua rodando

Os testes de desktop **não foram substituídos**. A conta fecha exatamente:

```
319  linha de base, antes dos dois blocos
-20  medições de transbordo a 380px, MOVIDAS para a execução de celular
 -1  a guarda da lista de isenção, que perdeu o objeto
 +1  a tela /mais entrando no inventário
───
299  desktop hoje    +    55 em celular
```

Nenhum teste foi afrouxado para o desktop passar. Em `caixa.spec.ts` e `design-system.spec.ts` só
os mocks de `ActivatedRoute` mudaram — nenhum `it` saiu, nenhuma asserção mudou.

As 22 telas são montadas e desenhadas nas **duas** execuções: `paginas.render.spec.ts` no desktop e
`paginas.celular.spec.ts` em celular.

### O que se perdeu

**Nada que fosse verdade.** As 20 medições movidas afirmavam "esta tela não transborda em 380px" e
mediam, por confissão do próprio arquivo, o layout de **desktop** espremido em 380px, porque a
janela do karma era 1440px. Elas passaram a rodar numa janela abaixo do ponto de quebra, onde o
layout medido é o que o celular renderiza. É a mesma asserção sobre um objeto diferente — e o
objeto certo.

O efeito colateral: **o desktop deixou de ter qualquer medição em largura estreita.** Não é perda
de cobertura real (o que se media era ficção), mas é uma capacidade que sumiu — hoje nada mede o
layout de desktop em container apertado.

### Os três buracos

**1 · Não existe teste de que a barra inferior não aparece no desktop.** Procurado: nenhuma
referência a `.barra-inferior` fora dos specs de celular. É justamente a asserção que o enunciado
pede e a que fecharia a §4.2 inteira — hoje o desktop depende de um `@if` que nada exercita.

**2 · Ninguém testa a fronteira 860/861px.** As duas execuções rodam em ~489px e 1440px. O ponto de
quebra fica no meio, sem nenhuma asserção — e é onde erro de `min-width` contra `max-width`
aparece. Agrava: o número **860 está escrito em quatro lugares independentes** —
`viewport.ts:QUEBRA_CELULAR`, `styles.css`, `shell.css` e `caixa.css`. Se um mudar sozinho, o
TypeScript e o CSS discordam **só na fronteira**: a barra aparece com a lateral ainda montada, ou a
caixa mostra uma vista com o CSS da outra.

**3 · `lateral.spec.ts:326` mede um cenário que deixou de existir.** O teste "EM 380px A BARRA NÃO
EMPURRA A PÁGINA DE LADO" renderiza a barra lateral numa caixa de 380px, com a janela em 1440px.
Ele passa — e não significa mais nada: em 380px de verdade a lateral não é renderizada. É o mesmo
defeito que a auditoria encontrou em `paginas.render.spec.ts`, sobrevivendo num arquivo que não foi
revisado.

---

## 6. Correções propostas

Em ordem de prioridade. Nenhuma era urgente — não havia nada quebrado. **As cinco foram
aplicadas**, depois de a varredura terminar.

**1 · Teste de que a barra não existe no desktop.** ✅ `layout.spec.ts` — `A BARRA INFERIOR NÃO EXISTE NO DESKTOP`, com a asserção de que a lateral continua lá (senão o teste passaria com as duas navegações sumindo). Uma asserção em `paginas.render.spec.ts`, onde
o `Shell` já é montado: `querySelector('.barra-inferior')` tem que ser nulo. Fecha o risco de toda
a §4.2 de uma vez e custa três linhas.

**2 · Um ponto de quebra, uma fonte.** ✅ `layout.spec.ts` — `O PONTO DE QUEBRA DO CSS É O MESMO DO TYPESCRIPT`. Ele lê a condição do `@media` que embrulha a barra na folha JÁ CARREGADA (`CSSMediaRule.conditionText`) e compara com `QUEBRA_CELULAR`. Não é leitura de arquivo: é o que o navegador entendeu. Falha também quando NENHUM `@media` embrulha a barra. `QUEBRA_CELULAR` já existe em `viewport.ts`. As três folhas
podem consumi-lo por variável CSS declarada no `:root`, ou — mais simples e sem custo de runtime —
um teste que leia o valor do TypeScript e afirme que `matchMedia` concorda com ele. O objetivo não
é elegância: é que a divergência apareça em teste, e não na fronteira, num aparelho.

**3 · Aposentar `lateral.spec.ts:326`.** ✅ Removido, com um comentário no lugar dizendo o que ele media, por que deixou de valer, e onde estão os dois testes que hoje afirmam a verdade — um em cada largura. Ou remover, ou reescrever no spec de celular afirmando o
que hoje é verdade — que em 380px a lateral **não** está no DOM. Do jeito que está, é uma asserção
verde sobre um cenário impossível, e isso é pior que não ter teste: dá confiança que não existe.

**4 · Fechar o `@if` do menu de etapas.** ✅ `@if (ehCelular() && menuMover(); as card)`. `@if (menuMover())` → `@if (ehCelular() && menuMover())`.
Hoje a proteção é a cadeia "o único botão que abre o menu é mobile", que é verdade e é frágil.

**5 · Amarrar a barra ao ponto de quebra também no CSS.** ✅ As nove regras de `.barra-inferior` foram para dentro de `@media (max-width: 860px)`, junto com o `.app { flex-direction: column }` — que existe por causa da barra e estava num bloco idêntico e adjacente. Envolver o bloco `.barra-inferior*` num
`@media (max-width: 860px)`. Redundante com o `@if` — e é o ponto: duas travas em vez de uma, para
o dia em que alguém mexer no template.

---

## 7. Diferenças intencionais

O que mudou no desktop de propósito e deve ficar.

**O modal alto agora rola por dentro em vez de ser cortado.** Vale em qualquer largura porque o
defeito também valia: numa janela de notebook baixa, ou redimensionada, o modal de cadastro de
contato perdia o topo e o rodapé sem rolagem — o botão de confirmar deixava de existir. A correção
é a mesma que as telas públicas já usavam.

**O chip "nova mensagem" mudou de altura no desktop.** Estava a 96px do rodapé do componente, um
número que era a altura do compositor de desktop escrita à mão. Passou a se apoiar no fim da área
de mensagens. No desktop ele desce alguns pixels; em compensação para de errar o lugar quando o
compositor cresce — anexo em prévia, gravação em curso, campo com cinco linhas.

**Os alvos de 44px valem em tablet largo.** Não é vazamento: `pointer: coarse` foi escolhido no
DES-3 exatamente para separar dedo de mouse sem depender de largura, com o argumento de que "um
tablet largo continua sendo dedo". Os controles novos seguem a mesma regra.

**Redimensionar a janela do desktop abaixo de 861px troca para o layout de celular** — lateral sai,
barra inferior entra, caixa vira uma vista de cada vez. É `matchMedia` com listener, e é o
comportamento correto: quem estreita a janela até largura de celular recebe o layout de celular.

**A rota `/mais` responde no desktop.** Nada aponta para ela lá — a lateral mostra os treze
destinos de uma vez —, mas a URL funciona. Deixar a rota sem `guardaDono` também é deliberado: a
tela mostra a lista que o papel permite, e o enforcement de cada destino continua no guard da rota
dele.

---

## 8. Depois das correções

```
ng build                 sem erro, sem warning novo
npm test -- --no-watch   300 SUCCESS   (299 + 2 novos − 1 obsoleto removido)
npm run test:celular      55 SUCCESS   (inalterado)
```

Os três buracos de cobertura da §5 estão fechados. As dezesseis regras da §4.2 continuam sem
limite de largura — e agora não precisam mais dele para a barra, que ganhou o `@media`. As de
`.voltar-lista`, `.indicador-etapas`, `.link-editar.mover` e `.etapa-alvo` seguem protegidas só
pelo `@if`, e a #4 tirou a mais frouxa delas da cadeia de dois passos.

O que não mudou: nenhuma regressão foi encontrada, então nada foi revertido. As quatro diferenças
intencionais da §7 continuam valendo.
