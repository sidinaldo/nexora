using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Api.Controllers;
using Nexora.Core;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Tests.Unidade;

namespace Nexora.Tests.Integracao;

/// <summary>A CREDENCIAL DE ANÚNCIO, contra Postgres real (INT-4).
///
/// Duas regras aqui protegem coisas que falham em silêncio e do lado de fora:
///
///   • o token nunca sai pela API, e salvar com o campo em branco MANTÉM o que está lá — apagá-lo
///     por descuido faria os eventos pararem sem nenhum sinal na tela;
///   • retirar o consentimento CANCELA a fila. Sem isso, o dado pessoal hasheado ficaria parado
///     numa tabela esperando o dia em que alguém religasse — e sairia sem que ninguém tivesse
///     decidido isso agora.</summary>
[Collection("banco")]
public class ConversoesDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Marco = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    private static SalvarCredencial Conectado => new(
        Identificador: "1234567890123456",
        Token: "EAAGtokenbemlongoparaMascarar",
        CodigoTeste: null,
        Ativo: true,
        EmLead: true,
        EmCompra: true,
        ConsentimentoDeclarado: true);

    // ==================================================================== papel
    [Fact]
    public void SO_QUEM_CONFIGURA_A_EMPRESA_CONECTA_ANUNCIO()
    {
        // Nenhuma permissão nova: a tabela já diz que integração é `ConfigurarEmpresa`. Quem ENTRA,
        // e não como o atributo escreve — ver `PapeisDaRota`.
        Assert.Equal("dono", PapeisDaRota.DaClasse(typeof(ConversoesController)));
    }

    // ==================================================================== o token
    [Fact]
    public async Task O_TOKEN_NUNCA_SAI_PELA_API__SO_O_SUFIXO()
    {
        var (db, tx, amb) = await PrepararAsync("token");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var painel = await amb.Conversoes.ObterAsync(default);

        // ⚠️ Nem uma vez, ao contrário do segredo do webhook: aquele nós geramos e dava para
        // revelar na criação; este o cliente cola do Gerenciador de Eventos.
        Assert.Equal("EAAG…arar", painel.Credencial!.TokenFinal);
        Assert.DoesNotContain("token", painel.Credencial.TokenFinal!);

        // E está de fato guardado por inteiro no banco — é com ele que a Graph API é chamada.
        var guardado = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Equal("EAAGtokenbemlongoparaMascarar", guardado.Token);
    }

    [Fact]
    public async Task SALVAR_COM_O_TOKEN_EM_BRANCO_MANTEM_O_ANTERIOR()
    {
        // ⚠️ É O DEFEITO MAIS CARO QUE ESTA TELA PODE TER. A tela não consegue preencher o campo de
        // volta (o `GET` não devolve o token), então o caso normal é salvar com ele vazio para
        // trocar outra coisa. Apagar aí faria "trocar o Pixel ID" desligar o envio em silêncio.
        var (db, tx, amb) = await PrepararAsync("mantem");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await amb.Conversoes.SalvarAsync(Conectado with { Identificador = "9999999999", Token = null },
            default);
        db.ChangeTracker.Clear();

        var c = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Equal("9999999999", c.Identificador);
        Assert.Equal("EAAGtokenbemlongoparaMascarar", c.Token);

        // String vazia e espaço em branco são a mesma coisa que nulo: é o que sobra de um campo
        // "limpo" na mão.
        await amb.Conversoes.SalvarAsync(Conectado with { Token = "   " }, default);
        db.ChangeTracker.Clear();
        Assert.Equal("EAAGtokenbemlongoparaMascarar",
            (await db.CredenciaisConversao.AsNoTracking().SingleAsync()).Token);
    }

    [Fact]
    public async Task TOKEN_NOVO_SUBSTITUI_E_RELIGA_O_QUE_O_MOTOR_DESLIGOU()
    {
        // O gesto real: a Meta recusou o token, o motor desativou a credencial, e a pessoa volta
        // com um token novo. Sem religar aqui, ela ficaria desativada para sempre.
        var (db, tx, amb) = await PrepararAsync("religa");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await db.CredenciaisConversao.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.DesativadaEm, Marco.UtcDateTime)
            .SetProperty(c => c.DesativadaMotivo, "A Meta recusou o token."));
        db.ChangeTracker.Clear();

        await amb.Conversoes.SalvarAsync(Conectado with { Token = "EAAGoutrotokenbemlongo" }, default);
        db.ChangeTracker.Clear();

        var c = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Equal("EAAGoutrotokenbemlongo", c.Token);
        Assert.Null(c.DesativadaEm);
        Assert.Null(c.DesativadaMotivo);
    }

    // ==================================================================== consentimento
    [Fact]
    public async Task O_CONSENTIMENTO_GRAVA_DATA_E_AUTOR__E_NAO_REESCREVE_A_DATA_A_CADA_SALVAMENTO()
    {
        // ⚠️ Declaração sem data e sem autor não responde a um pedido da ANPD. E reescrever a data
        // a cada salvamento de outro campo faria o registro dizer que a declaração é de hoje —
        // que é justamente a informação que ele existe para guardar.
        var (db, tx, amb) = await PrepararAsync("consent");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var primeiro = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Equal(Marco.UtcDateTime, primeiro.ConsentimentoEm);
        Assert.Equal(amb.Cenario.Dono.Id, primeiro.ConsentimentoPor);

        amb.Relogio.Avancar(TimeSpan.FromDays(10));
        await amb.Conversoes.SalvarAsync(Conectado with { CodigoTeste = "TEST1" }, default);
        db.ChangeTracker.Clear();

        var depois = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Equal(primeiro.ConsentimentoEm, depois.ConsentimentoEm);
        Assert.Equal("TEST1", depois.CodigoTeste);

        // E a tela mostra QUEM declarou, pelo nome.
        Assert.Equal(amb.Cenario.Dono.Nome, (await amb.Conversoes.ObterAsync(default))
            .Credencial!.ConsentimentoPor);
    }

    [Fact]
    public async Task RETIRAR_O_CONSENTIMENTO_ZERA_O_REGISTRO_E_CANCELA_A_FILA()
    {
        // ⚠️ A REGRA DE LGPD DESTE BLOCO. O `payload` de cada conversão pendente guarda SHA-256 de
        // telefone e e-mail. Sem consentimento nenhuma delas vai sair — e o que sobraria é dado
        // pessoal hasheado parado numa tabela, esperando o dia em que alguém religue.
        //
        // `cancelado`, não `DELETE`: o evento fica registrado, o dado sai.
        var (db, tx, amb) = await PrepararAsync("retira");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        db.EventosConversao.Add(PendenteDe(amb.Cenario));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Conversoes.SalvarAsync(Conectado with { ConsentimentoDeclarado = false }, default);
        db.ChangeTracker.Clear();

        var c = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Null(c.ConsentimentoEm);
        Assert.Null(c.ConsentimentoPor);
        Assert.False(c.PodeEnviar(TipoConversao.Lead));

        var evento = await db.EventosConversao.AsNoTracking().SingleAsync();
        Assert.Equal(StatusConversao.Cancelado, evento.Status);
        Assert.Null(evento.ProximaTentativaEm);
        Assert.Contains("consentimento", evento.Erro);
    }

    [Fact]
    public async Task DESLIGAR_TAMBEM_ESVAZIA_A_FILA()
    {
        // Desligar é o gesto de "para agora". Deixar a fila cheia faria o envio recomeçar do zero
        // no religar, com eventos velhos — e alguns deles já fora da janela de 7 dias da Meta.
        var (db, tx, amb) = await PrepararAsync("desliga");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        db.EventosConversao.Add(PendenteDe(amb.Cenario));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Conversoes.SalvarAsync(Conectado with { Ativo = false }, default);
        db.ChangeTracker.Clear();

        Assert.Equal(StatusConversao.Cancelado,
            (await db.EventosConversao.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task REMOVER_A_CREDENCIAL_CANCELA_O_QUE_ESTAVA_NA_FILA()
    {
        var (db, tx, amb) = await PrepararAsync("remove");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        db.EventosConversao.Add(PendenteDe(amb.Cenario));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Conversoes.RemoverAsync(default);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.CredenciaisConversao.AsNoTracking().ToListAsync());
        Assert.Equal(StatusConversao.Cancelado,
            (await db.EventosConversao.AsNoTracking().SingleAsync()).Status);
    }

    // ==================================================================== validação
    [Fact]
    public async Task PIXEL_QUE_NAO_E_NUMERO_E_RECUSADO_AQUI__E_NAO_PELA_META_TRES_DIAS_DEPOIS()
    {
        var (db, tx, amb) = await PrepararAsync("pixel");
        using var _ = db; using var __ = tx;

        // O erro comum: colar a URL do Gerenciador de Eventos inteira.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Conversoes.SalvarAsync(
                Conectado with { Identificador = "https://business.facebook.com/events_manager2/1234" },
                default));

        Assert.Contains("só números", erro.Message);

        await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Conversoes.SalvarAsync(Conectado with { Identificador = "  " }, default));
    }

    // ==================================================================== o número que cobra
    [Fact]
    public async Task O_NUMERO_QUE_COBRA_CONTA_SO_QUEM_VEIO_DE_ANUNCIO_NOS_ULTIMOS_30_DIAS()
    {
        // ⚠️ `identificadores <> '{}'` é a pergunta certa. Contar todo lead do formulário inflaria
        // o número com quem chegou pelo Google orgânico, e a frase "a Meta não ficou sabendo de 40"
        // seria falsa — ela não tem nada a saber sobre a maioria deles.
        var (db, tx, amb) = await PrepararAsync("numero");
        using var _ = db; using var __ = tx;

        var agora = Marco.UtcDateTime;

        db.RastreiosLead.AddRange(
            // Entra: veio de anúncio, e é de ontem.
            RastroDe(amb.Cenario, amb.Cenario.Contato.Id, agora.AddDays(-1),
                RegrasRastreio.Montar((RegrasRastreio.ChaveFbclid, "IwAR-1"))),
            // Fora: sem identificador de clique — chegou pelo formulário, mas não de anúncio.
            RastroDe(amb.Cenario, await OutroContatoAsync(db, amb.Cenario, "5584960001111"),
                agora.AddDays(-2), "{}"),
            // Fora: veio de anúncio, mas há 40 dias.
            RastroDe(amb.Cenario, await OutroContatoAsync(db, amb.Cenario, "5584960002222"),
                agora.AddDays(-40), RegrasRastreio.Montar((RegrasRastreio.ChaveFbclid, "IwAR-2"))));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, (await amb.Conversoes.ObterAsync(default)).LeadsComAnuncio30Dias);
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task A_CREDENCIAL_DE_UMA_EMPRESA_NAO_APARECE_PARA_A_OUTRA()
    {
        // É o token de anúncio DELA. Vazar aqui não é ver dado demais: é dar a outra empresa a
        // credencial com que se escreve na conta de anúncio desta.
        var (db, tx, amb) = await PrepararAsync("iso");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var outra = await Semeador.TenantAsync(db, "conversoes-iso-b");
        amb.Contexto.EmpresaId = outra.Id;
        amb.Contexto.UsuarioId = outra.Dono.Id;

        Assert.Null((await amb.Conversoes.ObterAsync(default)).Credencial);

        // E salvar como a outra empresa cria a DELA, sem tocar na primeira.
        await amb.Conversoes.SalvarAsync(Conectado with { Identificador = "5555555555" }, default);
        db.ChangeTracker.Clear();

        var todas = await db.CredenciaisConversao.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, todas.Count);
        Assert.Equal("1234567890123456", todas[0].Identificador);
        Assert.Equal("5555555555", todas[1].Identificador);
    }

    // ==================================================================== apoio
    private static EventoConversao PendenteDe(Cenario c) => new()
    {
        EmpresaId = c.Id,
        Plataforma = PlataformaConversao.Meta,
        Tipo = TipoConversao.Lead,
        EventoId = Guid.NewGuid(),
        ContatoId = c.Contato.Id,
        Payload = "{\"data\":[]}",
        OcorridoEm = Marco.UtcDateTime,
        ExpiraEm = Marco.UtcDateTime.AddDays(7),
        Status = StatusConversao.Pendente,
        ProximaTentativaEm = Marco.UtcDateTime
    };

    private static RastreioLead RastroDe(Cenario c, long contatoId, DateTime quando, string ids) => new()
    {
        EmpresaId = c.Id,
        ContatoId = contatoId,
        Fonte = FonteRastreio.FormularioSite,
        UtmCampaign = "promo",
        Identificadores = ids,
        OcorridoEm = quando
    };

    private static async Task<long> OutroContatoAsync(NexoraDbContext db, Cenario c, string telefone)
    {
        var contato = new Contato { EmpresaId = c.Id, Nome = "Outro", Telefone = telefone };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        return contato.Id;
    }

    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio,
        IServicoConversoes Conversoes);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Marco);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"conversoes-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(
            cenario, ctx, relogio, new ServicoConversoes(db, ctx, relogio)));
    }
}
