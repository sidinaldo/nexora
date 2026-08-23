# MOBILE — o painel em largura de celular

Auditoria de leitura das 19 telas em 360–430px, com alvo em **390px**. Nada foi corrigido: este
documento avalia e propõe.

---

## 1. Veredicto

O painel **não serve no celular hoje**, e o motivo é uma linha só: abaixo de 860px a caixa de
entrada esconde o painel da conversa por CSS — tocar num contato não abre nada, sem erro e sem
aviso. Fora isso o quadro é melhor do que parece: 13 das 19 já funcionam, porque houve
trabalho responsivo real e deliberado (tabelas que rolam, grades que colapsam, `100dvh` no lugar
certo, o detalhe do contato empilhando sozinho). O que falta são quatro defeitos estruturais que
atingem todas as telas de uma vez, e uma navegação que come 17% da largura para entregar alvos de
toque de 26px.

---

## 2. Placar por tela

| tela | usável hoje | problema principal | esforço |
|---|---|---|---|
| `/caixa` | **não** | a conversa é escondida por CSS; a lista não leva a lugar nenhum | **alto** |
| `/funil` | com esforço | arrastar não funciona em toque, e a tela não diz isso | médio |
| `/contatos` | com esforço | tabela de 7 colunas que rola de lado a cada linha | médio |
| `/equipe` | com esforço | tabela de 5 colunas, mesma rolagem | baixo |
| `/captacao` | com esforço | tabela de canais e de formulários | baixo |
| `/relatorios` | com esforço | leitura densa; as tabelas já rolam | baixo |
| `/meu-dia` | sim | botão pequeno dentro de linha clicável — mistoque frequente | baixo |
| `/dashboard` | sim | os valores exatos dos gráficos são inacessíveis sem mouse | baixo |
| `/contatos/:id` | sim | já empilha e já limita a altura da conversa | baixo |
| `/conta` · `/conexao` · `/configuracoes` · `/integracoes` · `/comecar` · `/etapas` | sim | só o zoom de foco (§3.1) | baixo |
| `/entrar` · `/esqueci` · `/convite/:token` · `/redefinir/:token` | sim | só o zoom de foco (§3.1) | baixo |

São **19 telas**, não as 15 previstas: existem também `/relatorios`, `/etapas`, `/comecar` e
`/integracoes`. A rota de detalhe é `/contatos/:id`. `/formularios` e `/canais` ainda respondem,
mas redirecionam para as abas de `/captacao` — não são telas próprias.

---

## 3. Problemas estruturais

Quatro defeitos que não pertencem a nenhuma tela — corrigi-los conserta várias de uma vez.

### 3.1 Todo campo do produto dá zoom no iPhone ao receber foco

O corpo do painel usa fonte de 15px, e os campos herdam esse tamanho. O Safari do iPhone aplica
**zoom automático** em qualquer campo com fonte menor que 16px, e depois do zoom a página fica
deslocada — quem digita tem que dar pinça para voltar a enxergar o botão de salvar.

Acontece em **toda tela com campo**: entrar, buscar, cadastrar contato, responder mensagem. O
campo de busca da caixa de entrada é o pior, com 13px.

É um limiar do sistema operacional, não uma preferência: 15px dá zoom, 16px não.

**Proposta:** os campos passam a 16px em telas estreitas. Um número, um arquivo, efeito nas 19
telas. Nada muda no desktop.

### 3.2 Alvos de toque abaixo do mínimo

A recomendação de plataforma é ~44px de lado. O que existe, medido:

| elemento | onde aparece | altura |
|---|---|---|
| item do menu recolhido | a navegação inteira no celular | **~26px** (1 linha) / ~38px (2) |
| "Editar", "Limpar", "Tentar de novo" | contatos, dashboard, funil, meu dia | **~18px** |
| "Sair" recolhido | rodapé da lateral | ~24px |
| pílula de filtro (abas) | caixa, meu dia, contatos, captação | ~30px |
| botão pequeno | "Assumir", "Concluir", "Registrar venda" | ~32px |
| botão padrão | formulários em geral | ~42px |

O detalhe que fecha o caso: o design system **já tem** uma regra escrita para isto, que engorda os
itens do menu quando o ponteiro é um dedo, e vem com o comentário *"REDUZIR ESPAÇAMENTO NÃO É
REDUZIR ALVO DE TOQUE"*. Ela está sendo **anulada** — a regra de celular vem depois no mesmo
arquivo, com a mesma força, e desfaz o que a primeira protegia.

Ou seja: a proteção foi pensada, escrita, e não está valendo.

### 3.3 Modal alto fica inalcançável

Os modais são centralizados verticalmente e não têm limite de altura nem rolagem própria. Um modal
mais alto que a janela perde o topo **e** o rodapé, e não há como chegar neles — não é que fique
feio, é que o botão de confirmar deixa de existir para o usuário.

Com o teclado virtual aberto a área útil cai para ~350px de altura, então esse é o caso **comum**
no celular, não o extremo. O formulário de novo contato tem seis campos.

O mesmo defeito já foi encontrado e resolvido nas telas públicas (login, convite), e a explicação
está escrita no design system. A solução simplesmente não foi aplicada aos modais.

Afeta: novo contato, edição de contato, convidar pessoa, e o modal de fechamento de venda — que é
o mais usado dos quatro.

### 3.4 Os gráficos não respondem a toque

O gráfico de linha e o de barras mostram os valores exatos numa etiqueta que aparece ao **passar o
mouse**. No celular não existe passar o mouse, então a etiqueta nunca aparece: sobram as formas,
sem número nenhum.

### Dois achados menores, do mesmo tipo

**Existe uma variante de "abas que rolam" que ninguém usa.** Ela foi escrita com o comentário
*"Numa lista estreita (a caixa de entrada), as abas rolam em vez de quebrar linha"* — e a caixa de
entrada usa a variante comum. As cinco abas somam ~474px e quebram em duas linhas a 390px, comendo
altura da lista de conversas.

**Nenhum teste roda abaixo de 860px.** O navegador de teste está fixado em 1440×960, e há um teste
que reconhece a regra de 860px e mede o desktop assim mesmo. Qualquer correção deste documento
entra sem rede — ver §7.4.

---

## 4. Proposta por tela

### 4.1 Navegação — barra inferior, e a lateral recolhida sai

**Decisão:** no celular a lateral desaparece e é substituída por uma barra inferior de cinco itens
com 56px de alvo — **Meu Dia · Caixa · Funil · Contatos · Mais**. Configuração, Conta e Sair vão
para a tela "Mais". Em 861px ou mais, nada muda: a lateral continua exatamente como está.

**Isto revoga a escolha registrada no DES-3**, que decidiu recolher a lateral para 68px em vez de
sobrepor, e recusou explicitamente a gaveta por exigir botão, estado e overlay. A recusa da gaveta
continua certa. O que mudou é a medição do recolhido:

- os itens ficam com 26px de altura (38px quando o rótulo quebra em duas linhas), abaixo do
  mínimo, e a regra de ponteiro grosso que protegia isso está anulada (§3.2);
- recuperar o alvo de toque **engorda a barra** e come altura — e altura é exatamente o que falta
  na caixa e no funil;
- os 68px custam 17% da largura em **toda** tela, o tempo todo.

A barra inferior devolve a largura inteira, dá alvo de 56px sem negociar, e usa o rodapé — que é
onde o polegar já está.

**O rodapé da lateral** (usuário, papel, Sair) vai para a tela "Mais", que é uma lista comum e
resolve o alvo de toque de graça.

**O ponto de status da Conexão** vai para a faixa de alerta, que já existe, já fica fixa acima do
conteúdo e já é impossível de ignorar. A faixa não muda em nada — continua vermelha, continua
fixa, continua com o link para reconectar.

### 4.2 `/caixa` — duas etapas, com a conversa em rota própria

**Decisão:** `/caixa` mostra a lista; `/caixa/:conversaId` mostra a conversa. Em 861px ou mais as
duas rotas desenham o mesmo painel duplo de hoje, e o número na URL só marca qual conversa está
selecionada — o desktop não percebe a mudança.

Rota, e não um botão de alternar, por quatro consequências que vêm de graça:

- o **Voltar do Android** volta para a lista, que é o primeiro gesto que qualquer usuário tenta;
- **recarregar a página** mantém a conversa aberta;
- o link do Meu Dia passa a **apontar direto** para a conversa, em vez de depender do mecanismo
  atual de fixar a conversa no topo da lista;
- a conversa vira um **link compartilhável** — hoje não é.

Os três pontos levantados sobre a thread:

**A âncora de rolagem** tem três modos (ir para o fim, preservar a posição, chip "nova mensagem")
e os três continuam valendo com a thread em tela cheia — a lógica não olha para a largura. O que
precisa de ajuste é a **posição do chip**, hoje ancorado a 96px do fundo contra um compositor que
no celular tem outra altura.

**O compositor** fica fixo no rodapé da thread. E a barra inferior de navegação **não aparece** em
`/caixa/:conversaId`: navegação e compositor disputariam o mesmo rodapé, e quem está atendendo
precisa do compositor. Sair da conversa é o Voltar.

**O teclado virtual** é o que `100dvh` existe para resolver, e ele já está aplicado no esqueleto do
painel. O que precisa ser verificado na implementação é a thread rolar para a última mensagem
quando o teclado abre — hoje ninguém testou isso.

### 4.3 `/funil` — o problema não é a largura, é o arrasto

O quadro já rola de lado dentro do próprio container: uma coluna de 280px cabe em 390px de tela,
com um pedaço da próxima aparecendo, e a página em volta não anda junto. Isso está resolvido.

O defeito é outro. **Arrastar card não funciona em toque**, e o DES-4 já registrou:

> HTML5 drag-and-drop **não funciona em toque**. `dragstart` simplesmente não dispara: no celular,
> o gesto de arrastar é interpretado como rolagem.

Ele registrou também o que ficou faltando: **dizer isso na tela**. Hoje o vendedor tenta arrastar,
o card não se move, e nada explica por quê. O DES-4 aponta a alternativa que já existe — abrir o
contato e trocar a etapa pelo seletor — mas ninguém descobre isso sozinho.

**Proposta, em duas partes:**

1. **Ação "Mover para…" no próprio card**, em telas de toque. Um toque abre a lista de etapas, um
   segundo toque move. É o caminho mais curto, e não depende de o vendedor adivinhar o desvio.
2. **Manter o quadro rolando na horizontal**, e não trocar por abas de etapa — ver §7.3.

**O que não entra:** biblioteca de arrasto por toque, nem reescrever tudo com Pointer Events. O
DES-4 já mediu que o gesto de arrastar conflita com a rolagem do quadro, que é horizontal — o
mesmo eixo.

### 4.4 `/contatos` — cartão empilhado

Sete colunas com largura mínima de 640px significa ver 61% da tabela e arrastar de lado a cada
linha para descobrir de quem é aquele valor. É usável no sentido de que nada quebra, e insuportável
no sentido de que ninguém faz isso duas vezes.

**Proposta: cada linha vira um cartão** — nome e telefone na primeira linha, etapa e responsável na
segunda, valor e situação na terceira.

**Por que cartão e não esconder coluna secundária:** as colunas desta tabela são o resultado dos
filtros que estão logo acima dela. Etapa, responsável e situação são as três coisas que a pessoa
acabou de filtrar. Esconder uma delas no celular esconde justamente a resposta da pergunta que ela
fez. No cartão **nenhum dado sai** — só é reempilhado.

### 4.5 `/equipe` — rolagem lateral, e está bom

Cinco colunas, tela do dono, aberta talvez uma vez por mês para convidar alguém. A rolagem lateral
já está aplicada e resolve. Reconstruir em cartão aqui é gastar esforço no lugar errado.

### 4.6 `/dashboard` — parar em duas colunas, e mostrar o número

A rosca já empilha em telas estreitas e a legenda já vai para baixo dela. Os quatro indicadores já
viram duas colunas e depois uma — a proposta é **parar nas duas**: dois números lado a lado ainda
se comparam, empilhados viram uma lista que ninguém lê como comparação.

O que falta é o valor dos gráficos (§3.4). Em vez de traduzir hover para toque, **rótulo
permanente** no gráfico de barras em tela estreita: menos peça móvel, e o número fica visível sem
ninguém precisar descobrir que aquilo é tocável.

### 4.7 `/meu-dia` — a que mais deve funcionar, e a que está mais perto

Lista de uma coluna, cada linha é um alvo de ~58px. Estruturalmente pronta. Duas correções:

**O botão "Concluir" está dentro de uma linha clicável.** Um alvo de 32px aninhado num alvo de
58px: errar o toque por três pixels abre o contato em vez de concluir o lembrete. No mouse isso é
irritante; no dedo é o comportamento esperado. O botão precisa crescer e se afastar da borda da
linha.

**Sobram ~149px para o texto** depois das colunas fixas (hora, marcador, avatar, botão), e o texto
é justamente o que diz o que fazer. Reduzir o respiro lateral da linha no celular devolve ~20px.

### 4.8 Telas públicas e de configuração — só o campo de 16px

`/entrar`, `/esqueci`, `/convite/:token` e `/redefinir/:token` já estão certas: rolagem própria,
altura que acompanha a barra do navegador, centralização que não corta o topo quando o cartão fica
alto, e `type`/`autocomplete` corretos nos campos.

`/conexao`, `/configuracoes`, `/integracoes`, `/comecar` e `/etapas` já têm quebra própria para
tela estreita. `/etapas` reordena por setas, não por arrasto — funciona em toque desde sempre.

Falta um detalhe pequeno: os campos de **telefone** não pedem o teclado numérico. Hoje só o campo
de valor faz isso. Quem cadastra contato no celular recebe o teclado de letras.

---

## 5. Ordem sugerida

Priorizada por uso real: o vendedor abre Meu Dia e Caixa o dia inteiro, e Configurações uma vez
por mês.

| # | o quê | por que aqui |
|---|---|---|
| 1 | `/caixa` em duas etapas | sem isso o produto não é usável no celular, e nada mais compensa |
| 2 | barra inferior | devolve 68px a toda tela e conserta o alvo de toque da navegação |
| 3 | campo em 16px + alvos de toque | uma mudança no design system, efeito nas 19 telas |
| 4 | `/meu-dia` | duas correções pequenas na tela que mais deve funcionar |
| 5 | `/funil`: "Mover para…" + aviso | fecha a pendência que o DES-4 deixou aberta |
| 6 | `/contatos` em cartão | a tabela mais usada |
| 7 | modal com altura máxima e rolagem | conserta quatro modais de uma vez |
| 8 | valor dos gráficos sem hover | o dashboard já é legível, falta o número |

Os itens **3 e 7 são os mais baratos e os de maior alcance**. Estão abaixo do 1 e do 2 só porque
não desbloqueiam nada — melhoram o que já funciona, enquanto os dois primeiros fazem funcionar o
que não funciona.

---

## 6. O que fica só no desktop

**`/relatorios`.** Sete tabelas de leitura densa com exportação em CSV. Elas já rolam de lado em
tela estreita, então a tela não *quebra* no celular — o que não se justifica é reconstruí-la em
cartão. Quem analisa faturamento por origem está sentado, com o CSV aberto ao lado.

**`/integracoes`.** Configurada uma vez, pelo dono, colando uma URL que veio de outro sistema. Já
tem quebra para tela estreita, e é o suficiente.

**A tabela de entregas do webhook.** Ferramenta de diagnóstico — "o cliente diz que não recebeu".
Ninguém investiga isso no celular.

Assumir estas três é o que permite fazer as outras bem.

---

## 7. Pendências — decisão de produto antes de implementar

**7.1 O quinto item da barra inferior.** Meu Dia, Caixa, Funil e Contatos são consenso. O quinto é
"Mais", ou é Dashboard com "Mais" virando um sexto? Cinco itens em 390px dão 78px cada; seis dão
65px, ainda acima do mínimo, mas o rótulo aperta.

**7.2 O que entra em "Mais" para quem não é dono.** Vendedor não tem acesso a Equipe, Conexão,
Etapas, Captação, Integrações nem Configurações. Sobram Dashboard, Relatórios e Conta — uma tela de
menu com três linhas, que talvez não justifique existir. A alternativa é a barra mudar de conteúdo
conforme o papel, e aí são dois desenhos para manter.

**7.3 `/funil`: quadro que rola, ou abas de etapa?** A proposta recomenda manter o quadro rolando
na horizontal. Trocar por uma etapa por vez é mais confortável de tocar — e desmonta a leitura lado
a lado, que é o valor inteiro de um kanban. Um vendedor que só vê "Negociação" perdeu a informação
de que há 40 cards parados em "Novo Lead". Vale confirmar com quem usa.

**7.4 Cobertura de teste abaixo de 860px.** Não existe nenhuma. O navegador de teste está fixado em
1440×960, e há um teste que reconhece a regra de 860px e mede o desktop assim mesmo. Duas saídas:
um segundo navegador no karma em 390px, ou aceitar que tudo isto entra sem rede. **Precisa ser
escolha, não descuido** — a caixa de entrada quebrou no celular exatamente assim, por uma regra que
nenhum teste exercitava.
