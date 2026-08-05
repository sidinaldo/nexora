# PI-3 — CI e testes de frontend

Estado: **fechado com uma ressalva de execução** — o pipeline não pôde ser disparado em push
porque **o Nexora não é um repositório git** (`fatal: not a git repository`). Detalhe no critério 1.

| | Antes | Depois |
|---|---|---|
| Testes de backend | 305 | **322** (+17) |
| Testes de frontend | 2, sendo **1 vermelho** | **89**, todos verdes |
| Pipeline | não existia | `.github/workflows/ci.yml`, 2 jobs |

---

## 1. O pipeline

`.github/workflows/ci.yml`, em `push` **e** `pull_request`. Sem o push, um commit direto na branch
principal passa sem checagem; sem o pull request, a checagem só acontece depois do merge.

Dois jobs em paralelo — o frontend não espera o Postgres subir para descobrir que o `ng build`
quebrou.

### `backend` — contra Postgres real

Serviço `postgres:17`, a **mesma major do desenvolvimento**. Testar contra outra major esconderia
diferença de comportamento em índice e enum nativo, que é onde o schema mora.

O `BancoTeste` já cria o banco e aplica as migrations sozinho; a única configuração é a variável:

```
NEXORA_TESTE_CONN=Host=localhost;Port=5432;Database=nexora_teste;Username=postgres;Password=postgres
```

**Nada de provider in-memory.** Índice parcial, `ON CONFLICT`, check constraint e query filter
global simplesmente não existem em memória — e é exatamente isso que os testes cobrem: o dedupe do
webhook, o teto diário de follow-up, o isolamento por empresa. Em memória daria verde sem provar
nada, o que é pior do que não testar.

O `health-cmd pg_isready` não é enfeite: sem ele o job começa antes de o banco aceitar conexão e
falha de forma intermitente — o pior tipo de falha, porque ensina a equipe a "rodar de novo".

**Warning novo reprova** (`dotnet build -warnaserror`). O build está limpo hoje; é assim que
continua. Warning que entra é como o repositório perde o sinal — depois de vinte, ninguém lê mais
nenhum.

### `frontend`

`npm ci` (não `install`: respeita o lock e falha se ele estiver fora de sincronia, em vez de
"consertar" em silêncio), `npm run build`, `npm run test:ci`.

### Cache

- **NuGet**: `actions/cache` em `~/.nuget/packages`. Não há `packages.lock.json` no repositório,
  então a chave sai do hash dos `.csproj` e do `.config/dotnet-tools.json`.
- **npm**: embutido no `setup-node` (`cache: npm`), com chave do `package-lock.json`.

Sem cache o pipeline fica lento e as pessoas param de esperar por ele — que é o mesmo que não ter.

### `concurrency`

Push novo cancela a execução anterior da mesma branch. Sem isso, uma sequência de commits enfileira
execuções já obsoletas e a fila cresce mais rápido do que anda.

### Duas mudanças que o pipeline exigiu

**`karma.conf.js`** (novo). `ChromeHeadless` puro fica intermitente em runner por causa do sandbox
do Chrome. As flags ficam isoladas num launcher `ChromeHeadlessCI` **de propósito**: rodar sem
sandbox é aceitável num runner descartável executando código do próprio repositório, e não é
aceitável na máquina de ninguém. `--disable-dev-shm-usage` entra porque o `/dev/shm` de container
tem 64MB e o Chrome trava sem aviso ao estourá-lo — o sintoma é um teste que "às vezes" não termina.

⚠️ Assim que existe um `karma.conf.js`, ele vira a base e o padrão do builder **não entra mais**:
foi preciso declarar `frameworks: ['jasmine']` e os plugins explicitamente. Sem isso, todo arquivo
de teste morria com `describe is not defined`. Fica registrado porque o próximo a mexer no arquivo
vai tropeçar no mesmo lugar.

**Efeito colateral aceito:** `npm test` local agora roda em `ChromeHeadless` em vez de abrir uma
janela do Chrome. É melhor assim, mas é uma mudança de hábito.

---

## 2. Os testes de frontend

89 testes em 6 arquivos. Nenhuma meta de cobertura: cada arquivo existe por um motivo nomeado.

### `semaforo.spec.ts` (19) — prioridade 1

`urgenciaDe`, `minutosUteis`, `dentroDaJanela`, `chaveDia`, `janelaDoStatus`, `rotuloEspera`.

O que os testes fixam, além do óbvio:

- **Fora da janela não acende.** Sem essa regra o vendedor abre o sistema às 8h com tudo vermelho
  por mensagens das 23h — e para de olhar para o semáforo, que é a única forma de ele deixar de
  funcionar.
- **A mensagem das 23h não chega vermelha às 8h** do dia seguinte: o tempo noturno não conta.
- **As 20h já estão fora** (a comparação é `< horaFim`) — o tipo de limite que alguém "corrige"
  para `<=` sem perceber.
- **A trava de 400 iterações**: com bitmask zerado o laço não terminaria. Se alguém a remover, o
  teste não fica vermelho — ele **pendura a suíte**, que é o sintoma que se quer ver.

#### O caso das 21h, e por que ele não usa um `Date` de verdade

O prompt pede o teste que impede a "simplificação" de `chaveDia` para `toISOString()`. Escrevê-lo
com um `Date` real **não funcionaria**: num runner em UTC — que é o caso do CI — hora local e UTC
coincidem, a discrepância não existe, e o teste passaria com qualquer das duas implementações.
Verde exatamente onde precisava morder.

O caso é montado com um objeto que responde os getters locais de um instante em UTC-3 e cujo
`toISOString()` já é o dia seguinte. Assim o teste vale **em qualquer fuso**:

```
chaveDia(...)                          -> '2026-08-06'   (dia local, correto)
...toISOString().substring(0, 10)      -> '2026-08-07'   (a armadilha)
```

**Verificado por mutação:** troquei o corpo de `chaveDia` por `toISOString().substring(0,10)` e a
suíte reprovou com `Expected '2026-08-07' to be '2026-08-06'`. Depois foi revertido.

### `semaforo.paridade.spec.ts` (17) + `ParidadeMinutosUteisTests.cs` (17) — o teste que o backend não tinha

**Um arquivo, dois lados**: `tests/paridade/minutos-uteis.json`, com 16 casos, importado pelo
TypeScript e lido do disco pelo C#. Não é cópia — é o mesmo caminho no disco.

O problema que isso resolve: a regra de minutos úteis existe **duas vezes**, em linguagens
diferentes, porque a cor do semáforo precisa envelhecer no cliente sem novo fetch. Duas
implementações da mesma regra divergem quando alguém mexe em uma só, e o sintoma é péssimo de
rastrear — o Meu Dia ordena pelo cálculo do **servidor** e a caixa pinta pelo do **cliente**, então
a lista "pula" quando o vendedor troca de tela, sem erro em lugar nenhum.

Cada lado com seus próprios casos não pega isso: ficam os dois verdes, discordando.

Decisões do arquivo:

- **Hora de parede, sem zona.** `"2026-08-06T19:50:00"` em C# é `DateTime` de Kind `Unspecified`;
  em JavaScript, `new Date(...)` sem sufixo é hora local. Os dois enxergam as mesmas 19h50. Um `Z`
  ali quebraria a paridade em toda máquina fora de UTC e a transformaria em teste de fuso.
- **Os dois lados têm uma guarda contra o arquivo sumir.** Se o JSON for movido ou quebrar, a
  `[Theory]` ficaria com zero casos e o `for` do Jasmine com zero expectativas — **os dois passam
  em silêncio**. Um teste em cada lado exige `>= 10` casos.
- Os 16 casos cobrem: virada da noite (19h50 → 10min), fim antes do início, começar antes de abrir,
  terminar depois de fechar, atravessar domingo, feriado no meio, **começar dentro de um feriado**,
  janela de 24h cruzando a meia-noite, e truncamento de minuto quebrado.

Os valores esperados foram calculados à mão e **os dois lados concordaram com todos os 16 na
primeira execução**.

### `thread.spec.ts` (16) — prioridade 4

O componente com mais mecânica escondida do painel, compartilhado entre a caixa e o detalhe do
contato. Quebra aqui é sutil por natureza e não lança erro:

- **Cursor = id da mensagem do topo.** Usando o do fim, a mesma página volta para sempre e o botão
  "carregar anteriores" nunca anda.
- **Âncora de rolagem**: `scrollTop` compensado pela altura inserida no topo (200 + (1600−1000) =
  800). Sem isso a thread pula na cara de quem está lendo.
- **Não rouba a rolagem** de quem subiu para ler: mostra o chip "nova mensagem".
- **ACK não move a posição de leitura** — o tick muda, a rolagem não.
- Mensagem registrada **mas não entregue** avisa sem bloquear; erro de requisição destrava o botão.

⚠️ Registro para quem for mexer: a primeira versão trocava o `@ViewChild` por um objeto falso e o
teste da âncora falhava. Qualquer detecção de mudanças **re-consulta a ViewChild** e devolve o
elemento original — o teste passava a medir um objeto que o componente já tinha largado. A solução
é sobrescrever as métricas (`scrollTop`, `scrollHeight`, `clientHeight`) **no elemento real**, que
sobrevive à re-consulta.

### `interceptor-token.spec.ts` (12) e `guardas.spec.ts` (6) — prioridade 3

O interceptor é o único ponto por onde passa toda requisição autenticada: erro ali não aparece numa
tela, aparece em todas.

- Anexa `Authorization` com sessão; não anexa sem.
- **Não manda token nos fluxos públicos** (login, convite, redefinição) mesmo com token velho
  guardado — mandar um token expirado faz a API recusar por expiração, e o sintoma é "não consigo
  entrar" logo depois de a sessão vencer.
- 401 derruba a sessão e vai para o login; **401 no próprio login não derruba nada** (é só senha
  errada, e mandar o usuário para a tela em que ele já está limparia o que ele digitou).
- 429 no login dispara a contagem regressiva; sem `Retry-After` legível usa 60 e não zero (zero
  destravaria o botão na hora e renderia outro 429).
- Guards: papel **desconhecido não vira dono por acidente** — se um papel novo entrar no backend e
  ninguém atualizar o cliente, o padrão é negar.

### `paginas.render.spec.ts` (17) — prioridade 2

As **16 telas** montam, recebem as respostas e desenham. É a rede que garante que nenhuma tela está
quebrada no commit.

Duas notas honestas sobre o alcance desta camada, medidas por mutação:

- Divergência de **tipo** entre template e componente — que é o que quebrou o Meu Dia — o
  `ng build` já pega, por causa de `strictTemplates`. Confirmei reproduzindo o bug original
  (`{{ saudacaoQueNaoExisteMais() }}` no `meu-dia.html`): o build reprova com
  `TS2339: Property ... does not exist on type 'MeuDia'`.
- Tentei duas mutações que o build aceita (`signal<T[]>(null!)` como valor inicial; `!` no lugar de
  `?.` num `computed`) e **o teste de renderização não pegou nenhuma das duas** — os templates
  guardam esses caminhos com `@if (carregando())`, e o valor nulo nunca chega ao render. Registro
  porque calibra o que a camada vale, em vez de vendê-la por mais do que ela é.
- O que ela **pega**, e o build não: erro em tempo de execução na inicialização. Verificado — um
  `ngOnInit` que estoura passa no `npm run build` e reprova no `npm run test:ci` com
  `TypeError: Cannot read properties of null`.

O corpo das respostas é um superset das formas esperadas (campo a mais o JavaScript ignora), com
uma lista explícita dos três endpoints que respondem **array** — `/equipe`, `/feriados`,
`/lembretes/contato/` —, porque mandar objeto neles faz o `@for` estourar por culpa do teste e não
da tela.

### `app.spec.ts` — reescrito

O teste gerado pelo CLI procurava o `Hello, nexora-painel` do esqueleto e estava **vermelho desde a
primeira tela de verdade**. Suíte vermelha de nascença é pior que suíte vazia: ensina a ignorar o
vermelho.

---

## 3. Critérios

| # | Critério | Estado |
|---|---|---|
| 1 | Pipeline roda em push e falha quando deve | **parcial** — ver abaixo |
| 2 | `dotnet test` no CI contra Postgres real | ✅ serviço `postgres:17` |
| 3 | Testes de `semaforo.ts` passam, incluindo o caso das 21h | ✅ 19 testes |
| 4 | Paridade roda dos dois lados com os mesmos casos | ✅ 16 casos × 2 lados |
| 5 | Renderização de cada página do painel | ✅ 16 telas |
| 6 | `ng test` headless passa local e no CI | ✅ local; CI não executado |

### O critério 1, exatamente como está

**O Nexora não é um repositório git.** `git rev-parse` responde
`fatal: not a git repository`, e não há `.git` nem remoto. Sem isso não existe push, não existe
GitHub Actions e o pipeline não tem como ser executado — o arquivo está escrito e correto, mas
**não foi visto rodando**.

O que deu para fazer, e foi feito: executar **os mesmos comandos do workflow**, localmente, contra
quebras propositais, e conferir que cada portão reprova.

| Portão | Comando | Quebra introduzida | Resultado |
|---|---|---|---|
| build backend | `dotnet build -warnaserror` | variável não usada (CS0219) | ❌ `error CS0219`, exit 1 |
| testes backend | `dotnet test` | — | ✅ 322 verdes |
| build frontend | `npm run build` | `{{ saudacaoQueNaoExisteMais() }}` no `meu-dia.html` | ❌ `TS2339`, exit 1 |
| testes frontend | `npm run test:ci` | `chaveDia` trocada por `toISOString()` | ❌ 2 reprovados |
| testes frontend | `npm run test:ci` | `ngOnInit` que estoura | ❌ 1 reprovado (build passa) |

Todas as quebras foram revertidas; a execução final com o código restaurado está verde nos quatro
portões.

**O que falta para fechar o critério:** `git init`, um commit e um remoto no GitHub. É um comando e
uma decisão sua — não fiz porque criar repositório e histórico não estava no escopo deste bloco.

---

## O que ficou de fora

**Ponta a ponta com navegador (Playwright/Cypress)** — excluído pelo prompt, e registrado como
pendência. É o que cobriria o que nenhuma camada atual cobre: arrastar card no kanban (nunca testado
em navegador desde o bloco 8), o fluxo de login de verdade, e o pareamento por QR.

**Meta de cobertura percentual** — excluída pelo prompt. `karma-coverage` está instalado mas não é
exigido por nada.

**Lint** — não há ESLint configurado no projeto, então o pipeline não tem passo de lint. Não estava
no escopo; registro.

---

## Pendências

**Deste bloco:**

1. **Pipeline nunca executou.** Sem repositório git, ver o critério 1. É a única lacuna real.
2. **Cache do NuGet sem lock file.** A chave sai dos `.csproj`, que é mais grosso do que um
   `packages.lock.json` — uma versão flutuante mudaria sem invalidar o cache. Considerar
   `RestorePackagesWithLockFile` quando o incômodo aparecer.
3. **`karma.conf.js` agora é a base do Karma**, e o padrão do builder não entra mais. Quem adicionar
   um plugin (coverage, por exemplo) precisa declará-lo lá.
4. **`npm test` local mudou** de Chrome com janela para `ChromeHeadless`.
5. **O teste de renderização é raso por desenho.** Ele prova que a tela monta e desenha, não que ela
   está certa. Comportamento de tela continua sem cobertura fora do `thread.spec.ts`.

**Encontradas e não corrigidas (fora do escopo, registradas como manda o protocolo):**

6. **`paginas/em-breve/` é código morto.** Não está roteada em `app.routes.ts` nem importada em
   lugar nenhum — o único arquivo que a menciona é ela mesma. Sobrou da fase em que havia telas por
   fazer. Não está no teste de renderização, de propósito.
7. **`<input type="time">` manda `"14:30"` e `TimeOnly` espera `"14:30:00"` → 400.**
   [contato.ts:281](frontend/nexora-painel/src/app/paginas/contato/contato.ts#L281). Consta desde o
   PI-1. **Nota nova:** nenhum teste deste bloco pegaria isso — é comportamento de tela, e a camada
   de renderização não chega lá.
8. **Funil do dashboard e funil do kanban contam diferente.**
   [ServicoDashboard.cs:57-59](src/Nexora.Infra/Servicos/ServicoDashboard.cs#L57-L59) não filtra
   `anonimizado_em IS NULL`. Aberta desde o bloco 9, aguardando decisão.

**Carregadas dos blocos anteriores:** nenhum celular pareado de verdade (desde o bloco 3), arrastar
card do kanban nunca testado em navegador, sem endpoint de série temporal, sem lock distribuído no
agendador, sem SPF/DKIM/DMARC documentados, três tenants de verificação em `nexora_dev`.
