namespace Nexora.Core.Servicos;

public record UsuarioEquipeDto(
    long Id, string Nome, string Email, string Papel, string Status, DateTime? UltimoAcessoEm,
    IReadOnlyList<string> Permissoes);

public record NovoConvite(string Nome, string Email, string Papel);

/// <summary>O que a tela de Equipe manda ao salvar.
///
/// ⚠️ `Permissoes` NULO NÃO É LISTA VAZIA, e a diferença tem consequência. O atalho de
/// inativar/reativar da tela manda só nome, papel e situação — se ausência significasse "nenhuma
/// permissão", inativar alguém apagaria em silêncio tudo que o dono tinha marcado para ele.
///
/// Quando vem, é a lista EFETIVA (o que os interruptores mostram), não as exceções: quem calcula o
/// que diverge do papel é o servidor. Deixar o cliente mandar o diff seria deixá-lo decidir a
/// autorização.</summary>
public record EditarUsuario(
    string Nome, string Papel, string Status, IReadOnlyList<string>? Permissoes = null);

/// <summary>Nome, e-mail e empresa por tras de um token de convite ou de redefinicao — o que a
/// pagina publica mostra antes de a pessoa definir a senha.</summary>
public record ConviteInfo(string Nome, string Email, string EmpresaNome);

/// <summary>O token gerado. Sem envio de e-mail na fase 1: o dono copia o link e manda por fora
/// (mesma limitacao do Recupera, registrada desde o bloco 1).</summary>
public record TokenGerado(long UsuarioId, string Token);

/// <summary>A própria conta. `Papel` e `EmpresaNome` vão junto porque a tela mostra os dois e
/// eles não são editáveis — quem muda papel é o dono, na tela de Equipe.</summary>
public record MinhaConta(long Id, string Nome, string Email, string Papel, string EmpresaNome);

/// <summary>`SenhaAtual` só é exigida quando o e-mail muda (BUG-XX) — o nome se troca sem ela.</summary>
public record EditarMinhaConta(string Nome, string Email, string? SenhaAtual = null);

public interface IServicoEquipe
{
    /// <summary>A equipe INTEIRA, para os seletores de responsável das outras telas.</summary>
    Task<IReadOnlyList<UsuarioEquipeDto>> ListarAsync(CancellationToken ct);

    /// <summary>Uma página da equipe, para a tabela da tela de Equipe (AUD-XX, #21). A tabela
    /// recortava a lista inteira no navegador e calculava "Página X de Y" lá.</summary>
    Task<PaginaComTotal<UsuarioEquipeDto>> PaginaAsync(int pagina, int tamanho, CancellationToken ct);
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
