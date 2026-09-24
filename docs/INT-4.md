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
