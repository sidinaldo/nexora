# Bloco 13 — Áudio na conversa

## O que já existia

O aviso do prompt estava certo, e o inventário veio antes de qualquer linha:

| | Estado |
|---|---|
| `TipoMidia.Audio` no domínio | pronto |
| `audio/ogg` e `audio/mpeg` na whitelist de recebimento | pronto |
| Webhook baixa, valida e grava o áudio recebido | pronto |
| `MediatypeDe` devolve `"audio"` para a Evolution | pronto |
| `GET /api/midia/{id}` autenticado | pronto |

**O recebimento não foi tocado.** Faltavam três coisas: tocar na thread, gravar pela tela, e
guardar a duração.

---

## O formato — a decisão central do bloco

O WhatsApp só trata como **nota de voz** o áudio em **OGG com codec Opus**. Qualquer outra coisa
chega como **arquivo anexo**: o cliente vê um clipe para baixar em vez do balão com onda e botão de
tocar.

Esse é o pior tipo de falha do bloco. **Não dá erro.** A mensagem chega, o vendedor acha que deu
certo, e ninguém abre chamado.

E o `MediaRecorder` do navegador não entrega OGG em todo lugar:

| navegador | grava | precisa de |
|---|---|---|
| Firefox | `audio/ogg;codecs=opus` | nada |
| **Chrome / Edge / Android** | `audio/webm;codecs=opus` | **trocar o contêiner** |
| Safari / iOS | `audio/mp4` (AAC) | **transcodificar** |

### O caminho escolhido: remux, não conversão

O caso do Chrome — que é a maioria — **não precisa de conversão de áudio**. Os bytes de Opus já
estão lá; estão empacotados em Matroska em vez de OGG. Trocar o contêiner é manipulação de bytes:
não decodifica, não recodifica, não perde qualidade, é instantâneo e **não acrescenta dependência
nenhuma**.

`AudioOpus.RemuxarWebmParaOgg` lê os pacotes dos `SimpleBlock` do Matroska e os reescreve em páginas
OGG, com os dois cabeçalhos que o formato exige (`OpusHead` e `OpusTags`).

Três detalhes que custam o arquivo inteiro se errados, e por isso têm teste próprio:

- **O CRC do OGG não é o CRC-32 comum.** Mesmo polinômio (`0x04C11DB7`), mas **sem reflexão de bits
  e sem XOR final**. Usar o do zip produz um arquivo que todo player recusa — e a mensagem de erro
  fala em "corrompido", não em CRC. O teste recalcula com a definição do formato, **reimplementada
  a partir da especificação**, não copiada do código testado: copiar faria os dois errarem juntos.
- **A tabela de segmentos** descreve cada pacote em pedaços de até 255 bytes, e o último pedaço
  (< 255) é o que marca o fim dele. Errar isso faz o decodificador juntar dois pacotes num só.
- **A granule position** é a contagem de amostras em 48 kHz, calculada pelo byte TOC de cada
  pacote. Errada, o player mostra a duração errada ou o WhatsApp corta o áudio no meio.

### Nenhuma dependência nova entrou

**FFmpeg não foi adicionado.** Ele resolveria os três casos, mas entra no Dockerfile e leva ~80 MB
de imagem para converter o que, na maioria das vezes, não precisa de conversão.

**Safari/iOS fica de fora**, e essa é a consequência assumida: AAC → Opus é transcodificação de
verdade e não dá sem FFmpeg. Lá a gravação é **recusada com mensagem que diz o que fazer** — "use o
Chrome, ou grave pelo celular" — em vez de mandar um anexo que o cliente não vai ouvir.

Se o Safari virar requisito, o caminho é FFmpeg no servidor, e aí **avise antes**: muda o
Dockerfile, o tamanho da imagem e o tempo de resposta do envio.

---

## O que foi implementado

### Backend

- **`AudioOpus`** (Core): detecção por bytes, remux WebM→OGG, duração pela granule position, teto
  de 5 minutos.
- **`POST /api/conversas/{id}/audio`**, `multipart`, com `RequestSizeLimit` recusando antes de ler
  o corpo.
- **`ServicoConversas.EnviarAudioAsync`** — **mesmo protocolo do bloco 4**: grava a linha, depois
  chama a Evolution, depois confirma. Reaproveita o `EnviarMidiaManualAsync`, então toda a barreira
  compartilhada continua valendo: recusa de tenant de demonstração, `ConfirmarEnvio`,
  `RegistrarFalha` e log.
- **`mensagens.midia_duracao_segundos`** (migration `20260807140000_DuracaoAudio`).
- Reprodução **pelo `/api/midia/{id}` que já existia**. Nenhum endpoint novo para servir arquivo.

Sem legenda em nota de voz: texto junto viraria uma segunda mensagem no WhatsApp e aqui pareceria
uma só.

### Frontend

- **Gravação por toque**: um clique inicia, outro para. Enquanto grava, a linha de digitação some —
  não dá para falar e digitar ao mesmo tempo, e deixar as duas sugere que dá.
- **Tempo à vista**, com ponto pulsante (que respeita `prefers-reduced-motion`), e **parada
  automática aos 5 minutos**.
- **Prévia antes de enviar**, com descartar: áudio é irreversível do outro lado, e o vendedor
  precisa saber que não gravou silêncio.
- **Player nativo** (`<audio controls>`) nas mensagens de entrada e saída. Ele já traz tocar,
  pausar, barra e velocidade, em todos os navegadores e com leitor de tela — um player próprio seria
  trabalho para reimplementar pior.
- **A duração vem do banco**, não do elemento: o `<audio>` só a conhece depois de carregar os
  metadados, e até lá mostraria `0:00`.
- **O microfone é solto** ao parar e ao sair da tela. Sem isso o indicador de gravação fica aceso no
  navegador e a pessoa se sente ouvida.

### Permissão negada não falha em silêncio

`getUserMedia` recusado mostra *"Não foi possível acessar o microfone. Autorize o uso no navegador e
tente de novo."* — cobre também "sem microfone" e "dispositivo em uso". Sem essa mensagem, clicar no
microfone não faz nada e o vendedor conclui que o botão está quebrado.

---

## Sobre transcrição

**O áudio fica guardado. Não é substituído por nada.** O arquivo é o registro do que aconteceu;
transcrição erra nome, valor e endereço, e sem o original ninguém confere.

A **duração** já é guardada desde agora, como o prompt pediu: aparece no player e permitiria estimar
o custo de uma transcrição futura com um `SUM` — sem varrer arquivo nenhum. Se transcrição entrar um
dia, é complemento **ao lado** do player.

---

## Verificação

- `dotnet build -warnaserror` — **0 avisos, 0 erros**
- `ng build` — limpo
- `dotnet test` — **656 passando** (15 novos)
- frontend — **250 passando** (7 novos)

O WebM dos testes é **montado à mão, byte a byte** — é a única forma de ter um arquivo
determinístico sem depender de um navegador nem versionar binário no repositório.

| teste | o que fixa |
|---|---|
| remux produz OGG com a estrutura que o formato exige | OpusHead, OpusTags, marcas de início/fim, sequência sem buraco |
| **o CRC de cada página confere** | o CRC do OGG não é o do zip |
| a duração sai da granule position | 50 pacotes de 20 ms = 1 s, menos o pre-skip |
| WebM sem pacote é recusado | OGG vazio chegaria como nota de voz de 0 s |
| arquivo truncado não estoura | cortado em 40 pontos diferentes |
| sem CodecPrivate monta OpusHead padrão | desistir seria pior que assumir 48 kHz mono |
| OGG passa direto, sem reempacotar | Firefox já grava certo |
| **o que sai para a Evolution é OGG**, não o WebM gravado | afirmado sobre os BYTES do POST, não sobre o banco |
| MP4 recusado com mensagem que diz o que fazer | e nada vai para o disco |
| áudio acima de 5 min recusado antes do disco | |
| falha mantém a linha com erro, sem duplicar | |
| prévia da caixa diz "🎤 Áudio · 3s" | prévia vazia pareceria que nada foi enviado |
| permissão negada mostra mensagem clara | |
| navegador sem formato compatível avisa e não grava | |
| escolhe OGG antes de WebM | pedir WebM tendo OGG é trabalho à toa |
| gravar/parar/enviar solta o microfone | |
| gravação de menos de 1 s não vira prévia | toque acidental não vira nota de voz |
| o player mostra a duração do banco | `1:07`, não `0:00` |

### Três defeitos meus, achados pelos testes

1. **O fixture de WebM codificava tamanho EBML em 1 byte** (`(byte)(0x80 | n)`), que só serve até
   127. Um cluster de 2350 bytes virava 126, o parser lia até o meio e continuava do lugar errado.
   Quase fui procurar o defeito no parser, que estava certo.
2. **O mesmo erro no fixture de 5 minutos**, com o codificador parando em 2 bytes: o cluster de
   ~700 KB saía truncado e o teste acusava "formato não suportado" em vez do limite de duração.
3. **O `IntersectionObserver` falso estava preso a um `describe`** — o `describe` seguinte voltava a
   usar o de verdade (que nunca dispara sem layout) e falhava por um motivo que não era o dele.

---

## Pendências

**O teste manual com telefone real não foi feito.** São os itens 2, 3 e 4 do critério de pronto, e
todos dependem de você:

1. Gravar e enviar pelo Chrome, e conferir que **chega como nota de voz, não como arquivo anexo** —
   é o item que decide se o remux está certo. Nenhum teste automatizado pode responder isso: eles
   provam que o OGG é estruturalmente válido, não que o WhatsApp o aceita como voz.
2. O mesmo pelo navegador do celular (Android/Chrome grava WebM/Opus, mesmo caminho).
3. Mandar um áudio do celular e conferir que toca na thread.

**O remux nunca passou por um WebM de navegador de verdade** — só pelos que eu montei. Um
`MediaRecorder` real pode escrever `Lacing` nos blocos (vários pacotes num `SimpleBlock`), o que o
leitor atual não desmonta. Não vi isso acontecer em áudio, mas não posso afirmar que não acontece; o
sintoma seria duração absurda ou áudio picotado.

**A migration foi escrita à mão**, porque a `dotnet ef` precisa compilar o projeto da API e o `bin/`
dela estava travado por uma instância no Visual Studio. É uma coluna anulável, sem transformação de
dado — o caso em que escrever à mão é seguro.

**Conferido depois, com o Visual Studio fechado:** um `dotnet ef migrations add` de teste saiu com
`Up()` **vazio**, o que prova que o snapshot ficou consistente com o modelo. A migration de
conferência foi removida.

⚠️ A coluna foi aplicada **nos dois bancos** — `nexora_dev` e `nexora_teste`. O segundo só apareceu
quando a suíte inteira quebrou com *"coluna não existe"*: os testes de integração usam banco
próprio, e migration aplicada só no de desenvolvimento passa despercebida até ali.

**Safari/iOS não grava.** Está registrado acima como decisão, não como esquecimento.

---

## Dois defeitos que só o uso real encontrou — 07/08/2026

O bloco foi entregue com 656 testes verdes e **duas coisas quebradas em produção**. Ambas do mesmo
tipo: a API respondeu `2xx`, a linha ficou `enviada` sem erro, e nada chegou. Nenhum teste
automatizado podia pegá-las, porque as duas dependem do que a Evolution faz com o que recebe.

### Áudio (e imagem) do cliente não chegava

```
POST /chat/getBase64FromMediaMessage  {"message":{"key":{"id":"..."}}}
  -> 400 {"message":["Message not found"]}
```

A Evolution procurava a mídia **no banco dela**, e o `docker-compose` desliga
`DATABASE_SAVE_DATA_NEW_MESSAGE` de propósito — para não manter um segundo acervo de conversa de
cliente. Ela nunca achava nada.

**A correção:** mandar a **mensagem inteira** em vez da chave. A `mediaKey` vem dentro dela, e a
Evolution decodifica sem consultar banco nenhum. Verificado contra a v2.3.7 com uma mensagem real —
com a chave, `400`; com a mensagem, o base64 do OGG.

O sintoma era mudo: `tipo_midia = nenhum`, texto vazio, **sem erro em lugar nenhum**. E não era só
áudio: **toda imagem recebida** entrava assim. Passou despercebido porque as imagens que apareciam
na thread eram as *enviadas*, não as recebidas.

Isto também explica, em retrospecto, por que o `findMessages` do REC-1 devolvia zero: é o mesmo
banco vazio, pelo mesmo motivo.

### Áudio da Nexora não chegava no cliente

A Evolution tem **duas rotas** para áudio, e o bloco usou a errada:

| rota | o que o cliente recebe |
|---|---|
| `sendMedia` com `mediatype=audio` | arquivo **anexo** |
| **`sendWhatsAppAudio`** | **nota de voz** |

As duas devolvem `2xx`. Por isso a linha ficava `enviada`, sem erro, e nada aparecia no celular.

**Era exatamente o risco anunciado no topo deste documento** — "o pior tipo de falha, porque ninguém
abre chamado" — e mesmo assim escapou, porque eu tratei o **formato** (OGG/Opus, com teste) e não
percebi que a **rota** era outra decisão independente. O formato estava certo; o caminho não.

A escolha agora é feita num lugar só (`EnviadorMensagem.Postar`), para envio novo e reenvio não
divergirem.

### O que virou teste

| teste | o que trava |
|---|---|
| a mídia é baixada mandando a **mensagem inteira**, não só a chave | e afirma que o JSON enviado contém `key`, `audioMessage` e `messageTimestamp` |
| áudio sai por `sendWhatsAppAudio` e **não** por `sendMedia` | `Assert.Empty(MidiasEnviadas)` é metade do teste |

### O seletor de emoji

Registrado acima como não implementado (é opcional no prompt). **Confirmado com o dono em 07/08:
não é necessário.** O que parecia "emoji não envia" era a ausência do botão — emoji digitado pelo
teclado do sistema sempre funcionou, e há 206 mensagens de saída com emoji guardadas corretamente
para provar.
