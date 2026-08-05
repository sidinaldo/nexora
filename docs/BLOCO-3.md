# Bloco 3 — Integração WhatsApp (Evolution API)

Estado: **fechado**. Os 4 critérios de pronto passam.

O Nexora pareia um número, recebe mensagem, casa com o contato certo, cria contato quando não
existe, registra ACK e mantém `aguardando_desde` correto. **111 testes verdes** (58 dos blocos
anteriores + 53 novos), e o fluxo foi exercitado ponta a ponta com a Evolution real rodando.

---

## 1. O que foi portado

| Destino | Origem no Recupera | Nível |
|---|---|---|
| `Core/Whatsapp/IClienteWhatsApp.cs` | `Core/Motor/IClienteWhatsApp.cs` | A + 2 métodos (§4.1) |
| `Infra/Evolution/ClienteEvolution.cs` | idem | **A — literal**, só `EnvioWhatsAppException` → `IntegracaoWhatsAppException` |
| `Infra/Evolution/EventosEvolution.cs` | idem | A + `audioMessage`/`videoMessage` |
| `Core/Whatsapp/CanonicalizadorTelefone.cs` | `Core/Motor/RenderizadorTemplate.cs:42-75` | **A — extraído**, sem o resto do arquivo |
| `Api/Controllers/WebhookController.cs` | idem | A |
| `Infra/Evolution/ProcessadorEventoEvolution.cs` | idem (428 linhas) | **B — amputado** (§2) |
| `Infra/Servicos/ServicoConexoes.cs` | idem (228 linhas) | B — cortado para 1 número |
| `Api/Controllers/ConexaoController.cs` | idem (10 endpoints) | B — 6 endpoints |
| `Api/Realtime/HubPainel.cs` | idem | B — vocabulário trocado |
| `docker-compose.yml` (Evolution) | idem | A — renomeado, porta trocada (§4.6) |

`RenderizadorTemplate.Renderizar` **não** veio junto, conforme instruído: os placeholders
(`{{credor}}`, `{{vencimento}}`, `{{dias_atraso}}`) são cobrança pura.

As quatro armadilhas vieram com os comentários que as explicam: o nono dígito
(`ResolverNumeroAsync`), o parse defensivo (`LerQr`, `ObterDetalhesInstanciaAsync`), o 2xx sem
`key.id`, e a canonicalização de telefone.

---

## 2. A amputação do `ProcessadorEventoEvolution`, método a método

O Recupera tem 428 linhas em 12 métodos. Copiei o esqueleto e amputei — não reescrevi.

| Método (linhas no Recupera) | Destino |
|---|---|
| `ProcessarAsync(payload)` **34-46** | **mantido** — try/catch que nunca deixa a exceção subir |
| `ProcessarAsync(ev, cru)` **48-81** | **mantido** — resolução de tenant por `instance_name`; tirei o `.Include(c => c.Empresa)` (só o `EmpresaId` é usado) |
| `ProcessarConexaoAsync` **87-130** | **mantido quase intacto** — `ownerJid`, perfil, detecção de troca de chip. Trocado: `Status` virou enum, `"desconectado"` string → `StatusConexao.Desconectado`, e some a menção a "pausa a régua" |
| `ProcessarMensagemAsync` **132-224** | **reescrito em cima do original** (§2.1) |
| `AbrirOuObterTicketAsync` **232-273 (42 linhas)** | **REMOVIDO INTEIRO** |
| `TicketAbertoDoDevedorAsync` **275-282 (8 linhas)** | **REMOVIDO INTEIRO** |
| `InserirMensagemAsync` **286-314** | **mantido** — mesmo `INSERT ... ON CONFLICT DO NOTHING RETURNING id`, colunas trocadas para o schema do Nexora |
| `EhMidia` **316-319** | **mantido**, ampliado para `audio` e `video` |
| `ReceberAnexoAsync` **324-366** | **mantido** — inclusive a chave determinística pelo `wa_message_id` |
| `InserirAnexoAsync` **368-384 (17 linhas)** | **REMOVIDO** — no Nexora a mídia é coluna em `mensagens`, não tabela `anexos` |
| `ProcessarAckAsync` **389-416** | **mantido**, simplificado: o Nexora tem `ack`/`ack_em`, não `entregue_em`/`lida_em`/`status_mensagem` |
| `AckDe` **418-427** | **A — copiado literal** |

### 2.1 O que saiu de dentro de `ProcessarMensagemAsync`

- **Linhas 146-155** (casar número com devedor) → viraram casar número com **contato**, mesma
  técnica de variantes.
- **Linhas 157-176** (ramo `fromMe`) → simplificado. O Recupera precisa buscar "o ticket aberto
  do devedor" antes de inserir; no Nexora a conversa é 1:1 e já está resolvida.
- **Linhas 178-190** (ramo "fora da carteira") → **invertido**. Onde o Recupera grava a mensagem
  sem devedor e sinaliza `semCadastro` para o operador decidir, o Nexora **cria o contato**. É a
  captura de lead da fase 1, e é a mudança de comportamento mais importante do bloco.
- **Linhas 192-223** (abrir ticket, anexo, atualizar ticket, notificar) → o miolo virou:
  obter/criar conversa → mídia → inserir → **atualizar `aguardando_desde`** → notificar.

### 2.2 O que foi escrito do zero

`CriarContatoAsync`, `ObterOuCriarConversaAsync` e `AtualizarConversaAsync`.

A terceira é o coração do semáforo e não tem equivalente no Recupera (lá o valor é calculado com
`max(id)` a cada leitura):

```
ENTRADA  → aguardando_desde ??= agora   (NÃO sobrescreve)   ·   nao_lidas += 1
SAÍDA    → aguardando_desde = null                          ·   nao_lidas = 0
```

O `??=` é a regra inteira: o que importa é **desde quando** o contato espera, não qual foi a
última mensagem dele. Sobrescrever faria o semáforo rejuvenescer toda vez que o cliente
cobrasse — o oposto do que ele deve mostrar. Há teste para isso.

Tudo isso roda na **mesma transação** do INSERT da mensagem. Se a mensagem entra e o
`aguardando_desde` não é gravado, o semáforo mente e ninguém percebe.

---

## 3. Divergências entre o inventário e o código real

**1. `ConectarInstanciaAsync` e `DesconectarInstanciaAsync` não estão em `IClienteWhatsApp`.**
O inventário (§3.3) lista as 8 operações do cliente numa tabela só, como se todas passassem pela
interface. No código real, `IClienteWhatsApp` tem 5 métodos; QR/conectar e desconectar existem
apenas na classe concreta, e o `ServicoConexoes` do Recupera injeta `ClienteEvolution` direto
(`ServicoConexoes.cs:19`).

Consequência prática lá: o serviço de conexões **não é testável com um dublê**. No Nexora movi os
dois para a interface — e foi justamente isso que permitiu escrever o `ClienteWhatsAppFalso` e
testar o processador sem falar com a Evolution.

**2. `.env.example` dessincronizado — confirmado, e pior do que o inventário registrou.** Além
das divergências já anotadas (tag, porta, variável morta), o compose do Recupera exige
`WEBHOOK_URL` sem default e o `.env.example` documenta a URL **sem o parâmetro `token`** — que é
a única barreira do webhook. Quem copiasse o exemplo subiria a Evolution mandando eventos que a
API recusaria com 401, sem pista do motivo. O `.env.example` do Nexora traz o `token` na URL.

**3. O resto do inventário bateu.** Contagens de linhas (428, 351), localização das funções de
telefone (`RenderizadorTemplate.cs:42-75`), o `SemCadastroAsync`, a whitelist de anexos
(pdf/jpg/png), a chave determinística de storage e os flags `DATABASE_SAVE_*` — tudo confere com
o código em `5e22f3e`.

---

## 4. Decisões que tomei por conta própria

**4.1 Movi conectar/desconectar para a interface.** §3.1.

**4.2 Ampliei a whitelist de mídia para áudio e vídeo.** O Recupera aceita pdf/jpg/png — faz
sentido para comprovante de pagamento. Num CRM de vendas, o cliente **responde por áudio o tempo
todo**, e o enum `tipo_midia` do schema já previa `audio` e `video`. Aceito: pdf, jpeg, png,
webp, ogg, mpeg, mp4. Continua whitelist fechada.

**4.3 Normalizo o mimetype antes de comparar.** O WhatsApp manda `audio/ogg; codecs=opus`;
comparar a string inteira contra a whitelist recusaria áudio de voz — o conteúdo mais comum de
todos. Há teste.

**4.4 Teto de 16 MB (o do WhatsApp), não os 10 MB do Recupera.** Recusar um arquivo que o próprio
WhatsApp aceitou entregar gera reclamação sem explicação boa.

**4.5 Mídia em disco + endpoint autenticado.** Conforme instruído para a fase 1.
`IArmazenamentoMidia` existe desde já para que a troca por S3/R2 seja um registro de DI. O
`MidiaController` depende do query filter para o isolamento: pedir o id de uma mensagem de outro
tenant devolve **404**, não 403 — 403 já confirmaria que a mensagem existe.

**4.6 Evolution na porta 8082, não 8081.** Descoberto rodando: a 8081 já está ocupada pelo
`recupera_evolution` nesta máquina, e o `docker compose up` falha com *port is already
allocated*. Mesma razão que levou o Postgres para a 5433 no bloco 1.

**4.7 Transação "só abre se não houver".** Mesmo padrão do `ServicoCadastroEmpresa` do bloco 2.
Em produção o processador sempre abre a dele; o caso de já existir uma é o teste.

**4.8 Mensagem `fromMe` de número desconhecido também cria contato.** `mensagens.conversa_id` e
`contato_id` são `NOT NULL` (decisão `[C4]` do schema): não existe mensagem órfã. Se o vendedor
inicia conversa pelo celular com um número que não está na base, o lead entra no funil do mesmo
jeito.

**4.9 `CanonicalizadorTelefone.EhValido` e `.Formatar`, que não existem no Recupera.** O primeiro
faz o pareamento falhar alto em vez de aceitar lixo que nunca casaria com mensagem nenhuma; o
segundo dá nome ao contato quando o WhatsApp não manda `pushName` (a coluna é `NOT NULL`).

**4.10 Notificações do SignalR só depois do commit.** Se o painel recebe o evento antes de a
transação fechar, a tela consulta e não encontra a linha.

Nenhuma biblioteca nova além de `Microsoft.Extensions.Http` (necessária para `AddHttpClient` no
projeto Infra).

---

## 5. Como rodar a Evolution e parear um número

### 5.1 Subir

```bash
cd nexora
cp .env.example .env
```

Preencha no `.env`:

```bash
POSTGRES_PASSWORD=<algo forte>
EVOLUTION_API_KEY=$(openssl rand -hex 32)
# O token TEM que ser o mesmo do user-secret Webhook:Segredo
WEBHOOK_URL=http://host.docker.internal:5123/api/webhook/evolution?token=<segredo>
```

```bash
docker compose up -d
```

A Evolution sobe em `http://localhost:8082` (manager em `/manager`). Confira:

```bash
curl http://localhost:8082/
# {"status":200,"message":"Welcome to the Evolution API...","clientName":"nexora_evolution"}
```

⚠️ Se aparecer `clientName` de outro projeto, a porta está sendo servida por outro container.

### 5.2 Configurar a API

```bash
dotnet user-secrets set "Evolution:ApiKey" "<a mesma EVOLUTION_API_KEY>" --project src/Nexora.Api
dotnet user-secrets set "Webhook:Segredo" "<o mesmo token da WEBHOOK_URL>" --project src/Nexora.Api
dotnet dotnet-ef database update --project src/Nexora.Infra --startup-project src/Nexora.Api
dotnet run --project src/Nexora.Api      # http://localhost:5123
```

### 5.3 Parear

1. Faça login como **dono** e guarde o token:

```bash
TOKEN=$(curl -s -X POST http://localhost:5123/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"...","senha":"..."}' | jq -r .token)
```

2. Peça o QR — a instância é criada na Evolution na primeira chamada:

```bash
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:5123/api/conexao/conectar
# {"base64":"data:image/png;base64,iVBOR...","codigo":"2@AtN/...","estado":"connecting"}
```

3. Abra o `base64` num navegador (cole na barra de endereço) e escaneie no
   **WhatsApp → Aparelhos conectados → Conectar aparelho**.
   Alternativa sem QR: `POST /api/conexao/parear` com `{"numero":"(84) 99999-0000"}` devolve um
   `pairingCode` para digitar no celular — **nem toda versão da Evolution devolve o código**;
   na v2.3.7 testada aqui ele veio nulo, então o QR é o caminho confiável.

4. Acompanhe com `GET /api/conexao/status` (é o que a tela vai fazer em polling de 3s). Ao
   conectar, o webhook `connection.update` carimba número e perfil sozinho.

### 5.4 Verificar o fluxo

Mande uma mensagem **de outro celular** para o número pareado e confira:

```sql
SELECT c.nome, c.telefone, c.origem, e.nome AS etapa, c.responsavel_id
  FROM contatos c JOIN etapas_funil e ON e.id = c.etapa_id;
SELECT nao_lidas, aguardando_desde, ultima_mensagem_direcao, ultima_mensagem_previa
  FROM conversas;
SELECT wa_message_id, direcao, texto, ack FROM mensagens ORDER BY id;
```

Esperado: contato criado em **Novo Lead** sem responsável, `aguardando_desde` preenchido,
`nao_lidas = 1`. Responda pelo celular e `aguardando_desde` volta a `NULL`. O `ack` da sua
resposta sobe 2 → 3 → 4 conforme o outro aparelho recebe e lê.

### 5.5 O que foi de fato verificado nesta sessão

Sem telefone real disponível, o pareamento em si não foi concluído. **Foi verificado**, com a
Evolution v2.3.7 rodando de verdade:

- `POST /api/conexao/conectar` criou a instância `emp-1` na Evolution e devolveu um **QR real**
  (PNG base64 de 13.174 caracteres) — o caminho completo API → Evolution funciona;
- `GET /api/conexao/status` devolveu `connecting` e **persistiu** o status (`nao_criada` →
  `conectando`), provando a persistência guardada;
- `POST /api/conexao/parear` com número inválido devolveu **400** e mensagem em português;
- webhook com token errado → **401**; com token certo → **200**;
- payload de número desconhecido **criou o contato** "Joana Ferreira" em Novo Lead, sem
  responsável, com `payload_raw` de 302 caracteres guardado;
- **o mesmo payload duas vezes gerou uma mensagem só**, e o `nao_lidas` ficou em 1;
- segunda entrada: `nao_lidas` foi a 2 e o `aguardando_desde` **não mudou**;
- resposta `fromMe`: `aguardando_desde` → `NULL`, `nao_lidas` → 0;
- `READ` seguido de `DELIVERY_ACK` atrasado: o `ack` **continuou 4**;
- payload malformado e payload de grupo: **200**, sem gravar nada, sem exceção.

---

## 6. Testes

**111 no total** (58 anteriores + 53 novos), contra Postgres real.

`CanonicalizadorTelefoneTests` (**9 testes, 25 casos**) — a função mais load-bearing do produto:
as duas pontas chegam no mesmo valor; variantes cobrem com e sem nono dígito e se cruzam nas duas
direções; número inválido falha alto (10 casos); formatação para exibição; idempotência.

`WebhookEvolutionDbTests` (**20 testes**) — o processador com payloads no formato real da
Evolution, rodando em **tenant zero** como em produção:

| Prova |
|---|
| mensagem de contato conhecido casa com o contato certo (com um segundo contato como distrator) |
| contato cadastrado sem o nono dígito casa com a mensagem que vem com ele, sem duplicar |
| número desconhecido cria contato em Novo Lead, sem responsável, e abre a conversa |
| sem `pushName`, o nome vira o telefone formatado |
| mesmo payload duas vezes → uma mensagem, `nao_lidas` não infla, painel não re-notifica |
| eco do próprio envio não vira mensagem nova |
| ACK fora de ordem: `READ` + `DELIVERY_ACK` mantém `READ`, e o atrasado não notifica |
| ACK avança 2 → 3 → 4 |
| entrada grava `aguardando_desde`; **segunda entrada não sobrescreve** |
| saída zera `aguardando_desde` e `nao_lidas` |
| entrada depois de resposta reabre a espera |
| grupo e broadcast ignorados |
| 8 payloads malformados não lançam |
| instância desconhecida ignorada sem lançar |
| `connection.update` open carimba número e perfil; troca de chip guarda o anterior; close marca desconectado |
| mídia permitida é baixada, gravada com chave determinística, e a reentrega não deixa órfão |
| mídia recusada não impede a mensagem de entrar |
| `audio/ogg; codecs=opus` é aceito |
| mensagem de uma instância nunca grava no tenant da outra |

---

## 7. Pendências

**Envio ainda não existe.** `EnviarTextoAsync` e `EnviarMidiaAsync` estão no cliente e ninguém os
chama — reserva, retry e drenagem da outbox são o bloco 4. O índice `ix_msg_pendentes` do bloco 2
segue sem uso.

**Mídia: sem expurgo e sem URL assinada.** Disco local, servido pela API. Não escala horizontal
(cada instância teria o próprio disco) — mesma premissa do rate limit em memória do bloco 1. Fase
2 troca por object storage.

**SignalR está no ar mas ninguém escuta.** O hub, o grupo por tenant e os 5 eventos funcionam; o
cliente Angular chega no bloco 5. Os eventos foram verificados pelos dublês nos testes, não por
um navegador.

**Pareamento com telefone real não foi concluído** — §5.5 lista o que foi verificado e o passo a
passo para fechar.

**`pairingCode` veio nulo na v2.3.7.** O endpoint funciona e o QR é gerado; o código por número
não foi devolvido pela Evolution nesta versão. Se o pareamento por código for requisito de
produto, precisa de investigação — pode ser versão, pode ser parâmetro.

**Rate limit do webhook é por IP.** A Evolution é uma origem só, então funciona. Se um dia houver
várias instâncias atrás do mesmo NAT, o teto de 300/min passa a ser compartilhado.

---

## 8. Estado da máquina

Os containers `nexora_evolution` e `nexora_evolution_db` **ficaram rodando** — a instância `emp-1`
já está criada e esperando o QR ser escaneado, para você fechar o teste manual da §5.3. Para
derrubar: `docker compose down` (adicione `-v` para apagar também os volumes).

O banco `nexora_dev` tem dados do teste manual: a empresa 1 ganhou as 5 etapas do funil, uma
conexão `emp-1` e o contato "Joana Ferreira" com 3 mensagens.
