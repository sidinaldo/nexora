# NAV-1 — Página de Captação e reorganização do menu

## 1. O que existia, conferido no código

O prompt pedia para verificar antes de mexer. As três respostas:

| pergunta | resposta |
|---|---|
| `paginas/formularios/` existe? Está roteada? | **Sim**, rota `/formularios` com `guardaDono` (INT-1) |
| Existe alguma tela de QR ou canal? | **Sim**, `paginas/canais/`, rota `/canais` com `guardaDono` (INT-2) |
| O menu tem seção CONFIGURAÇÃO? | **Sim**, `<div class="separador">Configuração</div>`, com cinco itens |
| `paginas/em-breve/` ainda é órfão? | **Não existe mais** — já havia sido removido |

As duas telas existiam. Nada ficou pendente por não existir, e **nada de INT-1 ou INT-2 foi
implementado aqui**: as duas viraram painéis das abas, com o comportamento que já tinham.

Menu antes:

```
CONFIGURAÇÃO
  Equipe · Conexão · Etapas do funil · Formulários do site · QR Code e links · Configurações
```

---

## 2. O que foi movido

**`/formularios` e `/canais` deixaram de ser telas e viraram as duas abas de `/captacao`.**

Os componentes **não foram reescritos**. Cada um perdeu exatamente o cabeçalho de página —
`.pagina`, `<h1>` e o subtítulo — porque quem desenha isso agora é o container; manter os dois
daria dois títulos na mesma tela. Todo o resto (criar, editar, ativar, o painel de código, o QR,
a confirmação de remoção) é o mesmo código, com os mesmos testes.

### Cada painel continua funcionando sozinho

Eles buscam a própria lista. Não recebem dados por `input`, e a decisão é deliberada: transformá-los
em componentes que dependem do pai custaria a capacidade de montá-los e testá-los isoladamente —
que é como `formularios.spec.ts` e `canais.spec.ts` funcionam, e é o que mantém os dois no teste de
transbordo a 380px.

O que ganharam foi um `output`:

```ts
mudou = output<void>();
```

Emitido depois de **toda** escrita, por um método único (`aposEscrita()`) em vez de espalhado pelos
handlers. O container ouve e recalcula o resumo. Sem isso, criar um canal deixaria o número do topo
da tela desatualizado — e **resumo velho não parece defeito, parece número**.

---

## 3. A tela nova

`/captacao`, `guardaDono`, componente `paginas/captacao/`.

```
Captação                                    ← cabeçalho (h1 + sub)
┌──────────────────────────────────────┐
│ 100 leads   30 formulário (30%)  70 QR (70%) │  ← resumo comum, ACIMA das abas
└──────────────────────────────────────┘
[ Formulários do site ] [ QR Code e links ]  ← abas
┌──────────────────────────────────────┐
│  o painel da aba ativa                │
└──────────────────────────────────────┘
```

### O resumo é o que justifica juntar

Ele soma os **dois** canais e mostra a fatia de cada um. É a comparação que antes exigia abrir duas
telas e somar de cabeça — e é por isso que fica acima das abas: dentro de uma, viraria dois resumos
que ninguém consegue comparar.

O card do canal que está trazendo mais ganha borda verde. Sem destaque são três números do mesmo
tamanho, e o olho não conclui nada.

**Ele busca as duas listas**, mesmo com uma aba só aberta — é a única forma de comparar. São dois
GET de configuração, com no máximo algumas dezenas de linhas cada, numa tela que o dono abre de vez
em quando. Um erro em um dos dois não apaga o outro: há `catchError` por ramo, e teste para isso.

### ⚠️ "no período" e "taxa de código preservado"

O prompt pede "total de leads captados **no período**" e, na aba de QR, "**taxa de código
preservado**". Duas ressalvas, e nenhuma das duas foi contornada inventando número:

**Não há recorte por período.** `leads_recebidos` é um contador acumulado desde a criação de cada
item — não existe série temporal por canal, e criar uma seria mudança de API, que este bloco não
faz. A tela diz isso, literalmente: *"Total desde que cada um foi criado — não há recorte por
período aqui."*

**A taxa de preservação continua não sendo calculável**, pelo motivo já registrado no INT-2 §7: quem
hospeda o `wa.me` é a Meta, e um scan que perdeu o código é indistinguível de alguém que nunca
escaneou. Não há denominador. O resumo apresenta o número do QR como **piso**, e explica por quê no
lugar onde a pessoa está olhando. Há teste travando esse texto.

### A aba vem da URL

`/captacao?aba=qr`. É o que faz o link antigo do QR cair na aba certa. Trocar de aba usa
`replaceUrl` — trocar de aba não é navegação para o histórico, e sem isso o botão "voltar" do
navegador percorreria as abas antes de sair da tela.

Valor desconhecido no parâmetro cai na primeira aba, sem quebrar.

### Só a aba ativa fica montada

`@if`, não `hidden`. A aba fechada não fica com requisição pendente nem com um QR carregado na
memória (ele é um `blob:`). Trocar de aba recarrega, que é o comportamento certo numa tela de
gestão. Há teste garantindo que `app-canais` **não** existe no DOM enquanto a aba é Formulários.

---

## 4. Largura e tabelas

Captação é tela **densa**: `.pagina` sem o modificador `.formulario`. Ela não define largura
própria — o container é o do shell, como no DES-2. Entrou na lista `DENSAS` de `larguras.spec.ts`,
que mede as bordas de cada tela e exige o mesmo recuo e a mesma largura de todas as outras.

As duas listas viraram **tabelas** (`.tabela` dentro de `.tabela-rolagem.tabela-area`), com o
controle de paginação compartilhado, 20 por página:

- a altura estável é do **container** (`--altura-tabela`), não de linha vazia — a regra do DES-2
  continua valendo, e nenhuma linha em branco é desenhada para completar página;
- a edição em linha e o painel de código viraram `<tr>` com `colspan`, com fundo próprio para não
  parecerem registro;
- ao encolher a lista, a página volta para a última existente — sem isso a pessoa ficaria olhando
  para uma tabela vazia com o controle dizendo "página 2 de 1".

Com os tetos atuais (20 formulários e 30 canais por empresa) a paginação quase nunca dispara. Ela
existe pelo mesmo motivo das outras tabelas: o comportamento é o mesmo em toda tela do painel, e
ninguém precisa aprender de novo.

### `.grade-3` nasceu aqui

O `styles.css` dizia, sobre a família de grades: *"Quando aparecer a terceira coluna, a classe entra
junto com o uso."* O resumo de Captação compara três números — foi o uso. A classe entrou com os
`span-*` correspondentes e um ponto de quebra próprio: três colunas viram **uma** a 720px, sem o
passo intermediário de duas, porque com três itens duas colunas deixam um órfão sozinho na segunda
linha — e um card isolado lê como "este é diferente", que é o oposto do que um resumo comparativo
pode sugerir.

---

## 5. Menu

```
Dashboard · Caixa de Entrada (badge) · Funil · Contatos · Meu Dia

CONFIGURAÇÃO
  Equipe · Conexão · Etapas do funil · Captação · Configurações
```

Dois itens viraram um. **Não há item de "Integrações"**: o webhook de saída (INT-3) não existe, e
item de menu para funcionalidade inexistente é a forma mais barata de mentir sobre o produto — o
cliente clica, não encontra nada, e passa a duvidar do resto. `navegacao.spec.ts` trava a ausência.

---

## 6. Rotas antigas

| antes | agora |
|---|---|
| `/formularios` | → `/captacao` |
| `/canais` | → `/captacao?aba=qr` |

O redirecionamento é uma **função** (`redirectTo: () => inject(Router).parseUrl(...)`), não uma
string: `redirectTo` em string não carrega query param, e sem o `?aba=qr` quem salvou o link do QR
chegaria na aba de formulários. Isso é pior que um 404 — a pessoa não percebe que está no lugar
errado.

---

## 7. Componentes removidos

**Nenhum.** Os dois que saíram do menu continuam vivos como painéis, e as rotas antigas
redirecionam. `paginas/em-breve/`, o órfão citado no prompt, já não existia.

O que o `navegacao.spec.ts` garante daqui para a frente:

- toda rota do painel ou carrega componente ou redireciona (rota morta reprova);
- `formularios` e `canais` **não** voltam a ter `loadComponent` — dois caminhos para a mesma coisa,
  um com cabeçalho e outro sem, é como um painel sem título acaba solto numa página.

---

## 8. Testes

**Frontend: 202 passando** (eram 181). Backend intocado — **nenhuma mudança de API ou de regra de
negócio** neste bloco.

| arquivo | o que mudou |
|---|---|
| `navegacao.spec.ts` | **novo**, 10 casos: os dois redirecionamentos com a aba certa, `/captacao` com guarda, nenhuma rota morta, os painéis fora do roteamento, a ordem do menu, a ausência de "Integrações", e o menu de quem não é dono |
| `captacao.spec.ts` | **novo**, 10 casos: o resumo somando os dois, zero sem `NaN`, o texto do "piso", só a aba ativa montada, a aba pela URL, parâmetro inválido, troca de aba, falha de uma lista sem apagar a outra, e `.pagina` sem `.formulario` |
| `larguras.spec.ts` | `/formularios` e `/canais` saíram de `FORMULARIOS`; `/captacao` entrou em `DENSAS` |
| `paginas.render.spec.ts` | `/captacao` entrou como tela; os dois viraram entradas de **painel** — é assim que a aba de QR continua no teste de 380px, já que dentro de Captação só a aba ativa renderiza |
| `design-system.spec.ts` | `/formularios` saiu da comparação de `.aba` e entrou `/captacao` |
| `canais.spec.ts` | os seletores de botão passaram a comparar sem caixa: as ações por linha viraram `link-editar`, escrito em minúscula |

### Um teste que passava sem provar nada

A comparação de `.aba` do `design-system.spec.ts` incluía `/formularios`. Só que as abas daquela
tela viviam **dentro** do painel de código, e só apareciam com um formulário criado e a chave
revelada — com a lista vazia (que é o que o payload genérico daquele arquivo produz), ela nunca
renderizava uma `.aba` e entrava na comparação **sem contribuir**. A asserção era
`toBeGreaterThan(1)`, então as duas telas restantes bastavam para passar.

Trocada por `/captacao`, cujas abas estão sempre na tela, e a asserção virou exata: a lista de
telas que renderizaram tem que ser **igual** à lista pedida. Com "maior que um", uma tela que
deixasse de renderizar a aba sumiria da comparação em silêncio.

---

## 9. Pendências

| O quê | Por quê |
|---|---|
| Item de menu "Integrações" | Só quando o webhook de saída (INT-3) existir |
| Recorte por período no resumo | Exigiria série temporal por canal na API — fora do escopo deste bloco |
| Taxa de código preservado | Não é calculável sem redirecionador próprio (INT-2 §7) |
| Nada verificado em navegador | Segue valendo o que os blocos anteriores registram: as medidas são as dos testes, não de um olho numa tela |

---

## 10. Arquivos

```
NOVOS
  src/app/paginas/captacao/{captacao.ts,captacao.html,captacao.css,captacao.spec.ts}
  src/app/navegacao.spec.ts

MOVIDOS DE TELA PARA PAINEL (mesmo componente, sem cabeçalho de página, lista → tabela)
  src/app/paginas/formularios/{formularios.ts,formularios.html,formularios.css}
  src/app/paginas/canais/{canais.ts,canais.html,canais.css}

NAVEGAÇÃO
  src/app/app.routes.ts            /captacao + redirecionamentos de /formularios e /canais
  src/app/layout/shell/shell.html  grupo CONFIGURAÇÃO

DESIGN SYSTEM
  src/styles.css                   .grade-3 (nasceu com o uso) + quebra própria a 720px

TESTES AJUSTADOS
  src/app/paginas/larguras.spec.ts
  src/app/paginas/paginas.render.spec.ts
  src/app/design-system.spec.ts
  src/app/paginas/canais/canais.spec.ts

DOCS
  docs/NAV-1.md
  docs/INVENTARIO-TECNICO.md       tabela de telas
```
