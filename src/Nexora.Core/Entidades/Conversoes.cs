namespace Nexora.Core.Entidades;

/// <summary>DE ONDE A PESSOA VEIO — o clique que virou este contato (INT-4).
///
/// ===================== POR QUE UMA TABELA, E NÃO COLUNAS EM `contatos` =====================
/// São quinze campos que só a captação preenche e que quase nenhuma consulta do produto lê. Em
/// `contatos` eles engordariam a linha mais lida do sistema para servir a um caso raro.
///
/// E há a razão que decide: `ip` e `user_agent` são dado pessoal de quem nem é cliente ainda.
/// Numa tabela própria, a anonimização é um `UPDATE` localizado com uma lista curta de colunas —
/// em `contatos` seria mais uma dezena de campos para alguém esquecer.
/// ==========================================================================================
///
/// ===================== UMA LINHA POR CONTATO, E A PRIMEIRA GANHA =====================
/// Índice único em `contato_id`, e a gravação é `ON CONFLICT DO NOTHING`. O `fbc` do clique
/// ORIGINAL é o elo de atribuição: trocá-lo pelo de uma visita posterior quebraria exatamente o
/// que este bloco existe para fazer — dizer qual anúncio trouxe a venda.
///
/// O que é "desta rodada" tem outro lugar: `negociacoes.canal_ciclo_id`.
/// ====================================================================================</summary>
public class RastreioLead
{
    // ===================== OS TETOS MORAM AQUI, E SO AQUI =====================
    // A largura de cada coluna e a regra de truncagem sao a MESMA decisao. Escrever 200 no
    // mapeamento e 200 no normalizador seria a duplicata classica deste projeto: no dia em que um
    // dos dois mudasse, o endpoint publico passaria a estourar "value too long" — e estouraria no
    // formulario do site do cliente, que e o pior lugar possivel.
    //
    // Cabe IPv6 com zona (45) e o User-Agent mais longo que se ve na pratica (512).
    // =========================================================================
    public const int TetoUtm = 200;
    public const int TetoUrl = 1000;
    public const int TetoIp = 45;
    public const int TetoUserAgent = 512;

    /// <summary>Teto de CADA identificador de clique dentro do `jsonb`. Nao ha coluna para
    /// limita-los, entao o limite e da aplicacao — e sem ele o `jsonb` e escrita sem teto numa
    /// rota publica.</summary>
    public const int TetoIdentificador = 300;

    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>Único: ver o bloco acima.</summary>
    public long ContatoId { get; set; }

    public FonteRastreio Fonte { get; set; }

    // ===================== CAMPANHA: COLUNA, PORQUE SE FILTRA =====================
    // `utm_campaign` é o GROUP BY de "qual campanha trouxe cliente", e agrupar por chave de jsonb
    // é o tipo de consulta que fica lenta sem ninguém perceber.
    // =============================================================================
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmContent { get; set; }
    public string? UtmTerm { get; set; }

    /// <summary>A URL onde a pessoa estava quando enviou. Vai para a Meta como
    /// `event_source_url`, que é obrigatório em evento de site.</summary>
    public string? Pagina { get; set; }

    /// <summary>O `document.referrer` — de onde ela chegou ANTES da nossa página.</summary>
    public string? Referencia { get; set; }

    public long? FormularioId { get; set; }

    /// <summary>OS IDENTIFICADORES DE CLIQUE: `fbclid`, `fbp`, `fbc`, `ctwa_clid`, `gclid`,
    /// `ttclid`.
    ///
    /// ===================== jsonb PARA O QUE SÓ SE ECOA =====================
    /// Nenhum deles aparece num `WHERE`: são lidos uma vez, para UM contato, na hora de montar o
    /// evento. E o conjunto é definido pela plataforma, não por nós — a Meta inventou o
    /// `ctwa_clid` depois do `fbclid`, o Google tem `gclid`, `gbraid` e `wbraid`. Uma coluna por
    /// parâmetro que um terceiro inventa é uma migração cujo cronograma não é nosso.
    ///
    /// Precedente no projeto: `mensagens.payload_raw`, `auditoria.alteracoes`,
    /// `entregas_webhook.payload`. Como eles, é `string` aqui e jsonb no banco.
    /// ======================================================================</summary>
    public string Identificadores { get; set; } = "{}";

    // ===================== DADO PESSOAL: A META PRECISA, O VENDEDOR NÃO =====================
    // Vão para o `user_data` do evento (em claro — a Meta NÃO os quer hasheados) e não aparecem
    // em tela nenhuma. São a primeira coisa que a anonimização apaga.
    // =======================================================================================
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>O id que o NAVEGADOR usou no evento do pixel. Reaproveitado no evento do servidor
    /// para a Meta reconhecer que os dois são o mesmo fato — é o que impede o lead de ser contado
    /// duas vezes. Nulo quando o site não tem pixel.</summary>
    public Guid? EventoId { get; set; }

    /// <summary>Quando o clique aconteceu — não quando a linha foi gravada. É o `event_time`, e a
    /// Meta recusa evento com mais de 7 dias.</summary>
    public DateTime OcorridoEm { get; set; }

    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
}

/// <summary>A CREDENCIAL DA PLATAFORMA DE ANÚNCIO, por empresa (INT-4).
///
/// ===================== O TOKEN É DELE, NÃO NOSSO =====================
/// É o pixel do cliente. Um token nosso não conseguiria enviar evento para a conta de anúncio
/// dele — nem deveria. Ele cola o que gerou no Gerenciador de Eventos, e o Nexora usa.
/// =====================================================================
///
/// Chave `(empresa_id, plataforma)`, e não uma por empresa: a segunda credencial (Google, TikTok)
/// é prevista, então a chave nasce composta e ninguém mexe no schema quando ela chegar.</summary>
public class CredencialConversao : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public PlataformaConversao Plataforma { get; set; }

    /// <summary>O Pixel ID. É PÚBLICO — vai no HTML do site de quem instalou o pixel —, então não
    /// tem tratamento de segredo nenhum.</summary>
    public string Identificador { get; set; } = null!;

    /// <summary>O token da API de Conversões, em claro.
    ///
    /// Mesma razão de `WebhookSaida.Segredo`: é a credencial com que NÓS chamamos a Graph API, e
    /// um hash não chama nada.
    ///
    /// ⚠️ A DIFERENÇA para aquele: o segredo do webhook nós geramos, então dava para mostrá-lo uma
    /// vez na criação. Este vem colado do Gerenciador de Eventos — não temos o que revelar, e a
    /// API NUNCA o devolve, nem uma vez. A tela mostra sufixo mascarado e a data.
    ///
    /// Nulo enquanto a empresa só preencheu o Pixel ID.</summary>
    public string? Token { get; set; }

    /// <summary>O `test_event_code` do Gerenciador. Com ele, o evento aparece na aba "Eventos de
    /// teste" e NÃO entra na otimização — que é o que separa testar de poluir o pixel do
    /// cliente.</summary>
    public string? CodigoTeste { get; set; }

    public bool Ativo { get; set; } = true;

    public bool EmLead { get; set; } = true;
    public bool EmCompra { get; set; } = true;

    // ===================== O CONSENTIMENTO É DATA E AUTOR, NÃO UM BOOL =====================
    // `WebhookSaida.SomenteIds` é preferência de formato; isto é uma DECLARAÇÃO — "eu tenho base
    // legal no meu site para mandar isto para a Meta". Declaração sem data e sem autor não
    // responde a um pedido da ANPD.
    //
    // Mesma natureza de `empresas.EquipeDispensadaEm`: estado do sistema se deriva, decisão de
    // pessoa se guarda.
    // ======================================================================================
    public DateTime? ConsentimentoEm { get; set; }
    public long? ConsentimentoPor { get; set; }

    /// <summary>Quando o MOTOR desligou sozinho — token recusado pela Meta, por exemplo. Separado
    /// de `Ativo` de propósito: `Ativo` é o interruptor da pessoa, isto é o do sistema.</summary>
    public DateTime? DesativadaEm { get; set; }

    /// <summary>Por quê, em português, para a tela repetir: "A Meta recusou o token. Gere um novo
    /// em Gerenciador de Eventos → Configurações." Sem isto, os eventos parariam em silêncio.</summary>
    public string? DesativadaMotivo { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Usuario? ConsentimentoUsuario { get; set; }

    /// <summary>O PORTÃO, num lugar só — espelha `WebhookSaida.Assina`.
    ///
    /// Sem consentimento declarado não enfileira NADA: guardar o rastro é uma coisa; deixar dado
    /// pessoal hasheado parado numa fila que não pode drenar é outra.</summary>
    public bool PodeEnviar(TipoConversao tipo) =>
        Ativo
        && DesativadaEm is null
        && ConsentimentoEm is not null
        && !string.IsNullOrWhiteSpace(Token)
        && tipo switch
        {
            TipoConversao.Lead => EmLead,
            TipoConversao.Compra => EmCompra,
            _ => false
        };
}

/// <summary>UM EVENTO A CAMINHO DA PLATAFORMA — e a FILA ao mesmo tempo (INT-4).
///
/// Mesma disciplina de `entregas_webhook`: a tabela é a fila, a linha nasce `pendente` com
/// `proxima_tentativa_em`, a rodada drena o que venceu, o resultado fica na própria linha.
///
/// ===================== O QUE ESTA FILA TEM E A DO WEBHOOK NÃO =====================
/// `expira_em` — a Meta recusa evento com `event_time` de mais de 7 dias, e recusa a requisição
/// INTEIRA por causa de um evento velho. Então a expiração é marcada ANTES de tocar a rede.
///
/// `codigo_meta` e `fbtrace_id` — o corpo da resposta É a informação aqui: o `error.code` decide
/// se vale tentar de novo, e o `fbtrace_id` é a primeira coisa que o suporte da Meta pede.
/// =================================================================================</summary>
public class EventoConversao
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public PlataformaConversao Plataforma { get; set; }
    public TipoConversao Tipo { get; set; }

    /// <summary>O id do FATO. Quando o navegador mandou um, é o MESMO — é assim que a Meta sabe
    /// que o evento do pixel e o do servidor são a mesma pessoa, e não duas.</summary>
    public Guid EventoId { get; set; }

    public long ContatoId { get; set; }

    /// <summary>Nulo no `lead`, obrigatório na `compra` — garantido por check no banco.</summary>
    public long? NegociacaoId { get; set; }

    /// <summary>O corpo exato que vai ser enviado, montado na hora em que o fato aconteceu.
    /// Mesma razão de `entregas_webhook.payload`: se o formato mudar entre a criação e a terceira
    /// tentativa, a plataforma receberia dois corpos diferentes com o mesmo id de evento.</summary>
    public string Payload { get; set; } = null!;

    public DateTime OcorridoEm { get; set; }

    /// <summary>`ocorrido_em` + 7 dias. Não é escolha nossa: é a janela da Meta.</summary>
    public DateTime ExpiraEm { get; set; }

    public StatusConversao Status { get; set; } = StatusConversao.Pendente;

    public short Tentativas { get; set; }

    public DateTime? ProximaTentativaEm { get; set; }

    /// <summary>O HTTP. NULL quando nem houve resposta (DNS, timeout, recusa).</summary>
    public int? CodigoResposta { get; set; }

    /// <summary>O `error.code` da Meta. É ELE que classifica: `190` é token morto e não adianta
    /// insistir; `2` é ela mesma fora do ar e adianta.</summary>
    public int? CodigoMeta { get; set; }

    /// <summary>O identificador que a Meta devolve em toda resposta. Guardado porque é o que o
    /// suporte dela pede primeiro quando o cliente abre um chamado.</summary>
    public string? FbtraceId { get; set; }

    public string? Erro { get; set; }

    public DateTime? EntregueEm { get; set; }
    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Negociacao? Negociacao { get; set; }
}
