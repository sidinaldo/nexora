# DES-4 — Arrastar e soltar no funil

## A causa

**Era a nº 1 da lista do prompt, e só ela.** O `preventDefault` estava correto no `dragover`, o
fantasma não interceptava, e o contador de profundidade não existia porque não havia estado de
coluna para perder.

As zonas de soltura eram as tiras `.solta` **entre** os cards:

```html
<div class="solta" (dragover)="..." (drop)="aoSoltar($event, col, c.id)"></div>
```

Faixas de poucos pixels, uma antes de cada card. O `.coluna-corpo` — que já ocupa toda a altura da
coluna (`flex: 1`, com rolagem própria desde o DES-1) — **não escutava nada**.

Consequência: o espaço vazio abaixo dos cards, que numa coluna com dois cards é a maior parte dela,
não era zona de soltura. O `drop` não disparava, o card voltava sozinho, e **nada aparecia no
console**. Coluna vazia era o caso extremo: tinha só a tira do topo.

O comentário no template dizia que era de propósito — *"assim não é preciso adivinhar se caiu na
metade de cima ou de baixo"*. A intenção era boa; o efeito foi entregar uma faixa de acerto de
poucos pixels ao vendedor.

---

## O que foi corrigido

### O alvo é a coluna inteira

`dragenter`, `dragover`, `dragleave` e `drop` passaram para `.coluna-corpo`. As tiras continuam
existindo como **marcador** — mostram onde o card vai entrar — com `pointer-events: none`, porque
um marcador que intercepta o ponteiro se põe entre o cursor e a coluna, bem em cima do ponto onde
o card vai cair.

`min-height: 120px` no corpo: coluna vazia é justamente onde o vendedor mais tenta soltar, e na
sua captura "Proposta" e "Negociação" estavam em zero.

O card arrastado também recebeu `pointer-events: none` — sob o cursor, ele roubaria o `dragover`
da coluna.

### `preventDefault` no `dragover` **e** no `dragenter`

Faltando um, a área não é zona válida e o `drop` **nunca dispara**, em silêncio. Está comentado no
código como "não limpe isso", porque é contraintuitivo o bastante para alguém remover numa
refatoração — e o teste falha se removerem.

### Contador de profundidade

Cada card filho dispara `dragenter`/`dragleave` da coluna ao passar por cima. Tratando
ingenuamente, o destaque apaga no primeiro card e o estado se perde no meio do gesto. O contador
incrementa em `dragenter`, decrementa em `dragleave`, e o destaque vale enquanto for maior que
zero.

### Onde o card entra

Decidido pela **metade** do card: cursor acima do meio entra antes, abaixo entra depois. Soltar no
espaço vazio manda para o fim da coluna.

A leitura é do DOM, não do modelo — a pergunta é geométrica ("em que altura está o cursor"), e o
modelo não sabe responder isso.

**O cálculo de `ordem_kanban` não foi tocado.** O ponto médio com renormalização continua no
`ServicoFunil`; daqui sai só a posição.

### Rolagem durante o arrasto

Vertical na **coluna** (que tem rolagem própria) e horizontal no **quadro**, quando o cursor chega
a 56px de uma borda. Sem isso, mover um card para a última etapa exige rolar antes de arrastar — e
não dá: soltar para rolar cancela o gesto.

### Feedback visual

O destaque agora pinta **exatamente a área que aceita o card** (`inset` de 2px em verde sobre o
creme). Antes ele era sutil e não correspondia à zona real, o que ensinava o vendedor a soltar no
lugar errado.

---

## O que não mudou, e foi verificado que continua

- **Etapa de ganho** abre o modal de venda em vez de mover. A API recusa `mover` para etapa com
  `e_ganho`, e o card só sai do lugar depois de confirmado.
- **Conflito (409)** devolve o card e avisa, sem travar a tela.
- **Atualização otimista**: o card se move antes da resposta, e volta se a API recusar.
- **Nenhuma mudança de API.**

---

## Toque / celular — **fica de fora, e é registrado**

HTML5 drag-and-drop **não funciona em toque**. `dragstart` simplesmente não dispara: no celular,
o gesto de arrastar é interpretado como rolagem.

Fazer funcionar exige reimplementar em `pointerdown`/`pointermove`/`pointerup`, com atraso curto
antes de iniciar o arrasto para não conflitar com a rolagem — e a rolagem do quadro é horizontal,
que é exatamente o gesto que competiria. É trabalho de bloco próprio, não de ajuste.

**A alternativa já existe e funciona no celular:** abrir o contato e mudar a etapa pelo `select` do
bloco "Negociação". Dois toques, sem gesto.

O que **não** foi feito: dizer isso na tela. Hoje o vendedor no celular tenta arrastar, o card não
sai, e nada explica por quê — que é o mesmo modo de falha que este bloco corrigiu no desktop. Um
aviso no quadro em viewport de toque ("no celular, mude a etapa pelo contato") fecharia isso em
poucas linhas.

---

## Verificação

- `ng build` — limpo
- `ng test` — **266 passando** (9 novos)

O funil **não tinha teste nenhum** antes deste bloco.

| teste | o que trava |
|---|---|
| quem escuta é o corpo da coluna | e o marcador tem `pointer-events: none` |
| `dragover` **e** `dragenter` chamam `preventDefault` | remover um faz o `drop` sumir em silêncio |
| **coluna vazia aceita, pelo evento REAL do DOM** | ver abaixo |
| soltar no espaço vazio manda para o fim | o caso exato do relato |
| a metade do card decide antes/depois | três posições: topo, meio, fim |
| o destaque não pisca sobre os cards filhos | entra, entra de novo, sai uma vez — continua aceso |
| soltar na etapa de ganho abre o modal | e não emite `mover` |
| 409 devolve o card sem travar | |
| soltar onde já estava não vira requisição | |

### Uma mutação que reprovou os meus próprios testes

Removi o `(drop)` do template para conferir se os testes pegavam — e **passaram todos**. Porque
chamavam `c.aoSoltar(...)` direto no componente: provavam a função, não a **ligação**. E a ligação
era exatamente o defeito.

Um dos testes passou a disparar um `DragEvent` de verdade no `.coluna-corpo`. Com ele, a mesma
mutação falha.

Fica registrado como limite honesto: os outros oito continuam exercitando a mecânica com eventos
sintéticos, porque o karma não arrasta nada. **O gesto real nunca foi feito em navegador** — nem
antes nem agora.

---

## Pendências

- **Nunca testado com o mouse de verdade.** Os testes provam mecânica; o gesto contínuo
  (`dragstart` → `dragover` repetido → `drop`) depende de navegador e de você.
- **Toque não implementado** (acima), e sem aviso na tela.
- **Rolagem nas bordas é por evento**, não por temporizador: ela anda enquanto o `dragover`
  dispara. Parando o cursor na borda sem mexer, a rolagem para. Suficiente para alcançar uma
  coluna vizinha; para percorrer oito etapas, um temporizador seria mais suave.
- **8 etapas** (item 9 do critério) não foi exercitado: o teste monta três colunas. A lógica não
  tem nada por etapa que dependa da quantidade, mas isso é argumento, não medição.
