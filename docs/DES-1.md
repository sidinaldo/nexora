# DES-1 — Revisão de layout e design

Nenhuma mudança de API, DTO ou regra de negócio. Só template, CSS e o cálculo de largura de uma
barra.

---

## Parte 1 — Os três problemas estruturais

### 1.1 O menu rolava junto com a página

A causa era `.app { min-height: 100vh }` sem nenhum container de rolagem: o documento inteiro
rolava, e a barra lateral ia junto.

| onde | antes | depois |
|---|---|---|
| `html, body` | rolagem do documento | `overflow: hidden` |
| `.app` | `min-height: 100vh` | `height: 100dvh; overflow: hidden` |
| `.lateral` | rolava com a página | `height: 100%; overflow-y: auto` |
| `main` | `flex: 1` | `+ min-height: 0; overflow: hidden` |
| **`.conteudo`** | não existia | **`flex: 1; min-height: 0; overflow-y: auto`** |

`.conteudo` é a **única** área que rola no painel. Os banners de WhatsApp desconectado e de troca
de número ficaram **fora** dela — o de desconexão existe para ser impossível de ignorar, e rolar
junto o faria sair da tela.

`100dvh` e não `100vh`: no celular o `vh` mede a janela com a barra do navegador escondida, então
o rodapé fica cortado enquanto a barra está visível.

`overscroll-behavior: contain` na lateral, no conteúdo e nas colunas do kanban: sem ele, chegar ao
fim de um painel transfere a rolagem para o container de trás e a tela "pula".

#### A âncora da thread

**Ela não observava o scroll do documento** — sempre usou `this.threadEl.nativeElement`, o próprio
container. Nenhuma linha de `nucleo/thread/thread.ts` mudou.

O risco era o inverso do que o prompt antecipou: se o container perdesse a altura limitada, ele
cresceria com o conteúdo, `scrollHeight` passaria a ser igual a `clientHeight` e a âncora viraria
um **no-op silencioso** — o chip "nova mensagem" pararia de aparecer e "carregar anteriores"
deixaria de compensar a posição, sem erro nenhum.

A cadeia de altura foi fechada nos dois lugares onde a thread aparece:

- **`/caixa`** — `:host { display: block; height: 100% }` (o `.caixa` pedia `height: 100%` sem ter
  contra o que resolver);
- **`/contatos/:id`** — o mesmo, mais `:host { height: auto }` abaixo de 1100px, onde o layout
  empilha e a conversa passa a ter altura fixa (`min(560px, 70dvh)`).

Um teste novo trava isso: `layout.spec.ts` verifica que o `router-outlet` está **dentro** de
`.conteudo`. Confirmado por mutação — tirando o outlet de lá, o teste reprova com a mensagem
"o outlet saiu de dentro de .conteudo — a página inteira volta a rolar".

### 1.2 Margem excessiva à direita

Cada tela declarava o próprio teto: 1180px no dashboard, 760px nas configurações, 800px nas
etapas — todos **colados à esquerda**, sem `margin: auto`. Num monitor de 1920px sobravam ~700px
mortos à direita.

`.pagina` virou global: `max-width: 1520px; margin-inline: auto; padding: 22px 28px 28px`.

As telas que precisam de medida de leitura mantêm teto **próprio e centralizado**:

| tela | teto | por quê |
|---|---|---|
| `/dashboard`, `/contatos`, `/contatos/:id`, `/equipe` | 1520 | dado tabular e cartões |
| `/meu-dia` | 1100 | lista de trabalho em coluna única |
| `/formularios` | 960 | blocos de código |
| `/configuracoes`, `/etapas`, `/conexao` | 860–900 | formulário |
| `/conta` | 560 | formulário curto |
| `/comecar` | 620 | checklist |

**A grade de duas colunas era o outro sintoma.** `.colunas { align-items: start }` fazia "De onde
vêm seus leads" terminar bem acima do funil, deixando o buraco branco da captura. Removido — o
grid volta ao `stretch` padrão, e o cartão mais curto estica.

### 1.3 Funil sem limite de rolagem

Já havia rolagem horizontal no quadro, rolagem vertical por coluna, contagem "50 de 312" e
carregamento por cursor com botão. **O que faltava era a altura ser limitada** — sem a cadeia da
parte 1.1, as colunas cresciam e a página rolava junto.

Fechado: `:host { height: 100% }`, `.coluna { max-height: 100%; min-height: 0 }`,
`.coluna-topo { flex: 0 0 auto }` (o cabeçalho fica), `.quadro { overflow-y: hidden }`.

O layout **não assume cinco colunas**: `@for` sobre o que a API devolver, largura fixa por coluna
e rolagem horizontal a partir do que não couber.

### 1.4 Paginação — 20 por página

Componente único em `nucleo/paginacao/paginacao.ts`: `Paginacao` (o controle), `POR_PAGINA`,
`fatiar`, `totalDePaginas`, `linhasFantasma`, `rolarParaTopoDaTabela`.

| tela | antes | depois |
|---|---|---|
| `/contatos` | 30, servidor | **20, servidor** |
| `/equipe` | carregava tudo | 20, cliente |
| `/configuracoes` (feriados) | carregava tudo | 20, cliente |
| `/contatos/:id` (lembretes) | carregava tudo | 20 nos **concluídos** |

O controle mostra "Página 2 de 14 · 276 contatos", com primeira/anterior/próxima/última
desabilitados nos extremos.

**A altura da tabela não muda entre páginas.** `linhasFantasma` completa a última página com
`<tr class="linha-fantasma">` até 20 linhas. Sem isso, uma última página de 3 linhas encolhe a
tabela ~400px, o controle sobe e o botão "próxima" sai de baixo do cursor.

**Trocar de página rola para o topo da tabela**, não da janela — rolar a janela faria a pessoa
perder de vista o filtro que acabou de aplicar.

**Filtro e busca voltam para a página 1.** Já valia para etapa, responsável e busca; `trocarOrigem`
não zerava e passou a zerar.

**As listas por cursor não foram convertidas**: caixa de entrada, thread e colunas do kanban
continuam com `PaginaCursor<T>`.

#### Decisões que valem registro

- **Só os lembretes CONCLUÍDOS paginam.** Os pendentes aparecem inteiros: são a lista acionável, e
  empurrar um follow-up de hoje para a página 2 esconderia o que precisa de ação. Eles também não
  crescem sem limite — o teto diário do motor os segura. Os concluídos acumulam para sempre.
- **Equipe e feriados paginam no CLIENTE.** `GET /api/equipe` e `GET /api/feriados` devolvem o
  array inteiro e não aceitam página nem tamanho. Este bloco não muda API. **Pendência
  registrada:** se a equipe passar de algumas centenas, o recorte precisa subir para o servidor.

---

## Parte 2 — Por tela

| rota | o que mudou |
|---|---|
| `/entrar`, `/esqueci`, `/convite/:token`, `/redefinir/:token` | os quatro tinham o mesmo bloco `.tela` duplicado com `100vh`. Viraram `.tela-centro` global, com `100dvh` e rolagem própria |
| `/caixa` | `:host` com altura; as três áreas rolam independentes |
| `/dashboard` | teto de 1180→1520; cartões da mesma linha com altura igual; funil e rosca com período declarado; barra do funil proporcional |
| `/meu-dia` | teto 860→1100, centralizado |
| `/funil` | `.pagina-cheia`; coluna com teto de altura e cabeçalho fixo |
| `/contatos` | 20 por página, controle novo, linhas-fantasma, `.tabela-rolagem`, estado vazio que distingue "sem filtro" de "sem dado" |
| `/contatos/:id` | `:host` com altura (âncora da thread); lembretes concluídos paginados; empilhamento abaixo de 1100px revisto |
| `/conta` | teto 520→560, centralizado |
| `/equipe` | paginação; `.tabela-rolagem`; estado vazio orientando a convidar |
| `/conexao` | centralizado; em ≤520px os 4 contadores viram 2 colunas e o código de pareamento reduz de 30px para 24px — a 380px ele transbordava |
| `/configuracoes` | teto 760→860; feriados paginados; estado vazio explicando que os nacionais já vêm prontos |
| `/etapas` | teto 800→900; corrigido o `NG8107` que eu havia introduzido no ARQ-1 |
| `/formularios` | teto 860→960 |
| `/comecar` | mantido em 620, já centralizado |

### Celular — medido, não estimado

`paginas.render.spec.ts` monta **cada uma das 18 telas** dentro de uma caixa de 380px e mede
`scrollWidth - clientWidth`. Passar de 1px reprova.

Confirmado por mutação: um bloco de `min-width: 900px` na tela de contatos fez o teste reprovar
com "Contatos passa 548px da largura de 380px".

Tabela larga rola **dentro do container** (`.tabela-rolagem`, `min-width: 640px` na tabela), nunca
na página. As duas tabelas do painel (contatos e equipe) estão cobertas.

---

## Parte 3 — Os dois problemas de leitura

### Período declarado

O KPI dizia "Vendas do mês: 24" e o funil, "Venda: 100". Recortes diferentes, nada na tela
dizendo isso.

- **Funil de Vendas** → `situação agora · a etapa de venda é o acumulado`
- **De onde vêm seus leads** → `todos os contatos, desde o início`

Os KPIs já traziam o recorte no rodapé ("soma dos fechados no mês", "dos negócios encerrados no
mês").

### Largura das faixas — decisão: **PROPORCIONAL**

O cálculo era:

```ts
Math.round(28 + (Math.min(100, contatos / f[0].contatos * 100) / 100) * 72)
```

Isso é uma função **afim**, não uma proporção. Duas consequências:

1. Uma etapa com 3 contatos num funil de 162 desenhava **29% da largura** — quase um terço do topo
   — enquanto o número ao lado dizia 3. A pessoa lê a barra antes do número, e a barra mentia.
2. A base era a **primeira** etapa. A de ganho acumula as vendas de todos os meses e passa o topo
   com frequência; o `Math.min(100, …)` a cortava, e ela desenhava 100% qualquer que fosse o
   excesso — outra forma de mentir sobre a proporção.

Era exatamente a mistura que o prompt descreve: proporcional em parte, decorativo no piso e no
teto.

**Agora:** `largura = contatos / maior_contagem × 100`. Sem piso, sem teto.

O piso de 28% existia porque o nome da etapa ficava **dentro** da barra e sumia quando ela era
fina. A correção foi tirar o nome de dentro:

```
[ nome da etapa ] [ trilho com a barra proporcional ] [ contagem ]
                                                      [ valor    ]
```

O trilho em `--creme-2` dá a referência de 100% — sem ele, uma barra de 12% não teria contra o que
ser lida. A grade é `minmax(84px, 22%) 1fr auto`, então funciona com 3 ou 11 etapas.

O degradê verde por posição continua: ele é derivado do índice, não da cor cadastrada da etapa, e
se distribui sobre quantas existirem.

---

## Critério de pronto

| # | item | estado |
|---|---|---|
| 1 | `ng build` limpo, sem budget | ✅ — e o `NG8107` do ARQ-1 também saiu |
| 2 | `ng test` verde | ✅ **140** (eram 116; 18 de 380px + 6 de layout/paginação) |
| 3 | lateral fixa em todas as telas autenticadas | ✅ |
| 4 | nenhuma barra dupla | ✅ — `overflow` auditado arquivo a arquivo |
| 5 | sem faixa morta em 1920/1440/1280 | ⚠️ ver pendência 1 |
| 6 | coluna do kanban limitada, com cursor | ✅ |
| 7 | quadro rola horizontal com 8 etapas | ⚠️ ver pendência 2 |
| 8 | cartões lado a lado com altura igual | ✅ |
| 9 | âncora da thread nos três modos, nas duas telas | ⚠️ ver pendência 3 |
| 10 | telas usáveis em 380px | ✅ medido no navegador |
| 11 | toda tabela de 20 em 20, mesmo controle | ✅ |
| 12 | última página não muda a altura | ✅ |
| 13 | filtro volta para a página 1 | ✅ |
| 14 | caixa, thread e kanban continuam por cursor | ✅ |

---

## Pendências

1. **Os itens 5, 7 e 9 não foram vistos em navegador de verdade.** Estão corretos por construção e
   por teste automatizado, mas nenhuma captura foi conferida em 1920px, com 8 etapas configuradas,
   nem com o chip "nova mensagem" aparecendo. É o mesmo buraco que o `/funil` tem desde o começo —
   o arrastar-e-soltar nunca foi exercitado em navegador.

2. **`.tabela-rolagem` usa `min-width: 640px` fixo.** Funciona para as duas tabelas atuais. Uma
   tabela com mais colunas vai precisar do próprio valor.

3. **Equipe e feriados paginam no cliente** — a API devolve tudo. Registrado na parte 1.4.

4. **O carregamento por cursor do kanban continua só com botão**, sem disparo automático ao chegar
   perto do fim da coluna. O prompt aceitava as duas formas; o botão já existia e não foi trocado.

5. **`.linha-fantasma` fixa a altura da linha em 44px.** Se uma tabela tiver linha mais alta (duas
   linhas de texto numa célula), a última página vai encolher um pouco. Nenhuma tabela atual tem.

6. **O budget de estilo do `dashboard.css` continua em 6 kB**, com erro em 8 kB — decisão do PI-6,
   não alterada aqui. O arquivo cresceu com a reescrita do funil e encolheu com a remoção do
   `.faixa` antigo; segue dentro do limite.
