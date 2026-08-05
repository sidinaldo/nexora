namespace Nexora.Core.Servicos;

/// <summary>A conexao da empresa, como o painel a ve.</summary>
public record ConexaoDto(
    long Id, string Nome, string InstanceName, string? Numero, string? NumeroAnterior,
    string? PerfilNome, string? PerfilFotoUrl, string Status,
    DateTime? ConectadoEm, DateTime? DesconectadoEm);

/// <summary>Estado AO VIVO, consultado na Evolution. `Estado` e o cru dela
/// (open|connecting|close|nao_criada|offline); `Conectado` e o que a tela usa.</summary>
public record StatusConexaoDto(string InstanceName, string Estado, bool Conectado);

/// <summary>QR para escanear, ou codigo de pareamento por numero.</summary>
public record QrCodeDto(string? Base64, string? Codigo, string? PairingCode, string Estado, bool Conectado);

/// <summary>Saude do envio pelo numero da empresa.
///
/// `Pendentes` e `Expiradas` sao numeros SEPARADOS de proposito. No Recupera existe um contador
/// so de "outbox", que mistura o que ainda vai ser tentado com o que ja passou da janela e nunca
/// mais sera — e a segunda categoria e justamente a que exige acao humana.</summary>
public record SaudeConexaoDto(
    int EnviadasHoje,
    int Pendentes,
    int Expiradas,
    int FalhasHoje);

/// <summary>A conexao de WhatsApp da empresa. FASE 1: uma por empresa (uq_conexoes_empresa),
/// criada junto com a empresa no cadastro — por isso nao ha CRUD aqui, so pareamento e status.
///
/// Simplificado do Recupera, que suporta N conexoes com uma padrao e atribuicao por usuario.
/// O que saiu: is_padrao e o rebaixamento da padrao, usuario_id (conexao por vendedor), o CRUD
/// de multiplas conexoes.</summary>
public interface IServicoConexoes
{
    Task<ConexaoDto?> MinhaAsync(CancellationToken ct);

    /// <summary>Estado ao vivo + persistencia GUARDADA (so escreve quando muda — a tela faz
    /// polling de 3s) + backfill do numero quando o webhook se perdeu.</summary>
    Task<StatusConexaoDto> StatusAsync(CancellationToken ct);

    /// <summary>Cria a instancia na Evolution se preciso e devolve o QR.</summary>
    Task<QrCodeDto> ConectarAsync(CancellationToken ct);

    /// <summary>Pareamento por CODIGO: a Evolution exige o numero para gera-lo.</summary>
    Task<QrCodeDto> ParearAsync(string numero, CancellationToken ct);

    Task DesconectarAsync(CancellationToken ct);

    /// <summary>A empresa viu o aviso de que conectou um numero diferente — limpa o alerta.</summary>
    Task ReconhecerTrocaAsync(CancellationToken ct);

    /// <summary>Quanto saiu, quanto espera e quanto foi perdido. Depende do bloco de envio —
    /// por isso ficou adiado no bloco 3.</summary>
    Task<SaudeConexaoDto> SaudeAsync(CancellationToken ct);
}
