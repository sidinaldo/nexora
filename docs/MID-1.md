# MID-1 — Emoji, exibição de mídia e envio de imagem e PDF

## O que já existia

O inventário veio antes de qualquer linha, e boa parte do bloco já estava pronta:

| | Onde | Estado |
|---|---|---|
| Recebimento completo (baixa da Evolution, valida, grava com chave determinística) | `ProcessadorEventoEvolution` | pronto |
| `GET /api/midia/{mensagemId}`, autenticado e isolado por query filter | `MidiaController` | pronto |
| `ClienteEvolution.EnviarMidiaAsync` (`POST message/sendMedia`) | `ClienteEvolution` | pronto |
| Whitelist de MIME, teto de 16 MB, `MediatypeDe`, `ExtensaoDe` | `ValidadorMidia` | pronto |
| Armazenamento em disco | `ArmazenamentoDisco` | pronto |
| `tipoMidia`, `midiaNome`, `midiaMime` no DTO da thread | `MensagemDto` | pronto |

**Nada disso foi reconstruído.** O que faltava era o corte da prévia, a exibição e o envio.

---

## Parte 1 — Emoji

**Confirmado, não implementado:** emoji é texto Unicode, a coluna é `text`, o banco é UTF-8 e o
`pushName` passa igual. Não há nada a fazer no caminho de ida e volta.

**O ponto de risco era a prévia, e ele era um defeito real.**

```csharp
texto[..120]   // 120 UNIDADES DE CÓDIGO UTF-16, que não é o mesmo que 120 caracteres
```

Emoji ocupa duas unidades (par substituto). Emoji **composto** ocupa muitas: a família
👨‍👩‍👧‍👦 são quatro pares ligados por ZWJ (11 unidades); a bandeira 🇧🇷 são dois indicadores
regionais; tom de pele é um modificador colado no anterior. Cortar no meio produz meio par
substituto — e a lista da caixa de entrada mostra o losango preto de interrogação, ou o emoji
errado (a bandeira do Brasil virando a letra B).

Agora o corte é por **cluster de grafema** (`StringInfo.GetTextElementEnumerator`), que já conhece
ZWJ, indicadores regionais e modificadores.

E passou a existir **num lugar só**. A regra estava duplicada no `ProcessadorEventoEvolution` e no
`ServicoConversas`: consertar uma e esquecer a outra daria emoji quebrado apenas nas mensagens que
o vendedor mandou — o tipo de bug que se descobre meses depois.

**Seletor de emoji no compositor: não fiz.** É opcional no prompt, o teclado do sistema resolve no
celular, e no desktop o atalho do sistema (Win + ponto) também. Construir um catálogo próprio seria
manutenção sem retorno nesta fase.

### O teste que reprovou código correto

A primeira versão afirmava `Assert.DoesNotContain(previa, char.IsSurrogate)` — e falhou contra a
implementação certa. **Emoji válido É feito de pares substitutos**; a ausência deles não é o
critério. O que denuncia o corte no meio é o substituto **órfão**, que `EnumerateRunes` decodifica
como `U+FFFD`. A asserção estava errada, não o código.

---

## Parte 2 — Exibição da mídia recebida

Antes tudo virava a mesma linha de texto: `📎 audio`.

| Tipo | Agora |
|---|---|
| Imagem | miniatura com teto de 280px de altura; clicar abre em tamanho maior |
| Documento | ícone, nome, **tamanho** e ação de baixar |
| Áudio | cartão com ícone próprio e download (o player fica para o bloco 13) |
| Falha | marcador com o nome e "tentar de novo" — **não some** |

**A legenda aparece abaixo do anexo**, e é a mesma coluna `texto` — venha de quem vier. Separar os
dois faria a mesma informação morar em lugares diferentes dependendo de quem mandou.

**A rota continua fechada.** `/api/midia/{id}` é autenticada por Bearer, e `<img src="...">` não
manda cabeçalho — então a miniatura é buscada como **blob** pelo `HttpClient` (passando pelo
interceptor de auth) e vira `blob:` local. É isso que permite não abrir uma rota pública para
servir arquivo. O teste afirma que o `src` começa com `blob:`, justamente para impedir que alguém
"simplifique" isso depois.

**Carregamento sob demanda** via `IntersectionObserver`, com `rootMargin: 200px` para não piscar.
Thread com cem fotos não baixa tudo de uma vez. Há um caminho de segurança que carrega tudo quando
o observador não existe — pior em desempenho, melhor que anexo que nunca aparece.

Os blobs são revogados no `ngOnDestroy`: sem isso, uma thread com cem fotos segura cem arquivos
enquanto a aba viver.

### Um defeito meu, achado pelo teste

`observarAnexos()` foi escrita e **nunca chamada**. Os anexos ficavam em "Carregando…" para sempre.
A correção não foi chamar nos três lugares que mudam a lista — foi chamar **de um só**, dentro do
`aposRender`, porque ligar em cada caminho deixaria o quarto de fora.

---

## Parte 3 — Envio de imagem e PDF

**Mesmo protocolo do texto: grava a linha, depois dispara.** O `DispararAsync` foi generalizado
para receber o POST como delegate — o que muda entre texto e anexo é só ele. Toda a barreira
continua compartilhada: a recusa de tenant de demonstração, o `ConfirmarEnvio`, o `RegistrarFalha`
e o log. Um caminho novo que esquecesse a checagem de demonstração mandaria mensagem de verdade
para contato fictício.

**O teste prova a ORDEM, não o resultado.** O gancho `AoEnviar` roda dentro da chamada, no instante
em que a Evolution estaria sendo chamada, e consulta o banco: se a gravação viesse depois, ali não
haveria linha. Afirmar depois não provaria nada.

### O conteúdo manda, não a extensão

`AssinaturaArquivo` confere os bytes iniciais: JPEG (`FF D8 FF`), PNG, PDF (`%PDF-`) e WEBP (RIFF
**e** WEBP — RIFF sozinho também é WAV e AVI). O mime usado daqui para frente é o **detectado**; o
`Content-Type` do multipart e o nome do arquivo são texto que o cliente escolhe.

Um executável renomeado para `.pdf`, com `Content-Type: application/pdf`, é recusado — e o teste
usa exatamente esse caso.

O nome também é higienizado: sem caminho (`..\..\etc\passwd.png` vira `passwd.jpg`), com teto de
tamanho, e com a extensão coerente com os **bytes**.

### Uma whitelist, não duas

`PermitidoParaEnvio` é um filtro **sobre a mesma lista** do recebimento: imagem e documento saem,
áudio e vídeo não. Duas listas divergiriam na primeira mudança, e a divergência apareceria como
"o cliente me mandou um webp e eu não consigo devolver um webp".

O teto é o mesmo `ValidadorMidia.TamanhoMaximoBytes`. No controller, `RequestSizeLimit` recusa
**antes de ler o corpo** — sem ele, um upload de 500 MB seria materializado em memória para só
então ouvir "grande demais".

### Falha e tentar de novo

Falha deixa a linha com `erro` e o arquivo guardado. "Tentar de novo" **reaproveita a mesma linha**
— criar outra duplicaria o anexo para o cliente no caso em que a Evolution recebeu e a resposta se
perdeu, que é o modo de falha mais provável de todos. Reenviar o que já foi enviado é recusado, o
que também cobre o duplo clique.

### No compositor

Botão de anexo, arrastar e soltar sobre o rodapé, prévia com opção de remover, legenda no mesmo
envio, barra de progresso (`reportProgress`) e um arquivo por vez. A validação do cliente é
conveniência — evita subir 20 MB para ouvir "não pode"; **a que vale é a do servidor**.

---

## Armazenamento — quanto o envio aumenta o volume

Disco local (`ArmazenamentoDisco`). **Não sobrevive a mais de uma instância nem a container
efêmero**, e continua registrado como limite conhecido. S3 compatível, URL assinada e expurgo por
retenção são fase 2 — não foram resolvidos aqui.

O que muda com este bloco: antes só entrava o que o **cliente** mandava. Agora sai também o que a
**empresa** manda, e os dois somam no mesmo disco.

Tamanhos observados no formato que o WhatsApp entrega:

| | típico |
|---|---|
| foto recomprimida pelo WhatsApp | 80–300 KB |
| áudio de voz (ogg/opus) | 15–60 KB |
| PDF de orçamento (1–3 páginas) | 100 KB–1 MB |
| PDF com fotos ou catálogo | 2–8 MB |

O envio pesa **mais por arquivo** que o recebimento: orçamento em PDF é o caso de uso central deste
bloco, e é justamente o formato mais pesado da tabela.

Uma conta de ordem de grandeza, por empresa:

```
30 conversas ativas/mês × 2 anexos recebidos × 200 KB  ≈  12 MB
30 conversas ativas/mês × 1 orçamento enviado × 800 KB ≈  24 MB
                                                  mês  ≈  36 MB
                                                  ano  ≈ 430 MB
```

**Enviar dobra a conta**, com folga. Cem empresas nesse perfil dão ~43 GB/ano sem nenhum expurgo —
o que ainda cabe num disco, mas não cabe num container efêmero e não sobrevive a um segundo nó.

⚠️ Os multiplicadores são **suposição de perfil de uso**, não medição: não há empresa em produção
para medir. Os tamanhos por arquivo, esses, vêm do formato real que o WhatsApp entrega. Quando
houver um cliente de verdade, `SUM(midia_bytes)` por empresa dá o número em uma consulta — a coluna
existe.

---

## Verificação

- `dotnet build -warnaserror` — **0 avisos, 0 erros**
- `ng build` — limpo
- backend — **608 passando**; 3 falhas conhecidas e explicadas abaixo
- frontend — **243 passando** (6 novos)

As 3 falhas são artefato do **meu contorno**, não do código: sua API estava rodando pelo Visual
Studio e travava o `bin/`, então compilei com `--artifacts-path` para não derrubá-la. O
`ParidadeMinutosUteisTests` sobe diretórios a partir do assembly procurando
`tests/paridade/minutos-uteis.json`, e o caminho isolado fica fora do repositório. Como ele é uma
`[Theory]` com `MemberData`, o arquivo ausente vira **1 falha no lugar de 30 casos** — o que
reconcilia exatamente 641 → 611. Rodado no caminho normal, o número é **641/641**.

| teste | o que fixa |
|---|---|
| prévia com emoji composto no limite | os quatro tipos que quebram: família, bandeira, tom de pele, seletor de variação |
| o corte antigo quebrava mesmo | o contrapeso — sem ele, o teste passaria sem código novo |
| detecta tipo pelos bytes / WEBP exige RIFF **e** WEBP | RIFF sozinho é WAV e AVI |
| **executável renomeado para .pdf não engana** | o caso que a extensão deixa passar inteiro |
| envio grava a linha **antes** do POST | provado pelo gancho, não por asserção posterior |
| extensão trocada não grava nada | nem linha, nem arquivo, nem POST |
| tipo fora da whitelist de envio | áudio e vídeo entram, mas não saem |
| acima do teto, recusado antes do disco | |
| falha mantém a linha com erro | e o arquivo continua guardado |
| tentar de novo reaproveita a linha | 1 mensagem, não 2 |
| reenviar o que já foi enviado é recusado | cobre o duplo clique |
| nome higienizado, extensão dos bytes | `..\..\etc\passwd.png` → `passwd.jpg` |
| imagem vira miniatura via `blob:` | prova que a rota continua autenticada |
| documento mostra nome e tamanho | |
| legenda aparece junto do anexo | |
| **mídia que falha não some** | vira marcador com "tentar de novo" |

---

## Pendências

**O teste manual com telefone real não foi feito.** É o item 3 do critério de pronto e depende de
você: mandar foto e PDF pela tela e conferir que chegam no celular no formato certo; mandar foto e
PDF do celular e conferir que aparecem na thread. Nada neste bloco foi exercitado contra a Evolution
de verdade — só contra o fake.

**O fake de mídia era cego, e isso importa para o que veio antes.** Ele devolvia `"WA-FAKE-MIDIA"`
fixo e ignorava `ErroParaLancar`, `IdParaDevolver` e `AoEnviar`. Um fake que nunca falha daria falso
verde em qualquer teste de falha no caminho de anexo. Corrigido para espelhar o de texto e registrar
o que saiu.

**Áudio continua sem player** — é o bloco 13, e a exibição que ele precisa já está pronta aqui.

**Vídeo não sai**, por decisão do prompt. Continua **entrando**: é conteúdo de negociação.
