# Bloco 4 — Envio confiável de mensagem

Estado: **fechado**. Os 3 critérios de pronto passam.

Build limpo, **129 testes verdes** (111 dos blocos anteriores + 18 novos). O protocolo
grava→dispara→confirma está provado com o banco inspecionado **durante** a chamada à Evolution.

Nenhum agendador foi criado — janela, feriado e gatilho de tempo são o bloco 6.

---

## 1. O que foi portado e o que foi adaptado

| Destino | Origem no Recupera | O que mudou |
|---|---|---|
| `Core/Whatsapp/EnviadorMensagem.cs` | `Core/Servicos/EnviadorMensagem.cs` | O protocolo, intacto. `EnviarDaReguaAsync`/`ReservarDaReguaAsync` → `EnviarLembreteAsync`/`ReservarLembreteAsync`. `ResponderAsync` → `EnviarManualAsync`. Ganhou `ExpirarVencidasAsync` e `EspacarAsync` |
| `Core/Whatsapp/OpcoesEnvio.cs` (no mesmo arquivo) | `Core/Motor/MotorReguaCobranca.cs:8-21` | `IntervaloEntreEnvios` (3s) e `JanelaReenvioDias` (3) — **A, literais**. `FusoHorario` ficou de fora: é do bloco 6 |
| `EnviadorMensagem.EspacarAsync` | `MotorReguaCobranca.cs:220-223` | **A** — mesmo `Task.Delay` com `TimeProvider`, para o teste não esperar 3 s de verdade |
| `Infra/Persistencia/DadosMensagem.cs` | idem | Mesmo SQL cru, colunas do schema do Nexora. `ReenviosPendentesAsync` → `PendentesAsync`; ganhou `ExpirarVencidasAsync` |
| `Infra/Servicos/ServicoConversas.cs` | `ServicoInbox.ResponderAsync` + `AtribuirSeSemDonoAsync` + `AssumirAsync`/`LiberarAsync` | **extraída só a parte de envio e atribuição**. Ficou de fora tudo que é listagem, contexto de dívida e resposta rápida com template |
| `ServicoConexoes.SaudeAsync` | `ServicoConexoes.SaudeAsync` | Adiado do bloco 3. O do Recupera devolve 2 números; este devolve 4 (§2) |

### Diferença estrutural que vale registrar

**O destino do envio viaja como parâmetro, não dentro da entidade.** No Recupera,
`Mensagem.RemoteJid` guarda o telefone e o `EnviadorMensagem` o lê de lá. A tabela `mensagens`
do Nexora **não tem `remote_jid`** — o telefone vive em `contatos`, fonte única. Então as
assinaturas são `EnviarLembreteAsync(reserva, telefone, ct)`. Quem chama já tem o contato em
mãos, e a duplicata de dado não existe.

---

## 2. A política de retry, e o que ela não cobre

O inventário (§3.4) já dizia: o Recupera **não tem** backoff exponencial, dead letter queue,
fila em memória nem worker dedicado. A política é "a rodada varre reservas dos últimos N dias e
tenta de novo". O buraco real: passado N, a mensagem **some do radar em silêncio** — o alerta
conta pendentes sem distinguir "vai ser tentada" de "expirou".

Implementado aqui, conforme instruído:

**1. Contador de tentativas.** Coluna `tentativas smallint NOT NULL DEFAULT 0`, incrementada
tanto na confirmação quanto na falha. Migration `EnvioConfiavel`.

**2. Estado terminal explícito.** Coluna `expirada_em timestamptz`. `ExpirarVencidasAsync`
marca toda reserva com `data_disparo` anterior à janela. A linha **fica** — com texto, erro e
contador — em vez de simplesmente sair do alcance da varredura.

**3. Números separados no endpoint de saúde.** `GET /api/conexao/saude` devolve:

```json
{ "enviadasHoje": 0, "pendentes": 0, "expiradas": 0, "falhasHoje": 0 }
```

`pendentes` e `expiradas` são contas **distintas** de propósito: a segunda é a que exige ação
humana, e somá-las (como o contador único de "outbox" do Recupera faz) esconde exatamente isso.

**Também ajustei o índice `ix_msg_pendentes`** do bloco 2 para incluir `expirada_em IS NULL` no
filtro. Sem isso, toda reserva expirada ficaria no índice para sempre e a drenagem carregaria
linhas que ela sempre descarta.

### Limites conhecidos

- **Sem backoff exponencial.** Uma reserva pendente é retentada a cada rodada, sempre no mesmo
  ritmo. Com a Evolution instável, isso gera tentativas repetidas sem espaçamento crescente.
- **Sem dead letter queue.** "Expirada" é um marcador, não uma fila de reprocessamento: não há
  caminho automático para retomar o que expirou. Retomar é decisão humana, e hoje exigiria SQL.
- **A expiração precisa de gatilho.** `ExpirarVencidasAsync` existe e está testada, mas **nada a
  chama ainda** — quem vai chamar é a rodada do bloco 6. Até lá, `expiradas` fica sempre em 0 em
  produção.
- **`tentativas` não tem teto.** Uma linha pendente por 3 dias com rodada diária chega a 3; se o
  bloco 6 rodar de hora em hora, chega a 72. Não é problema hoje, mas é a variável que um
  backoff futuro vai olhar.

Backoff e DLQ ficam para quando o volume justificar.

---

## 3. O que o teste prova

18 testes novos em `EnvioMensagemDbTests`, contra Postgres real.

**O protocolo (o teste mais importante do bloco).**
`A_linha_e_gravada_ANTES_de_chamar_a_Evolution` usa um gancho no cliente falso que **consulta o
banco no exato momento em que a Evolution estaria sendo chamada**. Verifica três coisas: a linha
já existe, é a mesma que volta no resultado, e `enviada_em` ainda é NULL. Se alguém inverter o
protocolo, este teste quebra alto — não fica na palavra do comentário.

| Prova | Como |
|---|---|
| Evolution com erro → linha permanece | `enviada_em` NULL, `erro` preenchido, `tentativas`=1, texto intacto |
| 200 sem `key.id` → não lança | `enviada_em` preenchido, `wa_message_id` **NULL** (o `NULLIF`), sem erro |
| duas confirmações com id vazio → ambas passam | 2 linhas com `wa_message_id` NULL |
| reenvio não cria linha nova | 1 linha de saída antes e depois; `tentativas`=2, `erro` limpo |
| mesmo lembrete não reserva 2× | `uq_msg_lembrete` barra; Evolution chamada **uma** vez |
| 2 lembretes automáticos no mesmo dia | `uq_lembrete_teto_diario` estoura no segundo |
| 3 mensagens manuais no mesmo dia | todas passam (`lembrete_id` NULL) |
| conexão caída → reserva sem postar | `enviada_em` NULL, `erro` NULL, `tentativas`=0, Evolution não chamada |
| responder com conexão caída | 409 com mensagem clara, **nenhuma linha de saída criada** |
| envio manual zera o semáforo | `aguardando_desde` NULL, `nao_lidas` 0, prévia e direção atualizadas |
| falha no envio **também** zera | decisão consciente, §5.3 |
| responder sem dono atribui | `responsavel_id` e `atribuido_em` preenchidos |
| assumir de outro → 409 | conversa permanece com o dono original |
| reassumir a própria → no-op | não lança |
| expiração marca, não some | `expirada_em` preenchido, texto preservado, idempotente |
| reserva dentro da janela não expira | 0 expiradas, 1 pendente |
| saúde separa as três contas | 1 enviada, 1 pendente, 1 expirada |

**Verificado também pela API real** (Evolution v2.3.7 no ar, instância não pareada):
`POST /api/conversas/{id}/responder` → **409** "O WhatsApp está desconectado…";
`GET /api/conexao/saude` → JSON com as 4 contas; `assumir` → 204 e `responsavel_id` gravado;
reassumir → 204 no-op; `liberar` → 204 e responsável NULL; conversa de outro tenant → 400
"Conversa não encontrada" (o query filter não encontra — não é 403, que já confirmaria a
existência).

O caminho feliz do envio **não foi exercitado por HTTP** porque a instância não está pareada
(sem telefone real disponível, como no bloco 3). Ele está coberto pelos testes de integração.

---

## 4. Divergências entre o inventário e o código real

**1. O inventário chama `EnviadorMensagem` de "nível B, esforço médio". É otimista para
`ServicoInbox`, não para o enviador.** O `EnviadorMensagem` em si transferiu quase literal — o
que custou foi extrair a parte de envio de dentro do `ServicoInbox` (581 linhas), que mistura
envio, listagem, contexto de dívida, resposta rápida com template e resolução de conexão por
ticket. A extração aproveitou ~60 linhas de um arquivo de 581.

**2. `ServicoInbox.ResponderAsync` do Recupera NÃO chama `TocarTicketAsync` quando o envio
falha** (`EnviadorMensagem.cs:94`: `if (ok && mensagem.TicketId is not null)`). Ou seja: se a
Evolution está fora, a mensagem é gravada mas o ticket não é "tocado". No Nexora fiz o oposto
para o equivalente (`aguardando_desde`) — justificativa em §5.3. Não é erro do Recupera; é uma
escolha diferente, e vale registrar que foi consciente.

**3. O inventário descreve o teto diário do Recupera como `(devedor_id, data_disparo)` em
`mensagens`.** Confere. O que muda no Nexora — e o inventário já antecipava — é que a proteção
migrou para `lembretes` (`uq_lembrete_teto_diario`), porque aqui a unidade agendada é o lembrete,
não a etapa da régua. Consequência prática: no Nexora o teto barra na **criação do lembrete**,
não na reserva da mensagem. Ambos testados.

**4. `MotorReguaCobranca.cs` mudou durante o projeto.** O inventário cita `linhas 8-21, 203-206`
para o espaçamento; no HEAD atual (`2d931cb`, com o botão "Disparar agora" que você adicionou) o
`EsperarAsync` está em **220-223**. O bloco de `OpcoesMotor` segue em 8-21. Confiei no código.

---

## 5. Decisões que tomei por conta própria

**5.1 `EnviarManualAsync` não faz reserve-defer nem espaçamento.** Conforme a tabela do prompt.
Mas fui além num ponto: **responder com a conexão caída é recusado com 409**, não reservado para
depois. O vendedor está olhando a tela; deixar a mensagem numa fila invisível faria ele achar que
enviou. Para o lembrete automático o reserve-defer continua sendo o certo — lá não há ninguém
esperando.

**5.2 Não coloquei `EspacarAsync` dentro do enviador.** Ele expõe o método, mas quem decide
espaçar é quem itera o lote — e esse laço é do bloco 6. Colocar um `Task.Delay` dentro de
`EnviarLembreteAsync` faria a resposta manual herdar 3 s de latência se alguém reusasse o método
sem perceber.

**5.3 Falha no envio manual TAMBÉM zera `aguardando_desde` e `nao_lidas`.** A mensagem existe e
aparece na thread marcada como "não chegou". Deixar o semáforo vermelho faria o vendedor
responder de novo — e o contato receberia duas vezes quando a fila drenasse. Escolhi o erro
menos danoso. Há teste explícito, e é o ponto onde divirjo do Recupera (§4.2).

**5.4 `ResponderAsync` devolve 200 com `enviada: false`, não 502.** Registrar a mensagem é
sucesso; a entrega falhar é detalhe. Devolver 502 esconderia o `mensagemId` que a tela precisa
para renderizar o balão. O mesmo raciocínio do `InboxController` do Recupera, e o comentário
veio junto.

**5.5 `SaudeAsync` devolve 4 números, não 2.** O do Recupera tem `enviadasHoje` e `naFila`.
Separei `naFila` em `pendentes` × `expiradas` (a razão de existir deste bloco) e acrescentei
`falhasHoje` — sem ele, "pendentes" não distingue "nunca tentou" de "tentou e falhou hoje".

**5.6 `ConfirmarEnvioAsync` limpa o `erro`.** Reenvio bem-sucedido não deve deixar o erro antigo
na linha, senão a tela mostra "não chegou" numa mensagem entregue. O contador de tentativas
guarda o histórico.

**5.7 Mantive o `NULLIF` mesmo com o índice já excluindo `''`.** São duas defesas. O índice
impede a colisão; o `NULLIF` mantém a coluna honesta — "não sabemos o id" é NULL, não string
vazia. Comentado no código.

Nenhuma biblioteca nova.

---

## 6. Pendências

### O que fica explicitamente para o bloco 6

- **O gatilho de tempo.** Nada chama `EnviarLembreteAsync`, `PendentesAsync`, `ReenviarAsync`
  nem `ExpirarVencidasAsync` em produção. A máquina está montada e testada; falta ligar.
- **Cálculo da janela de atendimento.** `Empresa.JanelaHoraInicio/Fim/DiasSemana` existem desde
  o bloco 2 e ninguém os lê. O reserve-defer recebe `DataDisparo` **já calculada** — a
  assinatura está pronta, o cálculo não.
- **Próximo dia permitido / feriados.** `CalendarioRegua` e `CalculadoraFeriados` continuam sem
  equivalente no Nexora, conforme instruído.
- **A ordem da rodada** (`ExpirarVencidasAsync` antes de drenar) está documentada no código mas
  não existe como código executável.

### Outras

- **Envio de mídia manual não tem endpoint.** `EnviarMidiaAsync` existe no cliente desde o bloco
  3 e ninguém o chama; responder com anexo é trabalho futuro.
- **`EspacarAsync` não é exercitado por teste de tempo.** Usa `TimeProvider`, então é testável
  com relógio falso quando o laço do bloco 6 existir.
- **Sem teste de concorrência real.** As invariantes são de banco e funcionam sob concorrência
  por construção, mas não há teste que dispare dois envios simultâneos para o mesmo lembrete.
- **Caminho feliz de envio não verificado por HTTP** — depende de parear um telefone real (§3).

---

## 7. Estado da máquina

`nexora_evolution` e `nexora_evolution_db` continuam de pé desde o bloco 3, com a instância
`emp-1` criada e aguardando o QR. O banco `nexora_dev` recebeu a migration `EnvioConfiavel` e
tem os dados dos testes manuais dos blocos 3 e 4.
