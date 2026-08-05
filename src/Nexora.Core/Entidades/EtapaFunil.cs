namespace Nexora.Core.Entidades;

/// <summary>Uma coluna do funil kanban. Cinco linhas fixas, semeadas no cadastro da empresa.
///
/// E tabela em vez de enum porque o kanban precisa de ordem, rotulo e cor — e porque funil
/// configuravel na fase 2 nao vai exigir migracao de dados.
///
/// Empresa sem etapa e empresa quebrada: o kanban nao renderiza e Contato.EtapaId e NOT NULL.
/// Quem cria empresa semeia as etapas na mesma transacao.</summary>
public class EtapaFunil : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public string Nome { get; set; } = null!;
    public short Ordem { get; set; }
    public string Cor { get; set; } = "#2F5D3A";

    /// <summary>Marca a etapa terminal de ganho. Uma so por empresa (uq_etapas_ganho).
    ///
    /// O dashboard e o calculo de conversao dependem desta flag, nao do nome nem da ordem:
    /// e o que permite a empresa renomear "Venda" para "Fechado" sem quebrar a logica.</summary>
    public bool EGanho { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
