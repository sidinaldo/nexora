# DES-2 — Ajustes de layout e navegação

Uma rota nova de API (`GET /api/conversas/{id}`), autorizada pelo prompt. Nenhuma outra mudança
de API, DTO ou regra de negócio.

---

## 1. Linhas em branco na última página

**Estava:** a última página de `/contatos` mostrava 10 registros e mais 10 faixas vazias com
borda, para completar 20. Indistinguíveis de dez linhas que não carregaram.

**Origem:** o DES-1 pediu "a tabela não muda de altura entre páginas" e eu cumpri ao pé da letra,
com `linhasFantasma()` gerando `<tr>` vazios. Funcionava e estava errado.

**Agora:** a reserva é do CONTAINER.

```
antes:  <tbody> 10 linhas reais + 10 <tr class="linha-fantasma"> </tbody>
depois: <div class="tabela-area" style="--altura-tabela: 926px"> <tbody> 10 linhas </tbody> </div>
```

`linhasFantasma()` foi **removida**; entrou `alturaMinimaDaTabela()`. O espaço abaixo da última
linha é do container — sem borda, sem listra, sem parecer registro.

**Decisão sobre a altura mínima:** mantida, não solta. O rodapé de paginação subindo ~400px na
última página tira o botão "próxima" de baixo do cursor de quem está navegando rápido. A reserva
só vale a partir da segunda página: numa base de 3 contatos, esticar a área para 20 linhas seria
espaço morto sem motivo.

Aplicado em `/contatos`, `/equipe` e na lista de feriados de `/configuracoes` (39px por linha ali,
contra 44 da tabela). **Nenhuma tabela do painel renderiza linha sem dado.**

---

## 2. Meu Dia não abria a conversa

### Precisou de endpoint novo: sim

`GET /api/conversas/{id}` não existia — a lista era o único jeito de obter uma conversa.

| | |
|---|---|
| Rota | `GET /api/conversas/{id}` |
| Isolamento | query filter global; conversa de outro tenant devolve `null` → **404** |
| Resposta de erro | **idêntica** para inexistente e para outro tenant |

A projeção `ConversaResumo` virou uma `Expression` compartilhada entre a lista e a busca por id.
Duplicada, ela divergiria no primeiro campo novo — e o sintoma seria a conversa aberta pelo Meu
Dia mostrando um dado a menos que a mesma linha na lista.

**Quatro testes**, em `ConversaPorIdDbTests.cs`:

- `CONVERSA_DE_OUTRA_EMPRESA_NAO_E_ENCONTRADA` — nos dois sentidos, para não passar por a lista
  do outro tenant estar vazia
- `O_CONTROLLER_DEVOLVE_404_PARA_CONVERSA_DE_OUTRO_TENANT` — e o corpo é idêntico ao do 404 de
  conversa inexistente
- `A_BUSCA_POR_ID_DEVOLVE_A_MESMA_LINHA_QUE_A_LISTA`
- `A_conversa_e_encontrada_MESMO_estando_fora_da_primeira_pagina`

### O que a caixa faz agora

**Antes:** procurava o id na página carregada (30 itens). Não achando, trocava o filtro para
"Todas" e tentava mais uma vez. Com base real, a conversa clicada quase sempre está na página 4 —
e a tela abria **vazia, sem erro**.

**Agora:** busca pelo id, e se a conversa não estiver na lista, **fixa no topo** e abre. A linha
fixada ganha uma barra verde à esquerda, para o vendedor entender por que ela está fora da ordem
cronológica; some ao trocar de aba ou buscar.

404 → mensagem: *"Essa conversa não existe mais, ou não é da sua empresa."* A lista continua
utilizável.

**Cinco testes de frontend** em `caixa.spec.ts`, com a lista montada SEM o alvo de propósito — o
bug é invisível em base pequena. Confirmado por mutação: desligando a busca por id, três reprovam.

### Lembretes

`/meu-dia` agora navega para `/contatos/{id}?lembrete={id}`, e o detalhe destaca a linha
(`.lembrete.em-foco`: fundo creme e barra verde). Marca de leitura, não de alerta — a tarefa não
está errada, só é a que a pessoa clicou.

---

## 3. Largura de conteúdo

### O que foi feito, e o que deu errado no meio

**Passo 1 — remover largura de página.** Todas as sete telas que declaravam `.pagina { max-width }`
tiveram a regra apagada. Restou uma única definição, global.

**Passo 2 — duas larguras, formulário à esquerda.** Como o prompt pediu:
`.pagina.formulario > * { max-width: 860px }`, alinhado à esquerda.

**Isto estava medível-correto e visualmente errado.** O teste que escrevi confirmava: mesmo recuo
em todas as telas, folga de 0px à esquerda, sem centralização. Mas na captura que você mandou,
num monitor de ~1900px, o cartão de `/etapas` terminava em ~1190 e sobravam **~700px de creme**
até a borda. Isso não lê como respiro; lê como página que não terminou de carregar — ainda mais
ao lado de `/contatos`, que usa a largura toda.

**Passo 3 — o limite desceu para onde o problema existe.** O cartão acompanha a largura padrão; o
que fica limitado é o que de fato tem medida de leitura:

| elemento | teto |
|---|---|
| `.sub`, `.explica`, `.dica`, `.nota` | 860px |
| `input`, `select`, `textarea` dentro de `.campo` | 620px |
| grade `.dois` | 1000px |
| **cartão, lista, tabela** | **largura padrão** |

Uma lista de etapas com nome à esquerda e botões à direita é tabela, não parágrafo — encolhê-la
não ajuda a ler nada.

### Medido, não afirmado

`larguras.spec.ts` renderiza 10 telas numa caixa de 1400px e mede:

- **todas começam no mesmo x** — um único valor de recuo
- **todas usam a largura do container**
- **nenhuma tela de formulário deixa vazio à direita** — a asserção é `borda interna − conteúdo
  mais à direita ≤ 1px`

A primeira versão dessa última asserção estava errada e o teste pegou: media o primeiro `.cartao`,
mas `/conexao` é uma **grade** de cartões e ali o primeiro é uma célula. Corrigi a asserção para
medir o bloco que chega mais à direita.

---

## Os três pontos de verificação

**Aviso de WhatsApp desconectado.** Não cria rolagem dupla e não corta conteúdo: ele é irmão de
`.conteudo` dentro do `main`, com `flex: 0 0 auto`. Rouba altura da área que rola, em vez de
empurrar o conteúdo para baixo da dobra. Teste novo em `layout.spec.ts` renderiza o shell com
`whatsappConectado: false` e verifica que a faixa **não** está dentro de `.conteudo`.

**Coluna "valor" com travessão.** Escondida quando nenhum contato da página tem valor. A decisão é
por página, não pela base: a página é o que está na tela, e o total exigiria um dado que a API não
devolve. Efeito colateral aceito — a coluna aparece e some ao paginar, o que ainda é melhor que
uma coluna morta em toda página.

**Rótulo de paginação.** Mantido: "Página 18 de 18 · 350 contatos", igual nas quatro tabelas.

---

## Critério de pronto

| # | item | estado |
|---|---|---|
| 1 | `ng build` limpo, `ng test` verde | ✅ **149** frontend · **450** backend |
| 2 | última página com 10 registros mostra 10 linhas | ✅ |
| 3 | nenhuma tabela renderiza linha sem dado | ✅ `linhasFantasma` removida |
| 4 | Meu Dia abre a conversa fora da primeira página | ✅ testado com a lista sem o alvo |
| 5 | lembrete leva ao contato com o lembrete em foco | ✅ |
| 6 | conversa inexistente ou de outro tenant: mensagem clara | ✅ 404 nos dois casos |
| 7 | mesmo recuo e mesma largura em todas as telas | ✅ medido |
| 8 | nenhuma página define largura máxima própria | ✅ |
| 9 | formulário sem faixa morta simétrica | ✅ e sem faixa morta assimétrica também |
| 10 | aviso de desconexão sem rolagem dupla | ✅ testado |

---

## 4. Classes duplicadas no CSS de componente

Levantado por você, não pelo prompt: *"que web design é você que define a mesma classe em diversos
arquivos .css?"*

**Não era bug.** O Angular encapsula CSS de componente (`ViewEncapsulation.Emulated`), então
`.dois` do `contatos.css` nunca vazou para o `configuracoes.css`. Nada estava quebrado.

**Mas já tinha apodrecido.** Medido antes de mexer:

| classe | definições | corpos **diferentes** |
|---|---|---|
| `.sub` | 15 | 3 |
| `.topo` | 11 | **5** |
| `.acoes` | 8 | **6** |
| `.avatar` | 6 | **4** |
| `.aba` | 3 | **3** — nenhuma igual |
| `.dois`, `.bloco`, `.explica`, `.nota`, `.contagem` | 2–5 | 1 |

Ninguém aprovou que a pílula de aba do `/caixa` fosse diferente da do `/contatos`. Foi
acontecendo, uma tela por vez. Esse é o custo real: a identidade do produto se desfaz sem
decisão.

### O que foi feito

**Promovidas ao `styles.css`**, numa seção "primitivas de tela": `.topo`, `.sub`, `.bloco`,
`.explica`, `.contagem`, `.sem-nada`, `.nota`, `.pessoa`, `.abas`, `.aba`, `.avatar`.

**Renomeadas por papel:**

| antes | depois | porquê |
|---|---|---|
| `.dois` | `.grade-2` | o nome dizia a QUANTIDADE. No dia em que virar três colunas, `.dois` mente |
| `.acoes` (célula de tabela) | `.celula-acoes` | |
| `.acoes` (linha de botões) | `.linha-acoes` | |
| `.acoes` (rodapé de form) | `.rodape-acoes` | |
| `.acoes` (a `<ul>` do Meu Dia) | `.lista-acoes` | |

**`.acoes` era o caso mais grave: um nome fazendo quatro trabalhos.** Fundir os quatro daria uma
regra que não serve a nenhum. O que a duplicação escondia era que faltavam três nomes.

**Viraram modificador** em vez de redefinição: `.avatar.grande` (34px, lista da caixa e equipe) e
`.avatar.pequeno` (30px, feed do dashboard). A diferença passa a ser escolhida, não herdada.

**Uma exceção mantida:** o `.avatar` do `shell.css`. Ele fica sobre o verde escuro da barra
lateral — branco translúcido, não creme. O que muda é o contraste, não o tamanho, então
modificador não resolve. Está comentado no arquivo.

### Mudanças visuais deliberadas

A consolidação **muda a aparência** de algumas telas, e isso foi aprovado:

- **abas de `/caixa` e `/contatos`** ganham a borda que só a de `/formularios` tinha — alvo de
  clique visível antes do hover
- **`.topo` de `/comecar`** cai de 26px para 16px de margem inferior; `/contato`, `/funil` e
  `/meu-dia` sobem de 12–14px para 16px
- **`.sub` de `/comecar`** cai de 15px para 14px
- **avatares** de `/contatos` e `/meu-dia` (32px) ficam iguais; `/caixa` e `/equipe` usam
  `.grande`, `/dashboard` usa `.pequeno`

### O guarda contra recaída

`design-system.spec.ts` renderiza telas diferentes e compara o estilo **computado** do mesmo
componente visual — aba, avatar e subtítulo. Uma cópia nova dentro de um componente vence o
global por ordem de carga, e o teste acusa mostrando a diferença exata.

Confirmado por mutação: reintroduzindo `.aba { padding: 5px 11px; border: 0 }` em
`contatos.css`, o teste reprova com

```
/caixa:    padding-left=12px | border-top-width=1px
/contatos: padding-left=11px | border-top-width=0px
```

**Resultado:** zero definições duplicadas dessas classes nos componentes (era 1 restante, o do
shell, documentado como exceção). CSS de componente: 21 arquivos, 46 KB.

---

## 5. Família de grade e o utilitário de span

### O inventário

Oito grades espalhadas. Três de duas colunas (`.dois`, `.secundarios`, `.colunas`) e duas de
quatro (`.numeros` no dashboard e na conexão), cada uma com a geometria reescrita — e com pontos
de quebra que **já divergiam**: 520px na conexão contra 620px no resto.

Viraram `.grade-2` e `.grade-4`, no global. Três ficaram locais, e o motivo está no CSS:
`auto-fit` não tem um "N" para nomear, e `120px 1fr` / `minmax(84px,22%) 1fr auto` são colunas
**assimétricas**. "N colunas iguais" é a única coisa que `.grade-N` sabe fazer.

**`.grade-3` e `.grade-6` não foram criadas.** Nada usa 3 ou 6 colunas; seriam CSS morto — e CSS
morto é o que faz ninguém confiar no arquivo.

O ponto de quebra virou modificador: `.grade-2` quebra em 620px (campos), `.grade-2.blocos` em
980px (gráfico, rosca). Mesma geometria, exigência diferente.

### `.span-2`, `.span-3`, `.span-tudo`

Um item ocupando mais de uma coluna. Não é só `grid-column: span N`: quando a grade colapsa, o
span precisa colapsar junto — senão o item cria colunas **implícitas** e aquela linha fica
desalinhada das vizinhas, exatamente na tela pequena.

Aplicado ao bloco de dias da semana em `/configuracoes`, que tinha ido parar numa coluna de 1/4 e
quebrava os sete botões em 6+1.

### O teste custou quatro tentativas erradas

Vale registrar, porque cada uma parecia certa:

| medição | por que não funciona |
|---|---|
| quantas trilhas o item ocupa | a trilha implícita é dimensionada pelo conteúdo, fica estreita, o arredondamento esconde |
| a grade transborda o container | não transborda — `1fr` é `minmax(auto, 1fr)`, as colunas explícitas encolhem |
| o item fica mais largo que a linha | mesmo motivo |
| o span declarado cabe nas colunas | `gridTemplateColumns` computado **já inclui** a implícita (`288px 288px 0px`), então `span 3 ≤ 3` passava sempre |

A que funciona: **a grade ganha uma trilha** ao receber o item. Compara-se a mesma grade com e sem
ele. Confirmado por mutação — tirando o colapso do `span-3`, reprova com "a grade passou de 2 para
3 trilhas".

Também descobri no caminho que `grid-column: span 3` põe o span no `grid-column-**start**` e deixa
o `end` em `auto` — ler só o `end` devolve `"auto"` e o teste passa achando que não há span.

---

## Pendências

1. **Nada disto foi visto em navegador por mim.** As medidas são de teste automatizado em Chrome
   headless. A correção do item 3 saiu da SUA captura, não de uma verificação minha — se outra
   tela ficou torta, o mesmo caminho vale: me mande a imagem.

2. **`GET /api/conversas/{id}` não tem teste de rate limit nem de papel.** Ela herda a política
   geral e o `[Authorize]` do controller, como as irmãs. Não foi verificado que um vendedor
   consegue abrir conversa de outro vendedor — o comportamento atual da lista permite, então a
   rota está consistente com ela.

3. **A conversa fixada some ao recarregar a página.** `?conversa=N` continua na URL, então F5 a
   traz de volta; mas se o vendedor rolar a lista e ela carregar mais páginas, a fixada continua
   no topo até ele trocar de aba. É deliberado, e pode incomodar.

4. **`alturaMinimaDaTabela` assume 44px por linha.** Uma tabela com célula de duas linhas de texto
   vai encolher um pouco na última página. Nenhuma tabela atual tem.

5. **A coluna "valor" aparece e some ao paginar.** Registrado acima como efeito colateral aceito.

6. **`/comecar` foi classificada como tela de formulário.** Ela não estava em nenhuma das duas
   listas do prompt. É um checklist, com texto de leitura — o tratamento de formulário cabe, mas
   é uma escolha minha.

7. **As mudanças visuais da consolidação de CSS não foram vistas em navegador.** As abas de
   `/caixa` e `/contatos` ganharam borda, e algumas margens de topo mudaram 2–10px. Os testes
   provam que estão *consistentes*; não provam que ficaram *bonitas*.

8. **O guarda do design system cobre três primitivas** — aba, avatar, subtítulo. `.topo`,
   `.pessoa`, `.nota` e as demais foram consolidadas mas não têm teste comparando telas. Cobrir
   todas custaria muito tempo de suíte para pouco ganho: as três escolhidas são as que já tinham
   divergido.

9. **`.acoes-edicao`, `.acoes-negocio`, `.acoes-modal` e `.acoes-confirmar` continuam locais.**
   Cada uma existe em uma tela só, então não são duplicação. Se uma segunda tela precisar de
   alguma, ela sobe.

10. **⚠️ TODOS OS TESTES DE LAYOUT RODAM NUMA VIEWPORT DE 747×428.** Medido. Consequências:

    - `@media (max-width: 980px)` está **ativo** em todo teste de layout deste projeto;
    - `@media (max-width: 620px)` e `(max-width: 720px)` **nunca** são exercitados;
    - o teste de 380px espreme o layout de ~980px numa caixa de 380px — não testa o layout de
      celular. Correção registrada em `docs/DES-1.md`.

    Media query responde à viewport, não ao elemento: dar largura a um `<div>` de palco não muda
    qual bloco está ativo. Cobrir os pontos de quebra exigiria rodar a suíte em mais de uma
    resolução, ou trocar as media queries por *container queries* — que é uma decisão de
    arquitetura de CSS, não um ajuste de teste.

11. **O `.span-2` de `/configuracoes` não foi visto em navegador.** O bloco de dias deveria passar
    a caber numa linha só; a prova é aritmética (7 × 38px + gaps ≈ 300px numa coluna de ~550px),
    não visual.
