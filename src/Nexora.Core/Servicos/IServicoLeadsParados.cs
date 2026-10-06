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
    int DiasParado);

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
    decimal? ValorMax = null);

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

/// <summary>O que aconteceu com cada um, no molde do relatorio da importacao CSV.
///
/// ⚠️ "PULADO" NAO E ERRO, e separa-lo de `Falhou` e o ponto: quem ja tem lembrete pendente foi
/// deixado de fora DE PROPOSITO — criar um segundo faria o vendedor receber a mesma tarefa duas
/// vezes. Juntar os dois numeros faria o operador procurar um problema que nao existe.</summary>
public record ResultadoEmLote(int Criados, int Pulados, int Falhou);

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
