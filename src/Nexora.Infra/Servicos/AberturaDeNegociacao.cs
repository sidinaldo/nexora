using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== O QUE SOBROU DO ESPELHO (E4e/3b) =====================
///
/// Entre o E4b e o E4e este arquivo se chamava `EspelhoNegociacao` e traduzia as duas
/// representacoes uma na outra: `contatos`/`vendas` eram a verdade, e `negociacoes` era mantida
/// em dia ao lado. Cinco metodos, e o comentario prometia que ele morreria no E4e.
///
/// Ele morreu. Nao ha mais o que espelhar: a negociacao E o negocio. O que restou nao e espelho
/// nenhum — e a ABERTURA, o unico gesto que os cinco pontos de entrada de contato compartilham.
///
/// ⚠️ OS TRES QUE SUMIRAM, e por que nenhum deles faz falta:
///
///   `AbertaAsync`   — "a aberta, criando se nao houver". Quem fecha venda agora PERGUNTA se ha
///                     negocio aberto e recusa quando nao ha (`MarcarGanhoAsync`), em vez de
///                     inventar um para ter onde carimbar. Criar por baixo do pano escondia o
///                     caso "ja concluiu tudo" em vez de dizer "reabra antes".
///   `GanhaAsync`    — so existia para achar a venda vigente e copiar o carimbo do contato nela.
///   `DasVendasAsync`— casava negociacao com `vendas` pelo `venda_id`. Nao ha mais `vendas`.
///
/// ⚠️ `ReconciliarAsync` tambem sumiu, e essa e a mudanca com consequencia real: os dois
/// semeadores inseriam contatos em lote, carimbavam `ganho_em`/`perdido_em` com `ExecuteUpdate` e
/// pediam a reconciliacao para DEDUZIR o estado do carimbo. Agora eles criam a negociacao com o
/// estado que quiseram — nao ha deducao, e portanto nao ha como deduzir errado.
/// ==========================================================================</summary>
internal static class AberturaDeNegociacao
{
    /// <summary>A negociacao aberta que nasce junto com o contato.
    ///
    /// ⚠️ Liga pela NAVEGACAO (`Contato = contato`) e nao por id, de proposito: os cinco pontos
    /// que criam contato gravam tudo num `SaveChanges` so, e exigir o id ja gerado obrigaria cada
    /// um deles a partir em dois — abrindo uma janela em que existe contato sem negociacao.
    ///
    /// ⚠️ `valor` AGORA E PARAMETRO, e antes era `contato.Valor`. A coluna do contato deixou de
    /// ser escrita no E4e/3b: quem cria o contato passa o valor direto para ca. Ler do contato
    /// continuaria "funcionando" e devolveria nulo sempre — silenciosamente, sem erro nenhum.
    ///
    /// `EtapaId` e `OrdemKanban` AINDA saem do contato porque as colunas sao NOT NULL ate a
    /// migracao do E4e/4. Ali elas invertem de dono, e este metodo passa a recebe-las.</summary>
    internal static async Task<Negociacao> NovaAsync(
        NexoraDbContext db, Contato contato, decimal? valor, long? canalCicloId,
        CancellationToken ct) =>
        new()
        {
            EmpresaId = contato.EmpresaId,
            Contato = contato,
            PipelineId = await PipelineDaEtapaAsync(db, contato.EtapaId, contato.EmpresaId, ct),
            EtapaId = contato.EtapaId,
            OrdemKanban = contato.OrdemKanban,
            ResponsavelId = contato.ResponsavelId,
            Valor = valor,
            Status = StatusNegociacao.Aberta,
            CanalCicloId = canalCicloId
        };

    /// <summary>Em qual funil esta a etapa. A negociacao guarda a pipeline redundantemente para o
    /// quadro e o relatorio nao precisarem de um join a cada consulta.
    ///
    /// ⚠️ `IgnoreQueryFilters` COM `empresa_id` EXPLICITO, e nao por comodidade: o webhook da
    /// Evolution e a captura publica rodam SEM tenant no contexto (`EmpresaId = 0`) — eles
    /// descobrem a empresa pela conexao ou pelo formulario. Com o filtro ligado esta consulta
    /// voltava vazia e o lead novo nao ganhava negociacao nenhuma.
    ///
    /// O recorte continua existindo: ele so passou a ser o parametro, que vem do proprio contato.
    /// E a FK composta `fk_negociacoes_etapa` e quem garante de verdade.</summary>
    internal static Task<long> PipelineDaEtapaAsync(
        NexoraDbContext db, long etapaId, long empresaId, CancellationToken ct) =>
        db.EtapasFunil.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.Id == etapaId && e.EmpresaId == empresaId)
            .Select(e => e.PipelineId)
            .FirstAsync(ct);
}
