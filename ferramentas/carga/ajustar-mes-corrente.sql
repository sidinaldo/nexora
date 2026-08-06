-- =============================================================================================
-- TRAZER PARTE DOS DESFECHOS PARA O MES CORRENTE.
--
-- ===================== O PROBLEMA QUE ISTO RESOLVE =====================
-- O dashboard mede "vendas do mes" e "taxa de conversao" contra o MES CORRENTE, nao contra a
-- janela toda. Espalhando 100 ganhos e 50 perdas uniformemente em 120 dias, o mes corrente
-- (cinco dias, hoje) recebe ~4 ganhos e ~2 perdas -- e, por sorte do arredondamento, ficou com
-- 4 ganhos e ZERO perdas. Resultado: conversao de 100%.
--
-- Conversao de 100% nao convence ninguem, e de 0% assusta. E nao e um numero que a empresa possa
-- corrigir mexendo na tela: sai da distribuicao dos dados.
-- =======================================================================
--
-- A correcao mexe SO em ganho_em e perdido_em -- nunca em criado_em. O contato continua tendo
-- nascido quando nasceu; o que muda e QUANDO ele fechou ou se perdeu. Negocio criado ha tres
-- meses e fechado esta semana e o caso mais comum de um funil de verdade.
-- =============================================================================================
BEGIN;

-- ---- perdas para o mes corrente ----
-- Sem perdido no mes, a conversao do mes fica em 100%.
WITH alvo AS (
    SELECT id, empresa_id,
           row_number() OVER (PARTITION BY empresa_id ORDER BY id) AS n
      FROM contatos
     WHERE perdido_em IS NOT NULL
       AND perdido_em < date_trunc('month', now())
)
UPDATE contatos c
   SET perdido_em = date_trunc('month', now())
                  + make_interval(hours => (((a.n * 7) % GREATEST(1, (EXTRACT(epoch FROM (now() - date_trunc('month', now())))/3600)::bigint))::int))
  FROM alvo a
 WHERE c.id = a.id
   AND a.n <= 12                                  -- 12 por empresa
   AND c.criado_em < date_trunc('month', now());  -- so quem ja existia: nada de perder antes de nascer

-- ---- alguns ganhos a mais no mes, para o numero do topo nao ficar magro ----
WITH alvo AS (
    SELECT id, empresa_id,
           row_number() OVER (PARTITION BY empresa_id ORDER BY id DESC) AS n
      FROM contatos
     WHERE ganho_em IS NOT NULL
       AND ganho_em < date_trunc('month', now())
)
UPDATE contatos c
   SET ganho_em = date_trunc('month', now())
                + make_interval(hours => (((a.n * 11) % GREATEST(1, (EXTRACT(epoch FROM (now() - date_trunc('month', now())))/3600)::bigint))::int))
  FROM alvo a
 WHERE c.id = a.id
   AND a.n <= 20                                  -- 20 por empresa
   AND c.criado_em < date_trunc('month', now());

COMMIT;

-- ---- conferencia: os dois lados existem no mes, e nenhum desfecho antecede a criacao ----
SELECT empresa_id,
       count(*) FILTER (WHERE ganho_em   >= date_trunc('month', now())) AS ganhos_no_mes,
       count(*) FILTER (WHERE perdido_em >= date_trunc('month', now())) AS perdas_no_mes,
       count(*) FILTER (WHERE ganho_em   < criado_em)                   AS ganho_antes_de_nascer,
       count(*) FILTER (WHERE perdido_em < criado_em)                   AS perda_antes_de_nascer,
       count(*) FILTER (WHERE ganho_em IS NOT NULL AND perdido_em IS NOT NULL) AS ganho_e_perdido
  FROM contatos
 GROUP BY empresa_id ORDER BY 1;
