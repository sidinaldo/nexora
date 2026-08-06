-- =============================================================================================
-- ESPALHAR OS REGISTROS PELOS ULTIMOS 120 DIAS.
--
-- Os REGISTROS foram todos criados pelos controllers, com validacao e regra de negocio. O que
-- a API nao consegue produzir e HISTORICO: o InterceptorAuditoria carimba criado_em em todo
-- INSERT -- de proposito, para nenhum caminho de escrita esquecer a coluna --, entao tudo nasce
-- com a data de hoje e o dashboard fica com um pico unico.
--
-- ===================== POR QUE DESLOCAR A SUBARVORE INTEIRA =====================
-- Sortear uma data por LINHA quebraria a coerencia: mensagem mais antiga que a conversa,
-- ultima_mensagem_em que nao bate com a ultima mensagem, aguardando_desde apontando para um
-- instante que nao existe mais. Cada uma dessas faria a tela mostrar estado que o produto real
-- nunca produziria, e alguem depuraria um bug que nao existe.
--
-- Aqui cada CONTATO ganha um deslocamento, e o mesmo deslocamento e aplicado a ele, as conversas
-- dele e as mensagens dele. A ordem relativa dentro da conversa fica intacta -- a thread inteira
-- so anda para tras no tempo.
-- ===============================================================================
--
-- O deslocamento e DETERMINISTICO (funcao do id), nao random(): rodar duas vezes daria o mesmo
-- resultado, e o segundo espalhamento nao empurraria tudo para fora da janela.
-- =============================================================================================
BEGIN;

CREATE TEMP TABLE deslocamento ON COMMIT DROP AS
SELECT id AS contato_id,
       -- 7919 e 104729 sao primos: o produto com o id nao cria faixas vazias no resto da divisao.
       make_interval(days  => ((id * 7919)   % 120)::int,
                     mins  => ((id * 104729) % 540)::int) AS atraso
  FROM contatos;

CREATE INDEX ON deslocamento (contato_id);

-- ---- contatos: criacao e os dois instantes terminais ----
UPDATE contatos c
   SET criado_em  = c.criado_em  - d.atraso,
       ganho_em   = c.ganho_em   - d.atraso,
       perdido_em = c.perdido_em - d.atraso
  FROM deslocamento d
 WHERE d.contato_id = c.id;

-- ---- conversas: mesmo atraso do contato dono ----
UPDATE conversas v
   SET criado_em         = v.criado_em         - d.atraso,
       ultima_mensagem_em = v.ultima_mensagem_em - d.atraso,
       aguardando_desde   = v.aguardando_desde   - d.atraso,
       atribuido_em       = v.atribuido_em       - d.atraso
  FROM deslocamento d
 WHERE d.contato_id = v.contato_id;

-- ---- mensagens: idem, inclusive data_disparo (ck_msg_data_disparo exige que saida a tenha) ----
UPDATE mensagens m
   SET criado_em    = m.criado_em    - d.atraso,
       recebida_em  = m.recebida_em  - d.atraso,
       enviada_em   = m.enviada_em   - d.atraso,
       reservado_em = m.reservado_em - d.atraso,
       ack_em       = m.ack_em       - d.atraso,
       data_disparo = CASE WHEN m.data_disparo IS NULL THEN NULL
                           ELSE (m.data_disparo - (EXTRACT(day FROM d.atraso))::int) END
  FROM deslocamento d
 WHERE d.contato_id = m.contato_id;

-- ---- lembretes: so a CRIACAO anda. `data_alvo` fica onde esta, de proposito: e ela que
--      coloca a pendencia no Meu Dia de hoje, e empurra-la para tras esvaziaria a tela.
UPDATE lembretes l
   SET criado_em = LEAST(l.criado_em - d.atraso, l.criado_em)
  FROM deslocamento d
 WHERE d.contato_id = l.contato_id;

-- ---- a empresa passou a ter historico de mensagem recebida ----
UPDATE empresas e
   SET primeira_mensagem_em = (SELECT min(m.criado_em) FROM mensagens m WHERE m.empresa_id = e.id)
 WHERE EXISTS (SELECT 1 FROM mensagens m WHERE m.empresa_id = e.id);

COMMIT;
