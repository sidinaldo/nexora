# INT-4 — Conversões de anúncio: do clique à venda (Meta primeiro)

O dono de uma PME anuncia no Instagram e no Facebook. A pessoa clica, entra em contato, vira lead no
Nexora — e compra três dias depois. **A plataforma nunca fica sabendo que aquele clique virou
venda**, então o algoritmo continua otimizando por quem preenche formulário, não por quem compra.

O pixel no site resolve metade: ele vê a visita. A outra metade acontece **dentro do CRM**, dias
depois, e é a que decide o dinheiro.

---

## 0. O diagnóstico: o anúncio do WhatsApp chega no payload cru?

Boa parte deste público **não tem site**: o anúncio é "Clique para WhatsApp" e vai direto para a
conversa. Se o rastreamento só funcionar pelo site, ele não serve para a maioria — então esta
pergunta vem antes de qualquer linha de código de produção.

**Resposta: o canal chega; o anúncio ainda não apareceu porque nunca houve um.**

### O que a consulta mostrou (`nexora_dev`, 2026-09-24)

| medida | número |
|---|---|
| mensagens | 3.294 (2.026 de entrada) |
| com `payload_raw` | 1.898 |
| com `data.contextInfo` | 47 (39 objeto, 8 nulo) |
| com `entryPointConversionSource` | 7 |
| `externalAdReply`, `ctwaClid`, `sourceId`, `sourceUrl`, `referral` | **0** |

As sete com ponto de entrada se dividem em `global_search_new_chat` (5 — alguém buscou o contato na
lupa do WhatsApp) e **`click_to_chat_link` (2)**.

### A `click_to_chat_link` é a prova

É uma pessoa que clicou num **link de captação do próprio Nexora** (`wa.me`, com o código `#ntjb` no
texto) e caiu na conversa. O que chegou, inteiro:

```json
"contextInfo": {
  "mentionedJid": [],
  "groupMentions": [],
  "statusAttributions": [],
  "entryPointConversionSource": "click_to_chat_link",
  "entryPointConversionDelaySeconds": 5
}
```

Três coisas que isso estabelece, e nenhuma delas dependia de ter um anúncio:

1. **A Evolution iça o `contextInfo` para a raiz do `data`** e o entrega sem podar. É exatamente
   onde a referência do anúncio mora (`externalAdReply`, ao lado do `entryPointConversionSource`).
2. **O Nexora guarda o evento inteiro.** `ProcessadorEventoEvolution` insere o corpo do webhook
   verbatim (`payloadCru` → `payload_raw`, `ProcessadorEventoEvolution.cs:528`); o contrato tipado
   (`EventosEvolution.cs`) lê só o que usa e **não descarta o resto**. Nada precisa mudar na entrada
   para o dado chegar — o que falta é alguém ler.
3. **A família de campos do ponto de entrada já funciona nesta instância** (Evolution `v2.3.7`,
   `docker-compose.yml:56`). Um clique de anúncio é o mesmo mecanismo com outro valor.

### O que NÃO é

- **`fbclid` (1 ocorrência)** — está dentro de `urlTrackingMap`, a Meta carimbando um link que **nós**
  mandamos para o cliente. É saída, não entrada; não atribui nada.
- **`statusSourceType` (6)** — homônimo de status/stories, sem relação com anúncio.

### O que fica em aberto, declarado

Não existe, neste banco, uma única mensagem vinda de anúncio — então **os nomes dos campos do caso
"anúncio" não estão provados pelos nossos próprios dados**, só o caminho por onde eles viriam. O
commit 8 (o Clique-para-WhatsApp virar rastro) fica **condicionado a um clique real num anúncio do
dono**, e a consulta abaixo é o que se roda no dia seguinte para conferir.

### A consulta, para repetir

```sql
-- 1. Os campos do anúncio aparecem em algum payload?
WITH p AS (SELECT payload_raw::text AS t FROM mensagens WHERE payload_raw IS NOT NULL)
SELECT count(*) FILTER (WHERE t ILIKE '%externaladreply%') AS external_ad_reply,
       count(*) FILTER (WHERE t ILIKE '%ctwaclid%')        AS ctwa_clid,
       count(*) FILTER (WHERE t ILIKE '%sourceid%')        AS source_id,
       count(*) FILTER (WHERE t ILIKE '%sourceurl%')       AS source_url,
       count(*) FILTER (WHERE t ILIKE '%referral%')        AS referral
FROM p;

-- 2. De onde cada conversa nasceu
SELECT payload_raw->'data'->'contextInfo'->>'entryPointConversionSource' AS fonte,
       direcao, count(*)
FROM mensagens
WHERE payload_raw->'data' ? 'contextInfo'
GROUP BY 1, 2 ORDER BY 3 DESC;
```

⚠️ **`ILIKE '%ctwa_clid%'` não acha `ctwaClid`** — em `LIKE`, `_` é curinga de **um** caractere, e
`ctwa_clid` exige quatro letras depois dele onde `ctwaClid` tem três. Foi o primeiro jeito que eu
rodei, e ele devolve zero por motivo errado. Nomes da Evolution são `camelCase`: procure sem o
separador.

---

## 1. O schema: três tabelas, e o índice que mais importa

Nada consome estas tabelas ainda. Elas vêm primeiro porque tudo depois depende delas, e porque um
schema errado é o único erro deste bloco que fica caro de desfazer.

| tabela | o que guarda |
|---|---|
| `rastreios_lead` | de onde a pessoa veio — uma linha por contato |
| `credenciais_conversao` | o Pixel ID e o token da empresa, por plataforma |
| `eventos_conversao` | a fila de eventos, e o histórico dela ao mesmo tempo |

Mais quatro enums nativos (`fonte_rastreio`, `plataforma_conversao`, `tipo_conversao`,
`status_conversao`), registrados nos **dois** lugares — `HasPostgresEnum` para a migração criar o
tipo e `MapEnum` para o driver ler e escrever. `EnumsNativosDbTests` já cobre a próxima vez que
alguém esquecer o segundo.

### O que decide o bloco: dois índices únicos parciais

```sql
uq_conversoes_compra  UNIQUE (negociacao_id) WHERE tipo = 'compra'
uq_conversoes_lead    UNIQUE (contato_id)    WHERE tipo = 'lead'
```

Reabrir e refechar a mesma venda é gesto **normal** na tela. `Purchase` duplicado não é registro
repetido: é o algoritmo da Meta aprendendo que aquele público converte o dobro do que converte, e
gastando a verba do cliente em cima disso. A trava é de **schema** porque o custo é irreversível do
lado de fora e invisível do lado de dentro — ninguém abre uma tela e vê "mandei duas vezes".

O `WHERE` de cada um é o que permite a mesma pessoa ter **um lead e uma compra**: os dois índices
são sobre `contato_id`, e sem o filtro a compra colidiria com o lead dela. Cada teste prova as duas
metades, e a segunda é a que pega um filtro frouxo demais.

### As decisões que a implementação mudou em relação ao plano

| plano | o que foi feito | por quê |
|---|---|---|
| `plataforma` em `rastreios_lead` | **não existe** | um visitante chega com `fbclid` **e** `gclid` na mesma URL. Uma coluna obrigaria escolher uma, e a escolha seria mentira — `identificadores` já diz quais plataformas marcaram o clique |
| `em_massa` em `eventos_conversao` | **não existe** | não há fonte em lote: a importação não enfileira conversão (abaixo). A coluna nasceria sempre falsa, e coluna que ninguém escreve é coluna que mente. No dia em que houver lote, ela entra com a migração que o criar |
| `tipo` com valor `venda` | `lead` \| `compra` | o índice único do plano já dizia `tipo='compra'`; um nome só |
| `rastreio_lead` | `rastreios_lead` | plural, como `entregas_webhook`, `canais_captacao`, `webhooks_saida` |

**A importação não enfileira conversão, e isto é decisão de produto.** A caixinha "Avisar minhas
integrações" continua valendo para webhook, onde o pior caso é ruído no sistema do cliente. Aqui o
pior caso é outro: `event_time` acima de 7 dias a Meta **recusa** (então a maior parte de uma
planilha antiga viraria `expirado` de todo jeito), e o que passasse ensinaria o algoritmo com gente
que chegou por outro caminho. Irreversível, e do lado de fora.

### As outras decisões de schema

**`identificadores jsonb`, e `utm_*` em coluna.** Coluna para o que se filtra: `utm_campaign` é o
`GROUP BY` de "qual campanha trouxe cliente". `jsonb` para o que só se ecoa: os identificadores de
clique nunca aparecem num `WHERE`, e o conjunto deles é definido pela plataforma — a Meta inventou
`ctwa_clid` depois do `fbclid`, o Google tem `gclid`, `gbraid` e `wbraid`. Uma migração por
parâmetro que um terceiro inventa é uma migração cujo cronograma não é nosso.

**CASCADE no rastro, RESTRICT no evento** — as duas regras de exclusão são opostas de propósito. O
rastro descreve a pessoa e não tem vida sem ela. O evento é registro do que **saiu daqui para um
terceiro**, e registro não some porque o cadastro sumiu; quem limpa o dado pessoal dele é a
anonimização.

**O consentimento é data + autor, não um `bool`.** `WebhookSaida.SomenteIds` é preferência de
formato; isto é uma declaração — "eu tenho base legal no meu site". Declaração sem data e sem autor
não responde a um pedido da ANPD. Mesma natureza de `empresas.equipe_dispensada_em`.

**`ativo` e `desativada_em` são colunas separadas.** A primeira é o interruptor da pessoa; a segunda
é o motor desligando sozinho quando a Meta recusa o token. Na mesma coluna, religar depois de trocar
o token exigiria adivinhar quem desligou — e o passo de "Primeiros passos" não saberia se deve
acender de novo.

**`uq_formularios_id_empresa`**: chave alternativa nova em `formularios_captura`, para
`rastreios_lead.formulario_id` apontar com FK composta `(id, empresa_id)`, como todo mundo neste
schema. O query filter protege leitura; só a FK composta impede a escrita cruzada entre empresas.

### O que os testes provam

Oito invariantes em `InvariantesDbTests`, mais cinco cláusulas do portão em
`CredencialConversaoTests`. **Cada uma foi sabotada, uma por vez, e derrubou exatamente um teste:**

| sabotagem | teste que caiu |
|---|---|
| `uq_conversoes_lead` sem o `WHERE tipo='lead'` | `O_mesmo_contato_nao_vira_dois_leads__mas_vira_lead_E_compra` |
| `uq_conversoes_compra` deixa de ser único | `A_mesma_venda_nao_vira_duas_conversoes_de_compra` |
| `ck_conversoes_negociacao` removido | `Compra_exige_negociacao_e_lead_recusa_uma` |
| `fk_rastreios_contato` vira `RESTRICT` | `Apagar_o_contato_leva_o_rastro_junto_e_nao_o_evento` |
| `uq_rastreios_contato` deixa de ser único | `Cada_contato_tem_no_maximo_um_rastro` |
| `uq_credenciais_empresa_plataforma` deixa de ser único | `Uma_credencial_por_plataforma_e_a_empresa_vizinha_tem_a_dela` |
| `ck_conversoes_expira` removido | `A_janela_de_sete_dias_da_Meta_e_check_no_banco` |
| `fk_conversoes_contato` perde o `empresa_id` | `Evento_de_conversao_nao_pode_apontar_para_contato_de_outro_tenant` |
| cada uma das 5 cláusulas de `PodeEnviar` | um teste de `CredencialConversaoTests` |

⚠️ **Um achado do próprio teste:** a primeira versão de `Apagar_o_contato...` usava
`db.Contatos.Remove`, e o EF decidia sozinho antes de chegar ao banco — estourava no `RESTRICT` e
emitia o `DELETE` do filho no `CASCADE`. O teste passava sem que o banco tivesse opinado. Agora o
`DELETE` é cru, porque é a regra **do banco** que precisa valer: é ela que protege quem escreve por
SQL, por outro serviço ou numa correção manual. E o contato do cenário teve de ser trocado por um
sem conversa — `fk_conversas_contato` barrava o `DELETE` primeiro, e o teste ficaria verde provando
a regra de outra tabela.

Migração `20260924210522_ConversoesDeAnuncio`, aplicada, revertida e reaplicada no `nexora_dev`.

---

## 2. O rastro entra pela captação

Agora o rastro passa a existir. Nada envia nada para a Meta ainda.

### O que mudou no contrato

`LeadDoFormulario` ganhou um `RastreioDoSite? Rastreio` **opcional** — o código que o cliente colou
no site ano passado não manda nada disso e continua funcionando igual. E o `string? origem` do
serviço virou `DadosDaConexao(Origem, Ip, UserAgent)`.

A troca não é organização. O corpo é escrito pelo JavaScript da página e diz o que quiser,
**inclusive o IP de outra pessoa**; estes três o servidor observa. Três parâmetros nulos em fila
também são três chances de trocar `ip` por `userAgent` numa chamada, sem o compilador reclamar.

`RastreioDoSite` tem **campos nomeados, não um dicionário aberto**: o `jsonb` do banco existe para
absorver o parâmetro que a Meta inventar sem pedir migração, mas isto é o corpo de um endpoint
público e sem sessão — dicionário aberto ali é escrita sem teto.

### A query string vai embora antes de gravar

`pagina` e `referencia` entram **sem query string e sem fragmento**. A URL é do site do cliente,
montada por ele, e não temos como auditar o que ele põe lá — e-mail, CPF e token de sessão em query
string são comuns. Guardar tudo importaria dado pessoal de terceiro sem saber que importamos, e a
anonimização não limparia o que não sabe que existe.

Não se perde nada: o produto pergunta "que página trouxe" e "de onde ela veio", e o caminho responde
as duas. `utm_*` e `fbclid` já vêm em campo próprio, lidos pelo formulário antes de postar.

### Os tetos moram na entidade

`RastreioLead.TetoUtm`, `TetoUrl`, `TetoIp`, `TetoUserAgent`, `TetoIdentificador` — usados pelo
mapeamento do EF **e** pelo normalizador. A largura da coluna e a regra de truncagem são a mesma
decisão; escritas duas vezes, o dia em que uma mudasse o endpoint público passaria a estourar "value
too long" **no formulário do site do cliente**.

### Primeiro rastro ganha

`INSERT ... ON CONFLICT (contato_id) DO NOTHING`. O `fbc` do clique original é o elo de atribuição:
é ele que, três dias depois, diz à Meta qual anúncio trouxe a venda.

E o caso que isso serve é o oposto do óbvio: **quem chegou pelo WhatsApp não tem rastro nenhum.** Se
depois clicar num anúncio e preencher o formulário, o rastro entra — não havia nada para preservar.
Por isso a gravação acontece **nos dois ramos** da captação, o do contato novo e o do que já existia.

### O `catch` sozinho era uma promessa falsa

Perder a atribuição é ruim; perder o lead é inaceitável. Então o `INSERT` do rastro é envolvido em
`try/catch` e o lead entra de qualquer jeito.

⚠️ **Só que no Postgres um comando que falha aborta a transação inteira.** Um `catch` seco engoliria
a exceção e deixaria a transação envenenada: todo comando seguinte morreria com `25P02 — transação
atual foi interrompida`. Em produção este caminho não tem transação ambiente, então o defeito não
apareceria — apareceria no dia em que alguém envolvesse a captura numa transação, longe daqui.

Agora há um **SAVEPOINT** quando existe transação em volta, e a promessa é verdadeira nos dois casos.
Foi o teste `SE_O_RASTRO_FALHAR_O_LEAD_AINDA_ENTRA` que mostrou isso — ele derruba a tabela dentro da
própria transação do teste, que a devolve no rollback.

### A anonimização: a pessoa sai, a campanha fica

| apagado | mantido |
|---|---|
| `ip`, `user_agent`, `evento_id`, `identificadores` | `utm_*`, `pagina`, `referencia` |
| `contatos.meta_lead_id` | `meta_ad_id`, `meta_campaign_id`, `meta_form_id` |

A divisão é uma só, aplicada duas vezes: **o que singulariza uma pessoa sai; o que descreve um
anúncio fica.** Dizer que 40 leads vieram da "promo-de-marco" não aponta para ninguém, e apagar isso
destruiria a resposta que o bloco existe para dar em troca de nada.

⚠️ **`contatos.meta_lead_id` era um achado em aberto** — os quatro `meta_*` sobreviviam à
anonimização. Ele identifica **esta pessoa** dentro do sistema da Meta: quem tem acesso à conta de
anúncio volta dele ao nome e ao telefone que ela preencheu. Anonimizar deixando-o seria anonimizar no
nome só.

A linha de rastro **não é apagada**, pelo mesmo princípio da trilha de auditoria: o fato de ter vindo
daquela campanha continua verdadeiro, e é ele que sustenta o número do relatório.

⚠️ **E um defeito que este commit criou e o teste pegou:** `ExecuteSqlRaw` trata a string como
formato, então `'{}'::jsonb` dentro do SQL estoura com "Expected an ASCII digit" — e derrubava a
anonimização **inteira**, não só o rastro. O objeto vazio agora vai por parâmetro. Mesma armadilha do
`DBNull.Value`, que `ExecuteSqlRaw` com marcador posicional também não aceita: o `INSERT` do rastro
usa `NpgsqlParameter` nomeado, e de quebra as dezessete colunas ficaram legíveis.

### O que os testes provam

Sete regras puras em `RegrasRastreioTests`, oito caminhos de banco em `CapturaDbTests`, dois de LGPD
em `ContatosDbTests`. Nove sabotagens, uma por vez:

| sabotagem | teste que caiu |
|---|---|
| a query string volta para a URL | `A_URL_PERDE_A_QUERY_STRING_E_O_FRAGMENTO` |
| vazio deixa de virar nulo | `CAMPO_VAZIO_VIRA_NULO__E_NAO_STRING_VAZIA` (+2) |
| a truncagem some | `CADA_CAMPO_E_CORTADO_NO_TETO_DA_PROPRIA_COLUNA` (+1) |
| o `ON CONFLICT DO NOTHING` some | `O_PRIMEIRO_RASTRO_GANHA__A_SEGUNDA_VISITA_NAO_SOBRESCREVE` |
| a guarda de rastro vazio some | `FORMULARIO_ANTIGO_SEM_RASTREIO_NAO_CRIA_LINHA_VAZIA` |
| o savepoint some | `SE_O_RASTRO_FALHAR_O_LEAD_AINDA_ENTRA` |
| o `meta_lead_id` sobrevive | `Anonimizar_apaga_o_meta_lead_id_e_preserva_os_ids_de_campanha` |
| a limpeza do rastro nao roda | `Anonimizar_apaga_a_PESSOA_do_rastro_e_MANTEM_a_campanha` |

⚠️ **Uma sabotagem não pegou nada na primeira rodada, e o teste era fraco.** Tirar o
`ON CONFLICT DO NOTHING` não derrubava nada: a segunda gravação estourava, o `catch` engolia, e o
resultado observável ficava idêntico. Só que não é idêntico — sem ele, **toda pessoa que preenche o
formulário duas vezes vira um erro no log**, e log cheio de alarme falso é log que ninguém lê no dia
do alarme verdadeiro. A afirmação que faltava era "repetir é fluxo normal, não erro", e ela exigiu um
`LoggerQueGuarda` no lugar do `NullLogger`.

---

## 3. O código do formulário carrega o rastro, e a tela dele volta

Agora o rastro passa a **chegar**. Nada envia nada para a Meta ainda.

### O snippet

O gerador de HTML já existia e estava completo. O que faltava era ele ler de onde a pessoa veio:
`utm_*`, `fbclid`, `gclid`, `ttclid` da URL, e os cookies `_fbp` / `_fbc` que o pixel da Meta escreve.

Três decisões dentro dele, e cada uma tem teste executável:

**1. O rastro é lido na CARGA da página, não no envio.** Entre abrir a página e clicar em "Enviar" a
URL pode ter mudado — site de página única troca a URL a cada navegação e o `fbclid` desaparece dela.
Lido no `submit`, o parâmetro do anúncio já não estaria lá **justamente para quem veio de anúncio**.

**2. Sem pixel, o `fbc` é montado do `fbclid`** — `fb.1.{instante em ms}.{fbclid}`, formato que a
Meta documenta e permite explicitamente quando o cookie `_fbc` não existe. É o que faz o
rastreamento funcionar no site que nunca instalou nada, que é a maioria deles. E **o cookie ganha do
montado** quando existe: ele foi escrito pela própria Meta, com o índice de subdomínio e o instante
certos.

**3. O snippet dispara o evento do pixel sozinho**, com o mesmo `eventoId` que manda para a API:

```js
if (typeof fbq === 'function') {
  fbq('track', 'Lead', {}, { eventID: eventoId });
}
```

Sem isso, o cliente com pixel instalado veria **dois** leads por pessoa — o do navegador e o do
servidor. A alternativa era uma instrução para ele fazer isso à mão, e é uma instrução que um
cliente não técnico erra. `typeof fbq === 'function'` e não `if (fbq)`: a segunda forma lança
`ReferenceError` numa página sem pixel, e o erro apareceria **depois** do envio — o lead entraria, o
aviso não apareceria, e o visitante preencheria de novo.

**Nenhum cookie nosso, nenhum `localStorage`.** Só leitura. Criar armazenamento próprio faria do
Nexora um rastreador no site de terceiro, com a base legal de outra pessoa. O custo é uma limitação
honesta, e a tela a diz: sem pixel, o formulário precisa estar **na mesma página** que recebeu o
clique. Quem quer persistência entre páginas instala o pixel — que é quem resolve isso de verdade.

**A página vai inteira, com query string.** Quem corta é o servidor (`RegrasRastreio.SemQuery`): uma
cópia só da regra, e no lado que ninguém quebra colando HTML errado no site.

### A tela voltou — como aba, e não como a primeira

O painel de formulários tinha saído da tela num bloco anterior, por uma razão boa: o cliente típico
desta ferramenta não tem site nem alguém que cole HTML nele. **Isso continua verdade**, e é por isso
que a aba que abre é a do QR.

O que mudou é o que o formulário passou a valer: é ele que carrega o rastro do anúncio para dentro do
CRM.

⚠️ **E havia um comentário errado no código.** Ele afirmava, em três arquivos, que `/formularios`
"segue acessível pela URL direta para poder desligar um formulário que esteja no ar". Não seguia: a
rota era um redirecionamento para `/captacao` e o painel não era renderizado em lugar nenhum. Quem
tinha formulário publicado não conseguia ver a chave, desligá-lo, nem pegar o código novo.

O teste de navegação tinha o mesmo defeito, e de um jeito que vale registrar: ele se chamava
`/formularios REDIRECIONA PARA A ABA DE FORMULÁRIOS` e afirmava `toBe('/captacao')` — o **nome** já
dizia a regra certa e a **afirmação** dizia outra. Agora as duas dizem a mesma coisa.

### As duas frases que ficam na tela, e não na documentação

> **Já colou uma versão anterior deste código?** Troque pela de agora. A antiga continua recebendo
> leads, mas não diz de onde eles vieram.

Porque quem já colou continua funcionando, **sem nenhum sinal de que algo mudou** — e é o único caso
em que o produto piora em silêncio para quem já é cliente.

> Se o pixel da Meta estiver instalado na mesma página, este código o usa sozinho: nada a configurar.

Porque é a única coisa que falta para o rastreamento ficar completo, e cabe numa linha.

### O que os testes provam

Seis testes executáveis novos em `formularios.spec.ts` — eles **injetam o snippet numa página de
verdade**, deixam o script rodar, disparam o `submit` e interceptam o `fetch`. Ler a string com
regex não provaria nada: o snippet pode conter tudo que se procura e ainda assim não enviar.

`history.replaceState` troca a query string da própria página de teste, que é o mais próximo do que
o navegador do visitante faz — e é o que permite testar `?fbclid=` de verdade.

| sabotagem | testes que caíram |
|---|---|
| o rastro sai do corpo do POST | 5 (todos os do rastro) |
| o `fbclid` deixa de ser lido | 3 |
| o `fbc` deixa de ser montado do `fbclid` | `SEM_PIXEL_NO_SITE_o_fbc_e_montado…` |
| o cookie `_fbc` perde para o montado | `COM_PIXEL_o_cookie_fbc_GANHA…` |
| o pixel recebe outro id | `O_MESMO_evento_id_VAI_PARA_A_API_E_PARA_O_PIXEL` |
| o pixel deixa de ser avisado | `O_MESMO_evento_id_VAI_PARA_A_API_E_PARA_O_PIXEL` |
| a aba padrão passa a ser a de formulários | `A_ABA_QUE_ABRE_E_A_DO_QR` |
| a rota antiga volta a cair na aba errada | `/formularios REDIRECIONA PARA A ABA DE FORMULÁRIOS` |

412 testes no painel, 93 no celular.

---

## 4. A credencial da Meta, por empresa

O cliente conecta. **Nada envia ainda** — o cliente HTTP, o motor e o botão de teste são o commit 6.

### `/integracoes` ganhou abas, em vez de uma tela nova

"Conversões de anúncio" e "webhook de saída" respondem à **mesma** pergunta do cliente: o que este
sistema fala com o que eu já uso. Uma tela por parceiro faria o menu crescer um item por integração,
e o dono — que abre isto uma vez por trimestre — teria de aprender onde cada uma mora.

O conteúdo que existia desceu para `integracoes/webhook/`, virando painel: perdeu `.pagina`, `<h1>`
e o subtítulo, porque quem desenha o cabeçalho agora é o container. Mesmo movimento que a Captação
já tinha feito.

**A aba que abre é o Webhook**, não Anúncios: `/integracoes` já existia com só ele, e quem salvou o
link espera cair na integração que pediu.

**Não há resumo comum**, ao contrário da Captação. Lá os dois números se comparam ("o panfleto trouxe
mais que a landing page?"); aqui "entregas de webhook" e "conversões enviadas" não se comparam com
nada, e somá-los daria um número que não responde pergunta nenhuma.

### Uma limpeza que veio de graça: `.marcador` virou primitiva

A linha de caixinha-com-explicação estava definida em `integracoes.css`, e a tela de importar tinha a
própria cópia (`.aviso-integracao`) — com um comentário **admitindo** que era "o mesmo desenho do
`.marcador` da tela Integrações". O painel de anúncios seria a terceira. Ela subiu para
`styles.css`, e as duas cópias foram apagadas.

### As regras que falham em silêncio, e do lado de fora

**O `GET` nunca devolve o token.** Nem uma vez, ao contrário do segredo do webhook: aquele nós
geramos, então dava para revelá-lo no ato da criação; este o cliente cola do Gerenciador de Eventos,
e não temos o que revelar. A API devolve `EAAG…arar` — quatro caracteres bastam para reconhecer qual
token está lá e não servem para chamar a Graph API.

**Token em branco MANTÉM o anterior**, e este é o defeito mais caro que a tela poderia ter. A tela
não consegue preencher o campo de volta, então o caso normal é salvar com ele vazio para trocar outra
coisa. Apagar aí faria de "trocar o Pixel ID" um jeito de desligar o envio — e o sintoma apareceria
semanas depois, como campanha otimizando errado.

**O consentimento é data + autor, e só é escrito quando MUDA.** Reescrevê-lo a cada salvamento faria
o registro dizer que a declaração é de hoje — justamente a informação que ele existe para guardar.

**Retirar o consentimento, desligar ou remover CANCELA a fila.** O `payload` de cada conversão
pendente guarda SHA-256 de telefone e e-mail; sem token ou sem consentimento nenhuma vai sair, e o
que sobraria é dado pessoal hasheado parado numa tabela esperando o dia em que alguém religue — e
saindo sem que ninguém tenha decidido isso agora. `cancelado` e não `DELETE`: o evento fica
registrado, o dado sai.

**`ativo` e `desativada_em` separados**, e salvar com token novo limpa a desativação do motor. É o
gesto de "troquei o token, tenta de novo"; sem isso a credencial ficaria desativada para sempre
depois do primeiro token recusado.

**O Pixel ID é recusado aqui se não for numérico.** O erro comum é colar a URL do Gerenciador de
Eventos inteira. Recusar agora é uma frase na tela; aceitar é um 400 da Graph API três dias depois,
quando a primeira venda fechar.

**Nenhuma permissão nova:** `ConfigurarEmpresa`, a mesma do webhook. A tabela de permissões já diz
que integração é configuração, e uma permissão por parceiro faria a tabela crescer um item por
integração para responder sempre a mesma pergunta. A fotografia de rotas em `RotasPorPermissaoTests`
ganhou as três linhas novas, no mesmo commit.

### O número que faz conectar

*"12 leads dos últimos 30 dias vieram de anúncio — e a Meta não ficou sabendo de nenhum deles."*

Ele conta `identificadores <> '{}'`, não "leads do formulário": são as pessoas para quem **existe o
que mandar de volta**. Contar todo lead inflaria o número com quem chegou pelo Google orgânico, e a
frase seria falsa.

E só aparece quando **não** está enviando. Dizer "você está perdendo 12 leads" para quem já conectou
seria mentira, e a próxima frase da tela perderia crédito junto.

Para quem já conectou, o mesmo número serve de conferência: um zero ali, com o código novo publicado,
significa que alguém colou o código antigo.

### O que os testes provam

Onze testes de banco em `ConversoesDbTests`, dez de tela em `anuncios.spec.ts`, quatro de container
em `integracoes.spec.ts`. Quinze sabotagens — onze no serviço, quatro no painel —, e cada uma
derrubou o teste da sua regra:

| sabotagem | teste que caiu |
|---|---|
| o token em branco APAGA o anterior | `SALVAR_COM_O_TOKEN_EM_BRANCO_MANTEM_O_ANTERIOR` |
| o `GET` devolve o token inteiro | `O_TOKEN_NUNCA_SAI_PELA_API__SO_O_SUFIXO` |
| a data do consentimento é reescrita a cada salvamento | `O_CONSENTIMENTO_GRAVA_DATA_E_AUTOR…` |
| retirar o consentimento não cancela a fila | 2 (retirar e desligar) |
| desligar deixa de esvaziar a fila | `DESLIGAR_TAMBEM_ESVAZIA_A_FILA` |
| remover a credencial deixa a fila cheia | `REMOVER_A_CREDENCIAL_CANCELA…` |
| o pixel aceita qualquer texto | `PIXEL_QUE_NAO_E_NUMERO_E_RECUSADO_AQUI…` |
| o número conta todo rastro, com anúncio ou sem | `O_NUMERO_QUE_COBRA_CONTA_SO_QUEM_VEIO…` |
| o número ignora a janela de 30 dias | `O_NUMERO_QUE_COBRA_CONTA_SO_QUEM_VEIO…` |
| religar não limpa a desativação do motor | `TOKEN_NOVO_SUBSTITUI_E_RELIGA…` |
| `PodeEnviar` ignora o consentimento | `RETIRAR_O_CONSENTIMENTO_ZERA…` |
| o token vazio vira string vazia no corpo | `SALVAR COM O CAMPO VAZIO MANDA token: null` |
| o campo de token nasce com o sufixo dentro | 2 |
| o número que cobra aparece para quem já conectou | `e NÃO aparece para quem já conectou` |
| a aba padrão de Integrações passa a ser Anúncios | `SÃO DUAS ABAS, e a que abre é o webhook` |

1140 testes de backend, 425 no painel, 93 no celular.

---

## 5. O evento entra na fila

Nada vai para a rede ainda. O cliente HTTP, o motor e o botão de teste são o commit 6.

### As peças puras

**`HashPessoal`** — SHA-256 hex minúsculo. Três regras, e nenhuma delas dá erro quando é violada:

| regra | o que acontece sem ela |
|---|---|
| e-mail em minúsculo | `Joao@X.com` e `joao@x.com` viram duas pessoas para a Meta |
| telefone reusando `CanonicalizadorTelefone` | duas definições de "o mesmo telefone", e leads que não casam |
| **campo vazio vira AUSENTE, nunca `sha256("")`** | todo lead sem e-mail casa com todo lead sem e-mail do mundo |

E o que **não** é hasheado: IP, User-Agent, `fbp` e `fbc`. A Meta os quer em claro. Hasheá-los é o
erro mais silencioso de todos — parece mais seguro, ela responde 200, e o casamento com o clique
deixa de acontecer.

**`MontadorEventoMeta`** — o corpo, sem token e sem código de teste. Os dois são acrescentados pelo
cliente HTTP na hora do envio, por duas razões: o payload guardado **aparece na tela** do cliente no
registro de conversões, e trocar o token não pode invalidar o que já está na fila.

**Um evento por requisição.** A Meta aceita mil, mas um evento inválido recusa o lote inteiro — o
lead da padaria não pode se perder porque o da farmácia veio sem telefone.

### `action_source`: onde o fato aconteceu

| caso | valor | por quê |
|---|---|---|
| lead do formulário | `website` | + `event_source_url`, que é obrigatório só aqui |
| lead do WhatsApp, ou sem rastro | `chat` | é o canal real da maioria deste público |
| **compra, sempre** | `system_generated` | ver abaixo |

A compra acontece quando um vendedor arrasta um card, dias depois, **dentro do CRM** — nenhum canal a
observou. `website` seria mais bonito (herdaria a URL da visita original) e seria falso: não é lá que
a venda aconteceu.

⚠️ **A decidir com o teste real (commit 6):** com `test_event_code`, o Gerenciador de Eventos mostra
se a Meta aceita a compra assim. Se recusar, o caminho é herdar `chat`/`website` do rastro — e a
linha que muda é `MontadorEventoMeta.Origem`.

### Os três pontos onde o evento nasce

| evento | ponto | detalhe |
|---|---|---|
| `Lead` | `ServicoCaptura` | depois do rastro ser gravado — é dele que sai o `fbc` |
| `Lead` | `ProcessadorEventoEvolution` | só em contato NOVO; sem rastro nenhum |
| `Purchase` | `ServicoContatos.MarcarGanhoAsync` | depois do save, com o valor da negociação |
| — | importação de CSV | **não publica** (ver o commit 1) |

**O lead do WhatsApp é o caminho de maior volume**, e é o que quase ficou sem teste. Boa parte destes
clientes não tem site; publicar só no formulário faria o bloco servir à minoria. Cada mensagem de
quem **já** é contato não gera lead de novo — isso ensinaria a Meta que o cliente mais fiel é o que
mais "converte".

### O que o publicador faz, e o que ele nunca faz

**`IgnoreQueryFilters` em tudo.** Dois dos três pontos rodam sem tenant no contexto: a captação
pública (a empresa vem da chave do formulário) e o processador da Evolution (vem do `instance_name`).
Com query filter a busca da credencial voltaria vazia nesses caminhos, e o resultado seria "o lead do
site e o do WhatsApp nunca viram conversão" — em silêncio, enquanto o criado à mão na tela vira.

**Nunca lança.** Um `catch` largo: o chamador está recebendo um lead ou fechando uma venda.

**A hora do FATO, não a de agora.** O lead leva `ocorrido_em` do rastro; a compra, o `ganha_em` da
negociação. A Meta atribui pelo `event_time`, e é isso que permite fechar hoje a venda de um lead de
três meses atrás — o que tem de estar dentro dos 7 dias é o fechamento, não o clique.

**O `event_id` do lead é o do navegador; o da compra é novo.** Reusá-lo faria a Meta tratar a compra
como repetição do lead e descartá-la — justamente o evento que o bloco existe para entregar.

**A colisão é no-op silencioso.** `ON CONFLICT (contato_id) WHERE tipo='lead' DO NOTHING`, e o
equivalente para a compra. Reabrir e refechar é gesto normal; o segundo `Purchase` não é registro
repetido.

### O que os testes provam

Quatorze testes puros em `EventoMetaTests`, treze de banco em `PublicadorConversoesDbTests`.
Dezenove sabotagens, todas pegas — as principais:

| sabotagem | teste que caiu |
|---|---|
| o portão `PodeEnviar` é ignorado | 2 (consentimento e interruptor) |
| a credencial deixa de usar `IgnoreQueryFilters` | `A_CAPTACAO_PUBLICA_ENFILEIRA_MESMO_SEM_TENANT…` |
| o lead usa um `event_id` novo em vez do do navegador | 2 |
| o lead usa a hora de agora em vez da do clique | `O_LEAD_COM_RASTRO_LEVA_O_FBC…` |
| a compra reusa o `event_id` do lead | `O_MESMO_CONTATO_TEM_LEAD_E_COMPRA…` |
| o `ON CONFLICT DO NOTHING` some | `O_MESMO_CONTATO_NAO_GERA_DOIS_LEADS` + `REABRIR_E_REFECHAR…` |
| a janela deixa de ser de 7 dias | `O_LEAD_COM_RASTRO_LEVA_O_FBC…` |
| a compra passa a ser `website` | 3 |
| o `event_time` vai em milissegundos | `O_CORPO_TEM_OS_QUATRO_OBRIGATORIOS_DA_META` |
| o telefone vai em claro | 3 |
| o IP passa a ser hasheado | 3 |
| campo vazio volta a virar `sha256("")` | `CAMPO_VAZIO_VIRA_AUSENTE…` |
| cada um dos 3 pontos deixa de publicar | o teste daquele ponto |

⚠️ **Mesma armadilha do commit 2, e antecipada aqui:** o publicador engole o próprio erro, então
tirar o `ON CONFLICT` deixaria o resultado observável idêntico — uma linha. Os dois testes de
repetição afirmam **também** que nada foi registrado no log (`LoggerQueGuarda`), e é isso que torna a
cláusula load-bearing.

1167 testes de backend.

---

## 6. O envio

O primeiro evento real chega na Meta. Este é o commit em que o bloco passa a fazer o que existe para
fazer.

### `ClienteMeta`

**O token vai no CORPO, nunca na query string.** A documentação da Meta mostra o exemplo com
`?access_token=`. Query string vaza: aparece em log de proxy, em APM, no `Referer` e em qualquer
captura de tráfego intermediária — e o que estaria vazando é a credencial com que se escreve na conta
de anúncio do cliente.

**A versão da Graph API é fixada** (`v21.0`) na URL. Sem ela, a Meta usa a mais antiga ainda
suportada e o comportamento muda sozinho no dia em que ela a aposenta — sem deploy nosso, sem aviso,
e o sintoma seria "as conversões pararam".

**O corpo da resposta É lido**, ao contrário do cliente de webhook, que só olha o status. Aqui:

> ⚠️ **200 com `error` dentro é FALHA.** É o caso que um cliente HTTP comum trata como sucesso, e o
> resultado seria a tela dizendo "entregue" para um evento que a Meta recusou — a pior mentira que
> este bloco pode contar, porque o cliente pararia de investigar.

Com teto de 8 KB, porque é resposta de terceiro. E `error_user_msg` ganha de `message` quando existe:
a primeira é a frase que a Meta escreve para pessoa ler, e é esta que vai para a tela.

### A classificação do erro, e a desativação

| código da Meta | decisão |
|---|---|
| `190`, `102` — token inválido/expirado | **desiste na primeira** e **desativa a credencial** |
| `200`, `10`, `272` — sem permissão | idem |
| `100` — parâmetro inválido | desiste, **sem** desativar: o problema é deste evento, não do token |
| `1`, `2`, `4`, `17`, `32`, `341`, `613`, rede | tenta de novo, backoff 1/5/30 min |
| desconhecido | tenta de novo |

**Por que desativar, e não só desistir:** insistir 3× com token morto são três linhas idênticas e zero
informação. Pior — sem a desativação, a credencial ficaria marcada como **ativa** na tela enquanto
nada sai. É o estado mais confuso possível para quem está olhando.

**`DesativadaEm`, e nunca `Ativo = false`.** O segundo é o interruptor da pessoa; sobrescrevê-lo faria
"religar" virar adivinhação, e o passo de "Primeiros passos" não saberia se deve acender de novo.

### O motor

**50 por rodada, a cada 60 s** — metade do volume e o dobro do intervalo do motor de webhooks. Não é
timidez: a Meta atribui pelo `event_time` do **fato**, não pela hora em que a requisição chegou, então
**atrasar não custa atribuição**. O que se ganha é metade da pressão numa API com limite de taxa (e o
limite de taxa é o código `4`, que custaria tentativas de verdade).

**O expirado sai primeiro, num comando só, sem tocar a rede.** A Meta recusa a requisição inteira por
causa de um evento com mais de 7 dias; gastar uma chamada nele é gastar por nada, e um slot da rodada
também. E ele vira `expirado`, não `falhou` — a diferença é um botão na tela.

**`PodeEnviar` é conferido DE NOVO na drenagem.** Entre enfileirar e drenar passam minutos, e é
exatamente nessa janela que alguém retira o consentimento. Sem esta checagem, o dado pessoal sairia
depois de a pessoa ter dito para não sair.

**A credencial é buscada por EMPRESA**, num dicionário: a rodada é uma só para todas, e o pior defeito
imaginável deste bloco é o telefone do cliente de uma empresa saindo pelo pixel de outra.

### O expurgo, e a assimetria deliberada

30 dias de registro de conversões, apagados na rodada **diária** (ao lado do de webhooks — trabalho
diário mora lá).

⚠️ **O rastro não tem prazo.** A venda pode fechar em três meses, e o `Purchase` precisa do `fbc` do
clique original. `rastreios_lead` morre com a anonimização do contato, não com o calendário.

### O botão de teste

Manda **dado sintético** (`teste@nexora.app`, `5500000000000`) e **não grava nada na fila**. As duas
decisões são a mesma: um contato real faria o botão enviar uma conversão de verdade, e o `Lead` daquela
pessoa sairia duas vezes no dia em que ela virasse lead mesmo — porque o único parcial já estaria
ocupado.

**Não passa pelo portão de consentimento**, e não deveria: ele protege o dado de uma *pessoa*, e aqui
não há pessoa nenhuma. Exigi-lo faria a ordem de configuração ser "declare que tem autorização, depois
descubra se o token funciona" — a ordem errada.

**Mas a desativação vale**, e é o ponto do botão: se a Meta recusou o token agora, é isto que o dono
precisa ver na tela, em vez de descobrir semanas depois que nada saiu. E um teste que **funciona**
religa o que o motor havia desligado — testar depois de trocar o token é como se diz ao sistema que
resolveu.

⚠️ **Sem código de teste preenchido, a tela avisa que o evento entrou como lead real.** A resposta da
Meta é a mesma "aceitei" nos dois casos; sem a frase, o cliente poluiria o próprio pixel sem saber.

### O reenvio

Só o que **falhou**. `pendente` já vai ser tentado sozinho, `entregue` mandaria o mesmo evento duas
vezes, e `expirado` nunca vai funcionar.

Duas guardas a mais, e a segunda veio de um teste:
- **fora dos 7 dias é recusado com a razão** — entre a falha e o clique podem passar dias;
- **a credencial precisa poder enviar.** Descoberto quando o teste do reenvio falhou: depois de um
  `190` o motor desativa a credencial, e reenviar ali devolveria a linha para a fila só para ela
  falhar de novo na rodada seguinte. A frase diz o que fazer primeiro.

### O que os testes provam

Vinte e seis testes puros (`ClienteMetaTests` + `PoliticaConversaoTests`), dezessete de banco
(`MotorConversoesDbTests`), onze novos de tela. **Vinte e duas sabotagens, todas pegas.**

| sabotagem | teste que caiu |
|---|---|
| o expirado deixa de sair antes da rede | 2 |
| o expirado vira `falhou` | 2 |
| a credencial deixa de ser conferida na drenagem | `DESLIGAR_ENTRE_ENFILEIRAR_E_DRENAR…` |
| a desativação por token morto não acontece | 2 |
| a desativação mexe no `Ativo` da pessoa | `TOKEN_RECUSADO_DESISTE_NA_PRIMEIRA…` |
| o permanente passa a tentar de novo | 3 |
| o `fbtrace_id` deixa de ser guardado | `O_EVENTO_ACEITO_VIRA_ENTREGUE_COM_FBTRACE` |
| o teto por rodada some | `A_RODADA_LEVA_NO_MAXIMO_CINQUENTA` |
| o expurgo apaga sem olhar a data | `O_EXPURGO_NAO_TOCA_NO_QUE_AINDA_ESTA_DENTRO…` |
| o token volta para a query string | `O_TOKEN_VAI_NO_CORPO__NUNCA_NA_QUERY_STRING` |
| a versão da Graph API sai da URL | `A_URL_LEVA_A_VERSAO_FIXADA_E_O_PIXEL` |
| 200 com `error` dentro passa a ser sucesso | `DOIS_ZERO_ZERO_COM_ERROR_DENTRO_E_FALHA` |
| o código de teste vai sempre, mesmo vazio | `O_CODIGO_DE_TESTE_VAI_QUANDO_EXISTE…` |
| a frase para pessoa perde da técnica | `A_FRASE_PARA_PESSOA_GANHA_DA_TECNICA…` |
| o botão de teste grava na fila | `O_BOTAO_DE_TESTE_MANDA_DADO_SINTETICO…` |
| o reenvio aceita entregue / expirado / credencial morta | 3 |
| a tela oferece reenvio para o expirado | `O_EXPIRADO_NAO_GANHA_BOTAO_DE_REENVIO` |

⚠️ **Uma sabotagem não pegou nada na primeira rodada**, e o teste era fraco: mandar `test_event_code`
sempre, mesmo nulo, passava — porque `Assert.Null` no indexador não distingue "chave ausente" de
"chave presente valendo null" (`JsonObject["x"] = null` grava um nó nulo, e ler de volta dá null nos
dois casos). A afirmação passou a ser sobre o **texto** enviado. E a diferença importa: a chave
presente e nula faz a Meta tratar o evento como de teste com um código que não existe — ele não
apareceria em lugar nenhum.

1210 testes de backend, 436 no painel, 93 no celular. A API subiu, registrou as duas drenagens
("Drenagem de webhooks a cada 00:00:30", "Drenagem de conversões a cada 00:01:00") e foi encerrada.

### O que falta para fechar o bloco

⚠️ **O teste ponta a ponta com a Meta de verdade**, e ele depende do dono: um pixel, um token, e um
`test_event_code`. É o que vai responder a única pergunta que nenhum teste automatizado responde —
**se ela aceita o `Purchase` com `action_source: system_generated`**. Se recusar, a linha que muda é
`MontadorEventoMeta.Origem`.

---

## 7. A jornada, o passo e o aviso

O produto passa a cobrar quem não conectou — e a mostrar, na tela do contato, o que a Meta ficou
sabendo sobre aquela pessoa.

### A jornada no contato

Bloco **"De onde veio"**, ao lado da origem do cadastro e da campanha do ciclo, e com rótulo
diferente dos dois de propósito: "Origem" é o canal, "Voltou pela campanha" é o ciclo de agora, e este
é o **clique** que trouxe a pessoa.

Mostra a campanha (`utm_campaign`), a origem (`utm_source` · `utm_medium`), o anúncio
(`utm_content`), a página de entrada, o referenciador, a hora — e o **estado de cada evento**
("Lead avisado à Meta em 12/03", "Compra: A Meta recusou o token").

⚠️ **Não mostra IP nem User-Agent, e eles nem chegam do servidor.** Existem para a Meta casar quem
clicou com quem virou lead; na tela não servem para nada — o vendedor não decide nada com um IP — e
exibi-los seria expor dado pessoal de alguém que **nem é cliente** a todo usuário do tenant, por
estética. Os identificadores de clique também não vêm crus: vêm como `deAnuncioPago`, que é a única
pergunta que a tela faz.

O teste afirma isso **serializando a jornada** e procurando o IP, o User-Agent e o `fbclid` no JSON —
para pegar também um campo que alguém acrescente sem pensar.

**Lista de eventos vazia não diz "nenhum evento".** Vazia quer dizer que a empresa não conectou
anúncio, e "nenhum evento" faria parecer defeito. A frase certa é o convite, com o link para a aba.

### O passo em "Primeiros passos"

⚠️ **Ele só existe quando há anúncio chegando.** Padaria, salão, loja de bairro: a maioria deste
público não anuncia. Um quarto passo **fixo** deixaria o checklist permanentemente incompleto para
ela, e "Primeiros passos" viraria a tela que nunca some — o oposto do que ela é.

Então a pergunta é sobre **fato**, não sobre oportunidade: chegou lead com identificador de clique nos
últimos 30 dias? Se não, o passo não existe. Se sim, ele é um número:

> **Conecte seus anúncios** — 3 leads dos últimos 30 dias vieram de anúncio, e a Meta não sabe que
> eles viraram cliente.

Passo genérico é conselho; passo com número é fato.

**Derivado de `PodeEnviar`**, e por isso ele **volta a acender sozinho** quando o motor desativa a
credencial por token recusado. Uma flag de "já configurou" diria que está tudo pronto enquanto nada
sai — exatamente o defeito que `ServicoOnboarding` inteiro existe para evitar.

**A rota leva direto na aba** (`/integracoes?aba=anuncios`): `/integracoes` seco abre no webhook, e a
pessoa chegaria numa tela que não é a que o passo prometeu.

**Dispensável** por `empresas.anuncios_dispensados_em`, com o mesmo carimbo idempotente
(`WHERE ... IS NULL`) dos outros dois. É o único caso em que o passo ficaria aceso para sempre: quem
anuncia e, mesmo assim, não quer mandar dado para a Meta.

### O aviso na Captação

O mesmo número, na tela onde o dono pensa em "de onde vêm meus leads".

⚠️ **Aqui e não só no passo**: o painel de primeiros passos some depois que o dono o fecha, e quem já
é cliente há meses nunca mais o vê. Este aviso alcança justamente quem o produto não alcançaria.

Só com número, e só para quem **não** está enviando. Uma rota própria — `GET /api/conversoes/resumo`
— porque a alternativa era buscar a credencial e as 50 últimas conversões para desenhar uma frase.

### O que os testes provam

Cinco testes de banco do passo (`OnboardingDbTests`), quatro da jornada (`ContatosDbTests`), sete de
tela. Doze sabotagens — **duas não pegaram nada na primeira rodada**, e vale registrar as duas porque
elas são de naturezas diferentes:

| sabotagem | teste que caiu |
|---|---|
| o passo de anúncios passa a existir sempre | 3, inclusive dois que já existiam |
| o passo conta todo rastro, com anúncio ou sem | `COM_ANUNCIO_CHEGANDO_O_PASSO_APARECE_COM_O_NUMERO` |
| o passo ignora a janela de 30 dias | idem |
| o passo olha `Ativo` em vez de `PodeEnviar` | 2 |
| o pulo reescreve a data a cada clique | `QUEM_ANUNCIA_E_NAO_QUER…_O_CARIMBO_E_IDEMPOTENTE` |
| a rota do passo perde a aba | `COM_ANUNCIO_CHEGANDO…` |
| a jornada passa a levar o IP | `A_JORNADA_MOSTRA_A_CAMPANHA_E_NUNCA_O_IP…` |
| a jornada devolve o identificador cru | idem |
| o booleano de anúncio pago fica sempre falso | idem |
| a jornada volta mesmo sem rastro | 9 |
| a lista de eventos não é carregada | `A_JORNADA_DIZ_O_ESTADO_DOS_EVENTOS…` |

⚠️ **A primeira que passou era TESTE fraco:** o semeador só criava rastros **com** `fbclid` e **de
ontem**, então tirar o filtro `identificadores <> '{}'` dava o mesmo número. Um teste que só semeia o
caso que passa não testa o filtro. Agora ele semeia também um rastro sem identificador e um de 40 dias
atrás — e o número 3 passou a significar algo.

⚠️ **A segunda que passou era SABOTAGEM fraca minha:** eu havia neutralizado só a primeira cláusula
do `||`, e o `fbc` do cenário mantinha o booleano verdadeiro. O teste estava certo desde o começo.

E um defeito real, pego pelo teste: `Fonte` saía como `formulariosite` — `ToString().ToLowerInvariant()`
não produz snake_case. Virou o enum com `[JsonConverter(typeof(EnumMinusculo<FonteRastreio>))]`, que é
a mesma política que o Npgsql grava no enum nativo. É o defeito que `EnumMinusculo` foi criado para
resolver, reaparecendo num campo novo.

1219 testes de backend, 443 no painel, 93 no celular. Migração `AnunciosDispensados` aplicada,
revertida e reaplicada no `nexora_dev`.
