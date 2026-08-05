# PI-5 — Ajustes finos

Estado: **fechado**. Os seis itens tratados, os cinco testes exigidos verdes.

`dotnet build -warnaserror` limpo, `ng build` limpo, **367 testes de backend** (352 → 367, +15) e
**89 de frontend**.

---

## 1. Fuso horário editável

Select em Configurações com os quatro fusos do Brasil (Brasília, Manaus, Rio Branco, Fernando de
Noronha), cada um mostrando o **offset atual** — evita a dúvida de sempre ("Manaus é -4 mesmo?").

A lista vem de `GET /api/configuracao/fusos`, **do servidor**, não de uma constante no cliente: ela
depende do tzdata do host, e oferecer um id que o servidor não tem levaria o dono direto ao erro de
validação por culpa nossa. Fuso que o host não conhece simplesmente não é oferecido.

### A validação, e por que ela é estrita onde a leitura é tolerante

`FusoDeNegocio.Resolver` cai em UTC-3 quando o id não existe, **em silêncio**. Isso está certo lá —
container alpine sem o pacote `tzdata` não pode derrubar o agendador — e seria péssimo na escrita: a
empresa de Manaus salvaria um id com erro de digitação, a tela mostraria o valor salvo, e a rodada
dispararia uma hora errada para sempre, sem nada no log.

Então a gravação recusa:

```
PUT /api/configuracao/empresa  {"fusoHorario":"America/Nao_Existe", ...}
400 {"erro":"O fuso horário \"America/Nao_Existe\" não existe neste servidor.
     Escolha um da lista — um fuso inválido faria os follow-ups dispararem na hora errada."}
```

A checagem é contra o **host** (`TimeZoneInfo.FindSystemTimeZoneById`), não contra a lista da tela:
é o host que o `FusoDeNegocio` vai consultar em produção.

⚠️ **A tela avisa que trocar o fuso não reagenda nada:** *"Trocar o fuso não reagenda o que já está
marcado — follow-ups e lembretes já criados mantêm a data e a hora que receberam."* Reprocessar
seria reescrever data de coisa já decidida, e o vendedor veria follow-up mudando de dia sozinho.

Detalhe de tela: se o fuso salvo não estiver na lista do servidor, ele entra como opção extra
("(atual)"). Sem isso o select "pularia" para outro valor só por abrir a tela, e o dono salvaria a
troca sem perceber.

---

## 2. `empresas.uf` e feriados estaduais

Coluna `uf` (`character(2)`, nullable) em `empresas`, select na tela, e `ServicoFeriados` passou a
semear os estaduais junto com os nacionais.

**Semeia pelas UFs em uso** (`DISTINCT uf` das empresas), não pelas 27: os feriados estaduais são
linhas **globais** (`empresa_id NULL`), então semear tudo encheria a tela de configuração de toda
empresa com feriados de outros estados.

**Idempotente**, como o seed nacional: `INSERT ... ON CONFLICT DO NOTHING` contra `uq_feriados`, que
inclui `COALESCE(uf,'')` — o estadual de RN não colide com o nacional da mesma data, e rodar de
novo (boot + rodada diária) é no-op. O teste roda o seed três vezes e confere que a contagem não
muda.

**UF sem cadastro não falha.** `CalculadoraFeriados.Estaduais` devolve lista vazia e um `log
.LogInformation` registra a lacuna. Lançar ali derrubaria o seed inteiro por um dado que falta — e o
efeito colateral seria não semear nem os **nacionais**, trocando uma lacuna pequena por uma grande.

### As UFs que faltam

Cadastrada: **RN** (Mártires de Cunhaú e Uruaçu, 3/10).

Faltam **26**: AC, AL, AP, AM, BA, CE, DF, ES, GO, MA, MT, MS, MG, PA, PB, PR, PE, PI, RJ, RS, RO,
RR, SC, SP, SE, TO.

Empresa dessas UFs recebe hoje só os feriados nacionais — o que é o comportamento atual e continua
correto, só incompleto. O cadastro é dado, não código: entra em
`CalculadoraFeriados.Estaduais`, uma linha por UF.

---

## 3. Concorrência ao mover card

Antes: dois vendedores movendo o mesmo card, o último ganhava **em silêncio** — o primeiro via seu
card em outro lugar no carregamento seguinte, sem explicação.

Agora o card carrega `versao`, que é o **`xmin` do PostgreSQL**.

**Por que `xmin` e não uma coluna própria:** uma coluna `versao` exigiria que todo caminho de
escrita lembrasse de incrementá-la, e bastaria um esquecer para o controle sumir em silêncio. O
`xmin` já existe em toda linha, é mantido pelo próprio Postgres, e não custa espaço nem escrita
extra.

⚠️ **A migration gerada teve um `AddColumn("xmin")` removido à mão.** `xmin` é coluna de sistema;
criá-la falha com *"column name xmin conflicts with a system column name"*. Quem regenerar a
migration vai ver o `AddColumn` voltar — ele não deve entrar. Está escrito no arquivo.

Verificado por HTTP:

```
POST /api/funil/89/mover  {"versao":127323}  -> 200 {"ordemKanban":0}
POST /api/funil/89/mover  {"versao":127323}  -> 409 {"erro":"Outra pessoa moveu este contato
                                                      enquanto você arrastava..."}
```

A tela **já tratava 409** desde o bloco 8: desfaz o movimento otimista, mostra o toast e recarrega a
coluna de origem e a de destino. Não foi preciso mudar nada nesse caminho — só passar a `versao`.

**A versão é opcional**, de propósito: `MarcarGanhoAsync` também move o card e não vem de um
arrasto, então não tem versão para mandar. Exigir sempre quebraria a porta única do ganho. Teste
próprio cobre isso.

**Nada trava e nada bloqueia o card** — é otimista: só recusa quando houve conflito de verdade.

---

## 4. Timing do "esqueci minha senha"

Resolvido com um **piso de tempo**: toda chamada leva pelo menos 250 ms, exista a conta ou não.

### Por que não a receita do login

A saída óbvia era copiar o login: gastar um PBKDF2 contra o `HashDummy` no caminho sem conta. **Foi
o que implementei primeiro, e estava errado.**

No login funciona porque o caminho *com* conta também faz um PBKDF2 — os dois custam ~50 ms. Aqui
não: o caminho com conta gera um token e grava (~2 ms), e o PBKDF2 custa ~50 ms. Equalizar assim não
fecharia a janela — **inverteria a assimetria**, e o e-mail inexistente passaria a ser o *lento*.
Continuaria dando para enumerar contas, ao contrário.

O piso é indiferente a qual lado é mais caro: enquanto os dois couberem embaixo dele, o tempo de
resposta não carrega informação.

Fica no `finally`, também de propósito: uma exceção no meio (banco fora, relay recusando) sairia
rápido e denunciaria pelo tempo tanto quanto o caminho feliz. E usa `Stopwatch`, não o `TimeProvider`
injetado — o que importa é tempo de **parede**, o mesmo que o atacante cronometra; um relógio falso
de teste zeraria a proteção.

**Verificado por mutação:** com o piso zerado, o teste reprova com
`O caminho SEM conta levou 0ms, abaixo do piso de 250ms`. Restaurado, passa.

⚠️ **Limite conhecido:** o envio SMTP acontece **dentro** da chamada. Num relay lento ele pode passar
dos 250 ms, e aí a assimetria volta. A correção é tirar o envio do caminho da requisição — está nas
pendências.

---

## 5. Recorte da janela de feriados

Os limites **não foram aumentados**. O caso passou a ser tratado.

`ServicoMeuDia` carrega feriados dos últimos `JanelaDeEspera.Dias` (30). Espera mais velha que isso
teria o desconto calculado **sem** os feriados anteriores ao recorte: um número maior que o real,
com cara de exato. Quem lê "12.480 minutos úteis" acredita.

Agora `AcaoDoDia` traz `esperaAcimaDaJanela`, e nesse caso `minutosUteis` vem **nulo**. A tela mostra
**"mais de 30 dias"** — que é verdade — em vez de um número que não é.

Um detalhe que quase passou: com `minutosUteis` nulo, o cálculo de urgência devolvia `'baixa'`. Quem
espera há mais de 30 dias é o item **mais** urgente da lista, e ele apareceria apagado. A tela agora
devolve `'alta'` direto quando a bandeira sobe — o número falta, a urgência não.

O limite virou constante nomeada (`JanelaDeEspera.Dias`) e o teste de fronteira usa a constante, não
um `30` literal: se alguém mudar o valor, o teste acompanha em vez de virar mentira.

---

## 6. Veredicto sobre a porta SMTP

**Não há problema hoje. Nada a trocar.**

A configuração padrão é **porta 587 com STARTTLS** (`OpcoesEmail.Porta = 587`, `UsarSsl = true`) —
exatamente o cenário que o `System.Net.Mail.SmtpClient` da BCL atende bem. O `appsettings` versionado
não sobrescreve a porta.

O limite continua registrado onde precisa estar (`RemetenteSmtp`): o `SmtpClient` **não fala SMTPS
implícito (465)** — `EnableSsl` nele significa STARTTLS. Se o provedor escolhido só oferecer 465, há
dois caminhos:

1. **Pedir a 587 ao provedor** — quase todos oferecem, e é mudança de uma linha de configuração.
2. **Trocar para MailKit** — resolve 465 e OAuth, mas é dependência nova.

**Não troquei a biblioteca**, conforme instruído: a decisão merece conversa e só é necessária se o
provedor escolhido não tiver 587. Enquanto a porta for 587, o custo de trocar não se paga.

---

## Critérios

| # | Critério | Estado |
|---|---|---|
| 1 | Builds limpos, testes verdes | ✅ 367 backend + 89 frontend |
| 2 | Os cinco testes exigidos | ✅ abaixo |
| 3 | Cada item entregue tem teste próprio | ✅ 15 testes novos |

| Exigido | Teste |
|---|---|
| fuso inválido é recusado, sem fallback silencioso | `FUSO_INVALIDO_E_RECUSADO_E_NAO_CAI_EM_FALLBACK_SILENCIOSO` |
| seed estadual idempotente e sem falhar com UF sem cadastro | `SEED_ESTADUAL_E_IDEMPOTENTE_E_NAO_FALHA_COM_UF_SEM_CADASTRO` |
| mover com versão desatualizada devolve 409 | `MOVER_CARD_COM_VERSAO_DESATUALIZADA_DEVOLVE_409` |
| "esqueci minha senha" gasta tempo comparável | `ESQUECI_MINHA_SENHA_GASTA_TEMPO_COMPARAVEL_NOS_DOIS_CAMINHOS` |
| espera acima de 30 dias devolve marcador | `ESPERA_ACIMA_DE_30_DIAS_DEVOLVE_MARCADOR_E_NAO_NUMERO` |

E mais dez: fuso válido gravado e UF normalizada para maiúscula, UF inválida recusada e vazia virando
nula, fusos oferecidos existindo de verdade no host, estadual e nacional convivendo na mesma data,
nenhuma UF lançando exceção, versão atual passando e mudando depois, mover sem versão continuando a
funcionar, espera dentro da janela ainda devolvendo número, fronteira usando a constante, e o reset
não revelando nada por retorno nem por exceção.

---

## Pendências

**Não construídas, conforme a lista do prompt:**

| Item | Por quê |
|---|---|
| Múltiplos telefones por contato | exige `telefones_contato` e mexe no casamento do webhook |
| Múltiplas conexões por empresa | remove `uq_conexoes_empresa`, muda roteamento de resposta |
| Histórico de movimentação entre etapas | tabela nova, alimenta relatório que não existe |
| Mídia fora do disco local | S3 compatível; bloqueia mais de uma instância |
| Lock distribuído no agendador | advisory lock do Postgres; antes da segunda instância |
| Rate limit distribuído | idem |
| Retry de e-mail | `emails_enviados` registra e nada relê |
| SPF, DKIM, DMARC | configuração de domínio, não de código |

**Deste bloco:**

1. **O envio SMTP continua dentro da requisição do reset.** É o resíduo do item 4: um relay lento
   passa dos 250 ms e a assimetria volta. A correção é enfileirar o envio (a tabela
   `emails_enviados` já existe) e responder sem esperar — mesmo desenho da outbox de mensagens.
2. **26 UFs sem feriado estadual cadastrado** (§2). É dado, não código.
3. **O `xmin` protege só o `mover`.** Editar contato, marcar ganho e anonimizar continuam sem
   controle otimista. Foi o escopo pedido; se dois vendedores editarem o mesmo contato, o último
   ainda vence em silêncio.
4. **A tela de configurações não avisa que trocar a UF não reprocessa feriados já usados.** O seed
   novo entra na próxima rodada; follow-ups já agendados sobre o calendário antigo mantêm a data.
   Efeito bem menor que o do fuso, mas é a mesma classe de coisa.
5. **`ContatoCard.versao` é `uint` e trafega como número JSON.** `xmin` cabe em 32 bits sem sinal, e
   o JavaScript representa isso exatamente — mas se algum dia virar `xid8` (64 bits), passa a
   precisar de string.

**Carregadas, ainda abertas:**

6. **Pipeline nunca executou** — o Nexora não é repositório git (PI-3, critério 1). Falta
   `git init`, um commit e um remoto.
7. **`<input type="time">` manda `"14:30"` e `TimeOnly` espera `"14:30:00"` → 400.**
   [contato.ts:281](frontend/nexora-painel/src/app/paginas/contato/contato.ts#L281). Desde o PI-1.
8. **Funil do dashboard e do kanban contam diferente.**
   [ServicoDashboard.cs:57-59](src/Nexora.Infra/Servicos/ServicoDashboard.cs#L57-L59) não filtra
   `anonimizado_em IS NULL`. Aberta desde o bloco 9 — é uma linha, e segue aguardando sua decisão.
9. **`paginas/em-breve/` é código morto** (PI-3).
10. **A semente só roda uma vez por banco** — e-mails fixos `@semente.dev` contra `uq_usuarios_email`
    global (PI-4).
11. Nenhum celular pareado de verdade; arrastar card do kanban nunca testado em navegador; sem lock
    distribuído no agendador; três tenants de verificação em `nexora_dev`.
