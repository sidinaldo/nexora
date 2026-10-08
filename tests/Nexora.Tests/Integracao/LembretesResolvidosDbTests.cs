using Nexora.Core.Entidades;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>Os lembretes resolvidos da ficha vêm paginados do banco, com o total (AUD-XX). A ficha
/// os recebia inteiros no detalhe e paginava na tela.</summary>
[Collection("banco")]
public class LembretesResolvidosDbTests(BancoTeste banco)
{
    [Fact]
    public async Task OS_RESOLVIDOS_PAGINAM_NO_BANCO_E_O_DETALHE_TRAZ_SO_OS_PENDENTES()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "lembretes-resolvidos");
        using var _ = db; using var __ = tx;

        var contato = amb.Cenario.Contato.Id;
        for (var i = 0; i < 25; i++)
        {
            db.Lembretes.Add(new Lembrete
            {
                EmpresaId = amb.Cenario.Id, ContatoId = contato, Origem = OrigemLembrete.Manual,
                DataAlvo = new DateOnly(2026, 7, 1).AddDays(i), Titulo = $"resolvido {i}",
                Status = i % 2 == 0 ? StatusLembrete.Concluido : StatusLembrete.Cancelado
            });
        }
        for (var i = 0; i < 2; i++)
        {
            db.Lembretes.Add(new Lembrete
            {
                EmpresaId = amb.Cenario.Id, ContatoId = contato, Origem = OrigemLembrete.Manual,
                DataAlvo = new DateOnly(2026, 8, 10), Titulo = $"pendente {i}"
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var servico = new ServicoLembretes(db, amb.Contexto, amb.Relogio);

        var segunda = await servico.ResolvidosDoContatoAsync(contato, 2, 20, default);
        Assert.Equal(5, segunda.Itens.Count);
        Assert.Equal(25, segunda.TotalCount);
        Assert.Equal(2, segunda.TotalPaginas);
        Assert.All(segunda.Itens, l => Assert.NotEqual("pendente", l.Status));

        var detalhe = await amb.Contatos.DetalheAsync(contato, default);
        Assert.Equal(2, detalhe.Lembretes.Count);
        Assert.All(detalhe.Lembretes, l => Assert.Equal("pendente", l.Status));

        // A outra empresa não vê nada, nem pedindo pelo id.
        var alheia = await Semeador.TenantAsync(db, "lembretes-resolvidos-vizinha");
        db.ChangeTracker.Clear();
        var daVizinha = await servico.ResolvidosDoContatoAsync(alheia.Contato.Id, 1, 20, default);
        Assert.Equal(0, daVizinha.TotalCount);
    }
}
