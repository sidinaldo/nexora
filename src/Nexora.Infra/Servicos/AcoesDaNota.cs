using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Tempo;
using Nexora.Core.Texto;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Ver `IAcoesDaNota` para o porque de cada faixa. Aqui mora o banco e o envio.</summary>
public class AcoesDaNota(
    NexoraDbContext db,
    EnviadorMensagem enviador,
    TimeProvider relogio,
    ILogger<AcoesDaNota> log) : IAcoesDaNota
{
    /// <summary>9 e 10. O corte de promotor do NPS, que e definicao da metrica e nao escolha.</summary>
    public const short PisoPromotor = 9;

    /// <summary>0 a 6. Entre 7 e 8 e neutro: nao conta no NPS e nao dispara nada.</summary>
    public const short TetoDetrator = 6;

    public async Task ExecutarAsync(long pesquisaId, CancellationToken ct)
    {
        // ⚠️ `IgnoreQueryFilters` COM O `empresa_id` VINDO DA PROPRIA LINHA: isto roda dentro do
        // webhook, sem tenant no contexto. A pesquisa e localizada pela chave primaria, e o que ela
        // devolve define o escopo de tudo o mais.
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisaId)
            .Select(x => new
            {
                x.Id, x.EmpresaId, x.ContatoId, x.NegociacaoId, x.Nota,
                Empresa = new
                {
                    x.Empresa.Nome, x.Empresa.NpsMensagemPromotor, x.Empresa.NpsMensagemDetrator,
                    x.Empresa.FusoHorario, x.Empresa.NpsAtivo
                },
                ContatoNome = x.Contato.Nome,
                Telefone = x.Contato.Telefone,
                // O dono do NEGOCIO, nunca o do contato: a `LiberacaoDeCiclo` zera o do contato ao
                // concluir a venda, e a tarefa cairia no Meu Dia de ninguem. Mesma regra do LPA-1.
                ResponsavelDaVenda = x.Negociacao.ResponsavelId,
                ConversaId = db.Conversas.IgnoreQueryFilters()
                    .Where(c => c.ContatoId == x.ContatoId && c.EmpresaId == x.EmpresaId)
                    .Select(c => (long?)c.Id).FirstOrDefault(),
                ConexaoId = db.Conversas.IgnoreQueryFilters()
                    .Where(c => c.ContatoId == x.ContatoId && c.EmpresaId == x.EmpresaId)
                    .Select(c => (long?)c.ConexaoId).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (p?.Nota == null) return;

        var nota = p.Nota.Value;

        // ---- 0 a 6: a acao humana, que acontece sempre -----------------------------------
        if (nota <= TetoDetrator)
        {
            await AvisarDoDetratorAsync(
                p.EmpresaId, p.ContatoId, p.ConversaId, p.ResponsavelDaVenda, nota,
                p.ContatoNome, p.Empresa.FusoHorario, ct);
        }

        // ---- A mensagem de agradecimento, se configurada ----------------------------------
        // ⚠️ 7 E 8 NAO MANDAM NADA, e nao e esquecimento: neutro nao conta no NPS, e escrever para
        // dizer "obrigado pela sua indiferenca" nao melhora relacao nenhuma.
        var texto =
            nota >= PisoPromotor ? p.Empresa.NpsMensagemPromotor
            : nota <= TetoDetrator ? p.Empresa.NpsMensagemDetrator
            : null;

        if (string.IsNullOrWhiteSpace(texto)) return;

        // ===================== DESLIGADA, NAO SAI MENSAGEM AUTOMATICA =====================
        // ⚠️ A PESQUISA DESLIGADA AINDA RECEBE RESPOSTA: o que ja tinha saido continua no prazo, e
        // o cliente que responde agora responde a uma pergunta que de fato recebeu. A nota e o
        // lembrete do detrator (acima) sao internos e seguem valendo.
        //
        // O que para e a mensagem AO CLIENTE. Quem desliga a pesquisa costuma desligar por causa
        // do cliente — reclamou, achou insistente —, e um "obrigado pela nota!" automatico dias
        // depois seria exatamente o que ele quis parar.
        // ================================================================================
        if (!p.Empresa.NpsAtivo)
        {
            log.LogInformation(
                "Pesquisa {Id}: nota {Nota} registrada, agradecimento nao enviado (pesquisa desligada).",
                p.Id, nota);
            return;
        }

        if (p.ConversaId == null || p.ConexaoId == null)
        {
            // Sem conversa nao ha por onde mandar. Nao e erro: a nota ja esta registrada, e e ela
            // que o relatorio precisa.
            log.LogInformation(
                "Pesquisa {Id}: nota {Nota} registrada, agradecimento nao enviado (sem conversa).",
                p.Id, nota);
            return;
        }

        var instancia = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == p.ConexaoId && c.EmpresaId == p.EmpresaId)
            .Select(c => c.InstanceName)
            .FirstOrDefaultAsync(ct);

        if (instancia == null) return;

        var hoje = DateOnly.FromDateTime(
            FusoDeNegocio.AgoraNo(relogio, FusoDeNegocio.Resolver(p.Empresa.FusoHorario)));

        // ===================== POSTA NA HORA, E A OUTBOX E A REDE =====================
        // ⚠️ RESPOSTA, NAO DISPARO. O agradecimento sai segundos depois de o cliente escrever — e o
        // momento mais natural que existe, e e por isso que ele NAO passa pelo teto diario por
        // contato: aquele freio existe contra automatica NAO SOLICITADA, e esta e a continuacao de
        // uma conversa que o cliente acabou de ter.
        //
        // ⚠️ E O WEBHOOK JA FAZ CHAMADA HTTP PARA A EVOLUTION — `ReceberMidiaAsync` baixa midia por
        // ali. Postar aqui nao abre caminho novo.
        //
        // ⚠️ FALHANDO, NAO HA REENVIO, e e escolha. A linha FICA com o erro (a outbox de `mensagens`
        // registra o que nao saiu), mas nada a tenta de novo: a drenagem do follow-up so pega
        // linha com `lembrete_id`. Um comentario antigo aqui dizia o contrario. E reenviar seria
        // pior que perder: "obrigado pela nota" chegando no dia seguinte, depois de o cliente ja
        // ter seguido a conversa, e uma mensagem fora de contexto. O que importa da nota — o
        // registro e o lembrete do detrator — nao depende desta mensagem.
        //
        // ⚠️ `negociacao_id` NULO DE PROPOSITO. `uq_msg_nps` e unico em `negociacao_id` filtrado por
        // `tipo_automacao = 'nps'`, e a PERGUNTA ja ocupa aquela vaga — preencher aqui faria o
        // agradecimento ser recusado pelo indice. O elo com a venda vive na pesquisa.
        // =============================================================================
        var reserva = new Mensagem
        {
            EmpresaId = p.EmpresaId,
            ConversaId = p.ConversaId.Value,
            ContatoId = p.ContatoId,
            ConexaoId = p.ConexaoId.Value,
            InstanceName = instancia,
            Direcao = DirecaoMensagem.Saida,
            Texto = Preencher(texto, p.ContatoNome, p.Empresa.Nome),
            DataDisparo = hoje
        };

        var resultado = await enviador.EnviarAgradecimentoNpsAsync(reserva, p.Telefone, ct);

        if (resultado != ResultadoEnvio.Enviada)
            log.LogInformation(
                "Pesquisa {Id}: agradecimento ficou {Resultado} e nao sera reenviado.",
                p.Id, resultado);
    }

    /// <summary>===================== O DETRATOR GERA TRABALHO HUMANO =====================
    ///
    /// Um lembrete para quem vendeu e um para o dono. ⚠️ UM SO QUANDO SAO A MESMA PESSOA — o caso
    /// comum na empresa pequena —, porque dois identicos na mesma lista nao avisam duas vezes:
    /// avisam que o sistema nao sabe quem e quem.
    ///
    /// ⚠️ `EnviaMensagem = false`, E ISSO IMPORTA DUAS VEZES. Primeiro porque e TAREFA, nao
    /// disparo: o lembrete pede que alguem LIGUE para o cliente. Segundo porque
    /// `uq_lembrete_teto_diario` cobre so `automatico AND envia_mensagem` — com `true`, um lembrete
    /// de follow-up do mesmo dia barraria este em silencio, e o aviso do detrator se perderia.
    ///
    /// ⚠️ PARA HOJE, e nao para amanha: cliente insatisfeito e o unico caso deste produto em que
    /// esperar um dia muda o resultado.
    /// ========================================================================</summary>
    private async Task AvisarDoDetratorAsync(
        long empresaId, long contatoId, long? conversaId, long? responsavelDaVenda, short nota,
        string contatoNome, string fusoHorario, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(
            FusoDeNegocio.AgoraNo(relogio, FusoDeNegocio.Resolver(fusoHorario)));

        var dono = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.EmpresaId == empresaId
                     && u.Papel == PapelUsuario.Dono
                     && u.Status == StatusUsuario.Ativo)
            .OrderBy(u => u.Id)
            .Select(u => (long?)u.Id)
            .FirstOrDefaultAsync(ct);

        var primeiroNome = NomeDePessoa.Primeiro(contatoNome) ?? contatoNome;

        // ⚠️ `Distinct` RESOLVE O CASO DO DONO-VENDEDOR sem um `if` que alguem possa esquecer de
        // atualizar quando aparecer um terceiro destinatario.
        var destinos = new[] { responsavelDaVenda, dono }
            .Where(x => x != null)
            .Distinct()
            .ToList();

        if (destinos.Count == 0)
        {
            // Sem dono ativo e sem responsavel, o lembrete nao apareceria em Meu Dia nenhum — e
            // lembrete que ninguem ve e pior que nenhum, porque da a impressao de que avisou.
            log.LogWarning(
                "Empresa {Id}: nota {Nota} de detrator sem dono ativo nem responsavel — ninguem "
              + "foi avisado.", empresaId, nota);
            return;
        }

        foreach (var destino in destinos)
        {
            db.Lembretes.Add(new Lembrete
            {
                EmpresaId = empresaId,
                ContatoId = contatoId,
                ConversaId = conversaId,
                Origem = OrigemLembrete.Automatico,
                Status = StatusLembrete.Pendente,
                DataAlvo = hoje,
                Titulo = $"Nota {nota} na pesquisa: falar com {primeiroNome}",
                Observacao =
                    $"{primeiroNome} deu nota {nota} de 10 na pesquisa pos-venda. "
                  + "Vale uma ligacao antes que ele conte para outra pessoa.",
                EnviaMensagem = false,
                ResponsavelId = destino,
                CriadoPor = null
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>As variaveis do agradecimento, as mesmas da pergunta. Ver `MotorNps.Preencher` para
    /// por que `{{saudacao}}` existe.</summary>
    private static string Preencher(string texto, string nomeDoContato, string nomeDaEmpresa) =>
        texto
            .Replace("{{saudacao}}", NomeDePessoa.Saudacao("Oi", nomeDoContato))
            .Replace("{{nome}}", NomeDePessoa.Primeiro(nomeDoContato) ?? "")
            .Replace("{{empresa}}", nomeDaEmpresa);
}
