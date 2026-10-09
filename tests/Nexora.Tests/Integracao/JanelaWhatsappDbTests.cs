using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A JANELA DO WHATSAPP NA CAIXA E NO FUNIL (INT-XX).
///
/// As duas telas recebem a janela pronta e so a pintam. O que este teste prova e a ponta do
/// servidor: o canal sai da conexao DA CONVERSA, e o instante sai de `ultima_entrada_em` — pelo SQL
/// de verdade, que e onde a traducao do canal poderia quebrar.</summary>
[Collection("banco")]
public class JanelaWhatsappDbTests(BancoTeste banco)
{
    private static readonly DateTime Entrada = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_CAIXA_E_O_FUNIL_RECEBEM_A_JANELA_DO_CANAL_DA_CONVERSA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "janela-telas");
        using var _ = db; using var __ = tx;

        var conversaId = amb.Cenario.Conversa.Id;
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UltimaEntradaEm, Entrada));
        db.ChangeTracker.Clear();

        // ---- Evolution: a janela aparece, e nao bloqueia ----
        var (caixa, card) = await LerAsync(db, amb);
        Assert.False(caixa.Janela!.Bloqueia);
        Assert.Equal(Entrada.AddHours(24), caixa.Janela.FechaEm);
        Assert.Equal(Entrada.AddHours(22), caixa.Janela.AvisoEm);
        Assert.Equal(caixa.Janela, card.Janela);

        // ---- a mesma conversa num numero da Cloud API: agora bloqueia ----
        var oficial = new Conexao
        {
            EmpresaId = amb.Cenario.Id, Nome = "Oficial", InstanceName = "cloud-janela-telas",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = "1090000000101", WabaId = "2090000000101",
            AccessTokenCifrado = "v1.x", AppSecretCifrado = "v1.y"
        };
        db.Conexoes.Add(oficial);
        await db.SaveChangesAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConexaoId, oficial.Id));
        db.ChangeTracker.Clear();

        (caixa, card) = await LerAsync(db, amb);
        Assert.Equal("cloud_api", caixa.Canal);
        Assert.True(caixa.Janela!.Bloqueia);
        Assert.True(card.Janela!.Bloqueia);
        Assert.Equal(Entrada.AddHours(24), card.Janela.FechaEm);
    }

    private static async Task<(Core.Servicos.ConversaResumo Caixa, Core.Servicos.CardFunil Card)> LerAsync(
        Nexora.Infra.Persistencia.NexoraDbContext db, ContatosDbTests.Ambiente amb)
    {
        var caixa = await new ServicoCaixa(db, amb.Contexto).ConversaAsync(amb.Cenario.Conversa.Id, default);
        var coluna = await amb.Funil.ColunaAsync(amb.Cenario.Negociacao.EtapaId, null, null, 50, default);
        var card = coluna.Itens.Single(c => c.Id == amb.Cenario.Negociacao.Id);
        return (caixa!, card);
    }
}
