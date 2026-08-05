# PI-2 — Cadastro de empresa e onboarding

Estado: **fechado**. Os 5 critérios verificados por execução.

`dotnet build` limpo, `ng build` limpo, **305 testes verdes** (287 → 305, +18).

Antes deste bloco não existia caminho para criar uma empresa. O tenant nascia por `INSERT` na mão
— o produto tinha dono, funil e caixa de entrada, mas não tinha porta de entrada.

---

## 1. A forma de proteção escolhida: chave de administração no header

`POST /api/cadastro/empresa` é **público por natureza** — não existe sessão antes de a empresa
existir, e o dono é criado junto com ela. A proteção é uma **chave em header**, comparada em tempo
constante:

```
X-Chave-Admin: <segredo>
```

**Por que chave, e não cadastro aberto.** Nesta fase o onboarding é manual: cliente entra por
reunião e quem cria a conta é a equipe. Cadastro irrestrito na internet, sem verificação de e-mail
nem aceite de termos, seria convite para lixo — e **cada tenant falso arrasta usuário, conexão e
cinco etapas de funil**.

**Por que não prende o desenho.** Quando o autoatendimento chegar, ele será *outro* fluxo — com
confirmação de e-mail, aceite de termos e provavelmente pagamento. Este endpoint continua servindo
à equipe interna. Trocar exige uma linha de configuração, não uma refatoração.

### As três decisões dentro dessa escolha

**Chave vazia = cadastro desligado, e esse é o padrão.**
`OpcoesCadastro.ChaveAdministracao` nasce `""`, e `ChaveConfere()` retorna `false` antes de olhar
qualquer header ([CadastroController.cs:69](src/Nexora.Api/Controllers/CadastroController.cs#L69)).
Um clone do repositório que alguém suba sem configurar nada **não fica com criação de conta aberta
na internet**. Mesma disciplina do segredo do webhook.

O segredo vive em user-secrets ou variável de ambiente, **nunca no `appsettings` versionado**:

```
dotnet user-secrets set "Cadastro:ChaveAdministracao" "..." --project src/Nexora.Api
```

**Comparação em tempo constante**
([CadastroController.cs:67-76](src/Nexora.Api/Controllers/CadastroController.cs#L67-L76)).
`CryptographicOperations.FixedTimeEquals`, não `==`. Diferente do webhook — onde o segredo viaja
na URL e já aparece em log de proxy —, esta chave vai em header e não é registrada em lugar nenhum;
comparar com `==` vazaria o prefixo correto pelo tempo de resposta.

**401 sem detalhe.** A resposta é idêntica para chave ausente e chave errada. Dizer "chave ausente"
confirmaria que o endpoint existe e o que ele espera.

### Rate limit: 3 por hora, por IP

Política `cadastro`, janela **fixa de uma hora**
([RateLimitingConfig.cs:104-109](src/Nexora.Api/Seguranca/RateLimitingConfig.cs#L104-L109)).
Teto baixíssimo porque o fluxo é manual — um cliente por reunião, nunca em rajada. Um número
folgado aqui não serve a ninguém e **transforma vazamento da chave em criação de tenants em massa**.

O teto conta **tentativas, não sucessos**. Se contasse só os sucessos, quem errasse a chave teria
tentativas infinitas para adivinhá-la.

---

## 2. O onboarding é derivado do estado

Nenhum passo tem flag de "concluído". Cada um é uma pergunta ao estado real
([ServicoOnboarding.cs](src/Nexora.Infra/Servicos/ServicoOnboarding.cs)):

| # | Passo | Pergunta ao banco |
|---|---|---|
| 1 | Conecte seu WhatsApp | existe conexão com status `conectado`? |
| 2 | Convide sua equipe | existe usuário não-Dono e não-Inativo? |
| 3 | Receba a primeira mensagem | existe mensagem com `direcao = 'entrada'`? |

**Por que derivado importa de verdade:** com flag, o painel mentiria no caso que mais dói — a
empresa configura tudo, o WhatsApp cai duas semanas depois, e o checklist continua dizendo "tudo
pronto" enquanto nada chega. Derivado, **o passo 1 volta a acender sozinho**. Isso está coberto por
teste (`O_CHECKLIST_E_DERIVADO_DO_ESTADO_NAO_DE_UMA_FLAG`).

### O passo 3 estava errado, e foi corrigido

A primeira versão derivava o passo 3 de `empresas.primeira_mensagem_em`. **Era uma flag disfarçada
de estado, e quebrava de verdade:** a coluna nasceu na migration deste bloco e o webhook só carimba
dali em diante, então **toda empresa que já recebia mensagem antes da migration ficaria com o passo
3 aceso para sempre** — numa conta em plena operação, com a caixa de entrada cheia.

O passo agora pergunta o que o critério sempre disse: *existe mensagem de entrada?*

```csharp
var recebeuMensagem = empresa.PrimeiraMensagemEm is not null
    || await db.Mensagens.AsNoTracking()
        .AnyAsync(m => m.Direcao == DirecaoMensagem.Entrada, ct);
```

A coluna continua na expressão, mas **rebaixada a atalho**: quando está preenchida ela prova que a
mensagem existiu — quem a escreve é o mesmo caminho que insere a linha —, e poupa uma consulta em
`mensagens`, a maior tabela do banco, em toda carga do painel (o shell chama `/api/onboarding` a
cada boot). Quando está NULL, quem responde é a tabela. **O atalho pode ficar para trás; nunca pode
mentir a favor.**

Três testes fecham isso: `PASSO_3_OLHA_A_MENSAGEM_DE_ENTRADA_NAO_A_COLUNA` (mensagem presente,
coluna NULL → concluído), `Mensagem_de_SAIDA_nao_conclui_o_passo_3` (o vendedor digitar não prova
que o produto funcionou) e a verificação por HTTP descrita adiante.

**Backfill** (migration `20260805135627_BackfillPrimeiraMensagem`): sem ele o passo 3 ficaria certo
mas a **métrica** do critério 5 continuaria vazia justamente para quem tem história para contar. O
`UPDATE` traz `MIN(COALESCE(recebida_em, criado_em))` das mensagens de entrada — `recebida_em`
primeiro porque é o mesmo valor que o webhook grava, e o backfill tem que produzir o que o caminho
normal produziria. `WHERE primeira_mensagem_em IS NULL` torna o script idempotente; sem ele,
reexecutar sobrescreveria o carimbo do webhook.

### O que *é* guardado, e por quê

Três colunas nullable em `empresas` (migration `20260805131620_Onboarding`):

| Coluna | Natureza |
|---|---|
| `primeira_mensagem_em` | **métrica** — o *instante*, que a tabela `mensagens` responderia mais caro |
| `equipe_dispensada_em` | **decisão** — "convido a equipe depois" |
| `onboarding_dispensado_em` | **decisão** — "fecha esse painel" |

A regra do prompt — *não guarde flag de onboarding, derive do estado* — vale para a **conclusão**,
e os três passos são derivados. As duas **decisões** são guardadas porque **nenhuma consulta
consegue inferir uma escolha**: não existe pergunta ao banco que distinga "ainda não convidei" de
"decidi não convidar". A distinção adotada, escrita no doc comment do serviço:

> **estado do sistema se deriva, decisão de pessoa se guarda.**

São timestamps e não booleanos de propósito: `equipe_dispensada_em` responde *quando* o dono
desistiu do passo, o que é dado de produto; um `bool` jogaria isso fora de graça.

**Os dois carimbos são idempotentes.** O `WHERE ... IS NULL` faz o segundo pedido virar no-op,
preservando a data da *primeira* decisão — recarregar a tela e clicar de novo não reescreve o
instante. Coberto por teste (`Dispensar_duas_vezes_preserva_a_data_da_PRIMEIRA_decisao`).

### O painel some por dois caminhos independentes

`Mostrar = !Completo && !Dispensado`. Ou os passos se cumprem, ou o dono fecha. E `Completo`
continua **honesto** depois de fechado: quem dispensa com passo em aberto tem `Dispensado=true` e
`Completo=false` — a tela some, o fato não é falsificado.

---

## 3. Tempo até a primeira mensagem

Carimbado no caminho normal do produto, dentro do processador do webhook
([ProcessadorEventoEvolution.cs](src/Nexora.Infra/Evolution/ProcessadorEventoEvolution.cs)), logo
após o `SaveChangesAsync` da conversa:

```csharp
if (entrada)
    await db.Empresas.IgnoreQueryFilters()
        .Where(e => e.Id == conversa.EmpresaId && e.PrimeiraMensagemEm == null)
        .ExecuteUpdateAsync(s => s.SetProperty(e => e.PrimeiraMensagemEm, quando), ct);
```

Três decisões nessas quatro linhas:

- **`IgnoreQueryFilters()` + `Where` explícito no id.** O webhook roda **fora de requisição
  autenticada**, onde o tenant é zero e o filtro global devolveria vazio em silêncio — a armadilha
  nº 1 do inventário. Sem isso o carimbo nunca aconteceria, e ninguém veria erro nenhum.
- **`PrimeiraMensagemEm == null` no `WHERE`.** É *primeira* mensagem. Toda mensagem seguinte é
  no-op no banco, sem leitura prévia e sem corrida entre dois webhooks simultâneos.
- **Só `entrada`.** Mensagem que o próprio vendedor mandou não é sinal de que o produto funcionou.

O valor exposto é `minutosAteAPrimeiraMensagem`, calculado como `primeira_mensagem_em − criado_em`,
com piso em zero — a semente de desenvolvimento produz mensagens datadas antes da criação da linha
da empresa, e sem o piso a métrica sairia negativa em dev.

**A métrica não aparece na tela.** Fica só no payload da API, para leitura interna. Pareamento por
QR, persistência de sessão e reconexão não obedecem cronômetro; prometer prazo que a tela não
controla queima confiança no primeiro minuto de uso.

---

## 4. A tela `/comecar`

- **Um passo destacado por vez.** `proximo()` é o primeiro em aberto. Uma lista de três itens
  iguais faz o usuário decidir por onde começar; destacar um só responde a pergunta.
- **O passo 3 não tem botão.** É espera — a mensagem tem que sair de um celular de verdade. Um
  botão ali prometeria que existe algo a clicar.
- **Dá para sair, em dois níveis:** pular o passo da equipe (muita empresa de uma pessoa só nunca
  vai cumpri-lo) e fechar o painel inteiro. Quem é obrigado a fingir que fez um passo passa a
  ignorar a tela toda.
- **Estado compartilhado num signal só** (`OnboardingServico.estado`): o shell decide se mostra o
  link, a tela desenha o checklist. Duas cópias divergiriam — o link continuaria aceso depois de a
  tela marcar o último passo.
- **Link na lateral** com badge `concluídos/total`, destacado e separado do resto. Some sozinho
  quando o checklist se cumpre: é derivado, não uma flag que alguém precise lembrar de desligar.
- **Login vai para `/comecar`** se `mostrar`, senão para `/caixa`. Se a consulta falhar, vai para a
  caixa — onboarding não pode ser motivo de login que não conclui.

**Papéis:** ler é de qualquer papel — o vendedor que entra numa conta recém-criada também merece
saber que o WhatsApp ainda não foi conectado, em vez de encarar uma caixa vazia sem explicação.
**Dispensar é só do dono**, é decisão de quem responde pela conta.

**Nenhuma promessa de tempo de implantação na tela.** Nada de "pronto em 5 minutos": conectar
depende do celular certo estar à mão, e prometer prazo que a tela não controla queima confiança no
primeiro minuto de uso.

---

## 5. Critérios, verificados por execução

### 1. Builds e testes

```
dotnet build   limpo
ng build       limpo (1 warning pré-existente, ver pendências)
dotnet test    Aprovado! Com falha: 0, Aprovado: 305, Total: 305
```

### 2. Testes — `tests/Nexora.Tests/Integracao/OnboardingDbTests.cs`, 18 casos

| Exigido pelo prompt | Teste |
|---|---|
| cadastro sem a chave é recusado | `CADASTRO_SEM_A_CHAVE_DE_ADMINISTRACAO_E_RECUSADO` (ausente **e** errada) |
| — | `CHAVE_VAZIA_NA_CONFIGURACAO_DESLIGA_O_CADASTRO` |
| cria tenant, dono, conexão e **as 5 etapas**, uma de ganho | `CADASTRO_CRIA_TENANT_DONO_CONEXAO_E_AS_CINCO_ETAPAS` |
| e-mail já usado é recusado com mensagem clara | `E_MAIL_JA_USADO_E_RECUSADO_COM_MENSAGEM_CLARA` (409 + `"Já existe usuário com este e-mail."`) |
| rate limit barra tentativa excessiva | teto de 3/h afirmado no teste + confirmado por HTTP (§ abaixo) |

E mais: checklist derivado, os três passos em aberto, passo 3 lendo a mensagem e não a coluna,
mensagem de saída não concluindo o passo 3, primeira mensagem com a métrica em minutos, métrica nula
sem mensagem, pular equipe, fechar painel, dispensar 2× preservando a data, empresa pronta não vendo
a tela, e os papéis dos três endpoints.

Duas armadilhas de teste que apareceram e foram fechadas:

- O teste da chave usa um `IServicoCadastroEmpresa` que **lança se for chamado** — assim um check
  de chave que passasse por engano não poderia falhar em silêncio.
- O `Semeador` monta um tenant **em operação** (conexão conectada + uma mensagem de entrada), o
  oposto do cenário de primeiros passos. Sem zerar isso, `PASSO_3_OLHA_A_MENSAGEM_DE_ENTRADA...`
  passava pela mensagem *do semeador* — verde sem provar nada. O helper `RecemNascidaAsync` devolve
  o tenant ao estado de recém-cadastrado antes dos testes de checklist.

### 3, 4 e 5. Ponta a ponta por HTTP, contra `nexora_dev`

Empresa criada pelo endpoint, **sem um único `UPDATE` manual**:

```
1.  cadastro sem chave                       -> 401 {"erro":"Não autorizado."}
2.  cadastro com chave                       -> 200 {"empresaId":4}
3.  e-mail duplicado                         -> 409 {"erro":"Já existe usuário com este e-mail."}
4.  login                                    -> 200  papel=dono
5.  GET /api/painel/status                   -> 200
    GET /api/funil                           -> 200
    GET /api/contatos                        -> 200
    GET /api/dashboard                       -> 200
    GET /api/meu-dia                         -> 200
    GET /api/configuracao                    -> 200
    GET /api/equipe                          -> 200
    GET /api/conexao/status                  -> 200
6.  5 etapas: Novo Lead | Primeiro Atendimento | Proposta | Negociação | Venda
    exatamente uma com eGanho: Venda
7.  GET /api/onboarding  -> mostrar=true, 0/3, os três passos em aberto
    minutosAteAPrimeiraMensagem: null
8.  POST /api/contatos                       -> 200 {"id":86}
8b. POST /api/webhook/evolution (mensagem de entrada, instância emp-4) -> 200
    passo 3 concluído, minutosAteAPrimeiraMensagem: 0
9.  POST /api/onboarding/equipe/dispensar    -> 204   passo pulado, painel continua
10. POST /api/onboarding/dispensar           -> 204   mostrar=false
11. 4ª tentativa de cadastro na mesma hora   -> 429
```

O passo **8b vale o critério 5 inteiro**: o tempo até a primeira mensagem foi registrado por uma
mensagem entrando pelo webhook da Evolution, não por escrita direta no banco.

### O passo 3 corrigido, provado ao vivo

Com a API no ar, a coluna do tenant de verificação foi zerada **deixando a mensagem no lugar** — o
estado exato de quem veio de antes da migration:

```
antes  (coluna preenchida) -> passo3.concluido = True   minutos = 0
UPDATE empresas SET primeira_mensagem_em = NULL WHERE id = 4;   (1 mensagem de entrada intacta)
depois (coluna NULL)       -> passo3.concluido = True   minutos = None
```

O passo continua concluído porque a **mensagem** responde; a métrica corretamente não inventa um
valor que ninguém guardou. Antes da correção, `depois` seria `False`. O tenant foi restaurado com o
próprio SQL do backfill.

### O backfill, contra `nexora_dev`

```
 id |            nome             |  primeira_mensagem_em  | msgs_entrada
----+-----------------------------+------------------------+--------------
  1 | Padaria do Bairro           | 2026-05-28 17:26:40-03 |           85
  3 | Padaria do Bairro 20260805a |                        |            0
  4 | Padaria do Bairro 20260805b | 2026-08-05 10:35:30-03 |            1
```

A empresa 1 tinha 85 mensagens de entrada e a coluna vazia — é exatamente a conta que ficaria com o
passo 3 aceso para sempre. Quem não tem mensagem continua NULL, como deve.

---

## Pendências

**Deste bloco:**

1. **Sem verificação de e-mail e sem aceite de termos.** Deliberado — o cadastro é operado pela
   equipe, e a chave substitui a verificação. Vira obrigatório no dia em que abrir autoatendimento.
2. **Rate limit em memória.** Vale para instância única. Com duas instâncias, o teto de 3/h vira
   6/h. Já era verdade das outras políticas; entra na mesma dívida de backplane distribuído.
3. **Sem e-mail de boas-vindas.** A empresa é criada e a senha é entregue por fora. O bloco 11
   deixou o transacional pronto; ligar é uma chamada.
4. **Sem tela de cadastro.** O endpoint é chamado por ferramenta (curl/Postman) por quem tem a
   chave. Tela pública só faz sentido junto com autoatendimento.
5. **Três tenants de verificação ficaram em `nexora_dev`** (ids 3, 4 e 5, "Padaria do Bairro
   20260805a/b/c"). Não estão marcados como semente, então `POST /api/dev/semente/limpar` não os
   remove. Ficaram de propósito — servem para testar isolamento multi-tenant. Apagar é decisão sua.
6. **A consulta do passo 3 não tem índice dedicado.** O `EXISTS` sobre `mensagens` filtrando
   `direcao = 'entrada'` se apoia no prefixo `empresa_id` de `ix_msg_timeline` e para na primeira
   linha — barato para quem tem mensagem de entrada, que é o caso comum. O caso ruim é a empresa com
   muita mensagem de **saída** e nenhuma de entrada, que varre as dela; e mesmo esse só acontece
   enquanto `primeira_mensagem_em` estiver NULL, porque o atalho curto-circuita o resto. Não vale
   um índice hoje; vale registrar.

**Encontradas e não corrigidas (fora do escopo, registradas como manda o protocolo):**

7. **`ng build` acusa `dashboard.css` acima do budget** (5.86 kB contra 4.00 kB). Vem do bloco 9,
   não deste. Um warning, não erro.
8. **`<input type="time">` manda `"14:30"` e `TimeOnly` espera `"14:30:00"` → 400.** Criar lembrete
   *com hora* pela tela de contato nunca funciona.
   [contato.ts:281](frontend/nexora-painel/src/app/paginas/contato/contato.ts#L281). Já constava do
   PI-1.
9. **Funil do dashboard e funil do kanban contam diferente.**
   [ServicoDashboard.cs:57-59](src/Nexora.Infra/Servicos/ServicoDashboard.cs#L57-L59) filtra só
   `perdido_em IS NULL`; `ServicoFunil` também filtra `anonimizado_em IS NULL`. Com contato
   anonimizado na etapa de Venda, o dashboard conta 6 e o kanban 5. Correção é uma linha, aguarda
   sua decisão desde o bloco anterior.

**Carregadas dos blocos anteriores:** nenhum celular pareado de verdade (desde o bloco 3), arrastar
card do kanban nunca testado em navegador, sem endpoint de série temporal (o gráfico do dashboard só
roda em modo demonstração), sem lock distribuído no agendador, zero teste de frontend, sem CI, sem
SPF/DKIM/DMARC documentados.
