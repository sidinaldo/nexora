-- =====================================================================
-- NEXORA — schema da fase 1  (versão revisada)
-- PostgreSQL 15+
--
-- Escopo: conexão WhatsApp (1 número por empresa), caixa de entrada
-- multi-atendente, contato com origem e etapa, funil kanban de 5 etapas,
-- lembrete de follow-up, semáforo de urgência, Meu Dia, dashboard.
--
-- Fora da fase 1: IA, custo por lead, índice de saúde comercial,
-- pós-venda automático, multicanal, campanhas em massa, funil configurável.
--
-- Convenções herdadas do Recupera:
--   - empresa_id denormalizado em toda tabela consultada pela aplicação,
--     e sempre a PRIMEIRA coluna de índice composto
--   - snake_case, mapeado explicitamente no DbContext (sem convention pack)
--   - enums nativos do Postgres, sufixo _enum (registrar em HasPostgresEnum
--     E em MapEnum do NpgsqlDataSourceBuilder — esquecer o segundo quebra
--     em runtime, não no boot)
--   - bigint GENERATED ALWAYS AS IDENTITY (não bigserial): forma padrão e
--     bloqueia insert explícito de id
--   - timestamptz em tudo; a aplicação decide o fuso de exibição
-- =====================================================================
--
-- ---------------------------------------------------------------------
-- O QUE MUDOU EM RELAÇÃO AO RASCUNHO
-- ---------------------------------------------------------------------
-- Cada ponto está marcado inline com [C1]…[C20].
--
-- BLOQUEADORES
--   [C1]  usuarios.senha_hash agora NULL — convidado não tem senha até aceitar
--   [C2]  uq_contatos_telefone virou parcial (WHERE anonimizado_em IS NULL) —
--         senão só dava para anonimizar um contato por empresa
--   [C3]  uq_msg_lembrete: impede disparar o mesmo lembrete duas vezes
--   [C4]  inbound de número desconhecido: decisão explicitada (cria contato)
--
-- RISCOS
--   [C5]  conversas.ultima_mensagem_em NOT NULL DEFAULT now() — DESC é
--         NULLS FIRST no Postgres e quebrava o cursor
--   [C6]  usuarios.ativo (boolean) → status_usuario_enum de 3 estados
--   [C7]  status_conexao_enum ganhou 'offline' (Evolution fora do ar ≠
--         número desconectado)
--   [C8]  aguardando_desde: requisito de transação documentado + consulta
--         de reconciliação no rodapé
--   [C9]  FKs compostas com empresa_id — o query filter protege leitura,
--         não escrita
--
-- MENORES
--   [C10] ix_msg_ack removido (redundante com uq_msg_wa_id)
--   [C11] comentário do NULLIF corrigido
--   [C12] CHECK (nao_lidas >= 0)
--   [C13] ordem_kanban virou numeric sem escala fixa
--   [C14] bigserial → GENERATED ALWAYS AS IDENTITY; enums com sufixo _enum
--   [C15] trigger de atualizado_em
--   [C16] CHECK de faixa nas horas da janela
--   [C17] motor usa data_alvo <= current_date (documentado)
--   [C18] ix_contatos_kanban parcial (esconde perdidos)
--   [C19] empresas.fuso_horario
--   [C20] mensagens.data_disparo nullable + CHECK (só saída exige)
-- =====================================================================


-- ---------------------------------------------------------------------
-- ENUMS                                                          -- [C14]
-- ---------------------------------------------------------------------

CREATE TYPE papel_usuario_enum AS ENUM ('dono', 'gestor', 'vendedor');

-- [C6] Três estados, não um booleano. 'convidado' é vaga ocupada mas sem
-- senha definida; 'inativo' é desligado e NÃO ocupa vaga. Com booleano,
-- convidado-pendente e desativado ficam indistinguíveis, e a regra da tela
-- de Equipe ("ocupadas = ativos + convidados") não se escreve.
CREATE TYPE status_usuario_enum AS ENUM ('ativo', 'convidado', 'inativo');

-- [C7] 'offline' = a Evolution API não respondeu (problema NOSSO).
-- 'desconectado' = a instância existe mas o número caiu (o cliente precisa
-- reparear). Colapsar os dois faz o banner mandar escanear QR quando não há
-- nada que o cliente possa fazer.
CREATE TYPE status_conexao_enum AS ENUM (
    'nao_criada', 'conectando', 'conectado', 'desconectado', 'offline'
);

CREATE TYPE origem_lead_enum AS ENUM (
    'instagram', 'facebook', 'whatsapp', 'google', 'site',
    'qrcode', 'indicacao', 'manual', 'outro'
);

CREATE TYPE direcao_mensagem_enum AS ENUM ('entrada', 'saida');

CREATE TYPE tipo_midia_enum AS ENUM (
    'nenhum', 'imagem', 'documento', 'audio', 'video'
);

CREATE TYPE status_conversa_enum AS ENUM ('aberta', 'resolvida');

CREATE TYPE status_lembrete_enum AS ENUM ('pendente', 'concluido', 'cancelado');

-- Origem do lembrete. 'automatico' é o gerado pela regra de follow-up;
-- 'manual' é o que o vendedor cria na mão. A distinção importa porque só
-- o automático entra no teto diário anti-spam.
CREATE TYPE origem_lembrete_enum AS ENUM ('automatico', 'manual');


-- ---------------------------------------------------------------------
-- TRIGGER DE atualizado_em                                       -- [C15]
-- ---------------------------------------------------------------------
-- No Recupera esta coluna é atribuída à mão em dezenas de serviços; basta
-- um SaveChanges esquecer para ela mentir. Aqui o banco garante — e sobrescreve
-- o valor que a aplicação mandar, certo ou errado. Aplicada ao fim de cada
-- tabela que tem a coluna.
--
-- now() é o horário de INÍCIO DA TRANSAÇÃO, não do statement: várias linhas
-- alteradas na mesma transação recebem o mesmo carimbo. É o comportamento
-- desejado (a unidade de mudança é a transação). Se algum dia for preciso
-- distinguir statements dentro de uma transação, trocar por clock_timestamp().

CREATE OR REPLACE FUNCTION fn_atualizado_em() RETURNS trigger AS $$
BEGIN
    NEW.atualizado_em := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;


-- ---------------------------------------------------------------------
-- EMPRESAS (tenant)
-- ---------------------------------------------------------------------

CREATE TABLE empresas (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nome                text        NOT NULL,
    documento           text        NULL,          -- CNPJ/CPF, sem máscara
    ativo               boolean     NOT NULL DEFAULT true,

    -- Janela de atendimento. No Recupera isso era conformidade CDC; aqui é
    -- simplesmente horário comercial. Governa três coisas: quando o lembrete
    -- automático pode disparar, quando o semáforo acende (para não piscar de
    -- madrugada) e o que o Meu Dia mostra.
    janela_hora_inicio  smallint    NOT NULL DEFAULT 8,
    janela_hora_fim     smallint    NOT NULL DEFAULT 20,

    -- Bitmask de dia da semana: bit 0 = domingo … bit 6 = sábado.
    -- 126 = seg a sáb. Bitmask em vez de tabela porque é lido em todo
    -- cálculo de janela e uma coluna evita join no caminho quente.
    janela_dias_semana  smallint    NOT NULL DEFAULT 126,

    -- [C19] A janela é comparada contra "agora no fuso de negócio". Deixar o
    -- fuso numa constante da aplicação (como o Recupera faz) erra 1–2h para
    -- cliente em Manaus ou Rio Branco. Coluna por tenant custa nada agora e
    -- evita migração depois.
    fuso_horario        text        NOT NULL DEFAULT 'America/Sao_Paulo',

    criado_em           timestamptz NOT NULL DEFAULT now(),
    atualizado_em       timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_empresas_janela CHECK (janela_hora_inicio < janela_hora_fim),
    -- [C16] smallint aceita 25; a aplicação compara com EXTRACT(hour) (0-23).
    CONSTRAINT ck_empresas_hora_faixa CHECK (
        janela_hora_inicio BETWEEN 0 AND 23 AND janela_hora_fim BETWEEN 1 AND 24),
    CONSTRAINT ck_empresas_dias CHECK (janela_dias_semana BETWEEN 1 AND 127)
);

CREATE TRIGGER tg_empresas_atualizado BEFORE UPDATE ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();


-- ---------------------------------------------------------------------
-- USUARIOS
-- ---------------------------------------------------------------------

CREATE TABLE usuarios (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id        bigint              NOT NULL REFERENCES empresas(id),
    nome              text                NOT NULL,
    email             text                NOT NULL,

    -- [C1] NULL enquanto o convite não foi aceito. Era NOT NULL no rascunho,
    -- o que obrigaria a inserir um hash falso no convite e destruiria a
    -- checagem que distingue "convidado sem senha" de "senha errada".
    senha_hash        text                NULL,      -- pbkdf2$iter$salt$hash

    papel             papel_usuario_enum  NOT NULL,
    status            status_usuario_enum NOT NULL DEFAULT 'ativo',   -- [C6]

    -- Bloqueio de conta por tentativas. Persistente e cross-IP — pega o que
    -- o rate limit por IP não pega.
    falhas_login      smallint            NOT NULL DEFAULT 0,
    bloqueado_ate     timestamptz         NULL,

    -- Convite e redefinição de senha. O Recupera reusa as mesmas colunas para
    -- os dois fluxos, o que confunde quando um usuário convidado pede reset
    -- antes de aceitar. Aqui estão separadas de propósito.
    token_convite     text                NULL,
    convite_expira    timestamptz         NULL,
    token_reset       text                NULL,
    reset_expira      timestamptz         NULL,

    ultimo_acesso_em  timestamptz         NULL,
    criado_em         timestamptz         NOT NULL DEFAULT now(),
    atualizado_em     timestamptz         NOT NULL DEFAULT now(),

    -- [C1] Coerência entre estado e senha: só 'convidado' pode estar sem
    -- hash. Impede que um usuário ativo fique sem senha por bug de fluxo.
    CONSTRAINT ck_usuarios_senha CHECK (
        status = 'convidado' OR senha_hash IS NOT NULL),

    -- [C9] Alvo das FKs compostas dos filhos (responsavel_id, criado_por…).
    CONSTRAINT uq_usuarios_id_empresa UNIQUE (id, empresa_id)
);

-- E-mail único global, não por empresa: o login não sabe o tenant antes de
-- resolver o usuário.
CREATE UNIQUE INDEX uq_usuarios_email ON usuarios (lower(email));
CREATE INDEX ix_usuarios_empresa ON usuarios (empresa_id, status);
CREATE UNIQUE INDEX uq_usuarios_token_convite ON usuarios (token_convite)
    WHERE token_convite IS NOT NULL;
CREATE UNIQUE INDEX uq_usuarios_token_reset ON usuarios (token_reset)
    WHERE token_reset IS NOT NULL;

CREATE TRIGGER tg_usuarios_atualizado BEFORE UPDATE ON usuarios
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();


-- ---------------------------------------------------------------------
-- CONEXOES (WhatsApp via Evolution API)
-- ---------------------------------------------------------------------
-- Fase 1: uma conexão por empresa. A tabela já é 1:N para não precisar de
-- migração quando isso mudar — o que trava em 1 é o índice único abaixo,
-- que se remove numa linha.

CREATE TABLE conexoes (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id        bigint              NOT NULL REFERENCES empresas(id),
    nome              text                NOT NULL,

    -- Chave de correlação com a Evolution. O webhook chega com o nome da
    -- instância e é por aqui que se descobre o tenant — antes de qualquer
    -- consulta com query filter, que fora de requisição autenticada
    -- retornaria vazio em silêncio.
    instance_name     text                NOT NULL,

    -- Número real do WhatsApp, canonicalizado com DDI (5584988887777).
    -- Preenchido pelo webhook connection.update a partir do ownerJid.
    numero            text                NULL,
    numero_anterior   text                NULL,     -- detecta troca de chip
    perfil_nome       text                NULL,
    perfil_foto_url   text                NULL,

    status            status_conexao_enum NOT NULL DEFAULT 'nao_criada',
    status_em         timestamptz         NULL,     -- última mudança de status
    conectado_em      timestamptz         NULL,
    desconectado_em   timestamptz         NULL,

    criado_em         timestamptz         NOT NULL DEFAULT now(),
    atualizado_em     timestamptz         NOT NULL DEFAULT now(),

    CONSTRAINT uq_conexoes_id_empresa UNIQUE (id, empresa_id)   -- [C9]
);

CREATE UNIQUE INDEX uq_conexoes_instance ON conexoes (instance_name);

-- Trava de 1 conexão por empresa na fase 1. Remover esta linha quando
-- multi-número entrar.
CREATE UNIQUE INDEX uq_conexoes_empresa ON conexoes (empresa_id);

CREATE TRIGGER tg_conexoes_atualizado BEFORE UPDATE ON conexoes
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();


-- ---------------------------------------------------------------------
-- ETAPAS DO FUNIL
-- ---------------------------------------------------------------------
-- Semeadas no cadastro da empresa (5 linhas fixas). É tabela em vez de enum
-- porque o kanban precisa de ordem, rótulo e cor — e porque funil
-- configurável na fase 2 não vai exigir migração de dados.

CREATE TABLE etapas_funil (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id    bigint      NOT NULL REFERENCES empresas(id),
    nome          text        NOT NULL,
    ordem         smallint    NOT NULL,
    cor           text        NOT NULL DEFAULT '#2F5D3A',

    -- Marca a etapa terminal de ganho. O dashboard e o cálculo de conversão
    -- dependem disso, e é o que permite trocar o nome "Venda" sem quebrar a
    -- lógica.
    e_ganho       boolean     NOT NULL DEFAULT false,

    criado_em     timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_etapas_id_empresa UNIQUE (id, empresa_id)     -- [C9]
);

CREATE UNIQUE INDEX uq_etapas_ordem ON etapas_funil (empresa_id, ordem);
CREATE UNIQUE INDEX uq_etapas_ganho ON etapas_funil (empresa_id)
    WHERE e_ganho;

CREATE TRIGGER tg_etapas_atualizado BEFORE UPDATE ON etapas_funil
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();


-- ---------------------------------------------------------------------
-- CONTATOS
-- ---------------------------------------------------------------------
-- Dado frio: cadastro, origem, posição no funil. Separado de `conversas`
-- (dado quente) para que a escrita a cada mensagem não toque a tabela que
-- o kanban lê o tempo todo.

CREATE TABLE contatos (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id    bigint      NOT NULL REFERENCES empresas(id),

    -- [C4] Mensagem de número desconhecido CRIA contato (ver nota em
    -- `mensagens`). Quando o pushName do webhook não vier, a aplicação
    -- preenche com o próprio telefone formatado — nunca deixa vazio.
    nome          text        NOT NULL,

    -- Telefone canonicalizado COM DDI, só dígitos: 5584988887777.
    -- Esta é a coluna mais crítica do schema. O cadastro digita
    -- "(84) 98888-7777" e o WhatsApp entrega "5584988887777@s.whatsapp.net".
    -- Se os dois lados não canonicalizarem igual, a mensagem recebida não
    -- casa com ninguém e some sem erro no log.
    --
    -- LIMITE CONHECIDO DA FASE 1: um telefone por contato. Se um cliente
    -- tiver dois números (comum em PJ), o segundo vira contato duplicado.
    -- Quando isso doer, extrair para `telefones_contato` (empresa_id,
    -- contato_id, telefone, principal) e mover o índice único para lá.
    telefone      text        NOT NULL,

    email         text        NULL,
    origem        origem_lead_enum NOT NULL DEFAULT 'whatsapp',

    -- Texto livre da campanha ou anúncio de onde veio. Na fase 1 é
    -- preenchido à mão ou pelo externalAdReply quando ele vier.
    -- NÃO É atribuição de custo — isso é fase 2 e depende da Cloud API.
    origem_detalhe text       NULL,

    etapa_id      bigint      NOT NULL,

    -- [C13] numeric SEM escala fixa. Com numeric(18,6), inserir sempre no
    -- meio do mesmo par de cards esgota a escala em ~19 movimentos
    -- (2^-19 ≈ 1e-6) e dois cards colidem na mesma posição. numeric puro vai
    -- até 16383 casas: o ponto médio nunca falta. Há uma consulta de
    -- renumeração no rodapé, para higiene, não para correção.
    ordem_kanban  numeric     NOT NULL DEFAULT 0,

    responsavel_id bigint     NULL,
    valor         numeric(14,2) NULL,              -- valor estimado do negócio

    -- Marcos terminais. Coluna em vez de tabela de histórico porque na fase 1
    -- o dashboard só precisa de "quando ganhou" e "quando perdeu".
    -- Histórico completo de movimentação de etapa é fase 2.
    ganho_em      timestamptz NULL,
    perdido_em    timestamptz NULL,
    motivo_perda  text        NULL,

    observacoes   text        NULL,

    -- Anonimização LGPD: zera a PII e preserva o histórico. Não há delete
    -- físico nem soft delete — o padrão vem do Recupera e está certo.
    anonimizado_em timestamptz NULL,

    criado_em     timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now(),

    -- Ganho e perda são mutuamente exclusivos.
    CONSTRAINT ck_contatos_terminal CHECK (
        ganho_em IS NULL OR perdido_em IS NULL),

    -- [C9] FKs COMPOSTAS. O HasQueryFilter do EF protege LEITURA; nada
    -- impede um bug de aplicação de gravar etapa_id ou responsavel_id de
    -- outro tenant. Aqui o banco fecha. Custo: um índice único extra por
    -- tabela-pai (as constraints uq_*_id_empresa).
    CONSTRAINT fk_contatos_etapa FOREIGN KEY (etapa_id, empresa_id)
        REFERENCES etapas_funil (id, empresa_id),
    -- responsavel_id nulo não é verificado (MATCH SIMPLE) — é o que se quer.
    CONSTRAINT fk_contatos_responsavel FOREIGN KEY (responsavel_id, empresa_id)
        REFERENCES usuarios (id, empresa_id),

    CONSTRAINT uq_contatos_id_empresa UNIQUE (id, empresa_id)
);

-- [C2] Um telefone por empresa — mas só entre contatos VIVOS. Sem o
-- predicado parcial, anonimizar o segundo contato de uma empresa viola o
-- índice (dois telefones zerados colidem) e a LGPD só funciona uma vez.
CREATE UNIQUE INDEX uq_contatos_telefone ON contatos (empresa_id, telefone)
    WHERE anonimizado_em IS NULL;

-- [C18] Kanban: carrega uma coluna do funil. Parcial porque lead perdido
-- não aparece no quadro — sem isso o índice carrega linhas que a consulta
-- sempre descarta.
CREATE INDEX ix_contatos_kanban ON contatos (empresa_id, etapa_id, ordem_kanban)
    WHERE perdido_em IS NULL;

-- "Leads hoje" do dashboard.
CREATE INDEX ix_contatos_criado ON contatos (empresa_id, criado_em DESC);

-- "Vendas hoje / no mês" do dashboard.
CREATE INDEX ix_contatos_ganho ON contatos (empresa_id, ganho_em DESC)
    WHERE ganho_em IS NOT NULL;

CREATE INDEX ix_contatos_responsavel ON contatos (empresa_id, responsavel_id)
    WHERE responsavel_id IS NOT NULL;

CREATE TRIGGER tg_contatos_atualizado BEFORE UPDATE ON contatos
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();


-- ---------------------------------------------------------------------
-- CONVERSAS
-- ---------------------------------------------------------------------
-- 1:1 com contato na fase 1. Existe como tabela própria porque é dado
-- quente: cada mensagem que entra ou sai escreve aqui.

CREATE TABLE conversas (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id        bigint               NOT NULL REFERENCES empresas(id),
    contato_id        bigint               NOT NULL,
    conexao_id        bigint               NOT NULL,

    status            status_conversa_enum NOT NULL DEFAULT 'aberta',

    -- Atribuição, não fila. Dono opcional: sem dono = "Aguardando",
    -- com dono = "Atendendo". Responder sem dono atribui automaticamente.
    -- Assumir conversa de outro devolve 409.
    responsavel_id    bigint               NULL,
    atribuido_em      timestamptz          NULL,

    -- ===== O CORAÇÃO DO SEMÁFORO =====
    -- Timestamp da primeira mensagem de entrada ainda não respondida.
    --   entrada chega  → se NULL, grava now()
    --   saída sai      → volta para NULL
    --
    -- Materializar em vez de calcular com max(id) por conversa (como o
    -- Recupera faz) porque três features leem isso o tempo todo: o semáforo,
    -- o Meu Dia e um dos quatro números do dashboard. Uma coluna indexada
    -- resolve as três; o cálculo dinâmico não indexa.
    --
    -- [C8] O PREÇO DA ESCOLHA: valor calculado não desincroniza; valor
    -- materializado, sim. O INSERT da mensagem e o UPDATE desta coluna
    -- PRECISAM estar na MESMA transação. O webhook do Recupera faz
    -- exatamente o contrário (SQL cru + SaveChanges separado, sem transação)
    -- — é o padrão a não copiar. Há uma consulta de reconciliação no rodapé
    -- para rodar como sanity check.
    aguardando_desde  timestamptz          NULL,

    -- [C5] NOT NULL. No Postgres, ORDER BY … DESC é NULLS FIRST: com a
    -- coluna nula, conversa recém-criada ia para o TOPO da caixa, e o
    -- predicado de cursor (col, id) < (:em, :id) devolvia NULL para ela,
    -- sumindo da paginação. A conversa sempre nasce colada a uma mensagem,
    -- então now() é honesto.
    ultima_mensagem_em      timestamptz      NOT NULL DEFAULT now(),
    ultima_mensagem_direcao direcao_mensagem_enum NULL,
    ultima_mensagem_previa  text             NULL,   -- primeiros ~120 chars

    nao_lidas         integer              NOT NULL DEFAULT 0,

    resolvido_em      timestamptz          NULL,
    resolvido_por     bigint               NULL,

    criado_em         timestamptz          NOT NULL DEFAULT now(),
    atualizado_em     timestamptz          NOT NULL DEFAULT now(),

    -- [C12] Contador incrementado e zerado pela aplicação sempre fica
    -- negativo uma vez. Melhor estourar no INSERT que exibir "-3 não lidas".
    CONSTRAINT ck_conversas_nao_lidas CHECK (nao_lidas >= 0),

    CONSTRAINT fk_conversas_contato FOREIGN KEY (contato_id, empresa_id)
        REFERENCES contatos (id, empresa_id),
    CONSTRAINT fk_conversas_conexao FOREIGN KEY (conexao_id, empresa_id)
        REFERENCES conexoes (id, empresa_id),
    CONSTRAINT fk_conversas_responsavel FOREIGN KEY (responsavel_id, empresa_id)
        REFERENCES usuarios (id, empresa_id),
    CONSTRAINT fk_conversas_resolvido_por FOREIGN KEY (resolvido_por, empresa_id)
        REFERENCES usuarios (id, empresa_id),

    CONSTRAINT uq_conversas_id_empresa UNIQUE (id, empresa_id)
);

CREATE UNIQUE INDEX uq_conversas_contato ON conversas (contato_id);

-- Lista da caixa de entrada, ordenada por atividade. O par de colunas é o
-- mesmo do cursor de paginação — ordenar por (ultima_mensagem_em DESC,
-- id DESC) e paginar por valor, nunca por offset: a lista se reordena em
-- tempo real e offset pula ou repete linha.
CREATE INDEX ix_conversas_lista
    ON conversas (empresa_id, status, ultima_mensagem_em DESC, id DESC);

-- Semáforo, Meu Dia e o contador "aguardando resposta" do dashboard.
-- Índice parcial: só as conversas que estão de fato esperando.
CREATE INDEX ix_conversas_aguardando
    ON conversas (empresa_id, aguardando_desde)
    WHERE aguardando_desde IS NOT NULL;

CREATE INDEX ix_conversas_responsavel
    ON conversas (empresa_id, responsavel_id, ultima_mensagem_em DESC)
    WHERE responsavel_id IS NOT NULL;

CREATE TRIGGER tg_conversas_atualizado BEFORE UPDATE ON conversas
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();


-- ---------------------------------------------------------------------
-- MENSAGENS
-- ---------------------------------------------------------------------
-- Também é a outbox. O protocolo é: grava a linha → só então chama o
-- WhatsApp → confirma (ou registra a falha). Nunca o contrário: um crash
-- entre disparar e gravar reenvia a mensagem na próxima rodada.
--
-- Na falha a linha FICA, com o erro gravado. Apagar transformaria um POST
-- que chegou mas deu timeout na resposta em mensagem duplicada.
--
-- [C4] DECISÃO EXPLÍCITA — INBOUND DE NÚMERO DESCONHECIDO.
-- conversa_id e contato_id são NOT NULL, então não existe mensagem órfã.
-- Consequência: quando chega mensagem de um número fora da base, a
-- aplicação CRIA o contato (origem='whatsapp', etapa = primeira do funil,
-- nome = pushName ou o próprio número) e a conversa, na mesma transação,
-- antes de inserir a mensagem. Num CRM de vendas isso é o certo — inbound
-- de desconhecido É um lead novo. Some, junto, a aba "Sem cadastro" que o
-- Recupera precisava ter.
-- A alternativa (deixar as duas colunas nulas e ter uma caixa de não
-- identificados) foi descartada: gera trabalho manual sem ganho.

CREATE TABLE mensagens (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id      bigint                NOT NULL REFERENCES empresas(id),
    conversa_id     bigint                NOT NULL,
    contato_id      bigint                NOT NULL,
    conexao_id      bigint                NOT NULL,

    -- Redundante com conexoes.instance_name, de propósito: é a chave do
    -- índice de dedupe, que precisa funcionar sem join no caminho do webhook.
    instance_name   text                  NOT NULL,

    direcao         direcao_mensagem_enum NOT NULL,

    -- Id da mensagem no WhatsApp (key.id). Correlaciona o ACK e é a metade
    -- do índice de dedupe. NULL enquanto a linha está reservada e ainda não
    -- foi postada.
    wa_message_id   text                  NULL,

    texto           text                  NULL,

    tipo_midia      tipo_midia_enum       NOT NULL DEFAULT 'nenhum',
    midia_chave     text                  NULL,      -- chave no storage
    midia_mime      text                  NULL,
    midia_nome      text                  NULL,
    midia_bytes     integer               NULL,

    -- ACK numérico do WhatsApp: 0=erro, 1=enviado, 2=servidor,
    -- 3=entregue, 4=lido. Só avança (WHERE ack IS NULL OR ack < novo),
    -- porque os webhooks chegam fora de ordem e um DELIVERY_ACK atrasado
    -- não pode sobrescrever um READ já recebido.
    ack             smallint              NULL,
    ack_em          timestamptz           NULL,

    -- Quem enviou. NULL quando é entrada ou quando é lembrete automático.
    enviado_por     bigint                NULL,
    lembrete_id     bigint                NULL,      -- FK adicionada no fim

    -- [C20] Data-alvo do envio, só para SAÍDA (em entrada não significa
    -- nada e o NOT NULL do rascunho obrigava a inventar um valor). Para
    -- lembrete automático é a data para a qual foi reservada — o
    -- reserve-defer carimba o próximo dia permitido quando a janela está
    -- fechada ou a conexão caiu, preservando a data-alvo sem duplicar a
    -- linha.
    data_disparo    date                  NULL,

    reservado_em    timestamptz           NOT NULL DEFAULT now(),
    enviada_em      timestamptz           NULL,      -- NULL = reservada
    recebida_em     timestamptz           NULL,

    erro            text                  NULL,

    -- Payload completo do webhook, para auditoria e replay. Só os campos
    -- usados viram coluna; o resto fica aqui.
    payload_raw     jsonb                 NULL,

    criado_em       timestamptz           NOT NULL DEFAULT now(),

    CONSTRAINT ck_msg_ack CHECK (ack IS NULL OR ack BETWEEN 0 AND 4),
    CONSTRAINT ck_msg_data_disparo CHECK (              -- [C20]
        direcao = 'entrada' OR data_disparo IS NOT NULL),

    CONSTRAINT fk_msg_conversa FOREIGN KEY (conversa_id, empresa_id)
        REFERENCES conversas (id, empresa_id),
    CONSTRAINT fk_msg_contato FOREIGN KEY (contato_id, empresa_id)
        REFERENCES contatos (id, empresa_id),
    CONSTRAINT fk_msg_conexao FOREIGN KEY (conexao_id, empresa_id)
        REFERENCES conexoes (id, empresa_id),
    CONSTRAINT fk_msg_enviado_por FOREIGN KEY (enviado_por, empresa_id)
        REFERENCES usuarios (id, empresa_id)
);

-- ===== INVARIANTE 1 — DEDUPE DE RECEBIMENTO =====
-- Copiada literal do Recupera. Cobre de uma vez dois casos distintos:
-- o webhook reentregue pela Evolution e o eco do próprio envio (a Evolution
-- devolve por webhook a mensagem que a gente acabou de mandar).
-- O INSERT usa ON CONFLICT DO NOTHING RETURNING id: quando volta NULL, a
-- mensagem já existia e não há nada a fazer.
--
-- [C11] O predicado já exclui string vazia, então duas linhas com '' NÃO
-- colidem — o NULLIF que o Recupera aplica no UPDATE de confirmação vira
-- redundante aqui (inofensivo, mas não é ele que sustenta a garantia).
CREATE UNIQUE INDEX uq_msg_wa_id
    ON mensagens (instance_name, wa_message_id)
    WHERE wa_message_id IS NOT NULL AND wa_message_id <> '';

-- ===== INVARIANTE 2 — DEDUPE DE ENVIO =====                      [C3]
-- Faltava no rascunho. O teto diário (em `lembretes`) garante que não
-- existam DOIS lembretes para o mesmo contato no mesmo dia; não garante que
-- UM lembrete não seja enviado duas vezes. O motor faz "insere mensagem →
-- marca lembrete concluído": um crash entre os dois passos, ou duas
-- instâncias rodando em paralelo, reenvia.
--
-- No Recupera a proteção nunca esteve no status — estava no INSERT da
-- própria mensagem, contra índice único. Este índice restaura isso: o motor
-- volta a ser INSERT … ON CONFLICT DO NOTHING RETURNING id, e NULL de volta
-- significa "já enviado, pula". O banco é o árbitro, não a aplicação.
CREATE UNIQUE INDEX uq_msg_lembrete ON mensagens (lembrete_id)
    WHERE lembrete_id IS NOT NULL;

-- Timeline da conversa. Também é o índice do cursor de paginação da thread.
CREATE INDEX ix_msg_timeline
    ON mensagens (empresa_id, conversa_id, id DESC);

-- Drenagem das reservas não despachadas.
CREATE INDEX ix_msg_pendentes
    ON mensagens (empresa_id, data_disparo)
    WHERE enviada_em IS NULL AND direcao = 'saida';

-- [C10] ix_msg_ack REMOVIDO: era (instance_name, wa_message_id) com
-- predicado mais frouxo que uq_msg_wa_id, ou seja, o mesmo lookup do
-- handler de ACK já é servido pelo índice único. Um índice a menos para
-- manter na tabela de maior taxa de escrita do sistema.


-- ---------------------------------------------------------------------
-- LEMBRETES (follow-up)
-- ---------------------------------------------------------------------

CREATE TABLE lembretes (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id     bigint               NOT NULL REFERENCES empresas(id),
    contato_id     bigint               NOT NULL,
    conversa_id    bigint               NULL,

    origem         origem_lembrete_enum NOT NULL,
    status         status_lembrete_enum NOT NULL DEFAULT 'pendente',

    -- Data-alvo, calculada na APLICAÇÃO, não no banco. A regra que preserva
    -- o índice não é usar igualdade — é não aplicar FUNÇÃO sobre a coluna.
    -- (CURRENT_DATE - data_x) descarta o índice; data_alvo <= :hoje faz
    -- range scan normal.
    --
    -- [C17] O motor usa <=, não =. Com igualdade estrita, um dia de
    -- indisponibilidade perde o lembrete para sempre. Com <=, o atrasado
    -- entra na próxima rodada. O teto diário e o uq_msg_lembrete impedem
    -- que a recuperação vire enxurrada.
    data_alvo      date                 NOT NULL,
    hora_alvo      time                 NULL,

    titulo         text                 NOT NULL,
    observacao     text                 NULL,

    -- Só o lembrete automático dispara mensagem. O manual é lembrete de
    -- ação para o vendedor (ligar, visitar) e aparece no Meu Dia.
    envia_mensagem boolean              NOT NULL DEFAULT false,
    texto_mensagem text                 NULL,

    responsavel_id bigint               NULL,
    criado_por     bigint               NULL,

    concluido_em   timestamptz          NULL,
    concluido_por  bigint               NULL,

    criado_em      timestamptz          NOT NULL DEFAULT now(),
    atualizado_em  timestamptz          NOT NULL DEFAULT now(),

    -- Se envia mensagem, precisa ter o que enviar.
    CONSTRAINT ck_lembretes_texto CHECK (
        NOT envia_mensagem OR texto_mensagem IS NOT NULL),

    CONSTRAINT fk_lembretes_contato FOREIGN KEY (contato_id, empresa_id)
        REFERENCES contatos (id, empresa_id),
    CONSTRAINT fk_lembretes_conversa FOREIGN KEY (conversa_id, empresa_id)
        REFERENCES conversas (id, empresa_id),
    CONSTRAINT fk_lembretes_responsavel FOREIGN KEY (responsavel_id, empresa_id)
        REFERENCES usuarios (id, empresa_id),
    CONSTRAINT fk_lembretes_criado_por FOREIGN KEY (criado_por, empresa_id)
        REFERENCES usuarios (id, empresa_id),
    CONSTRAINT fk_lembretes_concluido_por FOREIGN KEY (concluido_por, empresa_id)
        REFERENCES usuarios (id, empresa_id),

    CONSTRAINT uq_lembretes_id_empresa UNIQUE (id, empresa_id)
);

-- Meu Dia: o que este vendedor tem para fazer hoje.
CREATE INDEX ix_lembretes_dia
    ON lembretes (empresa_id, data_alvo, responsavel_id)
    WHERE status = 'pendente';

-- Rodada do motor: o que disparar hoje (e o que ficou para trás).
CREATE INDEX ix_lembretes_disparo
    ON lembretes (empresa_id, data_alvo)
    WHERE status = 'pendente' AND envia_mensagem;

-- ===== TETO DIÁRIO ANTI-SPAM =====
-- No máximo um lembrete AUTOMÁTICO com mensagem por contato por dia.
-- É a defesa que impede queimar o número: disparo em lote para o mesmo
-- destinatário é o jeito clássico de ser banido. O banco é o árbitro,
-- não a aplicação.
-- Cancelar libera a vaga do dia — intencional.
CREATE UNIQUE INDEX uq_lembrete_teto_diario
    ON lembretes (contato_id, data_alvo)
    WHERE origem = 'automatico' AND envia_mensagem
      AND status <> 'cancelado';

CREATE TRIGGER tg_lembretes_atualizado BEFORE UPDATE ON lembretes
    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();

-- FK circular resolvida no fim (mensagens nasce antes de lembretes).
ALTER TABLE mensagens
    ADD CONSTRAINT fk_mensagens_lembrete
    FOREIGN KEY (lembrete_id, empresa_id) REFERENCES lembretes (id, empresa_id);


-- ---------------------------------------------------------------------
-- SEED DAS ETAPAS
-- ---------------------------------------------------------------------
-- Executar no cadastro da empresa. As 5 etapas fixas da fase 1.
--
-- INSERT INTO etapas_funil (empresa_id, nome, ordem, cor, e_ganho) VALUES
--   (:id, 'Novo Lead',            1, '#7FA88B', false),
--   (:id, 'Primeiro Atendimento', 2, '#5C8F6E', false),
--   (:id, 'Proposta',             3, '#3E7554', false),
--   (:id, 'Negociação',           4, '#2F5D3A', false),
--   (:id, 'Venda',                5, '#1E4028', true);


-- =====================================================================
-- CONSULTAS DE REFERÊNCIA
-- Não fazem parte do schema; ficam aqui porque justificam os índices.
-- =====================================================================

-- SEMÁFORO — faixas por tempo sem resposta.
-- Só acende dentro da janela de atendimento; senão pisca de madrugada e o
-- vendedor abre o sistema de manhã com tudo vermelho sem ter culpa.
--
-- SELECT c.id, c.contato_id,
--        CASE
--          WHEN now() - c.aguardando_desde > interval '4 hours'  THEN 'vermelho'
--          WHEN now() - c.aguardando_desde > interval '1 hour'   THEN 'amarelo'
--          ELSE 'verde'
--        END AS urgencia
--   FROM conversas c
--  WHERE c.empresa_id = :empresa
--    AND c.aguardando_desde IS NOT NULL
--    AND c.status = 'aberta'
--  ORDER BY c.aguardando_desde;

-- MEU DIA — união de conversas esperando resposta e lembretes de hoje.
-- Não precisa de tabela própria: é uma leitura de duas fontes que já existem.
--
-- (a) conversas com aguardando_desde não nulo, do responsável ou sem dono
-- (b) lembretes pendentes com data_alvo <= hoje, do responsável
-- ordenado por urgência e hora_alvo

-- DASHBOARD — os quatro números.
--   leads hoje           → contatos WHERE criado_em >= date_trunc('day', now())
--   aguardando resposta  → conversas WHERE aguardando_desde IS NOT NULL
--   follow-ups pendentes → lembretes WHERE status='pendente' AND data_alvo <= current_date
--   vendas do mês        → contatos WHERE ganho_em >= date_trunc('month', now())
--
-- Nota: usar `criado_em >= date_trunc('day', now())` e não
-- `criado_em::date = current_date` — o cast é função sobre a coluna e
-- descarta ix_contatos_criado.
--
-- Separar o endpoint barato (contadores, polling de 45s) do caro (funil,
-- gráficos, sob demanda). O shell faz poll do barato; a página do dashboard
-- pede o caro uma vez.

-- [C8] RECONCILIAÇÃO DE aguardando_desde — sanity check.
-- Materializar troca correção por velocidade; esta consulta mostra a dívida.
-- Deve voltar zero linhas. Rodar no job noturno e logar (ou corrigir) o que
-- aparecer; divergência significa INSERT de mensagem fora da transação que
-- atualiza a conversa.
--
-- SELECT c.id, c.aguardando_desde, esperado.deveria_ser
--   FROM conversas c
--   JOIN LATERAL (
--        SELECT CASE WHEN m.direcao = 'entrada' THEN m.criado_em END AS deveria_ser
--          FROM mensagens m
--         WHERE m.conversa_id = c.id
--         ORDER BY m.id DESC
--         LIMIT 1
--   ) esperado ON true
--  WHERE c.empresa_id = :empresa
--    AND (c.aguardando_desde IS NULL) <> (esperado.deveria_ser IS NULL);

-- [C13] RENUMERAÇÃO DE ordem_kanban — higiene, não correção.
-- numeric sem escala nunca esgota o ponto médio, mas os valores viram
-- dízimas longas depois de muitos arrastes. Rodar sob demanda por coluna.
--
-- WITH ordenado AS (
--   SELECT id, row_number() OVER (ORDER BY ordem_kanban, id) * 1000 AS nova
--     FROM contatos
--    WHERE empresa_id = :empresa AND etapa_id = :etapa AND perdido_em IS NULL
-- )
-- UPDATE contatos c SET ordem_kanban = o.nova
--   FROM ordenado o WHERE o.id = c.id;
