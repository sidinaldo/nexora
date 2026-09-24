using System.Text.Json.Serialization;
using Nexora.Core.Entidades;

namespace Nexora.Core.Servicos;

/// <summary>Uma linha da lista de contatos.
///
/// `GanhoEm` e `PerdidoEm` vão como TIMESTAMP, não como rótulo "ganho"/"perdido": o cliente
/// precisa da data para exibir "fechado há 3 dias", e um rótulo pronto obrigaria o servidor a
/// decidir formatação. Mesmo princípio do `AguardandoDesde` no semáforo.</summary>
public record ContatoResumo(
    long Id,
    string Nome,
    string Telefone,
    string? Email,
    string Origem,
    /// <summary>⚠️ NULOS desde o E6, e pelo mesmo motivo da caixa: contato sem negociação não
    /// está em funil nenhum, e esse é o estado de todo lead que acaba de chegar.
    ///
    /// `OrdemKanban` continua não-nulo com zero porque é chave de ordenação, não informação de
    /// tela — zero ali ordena, não mente.</summary>
    long? EtapaId,
    /// <summary>A etapa do negócio VIGENTE — o aberto, ou o mais recente quando não há nenhum.
    ///
    /// ⚠️ A LISTA NÃO USA MAIS ESTE CAMPO, e o motivo está em `Negocios` logo abaixo: ele dá UMA
    /// resposta para uma pergunta que passou a ter várias. Continua aqui porque `PipelineId` do
    /// detalhe é derivado de `EtapaId`, e porque quem integra pela API já lê os dois.</summary>
    string? EtapaNome,
    /// <summary>ONDE esta pessoa está, um item por negócio vivo (`aberta` ou `ganha`).
    ///
    /// ⚠️ ELA EXISTE PORQUE A COLUNA "ETAPA" DA LISTA MENTIA POR OMISSÃO. Havia um par
    /// `EtapaId`/`EtapaNome` só, herdado de quando `contatos.etapa_id` existia e um contato ERA
    /// um card. Com a mesma pessoa em três funis, a projeção escolhia uma das três — as abertas
    /// primeiro, depois o maior id — e mostrava o nome da etapa sem dizer de qual funil.
    ///
    /// Relatado assim: "por que na lista de contato Ysia ficou com a etiqueta de impedimento?
    /// esse contato está em 3 funil diferente com etiquetas diferentes". "Impedimento" era uma
    /// ETAPA, do funil Teste, e ganhou a disputa por ter o id mais alto. Se ela tivesse aberto o
    /// negócio do Teste primeiro, a tela mostraria "Separado", de Vendas. Escolha arbitrária
    /// apresentada como resposta.
    ///
    /// Mesma ordem de `ContatoDetalhe.Negocios`: os abertos primeiro — é o que se trabalha hoje
    /// —, depois os ganhos esperando conclusão; dentro de cada grupo, pela ordem do funil no
    /// menu. NÃO por id: "id não é relógio" neste banco.</summary>
    IReadOnlyList<NegocioNaLista> Negocios,
    decimal OrdemKanban,
    long? ResponsavelId,
    string? ResponsavelNome,
    decimal? Valor,
    DateTime? GanhoEm,
    DateTime? PerdidoEm,
    /// <summary>O selo — "em aberto", "venda fechada", "perdido", "sem negócio" —, decidido AQUI
    /// e só desenhado pela tela. Sai das mesmas expressões que filtram e contam as abas: ver
    /// `RegrasNegociacao.Situacao`. `GanhoEm`/`PerdidoEm` continuam para a DATA, não para o rótulo.</summary>
    [property: JsonConverter(typeof(EnumMinusculo<SituacaoContato>))] SituacaoContato Situacao,
    DateTime CriadoEm,
    long? ConversaId,
    DateTime? AguardandoDesde,
    int NaoLidas);

/// <summary>O detalhe: tudo do contato mais a conversa e os lembretes dele.
///
/// Vem numa chamada só porque a tela de detalhe mostra as três coisas juntas — três requisições
/// para montar uma tela é latência que o vendedor sente ao abrir cada card.</summary>
/// <summary>Um negócio vivo, do tamanho de uma LINHA de lista.
///
/// ⚠️ É a versão leve de `NegocioDoContato`, e a diferença é deliberada: aquela carrega `Versao`
/// e as etiquetas porque a tela do contato MOVE o negócio e edita a etiqueta dali. A linha da
/// lista só desenha um chip, e trazer `xmin` e as etiquetas de cada negócio de 30 contatos seria
/// pagar por uma tela que não existe.</summary>
public record NegocioNaLista(
    long Id, long PipelineId, string PipelineNome,
    long EtapaId, string EtapaNome, string Status, decimal? Valor);

/// <summary>Um funil onde esta pessoa AINDA NÃO TEM card — os únicos em que "Abrir negociação"
/// pode dar certo. Vem pronto do servidor, na ordem do menu, e a tela só o desenha no seletor.
///
/// ⚠️ ERA CALCULADO NO PAINEL, de duas fontes: a caixa subtraía `funisOcupados` da lista do menu,
/// e a tela do contato filtrava os negócios que já tinha carregado. Mesma regra, duas cópias — e
/// a versão estreita (só `aberta`) já tinha escapado para uma delas uma vez.</summary>
public record FunilLivre(long Id, string Nome);

public record ContatoDetalhe(
    ContatoResumo Contato,
    /// <summary>Em qual FUNIL o contato está — derivado da etapa dele.
    ///
    /// ⚠️ Existe porque a tela precisa listar as etapas DESTE funil no seletor, e o resumo só
    /// traz `EtapaId`. Sem ele, a tela não tem como pedir o funil certo e acaba pedindo um
    /// qualquer — foi exatamente o que aconteceu: `quadro(1)` pedia a pipeline de id 1, que só
    /// por acidente é a da primeira empresa. Nas outras, o seletor vinha vazio.
    ///
    /// ⚠️ NULO quando não há negociação (E6). Antes deste bloco a consulta que o produzia
    /// (`PipelineDaEtapaAsync`) recebia a etapa do contato, e com a etapa em zero ela estourava
    /// com "Etapa não encontrada" — a tela do contato daria 500 para todo lead vindo da caixa.</summary>
    long? PipelineId,
    string? OrigemDetalhe,
    string? Observacoes,
    string? MotivoPerda,
    DateTime? AnonimizadoEm,
    DateTime? UltimaMensagemEm,
    /// <summary>===================== A CAMPANHA DESTE CICLO (NEG-3) =====================
    ///
    /// O nome do canal detectado numa mensagem RECEBIDA desde a última venda concluída, ou nulo.
    ///
    /// ⚠️ NÃO É `OrigemDetalhe`, e a diferença é o bloco inteiro. `OrigemDetalhe` é a campanha
    /// que trouxe a pessoa da PRIMEIRA vez e está congelada desde o cadastro; esta é a que a
    /// trouxe de volta AGORA. As duas aparecem juntas na tela, com rótulos diferentes.
    ///
    /// Sem isto o registro do ciclo era invisível: ficava gravado no banco, entrava no
    /// relatório depois da venda, e nada na tela dizia que existia. Quem escaneou o QR e abriu
    /// a negociação concluía — corretamente, pelo que via — que nada tinha sido registrado.
    /// ==========================================================================</summary>
    string? CanalDoCiclo,
    /// <summary>===================== OS NEGÓCIOS VIVOS DESTA PESSOA =====================
    /// Abertos e ganhos — os que aparecem em algum quadro. Perdidos e concluídos ficam no
    /// histórico (`Vendas` e a linha do tempo): a pergunta desta lista é "onde esta pessoa está
    /// AGORA", e misturar o que já acabou responde outra.
    ///
    /// ⚠️ ELA EXISTE PORQUE A TELA MOSTRAVA UM SÓ, E ISSO DEIXOU DE TER RESPOSTA. O bloco
    /// "Negociação" tinha UM seletor de etapa, UMA situação e UM conjunto de ações — escrito
    /// quando um contato era um card. Com a mesma pessoa em Vendas e em Pós-venda não existe
    /// resposta para "qual etapa o select mostra" nem "qual negócio ele move".
    ///
    /// Relatado como pergunta: "se no detalhe do contato tivesse uma lista de fases/etiquetas
    /// onde o respectivo contato está?".</summary>
    IReadOnlyList<NegocioDoContato> Negocios,
    IReadOnlyList<LembreteDto> Lembretes,
    /// <summary>Onde "Abrir negociação" pode dar certo — ver `FunilLivre`. Vazia para o
    /// anonimizado: a API recusa abrir negócio para ele, e o seletor não deve oferecer.</summary>
    IReadOnlyList<FunilLivre> FunisDisponiveis,
    /// <summary>DE ONDE ESTA PESSOA VEIO, e o que a Meta ficou sabendo (INT-4). Nulo quando não há
    /// rastro — o caso da maioria, e a tela simplesmente não mostra o bloco.</summary>
    JornadaDoAnuncio? Jornada);

/// <summary>A JORNADA: o clique que trouxe a pessoa, e os eventos que saíram daqui por causa dela.
///
/// ===================== O QUE ELA **NÃO** TEM: IP E USER-AGENT =====================
/// Eles existem para a Meta casar quem clicou com quem virou lead, e são dado pessoal de alguém que
/// nem é cliente ainda. Na tela não servem para nada — o vendedor não decide nada com um IP — e
/// exibi-los seria expor dado pessoal a todo usuário do tenant por estética.
///
/// Os identificadores de clique também ficam fora: em vez do `fbc` cru, um booleano que responde a
/// única pergunta que a tela faz ("veio de anúncio pago?").
/// ================================================================================
///
/// `origem` e `origem_detalhe` NÃO se repetem aqui: a tela já os mostra em cima, e a jornada é o que
/// só ela sabe — a campanha da URL, a página de entrada, e o estado dos eventos.</summary>
public record JornadaDoAnuncio(
    /// <summary>`formulario_site`, `anuncio_whatsapp` ou `importacao`.
    ///
    /// ⚠️ O ENUM, e não `ToString().ToLowerInvariant()` — que daria `formulariosite`. Foi o que o
    /// teste pegou. `EnumMinusculo` usa a política snake_case, a MESMA que o Npgsql grava no enum
    /// nativo do Postgres: a tela, o banco e o log passam a dizer a mesma palavra.</summary>
    [property: JsonConverter(typeof(EnumMinusculo<FonteRastreio>))] FonteRastreio Fonte,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? UtmContent,
    string? UtmTerm,
    string? Pagina,
    string? Referencia,

    /// <summary>Veio de anúncio PAGO — havia `fbclid`, `gclid` ou `ttclid` no clique. É a pergunta
    /// que a tela faz; o identificador em si não interessa a ninguém que esteja olhando.</summary>
    bool DeAnuncioPago,

    DateTime OcorridoEm,

    /// <summary>O que saiu daqui para a plataforma por causa desta pessoa. Vazia quando a empresa
    /// não conectou anúncio — e é isso que a tela diz, em vez de "nenhum evento".</summary>
    IReadOnlyList<EventoDaJornada> Eventos);

/// <summary>Um evento da jornada, como a tela do contato o mostra.</summary>
public record EventoDaJornada(
    /// <summary>`lead` ou `compra`.</summary>
    string Tipo,
    /// <summary>`pendente`, `entregue`, `falhou`, `expirado` ou `cancelado`.</summary>
    string Status,
    DateTime? EntregueEm,
    string? Erro);

/// <summary>Uma linha da lista de negócios do contato.
///
/// ⚠️ `Versao` VAI JUNTO, e não é enfeite: mover a etapa por esta lista usa o mesmo
/// `POST /api/funil/{negociacaoId}/mover` do quadro, que compara o `xmin` para detectar que
/// outra pessoa mexeu no card entre a leitura e o clique. Sem ele a tela do contato seria a
/// porta sem trava, e o último a clicar venceria em silêncio.</summary>
public record NegocioDoContato(
    long Id,
    long PipelineId,
    string PipelineNome,
    long EtapaId,
    string EtapaNome,
    decimal? Valor,
    /// <summary>`aberta` ou `ganha`, em minúsculas como todo enum que sai da API.</summary>
    string Status,
    DateTime? GanhaEm,
    uint Versao,
    IReadOnlyList<EtiquetaDto> Etiquetas);

public record NovoContato(
    string Nome,
    string Telefone,
    string? Email = null,
    string? Origem = null,
    string? OrigemDetalhe = null,
    long? EtapaId = null,
    long? ResponsavelId = null,
    decimal? Valor = null,
    string? Observacoes = null);

public record EditarContato(
    string Nome,
    string Telefone,
    string? Email = null,
    string? Origem = null,
    string? OrigemDetalhe = null,
    long? ResponsavelId = null,
    decimal? Valor = null,
    string? Observacoes = null);

/// <summary>Recorte da lista. `Abertos` é o default: contato ganho ou perdido é histórico, e a
/// lista de trabalho do vendedor não deve começar cheia de coisa fechada.</summary>
public enum FiltroContato
{
    Abertos,
    Ganhos,
    Perdidos,
    Todos
}

/// <summary>===================== O QUE O MODAL DE FECHAMENTO PRECISA SABER (NEG-3) =====
///
/// `DetectadoId` é o canal do CICLO — o código que chegou numa mensagem desde a última venda
/// concluída. Vem NULO no caso comum, e o campo aparece sem nada selecionado.
///
/// Uma chamada só, e não duas: pedir a lista de canais num endpoint e o detectado noutro faria a
/// tela abrir com a lista pronta e o pré-selecionado chegando depois — o vendedor veria o campo
/// mudar sozinho debaixo do dedo.
///
/// Só canais ATIVOS entram na lista: campanha encerrada não deve ser oferecida para uma venda de
/// hoje. Mas se o DETECTADO estiver desativado ele vem junto mesmo assim, com `Ativo = false` —
/// desativar acontece depois, e esconder a opção apagaria uma atribuição que o próprio sistema
/// fez.</summary>
public record CanaisDoFechamento(long? DetectadoId, IReadOnlyList<OpcaoCanalFechamento> Canais);

public record OpcaoCanalFechamento(long Id, string Nome, bool Ativo);

/// <summary>Quantos contatos há em CADA aba, com os demais recortes já aplicados.
///
/// ⚠️ ELA RESPONDE "se eu clicar nesta aba agora, quantos vou ver?", e é por isso que a busca, a
/// etapa e o responsável entram na conta. Um "Ganhos 48" fixo enquanto a busca diz "Ysia" seria
/// um número que não corresponde a nenhuma tela alcançável.
///
/// ⚠️ E É POR ISSO QUE ELA EXISTE. A tela abria em "Em aberto", e quem fechava todos os negócios
/// saía dessa aba sem nenhum sinal — relatado assim: "fechei os cards da Ysia em todos os funis e
/// o contato sumiu da lista". Ela não tinha sumido; estava em "Ganhos", uma aba ao lado e sem
/// nada apontando para lá.
///
/// `Abertos + Ganhos + Perdidos == Todos`, sempre: os três recortes são disjuntos e cobrem a
/// base. A tela mostra os quatro números lado a lado, então qualquer buraco aparece somado.</summary>
public record ContagemPorSituacao(int Abertos, int Ganhos, int Perdidos, int Todos);

/// <summary>A página da lista de contatos, com a contagem das abas junto.
///
/// Os quatro primeiros campos são os de `Pagina&lt;T&gt;`, de propósito: quem já lia `total` e
/// `itens` continua lendo igual. `Pagina&lt;T&gt;` em si não ganhou o campo porque contagem por
/// situação é pergunta de contato — nenhuma outra lista do painel tem abas assim.</summary>
public record PaginaContatos(
    int Total, int NumeroPagina, int Tamanho,
    IReadOnlyList<ContatoResumo> Itens,
    ContagemPorSituacao Contagens);

public interface IServicoContatos
{
    /// <summary>A lista, paginada por OFFSET com total — não por cursor.
    ///
    /// Aqui offset é seguro, e cursor seria pior: a ordenação é por nome (ou por data de
    /// criação), que NÃO se reordena sozinha entre requisições. O cursor existe na caixa de
    /// entrada porque lá conversa nova sobe para o topo enquanto o vendedor rola. Um contato não
    /// muda de nome sozinho. E o total permite mostrar "142 contatos", que a lista precisa.
    ///
    /// Filtro, busca, contagem e corte acontecem TODOS no SQL.</summary>
    Task<PaginaContatos> ListarAsync(
        FiltroContato filtro, string? busca, long? etapaId, long? responsavelId,
        int pagina, int tamanho, CancellationToken ct);

    Task<ContatoDetalhe> DetalheAsync(long id, CancellationToken ct);

    Task<long> CriarAsync(NovoContato novo, CancellationToken ct);

    Task AtualizarAsync(long id, EditarContato dados, CancellationToken ct);

    /// <summary>=========== A PORTA ÚNICA DO GANHO ===========
    ///
    /// Exige valor. Além de carimbar `ganho_em`, MOVE o card para a etapa de ganho — é o que
    /// permite ao cliente tratar "arrastar para Venda" e "clicar em Venda fechada" como a mesma
    /// operação. O `MoverAsync` recusa a etapa de ganho justamente para forçar tudo por aqui: se
    /// as duas portas escrevessem por rotas diferentes, existiria contato na coluna Venda sem
    /// `ganho_em` e sem `valor` — e o dashboard não saberia contá-lo.
    ///
    /// Empresa sem etapa marcada `e_ganho` (o índice único permite zero) mantém a etapa atual: o
    /// carimbo é o que importa para o dashboard, a coluna é conveniência visual.</summary>
    /// <summary>NEG-3: `canalId` e o canal de captacao informado no fechamento. NULO cai para o
    /// canal do CICLO (`conversas.canal_ciclo_id`) e, sem ele, para nulo — nunca para o canal do
    /// cadastro original do contato.</summary>
    /// <summary>⚠️ `negociacaoId` DIZ QUAL NEGOCIO FECHAR, e ele passou a ser necessario.
    ///
    /// Sem ele o servico escolhe "o negocio aberto do contato" — o mais recente. Enquanto uma
    /// pessoa tinha um negocio so, isso era uma resposta; com a mesma pessoa em Vendas e em
    /// Pos-venda, virou um sorteio, e a tela do contato (que agora LISTA os dois) teria um botao
    /// por linha fechando o negocio de outra linha.
    ///
    /// NULO mantem o comportamento antigo, e e o que o quadro e a caixa usam: la o gesto ja
    /// nasce de um card, e o card... tambem sabe seu id. Ficam como estao por ora — ver o teste
    /// `FECHAR_PELA_LINHA_FECHA_AQUELE_NEGOCIO`, que e quem descreve a diferenca.</summary>
    Task MarcarGanhoAsync(
        long id, decimal valor, long? canalId, long? negociacaoId, CancellationToken ct);

    /// <summary>Os canais que o modal de fechamento oferece, e qual deles já foi detectado.</summary>
    Task<CanaisDoFechamento> CanaisDoFechamentoAsync(long contatoId, CancellationToken ct);

    /// <summary>Exige motivo. NÃO muda de etapa: o índice parcial ix_contatos_kanban já filtra
    /// `perdido_em IS NULL`, então o card sai do quadro sozinho, e preservar a etapa registra
    /// ONDE a negociação morreu — que é a informação útil depois.</summary>
    /// <summary>`negociacaoId` pelo mesmo motivo do ganho: dizer QUAL negocio se perdeu.</summary>
    Task MarcarPerdidoAsync(
        long id, string motivo, long? negociacaoId, CancellationToken ct);

    /// <summary>Desfaz ganho ou perda. PRESERVA o `valor`: ele é a estimativa do negócio, não o
    /// registro da venda, e apagá-lo obrigaria o vendedor a digitar de novo ao reabrir.</summary>
    /// <summary>===================== UM GESTO SÓ: COMEÇAR UM NEGÓCIO =====================
    /// Era `ReabrirAsync`, e o E6 mostrou que "reabrir" e "abrir" sempre foram a mesma coisa vista
    /// de dois pontos: o vendedor quer começar a vender para esta pessoa. O que muda é só o que o
    /// sistema encontra pela frente.
    ///
    /// Dois métodos para isso seriam duas portas para o mesmo fato — exatamente a forma do defeito
    /// que o bloco E4 inteiro veio desmontar.
    ///
    /// `pipelineId` NULO deixa o sistema escolher, e a escolha tem precedência:
    ///   1. há negócio PERDIDO   → revive aquele, na etapa onde ele morreu (era o `reabrir`);
    ///   2. há negócio GANHO     → linha nova no início do funil DAQUELE negócio;
    ///   3. não há negócio nenhum→ linha nova no início do funil PADRÃO.
    ///
    /// `pipelineId` INFORMADO cria linha nova ali, sempre — inclusive havendo perda para reviver.
    /// Quem escolheu o funil está dizendo para onde quer ir, e ressuscitar a perda noutro lugar
    /// contrariaria a escolha em silêncio.
    ///
    /// Recusa com 409 se já houver negócio em aberto: dois cards da mesma pessoa no mesmo funil
    /// não é um estado que alguém pediu.</summary>
    Task AbrirNegociacaoAsync(long id, long? pipelineId, CancellationToken ct);

    /// <summary>LGPD: zera a PII e preserva o histórico. Sem delete físico, sem soft delete —
    /// conversa, mensagens, lembretes, etapa e valor continuam de pé.</summary>
    Task AnonimizarAsync(long id, CancellationToken ct);
}
