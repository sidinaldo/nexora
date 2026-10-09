using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Whatsapp;

/// <summary>===================== COMO A AUTOMATICA SAI (INT-XX) =====================
///
/// RODA SEM TENANT, nos motores: tudo por IgnoreQueryFilters, recortado pela empresa da mensagem.
///
/// A ordem das perguntas:
///   1. a janela permite texto livre? (Evolution sempre; API oficial so nas 24h) → texto livre;
///   2. ha template escolhido para ESTA automacao? → senao, nao sai;
///   3. ele esta aprovado, e e da conexao por onde a mensagem sai? → senao, nao sai;
///   4. sai o template, preenchido para este cliente.
///
/// O lembrete que nasceu AUTOMATICO e o follow-up, e usa o template dele; o manual usa o do
/// lembrete. A reserva grava os dois como `lembrete` — quem separa e a origem do lembrete.
/// ===============================================================================</summary>
public class SaidaDaAutomatica(NexoraDbContext db, TimeProvider relogio) : ISaidaDaAutomatica
{
    public async Task<SaidaAutomatica> DecidirAsync(Mensagem mensagem, TipoAutomacao tipo, CancellationToken ct)
    {
        var canal = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == mensagem.ConexaoId && c.EmpresaId == mensagem.EmpresaId)
            .Select(c => c.Canal)
            .FirstOrDefaultAsync(ct);

        // Na Evolution nao ha janela que barre: texto livre, sem ler mais nada.
        if (canal != CanalWhatsapp.CloudApi) return SaidaAutomatica.TextoLivre;

        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == mensagem.ConversaId && c.EmpresaId == mensagem.EmpresaId)
            .Select(c => new { c.UltimaEntradaEm, ContatoNome = c.Contato.Nome, c.ResponsavelId })
            .FirstOrDefaultAsync(ct);
        if (conversa == null) return SaidaAutomatica.TextoLivre;

        var agora = relogio.GetUtcNow().UtcDateTime;
        if (Janela24h.PermiteTextoLivre(canal, conversa.UltimaEntradaEm, agora)) return SaidaAutomatica.TextoLivre;

        var automacao = await AutomacaoAsync(mensagem, tipo, ct);

        var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == mensagem.EmpresaId)
            .Select(e => new { e.Nome, e.ModeloFollowUpId, e.ModeloLembreteId, e.ModeloNpsId })
            .FirstAsync(ct);

        long? modeloId;
        string qual;
        if (automacao == TipoAutomacao.FollowUp)
        {
            modeloId = empresa.ModeloFollowUpId;
            qual = "o follow-up";
        }
        else if (automacao == TipoAutomacao.Lembrete)
        {
            modeloId = empresa.ModeloLembreteId;
            qual = "o lembrete";
        }
        else
        {
            modeloId = empresa.ModeloNpsId;
            qual = "a pesquisa de satisfação";
        }

        const string Fechou = "A janela de 24h do WhatsApp fechou";
        if (modeloId == null)
            return SaidaAutomatica.NaoSai($"{Fechou} e não há template escolhido para {qual} em Configurações.");

        var modelo = await db.ModelosMensagem.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == modeloId && m.EmpresaId == mensagem.EmpresaId, ct);
        if (modelo == null || modelo.Status != StatusModelo.Aprovado)
            return SaidaAutomatica.NaoSai($"{Fechou} e o template escolhido para {qual} não está aprovado pela Meta.");
        if (modelo.ConexaoId != mensagem.ConexaoId)
            return SaidaAutomatica.NaoSai($"{Fechou} e o template escolhido para {qual} é de outro número.");

        string? vendedor = null;
        if (conversa.ResponsavelId != null)
            vendedor = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == conversa.ResponsavelId)
                .Select(u => u.Nome)
                .FirstOrDefaultAsync(ct);

        var dados = new DadosDoModelo(conversa.ContatoNome, empresa.Nome, vendedor);
        return SaidaAutomatica.PorModelo(
            new ModeloParaEnvio(modelo.Nome, modelo.Idioma, PreenchedorModelo.Valores(modelo.Variaveis, dados)),
            modelo.Id,
            PreenchedorModelo.Preencher(modelo.Corpo, dados));
    }

    /// <summary>O lembrete AUTOMATICO e o follow-up.</summary>
    private async Task<TipoAutomacao> AutomacaoAsync(Mensagem mensagem, TipoAutomacao tipo, CancellationToken ct)
    {
        if (tipo != TipoAutomacao.Lembrete || mensagem.LembreteId == null) return tipo;

        var origem = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == mensagem.LembreteId)
            .Select(l => l.Origem)
            .FirstOrDefaultAsync(ct);

        if (origem == OrigemLembrete.Automatico) return TipoAutomacao.FollowUp;
        return TipoAutomacao.Lembrete;
    }
}
