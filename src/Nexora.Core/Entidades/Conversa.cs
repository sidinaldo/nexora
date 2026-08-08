namespace Nexora.Core.Entidades;

/// <summary>A conversa de WhatsApp com um contato. DADO QUENTE: cada mensagem escreve aqui.
///
/// 1:1 com contato na fase 1 (uq_conversas_contato). Diferente do Recupera, onde a thread era
/// por devedor e o ticket por divida, aqui contato = conversa e ponto.</summary>
public class Conversa : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }
    public long ContatoId { get; set; }
    public long ConexaoId { get; set; }

    public StatusConversa Status { get; set; } = StatusConversa.Aberta;

    /// <summary>ATRIBUICAO, NAO FILA. Dono opcional: sem dono = "Aguardando", com dono =
    /// "Atendendo". Responder sem dono atribui automaticamente; assumir conversa de outro
    /// devolve 409. E o que impede dois vendedores responderem o mesmo cliente por cima.</summary>
    public long? ResponsavelId { get; set; }
    public DateTime? AtribuidoEm { get; set; }

    /// <summary>===== O CORACAO DO SEMAFORO =====
    ///
    /// Instante da primeira mensagem de ENTRADA ainda nao respondida.
    ///   entrada chega -> se estiver NULL, grava agora
    ///   saida sai     -> volta para NULL
    ///
    /// MATERIALIZADA, nao calculada. O Recupera calcula o equivalente com max(id) por
    /// conversa a cada leitura; aqui tres features leem isto o tempo todo (semaforo, Meu Dia
    /// e um dos quatro numeros do dashboard) e uma coluna indexada resolve as tres — calculo
    /// dinamico nao indexa.
    ///
    /// O PRECO DA ESCOLHA: valor calculado nao desincroniza, valor materializado sim. O INSERT
    /// da mensagem e o UPDATE desta coluna PRECISAM estar na MESMA transacao. O webhook do
    /// Recupera faz o contrario (SQL cru + SaveChanges separado, sem transacao) — e o padrao a
    /// nao copiar. A regra de manutencao chega no bloco da Evolution; ha uma consulta de
    /// reconciliacao no rodape do docs/SCHEMA-NEXORA.sql para usar como sanity check.</summary>
    public DateTime? AguardandoDesde { get; set; }

    /// <summary>NOT NULL de proposito. No Postgres, ORDER BY ... DESC e NULLS FIRST: com a
    /// coluna anulavel, conversa recem-criada iria para o TOPO da caixa, e o predicado de
    /// cursor `(coluna, id) &lt; (:em, :id)` devolveria NULL para ela, sumindo da paginacao.
    /// A conversa sempre nasce colada a uma mensagem, entao "agora" e honesto.</summary>
    public DateTime UltimaMensagemEm { get; set; }

    public DirecaoMensagem? UltimaMensagemDirecao { get; set; }

    /// <summary>Primeiros ~120 caracteres, para a previa da lista sem carregar a thread.</summary>
    public string? UltimaMensagemPrevia { get; set; }

    /// <summary>Contador com CHECK &gt;= 0 no banco: contador incrementado e zerado pela
    /// aplicacao sempre fica negativo uma vez, e estourar no INSERT e melhor que exibir
    /// "-3 nao lidas".</summary>
    public int NaoLidas { get; set; }

    /// <summary>===================== O CANAL DESTE CICLO (NEG-3) =====================
    ///
    /// O codigo de campanha detectado numa mensagem RECEBIDA desde a ultima venda concluida.
    ///
    /// ⚠️ VIVE NA CONVERSA, e nao no contato, de proposito. `contatos.origem` e a campanha que
    /// trouxe a pessoa da PRIMEIRA vez e nao se reescreve (NEG-1) — o cliente que voltou pelo
    /// panfleto de julho continua sendo o lead do Instagram de marco. Mas a compra de agosto tem
    /// a campanha DELA, e era ela que nao era guardada em lugar nenhum.
    ///
    /// Preenchido pelo webhook (inclusive em contato que ja existe), copiado para a venda no
    /// fechamento e LIMPO ao concluir. Proxima volta, proximo ciclo.
    /// ==========================================================================</summary>
    public long? CanalCicloId { get; set; }

    public DateTime? ResolvidoEm { get; set; }
    public long? ResolvidoPor { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Conexao Conexao { get; set; } = null!;
    public CanalCaptacao? CanalCiclo { get; set; }
    public Usuario? Responsavel { get; set; }
    public Usuario? UsuarioResolveu { get; set; }
}
