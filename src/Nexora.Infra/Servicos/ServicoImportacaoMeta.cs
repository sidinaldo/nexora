using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Csv;
using Nexora.Core.Entidades;
using Nexora.Core.LeadAds;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Core.Seguranca;

namespace Nexora.Infra.Servicos;

/// <summary>A importação do CSV do Meta Lead Ads (INT-XX). Ver `IServicoImportacaoMeta` para os
/// passos, e por que a prévia e a gravação dividem o mesmo julgamento.</summary>
public class ServicoImportacaoMeta(NexoraDbContext db, IContextoEmpresa contexto) : IServicoImportacaoMeta
{
    /// <summary>Quantas a prévia mostra. O spec: "as 10 primeiras".</summary>
    private const int NaPrevia = 10;

    // ==================================================================== 1. receber
    public async Task<ImportacaoRecebida> ReceberAsync(
        string nomeArquivo, byte[] arquivo, CancellationToken ct)
    {
        contexto.Exigir(Permissao.ImportarContatos, "Só o dono ou um gestor pode importar leads.");

        if (arquivo.Length == 0)
            throw new RegraDeNegocioException("O arquivo está vazio.");

        if (arquivo.Length > IServicoImportacaoMeta.MaximoBytes)
            throw new RegraDeNegocioException(
                $"Arquivo grande demais (máximo {IServicoImportacaoMeta.MaximoBytes / (1024 * 1024)} MB). "
                + "Divida o export em partes.");

        var tabela = LeitorCsv.Ler(arquivo)
            ?? throw new RegraDeNegocioException(
                "Não encontrei nenhuma linha no arquivo. Ele precisa de um cabeçalho e ao menos um lead.");

        // ⚠️ SÓ CABEÇALHO NÃO É ERRO AQUI — é importação vazia. O spec lista "arquivo vazio ou só
        // com cabeçalho" entre os que têm de ser tratados, e recusar o segundo esconderia do dono
        // que o export saiu sem linha nenhuma (filtro de data errado no Gerenciador, o caso comum).
        if (tabela.Quantidade > IServicoImportacaoMeta.MaximoLinhas)
            throw new RegraDeNegocioException(
                $"São {tabela.Quantidade} linhas, e o limite é {IServicoImportacaoMeta.MaximoLinhas} "
                + "por arquivo. Divida o export em partes.");

        var sugestao = MapeamentoMeta.Sugerir(tabela.Cabecalho)
            .Select(x => new ColunaMapeada(x.Coluna, x.Campo))
            .ToList();

        var importacao = new Importacao
        {
            EmpresaId = contexto.EmpresaId,
            UsuarioId = contexto.UsuarioId,
            NomeArquivo = Cortar(string.IsNullOrWhiteSpace(nomeArquivo) ? "sem-nome.csv" : nomeArquivo, 260),
            TotalLinhas = tabela.Quantidade,
            Status = StatusImportacao.AguardandoMapeamento,
            // O mapeamento sugerido fica guardado: é ele que registra QUAIS colunas o arquivo tem e
            // em que ordem, para a prévia recusar um mapeamento que cite coluna que não existe.
            Mapeamento = JsonSerializer.Serialize(sugestao, Json)
        };

        // ⚠️ AS LINHAS SÃO GUARDADAS AGORA, CRUAS, com todas as colunas — inclusive as que ninguém
        // vai mapear. É o que permite a prévia e o processamento lerem do banco sem pedir o arquivo
        // de novo, e o reprocessamento com outro mapeamento. `Resultado` fica NULO: ainda não
        // aconteceu nada com elas.
        for (var i = 0; i < tabela.Quantidade; i++)
            importacao.Linhas.Add(new ImportacaoLinha
            {
                NumeroLinha = tabela.NumeroLinha(i),
                DadosBrutos = JsonSerializer.Serialize(tabela.Registro(i), Json)
            });

        db.Importacoes.Add(importacao);
        await db.SaveChangesAsync(ct);

        return new ImportacaoRecebida(importacao.Id, importacao.NomeArquivo, importacao.TotalLinhas, sugestao);
    }

    // ==================================================================== 2. prever
    public async Task<PreviaImportacao> PreverAsync(
        long importacaoId, IReadOnlyList<ColunaMapeada> mapeamento, CancellationToken ct)
    {
        contexto.Exigir(Permissao.ImportarContatos, "Só o dono ou um gestor pode importar leads.");

        var linhas = await JulgarAsync(importacaoId, mapeamento, ct);

        return new PreviaImportacao(
            linhas.Count,
            linhas.Count(l => l.Resultado == ResultadoLinha.Importado),
            linhas.Count(l => l.Resultado == ResultadoLinha.Duplicado),
            linhas.Count(l => l.Resultado == ResultadoLinha.Invalido),
            [.. linhas.Take(NaPrevia).Select(l => new LinhaPrevia(
                l.Linha, l.Nome, l.Telefone, l.Email, l.MetaLeadId, l.CriadoEm, l.Resultado, l.Motivo))]);
    }

    // ==================================================================== o julgamento
    /// <summary>Uma linha lida e decidida. `internal` porque o commit da gravação a usa igual.</summary>
    internal sealed record LinhaJulgada(
        long LinhaId, int Linha, string Nome, string? Telefone, string? Email, string? Observacoes,
        string? OrigemDetalhe, string? MetaLeadId, string? MetaAdId, string? MetaCampaignId,
        string? MetaFormId, DateTime? CriadoEm, ResultadoLinha Resultado, string? Motivo,
        long? ContatoExistenteId);

    /// <summary>===================== AS REGRAS, NESTA ORDEM =====================
    ///   1. telefone ilegível ............ inválido (`telefone_invalido`)
    ///   2. `meta_lead_id` já na empresa . duplicado — o mesmo lead, importado antes
    ///   3. telefone já na empresa ....... duplicado — e o commit da gravação enriquece os `meta_*`
    ///   4. repetido no próprio arquivo .. duplicado
    ///   5. o resto ...................... importado
    ///
    /// ⚠️ A ORDEM 2 → 3 É A REGRA, e o spec diz por quê: o mesmo lead exportado duas vezes tem de
    /// ser reconhecido pelo id da Meta, que é exato; o telefone é o fallback para o lead que já tinha
    /// entrado pelo WhatsApp com outro nome.
    ///
    /// ⚠️ A 4 NÃO ESTÁ NO SPEC, e é a que impede o lote inteiro de cair. A checagem contra o banco
    /// não pega duplicata DENTRO do arquivo — nenhuma das duas existe ainda —, as duas passariam, e o
    /// índice único derrubaria o `SaveChanges` com as outras 9.998 junto. A issue #8 já aprendeu isso.
    /// =============================================================================</summary>
    internal async Task<List<LinhaJulgada>> JulgarAsync(
        long importacaoId, IReadOnlyList<ColunaMapeada> mapeamento, CancellationToken ct)
    {
        // O filtro de tenant da importação faz o isolamento: id de outra empresa simplesmente não
        // existe aqui, e a resposta é a mesma de um id inventado.
        var importacao = await db.Importacoes.AsNoTracking()
            .Where(i => i.Id == importacaoId)
            .Select(i => new { i.Mapeamento, i.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Importação não encontrada.") { StatusHttp = 404 };

        if (importacao.Status == StatusImportacao.Processando)
            throw new RegraDeNegocioException(
                "Esta importação está sendo processada. Espere terminar.", conflito: true);

        var colunasDoArquivo = JsonSerializer
            .Deserialize<List<ColunaMapeada>>(importacao.Mapeamento ?? "[]", Json)!
            .Select(c => c.Coluna)
            .ToHashSet();

        var regras = Validar(mapeamento, colunasDoArquivo);

        var brutas = await db.ImportacaoLinhas.AsNoTracking()
            .Where(l => l.ImportacaoId == importacaoId)
            .OrderBy(l => l.NumeroLinha)
            .Select(l => new { l.Id, l.NumeroLinha, l.DadosBrutos })
            .ToListAsync(ct);

        // ---------- transformar
        var transformadas = brutas.Select(b =>
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(b.DadosBrutos, Json)!;
            string? Um(CampoImportacao campo) =>
                regras.TryGetValue(campo, out var cols) && cols.Count > 0
                    ? Vazio(d.GetValueOrDefault(cols[0]))
                    : null;

            var bruto = Um(CampoImportacao.Telefone);
            var telefone = bruto is null ? null : CanonicalizadorTelefone.Canonicalizar(bruto);
            var telefoneValido = telefone is not null && CanonicalizadorTelefone.EhValido(telefone);

            // ⚠️ SEM NOME NÃO RECUSA: `contatos.nome` é NOT NULL, e a resposta que o produto já dá
            // para "não sei quem é" é o telefone formatado — é o que o webhook do WhatsApp faz
            // quando não vem pushName. Recusar o lead por falta de nome perderia um contato que
            // tem o que importa, que é o telefone.
            var nome = Um(CampoImportacao.Nome)
                ?? (telefoneValido ? CanonicalizadorTelefone.Formatar(telefone!) : "");

            return new
            {
                b.Id, Linha = b.NumeroLinha, Nome = Cortar(nome, 120),
                Telefone = telefoneValido ? telefone : null,
                TelefoneBruto = bruto,
                Email = Um(CampoImportacao.Email) is { } e ? Cortar(e, 160) : null,
                Observacoes = Observacoes(d, regras),
                OrigemDetalhe = Um(CampoImportacao.OrigemDetalhe),
                MetaLeadId = MapeamentoMeta.IdLimpo(Um(CampoImportacao.MetaLeadId)),
                MetaAdId = MapeamentoMeta.IdLimpo(Um(CampoImportacao.MetaAdId)),
                MetaCampaignId = MapeamentoMeta.IdLimpo(Um(CampoImportacao.MetaCampaignId)),
                MetaFormId = MapeamentoMeta.IdLimpo(Um(CampoImportacao.MetaFormId)),
                CriadoEm = MapeamentoMeta.Data(Um(CampoImportacao.CriadoEm))
            };
        }).ToList();

        // ---------- o que já existe na empresa: UMA consulta por chave, e não uma por linha
        var leadIds = transformadas.Where(t => t.MetaLeadId is not null)
            .Select(t => t.MetaLeadId!).Distinct().ToList();
        var telefones = transformadas.Where(t => t.Telefone is not null)
            .Select(t => t.Telefone!).Distinct().ToList();

        var porLeadId = leadIds.Count == 0 ? [] : await db.Contatos.AsNoTracking()
            .Where(c => c.MetaLeadId != null && leadIds.Contains(c.MetaLeadId))
            .Select(c => new { c.Id, c.MetaLeadId })
            .ToDictionaryAsync(c => c.MetaLeadId!, c => c.Id, ct);

        // `AnonimizadoEm == null` pelo mesmo motivo de `ServicoContatos.CriarAsync`: o índice único
        // de telefone é PARCIAL, e o anonimizado não disputa o número com ninguém.
        var porTelefone = telefones.Count == 0 ? [] : await db.Contatos.AsNoTracking()
            .Where(c => telefones.Contains(c.Telefone) && c.AnonimizadoEm == null)
            .Select(c => new { c.Id, c.Telefone })
            .ToDictionaryAsync(c => c.Telefone, c => c.Id, ct);

        // ---------- decidir
        var leadsVistos = new Dictionary<string, int>();
        var telefonesVistos = new Dictionary<string, int>();
        var julgadas = new List<LinhaJulgada>(transformadas.Count);

        foreach (var t in transformadas)
        {
            ResultadoLinha resultado;
            string? motivo = null;
            long? existente = null;

            if (t.Telefone is null)
            {
                resultado = ResultadoLinha.Invalido;
                motivo = t.TelefoneBruto is null ? "telefone_ausente" : "telefone_invalido";
            }
            else if (t.MetaLeadId is not null && porLeadId.TryGetValue(t.MetaLeadId, out var porId))
            {
                resultado = ResultadoLinha.Duplicado;
                motivo = "lead_ja_importado";
                existente = porId;
            }
            else if (porTelefone.TryGetValue(t.Telefone, out var porFone))
            {
                resultado = ResultadoLinha.Duplicado;
                motivo = "telefone_ja_cadastrado";
                existente = porFone;
            }
            else if (t.MetaLeadId is not null && leadsVistos.TryGetValue(t.MetaLeadId, out var linhaLead))
            {
                resultado = ResultadoLinha.Duplicado;
                motivo = $"repetido_no_arquivo:{linhaLead}";
            }
            else if (telefonesVistos.TryGetValue(t.Telefone, out var linhaFone))
            {
                resultado = ResultadoLinha.Duplicado;
                motivo = $"repetido_no_arquivo:{linhaFone}";
            }
            else
            {
                resultado = ResultadoLinha.Importado;
            }

            // Só quem VAI entrar reserva a chave: um inválido ou um duplicado da base não impede
            // a próxima linha com o mesmo telefone de ser a que entra.
            if (resultado == ResultadoLinha.Importado)
            {
                telefonesVistos[t.Telefone!] = t.Linha;
                if (t.MetaLeadId is not null) leadsVistos[t.MetaLeadId] = t.Linha;
            }

            julgadas.Add(new LinhaJulgada(
                t.Id, t.Linha, t.Nome, t.Telefone, t.Email, t.Observacoes, t.OrigemDetalhe,
                t.MetaLeadId, t.MetaAdId, t.MetaCampaignId, t.MetaFormId, t.CriadoEm,
                resultado, motivo, existente));
        }

        return julgadas;
    }

    // ==================================================================== o mapeamento
    /// <summary>Confere o mapeamento e o devolve agrupado por campo.
    ///
    /// ⚠️ TELEFONE É OBRIGATÓRIO, e é a única exigência do spec: sem ele não há como deduplicar nem
    /// como falar com a pessoa. Os outros campos únicos podem faltar, mas não podem aparecer DUAS
    /// vezes — duas colunas de nome obrigariam o sistema a escolher uma em silêncio.</summary>
    private static Dictionary<CampoImportacao, List<string>> Validar(
        IReadOnlyList<ColunaMapeada> mapeamento, HashSet<string> colunasDoArquivo)
    {
        var desconhecidas = mapeamento.Where(m => !colunasDoArquivo.Contains(m.Coluna))
            .Select(m => m.Coluna).ToList();
        if (desconhecidas.Count > 0)
            throw new RegraDeNegocioException(
                $"O arquivo não tem a coluna {string.Join(", ", desconhecidas.Select(c => $"\"{c}\""))}.");

        var regras = mapeamento
            .Where(m => m.Campo != CampoImportacao.Ignorar)
            .GroupBy(m => m.Campo)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Coluna).ToList());

        if (!regras.ContainsKey(CampoImportacao.Telefone))
            throw new RegraDeNegocioException(
                "Escolha qual coluna é o telefone. Sem ele não há como saber quem já está na base.");

        var repetidos = regras
            .Where(r => r.Key != CampoImportacao.Observacoes && r.Value.Count > 1)
            .Select(r => r.Key).ToList();
        if (repetidos.Count > 0)
            throw new RegraDeNegocioException(
                $"Cada campo só pode vir de uma coluna, e {string.Join(", ", repetidos)} vem de mais de uma.");

        return regras;
    }

    /// <summary>As colunas ligadas a `Observacoes`, em "Pergunta: resposta", uma por linha — é como
    /// as perguntas que o cliente criou no formulário chegam ao vendedor. Resposta vazia não entra:
    /// "Tem urgência: " sem nada depois só ocupa espaço.</summary>
    private static string? Observacoes(
        Dictionary<string, string> dados, Dictionary<CampoImportacao, List<string>> regras)
    {
        if (!regras.TryGetValue(CampoImportacao.Observacoes, out var colunas)) return null;

        var sb = new StringBuilder();
        foreach (var coluna in colunas)
            if (Vazio(dados.GetValueOrDefault(coluna)) is { } valor)
                sb.Append(sb.Length > 0 ? "\n" : "").Append(coluna).Append(": ").Append(valor);

        return sb.Length == 0 ? null : sb.ToString();
    }

    // ==================================================================== miúdos
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string Cortar(string v, int max) => v.Length <= max ? v : v[..max];
}
