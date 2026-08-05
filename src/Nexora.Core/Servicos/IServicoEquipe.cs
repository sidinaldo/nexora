namespace Nexora.Core.Servicos;

public record UsuarioEquipeDto(
    long Id, string Nome, string Email, string Papel, string Status, DateTime? UltimoAcessoEm);

public record NovoConvite(string Nome, string Email, string Papel);
public record EditarUsuario(string Nome, string Papel, string Status);

/// <summary>Nome, e-mail e empresa por tras de um token de convite ou de redefinicao — o que a
/// pagina publica mostra antes de a pessoa definir a senha.</summary>
public record ConviteInfo(string Nome, string Email, string EmpresaNome);

/// <summary>O token gerado. Sem envio de e-mail na fase 1: o dono copia o link e manda por fora
/// (mesma limitacao do Recupera, registrada desde o bloco 1).</summary>
public record TokenGerado(long UsuarioId, string Token);

/// <summary>A própria conta. `Papel` e `EmpresaNome` vão junto porque a tela mostra os dois e
/// eles não são editáveis — quem muda papel é o dono, na tela de Equipe.</summary>
public record MinhaConta(long Id, string Nome, string Email, string Papel, string EmpresaNome);

public record EditarMinhaConta(string Nome, string Email);

public interface IServicoEquipe
{
    Task<IReadOnlyList<UsuarioEquipeDto>> ListarAsync(CancellationToken ct);
    Task<TokenGerado> ConvidarAsync(NovoConvite novo, CancellationToken ct);
    Task<TokenGerado> ReenviarConviteAsync(long usuarioId, CancellationToken ct);
    Task<TokenGerado> GerarResetSenhaAsync(long usuarioId, CancellationToken ct);
    Task AtualizarAsync(long usuarioId, EditarUsuario dados, CancellationToken ct);

    /// <summary>Troca a PROPRIA senha. Exige a atual.</summary>
    Task TrocarMinhaSenhaAsync(string senhaAtual, string senhaNova, CancellationToken ct);

    Task<MinhaConta> MinhaContaAsync(CancellationToken ct);

    /// <summary>Altera o PRÓPRIO nome e e-mail. Não aceita id: o alvo é sempre o usuário do
    /// contexto, e é isso que permite a rota ser [Authorize] simples em vez de por papel.
    ///
    /// O e-mail é a identidade de LOGIN e é único globalmente (índice funcional em lower(email)),
    /// então trocar exige checar colisão com qualquer usuário de qualquer empresa.</summary>
    Task AtualizarMinhaContaAsync(EditarMinhaConta dados, CancellationToken ct);

    // ---- fluxos PUBLICOS (sem sessao): o convidado ainda nao tem senha ----

    /// <summary>"Esqueci minha senha", AUTO-SERVIÇO. Gera o token e manda o e-mail.
    ///
    /// ===================== NÃO DEVOLVE NADA, E É O PONTO =====================
    /// `Task`, não `Task&lt;bool&gt;`. Um retorno que diga se o e-mail existe transformaria o
    /// endpoint num VERIFICADOR DE CONTAS: qualquer um descobriria quem é cliente do Nexora
    /// testando endereços. A mesma disciplina do login com HashDummy (PoliticaLogin).
    ///
    /// E-mail inexistente é NO-OP silencioso. Quem chama não tem como saber, e o controller
    /// responde igual nos dois casos.
    /// ========================================================================</summary>
    Task SolicitarResetSenhaAsync(string endereco, CancellationToken ct);

    Task<ConviteInfo?> ConviteInfoAsync(string token, CancellationToken ct);
    Task<UsuarioAutenticado?> AceitarConviteAsync(string token, string senha, CancellationToken ct);
    Task<ConviteInfo?> ResetInfoAsync(string token, CancellationToken ct);
    Task<bool> RedefinirSenhaAsync(string token, string senha, CancellationToken ct);
}
