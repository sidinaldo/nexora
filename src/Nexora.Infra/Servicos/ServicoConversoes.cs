using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
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

        return new PainelConversoes(credencial, await LeadsComAnuncioAsync(ct));
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
