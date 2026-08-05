using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O checklist de primeiros passos.
///
/// ===================== DERIVADO, NÃO GUARDADO =====================
/// Nenhum passo tem flag de "concluído". Cada um é uma pergunta ao estado real:
///
///   1. existe conexão com status `conectado`?
///   2. existe usuário além do dono?
///   3. existe mensagem de ENTRADA?
///
/// Com flag, o painel mentiria no caso que mais importa: a empresa configura tudo, o WhatsApp
/// cai duas semanas depois, e o checklist continua dizendo "tudo pronto" enquanto nada chega.
/// Derivado, o passo 1 volta a acender sozinho.
///
/// O QUE É GUARDADO são as duas DECISÕES do dono — "convido a equipe depois" e "fecha esse
/// painel". Nenhuma consulta consegue inferir uma escolha; ela tem que ser registrada. A
/// distinção é essa: estado do sistema se deriva, decisão de pessoa se guarda.
/// ================================================================</summary>
public class ServicoOnboarding(NexoraDbContext db, TimeProvider relogio) : IServicoOnboarding
{
    public async Task<Onboarding> ObterAsync(CancellationToken ct)
    {
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new
            {
                e.CriadoEm, e.PrimeiraMensagemEm, e.EquipeDispensadaEm, e.OnboardingDispensadoEm
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var conectado = await db.Conexoes.AsNoTracking()
            .AnyAsync(c => c.Status == StatusConexao.Conectado, ct);

        // "Além do dono": qualquer usuário que não seja dono, inclusive o convidado que ainda
        // não aceitou — o dono já fez a parte dele quando mandou o convite.
        var temEquipe = await db.Usuarios.AsNoTracking()
            .AnyAsync(u => u.Papel != PapelUsuario.Dono && u.Status != StatusUsuario.Inativo, ct);

        // ===================== A VERDADE É A MENSAGEM, NÃO A COLUNA =====================
        // `primeira_mensagem_em` nasceu na migration deste bloco e o webhook só carimba dali em
        // diante. Empresa que JÁ recebia mensagem antes disso tem a coluna NULL — e derivar o
        // passo dela deixaria o checklist aceso para sempre numa conta em plena operação.
        //
        // A coluna entra só como ATALHO: quando está preenchida, ela prova que a mensagem
        // existiu (quem a escreve é o mesmo caminho que insere a linha) e poupa uma consulta em
        // `mensagens`, a maior tabela do banco, em toda carga do painel. Quando está NULL, quem
        // responde é a tabela. O atalho pode ficar para trás; nunca pode mentir a favor.
        var recebeuMensagem = empresa.PrimeiraMensagemEm is not null
            || await db.Mensagens.AsNoTracking()
                .AnyAsync(m => m.Direcao == DirecaoMensagem.Entrada, ct);

        var equipeDispensada = empresa.EquipeDispensadaEm is not null;

        var passos = new List<PassoOnboarding>
        {
            new("conexao", "Conecte seu WhatsApp",
                "Leia o QR code com o celular da empresa. É por esse número que os clientes " +
                "vão falar com você.",
                conectado, false, "/conexao", "Conectar agora"),

            new("equipe", "Convide sua equipe",
                "Cada vendedor entra com o próprio acesso, e as conversas ficam divididas entre " +
                "eles sem ninguém pisar no pé do outro.",
                temEquipe, equipeDispensada, "/equipe", "Convidar alguém"),

            // SEM ROTA e SEM AÇÃO: este passo é ESPERA. Um botão aqui prometeria que existe
            // algo a clicar, e não existe — a mensagem tem que sair de um celular de verdade.
            new("primeira_mensagem", "Receba a primeira mensagem",
                "Pegue outro celular e mande uma mensagem para o número que você conectou. " +
                "Ela aparece na Caixa de Entrada em segundos.",
                recebeuMensagem, false, null, null)
        };

        var resolvidos = passos.Count(p => p.Concluido || p.Dispensado);
        var completo = resolvidos == passos.Count;
        var dispensado = empresa.OnboardingDispensadoEm is not null;

        int? minutos = empresa.PrimeiraMensagemEm is { } primeira
            ? (int)Math.Max(0, (primeira - empresa.CriadoEm).TotalMinutes)
            : null;

        return new Onboarding(
            passos, resolvidos, passos.Count, completo, dispensado,
            Mostrar: !completo && !dispensado,
            MinutosAteAPrimeiraMensagem: minutos);
    }

    // Os dois carimbos são IDEMPOTENTES: o `WHERE ... IS NULL` faz o segundo pedido virar
    // no-op, preservando a data da PRIMEIRA decisão. Sem ele, recarregar a tela e clicar de
    // novo reescreveria o instante e a métrica de onboarding perderia sentido.
    //
    // Duas queries quase iguais em vez de uma genérica com Expression: passar a coluna como
    // parâmetro exigiria compilar a expressão dentro do Where, e o EF não traduz isso — a
    // consulta cairia para avaliação no cliente, carregando a tabela inteira.

    public Task DispensarEquipeAsync(CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        // O query filter de `empresas` já restringe à empresa da requisição.
        return db.Empresas
            .Where(e => e.EquipeDispensadaEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.EquipeDispensadaEm, agora), ct);
    }

    public Task DispensarAsync(CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        return db.Empresas
            .Where(e => e.OnboardingDispensadoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OnboardingDispensadoEm, agora), ct);
    }
}
