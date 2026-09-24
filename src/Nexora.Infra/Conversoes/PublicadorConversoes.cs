using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Conversoes;

/// <summary>Grava o evento de conversão na fila. Um INSERT, e nada mais (INT-4).
///
/// ===================== `IgnoreQueryFilters` EM TUDO =====================
/// Os dois caminhos que publicam `Lead` rodam SEM tenant no contexto: a captação pública (a empresa
/// vem da chave do formulário) e o processador do webhook da Evolution (vem do `instance_name`).
/// Com query filter, a busca da credencial voltaria vazia nesses caminhos — e o resultado seria "o
/// lead do site e o do WhatsApp nunca viram conversão", em silêncio, enquanto o criado à mão na
/// tela vira.
///
/// É a mesma armadilha que o `PublicadorEventos` documenta, e custa o mesmo: nada estoura.
/// ========================================================================
///
/// ===================== NUNCA LANÇA =====================
/// Um `catch` largo em volta de tudo. O chamador está recebendo um lead ou fechando uma venda: um
/// erro aqui não pode virar 500 no formulário do site do cliente nem impedir o vendedor de marcar
/// a venda como ganha.
/// =======================================================</summary>
public class PublicadorConversoes(
    NexoraDbContext db,
    TimeProvider relogio,
    ILogger<PublicadorConversoes> log) : IPublicadorConversoes
{
    /// <summary>A janela da Meta. Não é configurável: é a regra dela.</summary>
    private const int DiasDeValidade = 7;

    public async Task PublicarLeadAsync(Contato contato, CancellationToken ct = default)
    {
        try
        {
            var credencial = await CredencialAsync(contato.EmpresaId, TipoConversao.Lead, ct);
            if (credencial is null) return;

            var rastro = await RastroAsync(contato.Id, ct);

            var agora = relogio.GetUtcNow().UtcDateTime;
            var ids = RegrasRastreio.Ler(rastro?.Identificadores);

            var fato = new FatoDeConversao(
                TipoConversao.Lead,
                // O id do NAVEGADOR quando existe — é ele que a Meta casa com o evento do pixel.
                // Sem rastro, um novo: o evento continua valendo, só não deduplica com nada.
                rastro?.EventoId ?? Guid.NewGuid(),
                // ⚠️ A HORA DO FATO, e não a de agora: o lead do formulário pode ter entrado
                // enquanto o publicador estava indisponível, e a Meta atribui pelo `event_time`.
                rastro?.OcorridoEm ?? agora,
                Email: contato.Email,
                Telefone: contato.Telefone,
                Ip: rastro?.Ip,
                UserAgent: rastro?.UserAgent,
                Fbp: ids.GetValueOrDefault(RegrasRastreio.ChaveFbp),
                Fbc: ids.GetValueOrDefault(RegrasRastreio.ChaveFbc),
                Pagina: rastro?.Pagina,
                Fonte: rastro?.Fonte,
                CtwaClid: ids.GetValueOrDefault(RegrasRastreio.ChaveCtwaClid));

            await EnfileirarAsync(contato.EmpresaId, fato, contato.Id, negociacaoId: null, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Conversão de lead do contato {Id} não foi enfileirada.", contato.Id);
        }
    }

    public async Task PublicarCompraAsync(long negociacaoId, CancellationToken ct = default)
    {
        try
        {
            // Uma consulta só: a negociação, o contato e o rastro dele. O valor vem da NEGOCIAÇÃO
            // — é ele que a Meta usa para otimizar por receita.
            var venda = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.Id == negociacaoId)
                .Select(n => new
                {
                    n.Id,
                    n.EmpresaId,
                    n.ContatoId,
                    n.Valor,
                    n.GanhaEm,
                    Email = n.Contato.Email,
                    Telefone = n.Contato.Telefone
                })
                .FirstOrDefaultAsync(ct);

            if (venda is null) return;

            var credencial = await CredencialAsync(venda.EmpresaId, TipoConversao.Compra, ct);
            if (credencial is null) return;

            var rastro = await RastroAsync(venda.ContatoId, ct);
            var ids = RegrasRastreio.Ler(rastro?.Identificadores);
            var agora = relogio.GetUtcNow().UtcDateTime;

            var fato = new FatoDeConversao(
                TipoConversao.Compra,
                // ⚠️ ID NOVO, e nunca o do navegador. O `event_id` do rastro pertence ao `Lead`:
                // reusá-lo aqui faria a Meta tratar a COMPRA como repetição do lead e descartá-la —
                // que é exatamente o evento que o bloco existe para entregar.
                Guid.NewGuid(),
                // A hora em que a venda FECHOU. É ela que tem de estar dentro dos 7 dias, e é o que
                // permite fechar uma venda de um lead de três meses atrás.
                venda.GanhaEm ?? agora,
                Email: venda.Email,
                Telefone: venda.Telefone,
                Ip: rastro?.Ip,
                UserAgent: rastro?.UserAgent,
                Fbp: ids.GetValueOrDefault(RegrasRastreio.ChaveFbp),
                // O `fbc` do clique ORIGINAL — é o elo que diz qual anúncio trouxe esta venda, e a
                // razão pela qual o rastro nunca é sobrescrito.
                Fbc: ids.GetValueOrDefault(RegrasRastreio.ChaveFbc),
                Pagina: rastro?.Pagina,
                Fonte: rastro?.Fonte,
                Valor: venda.Valor,
                // Vai junto na COMPRA também: `Origem` devolve `system_generated` para ela de
                // qualquer jeito, mas o identificador ainda é o elo que diz qual anúncio pagou esta
                // venda quando ela vier do Clique-para-WhatsApp.
                CtwaClid: ids.GetValueOrDefault(RegrasRastreio.ChaveCtwaClid));

            await EnfileirarAsync(venda.EmpresaId, fato, venda.ContatoId, venda.Id, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Conversão de compra da negociação {Id} não foi enfileirada.",
                negociacaoId);
        }
    }

    /// <summary>A credencial que PODE enviar este tipo, ou nulo.
    ///
    /// O portão é `CredencialConversao.PodeEnviar` — uma cópia só da regra, na entidade. Sem
    /// consentimento declarado não enfileira NADA: guardar o rastro é uma coisa; deixar dado
    /// pessoal hasheado parado numa fila que não pode drenar é outra.</summary>
    private async Task<CredencialConversao?> CredencialAsync(
        long empresaId, TipoConversao tipo, CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.EmpresaId == empresaId && c.Plataforma == PlataformaConversao.Meta, ct);

        return credencial?.PodeEnviar(tipo) == true ? credencial : null;
    }

    private Task<RastreioLead?> RastroAsync(long contatoId, CancellationToken ct) =>
        db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.ContatoId == contatoId, ct);

    /// <summary>===================== A COLISÃO É NO-OP, E DE PROPÓSITO =====================
    /// `ON CONFLICT ... DO NOTHING` contra os dois únicos parciais (`uq_conversoes_lead` e
    /// `uq_conversoes_compra`). Reabrir e refechar a mesma venda é gesto NORMAL na tela, e o segundo
    /// `Purchase` não é registro repetido: é o algoritmo do cliente aprendendo que aquele público
    /// converte o dobro do que converte.
    ///
    /// Silencioso, como `ServicoCaptura` já faz com telefone repetido: não é erro, é o caso normal
    /// acontecendo duas vezes.
    ///
    /// SQL cru porque o EF não expressa `ON CONFLICT`, e capturar `DbUpdateException` envenenaria o
    /// ChangeTracker num caminho que barra de propósito. O `WHERE` do índice parcial precisa
    /// aparecer na cláusula — sem ele o Postgres não sabe qual índice inferir.
    /// ============================================================================</summary>
    private async Task EnfileirarAsync(
        long empresaId, FatoDeConversao fato, long contatoId, long? negociacaoId,
        CancellationToken ct)
    {
        // ⚠️ CONCATENACAO, e nao `$"..."`: o analisador do EF (EF1002) recusa string interpolada em
        // `ExecuteSqlRaw` por causa de injecao. Aqui nao ha entrada nenhuma no SQL — `alvo` e uma de
        // duas constantes —, mas ele nao tem como saber, e desligar o aviso por arquivo custaria a
        // protecao no proximo SQL cru que alguem escrever aqui.
        const string ConflitoDaCompra = "(negociacao_id) WHERE tipo = 'compra'";
        const string ConflitoDoLead = "(contato_id) WHERE tipo = 'lead'";

        var alvo = fato.Tipo == TipoConversao.Compra ? ConflitoDaCompra : ConflitoDoLead;
        var agora = relogio.GetUtcNow().UtcDateTime;

        var sql = """
            INSERT INTO eventos_conversao (
                empresa_id, plataforma, tipo, evento_id, contato_id, negociacao_id,
                payload, ocorrido_em, expira_em, status, tentativas, proxima_tentativa_em,
                criado_em)
            VALUES (
                @empresa,
                CAST('meta' AS plataforma_conversao_enum),
                CAST(@tipo AS tipo_conversao_enum),
                @evento, @contato, @negociacao,
                CAST(@payload AS jsonb), @ocorrido, @expira,
                CAST('pendente' AS status_conversao_enum), 0, @agora,
                @agora)
            ON CONFLICT
            """ + alvo + " DO NOTHING";

        await db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("empresa", empresaId),
            new NpgsqlParameter("tipo", fato.Tipo.ToString().ToLowerInvariant()),
            new NpgsqlParameter("evento", fato.EventoId),
            new NpgsqlParameter("contato", contatoId),
            new NpgsqlParameter("negociacao", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = (object?)negociacaoId ?? DBNull.Value
            },
            new NpgsqlParameter("payload", MontadorEventoMeta.Montar(fato)),
            new NpgsqlParameter("ocorrido", fato.OcorridoEm),
            // `ocorrido_em + 7 dias`: o motor marca `expirado` antes de tocar a rede, porque a Meta
            // recusa a requisição INTEIRA por causa de um evento velho.
            new NpgsqlParameter("expira", fato.OcorridoEm.AddDays(DiasDeValidade)),
            new NpgsqlParameter("agora", agora));
    }
}
