namespace Nexora.Core.Entidades;

/// <summary>O lead / cliente. DADO FRIO: cadastro, origem e posicao no funil.
///
/// Separado de <see cref="Conversa"/> (dado quente) de proposito: cada mensagem que entra ou
/// sai escreve na conversa, e o kanban le esta tabela o tempo todo. Juntar as duas faria a
/// escrita do chat competir com a leitura do quadro.</summary>
public class Contato : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>Nunca vazio. Mensagem de numero desconhecido CRIA contato (ver Mensagem), e
    /// quando o pushName do webhook nao vier, a aplicacao preenche com o proprio telefone
    /// formatado.</summary>
    public string Nome { get; set; } = null!;

    /// <summary>Telefone canonicalizado COM DDI, so digitos: 5584988887777.
    ///
    /// ESTA E A COLUNA MAIS CRITICA DO SCHEMA. O cadastro digita "(84) 98888-7777" e o
    /// WhatsApp entrega "5584988887777@s.whatsapp.net". Se os dois lados nao canonicalizarem
    /// igual, a mensagem recebida nao casa com ninguem e some SEM ERRO NO LOG — sem conversa,
    /// sem semaforo, sem nada. A canonicalizacao (e as variantes do nono digito) chega junto
    /// com a integracao da Evolution.
    ///
    /// LIMITE CONHECIDO DA FASE 1: um telefone por contato. Cliente com dois numeros (comum
    /// em PJ) vira contato duplicado. Quando doer, extrair para `telefones_contato`
    /// (empresa_id, contato_id, telefone, principal) e mover o indice unico para la.</summary>
    public string Telefone { get; set; } = null!;

    public string? Email { get; set; }

    public OrigemLead Origem { get; set; } = OrigemLead.Whatsapp;

    /// <summary>Texto livre da campanha ou anuncio. NAO e atribuicao de custo — isso e fase 2
    /// e depende da Cloud API.</summary>
    public string? OrigemDetalhe { get; set; }

    // ===================== O CONTATO SAIU DO FUNIL (E4e/4) =====================
    // `etapa_id`, `ordem_kanban`, `valor`, `ganho_em`, `perdido_em` e `motivo_perda` moravam
    // aqui. Todas as seis descreviam o NEGOCIO, nao a pessoa, e e por isso que a mesma pessoa
    // nunca pode ter dois negocios: a coluna so cabe um.
    //
    // Elas agora sao de `negociacoes`, com a mesma finalidade e um dono a mais — `ordem_kanban`
    // com a mesma justificativa de sempre (fracionario, `numeric` sem escala fixa, para o ponto
    // medio nunca faltar ao arrastar).
    //
    // ⚠️ O QUE ISTO CUSTA: nao ha mais coluna dizendo "onde esta este contato". Quem quiser
    // saber pergunta a negociacao dele, e a resposta pode ser NENHUMA — pessoa na caixa de
    // entrada sem negocio aberto e um estado legitimo desde este bloco. Todo codigo que assumir
    // "todo contato tem etapa" esta assumindo algo que o banco parou de garantir.
    // ==========================================================================

    /// <summary>Token de concorrência OTIMISTA, mapeado no `xmin` do Postgres — a coluna de
    /// sistema que guarda a transação que escreveu a linha pela última vez.
    ///
    /// ===================== POR QUE `xmin` E NÃO UMA COLUNA PRÓPRIA =====================
    /// Uma coluna `versao` exigiria que TODO caminho de escrita lembrasse de incrementá-la, e
    /// bastaria um esquecer para o controle sumir em silêncio — exatamente o problema que o
    /// InterceptorAuditoria existe para evitar em `atualizado_em`. O `xmin` já existe em toda
    /// linha, é mantido pelo próprio Postgres e não custa espaço nem escrita extra.
    ///
    /// NÃO gera coluna em migration: é coluna de sistema, e o provedor do Npgsql sabe disso.
    /// ==================================================================================</summary>
    public uint Versao { get; set; }

    public long? ResponsavelId { get; set; }

    public string? Observacoes { get; set; }

    /// <summary>LGPD: anonimizar zera a PII e preserva o historico. NAO ha delete fisico nem
    /// soft delete. O indice unico de telefone e parcial (so contatos vivos) justamente para
    /// que o segundo contato anonimizado da empresa nao colida com o primeiro.</summary>
    public DateTime? AnonimizadoEm { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    /// <summary>As etiquetas coladas neste contato.
    ///
    /// ⚠️ A NAVEGACAO NAO E CONVENIENCIA AQUI, e obrigatoria. A projecao da caixa
    /// (`ServicoCaixa.Resumo`) e uma `static readonly Expression`, e citar `db` dentro dela e
    /// CS9105 — campo de construtor primario nao pode aparecer num inicializador estatico. A
    /// unica forma de a lista da caixa mostrar os chips e por aqui, exatamente como
    /// `c.Contato.Vendas.Count(...)` ja faz na mesma expressao.</summary>
    public ICollection<ContatoEtiqueta> Etiquetas { get; set; } = [];

    /// <summary>Os negocios deste contato (E4). Existe pelo mesmo motivo de `Vendas`: perguntas
    /// sobre o conjunto de negocios da pessoa precisam caber DENTRO da consulta do quadro, e uma
    /// `Expression` estatica nao pode citar `db`.</summary>
    public ICollection<Negociacao> Negociacoes { get; set; } = [];

    public Empresa Empresa { get; set; } = null!;
    public Usuario? Responsavel { get; set; }
}
