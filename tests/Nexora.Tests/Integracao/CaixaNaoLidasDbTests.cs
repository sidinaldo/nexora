using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>"Marcar como lida" devolve o total NOVO de não lidas da empresa — o badge do menu
/// (AUD-XX). A tela não descontava nada ao ler, e o número só caía no próximo poll.</summary>
[Collection("banco")]
public class CaixaNaoLidasDbTests(BancoTeste banco)
{
    [Fact]
    public async Task MARCAR_LIDA_DEVOLVE_O_TOTAL_DA_EMPRESA_DEPOIS_DE_LER()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "caixa-nao-lidas");
        var outra = await Semeador.TenantAsync(db, "caixa-nao-lidas-vizinha");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // Uma segunda conversa aberta na mesma empresa, com 3 não lidas.
        var contato = new Contato { EmpresaId = cenario.Id, Nome = "Segunda", Telefone = "5584980004001" };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        var segunda = new Conversa
        {
            EmpresaId = cenario.Id, ContatoId = contato.Id, ConexaoId = cenario.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow, NaoLidas = 3
        };
        db.Conversas.Add(segunda);
        await db.SaveChangesAsync();

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == cenario.Conversa.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.NaoLidas, 4));
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == outra.Conversa.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.NaoLidas, 9));
        db.ChangeTracker.Clear();

        var total = await new ServicoCaixa(db, ctx).MarcarLidaAsync(cenario.Conversa.Id, default);

        // Sobram as 3 da segunda conversa; as 9 da outra empresa não entram.
        Assert.Equal(3, total);
    }
}
