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

    /// <summary>A pipeline a que esta etapa pertence.
    ///
    /// ⚠️ ELA MUDA O ESCOPO DE TODAS AS INVARIANTES DE ETAPA. `uq_etapas_ordem` e `uq_etapas_ganho`
    /// eram por EMPRESA; passaram a ser por PIPELINE. Sem isso a segunda pipeline nao consegue ter
    /// a propria etapa 1 nem a propria etapa de ganho — ela herdaria as restricoes da primeira e
    /// falharia ao ser criada, com erro de indice unico.
    ///
    /// `empresa_id` continua na etapa, denormalizado, porque toda consulta filtra por tenant e a
    /// convencao do schema e que ele seja a PRIMEIRA coluna de indice composto. A FK composta
    /// `(pipeline_id, empresa_id)` e o que impede uma etapa apontar para pipeline de outra
    /// empresa — o filtro de consulta protege leitura, nao escrita.</summary>
    public long PipelineId { get; set; }

    public string Nome { get; set; } = null!;
    public short Ordem { get; set; }
    public string Cor { get; set; } = "#2F5D3A";

    /// <summary>Marca a etapa terminal de ganho. Uma so por PIPELINE (uq_etapas_ganho).
    ///
    /// O dashboard e o calculo de conversao dependem desta flag, nao do nome nem da ordem:
    /// e o que permite a empresa renomear "Venda" para "Fechado" sem quebrar a logica.</summary>
    public bool EGanho { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Pipeline Pipeline { get; set; } = null!;
}
