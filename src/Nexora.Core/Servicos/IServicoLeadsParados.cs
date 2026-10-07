namespace Nexora.Core.Servicos;

/// <summary>Um lead que parou de ser trabalhado.
///
/// `EtapaNome` e `PipelineNome` são NULOS quando o contato nunca virou negócio — e isso é o caso
/// mais comum do lead frio, não uma falha de dado. A tela escreve travessão.</summary>
public record LeadParado(
    long ContatoId,
    string Nome,
    string Telefone,
    string Origem,
    long? ResponsavelId,
    string? ResponsavelNome,
    long? NegociacaoId,
    string? PipelineNome,
    string? EtapaNome,
    decimal? Valor,
    DateTime ParadoDesde,
    int DiasParado,
    /// <summary>⚠️ SO A ABA "PERDIDOS" PREENCHE, e e a primeira informacao de quem vai reabrir:
    /// "perdemos por preco" e "perdemos por prazo" levam a abordagens diferentes, e reabrir sem
    /// ler isso e repetir a conversa que falhou. Em `Parados` e sempre nulo — nao houve perda.</summary>
    string? MotivoPerda = null,
    /// <summary>As etiquetas do NEGOCIO da linha, em ordem de nome. Vazia sem negocio.
    ///
    /// ⚠️ FALTAVA, e a tela nao tinha como mostrar o efeito da propria acao em lote: "Aplicar
    /// etiqueta" gravava, e a lista continuava igual — o operador achava que nao tinha feito nada
    /// (relato de uso do LPA-1). E o filtro por etiqueta ja existia sem a coluna que o explica.
    /// O formato e o `EtiquetaDto` do funil: a tela desenha o mesmo chip.</summary>
    IReadOnlyList<EtiquetaDto>? Etiquetas = null);

public record PaginaLeadsParados(IReadOnlyList<LeadParado> Itens, int Total);

/// <summary>O recorte que a tela pede. `Dias` é lista fechada, validada no serviço.
///
/// ⚠️ TODOS OS FILTROS MENOS `Origem` SAO DA NEGOCIACAO, e isso tem uma consequencia que a tela
/// precisa assumir: ligar qualquer um deles ESCONDE quem nao tem negocio aberto. E o certo — o
/// lead que ninguem abriu nao esta em funil nenhum, nao tem etapa, nao tem valor e nao tem
/// etiqueta de negociacao. Fingir que esta exigiria inventar um lugar para ele.
///
/// ⚠️ A ETIQUETA E A DE NEGOCIACAO (`negociacoes_etiquetas`), nao a de contato. O projeto tem as
/// duas de proposito: a de contato vale para todos os negocios da pessoa, a de negociacao gruda
/// num negocio so. Esta tela filtra pela segunda porque e nela que a acao em lote vai escrever —
/// filtrar por uma e marcar a outra faria o operador nunca reencontrar o que acabou de marcar.</summary>
/// <summary>===================== DUAS ABAS, DUAS PERGUNTAS, DOIS EIXOS DE TEMPO =====================
///
/// ⚠️ NAO E UM FILTRO DE STATUS SOBRE A MESMA CONSULTA, e tentar unificar seria o erro. As duas
/// abas cortam o tempo por colunas DIFERENTES:
///
///   `Parados`   → `conversas.ultima_mensagem_em`. "Faz N dias que ninguem se fala" — o negocio
///                 esta aberto e esfriando;
///   `Perdidos`  → `negociacoes.perdida_em`. "Faz N dias que perdemos" — o negocio morreu, e a
///                 pergunta e se vale uma nova tentativa.
///
/// Um lead sem conversa nenhuma entra em `Parados` pela data de criacao; em `Perdidos` isso nao
/// faz sentido — perder exige ter havido negocio.
///
/// ⚠️ AS DUAS SAO DISJUNTAS: `Perdidos` exige NENHUMA negociacao aberta. Sem isso, o contato com
/// uma perda em Vendas e um negocio aberto em Pos-venda apareceria nas duas, e "reabrir em lote"
/// cairia sobre alguem que ja esta sendo trabalhado.</summary>
public enum AbaDeLeads { Parados, Perdidos }

public record FiltroLeadsParados(
    int Dias,
    long? ResponsavelId,
    int Pagina,
    int Tamanho,
    long? PipelineId = null,
    long? EtapaId = null,
    string? Origem = null,
    long? EtiquetaId = null,
    decimal? ValorMin = null,
    decimal? ValorMax = null,
    AbaDeLeads Aba = AbaDeLeads.Parados);

/// <summary>===================== QUEM PAROU DE SER TRABALHADO (LPA-1) =====================
///
/// O lead que chegou, teve uma conversa e esfriou fica no funil para sempre, indistinguível do que
/// foi atendido ontem. Até aqui, descobrir isso exigia abrir contato por contato.
///
/// ⚠️ FASE 1 NÃO ENVIA MENSAGEM NENHUMA, e isso não é falta de tempo: o WhatsApp roda via Baileys,
/// e disparo em massa queima o número do cliente. Quem envia é o vendedor, à mão, pela caixa. O que
/// esta tela faz é ACHAR os leads; agir em lote sobre eles vem nas entregas seguintes.
///
/// ⚠️ "PARADO" É `COALESCE(conversas.ultima_mensagem_em, contatos.criado_em)`, e as duas metades
/// importam:
///
///   · `ultima_mensagem_em` é NOT NULL, materializada e indexada — a única data de interação que o
///     banco já mantém pronta. `aguardando_desde` (a do semáforo) NÃO serve: ela vira nula no
///     instante em que alguém responde, e um lead respondido há seis meses é exatamente o alvo;
///   · `criado_em` cobre quem NUNCA conversou — o lead que entrou por formulário ou importação e
///     ninguém chamou. É o caso mais frio que existe, e sem o COALESCE ele seria o único a nunca
///     aparecer.
/// ============================================================================================</summary>
/// <summary>O que o operador pede: estes contatos, esta data, este titulo.
///
/// ⚠️ NAO HA CAMPO DE MENSAGEM, e a ausencia e a regra da fase. `EnviaMensagem` fica em FALSO no
/// serviço, sem parametro: o WhatsApp roda via Baileys e disparo em massa queima o numero do
/// cliente. Quem envia e o vendedor, a mao, pela caixa — este lembrete e a tarefa que o manda
/// fazer isso.</summary>
public record LembreteEmLote(
    IReadOnlyList<long> ContatoIds,
    DateOnly DataAlvo,
    string Titulo,
    string? Observacao);

/// <summary>===================== A ETIQUETA EM LOTE ADICIONA, NAO SUBSTITUI =====================
///
/// ⚠️ ESTA E A DIFERENCA COM `ServicoEtiquetas.AplicarNaNegociacaoAsync`, e e a razao de este
/// metodo existir em vez de chamar aquele num laco. Lá o corpo e o CONJUNTO FINAL: mandar uma
/// etiqueta remove todas as outras do negocio. Em lote isso apagaria "Urgente" e "Aguardando" de
/// cinquenta cards de uma vez, e o operador que quis marcar "reativacao-out" nao teria como
/// perceber nem como desfazer.
///
/// Aqui e UMA etiqueta e a operacao e somar. Quem ja a tem fica como esta — `criado_em` do
/// primeiro dia, que e o que a metrica de reativados le.
///
/// ⚠️ SO NEGOCIACAO ABERTA. Lead sem negocio nao tem onde colar, e e o lead frio mais comum:
/// entra em `Pulados`, e a tela diz o numero antes de aplicar.</summary>
public record EtiquetaEmLote(IReadOnlyList<long> NegociacaoIds, long EtiquetaId);

/// <summary>===================== REDISTRIBUIR MEXE EM TRES COLUNAS, NAO EM UMA =====================
///
/// ⚠️ ESTE PROJETO TEM TRES COLUNAS DE DONO, e cada uma responde uma pergunta diferente:
///
///   `negociacoes.responsavel_id`  de quem e o NEGOCIO     → relatorios, atribuicao, leads parados
///   `contatos.responsavel_id`     de quem e a PESSOA      → lista de contatos, card do kanban,
///                                                           filtro por responsavel e Meu Dia
///   `conversas.responsavel_id`    quem esta ATENDENDO     → caixa de entrada
///
/// Mexer so na primeira faria a lista de leads parados e os relatorios mostrarem a Ana enquanto a
/// caixa, o kanban e o Meu Dia continuam no nome do Bruno. `ServicoConversas` ja registra esse
/// defeito acontecendo ao contrario — "as quatro telas diziam 'sem responsavel' para lead com dono
/// ha semanas" — e a invariante que ele enuncia: "`conversa.ResponsavelId =
/// contato.ResponsavelId`. As duas andam juntas".
///
/// ⚠️ E AQUI SOBRESCREVER E O CERTO, ao contrario de `AtribuirContatoSeVagoAsync`, que "so
/// preenche o que esta vago" para o primeiro a responder nao roubar a carteira do colega. Aquele
/// e um efeito colateral de atender; este e um gesto de gestao, explicito, com permissao propria.
/// O proposito do botao E passar o lead para outra pessoa.
///
/// ⚠️ `ResponsavelId` NULO E VALIDO: devolve o lead ao bolo, sem dono. A lista ja desenha
/// travessao nessa coluna, e tirar o dono de quem saiu de ferias e metade do uso real.</summary>
public record RedistribuicaoEmLote(IReadOnlyList<long> NegociacaoIds, long? ResponsavelId);

/// <summary>O que aconteceu com cada um, no molde do relatorio da importacao CSV.
///
/// ⚠️ "PULADO" NAO E ERRO, e separa-lo de `Falhou` e o ponto: quem ja tem lembrete pendente foi
/// deixado de fora DE PROPOSITO — criar um segundo faria o vendedor receber a mesma tarefa duas
/// vezes. Juntar os dois numeros faria o operador procurar um problema que nao existe.</summary>
public record ResultadoEmLote(int Criados, int Pulados, int Falhou);

/// <summary>===================== O QUE A REATIVACAO RENDEU =====================
///
/// A pergunta e "dos leads que marquei com esta etiqueta, quantos fecharam DEPOIS?".
///
/// ⚠️ A JANELA E SOBRE A MARCA, NAO SOBRE A VENDA. Filtrar por `ganha_em` responderia outra
/// pergunta — "das vendas deste mes, quantas tinham sido marcadas" — e esconderia as reativacoes
/// ainda em andamento, que sao a maior parte do trabalho no primeiro mes.
///
/// ⚠️ A ETIQUETA E ESCOLHIDA NA HORA, nao configurada. Uma "etiqueta de reativacao" em
/// configuracoes obrigaria o dono a classificar antes de saber como vai usar, e a metrica so
/// comecaria a funcionar depois disso. Aqui qualquer etiqueta responde: foi usada como campanha,
/// serve como campanha.</summary>
public record FiltroReativacao(long EtiquetaId, DateOnly De, DateOnly Ate, long? ResponsavelId);

/// <summary>⚠️ `Ganhos` E "GANHOS DEPOIS DE MARCADO", e nao "ganhos". A negociacao que ja estava
/// ganha quando recebeu a marca nao foi reativada por ela — conta-la inflaria a metrica com
/// vendas que aconteceram antes do trabalho.
///
/// ⚠️ E VENDA CANCELADA NAO CONTA. `ServicoVendas.CancelarAsync` DEIXA `ganha_em` preenchido de
/// proposito ("o `ganha_em` fica, e quem tira do relatorio e o filtro do indice"). Olhar so
/// `ganha_em IS NOT NULL` creditaria a reativacao por uma venda que foi desfeita.</summary>
public record Reativacao(int Marcados, int Ganhos, decimal ValorGanho);

public interface IServicoLeadsParados
{
    /// <summary>⚠️ Quem não tem `ver_numeros_da_equipe` recebe só os PRÓPRIOS leads parados — e
    /// isso é útil, não uma limitação: o vendedor tem a lista dele sem precisar de permissão nova.
    /// É por isso que a tela não tem guarda de rota, igual a `/relatorios`.</summary>
    Task<PaginaLeadsParados> ListarAsync(FiltroLeadsParados filtro, CancellationToken ct);

    /// <summary>===================== O LEMBRETE VAI PARA QUEM TRABALHA O LEAD =====================
    ///
    /// ⚠️ E NAO PARA QUEM CLICOU, que e o que `ServicoLembretes.CriarAsync` faz ("quem cria
    /// assume"). Ali a regra esta certa: o vendedor marca o proprio retorno. Aqui seria o oposto —
    /// o dono seleciona trinta leads de cinco pessoas e levaria as trinta tarefas no Meu Dia dele,
    /// enquanto os cinco vendedores nao receberiam nada.
    ///
    /// O responsavel sai de `negociacoes.responsavel_id`. Lead sem dono cai para quem pediu: a
    /// tarefa precisa aparecer na lista de ALGUEM, e um lembrete sem responsavel nao aparece em
    /// Meu Dia nenhum.
    ///
    /// ⚠️ EXIGE O GESTO `AgirEmLote`. Ver nao e agir — a listagem acima nao tem guarda nenhuma.</summary>
    Task<ResultadoEmLote> CriarLembretesAsync(LembreteEmLote pedido, CancellationToken ct);

    /// <summary>===================== MARCAR O QUE ESTA SENDO REATIVADO =====================
    ///
    /// A etiqueta e o que liga a acao de hoje a venda de depois: a metrica de reativados compara
    /// `negociacoes_etiquetas.criado_em` com `negociacoes.ganha_em`. Sem a marca, reativar e um
    /// trabalho invisivel.
    ///
    /// ⚠️ ADICIONA, NAO SUBSTITUI — ver `EtiquetaEmLote`.
    ///
    /// ⚠️ NEGOCIO QUE JA ESTA NO TETO DE OITO ETIQUETAS ENTRA EM `Pulados`, nao derruba o lote.
    /// Recusar a chamada inteira por causa de um card cheio faria o operador perder os quarenta e
    /// nove que iam dar certo, sem saber qual era o problemático.
    ///
    /// ⚠️ EXIGE O GESTO `AgirEmLote`.</summary>
    Task<ResultadoEmLote> AplicarEtiquetaAsync(EtiquetaEmLote pedido, CancellationToken ct);

    /// <summary>Quantas negociacoes receberam a etiqueta na janela, e quantas delas fecharam
    /// DEPOIS. Ver `FiltroReativacao` e `Reativacao` para o porque de cada metade.
    ///
    /// ⚠️ NAO EXIGE GESTO NENHUM, como a listagem: quem nao tem `ver_numeros_da_equipe` recebe o
    /// numero dos PROPRIOS negocios. E o vendedor saber o que a reativacao dele rendeu e util.</summary>
    Task<Reativacao> ReativacaoAsync(FiltroReativacao filtro, CancellationToken ct);

    /// <summary>===================== REABRIR EM LOTE DELEGA, NAO REIMPLEMENTA =====================
    ///
    /// Cada id passa por `IServicoContatos.AbrirNegociacaoAsync(id, null)`, que e a MESMA porta do
    /// botao da tela do contato. Ela e quem decide reviver a perda na etapa onde morreu ou abrir
    /// linha nova, e quem publica `lead.movido` para quem integra.
    ///
    /// ⚠️ UMA SEGUNDA IMPLEMENTACAO SERIA DUAS PORTAS PARA O MESMO FATO — a forma de defeito que
    /// o comentario de `AbrirNegociacaoAsync` descreve ter passado o bloco E4 inteiro desmontando.
    /// A precedencia de funil, o `vendas` nao ser tocado, a etapa preservada e a trilha vivem la.
    ///
    /// ⚠️ CONFLITO E `Pulados`, NAO ERRO DO LOTE. Quem ja tem negocio em todos os funis volta 409
    /// naquela porta; aqui isso e um item que nao deu, e os outros quarenta e nove seguem. Abortar
    /// faria o operador perder o lote inteiro por causa de um contato.
    ///
    /// ⚠️ EXIGE O GESTO `AgirEmLote`.</summary>
    Task<ResultadoEmLote> ReabrirAsync(IReadOnlyList<long> contatoIds, CancellationToken ct);

    /// <summary>Troca o responsavel das negociacoes marcadas — e, com elas, o do contato e o da
    /// conversa. Ver `RedistribuicaoEmLote` para o porque das tres.
    ///
    /// ⚠️ O ALVO TEM DE ESTAR ATIVO. Atribuir a quem foi desativado esconde o lead de todo mundo:
    /// ele sai da lista de responsaveis que as telas oferecem, e ninguem mais o ve na propria
    /// carteira. Inativo e recusado; nulo e aceito, e quer dizer "sem dono".
    ///
    /// ⚠️ QUEM JA E DO ALVO ENTRA EM `Pulados`. Nao e erro nem trabalho: e o numero que explica
    /// "marquei quinze, mudaram doze" sem o operador procurar defeito.
    ///
    /// ⚠️ EXIGE O GESTO `AgirEmLote`.</summary>
    Task<ResultadoEmLote> RedistribuirAsync(RedistribuicaoEmLote pedido, CancellationToken ct);
}

/// <summary>As janelas que a tela oferece, em dias.
///
/// Lista FECHADA: o número vira uma subtração de data, e aceitar qualquer inteiro deixaria um
/// `dias=0` trazer a base inteira numa consulta paginada que o cliente pode pedir em laço.</summary>
public static class JanelasDeParada
{
    public static readonly int[] EmDias = [15, 30, 60, 90];

    public const int Padrao = 30;

    /// <summary>Teto de linhas por página. Cinquenta porque a tela existe para AGIR em lote sobre o
    /// que está na página — vinte obrigaria a paginar no meio de uma seleção, e duzentas fariam o
    /// dono rolar procurando o que marcou.</summary>
    public const int TamanhoMaximoPagina = 50;
}
