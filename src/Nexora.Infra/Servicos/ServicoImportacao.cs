using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Csv;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Core.Auditoria;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A importação de lead. Ver <see cref="IServicoImportacao"/> para as três decisões que
/// governam o bloco — não abrir negociação, pular repetido, e conferir antes de gravar.</summary>
public class ServicoImportacao(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    ColetorAuditoria trilha) : IServicoImportacao
{
    public async Task<ResumoImportacao> PreverAsync(byte[] arquivo, CancellationToken ct) =>
        Resumir(await JulgarAsync(arquivo, ct));

    public async Task<ResumoImportacao> ImportarAsync(
        byte[] arquivo, long? pipelineId, CancellationToken ct)
    {
        ExigirDonoOuGestor();

        // ⚠️ O MESMO JULGAMENTO DA PRÉVIA, e não uma segunda leitura com regras próprias. Se os
        // dois divergirem, o dono confere uma coisa na tela e o banco recebe outra — que é a forma
        // mais cara de errar aqui, porque ele APROVOU o que viu.
        var linhas = await JulgarAsync(arquivo, ct);
        var novas = linhas.Where(l => l.Situacao == SituacaoLinha.Nova).ToList();

        if (novas.Count == 0) return Resumir(linhas);

        // O funil é resolvido UMA vez, fora do laço: são 2.000 linhas, e perguntar a etapa de
        // entrada a cada uma seria 2.000 consultas para uma resposta que não muda.
        long? etapaId = null;
        var ordem = 0m;

        if (pipelineId is { } funil)
        {
            etapaId = await PrimeiraEtapaAsync(funil, ct);
            ordem = await ProximaOrdemAsync(etapaId.Value, ct);
        }

        var criados = new List<Contato>(novas.Count);

        foreach (var linha in novas)
        {
            var contato = new Contato
            {
                EmpresaId = contexto.EmpresaId,
                Nome = linha.Nome,
                Telefone = linha.Telefone,
                Email = linha.Email,
                Origem = ParseOrigem(linha.Origem),
                Observacoes = linha.Observacoes
            };

            db.Contatos.Add(contato);
            criados.Add(contato);

            // ⚠️ SÓ COM FUNIL ESCOLHIDO. É a decisão 1 do bloco: importar 800 clientes NÃO enche o
            // quadro de 800 cards. Ver `IServicoImportacao`.
            // ⚠️ O FUNIL VAI JUNTO, e é o que evita uma consulta por linha: sem ele, `NovaAsync`
            // perguntaria ao banco "de que funil é esta etapa?" 2.000 vezes, para a mesma resposta.
            if (etapaId is { } etapa)
                db.Negociacoes.Add(await AberturaDeNegociacao.NovaAsync(
                    db, contato, etapa, ordem++, null, null, ct, pipelineId));
        }

        // ⚠️ UM `SaveChanges` SÓ, e é o que torna a operação tudo-ou-nada. Gravar de 100 em 100
        // deixaria a base com metade da planilha dentro depois de uma falha, e ninguém sabendo
        // qual metade — para reimportar o arquivo e cair na regra do repetido.
        await db.SaveChangesAsync(ct);

        // ⚠️ A TRILHA DEPOIS, PELO MESMO MOTIVO DE `ServicoContatos.CriarAsync`: antes do INSERT o
        // id é 0, e declarar com zero produz evento órfão, que nunca aparece na linha do tempo de
        // ninguém. Um `Criou` por contato — a linha do tempo é DE CADA UM, então 800 contatos não
        // enchem a de nenhum.
        foreach (var criado in criados)
            trilha.Declarar(EntidadeAuditada.Contato, criado.Id, AcaoAuditoria.Criou);

        await db.SaveChangesAsync(ct);

        return Resumir(linhas);
    }

    // ==================================================================== o julgamento
    /// <summary>Lê o arquivo e decide o destino de cada linha, SEM gravar nada. É o coração dos
    /// dois passos.</summary>
    private async Task<List<LinhaImportada>> JulgarAsync(byte[] arquivo, CancellationToken ct)
    {
        if (arquivo.Length == 0)
            throw new RegraDeNegocioException("O arquivo está vazio.");

        if (arquivo.Length > IServicoImportacao.MaximoBytes)
            throw new RegraDeNegocioException(
                $"Arquivo grande demais (máximo {IServicoImportacao.MaximoBytes / 1024} KB). "
                + "Divida a planilha em partes.");

        var tabela = LeitorCsv.Ler(arquivo)
            ?? throw new RegraDeNegocioException(
                "Não encontrei nenhuma linha no arquivo. Ele precisa de um cabeçalho e ao menos "
                + "um contato.");

        // ⚠️ A CHECAGEM DO CABEÇALHO VEM ANTES DE QUALQUER LINHA, e com o nome das colunas que
        // faltam. "Nenhum contato válido" sobre um arquivo inteiro é a mensagem que faz a pessoa
        // desistir — ela não tem como adivinhar que o problema é a primeira linha.
        var temNome = tabela.Tem("nome") || tabela.Tem("contato") || tabela.Tem("cliente");
        var temTelefone = tabela.Tem("telefone") || tabela.Tem("celular") || tabela.Tem("whatsapp")
                       || tabela.Tem("fone");

        if (!temNome || !temTelefone)
            throw new RegraDeNegocioException(
                "O arquivo precisa das colunas "
                + (!temNome && !temTelefone ? "\"nome\" e \"telefone\""
                   : !temNome ? "\"nome\"" : "\"telefone\"")
                + ". A primeira linha da planilha é o cabeçalho.");

        if (tabela.Quantidade > IServicoImportacao.MaximoLinhas)
            throw new RegraDeNegocioException(
                $"São {tabela.Quantidade} linhas, e o limite é {IServicoImportacao.MaximoLinhas} "
                + "por arquivo. Divida a planilha em partes.");

        var linhas = new List<LinhaImportada>(tabela.Quantidade);
        var telefones = new List<string>(tabela.Quantidade);

        for (var i = 0; i < tabela.Quantidade; i++)
        {
            var nome = tabela.Valor(i, "nome", "contato", "cliente");
            var bruto = tabela.Valor(i, "telefone", "celular", "whatsapp", "fone");
            var telefone = CanonicalizadorTelefone.Canonicalizar(bruto);

            // O número do EXCEL, contado pelo leitor antes de descartar as linhas em branco. Era
            // `i + 2`, e errava a partir da primeira linha vazia — ver `TabelaCsv.NumeroLinha`.
            var numero = tabela.NumeroLinha(i);

            if (nome.Length == 0)
                linhas.Add(Recusada(numero, nome, bruto, "Sem nome."));
            else if (!CanonicalizadorTelefone.EhValido(telefone))
                linhas.Add(Recusada(numero, nome, bruto,
                    bruto.Length == 0 ? "Sem telefone." : "Telefone inválido."));
            else
            {
                linhas.Add(new LinhaImportada(
                    numero, nome, telefone,
                    Vazio(tabela.Valor(i, "email", "e-mail")),
                    Vazio(tabela.Valor(i, "origem")),
                    Vazio(tabela.Valor(i, "observacoes", "observacao", "obs")),
                    SituacaoLinha.Nova, null));

                telefones.Add(telefone);
            }
        }

        // UMA consulta para todos os telefones, e não uma por linha: 2.000 idas ao banco para
        // responder a mesma pergunta em lote é o que transforma um import de 3 segundos em um de
        // três minutos. O predicado repete `AnonimizadoEm == null` porque o índice é PARCIAL —
        // mesmo motivo de `ServicoContatos.CriarAsync`.
        var jaExistem = telefones.Count == 0
            ? []
            : (await db.Contatos.AsNoTracking()
                .Where(c => telefones.Contains(c.Telefone) && c.AnonimizadoEm == null)
                .Select(c => c.Telefone)
                .ToListAsync(ct)).ToHashSet();

        // ⚠️ E O REPETIDO DENTRO DO PRÓPRIO ARQUIVO, que é o caso que o banco não pega antes de
        // estourar: a mesma planilha com o cliente duas vezes passaria as duas pela checagem
        // acima — nenhuma existe ainda — e a segunda quebraria o `SaveChanges` inteiro no
        // `uq_contatos_telefone`, derrubando as outras 799 linhas junto.
        var vistos = new HashSet<string>();

        for (var i = 0; i < linhas.Count; i++)
        {
            if (linhas[i].Situacao != SituacaoLinha.Nova) continue;

            if (jaExistem.Contains(linhas[i].Telefone))
                linhas[i] = linhas[i] with
                {
                    Situacao = SituacaoLinha.Repetida,
                    Motivo = "Já existe um contato com este telefone."
                };
            else if (!vistos.Add(linhas[i].Telefone))
                linhas[i] = linhas[i] with
                {
                    Situacao = SituacaoLinha.Invalida,
                    Motivo = "Telefone repetido dentro do próprio arquivo."
                };
        }

        return linhas;
    }

    /// <summary>⚠️ A AMOSTRA É PEQUENA DE PROPÓSITO. Ver `ResumoImportacao`: o que resolve é o
    /// número mais as primeiras linhas problemáticas, e não 1.800 objetos que ninguém lê.
    ///
    /// As RECUSADAS primeiro, porque são as acionáveis — quem abre a prévia quer ver o que vai
    /// ficar de fora, não confirmar que "Maria" entrou.</summary>
    private static ResumoImportacao Resumir(List<LinhaImportada> linhas)
    {
        const int NaAmostra = 20;

        var amostra = linhas
            .OrderBy(l => l.Situacao == SituacaoLinha.Invalida ? 0
                        : l.Situacao == SituacaoLinha.Repetida ? 1 : 2)
            .ThenBy(l => l.Linha)
            .Take(NaAmostra)
            .ToList();

        return new ResumoImportacao(
            linhas.Count,
            linhas.Count(l => l.Situacao == SituacaoLinha.Nova),
            linhas.Count(l => l.Situacao == SituacaoLinha.Repetida),
            linhas.Count(l => l.Situacao == SituacaoLinha.Invalida),
            amostra);
    }

    private static LinhaImportada Recusada(int numero, string nome, string telefone, string motivo) =>
        new(numero, nome, telefone, null, null, null, SituacaoLinha.Invalida, motivo);

    private static string? Vazio(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Uma cópia só, em `OrigemLeadTexto` — `Enum.TryParse` aceitava número e lista de
    /// flags, e "15" numa planilha derrubava a importação inteira com 500.</summary>
    private static OrigemLead ParseOrigem(string? origem) => OrigemLeadTexto.Ler(origem);

    // ==================================================================== o funil, quando pedido
    /// <summary>⚠️ PASSA PELO FILTRO DE TENANT, e não é cerimônia: o id vem do CORPO da
    /// requisição. Sem esta leitura, um funil de outra empresa levaria os contatos importados para
    /// o quadro de outro cliente.</summary>
    private async Task<long> PrimeiraEtapaAsync(long pipelineId, CancellationToken ct)
    {
        var id = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId)
            .OrderBy(e => e.Ordem).ThenBy(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(ct);

        return id != 0
            ? id
            : throw new RegraDeNegocioException("Funil não encontrado, ou sem etapas.");
    }

    private async Task<decimal> ProximaOrdemAsync(long etapaId, CancellationToken ct) =>
        (await db.Negociacoes.AsNoTracking()
            .Where(n => n.EtapaId == etapaId)
            .Where(RegrasNegociacao.NoQuadro)
            .MaxAsync(n => (decimal?)n.OrdemKanban, ct) ?? 0m) + 1m;

    /// <summary>Mesma linha de corte de cancelar venda: escrita em massa que aparece no quadro de
    /// todo mundo é de quem responde pelo número.</summary>
    private void ExigirDonoOuGestor()
    {
        var papel = contexto.Papel ?? "";

        if (!papel.Equals("dono", StringComparison.OrdinalIgnoreCase)
            && !papel.Equals("gestor", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegraDeNegocioException("Só o dono ou um gestor pode importar contatos.");
        }
    }
}
