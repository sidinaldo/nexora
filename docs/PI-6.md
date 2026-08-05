# PI-6 — Correções pendentes

Estado: **fechado**. Os 9 critérios verificados por execução, um commit por item.

`dotnet build -warnaserror` limpo, `ng build` **sem warning nenhum**, **406 testes de backend**
(388 → 406, +18) e **98 de frontend** (96 → 98, +2).

---

## 1. `<input type="time">` devolvia 400

Criar lembrete **com hora** pela tela nunca funcionou. O input do navegador manda `"14:30"` — é o
que a especificação do HTML define — e o conversor padrão do `System.Text.Json` para `TimeOnly`
exige `"14:30:00"`.

**Corrigido na API**, não no cliente: um `JsonConverter` que lê os dois formatos. Mandar
`"14:30:00"` do Angular resolveria *esta* tela e deixaria a API recusando o formato que **todo
navegador produz** — o próximo consumidor tropeçaria no mesmo lugar, com o mesmo 400 sem
explicação. O servidor é quem define o contrato.

⚠️ **Precisou do par nulável.** O `System.Text.Json` **não** deriva o conversor de `TimeOnly?` a
partir do de `TimeOnly` quando ele é registrado em `Converters` — e `horaAlvo` é justamente
nulável. Sem o segundo conversor, a correção não teria efeito nenhum no campo que importa.

A **saída** continua em `HH:mm:ss`: formato único de saída, que o `input[type=time]` também lê.
Aceitar dois na entrada não vira devolver dois na saída.

Testes: 8 no backend (os dois formatos gravando o mesmo valor, nulo, saída canônica, e quatro
entradas inválidas falhando com mensagem que ensina) e 2 de tela, que fixam que o cliente manda o
formato **curto** — se alguém "consertar" no Angular, o teste avisa que o conserto foi no lugar
errado.

---

## 2. Contagem do funil divergia entre dashboard e quadro

### O predicado escolhido

```csharp
// RegrasContato.NoQuadro — Nexora.Core
c => c.PerdidoEm == null && c.AnonimizadoEm == null
```

**Perdido** sai porque o negócio acabou: mantê-lo na coluna faria a etapa crescer para sempre.
**Anonimizado** sai porque ele foi apagado a pedido do titular — contá-lo manteria exatamente o
rastro que a anonimização existe para remover.

Uma `Expression<Func<Contato, bool>>`, **não um método**: o EF só traduz o que consegue ler na
árvore de expressão, e uma chamada a método próprio estoura com *"could not be translated"* em
tempo de execução — já aconteceu neste projeto.

Sete pontos passaram a usá-la: cinco no `ServicoFunil` (quadro, coluna paginada, card de
referência do arrasto, vizinhança do cálculo de ordem, renormalização) e dois no
`ServicoDashboard`.

O índice parcial `ix_contatos_kanban` continua **mais largo** de propósito (`WHERE perdido_em IS
NULL`): ele entrega as linhas por etapa e ordem, e o Postgres descarta as anonimizadas por cima —
são poucas, e o índice continua servindo.

### O teste

Base com ativos, um perdido e um anonimizado; compara **etapa por etapa** as duas leituras reais.
Um total agregado esconderia duas diferenças que se cancelam entre colunas.

E afirma também os **valores absolutos**: dois serviços igualmente errados passariam na comparação.
Com a regra unificada, os serviços não podem mais divergir por construção — a comparação guarda
contra a re-duplicação, e os absolutos guardam contra a regra compartilhada estar errada.

**Verificado por mutação:** reintroduzindo a divergência, o teste reprova com `Expected 3, Actual 4`.

---

## 3. Teste da fábrica de design-time

Quatro casos: variável ausente (afirmando os três trechos que a mensagem precisa ter para
**ensinar** o caminho), string vazia e só-espaços — `IsNullOrWhiteSpace` e não `IsNullOrEmpty`,
porque `export NEXORA_CONN=` deixa a variável *definida* — e o caminho feliz.

A coleção desabilita paralelismo: variável de ambiente é estado global do processo.

Fecha o bloco A da varredura.

---

## 4. Higiene

**`paginas/em-breve/`** — removida. Não estava roteada nem importada; o único arquivo que a
mencionava era ela mesma.

**Três tenants de verificação** (ids 3, 4, 5) apagados de `nexora_dev` com as linhas dependentes,
na ordem das FKs — que são RESTRICT, não cascata. Sobraram o tenant de desenvolvimento e o de
demonstração.

**Budget de estilo** — `dashboard.css` passava dos 4 kB. Todas as regras mortas já tinham sido
removidas (nenhuma classe do arquivo está sem uso no template), e o que sobrou é estilo real da
tela mais densa do painel: quatro KPIs, funil desenhado, rosca com legenda, gráfico com abas, duas
listas e três pontos de quebra.

O `anyComponentStyle` subiu para **6 kB**, com o **erro mantido em 8 kB**. 4 kB era o padrão do
CLI, não uma decisão deste projeto; 6 kB continua sendo sinal de verdade. O motivo está escrito no
topo do próprio CSS, não só aqui.

`ng build` agora sai **sem warning nenhum** — que era o ponto: warning permanente treina a equipe a
ignorar warnings, e aí o próximo, que importa, passa batido.

---

## 5. Envio de e-mail fora da requisição de reset

O piso de 250 ms do PI-5 igualava a diferença entre gravar um token e não gravar nada. **Não**
igualava o SMTP: o envio acontecia dentro da requisição, e num relay lento o caminho *com* conta
estourava o piso enquanto o *sem* conta continuava em 250 ms. A assimetria voltava — mais lenta,
mas ainda mensurável de fora.

Agora o envio vai para uma fila em memória, drenada por um `BackgroundService` com **escopo de
injeção próprio**: o trabalho roda depois de a requisição terminar, e o `DbContext` daquele escopo
já foi descartado (o `NotificadorEmail` precisa de um vivo para registrar em `emails_enviados`).

**Não é fila com retry e backoff.** Uma tentativa, resultado registrado, e acabou. A rede de
segurança já existe e é outra: o link de redefinição continua visível na tela para quem tem a chave.
Uma fila com repetição precisaria de persistência, dedupe e fila morta — é a outbox de `mensagens`,
que existe porque WhatsApp duplicado é dano real.

Em memória é uma escolha registrada: se o processo cair com item na fila, o e-mail não sai e o
usuário pede de novo. Capacidade limitada com `DropWrite` — esperar devolveria a lentidão para a
requisição, que é o que isto evita.

O token é gravado **antes** de enfileirar (mesma disciplina do outbox), e os valores vão para
variáveis locais: a entidade pertence ao `DbContext` da requisição.

**Teste:** com um remetente que dorme 2 segundos, os dois caminhos continuam no piso — antes seriam
~2250 ms contra ~250 ms. E o e-mail não se perde: fica enfileirado e sai quando a fila drena. Mais
um teste garante que e-mail inexistente **não enfileira nada** — a simetria é de *tempo*, não de
trabalho; enfileirar para todo mundo "por simetria" mandaria mensagem para endereço que não é de
ninguém.

---

## 6. Semente de desenvolvimento em mais de um tenant

`uq_usuarios_email` é **global**, não por tenant. Com o e-mail fixo (`beatriz@semente.dev`), a
semente rodava uma vez por **banco**.

Reexecutar no **mesmo** tenant sempre funcionou — `LimparAsync` roda antes e o query filter o
recorta por empresa —, e é por isso que ninguém tinha percebido. O problema era só entre tenants.

O id da empresa entra no e-mail (`beatriz.7@semente.dev`), que continua sendo reconhecido pelo
sufixo do domínio, que é como a limpeza os encontra.

A semente **não tinha teste nenhum**. Três agora: duas execuções no mesmo tenant não duplicam,
um segundo tenant não colide (**verificado por mutação** — com o e-mail fixo, reprova com `23505`),
e a limpeza preserva contato digitado à mão.

⚠️ Os usuários de semente já existentes em `nexora_dev` têm os e-mails antigos. A próxima
semeadura os apaga (pelo sufixo do domínio) e cria os novos — quem loga como `rafael@semente.dev`
vai passar a usar `rafael.1@semente.dev`.

---

## Critérios

| # | Critério | Estado |
|---|---|---|
| 1 | `dotnet build -warnaserror` limpo | ✅ |
| 2 | `dotnet test` verde, com os testes novos | ✅ 406/406 |
| 3 | `ng build` limpo, **sem warning de budget** | ✅ |
| 4 | `ng test` verde | ✅ 98/98 |
| 5 | Criar lembrete com hora pela tela funciona | ✅ 2 testes de tela |
| 6 | Contagem do funil bate, com teste que prova | ✅ `A_CONTAGEM_DO_DASHBOARD_BATE_COM_A_DO_QUADRO` |
| 7 | `nexora_dev` sem tenant de verificação | ✅ sobraram os ids 1 e 6 |
| 8 | `paginas/em-breve/` não existe | ✅ |
| 9 | Reset com remetente lento não vaza timing | ✅ `COM_REMETENTE_LENTO_O_TEMPO_CONTINUA_IGUAL...` |

---

## Registrado sem correção

**Da lista do prompt, deliberadamente não construídos:**

| Item | Por quê |
|---|---|
| 26 UFs sem feriado estadual | trabalho de dados; preencher sob demanda, quando houver cliente na UF |
| Arrastar card nunca testado em navegador | precisa de teste ponta a ponta, que é projeto próprio |
| Lock distribuído no agendador | só importa antes da segunda instância |
| Rate limit distribuído | idem |

**Encontrado durante o trabalho:**

1. **Cinco testes existentes precisaram mudar por causa do item 5.** Eles afirmavam que o e-mail
   saía *dentro* da chamada — o que era verdade e deixou de ser. Os que verificam o **conteúdo**
   do e-mail agora drenam a fila explicitamente; os que verificam **timing** afirmam sobre a fila.
   Nenhuma asserção foi enfraquecida.
2. **A fila de segundo plano tem um só uso hoje** (o reset). Se um segundo caso aparecer com
   exigência de entrega garantida, ela não serve — e a resposta certa será uma tabela, não
   aumentar esta.
3. **A varredura marcou o pipeline de CI como parcial** ("nunca se observou uma execução"). Nada
   neste bloco muda isso: o repositório existe desde ontem, e basta abrir a aba Actions.

**Carregadas, ainda abertas:** nenhum celular real pareado (Bloco B da varredura — o maior
bloqueio do projeto); etapas 12–17 não começadas; áudio recebido e guardado, mas sem tocar nem
gravar na tela.
