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

    public long EtapaId { get; set; }

    /// <summary>Posicao do card dentro da coluna do kanban. FRACIONARIO, nao inteiro:
    /// arrastar um card entre dois outros e `(anterior + posterior) / 2`, um UPDATE de uma
    /// linha so, em vez de renumerar a coluna inteira.
    ///
    /// numeric SEM escala fixa de proposito: com numeric(18,6), inserir sempre no meio do
    /// mesmo par esgota a escala em ~19 movimentos (2^-19 ~ 1e-6) e dois cards colidem na
    /// mesma posicao. numeric puro vai a 16383 casas — o ponto medio nunca falta.</summary>
    public decimal OrdemKanban { get; set; }

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

    /// <summary>Valor estimado do negocio.</summary>
    public decimal? Valor { get; set; }

    /// <summary>Marcos terminais, mutuamente exclusivos (ck_contatos_terminal). Coluna em vez
    /// de tabela de historico porque na fase 1 o dashboard so precisa de "quando ganhou" e
    /// "quando perdeu"; historico de movimentacao entre etapas e fase 2.</summary>
    public DateTime? GanhoEm { get; set; }
    public DateTime? PerdidoEm { get; set; }
    public string? MotivoPerda { get; set; }

    public string? Observacoes { get; set; }

    /// <summary>LGPD: anonimizar zera a PII e preserva o historico. NAO ha delete fisico nem
    /// soft delete. O indice unico de telefone e parcial (so contatos vivos) justamente para
    /// que o segundo contato anonimizado da empresa nao colida com o primeiro.</summary>
    public DateTime? AnonimizadoEm { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public EtapaFunil Etapa { get; set; } = null!;
    public Usuario? Responsavel { get; set; }
}
