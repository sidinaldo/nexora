namespace Nexora.Core.Servicos;

/// <summary>O que o formulário do site manda.
///
/// `Armadilha` é o honeypot: um campo escondido por CSS que humano nunca vê e bot preenche por
/// varrer o formulário inteiro. O nome do campo no HTML é neutro (`sobrenome`) de propósito —
/// `honeypot` seria ignorado por qualquer bot que preste atenção.</summary>
public record LeadDoFormulario(
    string? Nome,
    string? Telefone,
    string? Email,
    string? Mensagem,
    string? Armadilha);

/// <summary>O que a captura decidiu. NÃO vai para o visitante do site — ele recebe sempre a
/// mesma resposta —, mas o log e o teste precisam distinguir.</summary>
public enum ResultadoCaptura
{
    /// <summary>Contato novo criado, com lembrete de primeiro contato.</summary>
    ContatoCriado,

    /// <summary>Telefone já conhecido: lembrete para o responsável, sem contato duplicado.</summary>
    LembreteParaContatoExistente,

    /// <summary>Honeypot preenchido. Descartado em silêncio.</summary>
    DescartadoComoBot
}

public interface IServicoCaptura
{
    /// <summary>Recebe um lead pela chave pública do formulário.
    ///
    /// ===================== ROTA PÚBLICA: TENANT ZERO =====================
    /// Roda SEM sessão, então `IContextoEmpresa.EmpresaId` é 0 e o query filter global devolveria
    /// vazio em silêncio — sem erro, sem log, sem lead. A empresa é resolvida pela CHAVE, e toda
    /// consulta seguinte usa `IgnoreQueryFilters()` mais filtro explícito por `empresaId`, do
    /// mesmo jeito que o processador do webhook faz com `instance_name`.
    /// =====================================================================
    ///
    /// `origem` é o cabeçalho `Origin` da requisição (nulo quando não há navegador na frente).
    ///
    /// Lança `RegraDeNegocioException` para chave inválida, formulário desligado, origem não
    /// permitida e dado inválido — o controller traduz para a resposta neutra.</summary>
    Task<ResultadoCaptura> ReceberAsync(
        string chave, LeadDoFormulario lead, string? origem, CancellationToken ct);
}

// ==================================================================== configuração (área logada)
public record FormularioDto(
    long Id,
    string Nome,
    string Chave,
    string? DominioPermitido,
    bool Ativo,
    int LeadsRecebidos,
    DateTime CriadoEm);

public record NovoFormulario(string Nome, string? DominioPermitido);

public interface IServicoFormularios
{
    Task<IReadOnlyList<FormularioDto>> ListarAsync(CancellationToken ct);
    Task<long> CriarAsync(NovoFormulario novo, CancellationToken ct);
    Task AtualizarAsync(long id, NovoFormulario dados, CancellationToken ct);

    /// <summary>Liga e desliga. NÃO apaga: o histórico dos leads que já vieram continua fazendo
    /// sentido, e religar é um clique.</summary>
    Task AlternarAtivoAsync(long id, bool ativo, CancellationToken ct);

    /// <summary>Gera uma chave nova e invalida a anterior na hora. É o que se faz quando a chave
    /// vaza — e por ela ser por FORMULÁRIO, os outros continuam funcionando.</summary>
    Task<string> RegerarChaveAsync(long id, CancellationToken ct);
}
