using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A credencial de anúncio da empresa, na área logada (INT-4).
///
/// Query filter global vale — é caminho autenticado. O oposto do motor de envio, que roda como job.
/// O enforcement de PAPEL é do controller, como no resto do sistema.
///
/// ===================== UMA PLATAFORMA HOJE, DUAS AMANHÃ =====================
/// Todo método filtra por `PlataformaConversao.Meta` explicitamente, em vez de pegar "a credencial
/// da empresa". A tabela já tem chave `(empresa_id, plataforma)`, e o dia em que o Google entrar,
/// um `FirstOrDefault` sem plataforma passaria a devolver a credencial errada — em silêncio, e no
/// caminho que manda dado pessoal para fora.
/// ============================================================================</summary>
public class ServicoConversoes(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IClienteMeta cliente,
    TimeProvider relogio) : IServicoConversoes
{
    /// <summary>A janela do número que cobra quem não conectou. Trinta dias porque é o recorte que
    /// o resto do painel usa, e porque mais do que isso incluiria lead que a Meta já não aceita
    /// (a janela dela é de 7 dias).</summary>
    private const int DiasDoAviso = 30;

    /// <summary>Hoje só a Meta. A constante existe para que a próxima plataforma seja um parâmetro,
    /// e não uma caçada por `FirstOrDefault` sem filtro.</summary>
    private const PlataformaConversao Plataforma = PlataformaConversao.Meta;

    public async Task<PainelConversoes> ObterAsync(CancellationToken ct)
    {
        // ⚠️ A MÁSCARA É FEITA EM MEMÓRIA, depois do banco. `Mascarar` é método nosso: dentro do
        // `Select` o EF não sabe traduzi-lo e estoura em runtime — erro que só aparece quando
        // alguém abre a tela, nunca no build.
        var linha = await db.CredenciaisConversao.AsNoTracking()
            .Where(c => c.Plataforma == Plataforma)
            .Select(c => new
            {
                c.Id,
                c.Plataforma,
                c.Identificador,
                c.Token,
                c.CodigoTeste,
                c.Ativo,
                c.EmLead,
                c.EmCompra,
                c.ConsentimentoEm,
                QuemDeclarou = c.ConsentimentoUsuario == null ? null : c.ConsentimentoUsuario.Nome,
                c.DesativadaEm,
                c.DesativadaMotivo,
                c.CriadoEm
            })
            .FirstOrDefaultAsync(ct);

        var credencial = linha is null ? null : new CredencialDto(
            linha.Id,
            linha.Plataforma.ToString().ToLowerInvariant(),
            linha.Identificador,
            // SUFIXO, NUNCA O TOKEN. Quatro caracteres bastam para a pessoa reconhecer QUAL token
            // está lá, e não servem para chamar a Graph API com ele.
            linha.Token is null ? null : Mascarar(linha.Token),
            linha.CodigoTeste,
            linha.Ativo,
            linha.EmLead,
            linha.EmCompra,
            linha.ConsentimentoEm,
            linha.QuemDeclarou,
            linha.DesativadaEm,
            linha.DesativadaMotivo,
            linha.CriadoEm);

        return new PainelConversoes(
            credencial, await LeadsComAnuncioAsync(ct), await ConversoesAsync(ct));
    }

    public async Task<ResumoConversoes> ResumoAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        // ⚠️ `PodeEnviar`, e não `Ativo`. A pergunta do aviso é "está saindo?", e a resposta mora na
        // entidade — uma segunda versão dela aqui divergiria no dia em que o portão mudasse.
        var enviando = credencial?.PodeEnviar(TipoConversao.Lead) == true
                    || credencial?.PodeEnviar(TipoConversao.Compra) == true;

        return new ResumoConversoes(enviando, await LeadsComAnuncioAsync(ct));
    }

    /// <summary>Quantos leads dos últimos 30 dias chegaram com identificador de clique.
    ///
    /// ===================== POR QUE ESTE NÚMERO E NÃO "leads do site" =====================
    /// `identificadores <> '{}'` é a pergunta certa: são as pessoas que vieram de um ANÚNCIO pago e
    /// para quem existe o que mandar de volta. Contar todo lead do formulário inflaria o número com
    /// quem chegou pelo Google orgânico, e a frase "a Meta não ficou sabendo de 40" seria falsa —
    /// a Meta não tem nada a saber sobre 28 deles.
    /// ==================================================================================</summary>
    private Task<int> LeadsComAnuncioAsync(CancellationToken ct)
    {
        var desde = relogio.GetUtcNow().UtcDateTime.AddDays(-DiasDoAviso);

        return db.RastreiosLead.AsNoTracking()
            .Where(r => r.OcorridoEm >= desde && r.Identificadores != "{}")
            .CountAsync(ct);
    }

    public async Task SalvarAsync(SalvarCredencial dados, CancellationToken ct)
    {
        var identificador = (dados.Identificador ?? "").Trim();
        if (identificador.Length == 0)
            throw new RegraDeNegocioException("Informe o ID do pixel.");

        // Só dígitos: o Pixel ID da Meta é numérico, e o erro comum é colar a URL do Gerenciador
        // de Eventos inteira. Recusar aqui é um erro na tela; aceitar é um 400 da Graph API três
        // dias depois, quando a primeira venda fechar.
        if (!identificador.All(char.IsAsciiDigit))
            throw new RegraDeNegocioException(
                "O ID do pixel é só números — copie o campo \"ID do conjunto de dados\" no "
                + "Gerenciador de Eventos.");

        var credencial = await db.CredenciaisConversao
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial is null)
        {
            credencial = new CredencialConversao
            {
                EmpresaId = contexto.EmpresaId,
                Plataforma = Plataforma
            };
            db.CredenciaisConversao.Add(credencial);
        }

        credencial.Identificador = identificador;
        credencial.CodigoTeste = Vazio(dados.CodigoTeste);
        credencial.Ativo = dados.Ativo;
        credencial.EmLead = dados.EmLead;
        credencial.EmCompra = dados.EmCompra;

        // ⚠️ TOKEN EM BRANCO MANTÉM O ANTERIOR. A tela não tem como preenchê-lo de volta — o `GET`
        // nunca o devolve —, então um PUT com o campo vazio é sempre "não mexi nisso". Apagar aqui
        // faria de "trocar o Pixel ID" um jeito de desligar o envio por descuido.
        var token = Vazio(dados.Token);
        if (token is not null) credencial.Token = token;

        // ===================== O CONSENTIMENTO: DATA E AUTOR, NÃO UM BOOL =====================
        // Só escreve quando MUDA, senão cada salvamento de qualquer outro campo reescreveria a data
        // e o registro passaria a dizer que a declaração é de hoje.
        // ====================================================================================
        var agora = relogio.GetUtcNow().UtcDateTime;
        var tinha = credencial.ConsentimentoEm is not null;

        if (dados.ConsentimentoDeclarado && !tinha)
        {
            credencial.ConsentimentoEm = agora;
            credencial.ConsentimentoPor = contexto.UsuarioId;
        }
        else if (!dados.ConsentimentoDeclarado && tinha)
        {
            credencial.ConsentimentoEm = null;
            credencial.ConsentimentoPor = null;
        }

        // Religar a mão limpa a desativação do MOTOR. É o gesto de "troquei o token, tenta de
        // novo": sem isto, a credencial ficaria desativada para sempre depois do primeiro token
        // recusado, e a pessoa não teria como dizer ao sistema que resolveu.
        if (credencial.Ativo && token is not null)
        {
            credencial.DesativadaEm = null;
            credencial.DesativadaMotivo = null;
        }

        await db.SaveChangesAsync(ct);

        // Retirar o consentimento, ou desligar, esvazia a fila. Ver `CancelarPendentesAsync`.
        if (!dados.ConsentimentoDeclarado || !dados.Ativo)
            await CancelarPendentesAsync("o consentimento foi retirado", ct);
    }

    public async Task RemoverAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial is null) return;

        db.CredenciaisConversao.Remove(credencial);
        await db.SaveChangesAsync(ct);

        await CancelarPendentesAsync("a credencial foi removida", ct);
    }

    /// <summary>===================== FILA QUE NÃO PODE DRENAR NÃO FICA NA FILA =====================
    ///
    /// O `payload` de cada conversão pendente guarda SHA-256 de telefone e e-mail. Sem token, sem
    /// consentimento ou com o envio desligado, nenhuma delas vai sair nunca — e o que sobraria é
    /// dado pessoal hasheado parado numa tabela, esperando o dia em que alguém religue e ele saia
    /// sem que ninguém tenha decidido isso agora.
    ///
    /// `cancelado` e não `DELETE`: o evento fica registrado, o dado sai. A mesma regra da trilha de
    /// auditoria — a tela precisa poder dizer "isto não foi enviado, e por quê".
    ///
    /// `ExecuteUpdateAsync` num comando só: pode haver centenas de linhas, e carregá-las para
    /// mudar dois campos seria trabalho para nada.
    /// ====================================================================================</summary>
    private Task CancelarPendentesAsync(string motivo, CancellationToken ct) =>
        db.EventosConversao
            .Where(e => e.Plataforma == Plataforma && e.Status == StatusConversao.Pendente)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, StatusConversao.Cancelado)
                .SetProperty(e => e.ProximaTentativaEm, (DateTime?)null)
                .SetProperty(e => e.Erro, motivo), ct);

    /// <summary>As últimas conversões da empresa.
    ///
    /// Cinquenta, como o registro de webhooks: responde "está chegando?" e "o que falhou hoje?";
    /// mais que isso é trabalho para uma consulta, não para uma tela.</summary>
    private const int UltimasConversoes = 50;

    private Task<List<ConversaoDto>> ConversoesAsync(CancellationToken ct) =>
        db.EventosConversao.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(UltimasConversoes)
            .Select(e => new ConversaoDto(
                e.Id,
                e.Tipo.ToString().ToLowerInvariant(),
                e.Status.ToString().ToLowerInvariant(),
                e.Contato.Nome,
                e.Negociacao == null ? null : e.Negociacao.Valor,
                e.Tentativas,
                e.CodigoResposta,
                e.CodigoMeta,
                e.FbtraceId,
                e.Erro,
                e.OcorridoEm,
                e.ExpiraEm,
                e.EntregueEm,
                e.CriadoEm,
                e.Payload,
                // ⚠️ SÓ O QUE DESISTIU. `pendente` já vai ser tentado sozinho; `entregue` mandaria o
                // mesmo evento duas vezes; e `expirado` NUNCA vai funcionar — oferecer o botão ali
                // seria oferecer um gesto que só pode fracassar.
                e.Status == StatusConversao.Falhou))
            .ToListAsync(ct);

    public async Task<ResultadoTesteConversao> TestarAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial is null || string.IsNullOrWhiteSpace(credencial.Token))
            throw new RegraDeNegocioException("Configure o pixel e o token antes de testar.");

        // ===================== O TESTE NÃO PASSA PELO PORTÃO DE CONSENTIMENTO =====================
        // E não deveria: `PodeEnviar` protege o dado de uma PESSOA de sair sem base legal. Aqui não
        // há pessoa — o evento é sintético, com um telefone que não existe.
        //
        // Exigir o consentimento para testar faria a ordem de configuração ser "declare que tem
        // autorização, depois descubra se o token funciona", que é a ordem errada.
        // =======================================================================================
        var agora = relogio.GetUtcNow().UtcDateTime;

        var corpo = MontadorEventoMeta.Montar(new FatoDeConversao(
            TipoConversao.Lead,
            Guid.NewGuid(),
            agora,
            // ⚠️ DADO SINTÉTICO, e de propósito. Mandar um contato real faria o botão de teste
            // enviar uma conversão de verdade para o pixel do cliente — e o `Lead` daquela pessoa
            // sairia duas vezes no dia em que ela virasse lead mesmo.
            Email: "teste@nexora.app",
            Telefone: "5500000000000"));

        var resultado = await cliente.EnviarAsync(
            credencial.Identificador, credencial.Token, corpo, credencial.CodigoTeste, ct);

        // ⚠️ NADA É GRAVADO NA FILA. O evento de teste não é conversão de ninguém: uma linha dele no
        // registro ocuparia o único parcial de um contato que nem existe.
        //
        // Mas a desativação da credencial VALE, e é o ponto do botão: se a Meta recusou o token
        // agora, é isto que o dono precisa ver na tela — em vez de descobrir semanas depois que
        // nada saiu.
        var decisao = PoliticaConversao.Classificar(resultado.CodigoMeta);

        if (decisao.DesativarCredencial && credencial.DesativadaEm is null)
        {
            credencial.DesativadaEm = agora;
            credencial.DesativadaMotivo = decisao.Motivo;
            await db.SaveChangesAsync(ct);
        }
        else if (resultado.Aceitou && credencial.DesativadaEm is not null)
        {
            // Funcionou: o que estava desativado pelo motor volta. É o par do gesto acima — testar
            // depois de trocar o token é como se diz ao sistema que resolveu.
            credencial.DesativadaEm = null;
            credencial.DesativadaMotivo = null;
            await db.SaveChangesAsync(ct);
        }

        return new ResultadoTesteConversao(
            resultado.Aceitou, resultado.Codigo, resultado.FbtraceId, resultado.Erro);
    }

    public async Task ReenviarAsync(long id, CancellationToken ct)
    {
        var evento = await db.EventosConversao.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new RegraDeNegocioException("Conversão não encontrada.");

        if (evento.Status != StatusConversao.Falhou)
            throw new RegraDeNegocioException(
                "Só é possível reenviar uma conversão que falhou.", conflito: true);

        // ⚠️ A JANELA É CONFERIDA AQUI TAMBÉM. Entre a falha e o clique no botão podem passar dias,
        // e um reenvio fora dos 7 dias seria uma requisição que a Meta recusa inteira — com o
        // agravante de o dono ficar achando que resolveu.
        if (evento.ExpiraEm <= relogio.GetUtcNow().UtcDateTime)
            throw new RegraDeNegocioException(
                "Este evento passou dos 7 dias que a Meta aceita. Reenviar não vai funcionar.",
                conflito: true);

        // ⚠️ E A CREDENCIAL PRECISA PODER ENVIAR. Descoberto por um teste: depois de um `190`, o
        // motor desativa a credencial — e reenviar ali devolveria a linha para a fila só para ela
        // falhar de novo na rodada seguinte, com outra mensagem.
        //
        // Mesma regra do `expirado`: a tela não oferece gesto que só pode fracassar. A frase diz o
        // que fazer PRIMEIRO.
        var credencial = await db.CredenciaisConversao.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial?.PodeEnviar(evento.Tipo) != true)
            throw new RegraDeNegocioException(
                "O envio está desligado. Confira o token e o consentimento acima antes de reenviar.",
                conflito: true);

        evento.Status = StatusConversao.Pendente;
        evento.Tentativas = 0;
        evento.ProximaTentativaEm = relogio.GetUtcNow().UtcDateTime;
        evento.Erro = null;
        evento.CodigoResposta = null;
        evento.CodigoMeta = null;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Campo de texto vazio é ausência. Sem isto, salvar com o campo em branco gravaria
    /// string vazia — e `""` é um token, tecnicamente, que só falha na hora de enviar.</summary>
    private static string? Vazio(string? texto)
    {
        var limpo = (texto ?? "").Trim();
        return limpo.Length == 0 ? null : limpo;
    }

    /// <summary>`EAAG…4Zc`. Token curto demais vira só bolinhas: mostrar o começo de um token de 12
    /// caracteres é mostrar um terço dele.</summary>
    private static string Mascarar(string token) =>
        token.Length <= 12 ? "••••••••" : $"{token[..4]}…{token[^4..]}";
}
