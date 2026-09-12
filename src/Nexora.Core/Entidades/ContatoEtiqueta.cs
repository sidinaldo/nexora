namespace Nexora.Core.Entidades;

/// <summary>Uma etiqueta colada num contato.
///
/// ===================== POR QUE A LIGACAO DEMOROU A NASCER =====================
/// A issue #4 criou o VOCABULARIO — dava para cadastrar "Urgente" e nao dava para marcar ninguem.
/// O bloco parou ali de proposito, esperando uma resposta: o card do funil e a PESSOA ou a
/// NEGOCIACAO?
///
/// A resposta veio, e e a negociacao. Mas `negociacoes` so nasce depois, e esta tabela nao precisa
/// esperar por ela — porque "Revendedor" e "VIP" sao da PESSOA, e continuam sendo quando a
/// negociacao existir. O que espera e a outra metade: `negociacoes_etiquetas`, para "Urgente", que
/// e do NEGOCIO.
///
/// Hoje os tres cards do produto sao a MESMA linha de `contatos` — `uq_conversas_contato` garante
/// uma conversa por contato —, entao marcar pela caixa, pelo quadro ou pela tela de contato e a
/// mesma operacao. A separacao entre etiqueta de pessoa e de negocio so passa a existir com o E4.
/// ==============================================================================
///
/// ===================== A LINHA E A RELACAO =====================
/// Sem `id` proprio: a chave e (contato_id, etiqueta_id), como em `FeriadoIgnorado`. Ela ja impede
/// marcar a mesma etiqueta duas vezes no mesmo contato, sem indice extra — e desmarcar e apagar a
/// linha.
///
/// `empresa_id` viaja junto, denormalizado. Nao e redundancia: e ele que permite as duas FKs serem
/// COMPOSTAS contra `(id, empresa_id)` dos dois lados, e e isso que faz o BANCO recusar colar uma
/// etiqueta da empresa A num contato da empresa B. O filtro de consulta protege leitura, nao
/// escrita.
/// ===============================================================</summary>
public class ContatoEtiqueta : IEntidadeCriada
{
    public long EmpresaId { get; set; }
    public long ContatoId { get; set; }
    public long EtiquetaId { get; set; }

    /// <summary>Quando a etiqueta foi colada. Nao aparece em tela hoje; existe porque "desde
    /// quando este cliente e VIP?" e uma pergunta que aparece sozinha depois, e a linha nao tem
    /// como responder retroativamente se ninguem anotou na hora.</summary>
    public DateTime CriadoEm { get; set; }

    /// <summary>Quem colou. NULO quando nao havia sessao — semente, migracao, script. Mesmo
    /// idioma de `Etiqueta.CriadoPor`: `IContextoEmpresa.UsuarioId` devolve 0 fora de requisicao
    /// autenticada, e gravar 0 criaria FK apontando para usuario inexistente.</summary>
    public long? CriadoPor { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Etiqueta Etiqueta { get; set; } = null!;
}
