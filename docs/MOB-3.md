# MOB-3 — A barra inferior estava empilhada

Escopo: CSS e template da barra de navegação mobile. Nenhuma mudança de API, rota ou regra de
negócio.

---

## 1. A causa

Não foi herança. Foi **escopo**.

`shell.css` declarava, num seletor de **elemento**:

```css
nav { display: flex; flex-direction: column; gap: 1px; }
```

Essa regra foi escrita para a barra lateral, que é vertical. A barra inferior do MOB-2 também é um
`<nav>`, no mesmo template — e o CSS de componente do Angular não separa um do outro.

O detalhe que fechou o caso: `.barra-inferior` declarava `display: flex` e **nunca declarou
`flex-direction`**. Especificidade não resolve o que não está escrito — a classe vencia o `display`
e deixava a direção para o `nav`, que dizia `column`. Os cinco itens empilharam.

**Medido antes da correção**, pelo teste que faltava:

```
A BARRA É HORIZONTAL — cinco itens lado a lado, na mesma linha FAILED
    os itens estão em 5 linhas diferentes — a barra está empilhada: Expected 5 to be 1.

A BARRA NÃO PASSA DE 64px DE ALTURA FAILED
    a barra mede 285px — está comendo a tela: Expected 285 to be less than or equal 64.
```

285px de 844 é 34% da altura da tela, numa tela cujo conteúdo é lista de conversas.

### A mesma causa explicava o segundo defeito

O retângulo de fundo claro do item ativo — o que o enunciado descreve como confundível com estado
de toque — vinha de `nav a.ativo { background: rgba(255,255,255,.14) }`. Mesmo seletor solto, mesma
lateral, mesmo vazamento.

E `nav a { padding; font-size; color }` também alcançava a barra: o que ela não sobrescrevesse,
herdava do menu lateral.

### Por que o MOB-2 não pegou

A suíte de celular tinha um teste da barra que afirmava `min-height >= 56px` **por item**. Uma barra
empilhada satisfaz isso com folga: cada item ocupava a largura inteira e mais de 56px de altura.

O teste media a altura e nunca a **orientação**. Foi exatamente o alerta que a auditoria deixou —
regra sem teste é regra que quebra — aplicado a um teste que existia e afirmava a coisa errada.

---

## 2. O que foi corrigido

### O escopo, que é a raiz

`nav`, `nav a`, `nav a:hover`, `nav a.ativo` e o `nav a` do bloco de ponteiro grosso passaram a
`.lateral nav …`. `.ponto-status` foi partido: a geometria fica compartilhada, e o anel branco mais
o `margin-left: auto` — que só existem por causa do verde escuro da lateral — foram para
`.lateral .ponto-status`.

**Escopo e não um `flex-direction: row` por cima**: com a regra solta, o próximo `<nav>` do shell
herdaria o layout da lateral de novo. A barra ganhou `flex-direction: row` **explícito** assim
mesmo, como cinto: quem lê o arquivo vê a direção declarada onde ela importa.

### Orientação e altura

Cinco itens lado a lado, `flex: 1 1 0` cada — 78px numa tela de 390px. **58px de altura**, entre os
44px de alvo mínimo e o teto de 64 que a lista de conversas pode pagar. `env(safe-area-inset-bottom)`
somado ao padding inferior, para a faixa de gestos do iPhone não comer os rótulos.

### Ícone acima, rótulo abaixo

SVG traçado em `currentColor`, escrito no template. Cinco formas: agenda com visto, balão, funil,
pessoa, três pontos. Nenhuma biblioteca, nenhuma cor fora dos tokens — o ícone herda a cor do item e
muda junto com o estado ativo.

⚠️ **Isto reverte uma decisão do DES-3**, que registrou "sem ícones — não existe conjunto de ícones
no projeto, e inventar um agora seria desenhar treze símbolos que ninguém validou". A razão continua
válida para a **lateral**, que tem treze itens. Aqui são **cinco**, os cinco destinos mais usados do
produto, e barra inferior funciona por reconhecimento visual: o dedo vai para a forma, não para a
palavra. O compromisso é de outra ordem, e a lateral continua sem ícone.

### O item ativo

`color: var(--verde)` e peso 600 no ícone e no rótulo. Sem bloco de fundo. A barra passou de verde
escuro para `--branco` com borda superior `--linha`: sobre fundo claro, o verde da marca **é** a cor
de ação, e o destaque por cor passa a significar alguma coisa. Nenhum token novo.

### O badge

Estilo mantido — âmbar dos tokens, número em negrito. Só mudou de lugar: sobre o ícone, com anel de
`--branco` para se separar dele. Ao lado do rótulo ele empurrava o texto e desalinhava a coluna.
`pointer-events: none`, senão o toque no número não conta como toque no item.

O ponto de status da conexão foi para o mesmo lugar, no canto do ícone do "Mais".

---

## 3. ⚠️ A barra ficou em FLUXO, não `position: fixed`

O enunciado pede `position: fixed; bottom: 0` mais `padding-bottom` equivalente no container de
conteúdo, "para todas as telas". Fiz diferente, e o motivo é o próprio critério 3.

A barra é **irmã de `main`** num `.app` em coluna, com `flex: 0 0 auto`. Em fluxo ela **encolhe** a
área de conteúdo em vez de flutuar sobre ela — o mesmo mecanismo que a faixa de WhatsApp
desconectado já usa no topo desde o DES-3.

O que isso muda:

| | fixa + padding | em fluxo |
|---|---|---|
| conteúdo coberto | depende de 22 telas lembrarem de compensar | impossível por construção |
| tela nova | precisa reservar altura, ou nasce quebrada | não precisa de nada |
| altura da barra muda | os 22 `padding-bottom` mentem até alguém corrigir | acompanha sozinho |

E há uma trava a mais: no Safari do iPhone, `position: fixed` com teclado virtual aberto é um
problema conhecido — o viewport de layout não encolhe, e a barra fixa não fica onde deveria.

**Nada disso contraria o objetivo**: "nenhum conteúdo coberto pela barra, em nenhuma tela" fica
verdadeiro de forma mais forte, e sem 22 pontos de manutenção. Se ainda assim a preferência for
`fixed`, a mudança é pequena — mas aí o `padding-bottom` precisa entrar no `.conteudo` do shell,
que é o container único (DES-1/DES-2), e não tela por tela.

O teste `A BARRA NÃO COBRE O CONTEÚDO DE NENHUMA TELA` afirma isso com uma asserção só, e ela vale
para as 22 telas justamente porque o container de rolagem é um só.

---

## 4. A barra com o teclado aberto — a decisão

**Ela some enquanto o compositor da thread está em foco.**

Dois motivos:

- ela come 58px de uma tela que já perdeu metade da altura para o teclado, e o que sobra é a
  conversa — que é o que a pessoa está lendo enquanto responde;
- no Safari do iPhone o viewport de **layout** não encolhe quando o teclado abre (só o visual),
  então a barra fica **atrás** do teclado: continua ocupando altura e não dá para tocar nela. Pior
  dos dois mundos — invisível e cobrando espaço.

E navegar não é o que se faz digitando.

A regra vive no `styles.css` global, e não no `shell.css`:

```css
@media (max-width: 860px) {
  .app:has(.responder textarea:focus) .barra-inferior { display: none; }
}
```

`.responder` é de **outro componente** (`nucleo/thread`), e o CSS de componente do Angular é
encapsulado — de lá a regra não alcançaria. No global não há encapsulamento e o seletor atravessa.

`:has()` em vez de um sinal de foco: é estado puramente visual, e levá-lo da thread até o shell
exigiria um serviço para dizer o que o CSS já enxerga sozinho.

Escopo deliberadamente estreito — **só o compositor**. Focar um campo em `/contatos` não esconde a
navegação: ali o formulário rola dentro da própria tela e a barra não disputa espaço com nada.

---

## 5. As verificações pedidas em paralelo

**A faixa de WhatsApp desconectado continua impossível de ignorar.** Ela é do topo do `main`, a
barra é irmã de `main` no rodapé — não se encontram. Há teste afirmando que a faixa fica acima da
área de conteúdo, que a barra fica abaixo dela, e que **um único container rola** (`.conteudo`),
que é a regra do DES-1 contra rolagem dupla.

**O ponto de status da conexão tem sinal fora do "Mais".** Ele acende no canto do ícone quando o
estado não é `ok`, e o detalhe continua dentro, junto do link de Conexão. Há teste para os dois
lados: aparece quando cai, e **não** aparece quando está tudo bem — ponto sempre aceso vira ruído e
ninguém olha mais.

**O alvo é o item inteiro.** O teste mede os quatro cantos com `elementFromPoint`, 3px para dentro:
se algum não devolver o item, o alvo é menor do que parece e o toque perto da borda cai no vazio.

---

## 6. Verificação

```
ng build                 sem erro, sem warning novo
npm test -- --no-watch   299 SUCCESS
npm run test:celular      55 SUCCESS   (eram 50; +5 neste bloco)
```

| critério | onde é afirmado |
|---|---|
| 2 · horizontal, ≤ 64px, cinco lado a lado | `A BARRA É HORIZONTAL` + `NÃO PASSA DE 64px` |
| 3 · nenhum conteúdo coberto | `A BARRA NÃO COBRE O CONTEÚDO DE NENHUMA TELA` |
| 4 · compositor acima da barra | a barra some com o campo em foco (§4) + `thread.celular.spec.ts` |
| 6 · alvo de 44px no item inteiro | `O ALVO É O ITEM INTEIRO`, pelos quatro cantos |
| 9 · teste que prova a orientação | o mesmo do critério 2 — e ele reprovou antes da correção |

Os itens 5 (ícone e rótulo, ativo por cor), 7 (badge sobre o ícone) e 8 (área segura) são visuais e
estão descritos em §2; não viraram teste porque afirmar "existe um `<svg>`" ou "o `padding-bottom`
contém `env()`" travaria a implementação sem provar aparência nenhuma.

---

## 7. Pendências

**Continua faltando conferir num aparelho de verdade.** O `env(safe-area-inset-bottom)` vale zero em
qualquer navegador de teste, e o comportamento do teclado é do sistema operacional — o headless não
o abre. O que existe é a regra escrita e o teste de que ela dispara com o campo em foco. É a mesma
pendência que o MOB-2 registrou no critério 11, e ela não fecha por aqui.

**Os cinco ícones não foram validados com ninguém.** Funil e balão são convenções fortes; "agenda
com visto" para Meu Dia e "três pontos" para Mais são escolhas minhas. O rótulo embaixo cobre a
ambiguidade enquanto isso — e é por isso que ele fica, em vez de a barra virar só ícone.
