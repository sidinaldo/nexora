using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Csv;
using Nexora.Core.Entidades;
using Nexora.Core.Webhooks;
using Nexora.Core.LeadAds;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Core.Seguranca;

namespace Nexora.Infra.Servicos;

/// <summary>A importação do CSV do Meta Lead Ads (INT-XX). Ver `IServicoImportacaoMeta` para os
/// passos, e por que a prévia e a gravação dividem o mesmo julgamento.</summary>
public class ServicoImportacaoMeta(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IPublicadorEventos eventos,
    ColetorAuditoria trilha,
    TimeProvider relogio) : IServicoImportacaoMeta
{
    /// <summary>Quantas a prévia mostra. O spec: "as 10 primeiras".</summary>
    private const int NaPrevia = 10;

    /// <summary>Quantas linhas por `SaveChanges`. Ver `GravarAsync` para por que em lotes.
    ///
    /// 500 é o mesmo corte do processamento em segundo plano: até aí a gravação inteira cabe num
    /// request, e acima dele cada lote é um ponto de recuperação.</summary>
    private const int PorLote = 500;

    /// <summary>O CSV da Meta traz o lead de ONTEM: a caixinha do aviso nasce MARCADA. Ver
    /// `AvisoIntegracoes` — na planilha comum é o contrário.</summary>
    private const bool AvisarPorPadrao = true;

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

        // De onde o arquivo parece vir, para a pergunta da tela já chegar respondida — e para o
        // job ter uma resposta mesmo que o cliente da API não mande nenhuma.
        var origemSugerida = MapeamentoMeta.OrigemSugerida(tabela.Cabecalho);

        var importacao = new Importacao
        {
            EmpresaId = contexto.EmpresaId,
            UsuarioId = contexto.UsuarioId,
            NomeArquivo = Cortar(string.IsNullOrWhiteSpace(nomeArquivo) ? "sem-nome.csv" : nomeArquivo, 260),
            TotalLinhas = tabela.Quantidade,
            Status = StatusImportacao.AguardandoMapeamento,
            Origem = origemSugerida,
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

        return new ImportacaoRecebida(
            importacao.Id, importacao.NomeArquivo, importacao.TotalLinhas, sugestao, origemSugerida);
    }

    // ==================================================================== 2. prever
    public async Task<PreviaImportacao> PreverAsync(
        long importacaoId, IReadOnlyList<ColunaMapeada> mapeamento, CancellationToken ct)
    {
        contexto.Exigir(Permissao.ImportarContatos, "Só o dono ou um gestor pode importar leads.");

        // ⚠️ A PRÉVIA NÃO OLHA PARA UMA IMPORTAÇÃO EM CURSO. Ela leria linhas que estão virando
        // contato AGORA e diria "487 novos" sobre gente que já entrou. Quem está processando tem
        // a tela de acompanhamento, não a de mapeamento.
        if (await db.Importacoes.AsNoTracking()
                .AnyAsync(i => i.Id == importacaoId && i.Status == StatusImportacao.Processando, ct))
            throw new RegraDeNegocioException(
                "Esta importação está sendo processada. Espere terminar.", conflito: true);

        var linhas = await JulgarAsync(importacaoId, mapeamento, ct);

        return new PreviaImportacao(
            linhas.Count,
            linhas.Count(l => l.Resultado == ResultadoLinha.Importado),
            linhas.Count(l => l.Resultado == ResultadoLinha.Duplicado),
            linhas.Count(l => l.Resultado == ResultadoLinha.Invalido),
            [.. linhas.Take(NaPrevia).Select(l => new LinhaPrevia(
                l.Linha, l.Nome, l.Telefone, l.Email, l.MetaLeadId, l.CriadoEm, l.Resultado, l.Motivo))],
            new AvisoIntegracoes(
                await eventos.AlguemAssinaAsync(contexto.EmpresaId, EventoWebhook.LeadCriado, ct),
                AvisarPorPadrao));
    }

    // ==================================================================== 3. gravar
    public async Task<ResultadoImportacao> GravarAsync(
        long importacaoId, GravarImportacao pedido, CancellationToken ct)
    {
        contexto.Exigir(Permissao.ImportarContatos, "Só o dono ou um gestor pode importar leads.");

        var importacao = await db.Importacoes.FirstOrDefaultAsync(i => i.Id == importacaoId, ct)
            ?? throw new RegraDeNegocioException("Importação não encontrada.") { StatusHttp = 404 };

        // ⚠️ UMA VEZ SÓ. Gravar de novo criaria os contatos como duplicados (o julgamento os
        // encontraria na base) e sobrescreveria os números da primeira vez com zeros — parecendo
        // que a importação não trouxe ninguém. Quem quer reimportar sobe o arquivo de novo.
        if (importacao.Status == StatusImportacao.Concluida)
            throw new RegraDeNegocioException(
                "Esta importação já foi processada. Suba o arquivo de novo para importar outra vez.",
                conflito: true);

        if (importacao.Status == StatusImportacao.Processando)
            throw new RegraDeNegocioException(
                "Esta importação já está sendo processada. Espere terminar.", conflito: true);

        // ===================== A RECUSA ACONTECE AGORA, COM ALGUÉM NA FRENTE DA TELA =====
        // Mapeamento inválido, funil sem etapa, responsável de outra empresa: tudo isto tem de
        // estourar no CLIQUE. No job, a mesma recusa viraria uma importação em `erro` que ninguém
        // está olhando, e o dono só descobriria minutos depois.
        // ================================================================================
        Validar(pedido.Mapeamento, ColunasDoArquivo(importacao.Mapeamento));
        var responsavelId = await ResponsavelValidoAsync(pedido.ResponsavelId, ct);
        await EntradaDoFunilAsync(pedido.PipelineId, ct);

        // O mapeamento CONFIRMADO substitui o sugerido, e as escolhas ficam guardadas: é o que
        // responde depois "com que mapeamento e para que funil estes contatos entraram?" — e é o
        // que o job lê quando o arquivo é grande demais para o request.
        importacao.Mapeamento = JsonSerializer.Serialize(pedido.Mapeamento, Json);
        importacao.PipelineId = pedido.PipelineId;
        importacao.ResponsavelId = responsavelId;
        importacao.AvisarIntegracoes = pedido.AvisarIntegracoes;
        // Ausente mantém a sugestão do upload: cliente antigo da API não perde a origem.
        if (pedido.Origem is { } origem) importacao.Origem = origem;
        importacao.Importados = 0;
        importacao.Duplicados = 0;
        importacao.Invalidos = 0;
        importacao.Erro = null;
        importacao.Status = StatusImportacao.Processando;

        // ===================== ACIMA DO CORTE, QUEM GRAVA É O JOB =====================
        // 10.000 linhas não cabem num request: o navegador desiste antes, e a pessoa fica sem
        // saber se importou. Até o corte, grava aqui mesmo — é a resposta imediata que o caso
        // comum (um export de 60 leads) merece.
        //
        // `ProcessandoDesde` é a RESERVA: preenchido, o job não pega. No caminho síncrono ela já
        // nasce preenchida — o trabalho é deste request.
        // ==============================================================================
        if (importacao.TotalLinhas > IServicoImportacaoMeta.CorteSincrono)
        {
            await db.SaveChangesAsync(ct);
            return Acompanhamento(importacao);
        }

        importacao.ProcessandoDesde = relogio.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return await ExecutarAsync(importacao, ct);
    }

    /// <summary>O que o job chama, e o que o caminho síncrono chama: a gravação em si, do
    /// mapeamento e das escolhas JÁ GUARDADOS. Nenhuma decisão nova acontece aqui.</summary>
    public async Task<ResultadoImportacao?> ProcessarAsync(long importacaoId, CancellationToken ct)
    {
        // ⚠️ SEM CHECAGEM DE PAPEL, e é deliberado: a autorização aconteceu no clique que pôs a
        // importação na fila. O job é a continuação dela, não um gesto novo — e não há papel
        // nenhum no contexto de um job (ver `ContextoDeFundo`).
        var importacao = await db.Importacoes.FirstOrDefaultAsync(i => i.Id == importacaoId, ct);

        if (importacao is null || importacao.Status != StatusImportacao.Processando) return null;

        return await ExecutarAsync(importacao, ct);
    }

    /// <summary>Onde a importação está — é o que a tela pergunta enquanto o arquivo grande
    /// processa. Os contadores sobem a cada lote, então o número anda na tela.</summary>
    public async Task<ResultadoImportacao> AcompanharAsync(long importacaoId, CancellationToken ct)
    {
        var importacao = await db.Importacoes.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == importacaoId, ct)
            ?? throw new RegraDeNegocioException("Importação não encontrada.") { StatusHttp = 404 };

        return Acompanhamento(importacao);
    }

    private static ResultadoImportacao Acompanhamento(Importacao i) =>
        new(i.Id, i.TotalLinhas, i.Importados, i.Duplicados, i.Invalidos, i.Status);

    private async Task<ResultadoImportacao> ExecutarAsync(Importacao importacao, CancellationToken ct)
    {
        var mapeamento = JsonSerializer
            .Deserialize<List<ColunaMapeada>>(importacao.Mapeamento ?? "[]", Json)!;

        // ⚠️ O MESMO JULGAMENTO DA PRÉVIA. Se a gravação tivesse regras próprias, o dono aprovaria
        // uma coisa na tela e o banco receberia outra — e ele CONFIRMOU o que viu.
        var julgadas = await JulgarAsync(importacao.Id, mapeamento, ct);

        var responsavelId = importacao.ResponsavelId;
        var (etapaId, ordem) = await EntradaDoFunilAsync(importacao.PipelineId, ct);

        importacao.TotalLinhas = julgadas.Count;
        var pedido = new GravarImportacao(
            mapeamento, importacao.PipelineId, responsavelId, importacao.AvisarIntegracoes);

        var criados = new List<Contato>(julgadas.Count(l => l.Resultado == ResultadoLinha.Importado));

        try
        {
            foreach (var lote in julgadas.Chunk(PorLote))
            {
                var (doLote, proxima) = await GravarLoteAsync(
                    lote, importacao, etapaId, responsavelId, pedido.PipelineId, ordem, ct);

                criados.AddRange(doLote);
                ordem = proxima;

                if (ct.IsCancellationRequested) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // O que já entrou FICA, e cada linha gravada diz o que virou. `erro` explica onde parou
            // — apagar o progresso obrigaria a recomeçar 10.000 linhas por causa da última.
            importacao.Status = StatusImportacao.Erro;
            importacao.Erro = Cortar(ex.Message, 500);
            await db.SaveChangesAsync(ct);
            throw;
        }

        importacao.Status = ct.IsCancellationRequested
            ? StatusImportacao.Erro
            : StatusImportacao.Concluida;
        if (ct.IsCancellationRequested) importacao.Erro = "cancelada_no_meio";
        await db.SaveChangesAsync(ct);

        // ⚠️ DEPOIS DE TUDO GRAVADO, e fora da operação: o publicador nunca lança, então um aviso
        // que não sai deixa os contatos importados e registra o erro. Só os CRIADOS avisam — o
        // enriquecido não nasceu agora, e um `lead.criado` sobre ele duplicaria o cadastro no ERP.
        if (pedido.AvisarIntegracoes && criados.Count > 0)
            await eventos.PublicarContatosEmMassaAsync(EventoWebhook.LeadCriado, criados, ct);

        return new ResultadoImportacao(
            importacao.Id, importacao.TotalLinhas, importacao.Importados,
            importacao.Duplicados, importacao.Invalidos, importacao.Status);
    }

    /// <summary>Um lote: cria os novos, enriquece os repetidos, carimba as linhas e soma os
    /// contadores. Um `SaveChanges` para as escritas e outro para os carimbos — o segundo precisa
    /// dos ids que o primeiro gerou.</summary>
    private async Task<(List<Contato> Criados, decimal ProximaOrdem)> GravarLoteAsync(
        LinhaJulgada[] lote, Importacao importacao, long? etapaId, long? responsavelId,
        long? pipelineId, decimal ordem, CancellationToken ct)
    {
        var novos = new List<(long LinhaId, Contato Contato)>();

        foreach (var l in lote.Where(x => x.Resultado == ResultadoLinha.Importado))
        {
            var contato = new Contato
            {
                EmpresaId = contexto.EmpresaId,
                Nome = l.Nome,
                Telefone = l.Telefone!,
                Email = l.Email,
                Observacoes = l.Observacoes,
                // A da LINHA quando a planilha disse algo que entendemos; senão, a que o dono
                // escolheu na tela. Fixo em `MetaAds` — como esta tela fazia — punha a lista de
                // clientes da padaria inteira no relatório de anúncios.
                Origem = l.Origem ?? importacao.Origem,
                OrigemDetalhe = l.OrigemDetalhe,
                ResponsavelId = responsavelId,
                MetaLeadId = l.MetaLeadId,
                MetaAdId = l.MetaAdId,
                MetaCampaignId = l.MetaCampaignId,
                MetaFormId = l.MetaFormId,
                // ⚠️ A DATA DO ANÚNCIO, E NÃO A DE HOJE. `created_time` é quando a pessoa preencheu
                // o formulário; sem isto o lead de três dias atrás entraria como "hoje" e poluiria
                // "leads de hoje" no dashboard. O interceptor da auditoria só carimba `CriadoEm`
                // quando ele vem zerado — foi afrouxado no commit 1 exatamente para isto.
                CriadoEm = l.CriadoEm ?? default
            };

            db.Contatos.Add(contato);
            novos.Add((l.LinhaId, contato));

            // O funil é opcional, e quando vem a negociação nasce junto, no mesmo `SaveChanges`.
            // `pipelineId` vai por parâmetro para não perguntar "de que funil é esta etapa?" por
            // linha — é o N+1 que a issue #8 pagou uma vez.
            if (etapaId is { } etapa)
                db.Negociacoes.Add(await AberturaDeNegociacao.NovaAsync(
                    db, contato, etapa, ordem++, null, null, ct, pipelineId));
        }

        // ---------- o repetido ENRIQUECE, e só o que está nulo
        var paraEnriquecer = lote
            .Where(l => l.Resultado == ResultadoLinha.Duplicado && l.ContatoExistenteId is not null)
            .ToList();

        if (paraEnriquecer.Count > 0)
        {
            var ids = paraEnriquecer.Select(l => l.ContatoExistenteId!.Value).Distinct().ToList();
            var existentes = await db.Contatos.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

            foreach (var l in paraEnriquecer)
            {
                if (!existentes.TryGetValue(l.ContatoExistenteId!.Value, out var c)) continue;

                // ⚠️ SÓ CAMPO NULO, e só os `meta_*` — que só este importador produz. Não é
                // sobrescrita: o nome que o vendedor corrigiu e a observação que ele escreveu
                // ontem continuam como estão. "Enriquecer é melhor que duplicar".
                c.MetaLeadId ??= l.MetaLeadId;
                c.MetaAdId ??= l.MetaAdId;
                c.MetaCampaignId ??= l.MetaCampaignId;
                c.MetaFormId ??= l.MetaFormId;
                c.OrigemDetalhe ??= l.OrigemDetalhe;
            }
        }

        await db.SaveChangesAsync(ct);

        // ---------- o carimbo de cada linha, agora que os ids existem
        var porLinha = novos.ToDictionary(n => n.LinhaId, n => n.Contato.Id);
        var idsDasLinhas = lote.Select(l => l.LinhaId).ToList();
        var linhas = await db.ImportacaoLinhas
            .Where(l => idsDasLinhas.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        foreach (var l in lote)
        {
            if (!linhas.TryGetValue(l.LinhaId, out var linha)) continue;

            linha.Resultado = l.Resultado;
            linha.Motivo = l.Motivo;
            linha.ContatoId = porLinha.GetValueOrDefault(l.LinhaId) is var id && id != 0
                ? id
                : l.ContatoExistenteId;
        }

        // Os contadores sobem A CADA LOTE, e não no fim: é deles que a tela de acompanhamento
        // (commit 4) lê o progresso enquanto a importação grande roda.
        importacao.Importados += lote.Count(l => l.Resultado == ResultadoLinha.Importado);
        importacao.Duplicados += lote.Count(l => l.Resultado == ResultadoLinha.Duplicado);
        importacao.Invalidos += lote.Count(l => l.Resultado == ResultadoLinha.Invalido);

        // A trilha DEPOIS do insert, como em `ServicoImportacao`: antes do `SaveChanges` o id é 0,
        // e evento órfão não aparece na linha do tempo de ninguém.
        foreach (var (_, contato) in novos)
            trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Criou);

        await db.SaveChangesAsync(ct);

        return (novos.Select(n => n.Contato).ToList(), ordem);
    }

    /// <summary>O responsável escolhido tem de ser da empresa — o id vem do CORPO da requisição, e o
    /// query filter protege LEITURA, não escrita.</summary>
    private async Task<long?> ResponsavelValidoAsync(long? responsavelId, CancellationToken ct)
    {
        if (responsavelId is not { } id) return null;

        var existe = await db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == id, ct);
        return existe ? id : throw new RegraDeNegocioException("Responsável não encontrado.");
    }

    /// <summary>A etapa de entrada do funil escolhido e a próxima posição na coluna. Resolvido UMA
    /// vez, fora do laço: são 10.000 linhas para uma resposta que não muda.</summary>
    private async Task<(long? EtapaId, decimal Ordem)> EntradaDoFunilAsync(
        long? pipelineId, CancellationToken ct)
    {
        if (pipelineId is not { } funil) return (null, 0m);

        // PASSA PELO FILTRO DE TENANT: sem esta leitura, um funil de outra empresa levaria os leads
        // importados para o quadro de outro cliente.
        var etapaId = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == funil)
            .OrderBy(e => e.Ordem).ThenBy(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(ct);

        if (etapaId == 0) throw new RegraDeNegocioException("Funil não encontrado, ou sem etapas.");

        var ordem = (await db.Negociacoes.AsNoTracking()
            .Where(n => n.EtapaId == etapaId)
            .Where(RegrasNegociacao.NoQuadro)
            .MaxAsync(n => (decimal?)n.OrdemKanban, ct) ?? 0m) + 1m;

        return (etapaId, ordem);
    }

    // ==================================================================== o julgamento
    /// <summary>Uma linha lida e decidida. `internal` porque o commit da gravação a usa igual.</summary>
    internal sealed record LinhaJulgada(
        long LinhaId, int Linha, string Nome, string? Telefone, string? Email, string? Observacoes,
        string? OrigemDetalhe, OrigemLead? Origem, string? MetaLeadId, string? MetaAdId,
        string? MetaCampaignId, string? MetaFormId, DateTime? CriadoEm, ResultadoLinha Resultado,
        string? Motivo, long? ContatoExistenteId);

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
            .Select(i => new { i.Mapeamento })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Importação não encontrada.") { StatusHttp = 404 };

        var regras = Validar(mapeamento, ColunasDoArquivo(importacao.Mapeamento));

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
                // ⚠️ `Reconhecer`, E NÃO `Ler`: `Ler` devolve `Manual` para o que não entende, e
                // isso ATROPELARIA em silêncio a escolha que o dono fez na tela. Nulo aqui quer
                // dizer "esta linha não disse nada" — e aí vale a escolha dele.
                Origem = OrigemLeadTexto.Reconhecer(Um(CampoImportacao.Origem)),
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
                t.Origem, t.MetaLeadId, t.MetaAdId, t.MetaCampaignId, t.MetaFormId, t.CriadoEm,
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

    /// <summary>As colunas que o arquivo TEM, lidas do mapeamento guardado no upload. É contra esta
    /// lista que um mapeamento citando coluna inexistente é recusado.</summary>
    private static HashSet<string> ColunasDoArquivo(string? mapeamentoGuardado) =>
        JsonSerializer.Deserialize<List<ColunaMapeada>>(mapeamentoGuardado ?? "[]", Json)!
            .Select(c => c.Coluna)
            .ToHashSet();

    private static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string Cortar(string v, int max) => v.Length <= max ? v : v[..max];
}
