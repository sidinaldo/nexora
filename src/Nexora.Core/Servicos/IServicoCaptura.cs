namespace Nexora.Core.Servicos;

/// <summary>O que o formulário do site manda.
///
/// `Armadilha` é o honeypot: um campo escondido por CSS que humano nunca vê e bot preenche por
/// varrer o formulário inteiro. O nome do campo no HTML é neutro (`sobrenome`) de propósito —
/// `honeypot` seria ignorado por qualquer bot que preste atenção.
///
/// `Rastreio` é OPCIONAL, e tem de ser: o código que o cliente colou no site ano passado não
/// manda nada disso, e continua funcionando igual (INT-4).</summary>
public record LeadDoFormulario(
    string? Nome,
    string? Telefone,
    string? Email,
    string? Mensagem,
    string? Armadilha,
    RastreioDoSite? Rastreio = null);

/// <summary>DE ONDE A PESSOA VEIO, pelos campos ocultos do formulário (INT-4).
///
/// ===================== CAMPOS NOMEADOS, E NÃO UM DICIONÁRIO ABERTO =====================
/// O `jsonb` do banco existe para absorver o parâmetro que a Meta ou o Google inventarem sem
/// pedir migração. Aqui, não: isto é o corpo de um endpoint PÚBLICO e sem sessão, e um
/// dicionário aberto é escrita sem teto — qualquer um postaria mil chaves de 10 KB.
///
/// Parâmetro novo custa uma linha de código e nenhuma migração. É o preço certo.
/// ======================================================================================
///
/// Tudo opcional: o visitante que chegou digitando o endereço não tem nenhum deles, e isso é o
/// caso mais comum. O lead entra de qualquer jeito — o rastro MELHORA a atribuição, não a
/// habilita.</summary>
public record RastreioDoSite(
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmContent = null,
    string? UtmTerm = null,

    /// <summary>A URL onde a pessoa estava. Guardada SEM query string — ver `SemQuery`.</summary>
    string? Pagina = null,

    /// <summary>O `document.referrer`. Também sem query string.</summary>
    string? Referencia = null,

    // ===================== OS IDENTIFICADORES DE CLIQUE =====================
    // `Fbclid` vem da URL; `Fbp` e `Fbc` vêm dos cookies que o pixel da Meta escreve. `Fbc` pode
    // ser MONTADO do `fbclid` quando não há pixel no site — é o que faz isto funcionar para quem
    // nunca instalou nada, e a Meta permite explicitamente.
    // =======================================================================
    string? Fbclid = null,
    string? Fbp = null,
    string? Fbc = null,
    string? Gclid = null,
    string? Ttclid = null,

    /// <summary>O id que o navegador usou no evento do pixel. Reaproveitado no evento do servidor
    /// para a Meta saber que os dois são o mesmo fato — sem ele, o lead conta duas vezes.</summary>
    Guid? EventoId = null);

/// <summary>O que vem da CONEXÃO, e não do corpo — por isso é parâmetro separado do lead.
///
/// A distinção não é organização: o corpo é escrito pelo JavaScript da página e pode dizer
/// qualquer coisa, inclusive o IP de outra pessoa. Estes três o servidor observa.</summary>
public record DadosDaConexao(string? Origem, string? Ip, string? UserAgent)
{
    /// <summary>Requisição sem navegador identificável: curl, servidor, app — e todo teste que
    /// não está falando sobre origem, IP ou navegador.</summary>
    public static readonly DadosDaConexao Nenhuma = new(null, null, null);

    /// <summary>Só a origem, para o teste que é sobre domínio permitido.</summary>
    public static DadosDaConexao De(string? origem) => new(origem, null, null);
}

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
    /// `conexao` traz o que o NAVEGADOR disse, e não o corpo: origem, IP e User-Agent.
    ///
    /// Lança `RegraDeNegocioException` para chave inválida, formulário desligado, origem não
    /// permitida e dado inválido — o controller traduz para a resposta neutra.</summary>
    Task<ResultadoCaptura> ReceberAsync(
        string chave, LeadDoFormulario lead, DadosDaConexao conexao, CancellationToken ct);
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
