namespace Nexora.Core.Entidades;

/// <summary>O QUE foi mexido. Guardado como TEXTO, nao como enum nativo do Postgres.
///
/// Os outros enums do sistema sao dominio fechado (`direcao_mensagem`, `status_conexao`) e mudam
/// junto com o produto. Este cresce com cada tabela nova que passe a ser auditada — e enum nativo
/// exigiria uma migration por valor acrescentado, sem ganho: ninguem consulta a trilha filtrando
/// por igualdade de enum, consulta por entidade + id.</summary>
public enum EntidadeAuditada
{
    Contato, Venda, Lembrete, Usuario, Empresa, EtapaFunil, Conexao, Conversa
}

/// <summary>O QUE ACONTECEU, em vocabulario de negocio.
///
/// ⚠️ Nao e derivavel do diff. O interceptor ve `ganho_em` indo de NULL para uma data e nao tem
/// como saber se foi "o vendedor fechou a venda", "a migracao carimbou" ou "o suporte corrigiu".
/// Quem sabe e o SERVICO, e por isso ele declara.</summary>
public enum AcaoAuditoria
{
    Criou, Editou, Moveu, Ganhou, Perdeu, Reabriu, Cancelou, Anonimizou, Desativou, Reativou,
    Resolveu, Concluiu, Atribuiu
}

/// <summary>Quem agiu.
///
/// `Sistema` existe porque o `MotorFollowUp` roda SEM sessao: ele cria lembrete e expira mensagem
/// sem usuario nenhum. Forcar um `usuario_id` ali produziria AUTORIA FALSA — alguem apareceria
/// como autor de uma acao que nao tomou, e a trilha existe justamente para isso nao acontecer.</summary>
public enum AtorAuditoria { Usuario, Sistema }

/// <summary>===================== A TRILHA (AUD-1) =====================
///
/// UMA LINHA POR EVENTO, nao por campo. "Editei o contato" e um fato; virar seis linhas soltas
/// deixaria a linha do tempo ilegivel e faria o leitor remontar na cabeca o que foi um clique so.
/// As mudancas vao juntas no `Alteracoes`.
///
/// ⚠️ ESTA TABELA GUARDA VALOR ANTIGO — e valor antigo de contato e PII. Ver
/// `Auditoria.Mascarar` e o tratamento na anonimizacao: o EVENTO fica, o dado pessoal sai. Sem
/// isso, anonimizar um contato apenas mudaria a PII de tabela, e a anonimizacao nao teria
/// acontecido.
/// ============================================================</summary>
public class Auditoria
{
    /// <summary>O marcador que substitui PII removida. Fixo e reconhecivel: a tela mostra
    /// "(removido)" e quem le entende que houve valor ali, sem saber qual.</summary>
    public const string Mascarado = "[removido]";

    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public EntidadeAuditada Entidade { get; set; }
    public long EntidadeId { get; set; }
    public AcaoAuditoria Acao { get; set; }

    /// <summary>`jsonb`: `{ "valor": { "antes": 5000, "depois": 3000 } }`.
    ///
    /// jsonb e nao json: permite indexar e consultar dentro numa investigacao de suporte, e
    /// normaliza a ordem das chaves — o que torna o conteudo comparavel entre linhas.
    ///
    /// Pode ser `{}` em acao sem diff (reabrir limpa carimbo, mas o que importa e o EVENTO).</summary>
    public string Alteracoes { get; set; } = "{}";

    /// <summary>NULL quando `Ator = Sistema`. Ver o comentario de `AtorAuditoria`.</summary>
    public long? UsuarioId { get; set; }
    public AtorAuditoria Ator { get; set; }

    public DateTime Quando { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Usuario? Usuario { get; set; }
}
