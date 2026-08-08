# REC-2 — Nenhuma mensagem chega invisível

Bloco nascido de um relato: *"o contato (83) 95278-7173 mandou um item que não consegui
visualizar"*.

---

## O que era

Um **`templateMessage`** do WhatsApp — imagem + texto + botão, o formato que conta de negócio
dispara. O conteúdo estava lá, inteiro e legível:

> Chegou uma nova atualização sobre o sorteio de R$ 2 milhões acontece hoje, às 21h
> Clique para ver mais detalhes 👇🏻
> `[ Mais informações ]` → `w.meta.me/s/21NW8gsIBSIylxC`

A linha foi gravada **vazia**: sem texto, sem mídia e **sem erro**. Na thread virou um balão
branco — nada para ler, nada explicando por quê.

`ConteudoMensagem` conhecia seis formatos (`conversation`, `extendedTextMessage` e as quatro
mídias). Qualquer outro caía num buraco silencioso: `Texto` e `Midia` voltavam nulos e a mensagem
era gravada assim mesmo.

## O tamanho real

Medido em `nexora_dev`: **8 de 1.984 entradas chegaram vazias** — e eram *dois* defeitos, não um.

| Tipo | Vazias | Diagnóstico |
|---|---|---|
| `templateMessage` | 3 | tipo desconhecido |
| `reactionMessage` | 2 | tipo desconhecido |
| `imageMessage` | 2 | **deveria funcionar** — download falhou sem deixar rastro |
| `audioMessage` | 1 | **deveria funcionar** — idem |

O segundo é o pior: mídia que falhava não gravava nada em `erro`, então não havia como distinguir
*"o cliente nunca mandou"* de *"mandou e a gente perdeu"*.

---

## A regra que passa a valer

**Nenhuma linha de mensagem é gravada vazia.** Ou tem conteúdo, ou tem rótulo do que é, ou não
vira linha. Balão branco deixou de ser um estado possível.

---

## 1 · `ConteudoLegivel` — o que o modelo tipado não alcança

`src/Nexora.Infra/Evolution/ConteudoLegivel.cs` (novo). Lê o **JSON cru**, não o modelo tipado, e
isso é deliberado: mapear cada formato novo em classe seria trabalho recorrente para sempre, e a
cada formato esquecido o sintoma voltaria.

| Tipo | Vira |
|---|---|
| `templateMessage` | título + corpo + rodapé + `[rótulo do botão] url` |
| `buttonsMessage` · `listMessage` · `interactiveMessage` e as respostas deles | o texto do corpo |
| `locationMessage` | `📍 Localização` + link do Google Maps montado das coordenadas |
| `contactMessage` · `contactsArrayMessage` | `👤 Contato: <nome>` |
| `pollCreationMessage` | `📊 Enquete: <pergunta>` |
| `eventMessage` | `📅 Evento: <título>` |

**O botão entra no texto** porque é parte da mensagem: sem ele o conteúdo termina em *"clique para
ver mais detalhes 👇🏻"* e não há nada abaixo.

**A rede de segurança:** o que sobrar vira `[mensagem não suportada: <tipo>]` com um
`LogInformation`. É o que garante que a próxima novidade do WhatsApp apareça como "algo chegou" —
e que a gente descubra pelo log, não pelo cliente.

**Figurinha não virou rótulo, virou imagem.** É `image/webp`, e webp já estava na whitelist desde
o MID-1; bastou `EhMidia` reconhecer `sticker`.

---

## 2 · Reação não vira linha — e é sobre o semáforo

⚠️ **A decisão mais importante do bloco**, e ela veio de uma pergunta durante o levantamento:
*"para reação, se for uma linha, vai apontar que o cliente espera uma resposta?"*

Ia, sim. `AtualizarConversaAsync` acende `aguardando_desde` e soma `nao_lidas` em **toda** entrada.
Uma reação virando linha faria um 😘 aparecer como "cliente esperando resposta", com semáforo e
badge — alarme falso numa tela cuja utilidade inteira depende de o alerta significar alguma coisa.

Um emoji não pede ação. `reactionMessage` sai antes de tocar o banco, junto do ruído de protocolo
(`protocolMessage`, `senderKeyDistributionMessage`, `messageContextInfo`).

Foi a pergunta que evitou trocar um balão branco por um alarme falso — que teria sido pior.

---

## 3 · Mídia que falha deixa rastro

`ReceberMidiaAsync` tinha **seis** saídas de falha, todas devolvendo o mesmo `nada`. Agora
`MidiaBaixada` carrega `Erro`, preenchido em cada caminho — payload sem nó `data`, Evolution fora
do ar, Evolution respondeu sem o arquivo, base64 inválido, falha ao gravar — e o valor vai para
`mensagens.erro`.

Na thread, entrada com `erro` e sem mídia mostra *"O anexo não foi recebido. Peça para o cliente
enviar de novo."*, com a causa técnica no `title`. **Sem botão de reenvio**: quem tem o arquivo é
o cliente, não nós.

A doc de `Mensagem.Erro` dizia "último erro do **despacho**" e foi ampliada. A invariante que ela
protege — a linha nunca ser apagada — continua valendo.

---

## 4 · O backfill

`20260807190000_ConteudoLegivelBackfill` — só dados, nenhuma coluna muda. Um `UPDATE` com a mesma
ordem de decisão do `ConteudoLegivel`, em SQL:

1. template → o texto de verdade + o botão;
2. tipo de mídia → `[anexo não recebido]`, porque o tipo **era** suportado e o download falhou —
   chamar isso de "não suportado" mandaria a próxima pessoa investigar o lado errado;
3. o resto → o rótulo com o nome do tipo.

Recorte estreito: só entrada, só o que está vazio nas duas pontas, só onde há payload. Mensagem
que já tem texto não é tocada. **Nenhuma linha é apagada**, nem as de reação.

**Aplicado:** `nexora_dev` — 8 linhas. `nexora_teste` — 0 (base limpa). Restam **zero** entradas
vazias. A mensagem do (83) 95278-7173 voltou a aparecer.

`Down` é vazio de propósito: reverter significaria apagar o texto e devolver os balões brancos.

---

## 5 · Duas correções depois do primeiro deploy

O bloco foi para o ar e duas coisas apareceram em produção no mesmo dia. As duas estão corrigidas,
com teste, e valem mais registradas que escondidas.

### A regressão: imagem sem legenda virou "não suportada"

Uma foto chegou, baixou com sucesso (65 KB, `image/jpeg`, `tipo_midia = imagem`) — e apareceu na
tela **com o aviso `[mensagem não suportada: imageMessage]` em cima dela**.

Causa: o guarda "nenhuma linha vazia" olhava só o texto. Imagem sem legenda é o caso NORMAL — a
foto se explica sozinha —, e ali o conteúdo é o anexo, não o texto. O guarda agora só rotula
quando **não há anexo salvo**:

```csharp
if (string.IsNullOrWhiteSpace(textoMensagem) && midia is not { Salvo: true })
```

⚠️ **A suíte não pegou porque o teste de mídia que já existia nunca olhava o `Texto`** — só
`TipoMidia`, `MidiaMime`, `MidiaBytes` e `MidiaChave`. A asserção entrou nele, mais dois testes
dedicados (imagem e áudio sem legenda). É o tipo de buraco que só aparece quando alguém acrescenta
comportamento perto de um teste que parecia cobrir a área.

### O template tem mais de uma forma

Um segundo `templateMessage` — do Mercado Pago — usa `interactiveMessageTemplate` com a estrutura
`header`/`body`/`footer`, e não `hydratedTemplate`. Mesmo `messageType`, estrutura diferente: caía
no rótulo com o texto inteiro à vista dentro do payload.

`DoInterativo` passou a servir os dois caminhos (`interactiveMessage` solto e o aninhado no
template). A `[Theory]` da varredura ganhou o segundo formato.

### Reparo dos dados

`nexora_dev`: a imagem voltou a ter `texto` nulo (a mídia é o conteúdo), o template do Mercado Pago
recebeu o texto de verdade, e as duas reações históricas viraram `reagiu com 👍` / `reagiu com 😘`
em vez de "não suportada" — elas não são apagadas, mas também não precisavam de um rótulo que
sugere defeito. **Zero linhas rotuladas erradas.**

---

## Verificação

`dotnet build -warnaserror` limpo · `dotnet test` **709 passando** (15 novos) · `ng build` limpo ·
`ng test` **285 passando**.

Os testes usam o **payload real** colhido do banco — o template do sorteio e a reação 😘 —
reduzidos à forma que decide o comportamento (o original tem 48 KB; só a `mediaKey` é um objeto
com 32 campos numerados).

### O teste que vale mais que os outros

`NENHUMA_ENTRADA_E_GRAVADA_VAZIA` é uma `[Theory]` sobre sete payloads, e o último é um **tipo
inventado** (`formatoQueAindaNaoExiste`). Ele não protege os formatos de hoje — protege o
**próximo**, que o WhatsApp vai acrescentar sem avisar.

### Provas por mutação

| Mutação | Teste que caiu |
|---|---|
| `reactionMessage` sai da lista de ruído | `REACAO_NAO_ACENDE_O_SEMAFORO` |
| tipo desconhecido volta a gravar texto nulo | `NENHUMA_ENTRADA_E_GRAVADA_VAZIA` |

---

## Notas de execução

- A instância da API estava rodando no Visual Studio (pid 23412) e segurando
  `src/Nexora.Api/bin`. Os builds saíram com `--artifacts-path` **dentro do repo** — fora dele os
  testes de paridade de minutos úteis não acham `tests/paridade/minutos-uteis.json` e falham por
  engano. A pasta foi removida ao final.
- Por isso o `dotnet ef` não pôde rodar: ele compila para o `bin` travado. O `Designer.cs` foi
  clonado do da migração anterior com a identidade trocada — como **não há mudança de modelo**, o
  snapshot alvo é idêntico, que é exatamente o que o `migrations add` produziria. O SQL foi
  validado numa transação revertida antes de ser aplicado, e a linha de `__EFMigrationsHistory`
  entrou junto, na mesma transação.
- ⚠️ O `Designer.cs` **existe e tem o atributo `[Migration]`** — sem ele o EF não enxerga a
  migração e um banco criado do zero a pula em silêncio. Foi o que aconteceu com a `DuracaoAudio`
  do bloco 13, corrigido no NEG-2.

## Pendências

- **Colar a reação na mensagem reagida**, como o WhatsApp faz. Precisaria casar pelo
  `wa_message_id` e guardar a reação à parte; com reação ignorada, não há nada pendurado.
- **Os botões do `nativeFlowMessage`** (o formato novo, dentro do `interactiveMessageTemplate`)
  ficam de fora: eles guardam os parâmetros como JSON *dentro* de uma string
  (`buttonParamsJson`). Nas mensagens vistas até aqui o link já vem no corpo do texto.
- **A imagem que vem dentro do template** não é baixada. Fica o texto e o botão, que é o conteúdo.
  Se o `getBase64FromMediaMessage` aceitar o template inteiro, entra depois.
- `lateral.spec.ts` continua vermelho (rodapé com 78px, teto de 64) e continua anterior a este
  bloco.
