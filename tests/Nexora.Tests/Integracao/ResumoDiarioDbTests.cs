using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Resumo;
using Nexora.Core.Servicos;
using Nexora.Infra.Email;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O RESUMO DE ONTEM, PARA O DONO (RES-XX), contra Postgres real e pelo mesmo caminho da
/// rodada: o motor sem tenant, que reserva o dia, assume a empresa como dono e monta os numeros com
/// os servicos das telas.
///
/// O que estes testes seguram: os numeros sao de ONTEM e da empresa INTEIRA (inclusive o que e do
/// vendedor); nada de outra empresa entra; um e-mail por dono ativo, uma vez por dia; e quem nao
/// ligou — ou e demonstracao — nao recebe.</summary>
[Collection("banco")]
public class ResumoDiarioDbTests(BancoTeste banco)
{
    /// <summary>Sexta, 07/08/2026, 8h de Brasilia: a hora da rodada. "Ontem" e quinta, 06/08.</summary>
    private static readonly DateTimeOffset SextaAsOito = new(2026, 8, 7, 11, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Ontem = new(2026, 8, 6);

    /// <summary>Quinta ao meio-dia de Brasilia.</summary>
    private static readonly DateTime OntemMeioDia = new(2026, 8, 6, 15, 0, 0, DateTimeKind.Utc);

    // ==================================================================== os numeros
    /// <summary>===================== ONTEM, DA EMPRESA INTEIRA =====================
    ///
    /// O lead, a venda e a conversa sao do VENDEDOR. Sem o papel de dono assumido pelo job, os
    /// servicos das telas recortariam tudo para "os do usuario" — e o resumo do dono sairia com os
    /// numeros de uma pessoa so.
    /// ======================================================================</summary>
    [Fact]
    public async Task O_RESUMO_CONTA_ONTEM_DA_EMPRESA_INTEIRA()
    {
        var (db, tx, amb) = await PrepararAsync("numeros");
        using var _ = db; using var __ = tx;
        var vendedor = await UsuarioAsync(db, amb, "vendedor", PapelUsuario.Vendedor);

        // ---- leads: dois de ontem contam; o de hoje e o anonimizado, nao
        var lead = await ContatoAsync(db, amb, "Lead do vendedor", OntemMeioDia, vendedor);
        await ContatoAsync(db, amb, "Lead sem dono", OntemMeioDia.AddHours(5), null);
        await ContatoAsync(db, amb, "Lead de hoje", SextaAsOito.UtcDateTime.AddHours(-1), vendedor);
        await ContatoAsync(db, amb, "Anonimizado", OntemMeioDia, vendedor, anonimizado: true);

        // ---- a venda de ontem, do vendedor; e uma antiga, que nao conta
        await NegocioAsync(db, amb, lead, StatusNegociacao.Ganha, OntemMeioDia, 1500m, vendedor);
        var antigo = await NegocioAsync(db, amb, amb.Cenario.Contato.Id, StatusNegociacao.Concluida,
            new DateTime(2026, 7, 1, 15, 0, 0, DateTimeKind.Utc), 900m, vendedor);

        // ---- agora: uma conversa esperando resposta e um lembrete para hoje, do vendedor
        var conversa = await ConversaEsperandoAsync(db, amb, lead, vendedor);
        await LembreteDeHojeAsync(db, amb, lead, vendedor);

        // ---- as automaticas de ontem
        await AutomaticaAsync(db, amb, conversa, lead, enviada: true);
        await AutomaticaAsync(db, amb, conversa, lead, erro: "O WhatsApp está desconectado.");
        await AutomaticaAsync(db, amb, conversa, lead, erro: "O WhatsApp está desconectado.");
        await AutomaticaAsync(db, amb, conversa, lead, expirada: true);
        await AutomaticaAsync(db, amb, conversa, lead);                                   // so esperando: nao e falha
        await AutomaticaAsync(db, amb, conversa, lead, enviada: true, tipo: TipoAutomacao.Nps);   // pesquisa: fora
        await AutomaticaAsync(db, amb, conversa, lead, enviada: true, quando: SextaAsOito.UtcDateTime); // hoje: fora

        // ---- as respostas da pesquisa de ontem (10 e 3); a de hoje fica fora
        await RespostaAsync(db, amb, antigo, 10, OntemMeioDia);
        await RespostaAsync(db, amb, await OutroAntigoAsync(db, amb, vendedor), 3, OntemMeioDia);
        await RespostaAsync(db, amb, await OutroAntigoAsync(db, amb, vendedor), 9, SextaAsOito.UtcDateTime);

        // ---- outra empresa, com lead e venda de ontem: nada dela entra
        var outra = await Semeador.TenantAsync(db, "resumo-numeros-outra");
        var deOutra = await ContatoAsync(db, outra, "Lead da outra", OntemMeioDia, null);
        await NegocioAsync(db, outra, deOutra, StatusNegociacao.Ganha, OntemMeioDia, 7000m, null);

        Assert.True(await amb.Motor.EnviarAsync(amb.Cenario.Id, default));

        var (_, para, r) = Assert.Single(amb.Email.Resumos);
        Assert.Equal(amb.Cenario.Dono.Email, para);
        Assert.Equal(Ontem, r.Dia);
        Assert.Equal(amb.Cenario.Empresa.Nome, r.Empresa);
        Assert.Equal(2, r.LeadsNovos);
        Assert.Equal(1, r.Vendas);
        Assert.Equal(1500m, r.ValorVendido);
        Assert.Equal(1, r.AguardandoResposta);
        Assert.Equal(1, r.LembretesDeHoje);
        Assert.Equal(1, r.AutomaticasEnviadas);
        Assert.Equal(3, r.AutomaticasNaoEnviadas);
        Assert.Equal(
            ["O WhatsApp está desconectado. (2)", "Passou do prazo sem sair. (1)"],
            r.Motivos.Select(m => $"{m.Motivo} ({m.Quantas})"));
        Assert.Equal(2, r.RespostasPesquisa);
        Assert.Equal(1, r.Promotores);
        Assert.Equal(1, r.Detratores);
    }

    // ==================================================================== quem recebe, e quando
    [Fact]
    public async Task VAI_PARA_CADA_DONO_ATIVO_E_SO_PARA_ELES()
    {
        var (db, tx, amb) = await PrepararAsync("donos");
        using var _ = db; using var __ = tx;
        var socio = await UsuarioAsync(db, amb, "socio", PapelUsuario.Dono);
        var saiu = await UsuarioAsync(db, amb, "saiu", PapelUsuario.Dono);
        await db.Usuarios.IgnoreQueryFilters().Where(u => u.Id == saiu)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, StatusUsuario.Inativo));
        await UsuarioAsync(db, amb, "vendedor", PapelUsuario.Vendedor);

        await amb.Motor.EnviarAsync(amb.Cenario.Id, default);

        Assert.Equal(
            [amb.Cenario.Dono.Email, "socio-resumo-donos@exemplo.com"],
            amb.Email.Resumos.Select(x => x.Email));
    }

    /// <summary>Um reinicio da API perto das 8h roda a rodada de novo. O dia ja reservado nao sai outra vez.</summary>
    [Fact]
    public async Task UM_RESUMO_POR_DIA()
    {
        var (db, tx, amb) = await PrepararAsync("uma-vez");
        using var _ = db; using var __ = tx;

        Assert.True(await amb.Motor.EnviarAsync(amb.Cenario.Id, default));
        Assert.False(await amb.Motor.EnviarAsync(amb.Cenario.Id, default));

        Assert.Single(amb.Email.Resumos);
        Assert.Single(await db.ResumosDiarios.IgnoreQueryFilters()
            .Where(x => x.EmpresaId == amb.Cenario.Id && x.Dia == Ontem).ToListAsync());
    }

    /// <summary>Desligado por padrao, e demonstracao nunca: os numeros sao de mentira, e o e-mail iria
    /// para alguem de verdade.</summary>
    [Fact]
    public async Task SO_RECEBE_QUEM_LIGOU_E_NAO_E_DEMONSTRACAO()
    {
        var (db, tx, amb) = await PrepararAsync("quem");
        using var _ = db; using var __ = tx;
        var desligada = await Semeador.TenantAsync(db, "resumo-quem-desligada");
        var demonstracao = await Semeador.TenantAsync(db, "resumo-quem-demo");
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == demonstracao.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.ResumoDiarioAtivo, true)
                .SetProperty(e => e.Demonstracao, true));

        var empresas = await amb.Motor.EmpresasAsync(default);

        Assert.Contains(amb.Cenario.Id, empresas);
        Assert.DoesNotContain(desligada.Id, empresas);
        Assert.DoesNotContain(demonstracao.Id, empresas);
        Assert.False(await amb.Motor.EnviarAsync(desligada.Id, default));
        Assert.False(await amb.Motor.EnviarAsync(demonstracao.Id, default));
        Assert.Empty(amb.Email.Resumos);
    }

    // ==================================================================== reenviar (o dono pede)
    /// <summary>O e-mail das 8h nao chegou: o dono pede de novo, e ele sai na hora. A rodada nao
    /// manda outra vez o mesmo dia.</summary>
    [Fact]
    public async Task REENVIAR_MANDA_DE_NOVO_E_A_RODADA_NAO_REPETE()
    {
        var (db, tx, amb) = await PrepararAsync("reenvia");
        using var _ = db; using var __ = tx;

        Assert.True(await amb.Motor.EnviarAsync(amb.Cenario.Id, default));
        var reenvio = await amb.Motor.ReenviarAsync(amb.Cenario.Id, default);

        Assert.Equal(new ResumoReenviado(Ontem, 1, 1), reenvio);
        Assert.Equal(2, amb.Email.Resumos.Count);
        Assert.False(await amb.Motor.EnviarAsync(amb.Cenario.Id, default));
        Assert.Equal(2, amb.Email.Resumos.Count);
    }

    /// <summary>Pedido ANTES das 8h, ele marca o dia: a rodada nao manda o mesmo resumo de novo.</summary>
    [Fact]
    public async Task REENVIAR_ANTES_DA_RODADA_MARCA_O_DIA()
    {
        var (db, tx, amb) = await PrepararAsync("reenvia-antes");
        using var _ = db; using var __ = tx;

        await amb.Motor.ReenviarAsync(amb.Cenario.Id, default);

        Assert.False(await amb.Motor.EnviarAsync(amb.Cenario.Id, default));
        Assert.Single(amb.Email.Resumos);
    }

    /// <summary>E um pedido explicito: sai mesmo com o resumo diario desligado.</summary>
    [Fact]
    public async Task REENVIAR_NAO_PRECISA_DO_RESUMO_LIGADO()
    {
        var (db, tx, amb) = await PrepararAsync("reenvia-desligado");
        using var _ = db; using var __ = tx;
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ResumoDiarioAtivo, false));

        Assert.Equal(1, (await amb.Motor.ReenviarAsync(amb.Cenario.Id, default)).Enviados);
    }

    /// <summary>Se nenhum e-mail saiu, a resposta e ERRO — dizer "enviado" para o que o servidor
    /// recusou e o pior dos dois mundos.</summary>
    [Fact]
    public async Task SE_NENHUM_E_MAIL_SAI_O_REENVIO_E_ERRO()
    {
        var (db, tx, amb) = await PrepararAsync("reenvia-falha");
        using var _ = db; using var __ = tx;
        amb.Email.ResumoNaoSai = true;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Motor.ReenviarAsync(amb.Cenario.Id, default));

        Assert.Equal(502, erro.StatusHttp);
        Assert.Contains("não saiu", erro.Message);
    }

    [Fact]
    public async Task DEMONSTRACAO_NAO_REENVIA()
    {
        var (db, tx, amb) = await PrepararAsync("reenvia-demo");
        using var _ = db; using var __ = tx;
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Demonstracao, true));

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Motor.ReenviarAsync(amb.Cenario.Id, default));
        Assert.Empty(amb.Email.Resumos);
    }

    /// <summary>O envio real continua sem lancar — mas agora diz se saiu, e o registro fica.</summary>
    [Fact]
    public async Task O_ENVIO_DO_RESUMO_DIZ_SE_SAIU()
    {
        var (db, tx, amb) = await PrepararAsync("envio-real");
        using var _ = db; using var __ = tx;
        var remetente = new RemetenteFalso();
        var notificador = new NotificadorEmail(
            remetente, db, new OpcoesEmail(), new RelogioFalso(SextaAsOito), NullLogger<NotificadorEmail>.Instance);
        var resumo = new ResumoDiario(Ontem, "Loja", 0, 0, 0, 0, 0, 0, 0, [], 0, 0, 0);

        Assert.True(await notificador.ResumoDiarioAsync(amb.Cenario.Id, "dono@loja.com", "Dono", resumo, default));

        remetente.ErroParaLancar = new InvalidOperationException("SMTP fora do ar");
        Assert.False(await notificador.ResumoDiarioAsync(amb.Cenario.Id, "dono@loja.com", "Dono", resumo, default));

        db.ChangeTracker.Clear();
        var registros = await db.EmailsEnviados.IgnoreQueryFilters()
            .Where(e => e.EmpresaId == amb.Cenario.Id && e.Tipo == "resumo_diario")
            .OrderBy(e => e.Id).Select(e => e.Sucesso).ToListAsync();
        Assert.Equal([true, false], registros);
    }

    [Fact]
    public async Task O_DONO_LIGA_E_DESLIGA_NA_CONFIGURACAO()
    {
        var (db, tx, amb) = await PrepararAsync("configuracao");
        using var _ = db; using var __ = tx;
        amb.Humano.EmpresaId = amb.Cenario.Id;
        amb.Humano.UsuarioId = amb.Cenario.Dono.Id;
        amb.Humano.Papel = "dono";
        var configuracao = new ServicoConfiguracao(db, new ColetorAuditoria());

        await configuracao.AtualizarResumoDiarioAsync(false, default);
        Assert.False((await configuracao.ObterAsync(default)).ResumoDiarioAtivo);

        await configuracao.AtualizarResumoDiarioAsync(true, default);
        Assert.True((await configuracao.ObterAsync(default)).ResumoDiarioAtivo);
    }

    // ==================================================================== apoio
    /// <summary>O contexto da PRODUCAO: a pessoa da requisicao quando ha uma; sem ela, a empresa e o
    /// PAPEL que o job assumiu. Ver `ContextoEmpresaHttp`.</summary>
    private sealed class ContextoComFundo(ContextoMutavel humano, ContextoDeFundo fundo) : IContextoEmpresa
    {
        public long EmpresaId => humano.EmpresaId != 0 ? humano.EmpresaId : fundo.EmpresaId;
        public long UsuarioId => humano.UsuarioId != 0 ? humano.UsuarioId : fundo.UsuarioId;
        public string? Papel => humano.EmpresaId != 0 ? humano.Papel : fundo.Papel;
        public IReadOnlyDictionary<Permissao, bool>? ExcecoesDePermissao =>
            humano.EmpresaId != 0 ? humano.ExcecoesDePermissao : null;
        public bool EstaAutenticado => EmpresaId != 0;
    }

    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Humano, MotorResumoDiario Motor, NotificadorEmailFalso Email);

    /// <summary>Uma empresa com o resumo LIGADO, e o motor como a rodada o monta. O contexto comeca
    /// SEM tenant: quem assume a empresa e o motor.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo)
    {
        var humano = new ContextoMutavel();
        var fundo = new ContextoDeFundo();
        var contexto = new ContextoComFundo(humano, fundo);
        var relogio = new RelogioFalso(SextaAsOito);
        var db = banco.NovoContexto(contexto, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"resumo-{sufixo}");
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ResumoDiarioAtivo, true));
        db.ChangeTracker.Clear();

        var servico = new ServicoResumoDiario(
            db, new ServicoRelatorios(db, contexto, relogio), new ServicoDashboard(db, relogio, contexto));
        var email = new NotificadorEmailFalso();
        var motor = new MotorResumoDiario(
            db, fundo, servico, email, relogio, NullLogger<MotorResumoDiario>.Instance);

        return (db, tx, new Ambiente(cenario, humano, motor, email));
    }

    private static async Task<long> UsuarioAsync(NexoraDbContext db, Ambiente amb, string quem, PapelUsuario papel)
    {
        var sufixo = amb.Cenario.Dono.Email.Split('@')[0]["dono-".Length..];
        var usuario = new Usuario
        {
            EmpresaId = amb.Cenario.Id, Nome = $"{quem} {sufixo}", Email = $"{quem}-{sufixo}@exemplo.com",
            Papel = papel, Status = StatusUsuario.Ativo,
            // Ativo sem senha a `ck_usuarios_senha` recusa — e o mesmo hash do `Semeador`.
            SenhaHash = HashSenha.Gerar("senha-de-teste-123")
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return usuario.Id;
    }

    private static int _telefone;

    private static Task<long> ContatoAsync(
        NexoraDbContext db, Ambiente amb, string nome, DateTime criadoEm, long? responsavel, bool anonimizado = false) =>
        ContatoAsync(db, amb.Cenario, nome, criadoEm, responsavel, anonimizado);

    private static async Task<long> ContatoAsync(
        NexoraDbContext db, Cenario c, string nome, DateTime criadoEm, long? responsavel, bool anonimizado = false)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = nome,
            Telefone = $"55849{Interlocked.Increment(ref _telefone) % 100_000_000:D8}",
            ResponsavelId = responsavel, CriadoEm = criadoEm,
            AnonimizadoEm = anonimizado ? criadoEm : null
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato.Id;
    }

    private static Task<long> NegocioAsync(
        NexoraDbContext db, Ambiente amb, long contatoId, StatusNegociacao status, DateTime ganhaEm,
        decimal valor, long? responsavel) =>
        NegocioAsync(db, amb.Cenario, contatoId, status, ganhaEm, valor, responsavel);

    private static async Task<long> NegocioAsync(
        NexoraDbContext db, Cenario c, long contatoId, StatusNegociacao status, DateTime ganhaEm,
        decimal valor, long? responsavel)
    {
        var etapa = c.Etapas[0];
        var negocio = new Negociacao
        {
            EmpresaId = c.Id, ContatoId = contatoId, PipelineId = etapa.PipelineId, EtapaId = etapa.Id,
            Status = status, Valor = valor, ResponsavelId = responsavel, GanhaEm = ganhaEm,
            ConcluidaEm = status == StatusNegociacao.Concluida ? ganhaEm.AddDays(1) : null
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return negocio.Id;
    }

    /// <summary>Outra venda antiga, concluida — so para ter onde pendurar mais uma pesquisa.</summary>
    private static Task<long> OutroAntigoAsync(NexoraDbContext db, Ambiente amb, long vendedor) =>
        NegocioAsync(db, amb, amb.Cenario.Contato.Id, StatusNegociacao.Concluida,
            new DateTime(2026, 7, 2, 15, 0, 0, DateTimeKind.Utc), 300m, vendedor);

    private static async Task<long> ConversaEsperandoAsync(NexoraDbContext db, Ambiente amb, long contatoId, long responsavel)
    {
        var conversa = new Conversa
        {
            EmpresaId = amb.Cenario.Id, ContatoId = contatoId, ConexaoId = amb.Cenario.Conexao.Id,
            ResponsavelId = responsavel, Status = StatusConversa.Aberta,
            UltimaMensagemEm = SextaAsOito.UtcDateTime.AddHours(-2),
            AguardandoDesde = SextaAsOito.UtcDateTime.AddHours(-2)
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return conversa.Id;
    }

    private static async Task LembreteDeHojeAsync(NexoraDbContext db, Ambiente amb, long contatoId, long responsavel)
    {
        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id, ContatoId = contatoId, ResponsavelId = responsavel,
            Origem = OrigemLembrete.Manual, Status = StatusLembrete.Pendente,
            DataAlvo = Ontem.AddDays(1), Titulo = "Ligar para o lead"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task AutomaticaAsync(
        NexoraDbContext db, Ambiente amb, long conversaId, long contatoId, bool enviada = false,
        string? erro = null, bool expirada = false, TipoAutomacao tipo = TipoAutomacao.Lembrete,
        DateTime? quando = null)
    {
        var reservada = quando ?? OntemMeioDia;
        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = amb.Cenario.Id, ConversaId = conversaId, ContatoId = contatoId,
            ConexaoId = amb.Cenario.Conexao.Id, InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = DirecaoMensagem.Saida, Texto = "Oi! Ainda tem interesse?",
            Origem = OrigemMensagem.Automatica, TipoAutomacao = tipo,
            DataDisparo = DateOnly.FromDateTime(reservada), ReservadoEm = reservada,
            EnviadaEm = enviada ? reservada.AddMinutes(1) : null,
            Erro = erro, ExpiradaEm = expirada ? reservada.AddHours(1) : null
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task RespostaAsync(NexoraDbContext db, Ambiente amb, long negociacaoId, short nota, DateTime quando)
    {
        db.PesquisasNps.Add(new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id, NegociacaoId = negociacaoId, ContatoId = amb.Cenario.Contato.Id,
            Status = StatusPesquisaNps.Respondida, DataAgendada = DateOnly.FromDateTime(quando).AddDays(-2),
            DataLimite = DateOnly.FromDateTime(quando).AddDays(5), DataEnvio = quando.AddDays(-1),
            DataResposta = quando, Nota = nota
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
