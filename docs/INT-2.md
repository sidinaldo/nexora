# INT-2 — QR Code e links rastreáveis

`qrcode` era um valor do enum `origem_lead` que ninguém preenchia. Agora tem quem preencha.

O mecanismo inteiro cabe numa frase: **um código curto viaja dentro do texto pré-preenchido de um
link `wa.me`, e o webhook lê esse código na primeira mensagem.** Nenhuma API externa, nenhum
serviço de terceiro, nenhuma dependência da decisão Evolution × Cloud API.

---

## 1. O formato do código

```
#k7m2
```

`#` + **4 caracteres**, do alfabeto `23456789bcdfghjkmnpqrstvwxyz` — 28 símbolos, 614.656
combinações por empresa.

### Sem vogais

Não é excesso de zelo: o código vai **impresso** em panfleto e cartão de visita de cliente. Com o
alfabeto completo, mais cedo ou mais tarde o sorteio produz uma palavra que ninguém quer ver no
material de uma padaria. Só consoantes e dígitos, e isso não acontece.

### Sem `l`, `0`, `1`

Alguém vai digitar à mão em algum momento, e `l`/`1` e `0`/`O` é o par que mais erra.

### Curto, e no fim da frase

Quatro caracteres passam por detalhe. Um bloco longo de caracteres aleatórios parece erro ou spam,
e a pessoa apaga por estranhamento — destruindo exatamente a coisa que estamos medindo.

O texto é `Olá! Tenho interesse. #k7m2`. A frase natural **na frente** não é estética: ela é o que
faz a mensagem ser enviada inteira. A pessoa lê uma saudação que faz sentido, reconhece como sua, e
manda. Um campo com só `#k7m2` é lixo aos olhos dela.

### O `#` é obrigatório na leitura

Sem ele, qualquer palavra de quatro consoantes viraria candidata e o sistema começaria a atribuir
origem por acidente. E há um lookahead negativo depois dos 4 caracteres: `#bcdfghjk` **não** casa
como `#bcdf` — hashtag comprida é hashtag de campanha, não código nosso.

`CodigoCanal` vive no **Core**, sem dependência de nada, porque os dois lados precisam concordar
caractere a caractere: quem gera (a tela) e quem lê (o webhook). Duas cópias divergiriam, e o
sintoma seria um canal que nunca atribui — sem erro em lugar nenhum.

---

## 2. O que o webhook faz

No **único** ponto em que ele cria contato:

```
1. extrai os candidatos do texto (na ordem em que aparecem, no máximo 5)
2. busca canais ATIVOS da empresa com esses códigos
3. achou → origem = a do canal, origem_detalhe = nome do canal, contador += 1
   não achou → origem = 'whatsapp', como sempre foi
```

### As três coisas que ele NÃO faz

**Não falha.** Código ausente, código inexistente, código de outra empresa — nos três casos o lead
entra normalmente. O rastreio não pode custar o lead.

**Não adivinha.** Há um teste que cria UM canal ativo, manda uma mensagem sem código, e exige que
o contador continue zero. Um sistema que "ajudasse" atribuindo por proximidade de horário ou por
ser o único candidato passaria a inventar origem — e **atribuição errada é pior que ausente**,
porque entra no relatório parecendo verdade e o cliente decide onde gastar em cima dela.

**Não reescreve.** Contato que já existe mantém a origem do dia em que apareceu. O cliente que veio
pelo Instagram em março e voltou pelo panfleto de julho continua sendo lead do Instagram —
sobrescrever faria toda campanha nova reivindicar os leads antigos, e a última sempre pareceria a
melhor.

O contador sobe **junto** com o contato, no mesmo `SaveChanges` e na mesma transação. Separar
deixaria o par "contato criado / lead contado" divergir na primeira falha parcial.

### O texto é gravado como veio

Com o código dentro. A mensagem é registro do que aconteceu; limpá-la antes de gravar deixaria a
thread do vendedor diferente do que o cliente realmente mandou. Se um dia incomodar, quem esconde é
a **exibição**.

### `IgnoreQueryFilters`, e por que ele é o ponto mais perigoso do bloco

O webhook roda em **tenant zero**. Sem `IgnoreQueryFilters` na busca do canal, a consulta volta
vazia — e nenhum canal atribui nada, para sempre, sem erro, sem log, sem sintoma até alguém reparar
que o relatório de origem está todo em "whatsapp".

**Confirmado por mutação:** removi o `IgnoreQueryFilters` e **7 testes reprovaram**. Revertido.

---

## 3. Canal desativado

Desativar **não** quebra o link nem apaga nada. O material impresso continua no mundo, quem escanear
continua caindo na conversa — o que para é a **atribuição**. É como o cliente diz "essa campanha
acabou".

Apagar é outra coisa, e é recusado quando já veio lead: `contatos.origem_detalhe` é TEXTO, cópia do
nome no dia do lead. Não há FK segurando — é justamente por isso que a regra é da aplicação. Apagar
a linha não quebraria nada no banco, e deixaria o histórico apontando para um canal que ninguém mais
consegue explicar.

O `motivoNaoRemove` vem do servidor e é o **mesmo texto** no `title` do botão desabilitado e na
mensagem de erro. Há teste comparando os dois — duas cópias divergiriam, e o sintoma seria um botão
dizendo uma coisa e o toast dizendo outra.

---

## 4. O QR

**Biblioteca: `QRCoder` 1.8.0.**

Codificar QR à mão é Reed-Solomon sobre GF(256), oito máscaras, tabela de versões e modos de
codificação — algumas centenas de linhas cujo modo de falha é um código que **parece certo na tela e
não escaneia no papel**. Não é lugar para código próprio.

A escolha entre as bibliotecas foi por uma razão concreta, não por popularidade: `SvgQRCode` e
`PngByteQRCode` da QRCoder são **100% gerenciados**. As alternativas comuns desenham via
`System.Drawing.Common` (que exige `libgdiplus` fora do Windows, e a API roda em contêiner Linux) ou
SkiaSharp (binário nativo por arquitetura). Aqui não entra nada nativo.

**Nível de correção Q (~25%)**, não o padrão M. Razão física: este QR vai para panfleto, adesivo de
balcão e cartão — superfícies que dobram, sujam e recebem dedo em cima. Q recupera o dobro de M e
custa ~15% a mais de área, que no papel não faz diferença. H (30%) cresceria a matriz sem ganho
prático nessa mídia.

**Gerado no servidor, servido como arquivo.** Nada de API de terceiro montando a imagem: isso poria
o número de WhatsApp do cliente no servidor de outra pessoa e transformaria a impressão de um
panfleto em dependência de disponibilidade alheia.

**SVG e PNG.** O SVG é o que importa — panfleto e placa são impressos em tamanho que nenhum PNG de
tela aguenta, e QR pixelado não escaneia. O PNG existe para post, story e apresentação.

### A falha silenciosa que quase passou

```csharp
$"https://wa.me/{numero}?text={Uri.EscapeDataString(CodigoCanal.TextoDoLink(codigo))}"
```

O `#` do código é **fragmento de URL**. Sem escapar, tudo dali para a frente some antes de chegar ao
WhatsApp: o link abre a conversa, a frase chega truncada, e o código **nunca aparece**. Funcionaria o
suficiente para ninguém desconfiar, e a atribuição nunca aconteceria. Há dois testes só nisso.

---

## 5. O canal pertence a um NÚMERO, não à empresa

O link embute o telefone, então o canal aponta para uma **conexão** — o que faz sentido depois do
ARQ-2, em que a empresa pode ter vários números. "Balcão da loja" atende pelo número de Vendas.

Empresa sem nenhuma conexão pareada **não cria canal**: sairia `https://wa.me/?text=...`, um QR que
escaneia, abre o WhatsApp e não leva a lugar nenhum. Impresso em panfleto, é dinheiro jogado fora — e
o cliente só descobriria depois da gráfica. A tela avisa antes; o serviço recusa; há teste.

A FK é **composta** contra `uq_conexoes_id_empresa`, como conversas e mensagens: garante no banco que
canal e conexão são da mesma empresa. Sem ela, um bug de aplicação poderia apontar o canal de uma
empresa para o número de outra — e o link levaria o lead para o WhatsApp do concorrente.

### O que não tem conserto

O número entra no material **impresso**. Trocar o chip dessa conexão invalida todo panfleto e cartão
já distribuídos, e o sistema não pode fazer nada: o QR já está no papel. A tela avisa ao trocar o
número de um canal, e o alerta de troca de chip do ARQ-2 (`numero_anterior`) é o outro lado do mesmo
sinal.

---

## 6. O código nunca muda

Não existe rota, não existe campo, e há teste no frontend garantindo que ele não vai no corpo do PUT.

Renomear o canal é livre. Trocar o número que atende também. Mas o **código** fica, porque a essa
altura já está impresso em papel que não volta — trocá-lo transformaria todo material distribuído em
link sem atribuição: funcionando, mas mudo.

Renomear também **não** reescreve os leads que já vieram. `origem_detalhe` é uma cópia do nome no dia
do lead; reescrever apagaria a história do lead para acertar um rótulo.

---

## 7. ⚠️ A "taxa de código preservado" NÃO é calculável, e a tela não finge que é

O prompt pede, na tela, "leads recebidos e **taxa de código preservado**". O primeiro número existe.
O segundo **não tem denominador**, e entregá-lo seria inventar um.

O motivo é estrutural: quem hospeda o `wa.me` é a Meta, e ela não nos conta nada. **Um scan que
perdeu o código é indistinguível de alguém que nunca escaneou.** Não há evento, callback ou
contador do outro lado. Qualquer percentual que a tela mostrasse teria um denominador escolhido por
nós — e um número inventado numa tela de relatório é pior que a ausência dele, pelo mesmo motivo que
a atribuição por proximidade de horário foi recusada.

**O que a tela faz em vez disso:** apresenta o contador explicitamente como **piso**, e explica por
quê, no lugar onde a pessoa está olhando o número. Há teste travando esse texto.

**O que tornaria a taxa medível:** um redirecionador nosso — `nexora.app/q/{codigo}` respondendo 302
para o `wa.me`. Aí o scan vira um evento contável e a taxa passa a ser real. Não foi feito por duas
razões de hoje, não de princípio:

- **não existe domínio público do Nexora** — o material impresso apontaria para um host que ainda não
  está de pé (e isso não se corrige depois da gráfica);
- um salto a mais entre o celular e o WhatsApp é um ponto a mais de falha, impresso em papel.

Está registrado no código (`CanalCaptacao`, `ServicoCanais.Link`) para que a decisão não seja
reaberta sem argumento novo.

---

## 8. Schema

**Migration:** `20260806140131_CanaisCaptacao`, aplicada em `nexora_dev`.

```sql
canais_captacao (
    id, empresa_id, nome varchar(80), codigo varchar(4),
    conexao_id, origem origem_lead_enum, ativo, leads_recebidos,
    criado_em, atualizado_em)

uq_canais_empresa_codigo  UNIQUE (empresa_id, codigo)
uq_canais_empresa_nome    UNIQUE (empresa_id, nome)
FK composta (conexao_id, empresa_id) -> uq_conexoes_id_empresa
```

### `varchar(4)`, não `char(4)`

O tamanho é fixo por construção, mas `char` no Postgres **preenche com espaço à direita** — e a
consulta do webhook casa o código com `= ANY(text[])`. Um dia alguém grava um código curto por
engano, o `bpchar` completa com espaços, o `ANY` não casa, e a atribuição para de funcionar em
silêncio. O limite de tamanho já barra lixo; o padding só traria a armadilha.

### Único por EMPRESA, não globalmente

Ao contrário da chave do formulário (INT-1), este código **não resolve o tenant**: quem resolve é o
`instance_name` da conexão que recebeu a mensagem, e a busca já sai recortada por empresa. Único
global gastaria espaço de código à toa e faria duas empresas disputarem `k7m2`.

O nome também é único por empresa, porque vai para `origem_detalhe`: dois canais "Panfleto" tornam
o relatório de origem impossível de ler.

---

## 9. Testes

**Backend: 512 passando** (eram 469 ao abrir o bloco).

| arquivo | o que prende |
|---|---|
| `CanaisDbTests` (20) | os 8 casos do critério, mais: origem do canal ≠ sempre `qrcode`, caixa diferente no código, canal desativado, reentrega do webhook, texto gravado cru, link escapado, QR da API decodificado, isolamento por tenant em 5 métodos, código único por empresa |
| `CodigoCanalTests` (23) | alfabeto sem vogal/ambíguos, extração (13 casos), ordem no texto, corte de hashtags, frase antes do código, **o QR decodificado de volta** |

Os oito do critério, nominalmente:

| critério | teste |
|---|---|
| código conhecido cria contato com a origem do canal | `MENSAGEM_COM_CODIGO_CONHECIDO_CRIA_CONTATO_COM_A_ORIGEM_DO_CANAL` |
| código inexistente cai em `whatsapp`, sem falhar | `CODIGO_INEXISTENTE_CAI_EM_whatsapp_SEM_FALHAR` |
| sem código cai em `whatsapp` | `MENSAGEM_SEM_CODIGO_CAI_EM_whatsapp` |
| código de outra empresa é ignorado | `CODIGO_DE_CANAL_DE_OUTRA_EMPRESA_E_IGNORADO` |
| contato existente não tem a origem reescrita | `CONTATO_QUE_JA_EXISTE_NAO_TEM_A_ORIGEM_REESCRITA` |
| contagem incrementa só na criação | `CONTAGEM_INCREMENTA_SO_NA_CRIACAO` |
| canal com lead não pode ser excluído | `CANAL_COM_LEAD_NAO_PODE_SER_EXCLUIDO` |
| empresa sem conexão não gera canal | `EMPRESA_SEM_CONEXAO_PAREADA_NAO_GERA_CANAL` |

**Frontend: 181 passando** (eram 170). `canais.spec.ts` novo, com 9 casos: botão apagar obedecendo o
servidor, confirmação antes do DELETE, sem número não oferece criar, canal com número caído não
desenha QR, criar abrindo o QR, PUT sem o código, download por HttpClient (não por link direto), o
texto do link visível, e o contador apresentado como piso.

### O QR foi lido de volta

`CodigoCanalTests.O_QR_GERADO_DECODIFICA_DE_VOLTA_PARA_O_MESMO_LINK` pega o PNG que o endpoint
devolve, decodifica os pixels (`LeitorPngQr`, decodificador de PNG mínimo, ~130 linhas) e passa por
um leitor de QR **independente** (ZXing, outra implementação, outro autor). O texto que sai tem que
ser exatamente o link que entrou.

É o que pega o `#` não escapado, o nível de correção trocado por engano e o conteúdo montado com o
número errado. E fica: na próxima mudança ninguém precisa lembrar de escanear.

---

## 10. ⚠️ O que NÃO foi verificado

**O QR não foi escaneado com um celular de verdade.** O critério pedia, e não dá para fazer daqui.
O que existe no lugar é o teste automatizado acima, que prova que os bytes são um QR e que ele
contém exatamente o link certo — mas não prova que a câmera enxerga o papel impresso, nem que o
WhatsApp abre com o texto pronto no aparelho.

**O teste de campo é curto**, e vale a pena fazer antes de qualquer material ir para a gráfica:

1. `/canais` → criar um canal apontando para o número conectado
2. Baixar o **PNG**, abrir na tela do computador
3. Escanear com a câmera de outro celular
4. Conferir que o WhatsApp abre **com a frase pronta**, incluindo o `#código` no fim
5. Enviar, e conferir em `/contatos` que o lead entrou com a origem do canal

O passo 4 é o que importa: é onde apareceria o `#` comido pela URL, se ele tivesse escapado da
revisão.

**Também não verificado:** nada foi aberto em navegador por mim (segue valendo o que os blocos
anteriores registram), e nenhum SVG foi levado a uma impressora.

---

## 11. Pendências e limites

| O quê | Por quê |
|---|---|
| Taxa de código preservado | Não é calculável sem redirecionador próprio — §7 |
| Redirecionador `nexora.app/q/{codigo}` | Precisa de domínio público, que não existe |
| QR sem escanear em celular | §10 |
| Canal não restringe a conexão de ENTRADA | Se o lead escaneia o QR do número A e manda para o número B da mesma empresa, a atribuição acontece do mesmo jeito. É de propósito: o código identifica a **campanha**, e a conexão só diz para onde o link aponta |
| Sem logo no meio do QR | QRCoder suporta, mas exige imagem e reduz a área útil. Não foi pedido |
| Sem QR de "cartão" pronto (arte com nome e chamada) | O que sai é o código puro; montar a arte é trabalho de design, não de sistema |
| Teto de 30 canais por empresa | Freio contra script, não limite de produto. Não é configurável |

---

## 12. Arquivos

**Backend**

```
src/Nexora.Core/Entidades/CanalCaptacao.cs        novo
src/Nexora.Core/Captacao/CodigoCanal.cs           novo — alfabeto, gerar, extrair, texto do link
src/Nexora.Core/Captacao/IGeradorQrCode.cs        novo
src/Nexora.Core/Servicos/IServicoCanais.cs        novo
src/Nexora.Infra/Captacao/GeradorQrCoder.cs       novo — QRCoder, ECC Q
src/Nexora.Infra/Servicos/ServicoCanais.cs        novo
src/Nexora.Infra/Evolution/ProcessadorEventoEvolution.cs   atribuição na criação do contato
src/Nexora.Infra/Persistencia/NexoraDbContext.cs
src/Nexora.Infra/ServicosInfra.cs
src/Nexora.Api/Controllers/CanaisController.cs    novo
src/Nexora.Infra/Persistencia/Migrations/20260806140131_CanaisCaptacao.cs
```

**Frontend**

```
src/app/paginas/canais/{canais.ts,canais.html,canais.css,canais.spec.ts}   novos
src/app/nucleo/servicos/canais.servico.ts                                  novo
src/app/nucleo/modelos.ts          CanalDto, ConexaoParaCanal, Canais
src/app/nucleo/download.ts         + baixarBlob (download autenticado)
src/app/app.routes.ts              /canais
src/app/layout/shell/shell.html    item de menu
```

**Testes**

```
tests/Nexora.Tests/Integracao/CanaisDbTests.cs    novo (20)
tests/Nexora.Tests/Unidade/CodigoCanalTests.cs   novo (23)
tests/Nexora.Tests/Unidade/LeitorPngQr.cs        novo — decodificador de PNG para o teste do QR
```
