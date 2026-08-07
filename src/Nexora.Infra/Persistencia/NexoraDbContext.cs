using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Nexora.Core;
using Nexora.Core.Entidades;

namespace Nexora.Infra.Persistencia;

/// <summary>Mapeamento explicito para snake_case via HasColumnName. NAO usamos o pacote
/// EFCore.NamingConventions de proposito: mapeamento explicito falha alto e claro no boot
/// se divergir do banco, e nao adiciona dependencia.
///
/// A fonte da verdade do schema sao as MIGRATIONS deste projeto — nunca um .sql aplicado
/// a mao. O docs/SCHEMA-NEXORA.sql e especificacao: ao mudar o modelo, gere a migration e
/// confira o DDL contra ele.</summary>
public class NexoraDbContext(DbContextOptions<NexoraDbContext> options, IContextoEmpresa contexto)
    : DbContext(options)
{
    private readonly IContextoEmpresa _contexto = contexto;

    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        // O EF cria, por convencao, um indice para CADA chave estrangeira. Com as FKs
        // compostas (id, empresa_id) isso gerava 12 indices que a especificacao nao pede —
        // 5 deles em `mensagens`, a tabela de maior taxa de escrita do sistema.
        //
        // O Postgres NAO cria indice de FK sozinho, entao o DDL do EF divergia do schema. Os
        // indices de FK servem principalmente para acelerar a verificacao referencial ao
        // APAGAR o pai; aqui todas as FKs sao RESTRICT e o desenho nao tem delete fisico —
        // pagariam custo de escrita sem uso.
        //
        // Consequencia: relacao nova NAO ganha indice automatico. Se uma consulta precisar,
        // declare o indice explicitamente (e no schema tambem).
        cfg.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Conexao> Conexoes => Set<Conexao>();
    public DbSet<EtapaFunil> EtapasFunil => Set<EtapaFunil>();
    public DbSet<Contato> Contatos => Set<Contato>();
    public DbSet<Conversa> Conversas => Set<Conversa>();
    public DbSet<Mensagem> Mensagens => Set<Mensagem>();
    public DbSet<Lembrete> Lembretes => Set<Lembrete>();
    public DbSet<Feriado> Feriados => Set<Feriado>();
    public DbSet<FeriadoIgnorado> FeriadosIgnorados => Set<FeriadoIgnorado>();
    public DbSet<EmailEnviado> EmailsEnviados => Set<EmailEnviado>();
    public DbSet<FormularioCaptura> FormulariosCaptura => Set<FormularioCaptura>();
    public DbSet<CanalCaptacao> CanaisCaptacao => Set<CanalCaptacao>();
    public DbSet<WebhookSaida> WebhooksSaida => Set<WebhookSaida>();
    public DbSet<EntregaWebhook> EntregasWebhook => Set<EntregaWebhook>();

    /// <summary>O HISTORICO de vendas (NEG-1). `contatos.ganho_em` continua existindo e continua
    /// sendo o carimbo do estado atual — mas quem responde "quanto faturamos" e esta tabela.</summary>
    public DbSet<Venda> Vendas => Set<Venda>();

    /// <summary>A TRILHA (AUD-1): o que mudou, de que para que, por quem, quando.</summary>
    public DbSet<Auditoria> Auditoria => Set<Auditoria>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Enums NATIVOS do Postgres. O Npgsql traduz o membro para snake_case
        // (Vendedor -> 'vendedor'). Precisam ser registrados nos DOIS lugares: aqui, para
        // a migration emitir o CREATE TYPE, e no NpgsqlDataSourceBuilder (ver ServicosInfra).
        // Esquecer o segundo compila e quebra so em runtime.
        //
        // ARMADILHA: a assinatura e HasPostgresEnum<T>(schema, name, nameTranslator). Passar
        // um unico argumento posicional define o SCHEMA, nao o nome — a migration entao emite
        // CREATE TYPE num schema inexistente e o `database update` morre com
        // 42704: tipo "papel_usuario_enum" nao existe. Por isso o `name:` explicito.
        // (O Recupera faz as 14 chamadas dele com o argumento posicional; la nunca apareceu
        // porque o schema e aplicado a mao e nenhuma migration chegou a ser gerada.)
        mb.HasPostgresEnum<PapelUsuario>(name: "papel_usuario_enum");
        mb.HasPostgresEnum<StatusUsuario>(name: "status_usuario_enum");
        mb.HasPostgresEnum<StatusConexao>(name: "status_conexao_enum");
        mb.HasPostgresEnum<StatusVenda>(name: "status_venda_enum");
        mb.HasPostgresEnum<OrigemLead>(name: "origem_lead_enum");
        mb.HasPostgresEnum<DirecaoMensagem>(name: "direcao_mensagem_enum");
        mb.HasPostgresEnum<TipoMidia>(name: "tipo_midia_enum");
        mb.HasPostgresEnum<StatusConversa>(name: "status_conversa_enum");
        mb.HasPostgresEnum<StatusLembrete>(name: "status_lembrete_enum");
        mb.HasPostgresEnum<OrigemLembrete>(name: "origem_lembrete_enum");
        mb.HasPostgresEnum<AbrangenciaFeriado>(name: "abrangencia_feriado_enum");
        mb.HasPostgresEnum<EventoWebhook>(name: "evento_webhook_enum");
        mb.HasPostgresEnum<StatusEntregaWebhook>(name: "status_entrega_webhook_enum");

        mb.Entity<Empresa>(e =>
        {
            e.ToTable("empresas", t =>
            {
                t.HasCheckConstraint("ck_empresas_janela", "janela_hora_inicio < janela_hora_fim");
                t.HasCheckConstraint("ck_empresas_hora_faixa",
                    "janela_hora_inicio BETWEEN 0 AND 23 AND janela_hora_fim BETWEEN 1 AND 24");
                t.HasCheckConstraint("ck_empresas_dias", "janela_dias_semana BETWEEN 1 AND 127");
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired();
            e.Property(x => x.Documento).HasColumnName("documento");
            e.Property(x => x.Ativo).HasColumnName("ativo").HasDefaultValue(true);
            // Default FALSE no banco, não só no C#: empresa criada por SQL cru (seed, migration,
            // correção manual) não pode nascer como demonstração por omissão.
            e.Property(x => x.Demonstracao).HasColumnName("demonstracao").HasDefaultValue(false);

            // Quantos números a empresa pode conectar. Default 1 no BANCO, não só no C#: empresa
            // criada por SQL cru não pode nascer sem limite e ganhar multi-número de graça.
            // O teto de 20 é freio contra digitação errada — ninguém opera 20 números num painel.
            e.Property(x => x.LimiteConexoes)
                .HasColumnName("limite_conexoes").HasDefaultValue((short)1);
            // NEG-2: zero = concluir na hora, e e valor legitimo (padaria, salao). O CHECK so
            // impede negativo e exagero — 90 dias ja e "nunca conclui" na pratica.
            e.Property(x => x.DiasParaConcluirVenda)
                .HasColumnName("dias_para_concluir_venda").HasDefaultValue((short)7);
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_empresas_limite_conexoes", "limite_conexoes BETWEEN 1 AND 20");
                t.HasCheckConstraint(
                    "ck_empresas_conclusao", "dias_para_concluir_venda BETWEEN 0 AND 90");
            });
            e.Property(x => x.JanelaHoraInicio).HasColumnName("janela_hora_inicio").HasDefaultValue((short)8);
            e.Property(x => x.JanelaHoraFim).HasColumnName("janela_hora_fim").HasDefaultValue((short)20);
            e.Property(x => x.JanelaDiasSemana).HasColumnName("janela_dias_semana").HasDefaultValue((short)126);
            e.Property(x => x.FusoHorario).HasColumnName("fuso_horario")
                .IsRequired().HasDefaultValue("America/Sao_Paulo");
            // char(2) fixo: sigla de UF tem exatamente dois caracteres, e o tipo já barra lixo
            // antes de chegar ao seed de feriados.
            e.Property(x => x.Uf).HasColumnName("uf").HasMaxLength(2).IsFixedLength();
            e.Property(x => x.DiasSemRespostaFollowUp).HasColumnName("dias_sem_resposta_followup")
                .HasDefaultValue((short)2);
            e.Property(x => x.SemaforoAmareloMinutos).HasColumnName("semaforo_amarelo_minutos")
                .HasDefaultValue((short)60);
            e.Property(x => x.SemaforoVermelhoMinutos).HasColumnName("semaforo_vermelho_minutos")
                .HasDefaultValue((short)240);

            // Tempo até o valor + as duas decisões de onboarding. Ver Empresa.cs.
            e.Property(x => x.PrimeiraMensagemEm).HasColumnName("primeira_mensagem_em");
            e.Property(x => x.EquipeDispensadaEm).HasColumnName("equipe_dispensada_em");
            e.Property(x => x.OnboardingDispensadoEm).HasColumnName("onboarding_dispensado_em");

            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            // A empresa so enxerga a si mesma.
            e.HasQueryFilter(x => x.Id == _contexto.EmpresaId);
        });

        mb.Entity<Usuario>(e =>
        {
            e.ToTable("usuarios", t =>
                // So 'convidado' pode estar sem senha. Impede que um bug de fluxo deixe um
                // usuario ativo sem hash — o que o login trataria como "senha errada" para
                // sempre, sem ninguem entender por que.
                t.HasCheckConstraint("ck_usuarios_senha",
                    "status = 'convidado' OR senha_hash IS NOT NULL"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired();
            e.Property(x => x.Email).HasColumnName("email").IsRequired();
            e.Property(x => x.SenhaHash).HasColumnName("senha_hash");
            // HasColumnType explicito: sem ele, o gerador de migration nao sabe que este enum
            // do C# corresponde ao tipo nativo do Postgres e emite a coluna como `integer`
            // — silenciosamente, porque compila e so quebra ao ler o dado. O
            // HasPostgresEnum acima cria o TIPO; isto amarra a COLUNA a ele.
            e.Property(x => x.Papel).HasColumnName("papel").HasColumnType("papel_usuario_enum");
            // Sem HasDefaultValue aqui de proposito: em tempo de design o provider nao tem o
            // mapeamento do enum (ver FabricaDbContextDesignTime) e renderizaria o default
            // como inteiro — o CREATE TABLE sai com DEFAULT 0 e o banco recusa. O default
            // 'ativo' e aplicado por SQL dentro da migration. A propriedade C# ja nasce Ativo,
            // entao o comportamento da aplicacao nao depende disso.
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("status_usuario_enum");
            e.Property(x => x.FalhasLogin).HasColumnName("falhas_login").HasDefaultValue((short)0);
            e.Property(x => x.BloqueadoAte).HasColumnName("bloqueado_ate");
            e.Property(x => x.TokenConvite).HasColumnName("token_convite");
            e.Property(x => x.ConviteExpira).HasColumnName("convite_expira");
            e.Property(x => x.TokenReset).HasColumnName("token_reset");
            e.Property(x => x.ResetExpira).HasColumnName("reset_expira");
            e.Property(x => x.UltimoAcessoEm).HasColumnName("ultimo_acesso_em");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            // Restrict, nao Cascade (que e o default do EF para relacao obrigatoria): apagar
            // uma empresa nao pode levar os usuarios junto por acidente. Nao ha delete fisico
            // no desenho — o caminho de saida e inativar, e a PII sai por anonimizacao.
            e.HasOne(x => x.Empresa).WithMany(o => o.Usuarios)
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Restrict);

            // Alvo das FKs COMPOSTAS (contato.responsavel_id, conversa.responsavel_id, ...).
            // O query filter protege LEITURA; nada impede um bug de aplicacao de gravar um
            // usuario de outro tenant. A FK composta (id, empresa_id) fecha isso no banco.
            //
            // Chave ALTERNATIVA, nao apenas indice unico: o EF so aceita HasPrincipalKey
            // apontando para uma chave do modelo. No banco o efeito e o mesmo (constraint
            // UNIQUE cria o indice).
            e.HasAlternateKey(x => new { x.Id, x.EmpresaId }).HasName("uq_usuarios_id_empresa");

            e.HasIndex(x => new { x.EmpresaId, x.Status }).HasDatabaseName("ix_usuarios_empresa");

            // Indices PARCIAIS: so as linhas que de fato tem token entram. O Recupera nao
            // indexa os tokens e faz varredura completa a cada aceite de convite.
            e.HasIndex(x => x.TokenConvite).IsUnique()
                .HasDatabaseName("uq_usuarios_token_convite")
                .HasFilter("token_convite IS NOT NULL");
            e.HasIndex(x => x.TokenReset).IsUnique()
                .HasDatabaseName("uq_usuarios_token_reset")
                .HasFilter("token_reset IS NOT NULL");

            // ARMADILHA: o LOGIN busca o usuario por e-mail ANTES de existir tenant no
            // contexto. O servico de auth PRECISA usar .IgnoreQueryFilters(), senao a
            // consulta volta vazia e o login nunca autentica ninguem.
            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<Conexao>(e =>
        {
            e.ToTable("conexoes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired();
            e.Property(x => x.InstanceName).HasColumnName("instance_name").IsRequired();
            e.Property(x => x.Numero).HasColumnName("numero");
            e.Property(x => x.NumeroAnterior).HasColumnName("numero_anterior");
            e.Property(x => x.PerfilNome).HasColumnName("perfil_nome");
            e.Property(x => x.PerfilFotoUrl).HasColumnName("perfil_foto_url");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("status_conexao_enum");
            e.Property(x => x.StatusEm).HasColumnName("status_em");
            e.Property(x => x.ConectadoEm).HasColumnName("conectado_em");
            e.Property(x => x.DesconectadoEm).HasColumnName("desconectado_em");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            e.HasAlternateKey(x => new { x.Id, x.EmpresaId }).HasName("uq_conexoes_id_empresa");

            // instance_name e unico GLOBALMENTE, nao por empresa: o webhook casa por ele sem
            // tenant no contexto, entao duas empresas com a mesma instancia tornariam o tenant
            // ambiguo.
            e.HasIndex(x => x.InstanceName).IsUnique().HasDatabaseName("uq_conexoes_instance");

            // O indice `uq_conexoes_empresa` (unico em empresa_id) SAIU no ARQ-2: multi-numero
            // entrou, e o limite passou a ser dado da empresa (`empresas.limite_conexoes`), nao
            // uma trava de schema. O comentario que estava aqui previa exatamente isso.
            //
            // O limite virou regra de APLICACAO de proposito: ele muda por contrato, e um numero
            // que muda por contrato nao pode morar num indice — trocar de plano viraria migration.
            e.HasIndex(x => x.EmpresaId).HasDatabaseName("ix_conexoes_empresa");

            // Nome unico DENTRO da empresa. Com N conexoes a tela vira uma lista, e duas linhas
            // "Principal" tornam impossivel saber qual numero e qual.
            e.HasIndex(x => new { x.EmpresaId, x.Nome }).IsUnique()
                .HasDatabaseName("uq_conexoes_empresa_nome");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<EtapaFunil>(e =>
        {
            e.ToTable("etapas_funil");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired();
            e.Property(x => x.Ordem).HasColumnName("ordem");
            e.Property(x => x.Cor).HasColumnName("cor").IsRequired().HasDefaultValue("#2F5D3A");
            e.Property(x => x.EGanho).HasColumnName("e_ganho").HasDefaultValue(false);
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            e.HasAlternateKey(x => new { x.Id, x.EmpresaId }).HasName("uq_etapas_id_empresa");

            e.HasIndex(x => new { x.EmpresaId, x.Ordem }).IsUnique()
                .HasDatabaseName("uq_etapas_ordem");

            // Uma unica etapa terminal de ganho por empresa. Indice unico PARCIAL: sem o
            // WHERE, so uma etapa da empresa inteira poderia existir.
            e.HasIndex(x => x.EmpresaId).IsUnique()
                .HasDatabaseName("uq_etapas_ganho")
                .HasFilter("e_ganho");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<Contato>(e =>
        {
            e.ToTable("contatos", t =>
                t.HasCheckConstraint("ck_contatos_terminal",
                    "ganho_em IS NULL OR perdido_em IS NULL"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired();
            e.Property(x => x.Telefone).HasColumnName("telefone").IsRequired();
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Origem).HasColumnName("origem").HasColumnType("origem_lead_enum");
            e.Property(x => x.OrigemDetalhe).HasColumnName("origem_detalhe");
            e.Property(x => x.EtapaId).HasColumnName("etapa_id");
            // numeric SEM precisao declarada: o ponto medio do kanban nunca esgota. Ver a
            // entidade. HasColumnType explicito porque o default do EF para decimal e
            // numeric(18,2), que quebraria a insercao entre dois cards vizinhos.
            e.Property(x => x.OrdemKanban).HasColumnName("ordem_kanban")
                .HasColumnType("numeric").HasDefaultValue(0m);

            // ===== CONCORRÊNCIA OTIMISTA NO CARD =====
            // `xmin` é coluna de SISTEMA do Postgres: existe em toda linha e guarda a transação
            // que a escreveu por último. Mapeada como token de concorrência, todo UPDATE do EF
            // passa a levar `WHERE ... AND xmin = @lido` — e se outra pessoa gravou no meio, o
            // UPDATE afeta zero linhas e o EF lança DbUpdateConcurrencyException.
            //
            // Não vira coluna em migration: o provedor do Npgsql reconhece `xid` como sistema.
            e.Property(x => x.Versao).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.Property(x => x.ResponsavelId).HasColumnName("responsavel_id");
            e.Property(x => x.Valor).HasColumnName("valor").HasColumnType("numeric(14,2)");
            e.Property(x => x.GanhoEm).HasColumnName("ganho_em");
            e.Property(x => x.PerdidoEm).HasColumnName("perdido_em");
            e.Property(x => x.MotivoPerda).HasColumnName("motivo_perda");
            e.Property(x => x.Observacoes).HasColumnName("observacoes");
            e.Property(x => x.AnonimizadoEm).HasColumnName("anonimizado_em");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // FKs COMPOSTAS com empresa_id: o query filter protege leitura, nao escrita.
            // Sem isto, um bug de aplicacao grava etapa_id de outro tenant e ninguem percebe.
            e.HasOne(x => x.Etapa).WithMany()
                .HasForeignKey(x => new { x.EtapaId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_contatos_etapa")
                .OnDelete(DeleteBehavior.Restrict);

            // responsavel_id nulo nao e verificado (MATCH SIMPLE) — e o comportamento desejado.
            e.HasOne(x => x.Responsavel).WithMany()
                .HasForeignKey(x => new { x.ResponsavelId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_contatos_responsavel")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasAlternateKey(x => new { x.Id, x.EmpresaId }).HasName("uq_contatos_id_empresa");

            // Um telefone por empresa, mas SO entre contatos vivos. Sem o predicado parcial,
            // anonimizar o segundo contato da empresa viola o indice (dois telefones zerados
            // colidem) e a LGPD so funciona uma vez por tenant.
            e.HasIndex(x => new { x.EmpresaId, x.Telefone }).IsUnique()
                .HasDatabaseName("uq_contatos_telefone")
                .HasFilter("anonimizado_em IS NULL");

            // Kanban: carrega uma coluna do funil. Parcial porque lead perdido nao aparece no
            // quadro — sem o filtro o indice carrega linhas que a consulta sempre descarta.
            e.HasIndex(x => new { x.EmpresaId, x.EtapaId, x.OrdemKanban })
                .HasDatabaseName("ix_contatos_kanban")
                .HasFilter("perdido_em IS NULL");

            e.HasIndex(x => new { x.EmpresaId, x.CriadoEm })
                .HasDatabaseName("ix_contatos_criado")
                .IsDescending(false, true);

            e.HasIndex(x => new { x.EmpresaId, x.GanhoEm })
                .HasDatabaseName("ix_contatos_ganho")
                .IsDescending(false, true)
                .HasFilter("ganho_em IS NOT NULL");

            e.HasIndex(x => new { x.EmpresaId, x.ResponsavelId })
                .HasDatabaseName("ix_contatos_responsavel")
                .HasFilter("responsavel_id IS NOT NULL");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<Conversa>(e =>
        {
            e.ToTable("conversas", t =>
                t.HasCheckConstraint("ck_conversas_nao_lidas", "nao_lidas >= 0"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.ContatoId).HasColumnName("contato_id");
            e.Property(x => x.ConexaoId).HasColumnName("conexao_id");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("status_conversa_enum");
            e.Property(x => x.ResponsavelId).HasColumnName("responsavel_id");
            e.Property(x => x.AtribuidoEm).HasColumnName("atribuido_em");
            e.Property(x => x.AguardandoDesde).HasColumnName("aguardando_desde");
            e.Property(x => x.UltimaMensagemEm).HasColumnName("ultima_mensagem_em")
                .HasDefaultValueSql("now()");
            e.Property(x => x.UltimaMensagemDirecao).HasColumnName("ultima_mensagem_direcao")
                .HasColumnType("direcao_mensagem_enum");
            e.Property(x => x.UltimaMensagemPrevia).HasColumnName("ultima_mensagem_previa");
            e.Property(x => x.NaoLidas).HasColumnName("nao_lidas").HasDefaultValue(0);
            e.Property(x => x.ResolvidoEm).HasColumnName("resolvido_em");
            e.Property(x => x.ResolvidoPor).HasColumnName("resolvido_por");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Contato).WithMany()
                .HasForeignKey(x => new { x.ContatoId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_conversas_contato")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Conexao).WithMany()
                .HasForeignKey(x => new { x.ConexaoId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_conversas_conexao")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Responsavel).WithMany()
                .HasForeignKey(x => new { x.ResponsavelId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_conversas_responsavel")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.UsuarioResolveu).WithMany()
                .HasForeignKey(x => new { x.ResolvidoPor, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_conversas_resolvido_por")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasAlternateKey(x => new { x.Id, x.EmpresaId }).HasName("uq_conversas_id_empresa");

            // 1:1 com contato na fase 1.
            e.HasIndex(x => x.ContatoId).IsUnique().HasDatabaseName("uq_conversas_contato");

            // Lista da caixa de entrada. O par (ultima_mensagem_em DESC, id DESC) e o MESMO
            // do cursor de paginacao — paginar por valor, nunca por offset: a lista se
            // reordena em tempo real e offset pula ou repete linha.
            e.HasIndex(x => new { x.EmpresaId, x.Status, x.UltimaMensagemEm, x.Id })
                .HasDatabaseName("ix_conversas_lista")
                .IsDescending(false, false, true, true);

            // Semaforo, Meu Dia e o contador "aguardando resposta" do dashboard.
            e.HasIndex(x => new { x.EmpresaId, x.AguardandoDesde })
                .HasDatabaseName("ix_conversas_aguardando")
                .HasFilter("aguardando_desde IS NOT NULL");

            e.HasIndex(x => new { x.EmpresaId, x.ResponsavelId, x.UltimaMensagemEm })
                .HasDatabaseName("ix_conversas_responsavel")
                .IsDescending(false, false, true)
                .HasFilter("responsavel_id IS NOT NULL");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<Mensagem>(e =>
        {
            e.ToTable("mensagens", t =>
            {
                t.HasCheckConstraint("ck_msg_ack", "ack IS NULL OR ack BETWEEN 0 AND 4");
                t.HasCheckConstraint("ck_msg_data_disparo",
                    "direcao = 'entrada' OR data_disparo IS NOT NULL");
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.ConversaId).HasColumnName("conversa_id");
            e.Property(x => x.ContatoId).HasColumnName("contato_id");
            e.Property(x => x.ConexaoId).HasColumnName("conexao_id");
            e.Property(x => x.InstanceName).HasColumnName("instance_name").IsRequired();
            e.Property(x => x.Direcao).HasColumnName("direcao").HasColumnType("direcao_mensagem_enum");
            e.Property(x => x.WaMessageId).HasColumnName("wa_message_id");
            e.Property(x => x.Texto).HasColumnName("texto");
            e.Property(x => x.TipoMidia).HasColumnName("tipo_midia").HasColumnType("tipo_midia_enum");
            e.Property(x => x.MidiaChave).HasColumnName("midia_chave");
            e.Property(x => x.MidiaMime).HasColumnName("midia_mime");
            e.Property(x => x.MidiaNome).HasColumnName("midia_nome");
            e.Property(x => x.MidiaBytes).HasColumnName("midia_bytes");
            e.Property(x => x.MidiaDuracaoSegundos).HasColumnName("midia_duracao_segundos");
            e.Property(x => x.Ack).HasColumnName("ack");
            e.Property(x => x.AckEm).HasColumnName("ack_em");
            e.Property(x => x.EnviadoPor).HasColumnName("enviado_por");
            e.Property(x => x.LembreteId).HasColumnName("lembrete_id");
            e.Property(x => x.DataDisparo).HasColumnName("data_disparo");
            // ValueGeneratedOnAdd: quem nao informar recebe o default do banco em vez de
            // 0001-01-01. Mensagem nao tem atualizado_em (log append-only), entao so criado_em
            // passa pelo InterceptorAuditoria.
            e.Property(x => x.ReservadoEm).HasColumnName("reservado_em")
                .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
            e.Property(x => x.EnviadaEm).HasColumnName("enviada_em");
            e.Property(x => x.RecebidaEm).HasColumnName("recebida_em");
            e.Property(x => x.RecuperadaEm).HasColumnName("recuperada_em");
            e.Property(x => x.Tentativas).HasColumnName("tentativas").HasDefaultValue((short)0);
            e.Property(x => x.ExpiradaEm).HasColumnName("expirada_em");
            e.Property(x => x.Erro).HasColumnName("erro");
            e.Property(x => x.PayloadRaw).HasColumnName("payload_raw").HasColumnType("jsonb");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Conversa).WithMany()
                .HasForeignKey(x => new { x.ConversaId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_msg_conversa")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Contato).WithMany()
                .HasForeignKey(x => new { x.ContatoId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_msg_contato")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Conexao).WithMany()
                .HasForeignKey(x => new { x.ConexaoId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_msg_conexao")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.UsuarioEnviou).WithMany()
                .HasForeignKey(x => new { x.EnviadoPor, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_msg_enviado_por")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Lembrete).WithMany()
                .HasForeignKey(x => new { x.LembreteId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_mensagens_lembrete")
                .OnDelete(DeleteBehavior.Restrict);

            // Mensagem recuperada e RARA — so existe quando houve queda. Indice PARCIAL: sem o
            // predicado seriam milhoes de NULLs indexados para o aviso da caixa encontrar dezenas.
            e.HasIndex(x => new { x.EmpresaId, x.RecuperadaEm })
                .HasDatabaseName("ix_msg_recuperada")
                .HasFilter("recuperada_em IS NOT NULL");

            // ===== INVARIANTE 1 — DEDUPE DE RECEBIMENTO =====
            // Cobre DOIS casos de uma vez: o webhook reentregue pela Evolution (ela reentrega
            // ate receber 2xx) e o ECO do proprio envio (ela devolve por webhook a mensagem
            // que acabamos de mandar). O INSERT ... ON CONFLICT DO NOTHING RETURNING id volta
            // vazio nos dois, e o handler sabe que e para pular.
            //
            // O predicado exclui string VAZIA alem de NULL: duas linhas com '' colidiriam
            // entre si, e a Evolution as vezes responde 2xx sem key.id.
            e.HasIndex(x => new { x.InstanceName, x.WaMessageId }).IsUnique()
                .HasDatabaseName("uq_msg_wa_id")
                .HasFilter("wa_message_id IS NOT NULL AND wa_message_id <> ''");

            // ===== INVARIANTE 2 — DEDUPE DE ENVIO =====
            // O teto diario (em lembretes) garante que nao existam DOIS lembretes para o mesmo
            // contato no mesmo dia; nao garante que UM lembrete nao seja enviado duas vezes.
            // Um crash entre "insere mensagem" e "marca lembrete concluido", ou duas instancias
            // do motor, reenviariam. Aqui o banco e o arbitro.
            e.HasIndex(x => x.LembreteId).IsUnique()
                .HasDatabaseName("uq_msg_lembrete")
                .HasFilter("lembrete_id IS NOT NULL");

            // Timeline da conversa; tambem serve o cursor de paginacao da thread.
            e.HasIndex(x => new { x.EmpresaId, x.ConversaId, x.Id })
                .HasDatabaseName("ix_msg_timeline")
                .IsDescending(false, false, true);

            // Drenagem das reservas nao despachadas.
            //
            // `expirada_em IS NULL` entrou no filtro no bloco 4: sem isso, toda reserva que
            // esgota a janela de reenvio fica no indice para sempre, e a drenagem passa a
            // carregar linhas que ela sempre descarta. Indice de drenagem so deve conter o que
            // ainda pode sair.
            e.HasIndex(x => new { x.EmpresaId, x.DataDisparo })
                .HasDatabaseName("ix_msg_pendentes")
                .HasFilter("enviada_em IS NULL AND expirada_em IS NULL AND direcao = 'saida'");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<Lembrete>(e =>
        {
            e.ToTable("lembretes", t =>
                t.HasCheckConstraint("ck_lembretes_texto",
                    "NOT envia_mensagem OR texto_mensagem IS NOT NULL"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.ContatoId).HasColumnName("contato_id");
            e.Property(x => x.ConversaId).HasColumnName("conversa_id");
            e.Property(x => x.Origem).HasColumnName("origem").HasColumnType("origem_lembrete_enum");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("status_lembrete_enum");
            e.Property(x => x.DataAlvo).HasColumnName("data_alvo");
            e.Property(x => x.HoraAlvo).HasColumnName("hora_alvo");
            e.Property(x => x.Titulo).HasColumnName("titulo").IsRequired();
            e.Property(x => x.Observacao).HasColumnName("observacao");
            e.Property(x => x.EnviaMensagem).HasColumnName("envia_mensagem").HasDefaultValue(false);
            e.Property(x => x.TextoMensagem).HasColumnName("texto_mensagem");
            e.Property(x => x.ResponsavelId).HasColumnName("responsavel_id");
            e.Property(x => x.CriadoPor).HasColumnName("criado_por");
            e.Property(x => x.ConcluidoEm).HasColumnName("concluido_em");
            e.Property(x => x.ConcluidoPor).HasColumnName("concluido_por");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Contato).WithMany()
                .HasForeignKey(x => new { x.ContatoId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_lembretes_contato")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Conversa).WithMany()
                .HasForeignKey(x => new { x.ConversaId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_lembretes_conversa")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Responsavel).WithMany()
                .HasForeignKey(x => new { x.ResponsavelId, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_lembretes_responsavel")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.UsuarioCriou).WithMany()
                .HasForeignKey(x => new { x.CriadoPor, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_lembretes_criado_por")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.UsuarioConcluiu).WithMany()
                .HasForeignKey(x => new { x.ConcluidoPor, x.EmpresaId })
                .HasPrincipalKey(p => new { p.Id, p.EmpresaId })
                .HasConstraintName("fk_lembretes_concluido_por")
                .OnDelete(DeleteBehavior.Restrict);

            e.HasAlternateKey(x => new { x.Id, x.EmpresaId }).HasName("uq_lembretes_id_empresa");

            // Meu Dia: o que este vendedor tem para fazer hoje.
            e.HasIndex(x => new { x.EmpresaId, x.DataAlvo, x.ResponsavelId })
                .HasDatabaseName("ix_lembretes_dia")
                .HasFilter("status = 'pendente'");

            // Rodada do motor: o que disparar hoje (e o que ficou para tras).
            e.HasIndex(x => new { x.EmpresaId, x.DataAlvo })
                .HasDatabaseName("ix_lembretes_disparo")
                .HasFilter("status = 'pendente' AND envia_mensagem");

            // ===== TETO DIARIO ANTI-SPAM =====
            // No maximo UM lembrete automatico com mensagem por contato por dia. Disparo em
            // lote para o mesmo destinatario e o jeito classico de ter o numero banido — e a
            // defesa mora no banco, nao na aplicacao. Cancelar libera a vaga do dia
            // (intencional: o vendedor pode remarcar).
            e.HasIndex(x => new { x.ContatoId, x.DataAlvo }).IsUnique()
                .HasDatabaseName("uq_lembrete_teto_diario")
                .HasFilter("origem = 'automatico' AND envia_mensagem AND status <> 'cancelado'");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<FormularioCaptura>(e =>
        {
            e.ToTable("formularios_captura");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired().HasMaxLength(80);
            e.Property(x => x.Chave).HasColumnName("chave").IsRequired().HasMaxLength(64);
            e.Property(x => x.DominioPermitido).HasColumnName("dominio_permitido").HasMaxLength(200);
            e.Property(x => x.Ativo).HasColumnName("ativo").HasDefaultValue(true);
            e.Property(x => x.LeadsRecebidos).HasColumnName("leads_recebidos").HasDefaultValue(0);
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // ===== A CHAVE É ÚNICA GLOBALMENTE, NÃO POR EMPRESA =====
            // A URL pública (`POST /api/captura/{chave}`) não carrega o tenant: é a chave que o
            // resolve. Única por empresa permitiria duas empresas com a mesma chave, e a
            // resolução passaria a depender de qual linha o banco devolvesse primeiro — que é o
            // desenho de um vazamento entre tenants.
            e.HasIndex(x => x.Chave).IsUnique().HasDatabaseName("uq_formularios_chave");

            e.HasIndex(x => new { x.EmpresaId, x.Nome }).HasDatabaseName("ix_formularios_empresa");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<CanalCaptacao>(e =>
        {
            e.ToTable("canais_captacao");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired().HasMaxLength(80);
            // varchar(4), NAO char(4). O tamanho e fixo por construcao (CodigoCanal.Tamanho), mas
            // `char` no Postgres preenche com espaco a direita — e a consulta do webhook casa o
            // codigo com `= ANY(text[])`. Um dia alguem grava um codigo curto por engano, o bpchar
            // completa com espacos, o ANY nao casa, e a atribuicao para de funcionar EM SILENCIO.
            // O limite de tamanho ja barra lixo; o padding so traria a armadilha.
            e.Property(x => x.Codigo).HasColumnName("codigo").IsRequired().HasMaxLength(4);
            e.Property(x => x.ConexaoId).HasColumnName("conexao_id");
            e.Property(x => x.Origem).HasColumnName("origem").HasColumnType("origem_lead_enum");
            e.Property(x => x.Ativo).HasColumnName("ativo").HasDefaultValue(true);
            e.Property(x => x.LeadsRecebidos).HasColumnName("leads_recebidos").HasDefaultValue(0);
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // FK COMPOSTA contra `uq_conexoes_id_empresa`, como conversas e mensagens: garante no
            // BANCO que o canal e a conexao sao da mesma empresa. Sem isso, um bug de aplicacao
            // poderia apontar o canal de uma empresa para o numero de outra — e o link `wa.me`
            // levaria o lead para o WhatsApp do concorrente.
            e.HasOne(x => x.Conexao).WithMany()
                .HasForeignKey(x => new { x.ConexaoId, x.EmpresaId })
                .HasPrincipalKey(c => new { c.Id, c.EmpresaId })
                .OnDelete(DeleteBehavior.Restrict);

            // ===== O CODIGO E UNICO POR EMPRESA, NAO GLOBALMENTE =====
            // Ao contrario da chave do formulario, este codigo NAO resolve o tenant: quem resolve
            // e o `instance_name` da conexao que recebeu a mensagem, e a busca ja sai recortada
            // por empresa. Unico global gastaria espaco de codigo a toa e faria duas empresas
            // disputarem `k7m2`.
            e.HasIndex(x => new { x.EmpresaId, x.Codigo }).IsUnique()
                .HasDatabaseName("uq_canais_empresa_codigo");

            // Nome unico dentro da empresa: ele vai para `contatos.origem_detalhe`, e dois canais
            // "Panfleto" tornam o relatorio de origem impossivel de ler.
            e.HasIndex(x => new { x.EmpresaId, x.Nome }).IsUnique()
                .HasDatabaseName("uq_canais_empresa_nome");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<WebhookSaida>(e =>
        {
            e.ToTable("webhooks_saida");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Url).HasColumnName("url").IsRequired().HasMaxLength(500);
            e.Property(x => x.Segredo).HasColumnName("segredo").IsRequired().HasMaxLength(128);
            e.Property(x => x.Ativo).HasColumnName("ativo").HasDefaultValue(true);
            e.Property(x => x.SomenteIds).HasColumnName("somente_ids").HasDefaultValue(false);
            e.Property(x => x.EmLeadCriado).HasColumnName("em_lead_criado").HasDefaultValue(true);
            e.Property(x => x.EmLeadMovido).HasColumnName("em_lead_movido").HasDefaultValue(true);
            e.Property(x => x.EmVendaFechada).HasColumnName("em_venda_fechada").HasDefaultValue(true);
            e.Property(x => x.EmVendaPerdida).HasColumnName("em_venda_perdida").HasDefaultValue(true);
            // Default FALSE no BANCO, nao so no C#: e o evento de maior volume, e webhook criado
            // por SQL cru (correcao manual, migracao de base) nao pode nascer assinando ele.
            e.Property(x => x.EmMensagemRecebida)
                .HasColumnName("em_mensagem_recebida").HasDefaultValue(false);
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // UM por empresa. A trava e de SCHEMA de proposito, ao contrario do limite de conexoes
            // (ARQ-2): la o numero vem do contrato e muda; aqui e decisao de produto, e o dia em
            // que mudar exige tela nova, tabela de entrega por destino e outra conversa de
            // suporte — ou seja, exige a migration de qualquer jeito.
            e.HasIndex(x => x.EmpresaId).IsUnique().HasDatabaseName("uq_webhooks_empresa");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<EntregaWebhook>(e =>
        {
            e.ToTable("entregas_webhook");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.EventoId).HasColumnName("evento_id");
            e.Property(x => x.Evento).HasColumnName("evento").HasColumnType("evento_webhook_enum");
            // jsonb, como `mensagens.payload_raw`: e o corpo EXATO que foi assinado, e guardar
            // como json permite consultar dentro dele numa investigacao de suporte.
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Url).HasColumnName("url").IsRequired().HasMaxLength(500);
            e.Property(x => x.Status).HasColumnName("status")
                .HasColumnType("status_entrega_webhook_enum");
            e.Property(x => x.Tentativas).HasColumnName("tentativas").HasDefaultValue((short)0);
            e.Property(x => x.CodigoResposta).HasColumnName("codigo_resposta");
            e.Property(x => x.Erro).HasColumnName("erro");
            e.Property(x => x.ProximaTentativaEm).HasColumnName("proxima_tentativa_em");
            e.Property(x => x.EntregueEm).HasColumnName("entregue_em");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // ===== O INDICE DA FILA =====
            // A rodada pergunta sempre a mesma coisa: "o que esta pendente e ja venceu?". PARCIAL
            // em `status = 'pendente'` porque o que ja foi entregue nunca mais e lido pela fila —
            // e essa e a parte da tabela que cresce. Sem o filtro, o indice carregaria 30 dias de
            // historico entregue para achar as poucas linhas que importam.
            e.HasIndex(x => x.ProximaTentativaEm)
                .HasDatabaseName("ix_entregas_fila")
                .HasFilter("status = 'pendente'");

            // A tela mostra as ultimas 50 da empresa.
            e.HasIndex(x => new { x.EmpresaId, x.Id }).HasDatabaseName("ix_entregas_empresa");

            // O expurgo varre por data, sem tenant.
            e.HasIndex(x => x.CriadoEm).HasDatabaseName("ix_entregas_criado");

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        // ==================================================================== vendas (NEG-1)
        mb.Entity<Venda>(e =>
        {
            e.ToTable("vendas", t =>
                // Venda de valor zero nao e venda. Diferente de `contatos.valor`, que e estimativa
                // e admite nulo, aqui e dinheiro que entrou — e um zero passando corromperia a
                // soma sem ninguem perceber, que e exatamente o modo de falha que este bloco
                // existe para acabar.
                t.HasCheckConstraint("ck_vendas_valor", "valor > 0"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.ContatoId).HasColumnName("contato_id");
            // Precisao DECLARADA, ao contrario de `contatos.valor`: aquilo e o ponto medio de um
            // kanban e nunca e somado em relatorio; isto e dinheiro, e `numeric` sem precisao
            // aceitaria centavos de ponto flutuante vindos de qualquer cliente da API.
            e.Property(x => x.Valor).HasColumnName("valor").HasColumnType("numeric(14,2)");
            e.Property(x => x.FechadaEm).HasColumnName("fechada_em");
            e.Property(x => x.ResponsavelId).HasColumnName("responsavel_id");
            e.Property(x => x.Observacao).HasColumnName("observacao");
            e.Property(x => x.EtapaId).HasColumnName("etapa_id");
            e.Property(x => x.CanceladaEm).HasColumnName("cancelada_em");
            e.Property(x => x.CanceladaPor).HasColumnName("cancelada_por");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("status_venda_enum");
            e.Property(x => x.ConcluidaEm).HasColumnName("concluida_em");
            e.Property(x => x.ConcluidaPor).HasColumnName("concluida_por");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // Restrict, nao Cascade: apagar contato NAO pode levar o faturamento junto. Se um dia
            // existir remocao de contato, ela tera que decidir o que fazer com o historico — e e
            // melhor que o banco a obrigue a decidir do que sumir com a receita em silencio.
            e.HasOne(x => x.Contato).WithMany(c => c.Vendas)
                .HasForeignKey(x => x.ContatoId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Responsavel).WithMany()
                .HasForeignKey(x => x.ResponsavelId).OnDelete(DeleteBehavior.SetNull);

            // ===== O INDICE DAS CONSULTAS DO DASHBOARD =====
            // `empresa_id` primeiro (convencao do bloco 2), `fechada_em DESC` porque toda pergunta
            // e sobre um periodo recente. PARCIAL, e o predicado e o mesmo do faturamento:
            // cancelada nao entra em contagem nenhuma, e mante-la no indice faria a varredura do
            // mes ler linhas que serao descartadas.
            //
            // NEG-2: o filtro passou de `cancelada_em IS NULL` para `status <> 'cancelada'`, o que
            // MANTEM `concluida` no indice — ela continua sendo receita do mes em que fechou.
            e.HasIndex(x => new { x.EmpresaId, x.FechadaEm })
                .HasDatabaseName("ix_vendas_periodo")
                .IsDescending(false, true)
                .HasFilter("status <> 'cancelada'");

            // A coluna do kanban pergunta "este contato tem venda EM ABERTO?" a cada card.
            e.HasIndex(x => new { x.EmpresaId, x.ContatoId, x.Status })
                .HasDatabaseName("ix_vendas_contato_status");

            // A secao "Vendas" da tela do contato, e a busca da venda vigente ao cancelar.
            e.HasIndex(x => new { x.EmpresaId, x.ContatoId, x.FechadaEm })
                .HasDatabaseName("ix_vendas_contato")
                .IsDescending(false, false, true);

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        // ==================================================================== auditoria (AUD-1)
        mb.Entity<Auditoria>(e =>
        {
            e.ToTable("auditoria");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");

            // TEXTO e nao enum nativo, ao contrario do resto do sistema: estes dois conjuntos
            // crescem a cada tabela/acao que passe a ser auditada, e enum nativo custaria uma
            // migration por valor. A seguranca de tipo fica no C#, que e onde os valores sao
            // escritos; ninguem consulta a trilha por igualdade de enum.
            e.Property(x => x.Entidade).HasColumnName("entidade")
                .HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.EntidadeId).HasColumnName("entidade_id");
            e.Property(x => x.Acao).HasColumnName("acao")
                .HasConversion<string>().HasMaxLength(20).IsRequired();

            e.Property(x => x.Alteracoes).HasColumnName("alteracoes")
                .HasColumnType("jsonb").IsRequired();

            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.Ator).HasColumnName("ator")
                .HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.Quando).HasColumnName("quando");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // SetNull, nao Restrict: desligar um usuario nao pode ser impedido pela trilha, e a
            // linha continua valendo sem ele — `ator` ja diz que foi pessoa, e o nome dela some
            // junto com o cadastro, que e o comportamento correto.
            e.HasOne(x => x.Usuario).WithMany()
                .HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.SetNull);

            // A linha do tempo de UM registro (a tela do contato): entidade + id, mais recente
            // primeiro.
            e.HasIndex(x => new { x.EmpresaId, x.Entidade, x.EntidadeId, x.Quando })
                .HasDatabaseName("ix_auditoria_registro")
                .IsDescending(false, false, false, true);

            // A visao geral da empresa, e tambem o expurgo por retencao.
            e.HasIndex(x => new { x.EmpresaId, x.Quando })
                .HasDatabaseName("ix_auditoria_empresa")
                .IsDescending(false, true);

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<Feriado>(e =>
        {
            e.ToTable("feriados");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Data).HasColumnName("data");
            e.Property(x => x.Nome).HasColumnName("nome").IsRequired();
            e.Property(x => x.Abrangencia).HasColumnName("abrangencia")
                .HasColumnType("abrangencia_feriado_enum");
            e.Property(x => x.Uf).HasColumnName("uf");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.Data).HasDatabaseName("ix_feriados_data");

            // QUERY FILTER DIFERENTE DE TODOS OS OUTROS: admite os GLOBAIS (empresa_id NULL,
            // nacionais/estaduais, visíveis a todo mundo) e isola os manuais por tenant. Assim
            // qualquer `db.Feriados` cru já sai seguro — não vaza feriado local de outra empresa.
            //
            // O seed e o motor rodam como JOB (sem tenant) e usam IgnoreQueryFilters + filtro
            // explícito, como todo caminho não autenticado deste sistema.
            e.HasQueryFilter(x => x.EmpresaId == null || x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<FeriadoIgnorado>(e =>
        {
            e.ToTable("feriados_ignorados");
            // Chave COMPOSTA em vez de id próprio: a linha É a relação, e a PK composta já
            // impede a mesma empresa dispensar o mesmo feriado duas vezes — sem índice extra.
            e.HasKey(x => new { x.EmpresaId, x.FeriadoId });
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.FeriadoId).HasColumnName("feriado_id");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("now()");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Cascade);

            // CASCADE no feriado: se um global for removido algum dia, as dispensas dele morrem
            // junto. Restrict deixaria linha órfã apontando para feriado inexistente.
            e.HasOne(x => x.Feriado).WithMany()
                .HasForeignKey(x => x.FeriadoId).OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(x => x.EmpresaId == _contexto.EmpresaId);
        });

        mb.Entity<EmailEnviado>(e =>
        {
            e.ToTable("emails_enviados");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Destinatario).HasColumnName("destinatario").IsRequired();
            e.Property(x => x.Tipo).HasColumnName("tipo").IsRequired();
            e.Property(x => x.Assunto).HasColumnName("assunto").IsRequired();
            e.Property(x => x.EnviadoEm).HasColumnName("enviado_em").HasDefaultValueSql("now()");
            e.Property(x => x.Sucesso).HasColumnName("sucesso");
            e.Property(x => x.Erro).HasColumnName("erro");

            e.HasOne(x => x.Empresa).WithMany()
                .HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);

            // A consulta que este log existe para servir é "o que foi mandado para este
            // endereço?" — depurar "não recebi" começa sempre pelo destinatário, nunca pela
            // empresa (e no caso do reset público nem há empresa).
            e.HasIndex(x => new { x.Destinatario, x.EnviadoEm })
                .HasDatabaseName("ix_emails_destinatario")
                .IsDescending(false, true);

            // Mesmo formato do query filter de `feriados`: admite as linhas SEM dono (o fluxo
            // público de recuperação de senha) e isola o resto por tenant.
            e.HasQueryFilter(x => x.EmpresaId == null || x.EmpresaId == _contexto.EmpresaId);
        });

        // O indice unico de e-mail e FUNCIONAL — lower(email) — e o EF Core nao expressa
        // indice por expressao no modelo. Ele e criado com SQL cru dentro da migration
        // (ainda versionado, ainda uma migration), nao a mao no banco. Mesmo caso dos
        // triggers de atualizado_em e dos DEFAULTs de coluna enum. Ver as migrations.
    }
}
