using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O quadro kanban: leitura paginada por coluna e o cálculo de posição do card.</summary>
public class ServicoFunil(NexoraDbContext db) : IServicoFunil
{
    /// <summary>Abaixo desta distância entre vizinhos, a coluna é renormalizada antes de calcular
    /// o ponto médio.
    ///
    /// ===================== A ARMADILHA DO PONTO MÉDIO =====================
    /// Dividir ao meio repetidamente entre os MESMOS dois vizinhos encolhe o intervalo
    /// exponencialmente. Chega uma hora em que o "meio" arredonda para um dos extremos e o card
    /// para de aceitar reordenação — sem erro, sem log, sem nada. É o tipo de bug que ninguém
    /// liga ao commit certo, porque ele aparece meses depois num único card.
    ///
    /// NOTA SOBRE O TIPO DA COLUNA: o prompt deste bloco assume `numeric(18,6)`, que esgotaria em
    /// ~19 movimentos. O schema que foi construído usa `numeric` SEM escala (ver o comentário em
    /// Contato.OrdemKanban) — no Postgres isso vai a milhares de casas decimais, e o limite real
    /// passa a ser o `decimal` do C#, com ~28 dígitos significativos: mais de 90 divisões no
    /// mesmo par. Ou seja, na prática isto quase nunca dispara.
    ///
    /// O limiar fica em 2e-6 assim mesmo, e não é excesso de zelo mal calibrado: renormalizar é
    /// barato (um UPDATE por linha da coluna, numa operação que já é interativa e rara) e mantém
    /// as ordens em números legíveis. Depurar uma coluna cujos valores são 3.0000019073486328125
    /// é bem pior do que renormalizar cedo demais.
    /// =====================================================================</summary>
    public const decimal LimiarRenormalizacao = 0.000002m;

    // ==================================================================== leitura
    public async Task<QuadroFunil> QuadroAsync(int porColuna, CancellationToken ct)
    {
        porColuna = Math.Clamp(porColuna, 1, 200);

        var etapas = await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Ordem)
            .Select(e => new
            {
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                // Contagem e soma AGREGADAS NO SQL, sobre o conjunto inteiro da coluna — não
                // sobre a página. O cabeçalho mostra "38 · R$ 47.500" com 50 cards carregados.
                //
                // O predicado vem de `RegrasContato.NoQuadro` — a MESMA expressão que o
                // `ServicoDashboard` usa. Escrito por extenso em cada lugar, ele já divergiu
                // uma vez: o dashboard esqueceu `anonimizado_em` e passou a contar mais que o
                // quadro. Uma `Expression` o EF traduz; um método próprio, não.
                Total = db.Contatos.Where(RegrasContato.NoQuadro).Count(c => c.EtapaId == e.Id),
                ValorTotal = db.Contatos.Where(RegrasContato.NoQuadro)
                    .Where(c => c.EtapaId == e.Id)
                    .Sum(c => (decimal?)c.Valor)
            })
            .ToListAsync(ct);

        var colunas = new List<ColunaFunil>(etapas.Count);

        // Uma consulta por coluna. A alternativa — uma consulta só com ROW_NUMBER() particionado —
        // traria tudo de uma vez, mas o EF não expressa window function sem SQL cru, e são 5
        // consultas indexadas contra ix_contatos_kanban. Não vale o SQL cru aqui.
        foreach (var e in etapas)
        {
            var pagina = await ColunaAsync(e.Id, null, null, porColuna, ct);
            colunas.Add(new ColunaFunil(
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                e.Total, e.ValorTotal ?? 0m, pagina.Itens, pagina.TemMais));
        }

        return new QuadroFunil(colunas);
    }

    public async Task<PaginaCursor<ContatoCard>> ColunaAsync(
        long etapaId, decimal? cursorOrdem, long? cursorId, int tamanho, CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, 200);

        var q = db.Contatos.AsNoTracking()
            .Where(RegrasContato.NoQuadro)
            .Where(c => c.EtapaId == etapaId);

        // CURSOR POR VALOR, no par exato da ordenação — o mesmo par do ix_contatos_kanban.
        // Offset não serve aqui: esta é literalmente a tela onde o vendedor arrasta cards, e
        // entre duas páginas a coluna pode ter sido reordenada.
        if (cursorOrdem is { } co)
        {
            var cid = cursorId ?? long.MinValue;
            q = q.Where(c => c.OrdemKanban > co || (c.OrdemKanban == co && c.Id > cid));
        }

        var linhas = await q
            .OrderBy(c => c.OrdemKanban).ThenBy(c => c.Id)
            .Take(tamanho + 1)   // +1 sonda se há próxima página
            .Select(c => new
            {
                c.Id, c.Nome, c.Telefone, c.OrdemKanban, c.Valor, c.Versao,
                c.ResponsavelId, ResponsavelNome = c.Responsavel == null ? null : c.Responsavel.Nome,
                Conversa = db.Conversas
                    .Where(v => v.ContatoId == c.Id)
                    .Select(v => new { v.Id, v.AguardandoDesde, v.NaoLidas, v.UltimaMensagemEm })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var temMais = linhas.Count > tamanho;

        var cards = linhas.Take(tamanho).Select(c => new ContatoCard(
            c.Id, c.Nome, c.Telefone, c.OrdemKanban, c.Valor,
            c.ResponsavelId, c.ResponsavelNome,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0,
            c.Conversa?.UltimaMensagemEm, c.Versao)).ToList();

        return new PaginaCursor<ContatoCard>(cards, temMais);
    }

    // ==================================================================== mover
    public async Task<decimal> MoverAsync(long contatoId, MoverContato destino, CancellationToken ct)
    {
        var contato = await db.Contatos.FirstOrDefaultAsync(c => c.Id == contatoId, ct)
            ?? throw new RegraDeNegocioException("Contato não encontrado.");

        if (contato.AnonimizadoEm is not null)
            throw new RegraDeNegocioException(
                "Este contato foi anonimizado e não aparece mais no funil.", conflito: true);

        if (contato.PerdidoEm is not null)
            throw new RegraDeNegocioException(
                "Este contato está marcado como perdido. Reabra antes de movê-lo.", conflito: true);

        // Etapa DESTA empresa. O query filter protege a leitura; um id vindo do cliente precisa
        // de checagem explícita — sem isso, um id de outro tenant passaria e o contato sairia do
        // funil da própria empresa.
        var etapa = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.Id == destino.EtapaId)
            .Select(e => new { e.Id, e.EGanho })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Etapa não encontrada.");

        // ===== A RECUSA QUE SUSTENTA A PORTA ÚNICA DO GANHO =====
        // Se `mover` aceitasse a etapa de ganho, existiria contato na coluna Venda sem `ganho_em`
        // e sem `valor` — e o dashboard, que conta por `ganho_em`, não o veria. O card estaria na
        // tela e a venda não existiria no relatório.
        if (etapa.EGanho)
            throw new RegraDeNegocioException(
                "Para mover para a etapa de venda, registre a venda com o valor fechado.",
                conflito: true);

        // Se veio card de referência, ele tem que estar na etapa de destino — senão o "meio"
        // seria calculado entre vizinhos de colunas diferentes, produzindo uma ordem sem sentido.
        if (destino.AposContatoId is { } apos)
        {
            if (apos == contatoId)
                throw new RegraDeNegocioException("Um contato não pode ser posicionado depois de si mesmo.");

            if (!await db.Contatos.Where(RegrasContato.NoQuadro).AnyAsync(
                    c => c.Id == apos && c.EtapaId == destino.EtapaId, ct))
                throw new RegraDeNegocioException(
                    "A posição de destino não existe mais. Recarregue o quadro.", conflito: true);
        }

        var nova = await CalcularOrdemAsync(contatoId, destino, ct);

        // Se o intervalo acabou, renormaliza a coluna e recalcula sobre valores frescos (que
        // passam a ter distância 1 entre si).
        if (nova is null)
        {
            await RenormalizarAsync(destino.EtapaId, ct);
            nova = await CalcularOrdemAsync(contatoId, destino, ct)
                ?? throw new InvalidOperationException(
                    "Ordem do kanban sem intervalo mesmo após renormalizar — isto não deveria acontecer.");
        }

        contato.EtapaId = destino.EtapaId;
        contato.OrdemKanban = nova.Value;

        // ===================== CONCORRÊNCIA OTIMISTA =====================
        // Se o cliente mandou a versão que ele viu, ela entra no `WHERE` do UPDATE. Outro
        // vendedor que tenha mexido no card entre a leitura e o arrasto muda o `xmin`, o UPDATE
        // afeta zero linhas e o EF lança — vira 409, e a tela recarrega a coluna.
        //
        // Sem isto o comportamento era "o último ganha, em silêncio": o primeiro vendedor via o
        // card voltar sozinho para outro lugar no próximo carregamento e não tinha como saber
        // por quê.
        //
        // A versão é OPCIONAL: `MarcarGanhoAsync` também move o card e não vem de um arrasto,
        // então não tem versão para mandar. Exigir sempre quebraria a porta única do ganho.
        if (destino.Versao is { } versaoDoCliente)
            db.Entry(contato).Property(c => c.Versao).OriginalValue = versaoDoCliente;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // `conflito: true` é o que o middleware traduz para 409 — o mesmo código que o
            // kanban já trata recarregando a coluna.
            throw new RegraDeNegocioException(
                "Outra pessoa moveu este contato enquanto você arrastava. A coluna foi recarregada.",
                conflito: true);
        }

        return nova.Value;
    }

    /// <summary>O ponto médio, com os três casos de borda. NULL = o intervalo acabou e a coluna
    /// precisa ser renormalizada antes.</summary>
    private async Task<decimal?> CalcularOrdemAsync(
        long contatoId, MoverContato destino, CancellationToken ct)
    {
        // O próprio card sai da conta: mover dentro da mesma coluna não pode considerar a posição
        // antiga dele como vizinha, senão o "meio" é calculado contra ele mesmo.
        var coluna = db.Contatos.AsNoTracking()
            .Where(RegrasContato.NoQuadro)
            .Where(c => c.EtapaId == destino.EtapaId && c.Id != contatoId);

        if (destino.AposContatoId is not { } aposId)
        {
            // TOPO da coluna (ou coluna vazia).
            var primeira = await coluna.MinAsync(c => (decimal?)c.OrdemKanban, ct);
            return primeira is null ? 0m : primeira.Value - 1m;
        }

        var anterior = await coluna
            .Where(c => c.Id == aposId)
            .Select(c => (decimal?)c.OrdemKanban)
            .FirstOrDefaultAsync(ct);

        if (anterior is null)
            throw new RegraDeNegocioException(
                "A posição de destino não existe mais. Recarregue o quadro.", conflito: true);

        // O vizinho de baixo: o menor que ainda é maior que o de cima. Desempate por id, para
        // acompanhar a ordenação (ordem_kanban, id) da leitura.
        var posterior = await coluna
            .Where(c => c.OrdemKanban > anterior.Value
                     || (c.OrdemKanban == anterior.Value && c.Id > aposId))
            .OrderBy(c => c.OrdemKanban).ThenBy(c => c.Id)
            .Select(c => (decimal?)c.OrdemKanban)
            .FirstOrDefaultAsync(ct);

        // FIM da coluna.
        if (posterior is null) return anterior.Value + 1m;

        // Intervalo esgotado — quem chama renormaliza.
        if (posterior.Value - anterior.Value < LimiarRenormalizacao) return null;

        return (anterior.Value + posterior.Value) / 2m;
    }

    /// <summary>Reescreve a coluna como 1, 2, 3… PRESERVANDO a ordem relativa.
    ///
    /// A ordenação da releitura é a MESMA da leitura do quadro — (ordem_kanban, id) — para o
    /// vendedor não ver os cards trocarem de lugar depois de arrastar um deles. Um `Id` a menos
    /// no desempate e dois cards com a mesma ordem sairiam invertidos.
    ///
    /// Vale a coluna inteira porque é raro: ver o comentário do LimiarRenormalizacao.</summary>
    private async Task RenormalizarAsync(long etapaId, CancellationToken ct)
    {
        var cards = await db.Contatos
            .Where(RegrasContato.NoQuadro)
            .Where(c => c.EtapaId == etapaId)
            .OrderBy(c => c.OrdemKanban).ThenBy(c => c.Id)
            .ToListAsync(ct);

        for (var i = 0; i < cards.Count; i++)
            cards[i].OrdemKanban = i + 1;

        // Uma transação implícita do SaveChanges: ou a coluna inteira é renumerada, ou nada é.
        // Renumerar pela metade deixaria cards com ordem antiga e nova misturadas.
        await db.SaveChangesAsync(ct);
    }
}
