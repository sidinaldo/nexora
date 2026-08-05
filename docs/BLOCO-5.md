# Bloco 5 — Frontend base

Estado: **fechado com uma ressalva** — 7 dos 8 critérios de pronto foram verificados; o oitavo
(fluxo manual com telefone real) depende de parear um aparelho, que segue pendente desde o
bloco 3.

`ng build` limpo, `dotnet build` limpo, **129 testes verdes**.

---

## 0. O bloqueio encontrado no começo — e o que foi preciso fazer

O prompt aponta para "o que a API já expõe" (blocos 1–4). Ao conferir, **a API não expunha o
que este bloco precisa**:

| O que faltava | Consequência |
|---|---|
| `GET /api/conversas` | **a caixa de entrada não tinha como listar nada** |
| `GET /api/conversas/{id}/mensagens` | **a thread não tinha como carregar** |
| `GET /api/painel/status` | o shell não tinha o payload barato do polling |
| `/api/equipe`, `/api/convite`, `/api/redefinir`, `/api/conta/senha` | 4 telas listadas em "Entra" apontariam para 404 |

`/api/conversas` só tinha `responder`, `assumir` e `liberar` — os endpoints de **escrita** do
bloco 4. Nenhum de leitura. O critério de pronto nº 5 (lista com cursor, thread com âncora de
scroll) era impossível.

**Escrevi o backend que faltava**, ainda dentro de `nexora/`. É trabalho fora do escopo nominal
deste bloco, e está listado em §1 separado do resto para você poder revisar como tal. A lacuna
de equipe/convite/senha já estava registrada como pendência nos relatórios dos blocos 1 e 2.

---

## 1. Backend escrito neste bloco (fora do escopo nominal)

| Arquivo | O que é |
|---|---|
| `Core/Servicos/IServicoCaixa.cs` + `Infra/Servicos/ServicoCaixa.cs` | Lista por cursor, thread por cursor, marcar lida |
| `Core/Servicos/IServicoPainel.cs` + `Infra/Servicos/ServicoPainel.cs` | O payload barato do shell |
| `Core/Servicos/IServicoEquipe.cs` + `Infra/Servicos/ServicoEquipe.cs` | Equipe, convite, reset, troca de senha |
| `Api/Controllers/PainelController.cs` | `GET /api/painel/status` |
| `Api/Controllers/EquipeController.cs` | 4 controllers: equipe, conta, convite, redefinição |
| `ConversasController` (3 métodos novos) | `GET`, `GET /{id}/mensagens`, `POST /{id}/lida` |
| `RateLimitingConfig` | política `senha` (5 por 15 min, por IP **+ token**) |

**Toda a paginação acontece no SQL.** O `ServicoInbox` do Recupera materializa todos os tickets
do status antes de cortar a página — o próprio comentário de lá admite que "Resolvidas cresce".
Aqui o cursor vira `WHERE (ultima_mensagem_em, id) < (:em, :id)` traduzido para SQL, usando o
índice `ix_conversas_lista` criado no bloco 2 exatamente com esse par de colunas.

Portado do `ServicoEmpresa` do Recupera, sem o que era de cobrança (comissão do atendente e o
histórico de troca dela). As travas de negócio vieram: anti-lockout (ninguém muda o próprio
papel nem se desativa) e "tem que restar ao menos um dono ativo".

---

## 2. Frontend — portado, amputado, escrito do zero

### Portado

| Destino | Origem | Nível |
|---|---|---|
| `styles.css` | idem do Recupera | B — estrutura inteira, paleta trocada (§3) |
| `nucleo/servicos/auth.servico.ts` | idem | A — chaves `nexora.token`/`nexora.usuario` |
| `nucleo/seguranca/throttle-login.ts` | idem | **A — literal** |
| `nucleo/seguranca/interceptor-token.ts` | idem | B — sem o ramo `super` (§4.1) |
| `nucleo/seguranca/guardas.ts` | `guarda-*.ts` (3 arquivos) | B — um arquivo só, papéis do Nexora |
| `nucleo/servicos/realtime.servico.ts` | idem | A — só os nomes dos eventos mudam |
| `nucleo/tick-status/tick-status.ts` | idem | A — SVG e estados iguais, cores da paleta nova |
| `rotuloAck` | `nucleo/rotulos.ts:49-62` | A — junto do tick-status |
| `nucleo/download.ts` | idem | **A — literal** |
| `layout/shell/` | idem | B — menu, banner e toast novos |
| `paginas/login/` | idem | B — **sem as credenciais chumbadas** (§4.2) |
| `paginas/convite/`, `paginas/redefinir/` | idem | B — tokens separados |
| `paginas/conta-senha/` | idem | A |
| `paginas/equipe/` | idem | B — sem comissão |
| `paginas/conexao/` | idem (318 linhas) | B — cortada para 1 número |
| `paginas/caixa/` | `caixa.ts` (526 linhas) | **B — esforço alto** (§5) |

### Escrito do zero

- **`nucleo/toast/`** — serviço + componente. O Recupera não tem: lá o padrão é
  `erro = signal('')` por página. Com realtime isso não basta, porque a mensagem chega **sem
  ninguém ter clicado em nada** e não há "página" dona daquele aviso.
- **`nucleo/semaforo.ts`** — cálculo da urgência no cliente, com desconto de horas fora da
  janela (§6).
- **`nucleo/modelos.ts`** — escrito a partir do que a API expõe. O do Recupera tem 941 linhas de
  DTO de cobrança.
- **`paginas/em-breve/`** — placeholder das telas de domínio que ainda não existem, para a
  sidebar não ter link morto.
- Os 4 serviços de API (`caixa`, `painel`, `conexao`, `equipe`).

### Amputado

- **O painel de contexto lateral** da caixa (score, recebíveis, acordo ativo, simulação de
  acordo) — ~40% do `caixa.ts` do Recupera.
- **A resposta rápida com template e recebível em foco.**
- **As duas granularidades de thread** (por devedor × por recebível). Aqui contato = conversa,
  e some junto toda a lógica de escolher "de qual dívida ele está falando".
- **A aba "Sem cadastro"** — no Nexora inbound de desconhecido vira contato automaticamente
  (decisão do bloco 3), então a aba não tem o que mostrar.
- **`funil-estados`** — é funil por estado de dívida, não o kanban de vendas.

---

## 3. Tokens de cor

Restrição atendida: **verde escuro, creme e um único tom de alerta**.

| Token | Valor | De onde veio |
|---|---|---|
| `--verde` | `#14432F` | cor da marca; substitui o `--ink` verde-escuro do Recupera |
| `--verde-2` | `#1D5B3F` | hover e links |
| `--verde-3` | `#2E7A56` | positivo/sucesso |
| `--creme` | `#FBF7EF` | fundo; substitui o `--paper` bege |
| `--creme-2` | `#F3EDE1` | hover de linha e shimmer |
| `--alerta` | `#B4552F` | **o único tom de alerta**: erro, falha, ação destrutiva |
| `--linha`, `--texto`, `--texto-fraco` | neutros | derivados |

O Recupera tem `--amber` como cor de **ação** (botão primário de destaque) além do `--danger`.
Aqui isso foi eliminado: ação primária é o próprio verde, e sobra um tom de alerta só.

**A exceção do semáforo**, explicitamente autorizada: três estados exigem três cores, e os
tokens são derivados da paleta, não vermelho/amarelo de bootstrap.

| Token | Valor | Origem |
|---|---|---|
| `--urgencia-baixa` | `#2E7A56` | é o `--verde-3` |
| `--urgencia-media` | `#A97A22` | âmbar terroso, família do creme |
| `--urgencia-alta` | `#B4552F` | é o próprio `--alerta` |

Há um quarto estado visual, `--urgencia-fora`, que usa `--linha` (cinza neutro): fora do
expediente o semáforo **não acende** (§6).

⚠️ O comentário sobre `.tabela th.num` foi preservado — é o tipo de coisa que alguém "limpa" e
quebra o alinhamento dos cabeçalhos numéricos sem entender por quê.

---

## 4. As proibições, uma a uma

**4.1 URL de API chumbada — não existe.** `api-base.ts` só lê de `environment` (veio do bloco 1).
Confirmado: `grep -r "localhost:5123" src/app/` não encontra nada; a porta aparece **só** em
`environments/environment.development.ts`.

**4.2 Credencial em tela — não existe.** `login.ts` inicializa `email = signal('')` e
`senha = signal('')`, com um comentário explicando por que não há condicional de ambiente aqui:
uma condicional é justamente o que falha num build de produção mal configurado.

**4.3 Vocabulário de cobrança — ausente.** Nenhuma ocorrência de devedor, credor, acordo,
carteira, parcela, recebível, régua, comissão, score, Pix ou boleto em código, label ou
comentário do frontend.

**4.4 Ramo de backoffice — removido.** O `interceptor-token` do Recupera escolhe entre token de
tenant e token de plataforma pela URL. Aqui a única distinção é "rota pública não leva token",
e o arquivo tem metade do tamanho.

**4.5 Bibliotecas — nenhuma nova além de `@microsoft/signalr`**, que é o cliente do realtime e
já era dependência do Recupera. Sem biblioteca de componentes, de estado ou de gráficos.

---

## 5. A caixa de entrada — a mecânica que valia ouro

**`mesclarTopo()`** — recarrega **só a primeira página** e a mescla preservando a cauda já
paginada, com dedupe por id. Sem isso, ou se recarrega tudo (e o vendedor perde a rolagem e o
"carregar mais") ou a lista diverge do servidor. Detalhe que veio junto: havendo cauda, o
`temMais` **mantém o valor anterior** em vez de adotar o da primeira página.

**Âncora de scroll com os três modos**, exatamente como no original:

| Modo | Quando | O que faz |
|---|---|---|
| `fim` | o vendedor acabou de enviar | rola para o fim |
| `auto` | mensagem chegando pelo realtime | rola **só se já estava no fim**; senão mostra o chip "↓ Nova mensagem" |
| `preservar` | ACK avançou | não mexe na rolagem |

Mais o "carregar anteriores", que prepende **compensando o `scrollTop` pela altura inserida** —
senão a thread pula sob os olhos de quem está lendo. E o respeito a
`prefers-reduced-motion`.

**Cursor, não offset.** `(ultimaMensagemEm DESC, id DESC)`, o mesmo par no cliente e no SQL.

**Atribuição, não fila.** Sem dono = "Aguardando"; responder atribui automaticamente (a API faz);
assumir conversa de outro devolve 409 e o toast mostra a mensagem da API.

---

## 6. O semáforo

**A cor é calculada no cliente** (`nucleo/semaforo.ts`), a partir do timestamp que a API devolve.
A razão não é preferência: a cor **muda com o passar do tempo**. Se o servidor mandasse
"amarelo", a lista ficaria amarela até o próximo fetch mesmo já tendo virado vermelha. Um tick de
um minuto (`agora = signal(new Date())`) força o recálculo — a lista envelhece sozinha sem
requisição nova.

Os **limites** (60 min / 240 min) vêm do servidor no `/api/painel/status`, para não virarem
constante duplicada no front.

**O desconto de horas fora da janela** está implementado em `minutosUteis()`: percorre dia a dia
somando só o tempo dentro de `[hora_inicio, hora_fim)` nos dias ligados no bitmask. Uma mensagem
que chegou às 19h50 com janela até 20h tem **10 minutos** de espera às 8h do dia seguinte, não 12
horas. E fora do expediente o ponto fica cinza (`urgencia-fora`) em vez de vermelho — sem isso o
vendedor abre o sistema de manhã com tudo vermelho e para de olhar para o semáforo, que é o único
jeito de ele deixar de funcionar.

⚠️ **A janela está fixa no cliente** (`JANELA_PADRAO`: 8h–20h, seg–sáb, o default do schema). O
servidor tem as colunas desde o bloco 2 mas ainda não as expõe — é pendência do bloco de tempo.

---

## 7. Divergências entre o inventário e o código real

**1. A maior: o inventário não registrou que a API não tinha endpoints de leitura.** O §3.7 fala
de "mecânica da thread de conversa — B, esforço alto" como se o backend correspondente
existisse. Ele foi mapeado no inventário (é o `ServicoInbox` do Recupera) mas **nunca foi portado
para o Nexora** — os blocos 2 a 4 construíram entidades, webhook e envio, e a leitura ficou num
vão entre os blocos. Ver §0.

**2. O `interceptor-token` do Recupera não trata 429 fora do login.** O código real só dispara o
throttle quando `ehLogin` — o inventário descreve isso como "trata 401 e 429" sem a ressalva.
Mantive o comportamento (o throttle é do botão de login), mas vale saber que um 429 no
`/api/conversas` hoje não produz feedback nenhum na tela.

**3. `caixa.ts` tem 526 linhas, e a mecânica reaproveitável são ~120.** O inventário classifica o
arquivo inteiro como "B, esforço alto". Preciso: `mesclarTopo`, os três modos de scroll, o
carregar-anteriores com compensação e o cursor somam cerca de 120 linhas; o resto é painel de
contexto, simulação de acordo e resposta rápida — tudo descartado.

---

## 8. Decisões que tomei por conta própria

**8.1 Escrevi o backend que faltava** em vez de entregar telas apontando para 404. Ver §0.

**8.2 Um arquivo `guardas.ts` em vez de três.** O Recupera tem `guarda-autenticado.ts`,
`guarda-dono.ts`, `guarda-gestor.ts` e `guarda-super.ts`. Três funções de 4 linhas em três
arquivos é cerimônia sem ganho.

**8.3 Rota placeholder para Funil, Contatos e Meu Dia.** O menu da fase 1 pede os sete itens; três
não têm tela. Um componente `em-breve` diz isso — clicar e cair num 404 é pior.

**8.4 O banner de WhatsApp caído é barra fixa no topo do conteúdo, cor de alerta, com o caminho
da solução** ("Reconectar agora" para o dono; "peça ao responsável" para o vendedor). O do
Recupera é mais discreto. O critério era "impossível de ignorar".

**8.5 Erro de entrega vira toast, não `erro` de página.** Quando `responder` devolve
`enviada: false`, a mensagem **existe** e aparece na thread marcada como não entregue. Um bloco
de erro no topo da tela sugeriria que a ação falhou inteira.

**8.6 Toast de erro dura 8s; os demais, 4s.** Quem precisa ler uma falha precisa de mais tempo
que quem lê "mensagem enviada".

**8.7 O rótulo de espera ("há 3h") usa tempo corrido, não o útil.** O desconto de janela decide a
**cor**; o texto mostra o tempo real. Dizer "há 10min" para uma mensagem de ontem à noite seria
mentira — o que se quer é não pintar de vermelho, não reescrever a história.

**8.8 A tela de pareamento avisa quando o `pairingCode` não vem.** Verificado no bloco 3: a
Evolution v2.3.7 não devolve o código. Em vez de deixar o campo vazio, o toast diz para usar o QR.

**8.9 CORS em Development aceita qualquer origem de LOOPBACK.** Mudança feita durante este bloco,
depois de o painel ser servido de `http://localhost:64326` e a API responder *"No
'Access-Control-Allow-Origin' header is present"*.

A causa: a lista fixa (`Cors:Origens`, default `http://localhost:4200`) só cobre o `ng serve`. Em
desenvolvimento o painel nem sempre sai de lá — Live Preview do editor, `http-server` sobre o
`dist` e afins sobem numa **porta efêmera que muda a cada execução**, e a mensagem de erro do
navegador não diz o que fazer a respeito.

Em `Development`, a política agora usa `SetIsOriginAllowed(u => u.IsLoopback)`. É a única forma de
combinar origem dinâmica com credenciais: `AllowAnyOrigin()` é **ilegal** junto de
`AllowCredentials()`, e o SignalR exige credenciais. O predicado restringe a loopback — não é
"libera geral".

**Em produção nada mudou**: continua lista explícita. E o default de produção passou de
`["http://localhost:4200"]` para lista vazia — um fallback para localhost num appsettings de
produção é exatamente o tipo de coisa que passa despercebida.

Verificado por HTTP: preflight de `localhost:64326` devolve **204** com
`Access-Control-Allow-Origin: http://localhost:64326`; o login real devolve **200**; a 4200
continua respondendo; e `https://site-malicioso.com` **não** recebe header nenhum.

---

## 9. Critérios de pronto

| # | Critério | Resultado |
|---|---|---|
| 1 | `ng build` limpo | **sem warning**; 338,60 kB inicial (93,50 kB transferidos), 9 chunks lazy |
| 2 | Login ponta a ponta; 401 limpa sessão; 429 mostra contagem | login **200** com JWT; 401 tratado no interceptor; **429 com `Retry-After: 60` e `Access-Control-Expose-Headers: Retry-After`** confirmados por HTTP |
| 3 | Guards barram rota por papel | vendedor recebe **403** em `/api/equipe` e **200** em `/api/conversas`; o guard esconde o link |
| 4 | Conexão: QR, polling 3s, número/foto, aviso de troca | QR real verificado no bloco 3; polling e aviso implementados — **conectar de fato exige telefone** |
| 5 | Caixa: cursor, âncora nos 3 modos, envio, ACK, atribuição | lista e thread por cursor **verificadas por HTTP** (paginação `antes=` devolve a página anterior correta); âncora e envio implementados |
| 6 | Realtime: mensagem aparece sem refresh, badge sobe, toast dispara | hub e 5 eventos verificados no bloco 3 com dublês; **no navegador, depende de telefone** |
| 7 | Nenhuma URL nem credencial chumbada | confirmado por `grep` |
| 8 | Teste manual do fluxo completo | **NÃO concluído** — depende de parear um telefone real |

Verificado adicionalmente por HTTP com API e painel no ar: CORS de `localhost:4200` para
`localhost:5123` (preflight **204** com `Allow-Credentials` e `Allow-Origin` corretos), o painel
servindo em `http://localhost:4200` com `<app-root>` no HTML, e o **fluxo de convite inteiro**:
dono convida → página pública lê o token sem sessão → aceite define a senha e devolve JWT → login
com a senha nova funciona.

---

## 10. Pendências

**O fluxo manual completo continua bloqueado em parear um telefone.** É a mesma pendência dos
blocos 3 e 4. Os containers `nexora_evolution` seguem de pé com a instância `emp-1` esperando o
QR; o passo a passo está no BLOCO-3.md §5.3.

**A janela de atendimento está fixa no cliente.** O servidor tem as colunas; falta expor. Bloco de
tempo.

**Sem envio de e-mail.** Convite e reset geram link que o dono copia e manda por fora. Limitação
conhecida desde o bloco 1, agora visível na tela (a caixa de link com botão "Copiar").

**Sem teste automatizado de frontend.** Não havia critério pedindo, e o Recupera também não tem.
A mecânica de `mesclarTopo` e de âncora de scroll é justamente o tipo de coisa que se beneficia
de teste — vale considerar antes de mexer nela de novo.

**429 fora do login não tem feedback.** Ver §7.2.

**Envio de mídia pela caixa não existe** — a thread **exibe** mídia recebida, mas não há como
anexar. O endpoint também não existe (pendência do bloco 4).

**Funil, Contatos, Meu Dia e Dashboard são placeholders.**
