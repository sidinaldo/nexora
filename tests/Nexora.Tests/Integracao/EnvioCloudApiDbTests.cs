using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Infra.Whatsapp;

namespace Nexora.Tests.Integracao;

/// <summary>O ENVIO PELA API OFICIAL (INT-XX), de ponta a ponta: o vendedor responde na caixa, o
/// `EnviadorMensagem` posta, e o `RoteadorWhatsApp` de verdade escolhe a Cloud API pela conexao da
/// conversa. So a Graph API e duble.
///
/// O que importa provar: com a janela de 24h fechada nada e gravado nem enviado, e o 409 diz por
/// que (`janela_fechada`); com ela aberta, a mensagem sai para o `wa_id` que a Meta reconhece.</summary>
[Collection("banco")]
public class EnvioCloudApiDbTests(BancoTeste banco)
{
    private sealed record Ambiente(
        Cenario Cenario, IServicoConversas Conversas, ClienteCloudApiFalso Meta);

    /// <summary>Uma conversa num numero da API oficial, com a ultima mensagem do cliente ha
    /// `horasDesdeOCliente` horas.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, double horasDesdeOCliente)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"envio-cloud-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cifra = CifraDeTeste.Nova();
        var oficial = new Conexao
        {
            EmpresaId = cenario.Id, Nome = "Oficial", InstanceName = $"cloud-envio-{sufixo}",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = "1090000000201", WabaId = "2090000000201",
            AccessTokenCifrado = cifra.Cifrar("EAAG-tok", FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar("seg", FinalidadeSegredo.AppSecret),
            Status = StatusConexao.Conectado
        };
        db.Conexoes.Add(oficial);
        await db.SaveChangesAsync();

        var ultima = DateTime.UtcNow.AddHours(-horasDesdeOCliente);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == cenario.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConexaoId, oficial.Id)
                .SetProperty(c => c.UltimaEntradaEm, ultima));
        db.ChangeTracker.Clear();

        // A Evolution nunca e chamada aqui: a conversa e da Cloud API. O cliente HTTP dela nao tem
        // para onde ir, e se o roteador errasse o canal o teste quebraria na rede.
        var evolution = new ClienteEvolution(
            new HttpClient { BaseAddress = new Uri("http://evolution-nao-deve-ser-chamada.invalid/") },
            NullLogger<ClienteEvolution>.Instance);
        var meta = new ClienteCloudApiFalso();
        var roteador = new RoteadorWhatsApp(db, evolution, meta, cifra);

        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, TimeProvider.System), roteador,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero }, TimeProvider.System,
            NullLogger<EnviadorMensagem>.Instance);
        var conversas = new ServicoConversas(
            db, ctx, enviador, new ArmazenamentoFalso(), new ColetorAuditoria(), TimeProvider.System);

        return (db, tx, new Ambiente(cenario, conversas, meta));
    }

    private static Task<int> SaidasAsync(NexoraDbContext db, long conversaId) =>
        db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.ConversaId == conversaId && m.Direcao == DirecaoMensagem.Saida);

    [Fact]
    public async Task JANELA_FECHADA_RECUSA_COM_O_CODIGO_E_NADA_E_GRAVADO()
    {
        var (db, tx, amb) = await PrepararAsync("fechada", horasDesdeOCliente: 25);
        using var _ = db; using var __ = tx;
        var antes = await SaidasAsync(db, amb.Cenario.Conversa.Id);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.ResponderAsync(amb.Cenario.Conversa.Id, "Oi, tudo bem?", default));

        Assert.True(erro.Conflito);
        Assert.Equal("janela_fechada", erro.Codigo);
        Assert.Contains("template", erro.Message);
        Assert.Empty(amb.Meta.Enviadas);
        Assert.Equal(antes, await SaidasAsync(db, amb.Cenario.Conversa.Id));
    }

    /// <summary>Anexo tambem e texto livre para a Meta: com a janela fechada, nem a foto sai.</summary>
    [Fact]
    public async Task JANELA_FECHADA_TAMBEM_BARRA_O_ANEXO()
    {
        var (db, tx, amb) = await PrepararAsync("fechada-anexo", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;

        byte[] png = [0x89, .. "PNG"u8, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[32]];
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Conversas.EnviarMidiaAsync(
            amb.Cenario.Conversa.Id, new ArquivoParaEnvio(png, "foto.png", "image/png"), null, default));

        Assert.Equal("janela_fechada", erro.Codigo);
        Assert.Empty(amb.Meta.Enviadas);
    }

    /// <summary>⚠️ O NONO DIGITO: a resposta vai para o `wa_id` que a Meta mandou, e nao para o
    /// telefone do cadastro — os dois podem diferir, e so o primeiro a Meta reconhece.</summary>
    [Fact]
    public async Task JANELA_ABERTA_ENVIA_PELA_META_PARA_O_WA_ID()
    {
        var (db, tx, amb) = await PrepararAsync("aberta", horasDesdeOCliente: 1);
        using var _ = db; using var __ = tx;
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Cenario.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.WaId, "558488887777"));
        db.ChangeTracker.Clear();

        var resposta = await amb.Conversas.ResponderAsync(amb.Cenario.Conversa.Id, "Oi, tudo bem?", default);

        Assert.True(resposta.Enviada);
        var (para, tipo, texto) = Assert.Single(amb.Meta.Enviadas);
        Assert.Equal("558488887777", para);
        Assert.Equal("text", tipo);
        Assert.Equal("Oi, tudo bem?", texto);

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().SingleAsync(m => m.Id == resposta.MensagemId);
        Assert.Equal("wamid.TESTE1", linha.WaMessageId);
        Assert.NotNull(linha.EnviadaEm);
    }

    [Fact]
    public async Task SEM_WA_ID_VAI_PARA_O_TELEFONE_DO_CADASTRO()
    {
        var (db, tx, amb) = await PrepararAsync("sem-wa-id", horasDesdeOCliente: 2);
        using var _ = db; using var __ = tx;

        await amb.Conversas.ResponderAsync(amb.Cenario.Conversa.Id, "Oi", default);

        Assert.Equal(amb.Cenario.Contato.Telefone, Assert.Single(amb.Meta.Enviadas).Para);
    }

    /// <summary>A Meta so aceita WEBP como figurinha: a tela diz antes, com o que fazer.</summary>
    [Fact]
    public async Task WEBP_PELA_API_OFICIAL_E_RECUSADO_ANTES()
    {
        var (db, tx, amb) = await PrepararAsync("webp", horasDesdeOCliente: 1);
        using var _ = db; using var __ = tx;

        byte[] webp = [.. "RIFF"u8, 0x20, 0x00, 0x00, 0x00, .. "WEBP"u8, .. new byte[64]];
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Conversas.EnviarMidiaAsync(
            amb.Cenario.Conversa.Id, new ArquivoParaEnvio(webp, "foto.webp", "image/webp"), null, default));

        Assert.Contains("JPG ou PNG", erro.Message);
        Assert.Empty(amb.Meta.Enviadas);
    }

    // ==================================================================== template (etapa 7)
    private const string CorpoModelo = "Oi {{nome}}, aqui é o {{vendedor}} da {{empresa}}. Podemos continuar?";

    /// <summary>Um template na conexao da conversa (ou na dada), no status pedido.</summary>
    private static async Task<long> ModeloAsync(
        NexoraDbContext db, Ambiente amb, StatusModelo status, long? conexaoId = null, string nome = "retomada")
    {
        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == amb.Cenario.Conversa.Id);
        var modelo = new ModeloMensagem
        {
            EmpresaId = amb.Cenario.Id, ConexaoId = conexaoId ?? conversa.ConexaoId, WabaId = "2090000000201",
            Nome = nome, Categoria = CategoriaModelo.Utility, Idioma = "pt_BR", Corpo = CorpoModelo,
            Variaveis = ["nome", "vendedor", "empresa"], Status = status, IdMeta = "594425479261599"
        };
        db.ModelosMensagem.Add(modelo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return modelo.Id;
    }

    /// <summary>===================== A SAIDA QUANDO A JANELA FECHOU =====================
    ///
    /// Janela fechada, o vendedor escolhe um template aprovado: ele sai COMO TEMPLATE (nada de texto
    /// livre), com os valores na ordem, e a thread guarda o texto que o cliente leu.
    /// ===============================================================================</summary>
    [Fact]
    public async Task JANELA_FECHADA_O_TEMPLATE_APROVADO_SAI_PREENCHIDO()
    {
        var (db, tx, amb) = await PrepararAsync("modelo", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado);
        const string Lido = "Oi Contato, aqui é o Dono da Empresa envio-cloud-modelo. Podemos continuar?";

        var oferecido = Assert.Single(await amb.Conversas.ModelosAsync(amb.Cenario.Conversa.Id, default));
        Assert.Equal(id, oferecido.Id);
        Assert.Equal(Lido, oferecido.Previa);

        var resposta = await amb.Conversas.EnviarModeloAsync(amb.Cenario.Conversa.Id, id, default);

        Assert.True(resposta.Enviada);
        Assert.Empty(amb.Meta.Enviadas);
        var (para, nome, idioma, parametros) = Assert.Single(amb.Meta.ModelosEnviados);
        Assert.Equal(amb.Cenario.Contato.Telefone, para);
        Assert.Equal("retomada", nome);
        Assert.Equal("pt_BR", idioma);
        Assert.Equal(["Contato", "Dono", "Empresa envio-cloud-modelo"], parametros);

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().SingleAsync(m => m.Id == resposta.MensagemId);
        Assert.Equal(Lido, linha.Texto);
        Assert.Equal(id, linha.ModeloId);
        Assert.Equal("wamid.MODELO1", linha.WaMessageId);
    }

    [Fact]
    public async Task SO_O_TEMPLATE_APROVADO_SAI()
    {
        var (db, tx, amb) = await PrepararAsync("modelo-revisao", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Enviado);
        var antes = await SaidasAsync(db, amb.Cenario.Conversa.Id);

        Assert.Empty(await amb.Conversas.ModelosAsync(amb.Cenario.Conversa.Id, default));
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.EnviarModeloAsync(amb.Cenario.Conversa.Id, id, default));

        Assert.True(erro.Conflito);
        Assert.Empty(amb.Meta.ModelosEnviados);
        Assert.Equal(antes, await SaidasAsync(db, amb.Cenario.Conversa.Id));
    }

    /// <summary>O template sai pelo token e pelas conversas da conexao dele. O de outro numero nao e
    /// oferecido, e pedido pelo id nao e achado.</summary>
    [Fact]
    public async Task TEMPLATE_DE_OUTRO_NUMERO_NAO_SAI_POR_ESTA_CONVERSA()
    {
        var (db, tx, amb) = await PrepararAsync("modelo-outro-numero", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var cifra = CifraDeTeste.Nova();
        var outro = new Conexao
        {
            EmpresaId = amb.Cenario.Id, Nome = "Outro oficial", InstanceName = "cloud-envio-outro-numero",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = "1090000000202", WabaId = "2090000000202",
            AccessTokenCifrado = cifra.Cifrar("EAAG-outro", FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar("seg", FinalidadeSegredo.AppSecret),
            Status = StatusConexao.Conectado
        };
        db.Conexoes.Add(outro);
        await db.SaveChangesAsync();
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado, outro.Id, "do_outro");

        Assert.Empty(await amb.Conversas.ModelosAsync(amb.Cenario.Conversa.Id, default));
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.EnviarModeloAsync(amb.Cenario.Conversa.Id, id, default));

        Assert.Equal(404, erro.StatusHttp);
        Assert.Empty(amb.Meta.ModelosEnviados);
    }

    /// <summary>Na conexao por QR code nao ha template: texto livre sai a qualquer hora.</summary>
    [Fact]
    public async Task NA_CONEXAO_POR_QR_CODE_NAO_HA_TEMPLATE()
    {
        var (db, tx, amb) = await PrepararAsync("modelo-qr", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Cenario.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConexaoId, amb.Cenario.Conexao.Id));
        db.ChangeTracker.Clear();

        Assert.Empty(await amb.Conversas.ModelosAsync(amb.Cenario.Conversa.Id, default));
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.EnviarModeloAsync(amb.Cenario.Conversa.Id, id, default));

        Assert.Contains("API oficial", erro.Message);
    }

    /// <summary>⚠️ O REENVIO DE UM TEMPLATE SAI COMO TEMPLATE. Reenviado como o texto da linha, ele
    /// seria texto livre — barrado pela janela aqui, ou recusado pela Meta (131047) la.</summary>
    [Fact]
    public async Task O_TEMPLATE_QUE_FALHOU_E_REENVIADO_COMO_TEMPLATE_COM_A_JANELA_FECHADA()
    {
        var (db, tx, amb) = await PrepararAsync("modelo-reenvio", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado);
        amb.Meta.FalhaNoEnvioDeModelo = "A Meta limitou o envio deste número agora.";

        var primeira = await amb.Conversas.EnviarModeloAsync(amb.Cenario.Conversa.Id, id, default);
        Assert.False(primeira.Enviada);

        amb.Meta.FalhaNoEnvioDeModelo = null;
        db.ChangeTracker.Clear();
        var reenvio = await amb.Conversas.ReenviarAsync(primeira.MensagemId, default);

        Assert.True(reenvio.Enviada);
        Assert.Equal(2, amb.Meta.ModelosEnviados.Count);
        Assert.Empty(amb.Meta.Enviadas);
        // A MESMA linha: o reenvio nao cria outra.
        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.ModeloId == id));
    }
}
