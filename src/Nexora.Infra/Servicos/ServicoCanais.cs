using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Captacao;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Os canais de captação por QR Code e link, na área logada.
///
/// Aqui o query filter global VALE — é caminho autenticado, e `db.CanaisCaptacao` já sai
/// recortado pela empresa da requisição. O oposto do `ProcessadorEventoEvolution`, que roda sem
/// sessão e resolve o tenant pelo `instance_name`.
///
/// O enforcement de PAPEL (só dono) é do controller, como no resto do sistema.</summary>
public class ServicoCanais(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IGeradorQrCode qr) : IServicoCanais
{
    /// <summary>Teto por empresa. Não é limite de produto: é freio contra script. Trinta canais é
    /// mais do que qualquer PME consegue manter material impresso, e sem teto uma chamada em laço
    /// encheria a tabela.</summary>
    private const int MaximoPorEmpresa = 30;

    private const int TamanhoMinimoNome = 2;
    private const int TamanhoMaximoNome = 80;

    /// <summary>Tentativas de sorteio antes de desistir. 28^4 combinações contra dezenas de
    /// canais: a chance de dez colisões seguidas é astronômica, e o laço existe só para o
    /// `Criar` não depender de sorte.</summary>
    private const int TentativasDeCodigo = 10;

    // ==================================================================== listar
    public async Task<CanaisDto> ListarAsync(CancellationToken ct)
    {
        var conexoes = await ConexoesPareadasAsync(ct);

        var linhas = await db.CanaisCaptacao.AsNoTracking()
            .OrderByDescending(c => c.Ativo).ThenBy(c => c.Nome)
            .Select(c => new
            {
                c.Id, c.Nome, c.Codigo, c.ConexaoId,
                ConexaoNome = c.Conexao.Nome,
                c.Conexao.Numero,
                c.Origem, c.Ativo, c.LeadsRecebidos, c.CriadoEm
            })
            .ToListAsync(ct);

        var itens = linhas.Select(c =>
        {
            var motivo = MotivoParaNaoRemover(c.LeadsRecebidos);
            var codigo = c.Codigo;
            return new CanalDto(
                c.Id, c.Nome, codigo, c.ConexaoId, c.ConexaoNome, c.Numero,
                c.Origem.ToString().ToLowerInvariant(), c.Ativo, c.LeadsRecebidos,
                Link(c.Numero, codigo), CodigoCanal.TextoDoLink(codigo),
                NomeDeArquivo(c.Nome, codigo),
                motivo is null, motivo, c.CriadoEm);
        }).ToList();

        return new CanaisDto(
            itens, conexoes,
            PodeCriar: conexoes.Count > 0 && itens.Count < MaximoPorEmpresa,
            LeadsAtribuidos: itens.Sum(c => c.LeadsRecebidos));
    }

    /// <summary>`https://wa.me/{numero}?text={texto}`.
    ///
    /// ===================== POR QUE `wa.me` E NÃO UM REDIRECIONADOR NOSSO =====================
    /// Um `nexora.app/q/{codigo}` que respondesse 302 daria a coisa que falta aqui: contar os
    /// SCANS, e portanto medir quantos perderam o código pelo caminho. Não foi feito por duas
    /// razões, e as duas são de hoje, não de princípio:
    ///
    ///   • não existe domínio público do Nexora — o material impresso apontaria para um host que
    ///     ainda não está de pé;
    ///   • um salto a mais entre o celular e o WhatsApp é um ponto a mais de falha, e ele fica
    ///     impresso em papel que não se corrige.
    ///
    /// O preço está registrado: sem redirecionador não há denominador, e o contador de leads é
    /// PISO. Ver `CanalCaptacao`.
    /// ========================================================================================
    ///
    /// O texto vai `Uri.EscapeDataString`: o `#` do código é fragmento de URL e, sem escapar,
    /// tudo dali para a frente some antes de chegar ao WhatsApp — o link "funcionaria" e o código
    /// nunca chegaria. É a falha silenciosa deste arquivo.</summary>
    private static string? Link(string? numero, string codigo) =>
        string.IsNullOrEmpty(numero)
            ? null
            : $"https://wa.me/{numero}?text={Uri.EscapeDataString(CodigoCanal.TextoDoLink(codigo))}";

    /// <summary>As conexões que podem receber um canal: só as que têm número pareado.
    ///
    /// Conexão sem número geraria `https://wa.me/?text=...` — um link que abre o WhatsApp sem
    /// destinatário. Impresso em panfleto, é dinheiro jogado fora.</summary>
    private async Task<IReadOnlyList<ConexaoParaCanal>> ConexoesPareadasAsync(CancellationToken ct) =>
        await db.Conexoes.AsNoTracking()
            .Where(c => c.Numero != null)
            .OrderBy(c => c.Id)
            .Select(c => new ConexaoParaCanal(c.Id, c.Nome, c.Numero!))
            .ToListAsync(ct);

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovoCanal novo, CancellationToken ct)
    {
        var nome = ValidarNome(novo.Nome);
        var origem = ParseOrigem(novo.Origem);

        var conexoes = await ConexoesPareadasAsync(ct);
        if (conexoes.Count == 0)
            throw new RegraDeNegocioException(
                "Nenhum número de WhatsApp está conectado. Conecte um número antes de criar canais "
              + "— o link do QR Code precisa dele, e sem ele o material impresso sai quebrado.",
                conflito: true);

        var conexao = conexoes.FirstOrDefault(c => c.Id == novo.ConexaoId)
            ?? throw new RegraDeNegocioException(
                "Escolha um número conectado para este canal.");

        if (await db.CanaisCaptacao.CountAsync(ct) >= MaximoPorEmpresa)
            throw new RegraDeNegocioException(
                $"Limite de {MaximoPorEmpresa} canais atingido. Desative ou reaproveite um existente.");

        if (await db.CanaisCaptacao.AnyAsync(c => c.Nome == nome, ct))
            throw new RegraDeNegocioException("Já existe um canal com este nome.", conflito: true);

        var canal = new CanalCaptacao
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Codigo = await SortearCodigoAsync(ct),
            ConexaoId = conexao.Id,
            Origem = origem,
            Ativo = true
        };

        db.CanaisCaptacao.Add(canal);
        await db.SaveChangesAsync(ct);
        return canal.Id;
    }

    /// <summary>Sorteia um código livre DENTRO da empresa. O `uq_canais_empresa_codigo` é a rede
    /// de verdade; esta checagem existe para a colisão virar um novo sorteio em vez de um 500.</summary>
    private async Task<string> SortearCodigoAsync(CancellationToken ct)
    {
        for (var i = 0; i < TentativasDeCodigo; i++)
        {
            var candidato = CodigoCanal.Gerar();
            if (!await db.CanaisCaptacao.AnyAsync(c => c.Codigo == candidato, ct))
                return candidato;
        }

        throw new RegraDeNegocioException(
            "Não foi possível gerar um código para este canal. Tente de novo.");
    }

    // ==================================================================== editar
    public async Task AtualizarAsync(long id, NovoCanal dados, CancellationToken ct)
    {
        var canal = await MeuCanalAsync(id, ct);
        var nome = ValidarNome(dados.Nome);

        if (await db.CanaisCaptacao.AnyAsync(c => c.Nome == nome && c.Id != id, ct))
            throw new RegraDeNegocioException("Já existe um canal com este nome.", conflito: true);

        if (dados.ConexaoId != canal.ConexaoId)
        {
            var conexoes = await ConexoesPareadasAsync(ct);
            if (conexoes.All(c => c.Id != dados.ConexaoId))
                throw new RegraDeNegocioException("Escolha um número conectado para este canal.");

            // Trocar o número TROCA O LINK, e o QR já impresso continua apontando para o antigo.
            // Não é proibido — a empresa pode ter desativado o número velho —, mas quem faz isso
            // precisa saber, e quem avisa é a tela.
            canal.ConexaoId = dados.ConexaoId;
        }

        // O CÓDIGO não entra aqui, em nenhuma circunstância: ele está impresso em papel que não
        // volta. Trocar o código transformaria todo material distribuído em link sem atribuição —
        // funcionando, mas mudo.
        canal.Nome = nome;
        canal.Origem = ParseOrigem(dados.Origem);
        await db.SaveChangesAsync(ct);
    }

    public async Task AlternarAtivoAsync(long id, bool ativo, CancellationToken ct)
    {
        var canal = await MeuCanalAsync(id, ct);
        canal.Ativo = ativo;
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== remover
    public async Task RemoverAsync(long id, CancellationToken ct)
    {
        var canal = await MeuCanalAsync(id, ct);

        if (MotivoParaNaoRemover(canal.LeadsRecebidos) is { } motivo)
            throw new RegraDeNegocioException(motivo, conflito: true);

        db.CanaisCaptacao.Remove(canal);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Por que este canal NÃO pode ser removido, ou null se pode.
    ///
    /// Um lugar só porque a lista e a remoção precisam responder a MESMA coisa: a tela desabilita
    /// o botão com este texto, e o serviço recusa com ele. Duas cópias divergiriam, e o sintoma
    /// seria um botão habilitado que devolve erro.
    ///
    /// Não há FK segurando: `contatos.origem_detalhe` é TEXTO, cópia do nome no dia do lead. É
    /// justamente por isso que a regra é da aplicação — apagar a linha não quebraria nada no
    /// banco, e deixaria o relatório de origem apontando para um canal que ninguém mais consegue
    /// explicar.</summary>
    private static string? MotivoParaNaoRemover(int leads) =>
        leads > 0
            ? $"Este canal já trouxe {leads} {(leads == 1 ? "lead" : "leads")}. "
            + "Apagar deixaria o histórico deles apontando para um canal que não existe mais — "
            + "desative em vez de apagar."
            : null;

    // ==================================================================== QR
    public async Task<QrDoCanal?> SvgAsync(long id, CancellationToken ct)
    {
        var canal = await ParaQrAsync(id, ct);
        return canal is null ? null : new QrDoCanal($"{canal.Value.Arquivo}.svg", qr.Svg(canal.Value.Link));
    }

    public async Task<(string NomeArquivo, byte[] Png)?> PngAsync(long id, CancellationToken ct)
    {
        var canal = await ParaQrAsync(id, ct);
        return canal is null ? null : ($"{canal.Value.Arquivo}.png", qr.Png(canal.Value.Link));
    }

    /// <summary>O link e o nome de arquivo do canal, ou null se ele não existe / não é desta
    /// empresa / não tem número pareado.
    ///
    /// Sem número não há QR: desenhar um `wa.me/` sem telefone produziria um código que escaneia
    /// e não faz nada — pior que não gerar, porque o cliente só descobre depois de imprimir.</summary>
    private async Task<(string Link, string Arquivo)?> ParaQrAsync(long id, CancellationToken ct)
    {
        var canal = await db.CanaisCaptacao.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Nome, c.Codigo, c.Conexao.Numero })
            .FirstOrDefaultAsync(ct);

        if (canal is null) return null;

        var link = Link(canal.Numero, canal.Codigo);
        if (link is null)
            throw new RegraDeNegocioException(
                "O número deste canal não está pareado. Reconecte o WhatsApp antes de gerar o QR Code.",
                conflito: true);

        return (link, NomeDeArquivo(canal.Nome, canal.Codigo));
    }

    /// <summary>`nexora-panfleto-julho-k7m2`. O código entra no nome do arquivo de propósito: seis
    /// meses depois, com quatro SVGs na pasta de downloads, é o que diz qual é qual.</summary>
    private static string NomeDeArquivo(string nome, string codigo)
    {
        var limpo = new string([.. nome.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')]);

        while (limpo.Contains("--")) limpo = limpo.Replace("--", "-");
        limpo = limpo.Trim('-');

        if (limpo.Length > 40) limpo = limpo[..40].Trim('-');
        if (limpo.Length == 0) limpo = "canal";

        return $"nexora-{limpo}-{codigo}";
    }

    // ==================================================================== apoio

    /// <summary>O query filter já recorta por empresa; o nulo vira "não encontrado" — que é a
    /// resposta certa tanto para id inexistente quanto para id de outro tenant.</summary>
    private async Task<CanalCaptacao> MeuCanalAsync(long id, CancellationToken ct) =>
        await db.CanaisCaptacao.FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new RegraDeNegocioException("Canal não encontrado.");

    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException(
                $"Dê um nome ao canal (mínimo {TamanhoMinimoNome} caracteres).");
        return limpo.Length <= TamanhoMaximoNome ? limpo : limpo[..TamanhoMaximoNome];
    }

    /// <summary>Origem vinda da tela. Cai em `qrcode` quando não reconhece — este bloco nasceu do
    /// QR Code, e um valor inválido virar `manual` faria o lead capturado por link parecer
    /// digitado por alguém.</summary>
    private static OrigemLead ParseOrigem(string? origem) =>
        Enum.TryParse<OrigemLead>(origem, ignoreCase: true, out var o) ? o : OrigemLead.Qrcode;
}
