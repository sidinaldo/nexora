\set ON_ERROR_STOP on
SET client_min_messages TO notice;
BEGIN;
\i 'f:/projetos/Nexora/docs/SCHEMA-NEXORA.sql'
\echo ''
\echo '================ TESTES DAS INVARIANTES ================'

-- ---- seed mínimo, dois tenants ----
INSERT INTO empresas (nome) VALUES ('Empresa A'), ('Empresa B');
INSERT INTO etapas_funil (empresa_id, nome, ordem, e_ganho)
  SELECT id, 'Novo Lead', 1, false FROM empresas;
INSERT INTO conexoes (empresa_id, nome, instance_name)
  SELECT id, 'Principal', 'inst-' || id FROM empresas;

INSERT INTO usuarios (empresa_id, nome, email, senha_hash, papel, status)
  VALUES ((SELECT id FROM empresas WHERE nome='Empresa A'),
          'Dono A', 'dono@a.com', 'pbkdf2$100000$s$h', 'dono', 'ativo');

-- =====================================================================
-- [C1] convidado PODE ficar sem senha; ativo NÃO
-- =====================================================================
INSERT INTO usuarios (empresa_id, nome, email, papel, status)
  VALUES ((SELECT id FROM empresas WHERE nome='Empresa A'),
          'Convidado', 'conv@a.com', 'vendedor', 'convidado');
\echo 'OK  [C1] convidado gravado sem senha_hash'

DO $$ BEGIN
  INSERT INTO usuarios (empresa_id, nome, email, papel, status)
    VALUES ((SELECT id FROM empresas WHERE nome='Empresa A'),
            'Ativo sem senha', 'x@a.com', 'vendedor', 'ativo');
  RAISE EXCEPTION 'FALHA [C1]: usuario ativo sem senha foi aceito';
EXCEPTION WHEN check_violation THEN
  RAISE NOTICE 'OK  [C1] usuario ativo sem senha rejeitado';
END $$;

-- =====================================================================
-- [C9] FK composta barra escrita cross-tenant
-- =====================================================================
DO $$ BEGIN
  INSERT INTO contatos (empresa_id, nome, telefone, etapa_id)
    VALUES ((SELECT id FROM empresas WHERE nome='Empresa A'),
            'Intruso', '5584900000000',
            (SELECT e.id FROM etapas_funil e JOIN empresas m ON m.id=e.empresa_id
              WHERE m.nome='Empresa B'));
  RAISE EXCEPTION 'FALHA [C9]: contato do tenant A aceitou etapa do tenant B';
EXCEPTION WHEN foreign_key_violation THEN
  RAISE NOTICE 'OK  [C9] etapa de outro tenant rejeitada';
END $$;

-- =====================================================================
-- [C2] anonimização de MAIS DE UM contato por empresa
-- =====================================================================
INSERT INTO contatos (empresa_id, nome, telefone, etapa_id)
  SELECT m.id, v.nome, v.tel, e.id
    FROM empresas m
    JOIN etapas_funil e ON e.empresa_id = m.id
    CROSS JOIN (VALUES ('Ana','5584988887777'), ('Bia','5584911112222'),
                       ('Caio','5584933334444')) AS v(nome, tel)
   WHERE m.nome = 'Empresa A';

UPDATE contatos SET nome = '(anonimizado)', telefone = '', anonimizado_em = now()
 WHERE nome IN ('Ana', 'Bia');
\echo 'OK  [C2] dois contatos anonimizados com o mesmo telefone vazio'

DO $$ BEGIN
  INSERT INTO contatos (empresa_id, nome, telefone, etapa_id)
    VALUES ((SELECT id FROM empresas WHERE nome='Empresa A'),
            'Caio 2', '5584933334444',
            (SELECT e.id FROM etapas_funil e JOIN empresas m ON m.id=e.empresa_id
              WHERE m.nome='Empresa A'));
  RAISE EXCEPTION 'FALHA [C2]: telefone duplicado entre contatos VIVOS aceito';
EXCEPTION WHEN unique_violation THEN
  RAISE NOTICE 'OK  [C2] telefone duplicado entre contatos vivos barrado';
END $$;

-- =====================================================================
-- [C5] ultima_mensagem_em nunca nula (cursor não quebra)
-- =====================================================================
INSERT INTO conversas (empresa_id, contato_id, conexao_id)
  SELECT c.empresa_id, c.id, x.id
    FROM contatos c JOIN conexoes x ON x.empresa_id = c.empresa_id
   WHERE c.nome = 'Caio';

DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM conversas WHERE ultima_mensagem_em IS NULL;
  IF n > 0 THEN RAISE EXCEPTION 'FALHA [C5]: % conversa(s) com ultima_mensagem_em nula', n; END IF;
  RAISE NOTICE 'OK  [C5] conversa nasce com ultima_mensagem_em preenchida';
END $$;

-- =====================================================================
-- TETO DIÁRIO: 1 lembrete automático com mensagem por contato por dia
-- =====================================================================
INSERT INTO lembretes (empresa_id, contato_id, origem, data_alvo, titulo,
                       envia_mensagem, texto_mensagem)
  SELECT c.empresa_id, c.id, 'automatico', current_date, 'Follow-up D+3',
         true, 'Oi, tudo certo?'
    FROM contatos c WHERE c.nome = 'Caio';

DO $$ BEGIN
  INSERT INTO lembretes (empresa_id, contato_id, origem, data_alvo, titulo,
                         envia_mensagem, texto_mensagem)
    SELECT c.empresa_id, c.id, 'automatico', current_date, 'Follow-up duplicado',
           true, 'Oi de novo'
      FROM contatos c WHERE c.nome = 'Caio';
  RAISE EXCEPTION 'FALHA: segundo lembrete automático do dia foi aceito';
EXCEPTION WHEN unique_violation THEN
  RAISE NOTICE 'OK  [teto] segundo lembrete automático no mesmo dia barrado';
END $$;

-- manual no mesmo dia DEVE passar (não entra no teto)
INSERT INTO lembretes (empresa_id, contato_id, origem, data_alvo, titulo)
  SELECT c.empresa_id, c.id, 'manual', current_date, 'Ligar para o Caio'
    FROM contatos c WHERE c.nome = 'Caio';
\echo 'OK  [teto] lembrete manual no mesmo dia passa (correto)'

-- =====================================================================
-- [C3] mesmo lembrete não pode gerar duas mensagens
-- =====================================================================
INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id,
                       instance_name, direcao, lembrete_id, data_disparo, texto)
  SELECT l.empresa_id, cv.id, l.contato_id, cv.conexao_id,
         'inst-1', 'saida', l.id, current_date, l.texto_mensagem
    FROM lembretes l JOIN conversas cv ON cv.contato_id = l.contato_id
   WHERE l.origem = 'automatico';

DO $$ BEGIN
  INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id,
                         instance_name, direcao, lembrete_id, data_disparo, texto)
    SELECT l.empresa_id, cv.id, l.contato_id, cv.conexao_id,
           'inst-1', 'saida', l.id, current_date, 'reenvio indevido'
      FROM lembretes l JOIN conversas cv ON cv.contato_id = l.contato_id
     WHERE l.origem = 'automatico';
  RAISE EXCEPTION 'FALHA [C3]: mesmo lembrete disparou duas mensagens';
EXCEPTION WHEN unique_violation THEN
  RAISE NOTICE 'OK  [C3] segundo envio do mesmo lembrete barrado';
END $$;

-- =====================================================================
-- uq_msg_wa_id: webhook reentregue / eco do próprio envio
-- =====================================================================
INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id,
                       instance_name, direcao, wa_message_id, recebida_em, texto)
  SELECT cv.empresa_id, cv.id, cv.contato_id, cv.conexao_id,
         'inst-1', 'entrada', 'WA-ABC-123', now(), 'oi'
    FROM conversas cv LIMIT 1;

DO $$ BEGIN
  INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id,
                         instance_name, direcao, wa_message_id, recebida_em, texto)
    SELECT cv.empresa_id, cv.id, cv.contato_id, cv.conexao_id,
           'inst-1', 'entrada', 'WA-ABC-123', now(), 'oi'
      FROM conversas cv LIMIT 1;
  RAISE EXCEPTION 'FALHA: webhook reentregue duplicou a mensagem';
EXCEPTION WHEN unique_violation THEN
  RAISE NOTICE 'OK  [dedupe] reentrega do mesmo wa_message_id barrada';
END $$;

-- ON CONFLICT DO NOTHING devolve zero linhas (é assim que o handler detecta)
DO $$
DECLARE devolvido bigint;
BEGIN
  INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id,
                         instance_name, direcao, wa_message_id, recebida_em, texto)
    SELECT cv.empresa_id, cv.id, cv.contato_id, cv.conexao_id,
           'inst-1', 'entrada', 'WA-ABC-123', now(), 'oi'
      FROM conversas cv LIMIT 1
  ON CONFLICT DO NOTHING
  RETURNING id INTO devolvido;

  IF devolvido IS NOT NULL THEN RAISE EXCEPTION 'FALHA: ON CONFLICT devolveu id'; END IF;
  RAISE NOTICE 'OK  [dedupe] ON CONFLICT DO NOTHING devolve NULL (handler sabe pular)';
END $$;

-- =====================================================================
-- [C20] saída exige data_disparo; entrada não
-- =====================================================================
DO $$ BEGIN
  INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id,
                         instance_name, direcao, texto)
    SELECT cv.empresa_id, cv.id, cv.contato_id, cv.conexao_id,
           'inst-1', 'saida', 'sem data'
      FROM conversas cv LIMIT 1;
  RAISE EXCEPTION 'FALHA [C20]: saida sem data_disparo aceita';
EXCEPTION WHEN check_violation THEN
  RAISE NOTICE 'OK  [C20] saida sem data_disparo rejeitada';
END $$;

-- =====================================================================
-- [C12] contador de não lidas não fica negativo
-- =====================================================================
DO $$ BEGIN
  UPDATE conversas SET nao_lidas = nao_lidas - 1;
  RAISE EXCEPTION 'FALHA [C12]: nao_lidas ficou negativo';
EXCEPTION WHEN check_violation THEN
  RAISE NOTICE 'OK  [C12] nao_lidas negativo rejeitado';
END $$;

-- =====================================================================
-- [C15] trigger de atualizado_em
-- =====================================================================
-- now() é o horário de INÍCIO DA TRANSAÇÃO: dentro de uma transação só ele não
-- avança. Então o teste não é "o valor subiu", e sim "o trigger ignora o que a
-- aplicação mandou" — que é exatamente o bug do Recupera que ele previne.
DO $$
DECLARE depois timestamptz;
BEGIN
  UPDATE contatos SET observacoes = 'toque', atualizado_em = timestamptz '2020-01-01'
   WHERE nome = 'Caio';
  SELECT atualizado_em INTO depois FROM contatos WHERE nome = 'Caio';
  IF depois = timestamptz '2020-01-01' THEN
    RAISE EXCEPTION 'FALHA [C15]: trigger nao sobrescreveu o valor da aplicacao';
  END IF;
  RAISE NOTICE 'OK  [C15] trigger sobrescreveu atualizado_em errado vindo da aplicacao';
END $$;

-- =====================================================================
-- [C13] ponto médio do kanban não esgota
-- =====================================================================
DO $$
DECLARE a numeric := 1; b numeric := 2; meio numeric; i int;
BEGIN
  FOR i IN 1..60 LOOP
    meio := (a + b) / 2;
    IF meio = a OR meio = b THEN
      RAISE EXCEPTION 'FALHA [C13]: escala esgotou na insercao %', i;
    END IF;
    b := meio;
  END LOOP;
  RAISE NOTICE 'OK  [C13] 60 insercoes no mesmo ponto sem colisao (escala livre)';
END $$;

-- =====================================================================
-- [C18] índice parcial do kanban é de fato usado
-- =====================================================================
\echo ''
\echo '--- plano da consulta do kanban ---'
EXPLAIN (COSTS OFF)
SELECT id, nome, ordem_kanban FROM contatos
 WHERE empresa_id = 1 AND etapa_id = 1 AND perdido_em IS NULL
 ORDER BY ordem_kanban;

\echo ''
\echo '================ FIM ================'
ROLLBACK;
\echo '>>> rollback feito, banco intacto'
