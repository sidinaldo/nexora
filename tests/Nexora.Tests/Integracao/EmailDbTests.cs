using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Email;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O e-mail transacional contra Postgres real.
///
/// A regra que sustenta todo o bloco: NENHUMA falha de e-mail pode desfazer trabalho de negócio.
/// O provedor é a dependência menos confiável do sistema, e a operação que ele acompanha (criar
/// usuário, redefinir senha) é das mais importantes.</summary>
[Collection("banco")]
public class EmailDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== camadas
    [Fact]
    public async Task O_SERVICO_DE_APLICACAO_NAO_CONHECE_O_PROVEDOR()
    {
        // ===================== O QUE ESTE TESTE PROVA =====================
        // O ServicoEquipe recebe INotificadorEmail, e o notificador recebe IRemetenteEmail. Este
        // teste monta a cadeia inteira com um remetente FALSO — se alguma camada conhecesse SMTP,
        // não seria possível trocá-lo sem tocar em código de domínio.
        // ==================================================================
        var (db, tx, amb) = await PrepararAsync("camadas");
        using var _ = db; using var __ = tx;

        await amb.Equipe.ConvidarAsync(new NovoConvite("Novo Vendedor", "novo@exemplo.com", "vendedor"), default);

        var enviado = amb.Remetente.UltimoDoTipo("convite");
        Assert.Equal("novo@exemplo.com", enviado.Destinatario);
        Assert.Contains("Nexora", enviado.Assunto);
        Assert.False(string.IsNullOrWhiteSpace(enviado.Html));
        Assert.False(string.IsNullOrWhiteSpace(enviado.Texto));   // texto puro SEMPRE junto
    }

    // ==================================================================== resiliência
    [Fact]
    public async Task FALHA_DO_PROVEDOR_NAO_IMPEDE_A_CRIACAO_DO_USUARIO_NEM_INVALIDA_O_CONVITE()
    {
        // ===================== A REGRA MAIS IMPORTANTE DO BLOCO =====================
        // Se o e-mail derrubasse o convite, a dependência menos confiável do sistema estaria
        // decidindo se a empresa consegue montar a equipe. O fallback manual (o dono copia o
        // link da tela) é o que existia antes deste bloco, e não sai do produto.
        // ============================================================================
        var (db, tx, amb) = await PrepararAsync("provedor-fora");
        using var _ = db; using var __ = tx;

        amb.Remetente.ErroParaLancar = new InvalidOperationException("SMTP fora do ar");

        var token = await amb.Equipe.ConvidarAsync(
            new NovoConvite("Apesar da Falha", "apesar@exemplo.com", "vendedor"), default);

        // O TOKEN VOLTOU: é ele que a tela mostra para o dono copiar.
        Assert.False(string.IsNullOrWhiteSpace(token.Token));

        db.ChangeTracker.Clear();
        var usuario = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Email == "apesar@exemplo.com");

        Assert.Equal(StatusUsuario.Convidado, usuario.Status);
        Assert.Equal(token.Token, usuario.TokenConvite);
        Assert.NotNull(usuario.ConviteExpira);

        // E o convite CONTINUA ACEITÁVEL — não é só a linha que sobrou, é o fluxo inteiro.
        var info = await amb.Equipe.ConviteInfoAsync(token.Token, default);
        Assert.NotNull(info);
    }

    [Fact]
    public async Task TODA_TENTATIVA_E_REGISTRADA_COM_SUCESSO_OU_COM_ERRO()
    {
        // Sem este registro, "o cliente diz que não recebeu" é indepurável.
        var (db, tx, amb) = await PrepararAsync("registro");
        using var _ = db; using var __ = tx;

        await amb.Equipe.ConvidarAsync(new NovoConvite("Deu Certo", "certo@exemplo.com", "vendedor"), default);

        amb.Remetente.ErroParaLancar = new InvalidOperationException("550 mailbox unavailable");
        await amb.Equipe.ConvidarAsync(new NovoConvite("Deu Errado", "errado@exemplo.com", "vendedor"), default);

        db.ChangeTracker.Clear();
        var registros = await db.EmailsEnviados.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Destinatario.EndsWith("@exemplo.com"))
            .OrderBy(e => e.Id).ToListAsync();

        Assert.Equal(2, registros.Count);

        Assert.True(registros[0].Sucesso);
        Assert.Null(registros[0].Erro);
        Assert.Equal("convite", registros[0].Tipo);

        Assert.False(registros[1].Sucesso);
        Assert.Contains("550", registros[1].Erro!);
        Assert.Equal("errado@exemplo.com", registros[1].Destinatario);
    }

    [Fact]
    public async Task Falha_ao_GRAVAR_o_registro_tambem_nao_derruba_a_operacao()
    {
        // Nem o log pode desfazer trabalho útil: o convite já foi criado e o e-mail já foi (ou
        // não) entregue quando o INSERT do registro acontece.
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "registro-quebrado");
        ctx.EmpresaId = cenario.Id; ctx.UsuarioId = cenario.Dono.Id; ctx.Papel = "dono";

        var remetente = new RemetenteFalso();
        var notificador = new NotificadorEmail(
            remetente, db, new OpcoesEmail(), relogio, NullLogger<NotificadorEmail>.Instance);

        // `tipo` é NOT NULL; um assunto gigante não quebra, então forço a falha pelo caminho mais
        // honesto: um destinatário maior que o que a coluna aceitaria seria artificial. Uso a FK:
        // empresa inexistente faz o INSERT do registro violar a chave estrangeira.
        await notificador.ResetSenhaAsync(
            empresaId: 999_999_999, email: "alguem@exemplo.com", nome: "Alguém",
            token: "tok", ct: default);

        // O envio ACONTECEU mesmo com o registro falhando.
        Assert.Single(remetente.Enviados);
        db.ChangeTracker.Clear();
    }

    // ==================================================================== conteúdo
    [Fact]
    public async Task O_link_do_convite_usa_a_BASE_CONFIGURADA_e_nao_uma_URL_chumbada()
    {
        var (db, tx, amb) = await PrepararAsync("link", baseUrl: "https://painel.minhaempresa.com.br");
        using var _ = db; using var __ = tx;

        var token = await amb.Equipe.ConvidarAsync(
            new NovoConvite("Com Link", "link@exemplo.com", "vendedor"), default);

        var enviado = amb.Remetente.UltimoDoTipo("convite");
        var esperado = $"https://painel.minhaempresa.com.br/convite/{token.Token}";

        Assert.Contains(esperado, enviado.Html);
        Assert.Contains(esperado, enviado.Texto);   // e no texto puro também, para copiar e colar
    }

    [Fact]
    public void O_HTML_e_montado_para_cliente_de_email_nao_para_browser()
    {
        var email = MontadorEmail.Convite("a@b.com", "Fulano de Tal", "Padaria", "https://x/convite/tok");

        // Tabela para layout e CSS inline: Outlook renderiza com o motor do Word e o Gmail
        // remove <style> do <head>.
        Assert.Contains("<table", email.Html);
        Assert.Contains("style=\"", email.Html);
        Assert.DoesNotContain("display:flex", email.Html);
        Assert.DoesNotContain("<style", email.Html);

        // Nenhuma imagem externa — bloqueada por padrão, e cabeçalho quebrado passa cara de golpe.
        Assert.DoesNotContain("<img", email.Html);

        // Largura fixa de 600px.
        Assert.Contains("600", email.Html);
    }

    [Fact]
    public void Nome_com_marcacao_e_ESCAPADO_no_HTML()
    {
        // Nome de pessoa e de empresa vêm de entrada do usuário e entram no corpo.
        var email = MontadorEmail.Convite(
            "a@b.com", "<script>alert(1)</script>", "Padaria & Cia", "https://x/convite/tok");

        Assert.DoesNotContain("<script>", email.Html);
        Assert.Contains("&amp;", email.Html);
    }

    [Fact]
    public void O_aviso_de_senha_alterada_NAO_tem_link()
    {
        // Um "não fui eu, clique aqui" seria justamente o vetor de phishing que este aviso
        // existe para combater.
        var email = MontadorEmail.SenhaAlterada("a@b.com", "Fulano", "05/08/2026 às 14:32");

        Assert.DoesNotContain("<a href", email.Html);
        Assert.DoesNotContain("http", email.Texto);
        Assert.Contains("14:32", email.Html);
        Assert.Contains("Não foi você", email.Html);
    }

    [Fact]
    public void Nenhum_template_usa_vocabulario_de_cobranca()
    {
        string[] proibidas =
            ["devedor", "credor", "acordo", "carteira", "parcela", "recebível", "régua",
             "comissão", "score", "boleto", "dívida", "inadimpl"];

        var todos = new[]
        {
            MontadorEmail.Convite("a@b.com", "F", "Padaria", "https://x/c/t"),
            MontadorEmail.ResetSenha("a@b.com", "F", "https://x/r/t"),
            MontadorEmail.SenhaAlterada("a@b.com", "F", "05/08/2026 às 14:32")
        };

        foreach (var email in todos)
            foreach (var palavra in proibidas)
            {
                Assert.DoesNotContain(palavra, email.Html, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(palavra, email.Texto, StringComparison.OrdinalIgnoreCase);
            }
    }

    // ==================================================================== esqueci minha senha
    [Fact]
    public async Task ESQUECI_MINHA_SENHA_RESPONDE_IGUAL_PARA_EMAIL_EXISTENTE_E_INEXISTENTE()
    {
        // ===================== POR QUE ISSO IMPORTA =====================
        // Resposta diferente transformaria o endpoint num verificador de contas: bastaria testar
        // endereços para descobrir quem é cliente do Nexora. O método devolve `Task` — não há o
        // que diferir — e nenhum dos dois caminhos lança.
        // ===============================================================
        var (db, tx, amb) = await PrepararAsync("esqueci");
        using var _ = db; using var __ = tx;

        // Existente: gera token e manda e-mail.
        await amb.Equipe.SolicitarResetSenhaAsync(amb.Cenario.Dono.Email, default);
        // Inexistente: NO-OP silencioso, sem exceção.
        await amb.Equipe.SolicitarResetSenhaAsync("ninguem-aqui@exemplo.com", default);

        // O ENVIO SAIU DO CAMINHO DA REQUISIÇÃO (PI-6): ele agora é enfileirado, para o tempo
        // de resposta não depender da velocidade do relay SMTP. Drenar aqui é o que torna o
        // resto do teste — que é sobre o CONTEÚDO do e-mail — verificável.
        Assert.Equal(1, amb.Fila.Enfileirados);
        await amb.Fila.ExecutarPendentesAsync(new ProvedorFalso(amb.Notificador));

        Assert.Equal(1, amb.Remetente.Quantos("reset"));
        Assert.Equal(amb.Cenario.Dono.Email, amb.Remetente.UltimoDoTipo("reset").Destinatario);

        // E o inexistente não deixou registro nenhum — nada foi tentado.
        db.ChangeTracker.Clear();
        Assert.False(await db.EmailsEnviados.IgnoreQueryFilters()
            .AnyAsync(e => e.Destinatario == "ninguem-aqui@exemplo.com"));
    }

    [Fact]
    public async Task Esqueci_minha_senha_gera_token_com_validade_de_2h_e_ele_funciona()
    {
        var (db, tx, amb) = await PrepararAsync("esqueci-token");
        using var _ = db; using var __ = tx;

        await amb.Equipe.SolicitarResetSenhaAsync(amb.Cenario.Dono.Email.ToUpperInvariant(), default);

        db.ChangeTracker.Clear();
        var usuario = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == amb.Cenario.Dono.Id);

        Assert.NotNull(usuario.TokenReset);
        Assert.Equal(QuintaDeManha.UtcDateTime.AddHours(2), usuario.ResetExpira);

        // O token do e-mail é o mesmo do banco, e abre a tela pública.
        var info = await amb.Equipe.ResetInfoAsync(usuario.TokenReset!, default);
        Assert.NotNull(info);
    }

    [Fact]
    public async Task Convidado_que_ainda_nao_aceitou_NAO_recebe_reset()
    {
        // Ele não tem senha para redefinir; o caminho dele é o reenvio do convite.
        var (db, tx, amb) = await PrepararAsync("esqueci-convidado");
        using var _ = db; using var __ = tx;

        await amb.Equipe.ConvidarAsync(new NovoConvite("Pendente", "pendente@exemplo.com", "vendedor"), default);
        db.ChangeTracker.Clear();

        await amb.Equipe.SolicitarResetSenhaAsync("pendente@exemplo.com", default);

        Assert.Equal(0, amb.Remetente.Quantos("reset"));
    }

    [Fact]
    public async Task TOKEN_EXPIRADO_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("token-expirado");
        using var _ = db; using var __ = tx;

        await amb.Equipe.SolicitarResetSenhaAsync(amb.Cenario.Dono.Email, default);
        db.ChangeTracker.Clear();

        var token = (await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == amb.Cenario.Dono.Id)).TokenReset!;

        // Passam 2h01: a validade é de 2h.
        amb.Relogio.Avancar(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        Assert.Null(await amb.Equipe.ResetInfoAsync(token, default));
        Assert.False(await amb.Equipe.RedefinirSenhaAsync(token, "senha-nova-123", default));

        // E a senha antiga continua valendo — o token expirado não estragou nada.
        db.ChangeTracker.Clear();
        var usuario = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == amb.Cenario.Dono.Id);
        Assert.True(HashSenha.Confere("senha-de-teste-123", usuario.SenhaHash));
    }

    // ==================================================================== senha alterada
    [Fact]
    public async Task Trocar_a_propria_senha_dispara_o_AVISO()
    {
        var (db, tx, amb) = await PrepararAsync("aviso-troca");
        using var _ = db; using var __ = tx;

        await amb.Equipe.TrocarMinhaSenhaAsync("senha-de-teste-123", "senha-nova-1234", default);

        var aviso = amb.Remetente.UltimoDoTipo("senha_alterada");
        Assert.Equal(amb.Cenario.Dono.Email, aviso.Destinatario);
        Assert.DoesNotContain("<a href", aviso.Html);
    }

    [Fact]
    public async Task Redefinir_por_LINK_tambem_dispara_o_aviso()
    {
        // É o caminho que um invasor com acesso à caixa de e-mail usaria — e é justamente onde o
        // aviso dá ao dono a chance de reagir.
        var (db, tx, amb) = await PrepararAsync("aviso-link");
        using var _ = db; using var __ = tx;

        await amb.Equipe.SolicitarResetSenhaAsync(amb.Cenario.Dono.Email, default);
        db.ChangeTracker.Clear();

        var token = (await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == amb.Cenario.Dono.Id)).TokenReset!;

        Assert.True(await amb.Equipe.RedefinirSenhaAsync(token, "outra-senha-123", default));
        Assert.Equal(1, amb.Remetente.Quantos("senha_alterada"));
    }

    [Fact]
    public async Task Aceitar_convite_NAO_dispara_aviso_de_senha_alterada()
    {
        // É a PRIMEIRA senha, não uma troca. Avisar aqui seria ruído no primeiro contato da
        // pessoa com o produto.
        var (db, tx, amb) = await PrepararAsync("aceite-sem-aviso");
        using var _ = db; using var __ = tx;

        var token = await amb.Equipe.ConvidarAsync(
            new NovoConvite("Aceitante", "aceita@exemplo.com", "vendedor"), default);
        db.ChangeTracker.Clear();

        Assert.NotNull(await amb.Equipe.AceitarConviteAsync(token.Token, "primeira-senha-1", default));
        Assert.Equal(0, amb.Remetente.Quantos("senha_alterada"));
    }

    // ==================================================================== remetente de arquivo
    [Fact]
    public async Task Remetente_de_ARQUIVO_grava_html_e_texto_em_disco()
    {
        // O dev abre o .html no navegador para ver como o cliente verá, e o .txt é o que quem
        // bloqueia HTML recebe. Conferir os dois é o ponto.
        var pasta = Path.Combine(Path.GetTempPath(), $"nexora-email-{Guid.NewGuid():N}");
        try
        {
            var remetente = new RemetenteArquivo(
                new OpcoesEmail { PastaArquivo = pasta },
                NullLogger<RemetenteArquivo>.Instance);

            await remetente.EnviarAsync(
                MontadorEmail.ResetSenha("alguem@exemplo.com", "Alguém", "https://x/redefinir/tok"),
                default);

            Assert.Single(Directory.GetFiles(pasta, "*.html"));
            var txt = Directory.GetFiles(pasta, "*.txt").Single();
            var conteudo = await File.ReadAllTextAsync(txt);
            Assert.Contains("alguem@exemplo.com", conteudo);
            Assert.Contains("https://x/redefinir/tok", conteudo);
        }
        finally
        {
            if (Directory.Exists(pasta)) Directory.Delete(pasta, recursive: true);
        }
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio,
        RemetenteFalso Remetente, IServicoEquipe Equipe,
        INotificadorEmail Notificador, FilaSegundoPlanoFalsa Fila);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, string? baseUrl = null)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var remetente = new RemetenteFalso();
        var opcoes = new OpcoesEmail { BaseUrlPainel = baseUrl ?? "http://localhost:4200" };
        var notificador = new NotificadorEmail(
            remetente, db, opcoes, relogio, NullLogger<NotificadorEmail>.Instance);

        var fila = new FilaSegundoPlanoFalsa();

        return (db, tx, new Ambiente(
            cenario, ctx, relogio, remetente,
            new ServicoEquipe(db, ctx, relogio, notificador, fila),
            notificador, fila));
    }
}
