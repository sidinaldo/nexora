namespace Nexora.Core.Servicos;

/// <summary>A conexao da empresa, como o painel a ve.
///
/// `PodeRemover` e `MotivoNaoRemove` vem do SERVIDOR, nao sao deduzidos na tela: quem sabe se ha
/// conversa apontando para a conexao e o banco, e a tela nao tem esse dado. Sem isso ela ofereceria
/// um botao que as vezes devolve erro — que e a pior forma de dizer "nao pode".</summary>
public record ConexaoDto(
    long Id, string Nome, string InstanceName, string? Numero, string? NumeroAnterior,
    string? PerfilNome, string? PerfilFotoUrl, string Status,
    DateTime? ConectadoEm, DateTime? DesconectadoEm,
    int Conversas, bool PodeRemover, string? MotivoNaoRemove);

/// <summary>A lista + o que o PLANO permite. O limite vem junto porque a tela precisa dele para
/// decidir se mostra "adicionar" — e um limite que a tela adivinha diverge do que o servidor
/// aplica no dia em que o contrato muda.</summary>
public record ConexoesDto(IReadOnlyList<ConexaoDto> Itens, int Limite, bool PodeAdicionar);

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

public record NovaConexao(string Nome);

/// <summary>As conexoes de WhatsApp da empresa.
///
/// ===================== MULTI-NUMERO, E O QUE ELE EXIGIU =====================
/// Ate o ARQ-2 era UMA por empresa, travada pelo indice `uq_conexoes_empresa`. O limite virou dado
/// (`empresas.limite_conexoes`), porque ele muda por CONTRATO — e numero que muda por contrato nao
/// pode morar num indice, senao trocar de plano vira migration.
///
/// Dois caminhos quentes ja estavam certos para N e nao mudaram:
///   • ENTRADA — o webhook resolve o tenant pelo `instance_name` do evento;
///   • RESPOSTA — `ServicoConversas` usa a conexao DA CONVERSA, nao "a da empresa".
///
/// O que quebrava era o motor de follow-up, que pegava a conexao da empresa como porteiro da
/// rodada inteira. Passou a usar a conexao da conversa de cada contato — ver `IDadosFollowUp`.
///
/// `instance_name` NAO e editavel, em nenhuma circunstancia: e a identidade na Evolution e a
/// chave global do webhook. Renomear orfanaria a instancia e pararia de receber mensagem EM
/// SILENCIO — sem erro, sem log, sem sintoma ate alguem reclamar que o cliente nao respondeu.
/// ============================================================================</summary>
public interface IServicoConexoes
{
    /// <summary>Todas as conexoes da empresa, com o limite do plano.</summary>
    Task<ConexoesDto> ListarAsync(CancellationToken ct);

    /// <summary>Uma conexao. `null` quando nao existe OU e de outra empresa — o `null` nao
    /// distingue os dois de proposito.</summary>
    Task<ConexaoDto?> ObterAsync(long conexaoId, CancellationToken ct);

    /// <summary>Cria uma conexao ainda NAO pareada. Recusa se a empresa atingiu o limite do plano.
    ///
    /// O `instance_name` e DERIVADO e imutavel — quem escolhe e o sistema, nao o dono: ele e a
    /// identidade na Evolution, e deixar o usuario digitar abriria colisao entre tenants.</summary>
    Task<long> CriarAsync(NovaConexao nova, CancellationToken ct);

    /// <summary>Renomeia. So o NOME — ver o bloco sobre `instance_name` acima.</summary>
    Task RenomearAsync(long conexaoId, string nome, CancellationToken ct);

    /// <summary>Remove a conexao.
    ///
    /// Recusa em dois casos, e os dois sao invariantes de dado, nao politica de tela:
    ///   • tem conversa ou mensagem apontando para ela — a FK e RESTRICT, e apagar em cascata
    ///     significaria perder historico de atendimento;
    ///   • e a ULTIMA da empresa — sem nenhuma conexao o webhook nao acha o tenant, o envio nao
    ///     tem instancia, e NADA no sistema recria. A empresa fica sem caminho de volta.</summary>
    Task RemoverAsync(long conexaoId, CancellationToken ct);

    /// <summary>Estado ao vivo + persistencia GUARDADA (so escreve quando muda — a tela faz
    /// polling de 3s) + backfill do numero quando o webhook se perdeu.</summary>
    Task<StatusConexaoDto> StatusAsync(long conexaoId, CancellationToken ct);

    /// <summary>Cria a instancia na Evolution se preciso e devolve o QR.</summary>
    Task<QrCodeDto> ConectarAsync(long conexaoId, CancellationToken ct);

    /// <summary>Pareamento por CODIGO: a Evolution exige o numero para gera-lo.</summary>
    Task<QrCodeDto> ParearAsync(long conexaoId, string numero, CancellationToken ct);

    Task DesconectarAsync(long conexaoId, CancellationToken ct);

    /// <summary>A empresa viu o aviso de que conectou um numero diferente — limpa o alerta.</summary>
    Task ReconhecerTrocaAsync(long conexaoId, CancellationToken ct);

    /// <summary>Quanto saiu, quanto espera e quanto foi perdido, NA CONEXAO. Com multi-numero o
    /// total da empresa esconderia justamente o que interessa: qual dos numeros esta falhando.</summary>
    Task<SaudeConexaoDto> SaudeAsync(long conexaoId, CancellationToken ct);
}
