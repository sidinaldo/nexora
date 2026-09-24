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
