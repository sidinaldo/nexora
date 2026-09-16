using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O ciclo de vida do contato contra Postgres real.
///
/// Antes deste bloco a ÚNICA escrita de contato no sistema era a criação automática pelo webhook.
/// Nada preenchia `ganho_em`, `perdido_em`, `valor` ou `ordem_kanban` — as colunas existiam, os
/// índices existiam, o dashboard lia, e nenhum caminho escrevia.</summary>
[Collection("banco")]
public class ContatosDbTests(BancoTeste banco)
{
    internal static readonly DateTimeOffset Agora = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== cadastro
    [Fact]
    public async Task Criar_canonicaliza_o_telefone_e_entra_na_primeira_etapa()
    {
        var (db, tx, amb) = await PrepararAsync("criar");
        using var _ = db; using var __ = tx;

        // O vendedor digita com máscara e sem DDI; o WhatsApp entrega 5584988887777.
        var id = await amb.Contatos.CriarAsync(
            new NovoContato("Maria Silva", "(84) 98888-1234", Email: "maria@exemplo.com"), default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);

        Assert.Equal("5584988881234", c.Telefone);          // canonicalizado, só dígitos, com DDI
        Assert.Equal(OrigemLead.Manual, c.Origem);          // cadastro manual, não WhatsApp

        // ⚠️ ETAPA E ESTADO SAO DA NEGOCIACAO (E4e/4). O contato criado a mao entra pela primeira
        // etapa da pipeline padrao, aberto — e e a negociacao que registra as duas coisas.
        var n = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == id);

        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, n.EtapaId);
        Assert.Equal(StatusNegociacao.Aberta, n.Status);
    }

    [Fact]
    public async Task Telefone_invalido_e_recusado_em_vez_de_virar_contato_mudo()
    {
        // Aceitar lixo aqui produz o pior modo de falha do sistema: o contato existe, aparece na
        // tela, e simplesmente nunca recebe nem casa com mensagem nenhuma. Sem erro no log.
        var (db, tx, amb) = await PrepararAsync("tel-ruim");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(new NovoContato("Fulano", "123"), default));

        Assert.Contains("inválido", erro.Message);
        Assert.False(erro.Conflito);   // entrada errada = 400, não 409
    }

    [Fact]
    public async Task Telefone_repetido_e_recusado_com_conflito()
    {
        var (db, tx, amb) = await PrepararAsync("tel-repetido");
        using var _ = db; using var __ = tx;

        await amb.Contatos.CriarAsync(new NovoContato("Primeiro", "(84) 98888-4321"), default);

        // Mesmo número, digitado de outro jeito — a canonicalização faz os dois colidirem.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(new NovoContato("Segundo", "5584988884321"), default));

        Assert.True(erro.Conflito);
    }

    [Fact]
    public async Task Editar_altera_os_dados_e_NAO_mexe_na_etapa()
    {
        // A etapa só muda por MoverAsync (que calcula ordem e recusa a etapa de ganho). Se este
        // PUT aceitasse etapa, existiria um segundo caminho sem nenhuma dessas regras.
        var (db, tx, amb) = await PrepararAsync("editar");
        using var _ = db; using var __ = tx;

        var id = await amb.Contatos.CriarAsync(new NovoContato("Nome Antigo", "(84) 98888-5555"), default);
        var etapaOriginal = (await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(n => n.ContatoId == id)).EtapaId;
        db.ChangeTracker.Clear();

        await amb.Contatos.AtualizarAsync(id, new EditarContato(
            "Nome Novo", "(84) 98888-5555", Email: "novo@exemplo.com",
            ResponsavelId: amb.Cenario.Dono.Id, Valor: 2500m), default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);

        Assert.Equal("Nome Novo", c.Nome);
        // O valor e do NEGOCIO desde o E4e; o contato so guarda quem a pessoa e.
        Assert.Equal(2500m, (await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == id)).Valor);
        Assert.Equal(amb.Cenario.Dono.Id, c.ResponsavelId);
        Assert.Equal(etapaOriginal, (await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(n => n.ContatoId == id)).EtapaId);
    }

    /// <summary>⚠️ ESTA TELA DAVA 500 PARA O LEAD QUE CHEGA PELA CAIXA (E6).
    ///
    /// `ContatoDetalhe.PipelineId` saia de `PipelineDaEtapaAsync(contato.EtapaId)`, e sem
    /// negociacao a etapa vem nula — a consulta nao achava nada e lancava "Etapa nao encontrada".
    /// O contato existia, a conversa existia, e abrir o detalhe dele quebrava.
    ///
    /// Nao era hipotetico: desde o E6 esse e o estado de TODO lead do WhatsApp e do formulario.
    /// Verificado tirando o guarda: reprova com `RegraDeNegocioException`.</summary>
    [Fact]
    public async Task O_DETALHE_DE_QUEM_NAO_TEM_NEGOCIO_ABRE_SEM_FUNIL()
    {
        var (db, tx, amb) = await PrepararAsync("detalhe-sem-negocio");
        using var _ = db; using var __ = tx;

        var lead = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Chegou pela caixa", Telefone = "5584966660001"
        };
        db.Contatos.Add(lead);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var detalhe = await amb.Contatos.DetalheAsync(lead.Id, default);

        Assert.Null(detalhe.PipelineId);
        Assert.Null(detalhe.Contato.EtapaId);
        Assert.Null(detalhe.Contato.EtapaNome);

        // E o resto da tela continua inteiro — o contato nao virou meia-linha por nao ter funil.
        Assert.Equal("Chegou pela caixa", detalhe.Contato.Nome);
        Assert.Equal("5584966660001", detalhe.Contato.Telefone);
    }

    /// <summary>⚠️ O LEAD QUE CHEGA PELA CAIXA SUMIA DA TELA DE CONTATOS (E6).
    ///
    /// O filtro "Abertos" — que e o PADRAO da tela — era `Negociacoes.Any(Aberta)`, escrito
    /// quando todo contato nascia com negociacao. Desde o E6 o lead novo nao tem nenhuma, e caia
    /// fora dos tres filtros: nao e aberto, nao e ganho, nao e perdido. So aparecia em "Todos".
    ///
    /// Era o contato mais novo da base, e o unico que a tela principal nao mostrava.</summary>
    [Fact]
    public async Task LEAD_SEM_NEGOCIO_APARECE_NO_FILTRO_ABERTOS()
    {
        var (db, tx, amb) = await PrepararAsync("lista-sem-negocio");
        using var _ = db; using var __ = tx;

        var lead = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Chegou pela caixa", Telefone = "5584955550001"
        };
        db.Contatos.Add(lead);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var abertos = await amb.Contatos.ListarAsync(
            FiltroContato.Abertos, null, null, null, 1, 50, default);

        Assert.Contains(abertos.Itens, c => c.Id == lead.Id);

        // E NAO invade os outros dois — as tres faixas continuam sem se sobrepor.
        var ganhos = await amb.Contatos.ListarAsync(
            FiltroContato.Ganhos, null, null, null, 1, 50, default);
        var perdidos = await amb.Contatos.ListarAsync(
            FiltroContato.Perdidos, null, null, null, 1, 50, default);

        Assert.DoesNotContain(ganhos.Itens, c => c.Id == lead.Id);
        Assert.DoesNotContain(perdidos.Itens, c => c.Id == lead.Id);
    }

    /// <summary>⚠️ A TELA DO CONTATO MOSTRAVA UM NEGOCIO SO, E ISSO DEIXOU DE TER RESPOSTA.
    ///
    /// O bloco "Negociacao" tinha UM seletor de etapa e UMA situacao — escrito quando um contato
    /// era um card. Com a mesma pessoa em Vendas e em Pos-venda nao existe resposta para "qual
    /// etapa o select mostra": a projecao escolhia uma e a outra sumia da tela.
    ///
    /// Perguntado assim: "se no detalhe do contato tivesse uma lista de fases/etiquetas onde o
    /// respectivo contato esta?".</summary>
    [Fact]
    public async Task O_DETALHE_LISTA_OS_NEGOCIOS_DE_CADA_FUNIL_COM_AS_ETIQUETAS_DELES()
    {
        var (db, tx, amb) = await PrepararAsync("negocios-do-contato");
        using var _ = db; using var __ = tx;

        var etiquetas = new ServicoEtiquetas(db, amb.Contexto);
        var urgente = await etiquetas.CriarAsync(new NovaEtiqueta("Urgente", null), default);
        var aguardando = await etiquetas.CriarAsync(new NovaEtiqueta("Aguardando peça", null), default);

        var posVenda = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Pós-venda", Ordem = 2 };
        db.Pipelines.Add(posVenda);
        await db.SaveChangesAsync();

        var recebido = new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = posVenda.Id, Nome = "Recebido", Ordem = 1
        };
        db.EtapasFunil.Add(recebido);
        // ⚠️ SALVA ANTES: a negociacao abaixo cita `recebido.Id`, que e ZERO ate o INSERT — e a
        // FK composta recusa. Ligar pela navegacao resolveria tambem; separar deixa mais claro.
        await db.SaveChangesAsync();

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id, ContatoId = amb.Cenario.Contato.Id,
            PipelineId = posVenda.Id, EtapaId = recebido.Id, OrdemKanban = 1000m,
            Status = StatusNegociacao.Aberta
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var noPosVenda = await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.PipelineId == posVenda.Id);

        await etiquetas.AplicarNaNegociacaoAsync(amb.Cenario.Negociacao.Id, [urgente], default);
        await etiquetas.AplicarNaNegociacaoAsync(noPosVenda.Id, [aguardando], default);
        db.ChangeTracker.Clear();

        var detalhe = await amb.Contatos.DetalheAsync(amb.Cenario.Contato.Id, default);

        Assert.Equal(2, detalhe.Negocios.Count);

        var vendas = detalhe.Negocios.Single(n => n.PipelineId == amb.Cenario.Pipeline.Id);
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, vendas.EtapaId);
        Assert.Equal(["Urgente"], vendas.Etiquetas.Select(e => e.Nome));

        var pos = detalhe.Negocios.Single(n => n.PipelineId == posVenda.Id);
        Assert.Equal("Pós-venda", pos.PipelineNome);
        Assert.Equal("Recebido", pos.EtapaNome);
        Assert.Equal(["Aguardando peça"], pos.Etiquetas.Select(e => e.Nome));

        // ⚠️ `Versao` VAI JUNTO: mover a etapa por esta lista usa o mesmo endpoint do quadro, que
        // compara o `xmin`. Sem ele a tela do contato seria a porta sem trava.
        Assert.All(detalhe.Negocios, n => Assert.NotEqual(0u, n.Versao));
    }

    /// <summary>Perdido e concluido NAO entram: eles ja tem lugar (o bloco de vendas e a linha do
    /// tempo), e a pergunta desta lista e "onde esta pessoa esta AGORA".</summary>
    [Fact]
    public async Task O_DETALHE_NAO_LISTA_NEGOCIO_PERDIDO_NEM_CONCLUIDO()
    {
        var (db, tx, amb) = await PrepararAsync("negocios-encerrados");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", null, default);
        db.ChangeTracker.Clear();

        var detalhe = await amb.Contatos.DetalheAsync(amb.Cenario.Contato.Id, default);
        Assert.Empty(detalhe.Negocios);
    }

    /// <summary>⚠️ COM DOIS NEGOCIOS ABERTOS, "REGISTRAR VENDA" ERA UM SORTEIO.
    ///
    /// O servico escolhia o aberto MAIS RECENTE. Enquanto uma pessoa tinha um negocio so isso era
    /// uma resposta; desde que ela pode estar em Vendas e em Pos-venda ao mesmo tempo, virou
    /// sorteio — e a tela do contato, que agora LISTA os dois, teria um botao por linha fechando
    /// o negocio da outra linha.
    ///
    /// ⚠️ O TESTE FECHA O MAIS ANTIGO DE PROPOSITO: e o que o comportamento antigo NAO faria.
    /// Fechar o mais recente passaria com o codigo velho e nao provaria nada.</summary>
    [Fact]
    public async Task FECHAR_PELA_LINHA_FECHA_AQUELE_NEGOCIO()
    {
        var (db, tx, amb) = await PrepararAsync("fechar-a-linha");
        using var _ = db; using var __ = tx;

        var outra = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Pós-venda", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();

        var entrada = new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Recebido", Ordem = 1
        };
        var ganho = new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Fechado", Ordem = 2,
            EGanho = true
        };
        db.EtapasFunil.AddRange(entrada, ganho);
        await db.SaveChangesAsync();

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id, ContatoId = amb.Cenario.Contato.Id,
            PipelineId = outra.Id, EtapaId = entrada.Id, OrdemKanban = 1000m,
            Status = StatusNegociacao.Aberta
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // O ANTIGO e o do cenario (Vendas); o recente e o de Pos-venda. Fechamos o ANTIGO.
        var antigo = amb.Cenario.Negociacao.Id;

        await amb.Contatos.MarcarGanhoAsync(
            amb.Cenario.Contato.Id, 900m, null, antigo, default);
        db.ChangeTracker.Clear();

        var fechado = await db.Negociacoes.AsNoTracking().SingleAsync(n => n.Id == antigo);
        Assert.Equal(StatusNegociacao.Ganha, fechado.Status);
        Assert.Equal(900m, fechado.Valor);

        // E o de Pos-venda continua ABERTO — era ele que o sorteio teria fechado.
        var oOutro = await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.PipelineId == outra.Id);
        Assert.Equal(StatusNegociacao.Aberta, oOutro.Status);
    }

    /// <summary>⚠️ O ID VEM DO CORPO DA REQUISICAO. Sem o `ContatoId` no filtro, fechar "o negocio
    /// 42" com o valor desta pessoa fecharia o negocio de OUTRA — do mesmo tenant, sem erro.</summary>
    [Fact]
    public async Task FECHAR_NEGOCIO_DE_OUTRA_PESSOA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("fechar-alheio");
        using var _ = db; using var __ = tx;

        var outroContato = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Outra pessoa", Telefone = "5584933330001"
        };
        db.Contatos.Add(outroContato);
        await db.SaveChangesAsync();

        db.Negociacoes.Add(Semeador.Negocio(outroContato, amb.Cenario.PrimeiraEtapa));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var alheio = await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.ContatoId == outroContato.Id);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(
                amb.Cenario.Contato.Id, 900m, null, alheio.Id, default));

        Assert.True(erro.Conflito);

        // E o negocio alheio continua intocado.
        db.ChangeTracker.Clear();
        Assert.Equal(StatusNegociacao.Aberta,
            (await db.Negociacoes.AsNoTracking().SingleAsync(n => n.Id == alheio.Id)).Status);
    }

    // ==================================================================== leitura
    [Fact]
    public async Task Listar_busca_por_nome_e_por_digitos_do_telefone()
    {
        // O vendedor digita "(84) 98888" e a coluna guarda "5584988887777". Sem tirar a máscara
        // da busca, procurar pelo que está na tela não acha nada.
        var (db, tx, amb) = await PrepararAsync("busca");
        using var _ = db; using var __ = tx;

        await amb.Contatos.CriarAsync(new NovoContato("Joana Prado", "(84) 98111-2222"), default);
        await amb.Contatos.CriarAsync(new NovoContato("Ricardo Alves", "(84) 98333-4444"), default);

        var porNome = await amb.Contatos.ListarAsync(FiltroContato.Abertos, "joana", null, null, 1, 30, default);
        Assert.Equal("Joana Prado", Assert.Single(porNome.Itens).Nome);

        var porTelefone = await amb.Contatos.ListarAsync(FiltroContato.Abertos, "(84) 98333", null, null, 1, 30, default);
        Assert.Equal("Ricardo Alves", Assert.Single(porTelefone.Itens).Nome);
    }

    [Fact]
    public async Task Listar_pagina_no_SQL_e_devolve_o_total_do_conjunto_inteiro()
    {
        var (db, tx, amb) = await PrepararAsync("paginar");
        using var _ = db; using var __ = tx;

        for (var i = 0; i < 7; i++)
            await amb.Contatos.CriarAsync(new NovoContato($"Contato {i:D2}", $"(84) 97000-00{i:D2}"), default);

        var p1 = await amb.Contatos.ListarAsync(FiltroContato.Abertos, null, null, null, 1, 3, default);
        var p2 = await amb.Contatos.ListarAsync(FiltroContato.Abertos, null, null, null, 2, 3, default);

        // 7 criados + o do Semeador = 8.
        Assert.Equal(8, p1.Total);
        Assert.Equal(3, p1.Itens.Count);
        Assert.Equal(3, p2.Itens.Count);
        Assert.Empty(p1.Itens.Select(i => i.Id).Intersect(p2.Itens.Select(i => i.Id)));
    }

    /// <summary>⚠️ O DETALHE DIZ EM QUAL FUNIL O CONTATO ESTÁ, e a tela depende disso.
    ///
    /// O seletor de etapa da tela de contato lista as etapas DO FUNIL DELE. Sem este campo a
    /// tela não tem como pedir o funil certo — e o que ela fazia era pedir a pipeline de id 1,
    /// que só por acidente é a da primeira empresa. Nas outras o combo vinha vazio.
    ///
    /// O teste põe o contato numa pipeline que NÃO é a padrão: com a padrão, um campo que
    /// devolvesse "a padrão" passaria sem estar certo.</summary>
    [Fact]
    public async Task O_DETALHE_DIZ_EM_QUAL_FUNIL_O_CONTATO_ESTA()
    {
        var (db, tx, amb) = await PrepararAsync("detalhe-pipeline");
        using var _ = db; using var __ = tx;

        var outra = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Atacado", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();

        var etapaDaOutra = new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Prospecção", Ordem = 1
        };
        db.EtapasFunil.Add(etapaDaOutra);
        await db.SaveChangesAsync();

        // A negociacao, que e de onde o detalhe le desde o E4e.
        await db.Negociacoes.Where(n => n.ContatoId == amb.Cenario.Contato.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.EtapaId, etapaDaOutra.Id)
                .SetProperty(n => n.PipelineId, outra.Id));
        db.ChangeTracker.Clear();

        var d = await amb.Contatos.DetalheAsync(amb.Cenario.Contato.Id, default);

        Assert.Equal(outra.Id, d.PipelineId);
        Assert.NotEqual(amb.Cenario.Pipeline.Id, d.PipelineId);
    }

    [Fact]
    public async Task Detalhe_traz_a_conversa_e_os_lembretes_numa_chamada_so()
    {
        var (db, tx, amb) = await PrepararAsync("detalhe");
        using var _ = db; using var __ = tx;

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Origem = OrigemLembrete.Manual,
            DataAlvo = new DateOnly(2026, 8, 10),
            Titulo = "ligar de volta"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var d = await amb.Contatos.DetalheAsync(amb.Cenario.Contato.Id, default);

        Assert.Equal(amb.Cenario.Contato.Nome, d.Contato.Nome);
        Assert.Equal(amb.Cenario.Conversa.Id, d.Contato.ConversaId);
        Assert.NotNull(d.UltimaMensagemEm);
        Assert.Equal("ligar de volta", Assert.Single(d.Lembretes).Titulo);
    }

    // ==================================================================== estado terminal
    [Fact]
    public async Task Marcar_ganho_sem_valor_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("ganho-sem-valor");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 0m, null, null, default));

        Assert.Contains("valor", erro.Message);
    }

    [Fact]
    public async Task Marcar_ganho_carimba_valor_data_e_MOVE_para_a_etapa_de_venda()
    {
        // A porta única: carimbar e mover na MESMA operação. É isso que permite ao cliente tratar
        // "arrastar para Venda" e "clicar em venda fechada" como a mesma coisa.
        var (db, tx, amb) = await PrepararAsync("ganho");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 3200m, null, null, default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);

        // ⚠️ AS ASSERCOES MUDARAM DE TABELA (E4e/3b). O contato nao carimba mais nada: quem
        // guarda valor, data e estado e a NEGOCIACAO. `contatos.etapa_id` ainda existe (e NOT
        // NULL) e so cai no E4e/4 — por isso a etapa continua sendo conferida nele.
        var n = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == amb.Cenario.Contato.Id);

        Assert.Equal(3200m, n.Valor);
        Assert.NotNull(n.GanhaEm);
        Assert.Null(n.PerdidaEm);
        Assert.Equal(StatusNegociacao.Ganha, n.Status);

        var etapaGanho = amb.Cenario.Etapas.Single(e => e.EGanho);
        Assert.Equal(etapaGanho.Id, n.EtapaId);
    }

    [Fact]
    public async Task Marcar_perdido_sem_motivo_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("perda-sem-motivo");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "   ", null, default));
    }

    [Fact]
    public async Task Marcar_perdido_preserva_a_etapa_onde_a_negociacao_morreu()
    {
        var (db, tx, amb) = await PrepararAsync("perda");
        using var _ = db; using var __ = tx;

        var etapaAntes = amb.Cenario.Negociacao.EtapaId;
        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", null, default);

        db.ChangeTracker.Clear();
        var n = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == amb.Cenario.Contato.Id);

        Assert.NotNull(n.PerdidaEm);
        Assert.Equal("achou caro", n.MotivoPerda);
        Assert.Equal(StatusNegociacao.Perdida, n.Status);

        // A ETAPA FICA: ela registra ONDE o negocio morreu, e e o que o relatorio de perdas por
        // etapa le. Quem tira o card do quadro e o STATUS, nao a etapa.
        Assert.Equal(etapaAntes, n.EtapaId);
    }

    [Fact]
    public async Task Ganho_sobre_perdido_e_recusado_em_vez_de_apagar_a_perda_por_baixo_do_pano()
    {
        // ck_contatos_terminal proíbe os dois juntos. Limpar a perda em silêncio faria o
        // histórico sumir sem ninguém entender por quê.
        var (db, tx, amb) = await PrepararAsync("ganho-sobre-perda");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "sumiu", null, default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 1000m, null, null, default));

        Assert.True(erro.Conflito);
        Assert.Contains("Reabra", erro.Message);
    }

    [Fact]
    public async Task Reabrir_limpa_ganho_perda_e_motivo_mas_PRESERVA_o_valor()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 4800m, null, null, default);
        // A regra e um card por funil: concluir o pedido libera o lugar.
        await ConcluirGanhaAsync(db, amb.Vendas, amb.Cenario.Contato.Id);

        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);

        // ⚠️ REABRIR NAO LIMPA NADA — ELE ABRE OUTRA (E4e). A venda anterior fica como
        // historico, com o valor dela, e nasce uma aberta. Era a coluna do contato que se
        // limpava, e era ela que apagava a venda anterior do dashboard — o defeito que o NEG-1
        // corrigiu.
        var negocios = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.ContatoId == amb.Cenario.Contato.Id)
            .ToListAsync();

        Assert.Equal(2, negocios.Count);

        // `Concluida` porque o passo acima concluiu o pedido para liberar o funil. O que importa
        // aqui e o valor: ele sobreviveu inteiro a rodada nova.
        var anterior = negocios.Single(x => x.Status == StatusNegociacao.Concluida);
        Assert.Equal(4800m, anterior.Valor);   // o faturamento nao se mexe ao reabrir

        var aberta = negocios.Single(x => x.Status == StatusNegociacao.Aberta);
        Assert.Null(aberta.GanhaEm);
        Assert.Null(aberta.PerdidaEm);
        Assert.Null(aberta.MotivoPerda);

        // E a rodada NOVA comeca na primeira etapa — nascer na coluna de venda a deixaria la sem
        // venda nenhuma, o estado divergente que a porta unica existe para impedir.
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, aberta.EtapaId);
    }

    [Fact]
    public async Task Reabrir_contato_que_ja_esta_aberto_devolve_conflito()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir-aberto");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default));
        Assert.True(erro.Conflito);
    }

    // ==================================================================== LGPD
    [Fact]
    public async Task Anonimizar_zera_a_PII_e_PRESERVA_o_historico()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd");
        using var _ = db; using var __ = tx;

        var alvo = amb.Cenario.Contato.Id;
        await amb.Contatos.MarcarGanhoAsync(alvo, 900m, null, null, default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AnonimizarAsync(alvo, default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == alvo);

        // PII zerada
        Assert.Equal("Contato anonimizado", c.Nome);
        Assert.Equal($"ANON-{alvo}", c.Telefone);
        Assert.Null(c.Email);
        Assert.Null(c.Observacoes);
        Assert.Null(c.OrigemDetalhe);
        Assert.NotNull(c.AnonimizadoEm);

        // Histórico preservado: nem delete físico, nem soft delete.
        // ⚠️ O HISTORICO PRESERVADO MUDOU DE CASA (E4e), mas a garantia e a mesma: anonimizar
        // apaga QUEM a pessoa era, nunca o que aconteceu. O dashboard continua contando a venda.
        var n = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == amb.Cenario.Contato.Id);
        Assert.Equal(900m, n.Valor);
        Assert.NotNull(n.GanhaEm);
        Assert.True(await db.Conversas.IgnoreQueryFilters().AnyAsync(v => v.ContatoId == alvo));
        Assert.True(await db.Mensagens.IgnoreQueryFilters().AnyAsync(m => m.ContatoId == alvo));
    }

    [Fact]
    public async Task Dois_contatos_anonimizados_convivem_sem_colidir_no_telefone()
    {
        // `telefone` é NOT NULL com índice único por empresa. Sem marcador determinístico e
        // único, ou a constraint quebra ou os dois colidem.
        var (db, tx, amb) = await PrepararAsync("lgpd-x2");
        using var _ = db; using var __ = tx;

        var a = await amb.Contatos.CriarAsync(new NovoContato("Pessoa A", "(84) 96000-1111"), default);
        var b = await amb.Contatos.CriarAsync(new NovoContato("Pessoa B", "(84) 96000-2222"), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AnonimizarAsync(a, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(b, default);

        db.ChangeTracker.Clear();
        var telefones = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == a || c.Id == b).Select(c => c.Telefone).ToListAsync();

        Assert.Equal(2, telefones.Distinct().Count());
        Assert.Contains($"ANON-{a}", telefones);
        Assert.Contains($"ANON-{b}", telefones);
    }

    [Fact]
    public async Task Anonimizado_some_da_lista_mas_o_telefone_dele_libera_cadastro_novo()
    {
        // O índice único é PARCIAL (`WHERE anonimizado_em IS NULL`): a linha sai do índice ao ser
        // anonimizada, e o mesmo número pode voltar como contato novo. Se a checagem de duplicata
        // não repetisse o predicado, o cadastro seria barrado com uma mensagem mentirosa.
        var (db, tx, amb) = await PrepararAsync("lgpd-libera");
        using var _ = db; using var __ = tx;

        var antigo = await amb.Contatos.CriarAsync(new NovoContato("Antigo", "(84) 95000-7777"), default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(antigo, default);
        db.ChangeTracker.Clear();

        var lista = await amb.Contatos.ListarAsync(FiltroContato.Todos, null, null, null, 1, 50, default);
        Assert.DoesNotContain(lista.Itens, i => i.Id == antigo);

        // E o número volta a ser cadastrável.
        var novo = await amb.Contatos.CriarAsync(new NovoContato("Novo", "(84) 95000-7777"), default);
        Assert.NotEqual(antigo, novo);
    }

    [Fact]
    public async Task Anonimizado_nao_aceita_mais_alteracao()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd-travado");
        using var _ = db; using var __ = tx;

        var alvo = amb.Cenario.Contato.Id;
        await amb.Contatos.AnonimizarAsync(alvo, default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(alvo, 100m, null, null, default));
        Assert.True(erro.Conflito);
    }

    // ==================================================================== multi-tenant
    [Fact]
    public async Task Criar_com_etapa_de_OUTRA_empresa_e_recusado()
    {
        // O query filter protege a LEITURA. Um id de etapa vindo do cliente precisa de checagem
        // explícita — sem ela, o contato entraria no funil de outro tenant.
        var (db, tx, amb, alheia) = await PrepararComVizinhaAsync("etapa-alheia");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(
                new NovoContato("Invasor", "(84) 94000-1111", EtapaId: alheia.PrimeiraEtapa.Id), default));

        Assert.Contains("não encontrada", erro.Message);
    }

    [Fact]
    public async Task Atribuir_responsavel_de_OUTRA_empresa_e_recusado()
    {
        var (db, tx, amb, alheia) = await PrepararComVizinhaAsync("resp-alheio");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(
                new NovoContato("Invasor", "(84) 94000-2222", ResponsavelId: alheia.Dono.Id), default));

        Assert.Contains("não encontrado", erro.Message);
    }

    [Fact]
    public async Task Contato_de_outra_empresa_nao_aparece_nem_pode_ser_alterado()
    {
        var (db, tx, amb, alheia) = await PrepararComVizinhaAsync("contato-alheio");
        using var _ = db; using var __ = tx;

        var lista = await amb.Contatos.ListarAsync(FiltroContato.Todos, null, null, null, 1, 50, default);
        Assert.DoesNotContain(lista.Itens, i => i.Id == alheia.Contato.Id);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.DetalheAsync(alheia.Contato.Id, default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(alheia.Contato.Id, 500m, null, null, default));
    }

    // ==================================================================== O CRITÉRIO DO BLOCO
    [Fact]
    public async Task O_DASHBOARD_SAI_DO_ZERO_DEPOIS_DE_MARCAR_UM_GANHO()
    {
        // ===================== O TESTE QUE FECHA O BURACO =====================
        // O dashboard do bloco 6 lê `ganho_em` e `valor`. Até este bloco, NADA no produto escrevia
        // essas colunas — "vendas do mês" mostrava zero para sempre, e não por falta de vendas.
        // Este teste amarra as duas pontas: a escrita nova e a leitura que já existia.
        // ======================================================================
        var (db, tx, amb) = await PrepararAsync("dashboard-zero");
        using var _ = db; using var __ = tx;

        var antes = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(0, antes.VendasDoMes);
        Assert.Equal(0m, antes.FaturamentoDoMes);
        Assert.Equal(0d, antes.TaxaConversao);

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 7500m, null, null, default);
        db.ChangeTracker.Clear();

        var depois = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(1, depois.VendasDoMes);
        Assert.Equal(7500m, depois.FaturamentoDoMes);
        Assert.Equal(1d, depois.TaxaConversao);   // 1 ganho, 0 perdas

        // E o funil também reflete: o card foi para a coluna de venda com o valor.
        var etapaGanho = amb.Cenario.Etapas.Single(e => e.EGanho);
        var coluna = depois.Funil.Single(f => f.EtapaId == etapaGanho.Id);
        Assert.Equal(1, coluna.Contatos);
        Assert.Equal(7500m, coluna.Valor);
    }

    [Fact]
    public async Task Perda_entra_na_taxa_de_conversao_mas_nao_no_faturamento()
    {
        var (db, tx, amb) = await PrepararAsync("dashboard-perda");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 1000m, null, null, default);
        db.ChangeTracker.Clear();

        var perdido = await amb.Contatos.CriarAsync(new NovoContato("Que perdeu", "(84) 93000-1111"), default);
        await amb.Contatos.MarcarPerdidoAsync(perdido, "preço", null, default);
        db.ChangeTracker.Clear();

        var d = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(1, d.VendasDoMes);
        Assert.Equal(1000m, d.FaturamentoDoMes);
        Assert.Equal(0.5d, d.TaxaConversao);   // 1 ganho / (1 ganho + 1 perda)
    }

    [Fact]
    public async Task Contato_anonimizado_continua_contando_no_dashboard()
    {
        // A anonimização apaga quem era a pessoa, não que a venda aconteceu.
        var (db, tx, amb) = await PrepararAsync("dashboard-lgpd");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 2000m, null, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(amb.Cenario.Contato.Id, default);
        db.ChangeTracker.Clear();

        var d = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(1, d.VendasDoMes);
        Assert.Equal(2000m, d.FaturamentoDoMes);
    }

    // ==================================================================== apoio
    internal sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto,
        IServicoContatos Contatos, IServicoFunil Funil, IServicoDashboard Dashboard,
        IServicoVendas Vendas,
        /// <summary>O MESMO coletor do contexto (AUD-1): quem monta um serviço à mão no teste
        /// precisa passar este, senão a declaração não chega ao interceptor.</summary>
        ColetorAuditoria Trilha,
        /// <summary>O relógio CONGELADO. Sai daqui para quem chama job direto — a conclusão
        /// automática (NEG-2) recebe um `TimeProvider`, e o real faria o prazo depender da hora
        /// em que a suíte roda.</summary>
        TimeProvider Relogio);

    /// <summary>O CARD de um contato — que desde o E4c/2 é a negociação aberta dele, e não ele.
    ///
    /// Existe porque `MoverAsync` passou a receber `negociacaoId`, e os dois são `long`: o
    /// compilador não ajuda, e passar o id errado só aparece como teste vermelho.</summary>
    /// <summary>===================== GANHAR E LIBERAR O FUNIL =====================
    /// Conclui a venda GANHA do contato, que e o que tira o card do quadro e devolve o lugar no
    /// funil.
    ///
    /// ⚠️ EXISTE PORQUE A REGRA MUDOU: um card nao-fechado por (contato, funil). "Ganhar e abrir
    /// de novo" no MESMO funil passou a exigir concluir no meio — antes os dois cards conviviam,
    /// um em "Venda" e outro em "Novo Lead", e a mesma pessoa aparecia duas vezes.
    ///
    /// Concluir NAO tira o dinheiro do faturamento (`concluida` continua contando), que e o que
    /// torna a exigencia aceitavel — e e por isso que estes testes continuam podendo afirmar
    /// "o faturamento nao se mexeu" depois de chamar isto.
    /// ====================================================================</summary>
    internal static async Task ConcluirGanhaAsync(
        NexoraDbContext db, IServicoVendas vendas, long contatoId)
    {
        db.ChangeTracker.Clear();

        var ganhas = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == contatoId && n.Status == StatusNegociacao.Ganha)
            .Select(n => n.Id).ToListAsync();

        if (ganhas.Count > 0) await vendas.ConcluirAsync(ganhas, default);
        db.ChangeTracker.Clear();
    }

    internal static Task<long> CardDoContatoAsync(NexoraDbContext db, long contatoId) =>
        db.Negociacoes.AsNoTracking().IgnoreQueryFilters()
            .Where(n => n.ContatoId == contatoId && n.Status == StatusNegociacao.Aberta)
            .OrderByDescending(n => n.Id)
            .Select(n => n.Id)
            .FirstAsync();

    internal static async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        BancoTeste banco, string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Agora);

        // O relógio falso vai TAMBÉM para o interceptor: sem isso o `criado_em` sai com a data
        // real da máquina, e "leads de hoje" do dashboard conta contra um "hoje" diferente.
        // O MESMO coletor no contexto e nos servicos (AUD-1): e o elo entre a declaracao e a
        // gravacao da trilha.
        var trilha = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(
            cenario, ctx,
            new ServicoContatos(db, ctx, PublicadorDeTeste.Novo(db, relogio), trilha, relogio),
            new ServicoFunil(db, PublicadorDeTeste.Novo(db, relogio), trilha),
            new ServicoDashboard(db, relogio),
            new ServicoVendas(db, ctx, trilha, relogio),
            trilha, relogio));
    }

    private Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo) =>
        PrepararAsync(banco, sufixo);

    /// <summary>O mesmo, mais um SEGUNDO tenant já semeado — para os testes de isolamento terem
    /// contra o que colidir.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb, Cenario Alheia)>
        PrepararComVizinhaAsync(string sufixo)
    {
        var (db, tx, amb) = await PrepararAsync(sufixo);

        // A vizinha é semeada com o contexto apontando para o tenant do teste; o Semeador atribui
        // empresa_id explicitamente, então as linhas saem corretas de qualquer jeito.
        var alheia = await Semeador.TenantAsync(db, $"{sufixo}-vizinha");
        db.ChangeTracker.Clear();

        return (db, tx, amb, alheia);
    }
}
