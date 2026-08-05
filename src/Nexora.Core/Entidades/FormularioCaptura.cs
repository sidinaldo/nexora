namespace Nexora.Core.Entidades;

/// <summary>Um formulário de captação no site do cliente.
///
/// ===================== A CHAVE É POR FORMULÁRIO, NÃO POR EMPRESA =====================
/// A chave viaja na URL de um endpoint público e vai colada no HTML do site — ou seja, é
/// pública por construção: qualquer visitante lê o código-fonte da página.
///
/// Com uma chave por EMPRESA, revogar um formulário comprometido derrubaria todos os outros ao
/// mesmo tempo: a landing page da campanha, o rodapé do site, o formulário do orçamento. Por
/// formulário, o cliente apaga só o que vazou.
///
/// É por isso que a chave não é segredo de autenticação: ela IDENTIFICA o formulário. Quem
/// protege são as outras quatro camadas (origem permitida, rate limit, honeypot e validação).
/// =====================================================================================</summary>
public class FormularioCaptura : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>Nome interno, que também vai para `contatos.origem_detalhe` — é o que o vendedor
    /// lê no card para saber de onde o lead veio.</summary>
    public string Nome { get; set; } = null!;

    /// <summary>Token de 24 bytes em hex. ÚNICO GLOBALMENTE: a URL pública não carrega o tenant,
    /// então é a chave que resolve a empresa.</summary>
    public string Chave { get; set; } = null!;

    /// <summary>Domínio de onde o formulário pode postar (`www.cliente.com.br`), sem esquema.
    ///
    /// NULO = aceita de qualquer origem. É o padrão para quem usa `fetch` de um app ou testa
    /// pelo terminal — pedir domínio de quem não tem navegador na frente seria atrapalhar sem
    /// proteger.
    ///
    /// Isto NÃO impede POST direto: quem manda `curl` escolhe o cabeçalho que quiser. Corta o
    /// abuso trivial pelo navegador, que é o volume real.</summary>
    public string? DominioPermitido { get; set; }

    /// <summary>Desligado NÃO apaga: o histórico dos leads que já vieram continua fazendo
    /// sentido, e religar é um clique.</summary>
    public bool Ativo { get; set; } = true;

    /// <summary>Envios ACEITOS — inclui o telefone repetido, que vira lembrete em vez de contato.
    /// Contador denormalizado de propósito: contar por `origem_detalhe` daria número errado no
    /// dia em que alguém renomeasse o formulário.</summary>
    public int LeadsRecebidos { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
