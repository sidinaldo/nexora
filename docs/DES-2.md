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
