using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A caixa de entrada, do lado da leitura.
///
/// TUDO acontece no SQL: filtro, ordenacao, cursor e corte. O ServicoInbox do Recupera
/// materializa todos os tickets do status em memoria antes de paginar (e o comentario de la
/// reconhece que "Resolvidas cresce"). Aqui isso nao se repete.</summary>
public class ServicoCaixa(NexoraDbContext db, IContextoEmpresa contexto) : IServicoCaixa
{
    /// <summary>A projeção da linha da caixa, num lugar só.
    ///
    /// A lista e a busca por id devolvem o MESMO `ConversaResumo`. Escrita duas vezes, ela já
    /// divergiria no primeiro campo novo — e o sintoma seria a conversa aberta pelo Meu Dia
    /// mostrando um dado a menos que a mesma linha na lista, sem ninguém entender por quê.
    ///
    /// `Expression` e não um método: o EF precisa TRADUZIR isto para SQL. Um método comum seria
    /// executado em memória, e a página inteira viria do banco antes do corte.</summary>
    /// <summary>⚠️ DEIXOU DE SER `static`, E ISSO FOI NECESSARIO. Ela precisa citar `db` para
    /// perguntar "sobrou funil livre?", e um campo de construtor primario dentro de um
    /// inicializador ESTATICO e CS9105. Como propriedade de instancia, a mesma expressao compila
    /// e o EF a traduz igual — `Select(Resumo)` nao muda.</summary>
    private System.Linq.Expressions.Expression<Func<Conversa, ConversaResumo>> Resumo =>
        c => new ConversaResumo(
            c.Id, c.ContatoId, c.Contato.Nome, c.Contato.Telefone,
            c.UltimaMensagemPrevia,
            c.UltimaMensagemDirecao == null ? null : c.UltimaMensagemDirecao.ToString()!.ToLower(),
            c.UltimaMensagemEm, c.AguardandoDesde, c.NaoLidas,
            c.Status.ToString().ToLower(),
            c.ResponsavelId, c.Responsavel == null ? null : c.Responsavel.Nome,
            // ===================== A ETAPA VEM DA NEGOCIACAO (E4e) =====================
            // `contatos.etapa_id` sai neste bloco, e com ele a resposta unica. Com dois negocios
            // vivos a pergunta "em que etapa esta este contato" passa a ter duas respostas, e a
            // caixa precisa de uma: a do negocio ABERTO.
            //
            // E a conversa em andamento que ela mostra — um pedido fechado esperando conclusao
            // nao e sobre o que se esta conversando. Sem aberto, cai na mais recente, que e como
            // o contato ja ganho aparece hoje ("Venda" com zero em aberto = "Pedido concluido").
            //
            // ⚠️ Ordenar por `Status == Aberta` e NAO por id: a migracao do elo reinseriu as
            // linhas vindas de venda, e os ids delas ficaram maiores que os das abertas. Id
            // deixou de ser relogio — a mesma armadilha que derrubou `RegrasNegociacao`.
            //
            // Pela NAVEGACAO, e nao por `db.Negociacoes`: esta expressao e `static readonly` e um
            // campo do construtor primario nao pode ser citado dentro dela (CS9105).
            // ⚠️ `(long?)` E O CONSERTO DE UM ZERO QUE MENTIA. `Select(n => n.EtapaId)` sobre
            // coleção vazia devolvia `0`, e zero e um id — a tela recebia "etapa 0" como se
            // fosse uma etapa de verdade. Com o E6 isso deixou de ser teórico: contato sem
            // negociacao e o estado de todo lead que acabou de chegar.
            c.Contato.Negociacoes
                .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                .ThenByDescending(n => n.Id)
                .Select(n => (long?)n.EtapaId)
                .FirstOrDefault(),
            // O nome ja era anulavel na pratica; o que muda e que o `?? ""` saiu. Vazio e nulo
            // dizem coisas diferentes, e so o segundo diz "nao ha funil".
            c.Contato.Negociacoes
                .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                .ThenByDescending(n => n.Id)
                .Select(n => n.Etapa.Nome)
                .FirstOrDefault(),
            // ===================== "SOBROU FUNIL LIVRE?", E NAO "NAO TEM NENHUM ABERTO" =====
            // ⚠️ ERA `!Any(Aberta)`, E ISSO FICOU PARA TRAS QUANDO A REGRA VIROU POR FUNIL.
            // Relatado assim: "ainda nao consigo adicionar Ysia a outro funil que ela nao esteja".
            //
            // Ela tinha uma aberta em Pos-venda, entao `!Any(Aberta)` era falso e a faixa
            // simplesmente NAO APARECIA na caixa — mesmo com Vendas e Teste livres. A tela do
            // contato ja fazia a pergunta certa; a caixa, que e onde o vendedor trabalha, nao.
            //
            // ⚠️ `Aberta` OU `Ganha`, e nao so aberta: e a MESMA pergunta que
            // `uq_negociacoes_card_por_funil` responde no banco — "ja ha um card desta pessoa
            // aqui?". So aberta aqui oferecia um funil que `AbrirNegociacaoAsync` recusa com 409,
            // e este projeto ja trata "oferecer um botao que sempre erra" como defeito.
            //
            // O custo e uma subconsulta por linha, e ela e pequena de proposito: o teto e 5 funis
            // por empresa (`ServicoPipelines.MaximoPipelines`), e `pipelines` e uma tabela de
            // unidades. `negociacoes` entra pelo indice de contato.
            //
            // ⚠️ A LISTA PRONTA, E NAO MAIS OS OCUPADOS. A tela subtraia os ocupados da lista do
            // menu — uma regra no painel. `PodeAbrirNegociacao` virou "a lista nao esta vazia",
            // e o anonimizado entra AQUI, no filtro, para as duas perguntas cairem juntas.
            //
            // `OcupaOFunil` pela navegacao com `AsQueryable`: e a MESMA expressao da abertura de
            // negociacao e do detalhe do contato, e o EF a traduz dentro do EXISTS.
            // ================================================================================
            db.Pipelines
                .Where(p => c.Contato.AnonimizadoEm == null
                         && !c.Contato.Negociacoes.AsQueryable()
                               .Where(RegrasNegociacao.OcupaOFunil)
                               .Any(n => n.PipelineId == p.Id))
                .OrderBy(p => p.Ordem).ThenBy(p => p.Nome)
                .Select(p => new FunilLivre(p.Id, p.Nome))
                .ToList(),
            // O par: há negócio ABERTO para fechar, e a pessoa está viva.
            c.Contato.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta)
                && c.Contato.AnonimizadoEm == null,
            // "Ja ganhou alguma vez" — era `contatos.ganho_em != null`. Continua, agora so para o
            // TEXTO da faixa.
            c.Contato.Negociacoes.Any(n => n.Status == StatusNegociacao.Ganha
                                        || n.Status == StatusNegociacao.Concluida),
            c.CanalCiclo == null ? null : c.CanalCiclo.Nome,
            // Pedidos em aberto: era `vendas` com status `fechada`, hoje e `ganha` (E4e). E o que
            // distingue "Venda" de "Pedido concluido" no rotulo da linha.
            c.Contato.Negociacoes.Count(n => n.Status == StatusNegociacao.Ganha),
            // Pela NAVEGACAO, pelo mesmo motivo do Count acima: esta expressao e `static readonly`
            // e nao pode citar `db` (CS9105).
            c.Contato.Etiquetas
                .OrderBy(x => x.Etiqueta.Nome)
                .Select(x => new EtiquetaDto(x.Etiqueta.Id, x.Etiqueta.Nome, x.Etiqueta.Cor))
                .ToList());

    /// <summary>Uma conversa pelo id. O query filter global faz o isolamento: id de outra
    /// empresa não casa e o retorno é `null` — que o controller traduz em 404.</summary>
    public async Task<ConversaResumo?> ConversaAsync(long conversaId, CancellationToken ct) =>
        await db.Conversas.AsNoTracking()
            .Where(c => c.Id == conversaId)
            .Select(Resumo)
            .FirstOrDefaultAsync(ct);

    public async Task<PaginaCursor<ConversaResumo>> ConversasAsync(
        FiltroConversa filtro, string? busca, long? etiquetaId,
        DateTime? cursorEm, long? cursorId, int tamanho,
        CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, 100);
        var meuId = contexto.UsuarioId;

        // O query filter global ja restringe ao tenant.
        var q = db.Conversas.AsNoTracking();

        q = filtro switch
        {
            FiltroConversa.Aguardando => q.Where(c => c.Status == StatusConversa.Aberta && c.AguardandoDesde != null),
            FiltroConversa.Minhas => q.Where(c => c.Status == StatusConversa.Aberta && c.ResponsavelId == meuId),
            FiltroConversa.NaoAtribuidas => q.Where(c => c.Status == StatusConversa.Aberta && c.ResponsavelId == null),
            FiltroConversa.Resolvidas => q.Where(c => c.Status == StatusConversa.Resolvida),
            _ => q.Where(c => c.Status == StatusConversa.Aberta)
        };

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim().ToLower();
            q = q.Where(c => c.Contato.Nome.ToLower().Contains(b) || c.Contato.Telefone.Contains(b));
        }

        // ===================== O FILTRO POR ETIQUETA (issue #3) =====================
        // ⚠️ ENTRA AQUI, entre a busca e o CURSOR, e a ordem importa. O cursor compara contra a
        // ultima linha JA ENTREGUE; se ele fosse aplicado antes deste `Where`, a pagina seguinte
        // pularia as conversas que o filtro tirou do meio — some contato da rolagem, sem erro.
        //
        // `Any` sobre a navegacao vira EXISTS, que usa `ix_contatos_etiquetas_etiqueta`. Um join
        // devolveria uma linha por marcacao e obrigaria a um DISTINCT.
        // ==========================================================================
        if (etiquetaId is { } et)
            q = q.Where(c => c.Contato.Etiquetas.Any(x => x.EtiquetaId == et));

        // CURSOR por VALOR, no par exato da ordenacao. O `<` composto e traduzido para SQL e usa
        // o indice ix_conversas_lista.
        if (cursorEm is { } ce)
        {
            var cid = cursorId ?? long.MaxValue;
            q = q.Where(c => c.UltimaMensagemEm < ce || (c.UltimaMensagemEm == ce && c.Id < cid));
        }

        var itens = await q
            .OrderByDescending(c => c.UltimaMensagemEm)
            .ThenByDescending(c => c.Id)
            .Take(tamanho + 1)   // +1 sonda se ha proxima pagina
            .Select(Resumo)
            .ToListAsync(ct);

        var temMais = itens.Count > tamanho;
        return new PaginaCursor<ConversaResumo>(itens.Take(tamanho).ToList(), temMais);
    }

    public async Task<PaginaCursor<MensagemDto>> MensagensAsync(
        long conversaId, long? antesDeId, int tamanho, CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, 200);

        var q = db.Mensagens.AsNoTracking().Where(m => m.ConversaId == conversaId);
        if (antesDeId is { } cursor) q = q.Where(m => m.Id < cursor);

        // As `tamanho` mais NOVAS antes do cursor, +1 para saber se ha mais antigas.
        var desc = await q
            .OrderByDescending(m => m.Id)
            .Take(tamanho + 1)
            .Select(m => new MensagemDto(
                m.Id, m.Direcao.ToString().ToLower(), m.Texto, m.Ack,
                m.EnviadaEm, m.RecebidaEm, m.ExpiradaEm, m.Erro,
                m.TipoMidia.ToString().ToLower(), m.MidiaNome, m.MidiaMime, m.MidiaBytes,
                m.MidiaDuracaoSegundos,
                m.EnviadoPor, m.UsuarioEnviou == null ? null : m.UsuarioEnviou.Nome,
                m.LembreteId != null,
                m.RecuperadaEm))
            .ToListAsync(ct);

        var temMais = desc.Count > tamanho;
        // Take volta a IEnumerable -> Reverse e o do LINQ (nao o in-place de List): ascendente.
        var mensagens = desc.Take(tamanho).Reverse().ToList();
        return new PaginaCursor<MensagemDto>(mensagens, temMais);
    }

    public async Task MarcarLidaAsync(long conversaId, CancellationToken ct)
    {
        // ExecuteUpdate: o filtro global vale, entao conversa de outro tenant afeta 0 linhas.
        // NAO toca aguardando_desde — ler nao e responder, e o semaforo mede resposta.
        var afetadas = await db.Conversas
            .Where(c => c.Id == conversaId && c.NaoLidas > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NaoLidas, 0), ct);

        if (afetadas == 0 && !await db.Conversas.AnyAsync(c => c.Id == conversaId, ct))
            throw new RegraDeNegocioException("Conversa não encontrada.");
    }
}
