# REC-1 — Recuperação da janela de queda

## O teste que vinha antes do código

A pergunta era: a Evolution reemite o que chegou enquanto o Nexora estava fora? Ela se decompõe
em três, e duas foram respondidas com evidência do próprio container.

### 1. Nexora fora, Evolution no ar — **ela reentrega sozinha**

Os defaults compilados na imagem `evoapicloud/evolution-api:v2.3.7`:

| constante | valor |
|---|---|
| `WEBHOOK_RETRY_MAX_ATTEMPTS` | `10` |
| `WEBHOOK_RETRY_INITIAL_DELAY_SECONDS` | `5` |
| `WEBHOOK_RETRY_MAX_DELAY_SECONDS` | `300` |
| `WEBHOOK_RETRY_USE_EXPONENTIAL_BACKOFF` | ligado (`!== "false"`) |
| `WEBHOOK_RETRY_NON_RETRYABLE_STATUS_CODES` | `[400, 401, 403, 404, 422]` |

Nenhuma está declarada no `docker-compose.yml`, então todas valem por default. Dez tentativas com
backoff de 5s até o teto de 300s dão **cerca de 20 minutos de janela**: reinício de API, deploy e
crash curto estão cobertos **sem código nenhum**.

O que essa tabela também explica é o incidente que motivou este bloco. O `.env` foi copiado do
exemplo e nunca preenchido — `?token=SEGREDO_AQUI` —, a API respondia `401`, e **401 está na lista
dos não retentáveis**. Cada mensagem foi descartada na primeira tentativa. Não foi perda por queda:
foi perda por configuração, e o modo de falha era silencioso dos dois lados.

### 2. Puxar da Evolution depois — **impossível hoje**

```
POST /chat/findMessages/Juba  →  {"messages":{"total":0,...,"records":[]}}
GET  /instance/fetchInstances →  "_count":{"Message":0,"Contact":0,"Chat":0}
```

Ela não guarda nada, porque `DATABASE_SAVE_DATA_NEW_MESSAGE=false` no `docker-compose.yml` — a
mesma decisão que mantém `DATABASE_SAVE_DATA_HISTORIC=false`, e pelo mesmo motivo: não duplicar
conteúdo de conversa num banco opaco.

**Consequência para o escopo:** a rota de reconciliação da seção 2 do prompt **não foi construída**,
porque não teria de onde ler. Construí-la assim daria um botão que sempre devolve zero.

Para viabilizá-la seria preciso ligar `DATABASE_SAVE_DATA_NEW_MESSAGE`, o que cria um segundo
armazenamento de mensagens de clientes fora do nosso banco. É uma decisão de produto e de LGPD,
não um ajuste de configuração — fica registrada, não tomada.

### 3. Evolution fora — **não verificado**

Precisa de celular. A expectativa é que o WhatsApp enfileire para o dispositivo e entregue ao
reconectar, e aí o webhook dispara normalmente — mas isso é inferência sobre o multi-device, não
observação. **Pendente**, com o procedimento no fim deste documento.

---

## O que foi implementado

O achado que reorienta tudo: **mensagem atrasada já entra hoje**, pela reentrega da Evolution. Ela
entra pelo webhook de sempre, com o `messageTimestamp` original — e o processador foi escrito
assumindo que o que chega é o instante mais recente da conversa. Esse é o defeito real, e ele já
está em produção esperando a primeira queda.

Não há caminho de importação, e isso é resposta ao "não crie caminho paralelo": não havia o que
criar. O trabalho foi tornar o caminho existente correto para mensagem fora de ordem.

### Os três guardas (`ProcessadorEventoEvolution.AtualizarConversaAsync`)

| antes | depois |
|---|---|
| `UltimaMensagemEm = quando` sempre | só quando `quando >= UltimaMensagemEm` |
| `AguardandoDesde ??= quando` | o **menor** entre o atual e `quando` |
| `NaoLidas += 1` em toda entrada | não incrementa se há saída **mais nova** que a mensagem |
| `NaoLidas = 0` em toda saída | zera só quando a saída é a mais recente |

O critério em uma frase: **a ordem no tempo, não a ordem de chegada.**

A pior regressão que isso evita é a conversa já respondida voltando a acender o semáforo porque uma
pergunta antiga do cliente só agora foi gravada — o vendedor responderia duas vezes.

Junto: `primeira_mensagem_em` passou a aceitar `> quando` além do `IS NULL`. Uma mensagem atrasada
pode ser a primeira de verdade, e sem isso a métrica de tempo até o produto funcionar mediria a
ordem de entrega.

### A coluna `recuperada_em` (migration `20260807030326_RecuperacaoJanela`)

`NULL` = chegou em tempo real. Preenchida = entrou atrasada.

**Por que uma coluna e não derivação:** `criado_em` recebe o timestamp *da mensagem*, não o da
gravação — de propósito, para a thread ordenar como o cliente viu no celular. O efeito colateral é
que o instante em que nós a vimos não ficava registrado em lugar nenhum.

O critério vive em `Nexora.Core.Whatsapp.JanelaRecuperacao`, num lugar só para a tela e o teste
concordarem:

- **Limiar de 5 minutos.** Entrega normal leva segundos. O número é folgado de propósito: o
  timestamp vem do servidor do WhatsApp e o nosso relógio é outro, e um limiar de segundos
  carimbaria mensagem normal por desvio de relógio. Aviso que aparece sem queda ensina a ser
  ignorado.
- **Teto de 7 dias.** Ele governa o **aviso**, não a entrada. Recusar uma mensagem que o WhatsApp
  nos entregou seria jogar fora dado do cliente; anunciar uma de três meses como "o período em que
  o WhatsApp esteve fora" seria mentira. Ela entra, sem carimbo.
- Só na **entrada**: `fromMe` é o eco do que nós mandamos.

### Sinalização

- **Aviso na caixa de entrada** — "N mensagens recuperadas do período em que o WhatsApp esteve
  desconectado (ontem, 14h20 às 16h05)". O período é o intervalo em que o **cliente escreveu**, não
  o instante da gravação: é o que o vendedor precisa reconstruir.
- Viaja no `StatusPainel`, o payload barato que o shell já busca em polling de 45s — **sem endpoint
  novo e sem requisição extra**, e aparece sozinho quando as atrasadas entram. Agregação no SQL
  sobre `ix_msg_recuperada`, um índice **parcial**: sem o predicado seriam milhões de `NULL`
  indexados para encontrar dezenas.
- **Some sozinho** depois de 24h. Sem botão de dispensar e sem flag — estado derivado, mesma
  escolha do checklist de primeiros passos.
- **Marca na thread**, no rodapé do balão junto da hora, porque é a hora que ela explica. Sem cor
  própria: a mensagem é o que importa, e o vermelho desta tela é o semáforo.
- A mensagem aparece na **posição cronológica** dela — consequência de `criado_em` já ser o
  timestamp da mensagem, nada a fazer.

### Nenhum envio durante a recuperação

O webhook nunca envia, então já estava garantido. O que faltava era **fixar** isso: há teste que
processa cinco mensagens atrasadas e afirma que `TextosEnviados` está vazio e que nenhuma linha de
saída foi criada. Ele existe para quebrar quando alguém "melhorar" o processador com resposta
automática.

O `MotorFollowUp` não é acionado por este caminho — ele roda por agendador, e sua condição de
elegibilidade é *a última mensagem foi de saída*. Uma entrada recuperada torna a última direção
entrada, o que **bloqueia** o follow-up em vez de disparar.

---

## Verificação

- `dotnet build -warnaserror` — **0 avisos**
- `dotnet test` — **597 passando**
- frontend — **237 passando**

Dez testes de integração novos contra Postgres real, rodando em **tenant zero** como o webhook de
produção.

**Provados por mutação, não só por ficarem verdes:** revertidos os guardas para o comportamento
antigo (`maisRecente = true`, `AguardandoDesde ??=`), **3 falharam** — regressão de
`ultima_mensagem_em`, espera na mensagem mais antiga, e semáforo reabrindo em conversa já
respondida. Restaurados, 38/38.

| teste | o que fixa |
|---|---|
| número desconhecido atrasado cria contato | o corte é por tempo, não por "contato conhecido" |
| mensagem em tempo real **não** é carimbada | o contrapeso — carimbo em tudo mataria o aviso |
| mais velha que 7 dias entra, mas sem carimbo | o teto governa o aviso, não a entrada |
| `aguardando_desde` = timestamp da mensagem | mensagem de ontem acende vermelho, não verde |
| fora de ordem, a espera fica na **mais antiga** | `??=` guardaria acidente de entrega |
| `ultima_mensagem_em` não regride | a lista não se embaralha sozinha |
| entrada anterior a uma resposta não reabre o semáforo | o vendedor não responde duas vezes |
| `nao_lidas` com entrada/saída/entrada | conta as de depois da resposta, não o total |
| reprocessar duas vezes não duplica | o `ON CONFLICT` não é contornado |
| nenhum envio sai durante a recuperação | não vira dez follow-ups ao religar |

---

## Pendências

**O teste com celular não foi feito** — nenhum aparelho real foi usado neste bloco. O que falta
observar, e o procedimento:

1. Pare **só a Evolution** (`docker compose stop evolution`), com a API no ar
2. Mande 3 mensagens do outro celular
3. `docker compose start evolution`, espere `connectionStatus: open`
4. Veja se as 3 aparecem na caixa, na ordem certa, com o semáforo marcando a hora original e o
   aviso de recuperação no topo

Se aparecerem, o caminho está fechado ponta a ponta e não falta nada. Se não aparecerem, a única
saída é a reconciliação — que exige a decisão sobre `DATABASE_SAVE_DATA_NEW_MESSAGE` descrita acima.

Repita parando **só a API**: esse caso a tabela de retentativas já responde, mas vale confirmar que
a janela de ~20 minutos se comporta como os defaults dizem.

**Não implementado, e por quê:**

- Rota de reconciliação e botão "Buscar mensagens perdidas" — sem fonte de dados (seção 2 acima)
- Recorte de janela maior que 7 dias na **entrada** — nada é importado, então não há o que
  recortar; o teto atua no aviso

**Achado fora do escopo:** um `401` repetido no webhook significa "sua integração está quebrada" e
hoje sai só como `LogWarning` num terminal que ninguém tem aberto. Foi assim que o token errado
passou despercebido. A tela de Conexão é onde a pessoa procura quando não chega mensagem, e é lá
que isso deveria aparecer.
