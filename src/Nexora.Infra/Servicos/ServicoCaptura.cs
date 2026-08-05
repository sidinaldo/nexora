using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A captação por formulário do site.
///
/// ===================== ROTA PÚBLICA, ESCRITA, NA INTERNET =====================
/// Este é o segundo endpoint do sistema que aceita escrita sem sessão (o outro é o webhook da
/// Evolution). Cinco camadas de proteção, e nenhuma delas sozinha basta:
///
///   1. CHAVE POR FORMULÁRIO — identifica e permite revogar um sem derrubar os outros;
///   2. RATE LIMIT por IP (no controller, política `captura`);
///   3. ORIGEM PERMITIDA — corta o abuso trivial pelo navegador;
///   4. HONEYPOT — bot que preenche o campo escondido recebe 200 e vai embora;
///   5. VALIDAÇÃO REAL — telefone canonicalizado, nome com tamanho mínimo, corpo com teto.
/// ==============================================================================
///
/// ===================== TENANT ZERO =====================
/// Sem sessão, `contexto.EmpresaId` é 0 e o query filter global devolve VAZIO EM SILÊNCIO. Toda
/// consulta aqui usa `IgnoreQueryFilters()` mais filtro explícito por `empresaId` — a empresa vem
/// da CHAVE, e é a única fonte de verdade neste caminho.
///
/// É a armadilha nº 1 do inventário, e o modo de falha é o pior possível: o lead simplesmente
/// não aparece, sem erro em lugar nenhum.
/// =======================================================</summary>
public class ServicoCaptura(
    NexoraDbContext db,
    INotificadorPainel painel,
    TimeProvider relogio,
    ILogger<ServicoCaptura> log) : IServicoCaptura
{
    public const int TamanhoMinimoNome = 2;
    public const int TamanhoMaximoMensagem = 2000;

    public async Task<ResultadoCaptura> ReceberAsync(
        string chave, LeadDoFormulario lead, string? origem, CancellationToken ct)
    {
        // ===== CAMADA 4: HONEYPOT, ANTES DE TUDO =====
        // Vem primeiro de propósito: se o campo escondido veio preenchido, não há por que tocar
        // no banco. E quem chama devolve 200 — bot que recebe erro tenta de novo com outra
        // variação; bot que recebe sucesso risca o alvo da lista.
        if (!string.IsNullOrWhiteSpace(lead.Armadilha))
        {
            log.LogInformation("Captura descartada pelo honeypot (chave {Chave}).", Mascarar(chave));
            return ResultadoCaptura.DescartadoComoBot;
        }

        // ===== CAMADA 1: A CHAVE RESOLVE O TENANT =====
        var formulario = await db.FormulariosCaptura.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(f => f.Chave == chave, ct);

        // Mesma mensagem para chave inexistente e formulário desligado: distinguir contaria a
        // quem sonda que a chave existe e só está pausada.
        if (formulario is null || !formulario.Ativo)
        {
            log.LogWarning("Captura recusada: chave inválida ou formulário inativo ({Chave}).",
                Mascarar(chave));
            throw new RegraDeNegocioException("Formulário não encontrado.");
        }

        // ===== CAMADA 3: ORIGEM PERMITIDA =====
        ExigirOrigemPermitida(formulario, origem);

        // ===== CAMADA 5: VALIDAÇÃO REAL =====
        var nome = (lead.Nome ?? "").Trim();
        if (nome.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException("Informe seu nome.");
        if (nome.Length > 120) nome = nome[..120];

        if (!CanonicalizadorTelefone.EhValido(lead.Telefone))
            throw new RegraDeNegocioException("Informe um telefone válido com DDD.");

        var telefone = CanonicalizadorTelefone.Canonicalizar(lead.Telefone!);

        var mensagem = (lead.Mensagem ?? "").Trim();
        if (mensagem.Length > TamanhoMaximoMensagem) mensagem = mensagem[..TamanhoMaximoMensagem];

        var email = (lead.Email ?? "").Trim();
        if (email.Length > 160) email = email[..160];

        var resultado = await GravarAsync(formulario, nome, telefone, email, mensagem, ct);

        // Contador de envios ACEITOS, incrementado no SQL: dois formulários postando ao mesmo
        // tempo não podem sobrescrever a contagem um do outro.
        await db.FormulariosCaptura.IgnoreQueryFilters()
            .Where(f => f.Id == formulario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LeadsRecebidos, f => f.LeadsRecebidos + 1), ct);

        return resultado;
    }

    /// <summary>Compara o HOST do cabeçalho `Origin` com o domínio cadastrado.
    ///
    /// Compara só o host, não a string inteira: o navegador manda `https://www.cliente.com.br`
    /// (com esquema, sem barra final), e o cliente cadastra `www.cliente.com.br`. Exigir que ele
    /// acerte o formato exato seria transformar uma proteção em suporte técnico.</summary>
    private static void ExigirOrigemPermitida(FormularioCaptura formulario, string? origem)
    {
        if (string.IsNullOrWhiteSpace(formulario.DominioPermitido)) return;

        // Sem `Origin` no cabeçalho não há navegador na frente (curl, app, servidor). A camada 3
        // existe para cortar abuso VIA NAVEGADOR; recusar aqui bloquearia integração legítima
        // por servidor sem impedir absolutamente nada de quem monta a requisição à mão.
        if (string.IsNullOrWhiteSpace(origem)) return;

        var host = Uri.TryCreate(origem, UriKind.Absolute, out var uri) ? uri.Host : origem;

        if (!string.Equals(host, formulario.DominioPermitido.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new RegraDeNegocioException("Origem não permitida para este formulário.");
    }

    private async Task<ResultadoCaptura> GravarAsync(
        FormularioCaptura formulario, string nome, string telefone, string email, string mensagem,
        CancellationToken ct)
    {
        var empresaId = formulario.EmpresaId;
        var agora = relogio.GetUtcNow().UtcDateTime;

        // ===================== DUPLICATA: CONSULTA, NÃO EXCEÇÃO =====================
        // O mesmo telefone chega de novo o tempo todo — a pessoa preenche duas vezes, ou já é
        // cliente. `uq_contatos_telefone` barraria, mas erro de banco não é fluxo de controle:
        // ele vira 500 no formulário do site do cliente.
        //
        // O predicado repete `anonimizado_em IS NULL` porque o índice é PARCIAL. Sem isso, um
        // contato anonimizado bloquearia para sempre a recaptura daquele número.
        var existente = await db.Contatos.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == empresaId && c.Telefone == telefone && c.AnonimizadoEm == null)
            .Select(c => new { c.Id, c.Nome, c.ResponsavelId })
            .FirstOrDefaultAsync(ct);

        if (existente is not null)
        {
            // Lembrete MANUAL para quem já cuida dele. Manual e não automático de propósito: o
            // automático é do motor de follow-up, tem teto diário e semântica de robô. Este é um
            // recado de pessoa para pessoa — "o cliente preencheu o formulário de novo".
            db.Lembretes.Add(new Lembrete
            {
                EmpresaId = empresaId,
                ContatoId = existente.Id,
                Origem = OrigemLembrete.Manual,
                Status = StatusLembrete.Pendente,
                DataAlvo = DateOnly.FromDateTime(agora),
                Titulo = $"Preencheu o formulário {formulario.Nome} de novo",
                Observacao = TextoDoLembrete(nome, telefone, email, mensagem),
                EnviaMensagem = false,
                ResponsavelId = existente.ResponsavelId
            });
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Captura: telefone já conhecido (contato {Id}); lembrete criado em vez de contato.",
                existente.Id);

            return ResultadoCaptura.LembreteParaContatoExistente;
        }

        var etapaId = await db.EtapasFunil.IgnoreQueryFilters()
            .Where(e => e.EmpresaId == empresaId)
            .OrderBy(e => e.Ordem)
            .Select(e => (long?)e.Id)
            .FirstOrDefaultAsync(ct);

        if (etapaId is null)
        {
            // Empresa sem funil não deveria existir (o cadastro semeia as etapas). Falhar alto
            // aqui é melhor que gravar contato órfão de etapa.
            log.LogError("Empresa {Id} sem etapa de funil — captura não pode criar contato.", empresaId);
            throw new RegraDeNegocioException("Este formulário não está configurado corretamente.");
        }

        var contato = new Contato
        {
            EmpresaId = empresaId,
            Nome = nome,
            Telefone = telefone,
            Email = email.Length == 0 ? null : email,
            Origem = OrigemLead.Site,
            OrigemDetalhe = formulario.Nome,
            EtapaId = etapaId.Value,
            // SEM responsável: cai em "não atribuídas" para alguém assumir, igual ao lead que
            // chega pelo WhatsApp.
            ResponsavelId = null,
            OrdemKanban = 0m,
            Observacoes = mensagem.Length == 0 ? null : mensagem
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync(ct);

        await CriarLembreteDePrimeiroContatoAsync(empresaId, contato.Id, formulario.Nome, agora, ct);

        // ===================== NENHUMA MENSAGEM DE WHATSAPP =====================
        // O lead deu o telefone num formulário; ele NÃO iniciou conversa. Mensagem não
        // solicitada é o caminho curto para o número ser denunciado — e o Nexora roda em rota
        // não-oficial, onde banimento é risco real e sem recurso.
        //
        // Só a notificação do PAINEL sai daqui, para o badge subir na hora.
        // =======================================================================
        await painel.ContatoCriadoAsync(empresaId,
            new ContatoPainel(contato.Id, contato.Nome, contato.Telefone, contato.EtapaId), ct);

        log.LogInformation("Captura: contato {Id} criado pelo formulário {Form}.",
            contato.Id, formulario.Nome);

        return ResultadoCaptura.ContatoCriado;
    }

    /// <summary>O lembrete de primeiro contato — o lead chegou, mas ninguém falou com ele ainda.
    ///
    /// ===================== SOBRE O TETO DIÁRIO =====================
    /// `uq_lembrete_teto_diario` é um índice PARCIAL: ele só cobre
    /// `origem = 'automatico' AND envia_mensagem AND status <> 'cancelado'`.
    ///
    /// Este lembrete tem `envia_mensagem = false` — obrigatoriamente, porque a captura não pode
    /// disparar WhatsApp. Ou seja: o índice NÃO o cobre, e o banco não impediria um segundo.
    ///
    /// Então a guarda é da aplicação, e explícita: já existe lembrete automático pendente para
    /// este contato hoje? Não cria outro. Ela cobre tanto um segundo lembrete de captura quanto
    /// o do motor de follow-up, que é quem o teto foi feito para conter.
    /// ==============================================================</summary>
    private async Task CriarLembreteDePrimeiroContatoAsync(
        long empresaId, long contatoId, string nomeFormulario, DateTime agora, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(agora);

        var jaTem = await db.Lembretes.IgnoreQueryFilters().AnyAsync(
            l => l.EmpresaId == empresaId
              && l.ContatoId == contatoId
              && l.DataAlvo == hoje
              && l.Origem == OrigemLembrete.Automatico
              && l.Status != StatusLembrete.Cancelado, ct);

        if (jaTem)
        {
            log.LogInformation(
                "Contato {Id} já tem lembrete automático hoje; captura não gera outro.", contatoId);
            return;
        }

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = empresaId,
            ContatoId = contatoId,
            Origem = OrigemLembrete.Automatico,
            Status = StatusLembrete.Pendente,
            DataAlvo = hoje,
            Titulo = $"Primeiro contato — veio do formulário {nomeFormulario}",
            EnviaMensagem = false,
            ResponsavelId = null
        });

        await db.SaveChangesAsync(ct);
    }

    private static string TextoDoLembrete(string nome, string telefone, string email, string mensagem)
    {
        var partes = new List<string> { $"Nome informado: {nome}", $"Telefone: {telefone}" };
        if (email.Length > 0) partes.Add($"E-mail: {email}");
        if (mensagem.Length > 0) partes.Add($"Mensagem: {mensagem}");
        return string.Join('\n', partes);
    }

    /// <summary>Log NUNCA leva a chave inteira: ela é o que abre o endpoint, e log de aplicação
    /// costuma acabar em ferramenta de terceiro.</summary>
    private static string Mascarar(string chave) =>
        chave.Length <= 8 ? "********" : $"{chave[..4]}…{chave[^4..]}";

    /// <summary>24 bytes em hex. Gerado por RNG criptográfico, não por `Guid`: a chave viaja em
    /// URL pública, e adivinhá-la daria escrita no funil de outra empresa.</summary>
    public static string GerarChave() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
