# Bloco 6 — Camada de tempo: follow-up, semáforo e Meu Dia

Estado: **fechado**. Todos os critérios de pronto foram verificados, incluindo o teste manual.

`dotnet build` limpo, `ng build` limpo, **208 testes verdes** (129 antes deste bloco → 79 novos).

---

## 1. O que foi PORTADO do Recupera e o que foi ESCRITO DO ZERO

A divisão não é arbitrária. O que transferiu é **mecanismo de tempo** — calendário, feriado,
janela, fuso, deslize. O que não transferiu é **a razão para mandar mensagem**: no Recupera é o
vencimento de uma dívida com offset de dias; aqui é a inatividade de uma conversa. São perguntas
diferentes sobre bancos diferentes.

### Portado (nível A/B do inventário)

| Nexora | Origem no Recupera | O que mudou |
|---|---|---|
| `Core/Tempo/CalculadoraFeriados.cs` | `CalculadoraFeriados` | Nada de estrutura. Só a nota de que estaduais não são semeados (ver §6) |
| `Core/Tempo/CalendarioAtendimento.cs` | `CalendarioRegua` | **Só o nome.** "Régua" é vocabulário de cobrança |
| `Core/Tempo/JanelaAtendimento.cs` | `JanelaContato` (conformidade CDC) | Mesmo cálculo, justificativa diferente: lá é não importunar o devedor, aqui é o expediente |
| `Core/Tempo/FusoDeNegocio.cs` | disperso no `MotorReguaCobranca` | Extraído para um lugar só, com fallback fixo UTC-3 |
| `Api/Servicos/AgendadorFollowUp.cs` | `AgendadorRegua` | Estrutura idêntica; a hora vem de opções e não de constante |
| `Core/FollowUp/MotorFollowUp.cs` | `MotorReguaCobranca` | **Esqueleto e proteções sim, elegibilidade não** (ver abaixo) |

O que o esqueleto do motor carrega e vale ter copiado: laço por empresa com `try/catch`
individual, freio por conexão antes de postar, espaçamento entre envios, reserve-defer para
fora da janela, e a separação entre "quem decide" (motor) e "quem envia" (`EnviadorMensagem`,
do bloco 4).

### Escrito do zero

| Arquivo | Por que não havia equivalente |
|---|---|
| `Core/Tempo/TempoUtil.cs` | O Recupera **não desconta** horas fechadas de nada. Lá não existe semáforo |
| `Core/FollowUp/IDadosFollowUp.cs` + `Infra/Persistencia/DadosFollowUp.cs` | A elegibilidade é outra pergunta, contra outras tabelas |
| `Core/Servicos/IServicoMeuDia.cs` + `Infra/Servicos/ServicoMeuDia.cs` | Não existe "Meu Dia" no Recupera |
| `Core/Servicos/IServicoDashboard.cs` + `Infra/Servicos/ServicoDashboard.cs` | O dashboard de lá é de recuperação de crédito |
| `Core/Servicos/IServicoLembretes.cs` + `Infra/Servicos/ServicoLembretes.cs` | Lembrete manual de vendedor não tem par |
| `Core/Servicos/IServicoFeriados.cs` + `Infra/Servicos/ServicoFeriados.cs` | O seed de lá é manual, por script |
| `frontend/.../paginas/meu-dia/` | Tela nova |

---

## 2. A regra de elegibilidade, e o porquê de cada condição

Toda ela vive no SQL (`DadosFollowUp.ConversasInativasAsync`). São **cinco condições**:

```
1. conversa ABERTA
2. última mensagem foi de SAÍDA          <- a mais importante
3. parada há >= N dias                    (N = empresas.dias_sem_resposta_followup)
4. contato não é terminal                 (ganho_em IS NULL AND perdido_em IS NULL)
5. contato não tem lembrete pendente
```

**A condição 2 é a que separa este produto de um robô de spam.** Se a última mensagem foi de
ENTRADA, o cliente está esperando resposta — e isso é o **semáforo**, não follow-up. Sem essa
condição o sistema cobraria o vendedor duas vezes pela mesma coisa: uma no vermelho da caixa,
outra no Meu Dia. Está provado em `Conversa_cuja_ultima_mensagem_foi_de_ENTRADA_nao_gera_lembrete`.

A condição 5 evita o outro modo de falha: sem ela o vendedor recebe a mesma tarefa todos os dias
até fazê-la, e para de olhar a lista.

**Nada de `CURRENT_DATE - coluna`.** O `limite` é calculado na aplicação e vai como parâmetro
comparado *contra* a coluna. Função sobre a coluna descartaria o índice, e a varredura viraria
seq scan quando a base crescer.

### Parâmetros configuráveis (colunas em `empresas`, nenhuma constante no código)

| Coluna | Padrão | Governa |
|---|---|---|
| `dias_sem_resposta_followup` | 2 | Quantos dias de conversa parada disparam o follow-up |
| `semaforo_amarelo_minutos` | 60 | Minutos ÚTEIS até o amarelo |
| `semaforo_vermelho_minutos` | 240 | Minutos ÚTEIS até o vermelho |
| `janela_hora_inicio` / `janela_hora_fim` | 8 / 20 | Expediente (início inclusivo, fim exclusivo) |
| `janela_dias_semana` | 126 | Bitmask Dom=bit0..Sáb=bit6 |
| `fuso_horario` | `America/Sao_Paulo` | Base de "hoje" e da hora da janela |

As três primeiras são novas deste bloco. O `ServicoPainel` lia amarelo/vermelho de **constante**
até agora — passou a ler da empresa.

---

## 3. O semáforo: por que o servidor manda o timestamp, não a cor

A cor **muda com o passar do tempo**. Se a API respondesse "amarelo", a lista ficaria amarela até
o próximo fetch, mesmo depois de a conversa já ter virado vermelha. A API manda o **timestamp** e
os **limites**; o navegador faz a conta, e a lista amadurece sozinha.

Mas o cálculo tem uma parte que o navegador **não pode** fazer: descontar os feriados. Ele não os
conhece. Por isso o payload ganhou dois campos novos:

- a **janela** da empresa (`janelaHoraInicio`, `janelaHoraFim`, `janelaDiasSemana`);
- os **feriados dos últimos 30 dias** (`feriadosRecentes`).

Faixa fechada de 30 dias porque o desconto só precisa dos dias no meio da espera, e uma conversa
parada há mais de um mês já está vermelha de qualquer jeito. É um range scan sobre `ix_feriados_data`
devolvendo um punhado de linhas — o endpoint continua barato o bastante para o polling de 45s.

O `nucleo/semaforo.ts` era um **espelho** do cálculo do servidor com a janela **chumbada**
(`JANELA_PADRAO`, 8h-20h). Agora ele recebe a janela real via `janelaDoStatus(status)`, e ganhou
suporte a feriado. Os dois lados dão o **mesmo número** — verificado em §7.

Uma armadilha que custou atenção no espelho: a chave do dia usa `getFullYear/getMonth/getDate`,
**não** `toISOString()`. Às 21h em Brasília o `toISOString()` devolve o dia seguinte, e o feriado
seria descontado no dia errado.

### O desconto, em uma frase

Mensagem das 23h, com janela até 20h: às 8h30 do dia seguinte ela tem **30 minutos** de espera,
não 9h30. Sem isso o vendedor abre o sistema todo dia com a tela inteira vermelha por algo que
ninguém poderia ter respondido — e **para de olhar para o semáforo**, que é a única forma de o
semáforo deixar de funcionar.

---

## 4. Meu Dia: zero tabela

O Meu Dia é uma **leitura de duas fontes que já existem**: conversas com `aguardando_desde`
preenchido + lembretes pendentes com `data_alvo <= hoje`. Não há tabela de tarefas.

A consequência prática está provada em dois testes: **responder na caixa faz a linha sumir do
Meu Dia sozinha**, porque o caminho de envio zera `aguardando_desde`; e **concluir o lembrete o
tira da lista** pela mudança de status. Nenhum código de sincronização, e por construção não
existe "tarefa fantasma" — o modo de falha clássico de uma tabela de to-do paralela.

`data_alvo <= hoje`, não `= hoje`: com igualdade estrita, um dia de folga do vendedor faria a
tarefa sumir para sempre. O atrasado aparece marcado.

Cada ação devolve **`aguardandoDesde` e `minutosUteis` juntos**: o timestamp para o cliente pintar
(cor que envelhece), e os minutos úteis já descontados — porque esse desconto depende dos feriados.

O escopo é do **responsável certo**: minhas + sem dono, nunca as de outro vendedor. Sem isso o
"Meu Dia" seria a agenda da equipe inteira, que é a mesma coisa que não ter Meu Dia.

---

## 5. Dashboard: tudo agregado no SQL

Os quatro números, mais faturamento, conversão e a leitura do funil — **nenhuma linha
materializada em memória**. O `ServicoInbox` do Recupera carrega todas as linhas antes de contar;
esse padrão não se repete aqui.

Duas decisões próprias, registradas porque não estavam no prompt:

1. **`criado_em >= :inicioDoDia`, não `criado_em::date = current_date`.** O cast é função sobre a
   coluna e descartaria o `ix_contatos_criado`. O corte é calculado no fuso de negócio e vai como
   parâmetro.
2. **Conversão = ganhos ÷ (ganhos + perdidos) do mês.** Contatos ainda em negociação ficam de fora:
   incluí-los faria a taxa despencar toda vez que entrasse lead novo, que é o oposto do que a
   métrica precisa mostrar.

`SUM` sobre conjunto vazio devolve `NULL` no SQL — daí o `?? 0`. Sem ele, o dashboard de **toda
empresa recém-criada** quebraria; há teste para isso.

O endpoint barato (`/api/painel/status`, polling de 45s) e o caro (`/api/dashboard`, sob demanda)
continuam separados.

---

## 6. Divergências entre o inventário e o código real

Nenhuma nova neste bloco. As três que apareceram ao portar já estavam registradas no inventário e
se confirmaram:

| Item | Inventário | Código real |
|---|---|---|
| `CalculadoraFeriados` estaduais | "cobre RN" | Cobre RN **e só**; o `switch` devolve vazio para as demais |
| `AgendadorRegua` | "roda no fuso de Brasília" | Roda no fuso **do servidor** — em UTC dispararia às 5h BRT, fora da janela, e os follow-ups se acumulariam sem erro nenhum no log. Corrigido aqui via `FusoDeNegocio` |
| `JanelaContato` | "horário comercial" | É conformidade CDC, com finalidade legal. O cálculo transfere; a justificativa não |

Uma nota de **schema**, não de inventário: `CalculadoraFeriados.Estaduais` está pronto mas **não é
semeado**, porque `empresas` não tem coluna `uf` — não há como saber a que estado a empresa
pertence. Quando a UF entrar no cadastro, basta o seed passar a chamar o método.

---

## 7. Como cada critério de pronto foi verificado

### Testes automatizados — 79 novos, todos verdes

**Unidade (41)** — `tests/Nexora.Tests/TempoTests.cs`, sem banco:

- Páscoas de 2025–2030 conferidas contra o calendário real, mais "cai sempre num domingo" de 2024 a 2040. É o número do qual **todos** os móveis dependem: errar por um dia desloca carnaval, sexta-feira santa e Corpus Christi de uma vez.
- Sexta-feira Santa sempre sexta e Corpus Christi sempre quinta, 2024–2035.
- Consciência Negra na lista (virou nacional em 2024 — o feriado mais fácil de esquecer numa lista copiada de tutorial antigo).
- `DiaPermitido` com bitmask e com feriado; `ProximaDataPermitida` para domingo, para emenda de feriado, e a **trava de 370 iterações** com bitmask zerado.
- `FusoDeNegocio.Resolver` com id válido, `null`, vazio e id inexistente — sempre UTC-3, nunca exceção.
- `AgoraNo` provando o off-by-one: 23h30 UTC é **20h30 do dia anterior** em Brasília.
- `TempoUtil` linha a linha, incluindo o caso da mensagem da noite (30 min úteis contra 570 de parede), domingo inteiro descontado, feriado no meio, `fim < inicio` devolvendo zero e janela zerada não travando.

**Integração contra Postgres real (38)** — `FollowUpDbTests.cs` e `MeuDiaDbTests.cs`:

| Critério do prompt | Teste |
|---|---|
| conversa parada N dias com saída gera lembrete | `Conversa_parada_com_ultima_mensagem_de_SAIDA_gera_lembrete` |
| entrada não gera | `Conversa_cuja_ultima_mensagem_foi_de_ENTRADA_nao_gera_lembrete` |
| etapa terminal não gera | `Contato_em_etapa_terminal_nao_gera_lembrete`, `Contato_perdido_nao_gera_lembrete` |
| segundo automático no mesmo dia barrado **sem exceção** | `Segundo_automatico_no_mesmo_dia_e_BARRADO_pelo_banco_sem_excecao` |
| rodada fora da janela reserva sem postar | `Rodada_FORA_da_janela_reserva_sem_postar` |
| conexão caída reserva sem postar | `Conexao_caida_reserva_sem_postar_e_a_rodada_seguinte_drena` |
| exceção numa empresa não interrompe as outras | `Excecao_numa_empresa_nao_interrompe_as_outras` |
| semáforo desconta horas fora da janela | `MINUTOS_UTEIS_DESCONTAM_AS_HORAS_FORA_DO_EXPEDIENTE`, `Feriado_no_meio_da_espera_tambem_e_descontado_no_Meu_Dia` |
| Meu Dia traz conversas + lembretes do responsável certo | `Traz_o_do_RESPONSAVEL_certo_e_os_sem_dono_mas_nao_os_de_outro_vendedor` |
| responder remove do Meu Dia | `RESPONDER_A_CONVERSA_TIRA_ELA_DO_MEU_DIA` |

Mais o que não estava na lista e vale ter: conversa resolvida não gera; contato com lembrete
pendente não ganha outro; empresa sem conexão é pulada; feriado fecha a janela em pleno horário
comercial; feriado de uma empresa não vale para outra; seed anual idempotente; remover feriado
global é recusado; lembrete manual **não** entra no teto diário; concluir duas vezes devolve 409;
dashboard de empresa vazia não estoura no `SUM`; nada vaza entre tenants em nenhuma das leituras.

### Duas expectativas minhas estavam erradas sobre o deslize

Escrevi dois testes assumindo que a rodada das 23h de uma quinta reservaria para **sexta**. Ela
reserva para **quinta mesmo** — e o código está certo: o que fechou a janela foi a *hora*, não o
*dia*, e quinta é um dia em que a empresa atende. O deslize só muda a data quando o próprio dia
está bloqueado (fim de semana ou feriado). Corrigi os testes, não o motor, e o caso do deslize
real ganhou teste próprio (`Lembrete_ja_vencido_e_reservado_com_a_data_do_proximo_dia_aberto`).

### O espelho TypeScript, verificado contra o servidor

O cálculo do servidor está provado em C#, mas quem pinta a tela é o TypeScript. Transpilei
`nucleo/semaforo.ts` e rodei 14 asserções contra ele: **todos os números batem com os do
`TempoUtilTests`** — 30, 10, 120, 840 e 120 minutos nos mesmos cenários. Divergir faria a lista
"pular" quando o vendedor trocasse de tela.

### Teste manual — mensagem fora do expediente

Com a API de pé contra o `nexora_dev`, simulei uma mensagem chegando às **23h de 03/08**:

```
GET /api/meu-dia   ->  minutosUteis: 528
tempo de parede    ->  1067 minutos
desconto           ->  539 minutos fora do expediente  (23h -> 8h)
```

528 minutos é exatamente 8h00 → 16h48, a hora do teste. Em seguida estreitei a janela para 8h-16h
(deixando "agora" fora dela) e passei o payload real do `/api/painel/status` pelo cálculo do
cliente:

```
janela          : 8h-16h
agora (16h58)   : dentro da janela = false
                  urgência = "fora"   <- CINZA, o alerta NÃO acende
  08h00:    0 min úteis -> baixa
  08h30:   30 min úteis -> baixa
  09h05:   65 min úteis -> media
  12h05:  245 min úteis -> alta
```

O semáforo não acende à noite, e acende na abertura do expediente com o tempo certo. O banco de
desenvolvimento foi restaurado ao estado anterior ao teste.

### O agendador, de pé

`Próxima rodada de follow-up em 15:26:34 (às 8h)` — calculado no horário de Brasília, não no do
servidor. Os 26 feriados nacionais (13 de 2026 + 13 de 2027) foram semeados no boot.

---

## 8. Um bug encontrado durante o teste manual, e corrigido

Na primeira subida, o `AgendadorFollowUp` **caiu** — exatamente o que ele existe para nunca fazer.

O `catch` protege a operação, mas o `log.LogError` **dentro do catch** não estava protegido, e ele
lança: no Windows o provider de EventLog estoura `ObjectDisposedException` quando a aplicação já
está desligando. A exceção saía do catch, subia pelo `ExecuteAsync` e derrubava o
`BackgroundService`. O mecanismo que existe para o serviço nunca cair era por onde ele caía.

Corrigido com um `Registrar()` que envolve o log em `try/catch` e cai para `Console.Error` como
último recurso. Perder uma linha de log é aceitável; perder o agendador não é.

---

## 9. Limites conhecidos

### O que mais importa: **não há lock distribuído**

Com **duas instâncias** da API, a rodada diária roda **duas vezes**. As invariantes de banco
seguram o estrago onde ele seria visível para o cliente — `uq_lembrete_teto_diario` barra o
lembrete duplicado e `uq_msg_lembrete` barra a mensagem duplicada — mas:

- o trabalho é feito em dobro;
- o **espaçamento de 3s entre envios deixa de valer entre as instâncias**, e disparo em rajada
  pela mesma instância do WhatsApp é o jeito clássico de ter o número banido. Em rota não-oficial,
  isso é risco contratual.

Enquanto o Nexora rodar em **uma instância**, está correto. Ao escalar horizontal, isto precisa de
resolução — um advisory lock do Postgres (`pg_try_advisory_lock`) resolveria em poucas linhas.
Está documentado no próprio `AgendadorFollowUp`.

### Os outros

| Limite | Consequência | Quando doer |
|---|---|---|
| Rodada **uma vez por dia**, às 8h globais | Empresa que abre às 9h teria os follow-ups do dia reservados mas só postados na rodada seguinte | Quando houver cliente com janela começando depois da hora da rodada. Resolve-se rodando de hora em hora e checando a janela de cada empresa |
| Feriados **estaduais** não semeados | Empresa em estado com feriado próprio atenderá/disparará num dia fechado | Assim que houver cliente fora do eixo. Precisa de `empresas.uf` |
| `feriadosRecentes` limitado a 30 dias | Espera de mais de um mês tem o desconto incompleto | Nunca na prática: já está vermelha |
| Sem tela de **configuração** da janela e das faixas | Ajustar o expediente exige `UPDATE` no banco | Na primeira venda a um cliente com horário diferente |
| Sem tela de **feriados** | O endpoint existe (`/api/feriados`), a tela não | Junto com a de configuração |
| Sem UI para criar **lembrete manual** | A API existe (`POST /api/lembretes`), o botão não. O Meu Dia lista e conclui, mas não cria | Junto com a tela de contato/funil |
| `TempoUtil` trava em **400 iterações** | Espera de mais de ~400 dias tem o total limitado | Nunca; é rede de segurança contra dado ruim |
| Rodada **sem telemetria persistida** | O `ResultadoRodada` vai para o log, não para uma tabela | Quando alguém precisar de "quantos follow-ups saíram no mês passado" |

### Carregadas dos blocos anteriores, ainda abertas

- **Nenhum telefone pareado** (desde o bloco 3): o caminho de envio nunca falou com um WhatsApp real.
- **Nenhum envio de e-mail** (desde o bloco 1): convite e reset de senha são links copiados à mão.
- **Funil, Contatos e Dashboard** seguem placeholder no frontend. O **Meu Dia deixou de ser** neste bloco.
- **Nenhum teste de frontend.** A verificação do `semaforo.ts` em §7 é um script, não uma suíte.
- **Rate limiting em memória** — instância única, mesma restrição do lock.

---

## 10. Decisões próprias (não estavam no prompt)

1. **`ProximosAsync` de feriados e o CRUD manual** — o prompt pedia a camada de tempo; sem um jeito
   de a empresa marcar o ponto facultativo dela, o calendário só tem os 13 nacionais.
2. **Conversão do mês = ganhos ÷ fechados**, não ÷ total de leads (justificado em §5).
3. **Funil dentro do payload do dashboard** — o quadro precisa dos mesmos números e uma segunda
   chamada seria uma segunda varredura de `contatos`. Perdidos ficam fora, pela mesma regra do
   índice parcial `ix_contatos_kanban`.
4. **`feriadosRecentes` no `/api/painel/status`** — a alternativa era um endpoint separado; para
   um punhado de linhas indexadas não compensa a complexidade de manter dois estados no cliente.
5. **Lembrete manual amarrado à conversa do contato** quando ela existe — necessário para o
   lembrete que envia mensagem saber por qual conexão sair.
6. **Lembrete manual não entra no teto diário.** O teto é a defesa contra o *robô* cansar o
   cliente; uma pessoa marcando três tarefas para o mesmo contato sabe o que está fazendo.
7. **Query filter especial para `feriados`**: `EmpresaId == null || EmpresaId == contexto`. É a
   única tabela com linhas globais compartilhadas entre tenants. Remover só o manual do próprio
   tenant é enforcement explícito no serviço — uma empresa apagando o Natal apagaria o de todos.
8. **`?conversa=N` na caixa de entrada** — o Meu Dia precisa abrir a conversa da ação clicada. Se
   ela não está na primeira página do filtro atual, cai em "Todas" e tenta de novo: uma ação que
   às vezes não leva a lugar nenhum destrói a confiança na lista.
9. **Tela do Meu Dia construída**, não deixada como placeholder. Uma camada de tempo sem a tela em
   que ela aparece não é verificável pelo usuário.

---

## 11. Estado da fase 1

### O que está pronto e funcionando ponta a ponta

| Área | Estado |
|---|---|
| Multi-tenant | Filtro global + testes de vazamento em **todas** as leituras dos 6 blocos |
| Autenticação, papéis, convite, reset de senha | Completo, com rate limiting |
| Schema (13 tabelas, 9 enums, invariantes) | Completo e migrado |
| Integração Evolution (QR, webhook, mídia) | Completa **contra a API real** — falta parear um número |
| Protocolo de envio grava→dispara→confirma | Completo, com reenvio, expiração e freio por conexão |
| Caixa de entrada (lista, thread, responder, atribuir) | Completa, com realtime e cursor |
| Follow-up automático | Completo |
| Semáforo de urgência com desconto de expediente | Completo |
| Meu Dia | Completo |
| Dashboard (API) | Completo |
| Equipe e Conexão (telas) | Completas |

### O que falta para o produto ser vendável

Em ordem de bloqueio:

1. **Parear um telefone e mandar uma mensagem de verdade.** Pendente desde o bloco 3. Tudo abaixo
   depende disto para ser confiável — é o único caminho que ainda nunca rodou com WhatsApp real.
2. **Funil (kanban) e Contatos.** Um CRM sem a tela de cadastrar lead e arrastar card não é um CRM.
   O schema e os índices já estão lá (`ordem_kanban` fracionário, `ix_contatos_kanban`); falta a API
   de escrita e as duas telas.
3. **Tela do dashboard.** A API está pronta; a tela é placeholder.
4. **Tela de configuração** (expediente, faixas do semáforo, dias de follow-up, feriados). Hoje
   exige `UPDATE` no banco — inviável para o cliente final.
5. **Envio de e-mail.** Convidar alguém copiando link de tela não escala além do primeiro cliente.
6. **Onboarding**: cadastro de empresa pela tela, com as 5 etapas semeadas.
7. **Lock distribuído** — só antes da segunda instância, não antes da primeira venda.

Fora da fase 1, registrado para não se perder: histórico de movimentação entre etapas, telefone
secundário por contato, atribuição de custo por campanha, e telemetria persistida da rodada.

---

## 12. Nota operacional

Ao aplicar as migrations eu rodei `dotnet ef database update` sem `NEXORA_CONN`, e o padrão da
`FabricaDbContextDesignTime` aponta para um banco chamado **`nexora`** (o de desenvolvimento é
`nexora_dev`, definido em user-secrets). Isso **criou um banco `nexora` vazio** — schema aplicado,
zero linhas em todas as tabelas. Ele não é usado por nada. Pode ser removido com
`DROP DATABASE nexora;` quando você quiser; não removi por conta própria porque apagar banco é
irreversível.

O `nexora_dev` recebeu a migration `CamadaDeTempo` e os 26 feriados nacionais, e foi restaurado ao
estado anterior depois do teste manual (janela de volta em 8h-20h, conversa 1 sem
`aguardando_desde`).
