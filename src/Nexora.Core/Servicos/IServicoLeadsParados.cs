namespace Nexora.Core.Servicos;

/// <summary>Um lead que parou de ser trabalhado.
///
/// `EtapaNome` e `PipelineNome` são NULOS quando o contato nunca virou negócio — e isso é o caso
/// mais comum do lead frio, não uma falha de dado. A tela escreve travessão.</summary>
public record LeadParado(
    long ContatoId,
    string Nome,
    string Telefone,
    string Origem,
    long? ResponsavelId,
    string? ResponsavelNome,
    long? NegociacaoId,
    string? PipelineNome,
    string? EtapaNome,
    decimal? Valor,
    DateTime ParadoDesde,
    int DiasParado);

public record PaginaLeadsParados(IReadOnlyList<LeadParado> Itens, int Total);

/// <summary>O recorte que a tela pede. `Dias` é lista fechada, validada no serviço.</summary>
public record FiltroLeadsParados(int Dias, long? ResponsavelId, int Pagina, int Tamanho);

/// <summary>===================== QUEM PAROU DE SER TRABALHADO (LPA-1) =====================
///
/// O lead que chegou, teve uma conversa e esfriou fica no funil para sempre, indistinguível do que
/// foi atendido ontem. Até aqui, descobrir isso exigia abrir contato por contato.
///
/// ⚠️ FASE 1 NÃO ENVIA MENSAGEM NENHUMA, e isso não é falta de tempo: o WhatsApp roda via Baileys,
/// e disparo em massa queima o número do cliente. Quem envia é o vendedor, à mão, pela caixa. O que
/// esta tela faz é ACHAR os leads; agir em lote sobre eles vem nas entregas seguintes.
///
/// ⚠️ "PARADO" É `COALESCE(conversas.ultima_mensagem_em, contatos.criado_em)`, e as duas metades
/// importam:
///
///   · `ultima_mensagem_em` é NOT NULL, materializada e indexada — a única data de interação que o
///     banco já mantém pronta. `aguardando_desde` (a do semáforo) NÃO serve: ela vira nula no
///     instante em que alguém responde, e um lead respondido há seis meses é exatamente o alvo;
///   · `criado_em` cobre quem NUNCA conversou — o lead que entrou por formulário ou importação e
///     ninguém chamou. É o caso mais frio que existe, e sem o COALESCE ele seria o único a nunca
///     aparecer.
/// ============================================================================================</summary>
public interface IServicoLeadsParados
{
    /// <summary>⚠️ Quem não tem `ver_numeros_da_equipe` recebe só os PRÓPRIOS leads parados — e
    /// isso é útil, não uma limitação: o vendedor tem a lista dele sem precisar de permissão nova.
    /// É por isso que a tela não tem guarda de rota, igual a `/relatorios`.</summary>
    Task<PaginaLeadsParados> ListarAsync(FiltroLeadsParados filtro, CancellationToken ct);
}

/// <summary>As janelas que a tela oferece, em dias.
///
/// Lista FECHADA: o número vira uma subtração de data, e aceitar qualquer inteiro deixaria um
/// `dias=0` trazer a base inteira numa consulta paginada que o cliente pode pedir em laço.</summary>
public static class JanelasDeParada
{
    public static readonly int[] EmDias = [15, 30, 60, 90];

    public const int Padrao = 30;

    /// <summary>Teto de linhas por página. Cinquenta porque a tela existe para AGIR em lote sobre o
    /// que está na página — vinte obrigaria a paginar no meio de uma seleção, e duzentas fariam o
    /// dono rolar procurando o que marcou.</summary>
    public const int TamanhoMaximoPagina = 50;
}
