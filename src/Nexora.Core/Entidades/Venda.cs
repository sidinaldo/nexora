namespace Nexora.Core.Entidades;

/// <summary>===================== UMA VENDA FECHADA, UMA LINHA (NEG-1) =====================
///
/// A venda morava em COLUNA do contato (`ganho_em`, `valor`). Coluna guarda um valor so, e o
/// modelo assumia um negocio por contato, para sempre.
///
/// Padaria, oficina, clinica e salao vivem de cliente recorrente. O cliente volta, o vendedor
/// REABRE o card para negociar de novo, e reabrir limpa `ganho_em` — porque `ck_contatos_terminal`
/// proibe estar ganho e em negociacao ao mesmo tempo. A venda anterior nao era arquivada: a
/// coluna era sobrescrita. O dashboard, que conta `WHERE ganho_em >= inicioDoMes`, deixava de
/// encontra-la.
///
/// O sintoma e o pior possivel num sistema de vendas: **o faturamento de um mes fechado muda
/// sozinho**, e nao ha como o dono saber por que.
///
/// ===================== A DIVISAO DE PAPEIS =====================
///   CARIMBO  (`contatos.ganho_em`, `contatos.valor`) — em que estado o contato esta AGORA.
///                                                      O kanban precisa dele para saber que o
///                                                      card esta na etapa de ganho.
///   HISTORICO (esta tabela)                          — o que JA ACONTECEU. Fonte da verdade
///                                                      para faturamento e contagem.
///
/// Reabrir limpa o carimbo. As linhas ficam.
/// ==============================================================
///
/// ⚠️ NUNCA se apaga linha daqui. Faturamento que some sem rastro e pior que faturamento errado:
/// o primeiro nao tem investigacao possivel. Desfazer e `CanceladaEm`, nao DELETE.
///
/// PERDAS NAO ENTRAM. Perda e estado, nao evento com valor — `perdido_em` e `motivo_perda`
/// continuam no contato. A taxa de conversao passa a ser vendas ÷ (vendas + perdas do periodo).
/// ============================================================================================
/// </summary>
public class Venda
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }
    public long ContatoId { get; set; }

    /// <summary>`numeric(14,2)`, obrigatorio e MAIOR QUE ZERO (`ck_vendas_valor`).
    ///
    /// Diferente de `contatos.valor`, que e estimativa do negocio em andamento e admite nulo:
    /// aqui e dinheiro que entrou. Venda de valor zero nao e venda, e deixar passar corromperia
    /// a soma sem ninguem perceber.</summary>
    public decimal Valor { get; set; }

    public DateTime FechadaEm { get; set; }

    /// <summary>Quem fechou. Anulavel porque o usuario pode ser desligado depois, e a venda nao
    /// pode desaparecer junto com ele (FK com Restrict, mas o vinculo nasce podendo faltar
    /// quando o ganho vem de caminho sem sessao — a migracao dos dados antigos e um deles).</summary>
    public long? ResponsavelId { get; set; }

    public string? Observacao { get; set; }

    /// <summary>A etapa de ganho NO MOMENTO do fechamento.
    ///
    /// Congelada de proposito: a empresa pode renomear "Venda fechada" para "Contrato assinado"
    /// no mes que vem, e um relatorio do mes passado precisa dizer o que estava escrito la.</summary>
    public long EtapaId { get; set; }

    /// <summary>Desfazer — "marquei errado" —, que e diferente de reabrir — "o cliente voltou".
    ///
    /// Sem um caminho para desfazer, o vendedor que errou o valor nao tem saida e o faturamento
    /// fica errado para sempre. A linha permanece; sai das contagens.</summary>
    public DateTime? CanceladaEm { get; set; }
    public long? CanceladaPor { get; set; }

    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Usuario? Responsavel { get; set; }
}
