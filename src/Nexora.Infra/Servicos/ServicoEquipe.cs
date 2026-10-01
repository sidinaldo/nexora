using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A equipe da empresa, e os fluxos de senha.
///
/// Portado do ServicoEmpresa do Recupera. Ficou de fora tudo que era de cobranca: comissao do
/// atendente e o historico de troca dela.
///
/// Convite e reset usam colunas SEPARADAS (token_convite/token_reset), diferente do Recupera,
/// que reusa as mesmas para os dois — la, um convidado que pede reset antes de aceitar sobrescreve
/// o proprio convite.</summary>
public class ServicoEquipe(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio,
    INotificadorEmail email,
    IFilaSegundoPlano fila) : IServicoEquipe
{
    private const int TamanhoMinimoSenha = 8;
    private static readonly TimeSpan ValidadeConvite = TimeSpan.FromDays(7);
    private static readonly TimeSpan ValidadeReset = TimeSpan.FromHours(2);

    public async Task<IReadOnlyList<UsuarioEquipeDto>> ListarAsync(CancellationToken ct) =>
        await db.Usuarios.AsNoTracking()
            .OrderBy(u => u.Nome)
            .Select(u => new UsuarioEquipeDto(
                u.Id, u.Nome, u.Email,
                u.Papel.ToString().ToLower(), u.Status.ToString().ToLower(), u.UltimoAcessoEm))
            .ToListAsync(ct);

    public async Task<TokenGerado> ConvidarAsync(NovoConvite novo, CancellationToken ct)
    {
        var nome = (novo.Nome ?? "").Trim();
        var email = (novo.Email ?? "").Trim();
        if (nome.Length == 0 || email.Length == 0)
            throw new RegraDeNegocioException("Informe nome e e-mail.");

        // E-mail unico GLOBALMENTE: o login busca por ele sem tenant no contexto. Precisa de
        // IgnoreQueryFilters, senao a checagem so olharia dentro da propria empresa e o INSERT
        // estouraria no indice unico com erro ilegivel.
        if (await db.Usuarios.IgnoreQueryFilters().AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct))
            throw new RegraDeNegocioException("Já existe usuário com este e-mail.", conflito: true);

        await ExigirVagaLivreAsync(ct);

        var usuario = new Usuario
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Email = email,
            SenhaHash = null,                 // define no aceite (ck_usuarios_senha permite)
            Papel = ParsePapel(novo.Papel),
            Status = StatusUsuario.Convidado,
            TokenConvite = GerarToken(),
            ConviteExpira = relogio.GetUtcNow().UtcDateTime.Add(ValidadeConvite)
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync(ct);

        // FORA da criação: o SaveChanges já passou, então uma falha de e-mail não desfaz o
        // convite. O token volta para a tela de qualquer jeito, e o dono pode mandar por fora.
        await EnviarConviteAsync(usuario, ct);

        return new TokenGerado(usuario.Id, usuario.TokenConvite!);
    }

    public async Task<TokenGerado> ReenviarConviteAsync(long usuarioId, CancellationToken ct)
    {
        var usuario = await MeuUsuarioAsync(usuarioId, ct);
        if (usuario.Status != StatusUsuario.Convidado)
            throw new RegraDeNegocioException("Só convites pendentes podem ser reenviados.");

        usuario.TokenConvite = GerarToken();
        usuario.ConviteExpira = relogio.GetUtcNow().UtcDateTime.Add(ValidadeConvite);
        await db.SaveChangesAsync(ct);

        await EnviarConviteAsync(usuario, ct);
        return new TokenGerado(usuario.Id, usuario.TokenConvite!);
    }

    public async Task<TokenGerado> GerarResetSenhaAsync(long usuarioId, CancellationToken ct)
    {
        var usuario = await MeuUsuarioAsync(usuarioId, ct);
        if (usuario.Status != StatusUsuario.Ativo)
            throw new RegraDeNegocioException(
                "Só usuários ativos redefinem senha. Convite pendente: reenvie o convite.");

        usuario.TokenReset = GerarToken();
        usuario.ResetExpira = relogio.GetUtcNow().UtcDateTime.Add(ValidadeReset);
        await db.SaveChangesAsync(ct);

        await email.ResetSenhaAsync(
            usuario.EmpresaId, usuario.Email, usuario.Nome, usuario.TokenReset!, ct);

        return new TokenGerado(usuario.Id, usuario.TokenReset!);
    }

    /// <summary>=========== "ESQUECI MINHA SENHA", AUTO-SERVIÇO ===========
    ///
    /// Roda SEM tenant: quem esqueceu a senha não tem sessão. Daí o IgnoreQueryFilters — o
    /// e-mail é a chave, e ela é global.
    ///
    /// E-MAIL INEXISTENTE É NO-OP SILENCIOSO. Nada é lançado, nada é devolvido, e o controller
    /// responde igual nos dois casos. Qualquer diferença — corpo, status ou exceção — faria deste
    /// endpoint um verificador de contas: bastaria testar endereços para descobrir quem é cliente
    /// do Nexora.
    ///
    /// Só usuário ATIVO recebe: quem ainda não aceitou o convite não tem senha para redefinir, e
    /// o caminho dele é o reenvio do convite.
    /// ==========================================================</summary>
    /// <summary>PISO de tempo do "esqueci minha senha". Toda chamada leva PELO MENOS isto,
    /// exista a conta ou não.
    ///
    /// ===================== POR QUE UM PISO, E NÃO TRABALHO EQUIVALENTE =====================
    /// A saída óbvia seria copiar o login: gastar um PBKDF2 contra o hash descartável no caminho
    /// sem conta. No login funciona porque o caminho COM conta também faz um PBKDF2 — os dois
    /// custam o mesmo.
    ///
    /// Aqui não: o caminho com conta gera um token e grava (~2ms), e um PBKDF2 de 100k iterações
    /// custa ~50ms. Equalizar assim não fecharia a janela — INVERTERIA a assimetria, e o e-mail
    /// inexistente passaria a ser o lento. Continuaria dando para enumerar contas, ao contrário.
    ///
    /// O piso é indiferente a qual lado é mais caro: enquanto os dois couberem embaixo dele, o
    /// tempo de resposta não carrega informação nenhuma.
    ///
    /// O ENVIO SAIU DAQUI. Ele ia dentro da chamada e passava do piso num relay lento, reabrindo
    /// a assimetria que o piso fechou — o caminho COM conta era o único que pagava o SMTP. Agora
    /// vai para a fila de segundo plano e a requisição volta sempre no mesmo tempo.
    /// ======================================================================================</summary>
    public static readonly TimeSpan PisoDeTempoReset = TimeSpan.FromMilliseconds(250);

    public async Task SolicitarResetSenhaAsync(string endereco, CancellationToken ct)
    {
        var relogioDeParede = Stopwatch.StartNew();
        try
        {
            var alvo = (endereco ?? "").Trim().ToLowerInvariant();
            if (alvo.Length == 0) return;

            var usuario = await db.Usuarios.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == alvo && u.Status == StatusUsuario.Ativo, ct);

            // E-mail inexistente é no-op — e sai pelo `finally`, que paga o piso igual.
            if (usuario is null) return;

            usuario.TokenReset = GerarToken();
            usuario.ResetExpira = relogio.GetUtcNow().UtcDateTime.Add(ValidadeReset);
            await db.SaveChangesAsync(ct);

            // ===== O ENVIO SAI DO CAMINHO DA REQUISIÇÃO =====
            // O token JÁ ESTÁ GRAVADO quando isto executa — a mesma disciplina do outbox de
            // mensagens: grava primeiro, dispara depois. Se o envio falhar, o token continua
            // válido e o link visível na tela (para quem tem a chave) segue funcionando.
            //
            // Os valores são copiados para variáveis locais: a entidade `usuario` pertence ao
            // DbContext desta requisição, que já terá sido descartado quando o trabalho rodar.
            var empresaId = usuario.EmpresaId;
            var endereçoDele = usuario.Email;
            var nome = usuario.Nome;
            var token = usuario.TokenReset!;

            fila.Enfileirar(async (sp, ctFundo) =>
            {
                var notificador = (INotificadorEmail)sp.GetService(typeof(INotificadorEmail))!;
                await notificador.ResetSenhaAsync(empresaId, endereçoDele, nome, token, ctFundo);
            });
        }
        finally
        {
            // No `finally` de propósito: uma exceção no meio (banco fora, relay recusando) sairia
            // rápido e denunciaria pelo tempo tanto quanto o caminho feliz.
            //
            // `Stopwatch` e não o TimeProvider injetado: o que interessa aqui é tempo de PAREDE,
            // o mesmo que o atacante cronometra. Um relógio falso de teste zeraria a proteção.
            var restante = PisoDeTempoReset - relogioDeParede.Elapsed;
            if (restante > TimeSpan.Zero) await Task.Delay(restante, CancellationToken.None);
        }
    }

    private async Task EnviarConviteAsync(Usuario usuario, CancellationToken ct)
    {
        var empresaNome = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == usuario.EmpresaId)
            .Select(e => e.Nome)
            .FirstOrDefaultAsync(ct) ?? "sua empresa";

        await email.ConviteAsync(
            usuario.EmpresaId, usuario.Email, usuario.Nome, empresaNome, usuario.TokenConvite!, ct);
    }

    public async Task AtualizarAsync(long usuarioId, EditarUsuario dados, CancellationToken ct)
    {
        var nome = (dados.Nome ?? "").Trim();
        if (nome.Length == 0) throw new RegraDeNegocioException("Informe o nome.");

        var papel = ParsePapel(dados.Papel);
        var status = ParseStatusEdicao(dados.Status);
        var usuario = await MeuUsuarioAsync(usuarioId, ct);

        if (status == StatusUsuario.Ativo && usuario.SenhaHash is null)
            throw new RegraDeNegocioException("O convite ainda não foi aceito (sem senha definida).");

        // ANTI-LOCKOUT: sobre SI MESMO, ninguem muda o proprio papel nem se desativa. Sem isso,
        // o unico dono se rebaixa a vendedor e a empresa fica sem quem gerencie equipe e conexao.
        if (usuario.Id == contexto.UsuarioId)
        {
            if (papel != usuario.Papel)
                throw new RegraDeNegocioException("Você não pode mudar o próprio papel.");
            if (status != StatusUsuario.Ativo)
                throw new RegraDeNegocioException("Você não pode desativar a si mesmo.");
        }

        // Tem que restar ao menos UM dono ativo.
        if (usuario.Papel == PapelUsuario.Dono && usuario.Status == StatusUsuario.Ativo
            && (papel != PapelUsuario.Dono || status != StatusUsuario.Ativo))
        {
            var outros = await db.Usuarios.CountAsync(
                u => u.Id != usuarioId && u.Papel == PapelUsuario.Dono && u.Status == StatusUsuario.Ativo, ct);
            if (outros == 0)
                throw new RegraDeNegocioException("Precisa restar ao menos um dono ativo.");
        }

        // A SEGUNDA PORTA DA COTA. Vem depois do anti-lockout e do "ao menos um dono ativo" de
        // propósito: erro de travamento ganha de erro de contrato, porque um fala da empresa
        // quebrando e o outro, do que foi vendido.
        if (status == StatusUsuario.Ativo && usuario.Status != StatusUsuario.Ativo)
            await ExigirVagaLivreAsync(ct);

        usuario.Nome = nome;
        usuario.Papel = papel;
        usuario.Status = status;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>===================== A COTA DE PESSOAS (OPE-1) =====================
    /// DUAS portas abrem vaga, e é por isso que isto é um método em vez de um `if` no convite:
    /// `ConvidarAsync` cria linha `convidado`, e `AtualizarAsync` reativa um `inativo`. Checar só a
    /// primeira deixa o teto burlável em três cliques — desativa três, convida três, reativa três.
    ///
    /// ⚠️ TRÊS CAMINHOS NÃO CHAMAM AQUI, E CADA UM POR UM MOTIVO DIFERENTE:
    ///   • `ReenviarConviteAsync` — o convite JÁ ocupa a vaga; cobrar de novo recusaria reenviar um
    ///     convite que já está pago;
    ///   • `AceitarConviteAsync` — a vaga foi reservada no convite EXATAMENTE para o aceite nunca
    ///     poder falhar. Cobrar ali faria alguém convidado na segunda, com o limite baixado na
    ///     terça, ser barrado na quarta com um link válido na mão e nenhuma ação possível;
    ///   • `ServicoCadastroEmpresa` — a empresa nasce com UMA pessoa e o piso do CHECK é 1, então
    ///     nunca estoura; e cobrar ali faria a criação depender de um limite ainda não atribuído.
    ///
    /// ⚠️ ESTAR ACIMA DO LIMITE É ESTADO LEGAL. O operador pode baixar o teto abaixo do uso atual, e
    /// quando isso acontece ninguém é deslogado e ninguém é desativado — só o próximo convite falha.
    /// A alternativa seria o software escolher quais 2 de 5 funcionários perdem acesso, e ninguém
    /// desenhou essa escolha nem escreveu a mensagem para quem fosse sorteado.
    ///
    /// A CORRIDA: dois convites simultâneos passam os dois pela contagem. Sem trava, pelo mesmo
    /// motivo do limite de conexões — quem clica é o dono, numa tela de configuração, um clique por
    /// vez, e o estrago é uma linha a mais, não dado corrompido. Se um dia importar, o lugar é um
    /// advisory lock por empresa.
    /// ====================================================================</summary>
    private async Task ExigirVagaLivreAsync(CancellationToken ct)
    {
        var limite = await db.Empresas.AsNoTracking()
            .Select(e => (int)e.LimiteUsuarios)
            .FirstOrDefaultAsync(ct);

        // Contam ATIVO + CONVIDADO; inativo não conta, porque desativar é a única saída que o
        // desenho oferece (não há delete de usuário) e saída que não libera vaga não é saída.
        // O índice `ix_usuarios_empresa` (empresa_id, status) cobre este predicado.
        var ocupadas = await db.Usuarios.CountAsync(
            u => u.Status == StatusUsuario.Ativo || u.Status == StatusUsuario.Convidado, ct);

        if (ocupadas < limite) return;

        throw new RegraDeNegocioException(
            limite == 1
                ? "Seu plano permite um usuário. Fale com o suporte para incluir mais pessoas."
                // ⚠️ "(contam os ativos e os convites pendentes)" É LOAD-BEARING. Sem essa frase, um
                // dono com 3 vagas e 1 convite pendente lê "3 usuários", conta 2 pessoas na tela e
                // conclui que o software está quebrado. É a diferença entre um limite e um chamado.
                : $"Seu plano permite {limite} usuários e as {limite} vagas já estão ocupadas "
                  + "(contam os ativos e os convites pendentes). Desative alguém ou fale com o suporte.",
            conflito: true);
    }

    public async Task TrocarMinhaSenhaAsync(string senhaAtual, string senhaNova, CancellationToken ct)
    {
        if ((senhaNova ?? "").Length < TamanhoMinimoSenha)
            throw new RegraDeNegocioException($"A nova senha precisa de ao menos {TamanhoMinimoSenha} caracteres.");

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == contexto.UsuarioId, ct)
            ?? throw new RegraDeNegocioException("Usuário não encontrado.");

        if (!HashSenha.Confere(senhaAtual, usuario.SenhaHash))
            throw new RegraDeNegocioException("Senha atual incorreta.");

        usuario.SenhaHash = HashSenha.Gerar(senhaNova!);
        await db.SaveChangesAsync(ct);

        // AVISO de senha alterada. É a defesa mais barata contra conta invadida sem o dono
        // perceber: quem não trocou a senha descobre na hora.
        await email.SenhaAlteradaAsync(usuario.EmpresaId, usuario.Email, usuario.Nome, ct);
    }

    public async Task<MinhaConta> MinhaContaAsync(CancellationToken ct) =>
        await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == contexto.UsuarioId)
            .Select(u => new MinhaConta(
                u.Id, u.Nome, u.Email, u.Papel.ToString().ToLower(), u.Empresa.Nome))
            .FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException("Usuário não encontrado.");

    public async Task AtualizarMinhaContaAsync(EditarMinhaConta dados, CancellationToken ct)
    {
        var nome = (dados.Nome ?? "").Trim();
        if (nome.Length == 0) throw new RegraDeNegocioException("Informe o seu nome.");

        var email = (dados.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0 || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
            throw new RegraDeNegocioException("Informe um e-mail válido.");

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == contexto.UsuarioId, ct)
            ?? throw new RegraDeNegocioException("Usuário não encontrado.");

        if (!string.Equals(email, usuario.Email, StringComparison.OrdinalIgnoreCase))
        {
            // IgnoreQueryFilters: o e-mail é único GLOBALMENTE (índice funcional em lower(email)),
            // não por empresa. Checar só dentro do tenant deixaria passar uma colisão com outra
            // empresa, e a violação estouraria como erro de banco na cara do usuário.
            var emUso = await db.Usuarios.IgnoreQueryFilters()
                .AnyAsync(u => u.Id != usuario.Id && u.Email.ToLower() == email, ct);

            if (emUso)
                throw new RegraDeNegocioException(
                    "Este e-mail já está em uso por outra conta.", conflito: true);

            usuario.Email = email;
        }

        usuario.Nome = nome;
        await db.SaveChangesAsync(ct);
    }

    // ---- fluxos PUBLICOS: rodam SEM tenant no contexto ----
    // Todos usam IgnoreQueryFilters porque o token e a chave, e ela e global. Sem isso, o
    // EmpresaId 0 faria a busca voltar vazia e o convite nunca seria aceito.

    public Task<ConviteInfo?> ConviteInfoAsync(string token, CancellationToken ct) =>
        db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TokenConvite == token && u.Status == StatusUsuario.Convidado
                     && u.ConviteExpira != null && u.ConviteExpira >= relogio.GetUtcNow().UtcDateTime)
            .Select(u => new ConviteInfo(u.Nome, u.Email, u.Empresa.Nome))
            .FirstOrDefaultAsync(ct);

    public async Task<UsuarioAutenticado?> AceitarConviteAsync(string token, string senha, CancellationToken ct)
    {
        if ((senha ?? "").Length < TamanhoMinimoSenha)
            throw new RegraDeNegocioException($"A senha precisa de ao menos {TamanhoMinimoSenha} caracteres.");

        var agora = relogio.GetUtcNow().UtcDateTime;
        var usuario = await db.Usuarios.IgnoreQueryFilters().Include(u => u.Empresa)
            .FirstOrDefaultAsync(u => u.TokenConvite == token, ct);

        if (usuario is null || usuario.Status != StatusUsuario.Convidado
            || usuario.ConviteExpira is null || usuario.ConviteExpira < agora)
            return null;

        usuario.SenhaHash = HashSenha.Gerar(senha!);
        usuario.Status = StatusUsuario.Ativo;
        usuario.TokenConvite = null;
        usuario.ConviteExpira = null;
        usuario.UltimoAcessoEm = agora;
        await db.SaveChangesAsync(ct);

        return new UsuarioAutenticado(
            usuario.Id, usuario.Nome, usuario.Email, usuario.Papel.ToString().ToLower(),
            usuario.EmpresaId, usuario.Empresa.Nome);
    }

    public Task<ConviteInfo?> ResetInfoAsync(string token, CancellationToken ct) =>
        db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TokenReset == token
                     && u.ResetExpira != null && u.ResetExpira >= relogio.GetUtcNow().UtcDateTime)
            .Select(u => new ConviteInfo(u.Nome, u.Email, u.Empresa.Nome))
            .FirstOrDefaultAsync(ct);

    public async Task<bool> RedefinirSenhaAsync(string token, string senha, CancellationToken ct)
    {
        if ((senha ?? "").Length < TamanhoMinimoSenha)
            throw new RegraDeNegocioException($"A senha precisa de ao menos {TamanhoMinimoSenha} caracteres.");

        var usuario = await db.Usuarios.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TokenReset == token, ct);

        if (usuario is null || usuario.ResetExpira is null
            || usuario.ResetExpira < relogio.GetUtcNow().UtcDateTime) return false;

        usuario.SenhaHash = HashSenha.Gerar(senha!);
        usuario.TokenReset = null;
        usuario.ResetExpira = null;
        await db.SaveChangesAsync(ct);

        // Também avisa quando a troca vem por LINK: é justamente o caminho que um invasor usaria
        // se tivesse acesso à caixa de e-mail, e o aviso é o que dá ao dono a chance de reagir.
        await email.SenhaAlteradaAsync(usuario.EmpresaId, usuario.Email, usuario.Nome, ct);
        return true;
    }

    // ---- apoio ----
    /// <summary>SEM IgnoreQueryFilters: o filtro por empresa E a protecao — usuario de outro
    /// tenant simplesmente nao e encontrado.</summary>
    private async Task<Usuario> MeuUsuarioAsync(long id, CancellationToken ct) =>
        await db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new RegraDeNegocioException("Usuário não encontrado.");

    private static string GerarToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static PapelUsuario ParsePapel(string? s) =>
        Enum.TryParse<PapelUsuario>((s ?? "").Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p)
            ? p : throw new RegraDeNegocioException("Papel inválido. Use dono, gestor ou vendedor.");

    private static StatusUsuario ParseStatusEdicao(string? s) =>
        Enum.TryParse<StatusUsuario>((s ?? "").Trim(), ignoreCase: true, out var st)
        && st is StatusUsuario.Ativo or StatusUsuario.Inativo
            ? st : throw new RegraDeNegocioException("Status inválido. Use ativo ou inativo.");
}
