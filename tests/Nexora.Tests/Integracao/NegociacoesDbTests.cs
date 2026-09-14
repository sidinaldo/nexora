using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A NEGOCIAÇÃO, no nível do banco.
///
/// ===================== O QUE ESTE ARQUIVO PROVA =====================
/// A tabela ainda não tem serviço — ninguém a escreve pela aplicação. O que ela já tem é o
/// conjunto de garantias que este bloco moveu de duas tabelas para uma, e todas elas moram no
/// BANCO: os dois CHECKs, as FKs compostas por tenant, a exceção deliberada do canal e o `xmin`.
///
/// São garantias que nenhum código de aplicação pode afrouxar por engano — e é exatamente por
/// isso que só um teste que escreve DIRETO na tabela, passando por cima de qualquer serviço,
/// prova que elas estão mesmo lá.
/// ====================================================================</summary>
[Collection("banco")]
public class NegociacoesDbTests(BancoTeste banco)
{
    // ==================================================================== ck_negociacoes_valor
    /// <summary>⚠️ ESTE É O CHECK QUE SUBSTITUI DOIS, E O MOTIVO DELE É DINHEIRO.
    ///
    /// `contatos.valor` era estimativa anulável; `vendas.valor` era NOT NULL com `> 0`. A mesma
    /// coluna faz os dois papéis agora, e sem o CHECK uma negociação ganha sem valor entraria no
    /// faturamento como zero — o dono só notaria fechando o mês.</summary>
    [Fact]
    public async Task GANHA_SEM_VALOR_NAO_ENTRA()
    {
        var (db, tx, c, _) = await PrepararAsync("ganha-sem-valor");
        using var _1 = db; using var _2 = tx;

        db.Negociacoes.Add(Nova(c, n =>
        {
            n.Status = StatusNegociacao.Ganha;
            n.GanhaEm = DateTime.UtcNow;
            n.Valor = null;
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task GANHA_COM_VALOR_ZERO_TAMBEM_NAO()
    {
        // Zero passaria por "preenchido" e continuaria sendo faturamento nenhum.
        var (db, tx, c, _) = await PrepararAsync("ganha-valor-zero");
        using var _1 = db; using var _2 = tx;

        db.Negociacoes.Add(Nova(c, n =>
        {
            n.Status = StatusNegociacao.Ganha;
            n.GanhaEm = DateTime.UtcNow;
            n.Valor = 0m;
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ABERTA_SEM_VALOR_ENTRA()
    {
        // O outro lado do mesmo CHECK, e o caso comum: o lead que chega pelo WhatsApp não tem
        // valor nenhum. Exigir valor na entrada faria o webhook inventar um número.
        var (db, tx, c, _) = await PrepararAsync("aberta-sem-valor");
        using var _1 = db; using var _2 = tx;

        db.Negociacoes.Add(Nova(c, n => n.Valor = null));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Negociacoes.CountAsync());   // a do cenário + esta
    }

    [Fact]
    public async Task PERDIDA_SEM_VALOR_ENTRA()
    {
        // Perder sem nunca ter estimado é rotina, e cobrar valor de uma perda seria cobrar um
        // número que ninguém tem.
        var (db, tx, c, _) = await PrepararAsync("perdida-sem-valor");
        using var _1 = db; using var _2 = tx;

        db.Negociacoes.Add(Nova(c, n =>
        {
            n.Status = StatusNegociacao.Perdida;
            n.PerdidaEm = DateTime.UtcNow;
            n.MotivoPerda = "achou caro";
            n.Valor = null;
        }));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Negociacoes.CountAsync());
    }

    // ==================================================================== ck_negociacoes_terminal
    [Fact]
    public async Task GANHA_E_PERDIDA_CONTINUAM_SE_EXCLUINDO()
    {
        // É o `ck_contatos_terminal` de antes, no lugar novo: um negócio não acaba das duas
        // formas, e uma linha assim apareceria nos dois relatórios ao mesmo tempo.
        var (db, tx, c, _) = await PrepararAsync("terminal");
        using var _1 = db; using var _2 = tx;

        db.Negociacoes.Add(Nova(c, n =>
        {
            n.Status = StatusNegociacao.Ganha;
            n.Valor = 100m;
            n.GanhaEm = DateTime.UtcNow;
            n.PerdidaEm = DateTime.UtcNow;
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>⚠️ CANCELADA MANTÉM `ganha_em`, e isso é de propósito.
    ///
    /// Ela FOI ganha e depois desfeita — apagar o carimbo perderia quando. Quem a tira do
    /// faturamento é o filtro do índice (`status <> 'cancelada'`), não o campo em branco. É a
    /// mesma separação que o NEG-2 fez entre concluir e cancelar.</summary>
    [Fact]
    public async Task CANCELADA_GUARDA_QUANDO_FOI_GANHA()
    {
        var (db, tx, c, _) = await PrepararAsync("cancelada");
        using var _1 = db; using var _2 = tx;

        var quando = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        db.Negociacoes.Add(Nova(c, n =>
        {
            n.Status = StatusNegociacao.Cancelada;
            n.Valor = 250m;
            n.GanhaEm = quando;
            n.CanceladaEm = DateTime.UtcNow;
            n.CanceladaPor = c.Dono.Id;
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var salva = await db.Negociacoes.SingleAsync(n => n.Status == StatusNegociacao.Cancelada);
        Assert.Equal(quando, salva.GanhaEm);
    }

    // ==================================================================== tenant
    /// <summary>===================== QUEM GARANTE É A FK COMPOSTA =====================
    /// O filtro de consulta protege LEITURA, não escrita. Sem `(etapa_id, empresa_id)` na FK, um
    /// bug de aplicação põe o negócio numa etapa do vizinho — e o sintoma seria um card sumindo
    /// do quadro de uma empresa e aparecendo no da outra.
    /// =========================================================================</summary>
    [Fact]
    public async Task NAO_DA_PARA_POR_O_NEGOCIO_NUMA_ETAPA_DE_OUTRA_EMPRESA()
    {
        var (db, tx, c, _) = await PrepararAsync("tenant-etapa");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "neg-vizinha-etapa");

        db.Negociacoes.Add(Nova(c, n => n.EtapaId = vizinha.PrimeiraEtapa.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NAO_DA_PARA_ABRIR_NEGOCIO_PARA_CONTATO_DE_OUTRA_EMPRESA()
    {
        var (db, tx, c, _) = await PrepararAsync("tenant-contato");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "neg-vizinha-contato");

        db.Negociacoes.Add(Nova(c, n => n.ContatoId = vizinha.Contato.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NAO_DA_PARA_POR_O_NEGOCIO_NUMA_PIPELINE_DE_OUTRA_EMPRESA()
    {
        var (db, tx, c, _) = await PrepararAsync("tenant-pipeline");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "neg-vizinha-pipeline");

        db.Negociacoes.Add(Nova(c, n => n.PipelineId = vizinha.Pipeline.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_EMPRESA_NAO_ENXERGA_NEGOCIO_DE_OUTRA()
    {
        var (db, tx, c, _) = await PrepararAsync("tenant-invisivel");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "neg-vizinha-invisivel");

        // O contexto é o da MINHA empresa. O cenário da vizinha já semeou a negociação dela.
        Assert.Equal(1, await db.Negociacoes.CountAsync());
        Assert.Equal(c.Id, (await db.Negociacoes.SingleAsync()).EmpresaId);

        // E a linha da vizinha existe de verdade: o teste não passa por a tabela estar vazia.
        Assert.Equal(2, await db.Negociacoes.IgnoreQueryFilters().CountAsync());
        Assert.NotEqual(c.Id, vizinha.Negociacao.EmpresaId);
    }

    // ==================================================================== restrict
    /// <summary>A negociação é o registro do negócio: nenhuma das pontas pode levá-la junto ao
    /// sumir. Apagar contato com negócio na mão tem de ser recusado, e não apagar histórico de
    /// faturamento em silêncio — é o oposto de `contatos_etiquetas`, onde a cascata é o certo
    /// porque etiqueta é vocabulário.</summary>
    [Fact]
    public async Task APAGAR_O_CONTATO_NAO_LEVA_A_NEGOCIACAO_JUNTO()
    {
        var (db, tx, c, _) = await PrepararAsync("restrict-contato");
        using var _1 = db; using var _2 = tx;

        db.Contatos.Remove(await db.Contatos.SingleAsync(x => x.Id == c.Contato.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ==================================================================== o canal, que é a exceção
    /// <summary>⚠️ A ÚNICA FK DESTA TABELA QUE NÃO É `Restrict` NEM COMPOSTA, E AS DUAS COISAS
    /// ANDAM JUNTAS.
    ///
    /// `SetNull` não pode ser composta — anularia `empresa_id` junto, que é NOT NULL. E tem de ser
    /// `SetNull` porque `ServicoCanais` APAGA canal: com `Restrict`, apagar uma campanha que um
    /// dia trouxe um negócio viraria 500 na cara do dono. As outras duas colunas que apontam para
    /// canal (`vendas.canal_id` e `conversas.canal_ciclo_id`) resolveram assim pelo mesmo motivo.
    ///
    /// Sem este teste, o defeito só apareceria no dia em que alguém apagasse uma campanha antiga.</summary>
    [Fact]
    public async Task APAGAR_A_CAMPANHA_NAO_E_IMPEDIDO_E_SO_LIMPA_A_COLUNA()
    {
        var (db, tx, c, _) = await PrepararAsync("canal-setnull");
        using var _1 = db; using var _2 = tx;

        var canal = new CanalCaptacao
        {
            EmpresaId = c.Id,
            ConexaoId = c.Conexao.Id,
            Nome = "Panfleto de julho",
            Codigo = "PF07"
        };
        db.CanaisCaptacao.Add(canal);
        await db.SaveChangesAsync();

        var negociacao = Nova(c, n => n.CanalCicloId = canal.Id);
        db.Negociacoes.Add(negociacao);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Apagar a campanha NÃO é recusado…
        db.CanaisCaptacao.Remove(await db.CanaisCaptacao.SingleAsync(x => x.Id == canal.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // …e o negócio continua lá, só sem a anotação de origem.
        var salva = await db.Negociacoes.SingleAsync(n => n.Id == negociacao.Id);
        Assert.Null(salva.CanalCicloId);
    }

    // ==================================================================== xmin
    /// <summary>O TOKEN DE CONCORRÊNCIA DO ARRASTO, que migrou de `contatos` junto com a posição
    /// no quadro — ela é que precisa da proteção.
    ///
    /// O cliente devolve a versão que pintou na tela; se outra pessoa moveu o card no meio do
    /// caminho, a escrita afeta zero linhas e vira 409 em vez de sobrescrever em silêncio. Aqui a
    /// versão velha é forjada no `OriginalValue`, que é o mesmo que o navegador mandaria.</summary>
    [Fact]
    public async Task MOVER_COM_VERSAO_VELHA_NAO_SOBRESCREVE()
    {
        var (db, tx, c, _) = await PrepararAsync("xmin");
        using var _1 = db; using var _2 = tx;

        var negociacao = await db.Negociacoes.SingleAsync();
        negociacao.OrdemKanban = 2000m;

        // A versão que o cliente teria pintado antes de alguém mexer.
        db.Entry(negociacao).Property(x => x.Versao).OriginalValue = 1u;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MOVER_COM_A_VERSAO_CERTA_FUNCIONA()
    {
        // O par do teste acima: sem ele, um mapeamento que recusasse TUDO passaria no primeiro.
        var (db, tx, c, _) = await PrepararAsync("xmin-ok");
        using var _1 = db; using var _2 = tx;

        var negociacao = await db.Negociacoes.SingleAsync();
        negociacao.OrdemKanban = 2000m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(2000m, (await db.Negociacoes.SingleAsync()).OrdemKanban);
    }


    // ==================================================================== o que a FK nova exige
    /// <summary>⚠️ ESTE TESTE EXISTE PORQUE A TABELA NOVA QUEBROU UM CAMINHO QUE JA FUNCIONAVA.
    ///
    /// `ServicoEtapas.RemoverAsync` movia os CONTATOS para a etapa de destino e apagava a coluna.
    /// Com `fk_negociacoes_etapa` sendo `Restrict` igual a do contato, deixar a negociação para
    /// trás faz o banco recusar — e o dono levaria um 500 numa tela de configuração.
    ///
    /// Verificado tirando o `ExecuteUpdate` das negociações: o teste reprova com
    /// `PostgresException 23503 ... fk_negociacoes_etapa`.</summary>
    [Fact]
    public async Task APAGAR_A_ETAPA_LEVA_A_NEGOCIACAO_PARA_O_DESTINO()
    {
        var (db, tx, c, ctx) = await PrepararAsync("etapa-destino");
        using var _1 = db; using var _2 = tx;

        var origem = c.PrimeiraEtapa;
        var destino = c.Etapas[1];
        var servico = new ServicoEtapas(db, ctx);

        // O contato e a negociação dele estão os dois na primeira etapa, que é como o backfill
        // deixa todo mundo.
        await servico.RemoverAsync(origem.Id, destino.Id, default);
        db.ChangeTracker.Clear();

        // ⚠️ SO HA UMA COLUNA PARA CONFERIR (E4e/4). O teste dizia "e o contato foi junto",
        // porque as duas tabelas apontavam para etapa e podiam acabar em colunas diferentes — o
        // card numa, o negócio noutra. Nao ha mais duas.
        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(destino.Id, negociacao.EtapaId);
    }

    /// <summary>A etapa com negócio e SEM contato é o caso que ainda não existe — hoje os dois
    /// andam juntos. O E4b desatrela, e aí a pergunta tem de falar de negócio.
    ///
    /// Sem esta ramificação a mensagem diria "Esta etapa tem 0 contatos", que é pior que o 500:
    /// manda o dono procurar uma coluna vazia que o banco insiste em não apagar.</summary>
    [Fact]
    public async Task ETAPA_COM_NEGOCIO_E_SEM_CONTATO_PEDE_DESTINO_FALANDO_DE_NEGOCIO()
    {
        var (db, tx, c, ctx) = await PrepararAsync("etapa-so-negocio");
        using var _1 = db; using var _2 = tx;

        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => new ServicoEtapas(db, ctx).RemoverAsync(c.PrimeiraEtapa.Id, null, default));

        Assert.Contains("1 negócio", erro.Message);
    }

    /// <summary>Apagar o FUNIL inteiro com negócio dentro: a recusa tem de ser uma frase, não uma
    /// violação de FK. É o mesmo argumento que `ServicoPipelines` já escreveu sobre a contagem
    /// crua de contatos — contar o que a FK enxerga, e não o que o quadro mostra.</summary>
    [Fact]
    public async Task APAGAR_O_FUNIL_COM_NEGOCIO_E_RECUSADO_COM_EXPLICACAO()
    {
        var (db, tx, c, ctx) = await PrepararAsync("funil-com-negocio");
        using var _1 = db; using var _2 = tx;

        var servico = new ServicoPipelines(db, ctx);
        var outraId = await servico.CriarAsync(new NovaPipeline("Atacado", null), default);

        // A negociação muda de funil; o contato NÃO vai junto. Essa é justamente a combinação
        // que o modelo velho não sabia representar — e é a que deixa o funil com zero contatos e
        // um negócio dentro.
        var laFora = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == outraId).OrderBy(e => e.Ordem).FirstAsync();

        await db.Negociacoes.Where(n => n.Id == c.Negociacao.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.PipelineId, outraId)
                .SetProperty(n => n.EtapaId, laFora.Id));
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.RemoverAsync(outraId, default));

        Assert.Contains("negócio", erro.Message);
        Assert.True(await db.Pipelines.AnyAsync(p => p.Id == outraId));
    }

    // ====================================================================
    /// <summary>Uma negociação aberta válida, para o teste mexer só no que lhe interessa.</summary>
    private static Negociacao Nova(Cenario c, Action<Negociacao>? ajuste = null)
    {
        var n = new Negociacao
        {
            EmpresaId = c.Id,
            ContatoId = c.Contato.Id,
            PipelineId = c.Pipeline.Id,
            EtapaId = c.PrimeiraEtapa.Id,
            ResponsavelId = c.Dono.Id,
            OrdemKanban = 3000m,
            Status = StatusNegociacao.Aberta
        };
        ajuste?.Invoke(n);
        return n;
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, Cenario, ContextoMutavel)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"negociacao-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, cenario, ctx);
    }
}
