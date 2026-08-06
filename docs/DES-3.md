# DES-3 — Barra lateral: estrutura, densidade e indicadores

## 1. Três zonas

```
┌────────────────────┐
│ .topo-lateral      │  FIXO   marca "nexora" + pulso, nome da empresa
├────────────────────┤
│ .meio              │  ROLA   primeiros passos (cartão) + navegação
│                    │
├────────────────────┤
│ .rodape            │  FIXO   avatar · nome/papel · Sair
└────────────────────┘
```

A lateral era `overflow-y: auto` **no elemento inteiro**. Quando o menu passava da altura da
janela, o bloco do usuário e o "Sair" rolavam para fora junto com os itens — a parte que sumia era
justamente a que precisa estar sempre lá.

Agora a lateral é `overflow: hidden` e **só `.meio` rola**. O `min-height: 0` nele é obrigatório:
sem isso, um filho flex nunca encolhe abaixo do próprio conteúdo, o container cresce e volta a
empurrar o rodapé para fora — é o mesmo detalhe que faz `.conteudo` funcionar do lado direito.

`100dvh` no `.app`, não `100vh`, e agora vale duas vezes: no celular o `vh` mede a janela com a
barra do navegador escondida, e o que ficaria cortado abaixo da dobra é exatamente o rodapé fixo
que este bloco criou.

---

## 2. Densidade — as medidas

| | antes | agora |
|---|---|---|
| item do menu | `padding: 9px 12px`, fonte 14px, gap 2px | `padding: 6px 10px`, fonte 13,5px, gap 1px |
| altura do item | ~40px | **~31px** |
| largura da barra | 220px | 210px |
| separador | 11px, margem `14px 12px 4px` | 10px, margem `12px 10px 3px` |
| cartão de primeiros passos | ~40px | ~34px |
| rodapé | três blocos empilhados, ~122px | **uma linha, ~52px** |
| topo | ~56px | ~50px |

Em 768px de altura sobram ~658px para `.meio`. Com item de 31px + 1 de gap, **cabem 20** — os 11
links de hoje, o separador e o cartão ocupam ~460px.

O alvo do prompt era 14. A folga não é gratuita: o menu já cresceu duas vezes em dois blocos
(Captação no NAV-1, Integrações no INT-3).

### Reduzir espaçamento não é reduzir alvo de toque

```css
@media (pointer: coarse) {
  nav a, .primeiros-passos { padding: 10px; }
}
```

31px é confortável para mouse e apertado para dedo. `pointer: coarse` separa as duas situações
**sem depender de largura de tela** — um tablet de 1024px continua sendo dedo, e uma janela
estreita no desktop continua sendo mouse. Com o padding restaurado o item passa de 39px.

### Nome da empresa

`white-space: nowrap` + `text-overflow: ellipsis`, com o nome completo no `title`. Sem isso,
"Comércio de Materiais de Construção Silva & Filhos Ltda ME" vira três linhas e empurra o menu
inteiro — teste mede a altura contra 1,6× a da fonte.

### A barra de rolagem

`scrollbar-width: thin` + `::-webkit-scrollbar { width: 6px }`, polegar em branco a 22% de opacidade
sobre trilho transparente. A barra grossa do sistema, branca sobre o verde escuro, rouba a atenção
de um menu que na maior parte do tempo nem rola.

---

## 3. Os indicadores — o que estava errado

O prompt diz que o indicador do rodapé e a faixa do topo eram "dois lugares para o mesmo fato".
**Eles não eram o mesmo fato**, e é isso que tornava a situação pior do que o diagnóstico:

- o do rodapé (`.realtime`) era o estado do **hub de tempo real** — se ele cai, o painel continua
  funcionando por requisição normal, mas as telas param de atualizar sozinhas;
- a faixa do topo é o estado do **WhatsApp** — se ele cai, mensagem nenhuma entra nem sai.

O rótulo do primeiro era **"sem conexão"**. Dois textos quase iguais, dois fatos diferentes, e
nenhuma forma de distinguir qual estava dizendo o quê.

### O que ficou

| fato | onde | papel |
|---|---|---|
| WhatsApp | **ponto no item "Conexão"** | informa **sempre** |
| WhatsApp desconectado | faixa vermelha no topo do conteúdo | **interrompe**, só no crítico |
| tempo real | pulso de 6px ao lado da marca, no topo fixo | informa, sem rótulo |

O ponto no item é o lugar semanticamente correto: o estado da coisa junto do link que leva até ela.
A faixa continua porque desconexão é crítica — o vendedor não pode digitar uma resposta que não vai
sair. **Um fato, dois lugares com papéis distintos** — informação contínua contra interrupção
pontual — é diferente de dois lugares com o mesmo papel.

O pulso do tempo real subiu para o topo **fixo**, perdeu o rótulo ambíguo e ficou só com o `title`.
Ele deixou o rodapé, que agora é do usuário, e deixou de competir com a faixa do WhatsApp.

### As três cores

`--urgencia-baixa` (verde), `--urgencia-media` (âmbar), `--urgencia-alta` (terracota). Nenhuma cor
nova.

⚠️ Os tokens foram calibrados para o fundo **creme**. Sobre o verde escuro da lateral, o verde
quase some — por isso o ponto leva `box-shadow: 0 0 0 2px rgba(255,255,255,.28)`. O anel é o que faz
o ponto ser **encontrado**; uma vez encontrado, o matiz distingue o estado. O branco a 28% já é
usado em toda a lateral, então não entra cor nova por essa porta.

### ⚠️ "Conectando" não é distinguível hoje

O prompt pede **verde conectado, âmbar conectando, vermelho desconectado**. O âmbar existe, mas
significa **"verificando"**, não "conectando".

`StatusPainel` carrega `whatsappConectado` e `conexoesCaidas` — e nenhum dos dois separa "pareando
agora" de "conectado". Distinguir exigiria acrescentar o estado ao payload do painel, o que é
**mudança de API**, que este bloco proíbe.

O âmbar cobre o intervalo entre abrir o painel e a primeira resposta chegar — que é justamente
quando um ponto verde estaria mentindo. E o estado "conectando" é curto e já aparece em `/conexao`,
onde o polling de 3s o mostra ao vivo.

**O que destravaria:** um campo a mais em `StatusPainel` (`conectando: bool`, derivado de
`Status == StatusConexao.Conectando` em `ServicoPainel`). Uma linha no DTO e uma no serviço.

---

## 4. O rodapé

```
[AV] Ana Souza      [Sair]
     dono
```

Uma linha, ~52px. Antes eram três blocos empilhados — tempo real, usuário, botão — somando ~122px
num lugar que agora é fixo e portanto cobra esse espaço de todos os itens do menu.

- o bloco inteiro é o link para `/conta`;
- o papel é 10,5px contra 12,5px do nome, e mais fraco — é qualificação, não identidade;
- "Sair" é botão discreto **ao lado**, 11,5px contra 13,5px do item de menu: ação, não item de
  navegação do mesmo peso que Dashboard.

**Nenhum cabeçalho horizontal foi criado.** Uma faixa no topo para acomodar o usuário roubaria
altura de todas as telas — e altura é o que falta na caixa de entrada e no funil.

---

## 5. Primeiros passos

Cartão (borda tracejada, fundo levemente claro), **fora do `<nav>`**: é onboarding, temporário por
natureza, não navegação permanente. Fica no topo da zona do meio.

Some sozinho porque `mostrar` é **derivado** do estado no servidor — o checklist é recalculado a
cada carregamento, não guardado em flag. Dispensar manualmente carimba `onboarding_dispensado_em`, e
aí também não volta. Nada disso mudou neste bloco; só o desenho.

---

## 6. Celular: a barra RECOLHE

**Escolha registrada:** recolhida a 68px em ≤860px, não sobreposta.

Sobreposta exigiria um botão de abrir, um estado de aberto/fechado e um overlay que fecha ao clicar
fora — três peças novas num shell que hoje não tem nenhuma, mais um estado a mais para o teste
cobrir. Recolhida, a navegação continua a **um toque** o tempo todo. O custo são 68px, que em 380px
é 18% da largura.

No recolhido: rótulos por extenso em 10px, empilhados sob o item, centralizados. **Sem ícones** — não
existe conjunto de ícones no projeto, e inventar um agora seria desenhar treze símbolos que ninguém
validou. Texto pequeno que quebra em duas linhas é feio e legível; ícone errado é bonito e ilegível.

O rodapé continua **fixo** e só perde o texto: o avatar sozinho ainda leva a `/conta`, e "Sair"
encolhe em vez de sumir — sair da conta num aparelho compartilhado é exatamente o que não pode ficar
escondido.

---

## 7. A janela do karma mudou — e o que isso revelou

`karma.conf.js` passou a abrir o Chrome com `--window-size=1440,960`.

O `ChromeHeadless` padrão dava um viewport de **747×428**. Isso tinha uma consequência que o DES-1
registrou e não conseguiu resolver: **media query responde à JANELA**, não ao container. Com 747px de
largura, `@media (max-width: 980px)` estava sempre ativa em todo teste — inclusive nos que
renderizam a tela dentro de uma caixa de 1400px.

Ou seja: os testes de layout mediam o layout de **tablet** achando que mediam o de desktop, e as
quebras de 620px e 720px nunca foram exercitadas. Sem essa correção, o teste de densidade deste
bloco mediria a barra recolhida de 68px.

### O que a correção expôs

`Caixa de entrada não transborda em 380px` **passou a falhar** — e estava passando pelo motivo
errado. Em desktop a caixa é lista de 340px fixos + thread lado a lado; a
`@media (max-width: 860px)` faz a lista ocupar 100% e esconde a thread. Antes, o viewport de 747px
já ativava essa media query e o teste media o layout mobile. Agora ele mede o desktop espremido em
380px, que **nenhum celular chega a renderizar**, e acusa 61px de excesso.

O teste de 380px foi mantido estrito para as outras 20 telas, e `/caixa` entrou numa lista de
exclusão **explícita e travada por asserção própria** (`SEM_COBERTURA_A_380PX`) — uma tela a mais ali
é uma tela a menos testada, e tem que ser decisão visível.

⚠️ **Isto é buraco de cobertura, não isenção.** O comportamento da caixa em tela pequena continua sem
teste automatizado, e continua sem ter sido aberto num celular.

---

## 8. Testes

**Frontend: 234 passando** (eram 217). `lateral.spec.ts` novo, com 16 casos.

| o que | como |
|---|---|
| não rola em 768px | o `.app` recebe `height` em linha (o `dvh` mediria a janela do runner) e o teste compara `scrollHeight` com `clientHeight` de `.meio`, com os 11 links e o cartão presentes |
| densidade | mede a altura real de um item (≤36px) e calcula quantos cabem (≥14) |
| a lateral não rola como um todo | `overflowY` de `.lateral` é `hidden` e o de `.meio` é `auto` |
| Sair sempre visível | quatro alturas — 900, 768, 600 e **420px** —, comparando as bordas do bloco com as da lateral |
| ponto de status | as três classes e as **três cores computadas diferentes** |
| indicador fora do rodapé | `.realtime` ausente, sem "sem conexão"/"ao vivo", e o pulso presente no topo |
| faixa sem rolagem dupla | `main` com `overflow: hidden` e `scrollHeight <= clientHeight`, no `main` e no `.app` |
| rodapé compacto | altura ≤64px, `href="/conta"`, fonte do "Sair" menor que a do item de menu |
| empresa longa | `nowrap` + `ellipsis` + altura de uma linha |
| 380px | o `.app` não ganha rolagem horizontal, e nav/usuário/Sair continuam com altura |

**Confirmado por mutação:** devolvi a densidade antiga (`padding: 9px 12px`, fonte 14px, gap 2px) e
o teste de densidade reprovou com *"o item ficou alto demais: Expected 37.59375 to be less than or
equal 36"*. Revertido.

Nota honesta sobre a mutação: o teste de **768px** continuou passando com a densidade velha — nessa
altura específica a barra antiga ainda cabia. O que ela não fazia era caber num notebook 1366×768
**com a barra do navegador** (~640px úteis), que é o caso do relato. Quem prende essa parte são os
testes de "Sair visível em 600px e 420px", que com o rodapé rolante falhariam.

---

## 9. Pendências

| O quê | Por quê |
|---|---|
| "Conectando" no ponto de status | Exige um campo a mais em `StatusPainel` — mudança de API, fora do escopo. §3 |
| `/caixa` sem teste de 380px | Media query não é ativável pelo karma. §7 |
| Sem ícones no menu recolhido | Não há conjunto de ícones no projeto; treze símbolos novos é trabalho de design |
| Cor + `title` como únicos canais do status | O `title` é lido por leitor de tela na maioria dos ambientes, mas não é texto no documento. Um `aria-label` no link resolveria melhor — e mudaria o `textContent` que `navegacao.spec` compara |
| Nada verificado em navegador ou celular real | As medidas são as dos testes. Nenhuma tela foi aberta por mim |

---

## 10. Arquivos

```
src/app/layout/shell/shell.html      três zonas, ponto de status, pulso no topo, rodapé em linha
src/app/layout/shell/shell.css       reescrito
src/app/layout/shell/shell.ts        + statusConexao(), rotuloConexao()
src/app/layout/shell/lateral.spec.ts novo (16)
karma.conf.js                        --window-size=1440,960 nos dois launchers
src/app/paginas/paginas.render.spec.ts   SEM_COBERTURA_A_380PX + o porquê
docs/DES-3.md                        este arquivo
```

Backend intocado — nenhuma mudança de API ou de regra de negócio.
